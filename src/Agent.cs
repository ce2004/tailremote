using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Hosting as the Windows service, in two halves.
    ///
    /// The service itself (SYSTEM, session 0) hosts: connections, the sound, files, Control Alt
    /// Delete. It starts with Windows, before the sign-in screen, so the controlling PC can be
    /// connected and hear the startup sound, and the connection carries on through signing in,
    /// out, and switching user.
    ///
    /// "TailRemote.exe --agent" runs as SYSTEM in whichever session is at the screen (the service
    /// keeps one there). It types the keys the service hands it, on whichever desktop has the
    /// keyboard (the lock screen and UAC prompts too), and puts on the clipboard what arrives when
    /// no TailRemote window is open to do it.
    /// </summary>
    internal static class Agent
    {
        // ================= In the service =================

        private static Host? _host;
        private static volatile bool _stopping;
        private static System.Threading.Timer? _folders;

        /// <summary>Starts hosting from the service. Retries until it can (the network may not be up yet at boot); never throws.</summary>
        public static void StartHosting()
        {
            var cfg = ServiceHost.LoadConfig();
            DiagLog.Enabled = cfg?.Logging == true;
            if (cfg == null) { ServiceHost.Log("No settings, so not hosting."); return; }
            ChooseFolders();
            _folders = new System.Threading.Timer(_ => ChooseFolders(), null, 5000, 5000);
            var agent = new AgentLink.Server();
            Native.KeySink = agent.Key; // keys are typed by the agent, at the screen
            string? lastError = null;
            while (!_stopping)
            {
                try
                {
                    _host = new Host(cfg.Port, ServiceHost.Config.Open(cfg.PasswordEnc), ServiceHost.Config.Open(cfg.ListenPasswordEnc), ServiceHost.Log,
                        string.IsNullOrEmpty(cfg.CaptureDevice) ? null : cfg.CaptureDevice)
                    {
                        SecureAttention = SendSecureAttention,
                    };
                    break;
                }
                catch (Exception e)
                {
                    if (e.Message != lastError) ServiceHost.Log("Not hosting yet: " + e.Message + " Trying again.");
                    lastError = e.Message;
                    Thread.Sleep(500);
                }
            }
            if (_host == null) return;
            // The TailRemote window, when open, does the clipboard and sends files through the service
            // (ServiceLink), as the signed-in user; it must be in the session at the screen. Without a
            // window, what the controlling PC sends goes onto the clipboard through the agent.
            var link = new ServiceLink.Server(_host, NativeService.WTSGetActiveConsoleSessionId)
            {
                FolderChosen = folder => { _windowFolder = folder; ChooseFolders(); }, // picked in the window, checked as its user
            };
            _host.ClipboardReceived += text => { if (!link.Text(text)) agent.Text(text); };
            _host.ClipboardFilesReceived += paths => { if (!link.Files(paths)) agent.Files(paths); };
            _host.TransferProgress += t => link.Transfer(t);
            _host.UpdateRequested += ServiceHost.UpdateFor;
            ServiceHost.Log("Hosting on port " + cfg.Port + ", from the service.");
            // Audio settings and device names are never touched here: only Set up audio device, when pressed.
        }

        public static void StopHosting(byte why = Protocol.LeavingStopped)
        {
            _stopping = true;
            _host?.Leave(why);
            _host?.Dispose();
        }

        /// <summary>The service is about to restart into a new version: the connected PCs wait for it quietly.</summary>
        public static void LeaveForUpdate(string version) => _host?.Leave(Protocol.LeavingUpdating, version);

        /// <summary>Only a service may send Control Alt Delete; this is the service.</summary>
        private static bool SendSecureAttention()
        {
            try { NativeService.SendSAS(false); ServiceHost.Log("Sent Control Alt Delete."); return true; }
            catch { return false; }
        }

        /// <summary>Received files go into the folders of whoever is signed in at the screen (Public before anyone is).</summary>
        private static void ChooseFolders()
        {
            uint session = NativeService.WTSGetActiveConsoleSessionId();
            string? profile = NativeService.UserProfile(session);
            string root = profile ?? Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public");
            // The signed-in user's own folders, wherever they really are (Downloads moved to another
            // drive or OneDrive): the service runs as SYSTEM, whose own folders are deep inside Windows
            // and which the user's Explorer could not paste from.
            string local = (profile != null ? NativeService.UserFolder(session, NativeService.FolderLocalAppData) : null) ?? Path.Combine(root, "AppData", "Local");
            string downloads = (profile != null ? NativeService.UserFolder(session, NativeService.FolderDownloads) : null) ?? Path.Combine(root, "Downloads");
            FileChannel.StagingOverride = Path.Combine(local, "TailRemote", "Clipboard");
            FileChannel.DownloadsOverride = _windowFolder ?? Path.Combine(downloads, "TailRemote");
        }

        /// <summary>Where the TailRemote window says received files go; null: the signed-in user's Downloads\TailRemote.</summary>
        private static volatile string? _windowFolder;

        // ================= In the session at the screen (--agent) =================

        public static int Run()
        {
            // Keys go to whichever desktop has the keyboard: the lock screen, sign-in and UAC prompts too.
            Native.FollowInputDesktop = true;
            var clip = new ClipboardSetter();
            AgentLink.Run((vk, scan, up, ext) => Native.SendKey(vk, scan, up, ext), clip.Arrived, clip.FilesArrived);
            return 0;
        }

        /// <summary>
        /// Puts what arrives onto this PC's clipboard when no TailRemote window is open to do it.
        /// On a thread of its own, set up for the clipboard (STA): the main thread is not, and every
        /// clipboard call made there used to fail without a word (nothing could be pasted).
        /// </summary>
        private sealed class ClipboardSetter
        {
            private readonly System.Collections.Concurrent.BlockingCollection<Action> _jobs = new(16);

            public ClipboardSetter()
            {
                var t = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) try { job(); } catch { } })
                    { IsBackground = true, Name = "TailRemote agent clipboard" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }

            // Nothing never replaces what is on the clipboard here: an empty one sent from the other PC wiped this one.
            public void Arrived(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                _jobs.TryAdd(() => Set(new DataObject(DataFormats.UnicodeText, text)));
            }

            public void FilesArrived(string[] paths) => _jobs.TryAdd(() =>
            {
                if (paths.Length == 0) return;
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(paths);
                var data = new DataObject();
                data.SetFileDropList(list);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(2))); // paste moves them out of the holding folder: never twice the space
                Set(data);
            });

            private static void Set(DataObject data)
            {
                for (int i = 0; ; i++)
                {
                    try { Clipboard.SetDataObject(data, true, 2, 50); return; }
                    catch (Exception e)
                    {
                        if (i == 4) { ServiceHost.Log("Could not put it on the clipboard: " + e.Message); return; }
                        Thread.Sleep(100); // another program has it open
                    }
                }
            }
        }
    }
}
