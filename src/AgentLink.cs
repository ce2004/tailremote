using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The service hosts from session 0, from the moment Windows starts it (before the sign-in
    /// screen, so the startup sound is heard). Keys must be typed on the screen that is showing,
    /// which a service cannot reach, so the service hands them to its agent in the signed-in
    /// session over this pipe, together with what goes onto the clipboard when no TailRemote
    /// window is open. Only SYSTEM can open it, and the agent types nothing until it has checked
    /// that the pipe really is SYSTEM's: anyone else could otherwise type as SYSTEM.
    /// </summary>
    internal static class AgentLink
    {
        private const string PipeName = "TailRemoteAgent";
        private const byte KeyMessage = (byte)'K', TextMessage = (byte)'T', FilesMessage = (byte)'F';

        /// <summary>Test only (--chaostest): the test plays the service too, so the pipe is not SYSTEM's.</summary>
        internal static bool TestAnyOwner;

        private static void Write(Stream s, byte type, byte[] payload)
        {
            byte[] m = new byte[5 + payload.Length];
            m[0] = type;
            BitConverter.TryWriteBytes(m.AsSpan(1), payload.Length);
            payload.CopyTo(m, 5);
            s.Write(m);
            s.Flush();
        }

        // ================= The service's side =================

        public sealed class Server
        {
            // Keys are never waited on (the host types under its lock): queued, written by a thread of their own.
            private readonly BlockingCollection<(byte, byte[])> _out = new(4096);
            private volatile NamedPipeServerStream? _pipe;

            public Server()
            {
                new Thread(AcceptLoop) { IsBackground = true, Name = "TailRemote agent link" }.Start();
                new Thread(WriteLoop) { IsBackground = true, Name = "TailRemote agent link out", Priority = ThreadPriority.AboveNormal }.Start();
            }

            /// <summary>Hands a key to the agent; false if no agent is there (nobody's session yet, or it is starting).</summary>
            public bool Key(ushort vk, ushort scan, bool up, bool extended)
            {
                if (_pipe == null) return false;
                byte[] k = new byte[5];
                BitConverter.TryWriteBytes(k.AsSpan(0), vk);
                BitConverter.TryWriteBytes(k.AsSpan(2), scan);
                k[4] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
                return _out.TryAdd((KeyMessage, k));
            }

            public void Text(string text) { if (_pipe != null) _out.TryAdd((TextMessage, Encoding.UTF8.GetBytes(text))); }
            public void Files(string[] paths) { if (_pipe != null) _out.TryAdd((FilesMessage, Encoding.UTF8.GetBytes(string.Join("\n", paths)))); }

            private void AcceptLoop()
            {
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
                using (var me = WindowsIdentity.GetCurrent()) if (me.IsSystem) sec.SetOwner(system); // the agent checks this before typing anything
                else sec.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow)); // the chaos test plays both halves
                bool first = true, warned = false;
                while (true)
                {
                    try
                    {
                        var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.Out, 2, PipeTransmissionMode.Byte,
                            first ? PipeOptions.FirstPipeInstance : PipeOptions.None, 0, 0, sec);
                        first = false;
                        pipe.WaitForConnection();
                        // The newest agent (the session now at the screen) takes over; anything queued for the old one goes.
                        var old = Interlocked.Exchange(ref _pipe, pipe);
                        try { old?.Dispose(); } catch { }
                        while (_out.TryTake(out _)) { }
                        ServiceHost.Log("The agent in the signed-in session is connected.");
                    }
                    catch (Exception e)
                    {
                        if (!warned) ServiceHost.Log("Agent link: " + e.Message + (first ? " Another program may hold the pipe " + PipeName + "." : ""));
                        warned = true;
                        Thread.Sleep(1000);
                    }
                }
            }

            private void WriteLoop()
            {
                foreach (var (type, payload) in _out.GetConsumingEnumerable())
                {
                    var pipe = _pipe;
                    if (pipe == null) continue;
                    try { Write(pipe, type, payload); }
                    catch
                    {
                        // The agent is gone (signed out, or switching user): until the next one connects, keys go nowhere.
                        Interlocked.CompareExchange(ref _pipe, null, pipe);
                        try { pipe.Dispose(); } catch { }
                    }
                }
            }
        }

        // ================= The agent's side =================

        /// <summary>Keeps connected to the service and does what it is handed. Never returns.</summary>
        public static void Run(Action<ushort, ushort, bool, bool> key, Action<string> text, Action<string[]> files)
        {
            byte[] head = new byte[5];
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In);
                    pipe.Connect(2000);
                    // Really the service: a pipe of this name made by anyone else (an ordinary user's
                    // program, before the service made its own) would be typing as SYSTEM.
                    var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                    if (owner?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true && !TestAnyOwner)
                    {
                        ServiceHost.Log("Agent: refused a pipe " + PipeName + " not owned by SYSTEM (owner " + owner + ").");
                        Thread.Sleep(5000);
                        continue;
                    }
                    while (true)
                    {
                        Protocol.ReadExactly(pipe, head);
                        int n = BitConverter.ToInt32(head, 1);
                        if (n < 0 || n > 64 << 20) throw new InvalidDataException("a message too big");
                        byte[] p = new byte[n];
                        Protocol.ReadExactly(pipe, p);
                        switch (head[0])
                        {
                            case KeyMessage when n == 5: key(BitConverter.ToUInt16(p, 0), BitConverter.ToUInt16(p, 2), (p[4] & 1) != 0, (p[4] & 2) != 0); break;
                            case TextMessage: text(Encoding.UTF8.GetString(p)); break;
                            case FilesMessage: files(Encoding.UTF8.GetString(p).Split('\n', StringSplitOptions.RemoveEmptyEntries)); break;
                        }
                    }
                }
                catch { Thread.Sleep(500); } // the service is starting or restarting: try again soon
            }
        }
    }
}
