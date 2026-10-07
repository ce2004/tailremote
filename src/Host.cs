using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The PC being controlled. One controller at a time (a new controller
    /// replaces the old one, so reconnecting after a dropped network just works),
    /// plus up to four listeners who hear everything but cannot type. Which one a
    /// client becomes depends on which password it proved.
    /// </summary>
    internal sealed class Host : IDisposable
    {
        public const int MaxListeners = 4;

        private readonly byte[] _key;
        private readonly byte[]? _listenKey;
        private readonly Action<string> _status;
        private readonly TcpListener _listener;
        private readonly UdpClient _udp;
        private volatile bool _stop;

        /// <summary>The pause before answering a wrong password. The self-test sets it to 0.</summary>
        public static int WrongPasswordDelayMs = 2000;

        /// <summary>Clipboard text from the controller. Raised on a network thread.</summary>
        public event Action<string>? ClipboardReceived;
        /// <summary>A sentence about a file that arrived. Raised on a network thread.</summary>
        public event Action<string>? FileMessage;

        private readonly object _gate = new();
        private Session? _controller;
        private readonly List<Session> _listeners = new();
        private LoopbackCapture? _capture;

        private sealed class Session
        {
            public required TcpClient Tcp;
            public required NetworkStream Stream;
            public required byte[] Token;
            public required IPAddress Address;
            public required SecureLink Link;
            public required byte Role;
            public required byte[] Key, HostNonce, ClientNonce;
            public FileChannel? Files;
            public uint PeerFeatures;
            public volatile IPEndPoint? AudioTo;
            public readonly HashSet<(ushort Vk, bool Ext)> Held = new();
            public readonly byte[] Audio = new byte[Protocol.AudioPacketBytes];
        }

        public Host(int port, string password, string? listenPassword, Action<string> status)
        {
            _key = Protocol.DeriveKey(password);
            _listenKey = string.IsNullOrEmpty(listenPassword) ? null : Protocol.DeriveKey(listenPassword);
            _status = status;

            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true;
            _udp = new UdpClient(AddressFamily.InterNetworkV6);
            _udp.Client.DualMode = true;
            // Exclusive: no other program may share the port while TailRemote holds it.
            _listener.ExclusiveAddressUse = true;
            _udp.Client.ExclusiveAddressUse = true;
            try
            {
                _listener.Start();
                _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch (SocketException e)
            {
                _listener.Stop();
                _udp.Dispose();
                throw new InvalidOperationException(e.SocketErrorCode == SocketError.AccessDenied
                    ? "Port " + port + " is reserved by Windows on this PC. Choose another port."
                    : "Port " + port + " is already used by another program on this PC. Choose another port.");
            }
            IgnoreUdpResets(_udp.Client);

            new Thread(AcceptLoop) { IsBackground = true, Name = "TailRemote accept" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "TailRemote host udp" }.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            lock (_gate)
            {
                foreach (var s in AllSessions()) End(s, null);
                _controller = null;
                _listeners.Clear();
                UpdateCapture();
            }
        }

        /// <summary>
        /// Windows reports an ICMP "port unreachable" from an earlier send as an
        /// error on the next receive, which would end the receive loop.
        /// </summary>
        public static void IgnoreUdpResets(Socket s)
        {
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
        }

        /// <summary>Sends clipboard text to the controller, if it shares the clipboard.</summary>
        public void SendClipboard(string text)
        {
            Session? c;
            lock (_gate) c = _controller;
            if (c == null || (c.PeerFeatures & Protocol.FeatureClipboard) == 0) return;
            try { c.Link.Send(c.Stream, Protocol.TextMessage(Protocol.Clipboard, text)); } catch { }
        }

        /// <summary>Sends files to the controller. Blocking; throws with a readable message.</summary>
        public string SendFiles(IReadOnlyList<string> paths, Action<string, int> report, CancellationToken ct)
        {
            Session? c;
            lock (_gate) c = _controller;
            var files = c?.Files ?? throw new InvalidOperationException("No one is controlling this PC, so there is no one to send files to.");
            return files.Send(paths, report, ct);
        }

        private List<Session> AllSessions()
        {
            var all = new List<Session>(_listeners);
            if (_controller != null) all.Add(_controller);
            return all;
        }

        /// <summary>One capture serves everyone; it runs only while someone is connected. Call under _gate.</summary>
        private void UpdateCapture()
        {
            bool anyone = !_stop && (_controller != null || _listeners.Count > 0);
            if (anyone && _capture == null)
                _capture = new LoopbackCapture(SendAudio, msg => { _status(msg); Broadcast(msg); });
            else if (!anyone && _capture != null)
            {
                var c = _capture;
                _capture = null;
                // Disposing joins the capture thread, which may be waiting on _gate: do it outside.
                ThreadPool.QueueUserWorkItem(_ => c.Dispose());
            }
        }

        private void Broadcast(string msg)
        {
            List<Session> all;
            lock (_gate) all = AllSessions();
            foreach (var s in all) { try { Protocol.SendMessage(s.Link, s.Stream, msg); } catch { } }
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_stop) return; continue; }
                new Thread(() => Serve(tcp)) { IsBackground = true, Name = "TailRemote session" }.Start();
            }
        }

        private void Serve(TcpClient tcp)
        {
            var remote = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            Session? s = null;
            bool handedOff = false;
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                stream.ReadTimeout = 10_000;

                byte[] nonce = RandomNumberGenerator.GetBytes(16);
                byte[] hello = new byte[20];
                Protocol.Magic.CopyTo(hello, 0);
                nonce.CopyTo(hello, 4);
                stream.Write(hello);

                byte[] answer = new byte[52];
                Protocol.ReadExactly(stream, answer);
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.FileMagic))
                {
                    // The controller's second connection, for files.
                    // It can arrive a moment before the main connection is registered.
                    Session? owner = null;
                    for (int i = 0; i < 40 && owner == null; i++)
                    {
                        lock (_gate) owner = _controller != null && _controller.Token.AsSpan().SequenceEqual(answer.AsSpan(4, 8)) && _controller.Address.Equals(remote) ? _controller : null;
                        if (owner == null) Thread.Sleep(50);
                    }
                    if (owner == null) { tcp.Dispose(); return; }
                    owner.Files?.Dispose();
                    owner.Files = new FileChannel(tcp, new SecureLink(owner.Key, owner.HostNonce, owner.ClientNonce, isHost: true, "files "),
                        msg => FileMessage?.Invoke(msg));
                    handedOff = true;
                    return;
                }
                byte[] clientNonce = answer[4..20];
                byte role = 0;
                byte[]? key = null;
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.Magic))
                {
                    if (CryptographicOperations.FixedTimeEquals(answer.AsSpan(20), Protocol.Proof(_key, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleControl; key = _key; }
                    else if (_listenKey != null && CryptographicOperations.FixedTimeEquals(answer.AsSpan(20), Protocol.Proof(_listenKey, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleListen; key = _listenKey; }
                }
                if (key == null)
                {
                    Thread.Sleep(WrongPasswordDelayMs); // slows down anyone guessing passwords
                    stream.Write(new byte[] { 0 });
                    _status("Refused " + remote + ": wrong password.");
                    tcp.Dispose();
                    return;
                }

                byte[] token = RandomNumberGenerator.GetBytes(8);
                byte[] accept = new byte[42];
                accept[0] = 1;
                accept[1] = role;
                token.CopyTo(accept, 2);
                Protocol.Proof(key, 'H', clientNonce, nonce).CopyTo(accept, 10);
                stream.Write(accept);
                // The client pings every 2 seconds; 10 silent seconds means it is gone,
                // and ending the session lets go of any keys it was holding.
                stream.ReadTimeout = 10_000;

                s = new Session
                {
                    Tcp = tcp, Stream = stream, Token = token, Address = remote, Role = role,
                    Key = key, HostNonce = nonce, ClientNonce = clientNonce,
                    Link = new SecureLink(key, nonce, clientNonce, isHost: true),
                };
                s.Link.Send(s.Stream, Protocol.FeaturesMessage());
                lock (_gate)
                {
                    if (_stop) { End(s, null); return; }
                    if (role == Protocol.RoleControl)
                    {
                        if (_controller != null) End(_controller, "Replaced by a new connection.");
                        _controller = s;
                    }
                    else
                    {
                        if (_listeners.Count >= MaxListeners)
                        {
                            End(s, "There are already " + MaxListeners + " people listening.");
                            s = null;
                            return;
                        }
                        _listeners.Add(s);
                    }
                    UpdateCapture();
                }
                _status((role == Protocol.RoleControl ? "Connected: " : "Listening: ") + remote + ".");

                ReadLoop(s);
            }
            catch (Exception e)
            {
                if (s == null) _status("Connection from " + remote + " failed: " + e.Message);
            }
            finally
            {
                if (s != null)
                {
                    bool was = false;
                    lock (_gate)
                    {
                        if (_controller == s) { _controller = null; was = true; }
                        else if (_listeners.Remove(s)) was = true;
                        if (was) { End(s, null); UpdateCapture(); }
                    }
                    if (was) _status((s.Role == Protocol.RoleControl ? "Disconnected: " : "Stopped listening: ") + remote + ".");
                }
                else if (!handedOff) tcp.Dispose();
            }
        }

        private void ReadLoop(Session s)
        {
            while (!_stop)
            {
                byte[] m = s.Link.Receive(s.Stream);
                if (m.Length == 0) continue;
                switch (m[0])
                {
                    case Protocol.Key when m.Length == 6:
                        ushort vk = BitConverter.ToUInt16(m, 1);
                        ushort scan = BitConverter.ToUInt16(m, 3);
                        bool up = (m[5] & 1) != 0, ext = (m[5] & 2) != 0;
                        lock (_gate)
                        {
                            // Only the current controller types. A replaced or closed session
                            // must not press anything after its keys were released.
                            if (_controller != s) break;
                            lock (s.Held) { if (up) s.Held.Remove((vk, ext)); else s.Held.Add((vk, ext)); }
                            Native.SendKey(vk, scan, up, ext);
                        }
                        break;
                    case Protocol.Ping when m.Length == 9:
                        m[0] = Protocol.Pong;
                        s.Link.Send(s.Stream, m);
                        break;
                    case Protocol.ReleaseAll:
                        ReleaseHeld(s);
                        break;
                    case Protocol.Features when m.Length >= 5:
                        s.PeerFeatures = BitConverter.ToUInt32(m, 1);
                        break;
                    case Protocol.Clipboard when s.Role == Protocol.RoleControl:
                        ClipboardReceived?.Invoke(System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                        break;
                    // Anything else is from a newer version: ignore it.
                }
            }
        }

        private static void ReleaseHeld(Session s)
        {
            lock (s.Held)
            {
                foreach (var (vk, ext) in s.Held) Native.SendKey(vk, 0, true, ext);
                s.Held.Clear();
            }
        }

        private static void End(Session s, string? why)
        {
            if (why != null) { try { Protocol.SendMessage(s.Link, s.Stream, why); } catch { } }
            ReleaseHeld(s);
            try { s.Tcp.Dispose(); } catch { }
            s.Files?.Dispose();
        }

        /// <summary>Called on the capture thread with each packet; sent to every session that said where.</summary>
        private void SendAudio(uint seq, short[]? pcm)
        {
            List<Session> all;
            lock (_gate) all = AllSessions();
            int payload = pcm == null ? 0 : pcm.Length * 2;
            foreach (var s in all)
            {
                var to = s.AudioTo;
                if (to == null) continue;
                byte[] a = s.Audio; // each session seals its own copy with its own keys
                a[0] = pcm == null ? Protocol.UdpSilence : Protocol.UdpAudio;
                BitConverter.TryWriteBytes(a.AsSpan(1), seq);
                if (pcm != null) Buffer.BlockCopy(pcm, 0, a, 5, payload);
                try
                {
                    s.Link.SealAudio(a, payload);
                    _udp.Send(a, 5 + payload + SecureLink.TagSize, to);
                }
                catch { }
            }
        }

        private void UdpLoop()
        {
            var any = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!_stop)
            {
                byte[] d;
                try { d = _udp.Receive(ref any); }
                catch { if (_stop) return; continue; }
                if (d.Length != 9 || d[0] != Protocol.UdpHello) continue;
                Session? s;
                lock (_gate) s = AllSessions().FirstOrDefault(x => d.AsSpan(1).SequenceEqual(x.Token));
                if (s == null) continue;
                var from = any.Address.IsIPv4MappedToIPv6 ? any.Address.MapToIPv4() : any.Address;
                if (!from.Equals(s.Address)) continue;
                s.AudioTo = new IPEndPoint(any.Address, any.Port);
            }
        }
    }
}
