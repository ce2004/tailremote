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
            _clip = new ClipboardWindow(_host);
            _host.ClipboardReceived += text => _clip.Arrived(text);
            _host.FileMessage += ServiceHost.Log;
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
            FileChannel.FolderOverride = Path.Combine(root, "Downloads", "TailRemote");
        }

        /// <summary>A hidden window that hears clipboard changes and sets the clipboard on its own thread.</summary>
        private sealed class ClipboardWindow : NativeWindow
        {
            private readonly Host _host;
            private string? _lastIn;
            private readonly Control _invoker = new();

            public ClipboardWindow(Host host)
            {
                _host = host;
                _invoker.CreateControl();
                CreateHandle(new CreateParams());
                Native.AddClipboardFormatListener(Handle);
            }

            public void Arrived(string text)
            {
                if (!_invoker.IsHandleCreated) return;
                _invoker.BeginInvoke(() =>
                {
                    _lastIn = text;
                    for (int i = 0; i < 5; i++)
                    {
                        try { Clipboard.SetText(text); return; }
                        catch { System.Threading.Thread.Sleep(20); }
                    }
                });
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x031D) // WM_CLIPBOARDUPDATE
                {
                    try
                    {
                        string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                        if (text.Length > 0 && text != _lastIn && text.Length <= Protocol.MaxClipboardChars) _host.SendClipboard(text);
                        _lastIn = null;
                    }
                    catch { }
                }
                base.WndProc(ref m);
            }
        }
    }
}
