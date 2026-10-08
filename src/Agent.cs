using System;
using System.IO;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// The hosting half of the service: "TailRemote.exe --agent", started by the
    /// service as SYSTEM in the console session. No window. It hosts with the
    /// service's settings, follows the input desktop so keys reach the lock
    /// screen and UAC prompts, shares the clipboard, and saves received files in
    /// the signed-in user's Downloads (or Public Downloads when nobody is).
    /// </summary>
    internal sealed class Agent : ApplicationContext
    {
        private readonly Host _host;
        private readonly ClipboardWindow _clip;
        private readonly System.Windows.Forms.Timer _folderTimer = new() { Interval = 5000 };

        public static int Run()
        {
            Native.FollowInputDesktop = true;
            var cfg = ServiceHost.LoadConfig();
            DiagLog.Enabled = cfg?.Logging == true;
            if (cfg == null) { ServiceHost.Log("Agent: no settings, so not hosting."); return 1; }
            ApplicationConfiguration.Initialize();
            try { Application.Run(new Agent(cfg)); }
            catch (Exception e) { ServiceHost.Log("Agent stopped: " + e.Message); return 1; }
            return 0;
        }

        private Agent(ServiceHost.Config cfg)
        {
            ChooseFolder();
            _folderTimer.Tick += (_, _) => ChooseFolder();
            _folderTimer.Start();

            AudioSetup.FinishQuietly();
            _host = new Host(cfg.Port, ServiceHost.Config.Open(cfg.PasswordEnc), ServiceHost.Config.Open(cfg.ListenPasswordEnc), ServiceHost.Log,
                string.IsNullOrEmpty(cfg.CaptureDevice) ? null : cfg.CaptureDevice)
            {
                SecureAttention = ServiceHost.RequestSas,
            };
            // The service has no window, so it never sends; what the controlling PC sends with
            // Send clipboard goes onto this PC's clipboard.
            _clip = new ClipboardWindow(_host, share: false);
            _host.ClipboardReceived += text => _clip.Arrived(text);
            _host.ClipboardFilesReceived += paths => _clip.FilesArrived(paths);
            ServiceHost.Log("Agent hosting on port " + cfg.Port + ".");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _host.Dispose(); _clip.DestroyHandle(); }
            base.Dispose(disposing);
        }

        private static void ChooseFolder()
        {
            string? profile = NativeService.UserProfile(NativeService.WTSGetActiveConsoleSessionId());
            string root = profile ?? Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public");
            // The signed-in user's own holding folder: the agent runs as SYSTEM, whose folders
            // the user's Explorer could not paste from.
            FileChannel.StagingOverride = Path.Combine(root, "AppData", "Local", "TailRemote", "Clipboard");
        }

        /// <summary>A hidden window that hears clipboard changes and sets the clipboard on its own thread.</summary>
        private sealed class ClipboardWindow : NativeWindow
        {
            private readonly Host _host;
            private readonly bool _share;
            private string? _lastIn, _lastFiles; // fingerprints, never the text itself

            private static string Print(string text) =>
                text.Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), 0, 12);
            private uint _own;
            private long _ownAt;
            private readonly System.Windows.Forms.Timer _gather = new() { Interval = 150 };
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
            private readonly Control _invoker = new();

            public ClipboardWindow(Host host, bool share)
            {
                _host = host;
                _share = share;
                _invoker.CreateControl();
                _gather.Tick += (_, _) => { _gather.Stop(); Settled(); };
                CreateHandle(new CreateParams());
                Native.AddClipboardFormatListener(Handle);
            }

            public void Arrived(string text)
            {
                if (!_invoker.IsHandleCreated) return;
                _invoker.BeginInvoke(() =>
                {
                    _lastIn = Print(text);
                    for (int i = 0; i < 5; i++)
                    {
                        try { _ownAt = Environment.TickCount64; Clipboard.SetDataObject(text, true, 2, 50); _own = GetClipboardSequenceNumber(); _ownAt = Environment.TickCount64; return; }
                        catch { System.Threading.Thread.Sleep(20); }
                    }
                });
            }

            public void FilesArrived(string[] paths)
            {
                if (!_invoker.IsHandleCreated) return;
                _invoker.BeginInvoke(() =>
                {
                    _lastFiles = string.Join("|", paths);
                    var list = new System.Collections.Specialized.StringCollection();
                    list.AddRange(paths);
                    var data = new DataObject();
                    data.SetFileDropList(list);
                    data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // paste copies
                    for (int i = 0; i < 5; i++)
                    {
                        try { _ownAt = Environment.TickCount64; Clipboard.SetDataObject(data, true, 2, 50); _own = GetClipboardSequenceNumber(); _ownAt = Environment.TickCount64; return; }
                        catch { System.Threading.Thread.Sleep(20); }
                    }
                });
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x031D && _share) // WM_CLIPBOARDUPDATE, only while sharing
                {
                    // Not our own paste (0.6 s quiet after it), and only once the copying stops for 150 ms.
                    if (Environment.TickCount64 - _ownAt >= 600) { _gather.Stop(); _gather.Start(); }
                }
                base.WndProc(ref m);
            }

            private void Settled()
            {
                {
                    try
                    {
                        if (GetClipboardSequenceNumber() == _own || Environment.TickCount64 - _ownAt < 600) return; // what the agent itself just put there
                        if (Clipboard.ContainsFileDropList())
                        {
                            var list = Clipboard.GetFileDropList();
                            var files = new string[list.Count];
                            list.CopyTo(files, 0);
                            string key = string.Join("|", files);
                            if (files.Length > 0 && key != _lastFiles) _host.SendClipboardFiles(files);
                            _lastFiles = key;
                            _lastIn = null;
                        }
                        else
                        {
                            string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                            string print = Print(text);
                            if (text.Length > 0 && print != _lastIn) _host.SendClipboard(text);
                            _lastIn = print;
                            _lastFiles = null;
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
