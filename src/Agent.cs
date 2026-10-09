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
        private readonly ClipboardSetter _clip;
        private readonly ServiceLink.Server _link;
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
            // The TailRemote window, when open, does the clipboard and sends files through this
            // agent (ServiceLink), as the signed-in user. Without a window, what the controlling
            // PC sends still goes onto this PC's clipboard, from here.
            _clip = new ClipboardSetter();
            _link = new ServiceLink.Server(_host);
            _host.ClipboardReceived += text => { if (!_link.Text(text)) _clip.Arrived(text); };
            _host.ClipboardFilesReceived += paths => { if (!_link.Files(paths)) _clip.FilesArrived(paths); };
            _host.TransferProgress += t => _link.Transfer(t);
            ServiceHost.Log("Agent hosting on port " + cfg.Port + ".");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _host.Dispose();
            base.Dispose(disposing);
        }

        private static void ChooseFolder()
        {
            uint session = NativeService.WTSGetActiveConsoleSessionId();
            string? profile = NativeService.UserProfile(session);
            string root = profile ?? Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public");
            // The signed-in user's own folders, wherever they really are (Downloads moved to another
            // drive or OneDrive): the agent runs as SYSTEM, whose own folders are deep inside Windows
            // and which the user's Explorer could not paste from.
            string local = (profile != null ? NativeService.UserFolder(session, NativeService.FolderLocalAppData) : null) ?? Path.Combine(root, "AppData", "Local");
            string downloads = (profile != null ? NativeService.UserFolder(session, NativeService.FolderDownloads) : null) ?? Path.Combine(root, "Downloads");
            FileChannel.StagingOverride = Path.Combine(local, "TailRemote", "Clipboard");
            FileChannel.DownloadsOverride = Path.Combine(downloads, "TailRemote");
        }

        /// <summary>
        /// Puts what arrives onto this PC's clipboard when no TailRemote window is open to do it.
        /// On a thread of its own, set up for the clipboard (STA): the agent's main thread is not,
        /// so every clipboard call made there used to fail without a word, and nothing that the
        /// controlling PC sent could be pasted.
        /// </summary>
        private sealed class ClipboardSetter
        {
            private readonly System.Collections.Concurrent.BlockingCollection<Action> _jobs = new(16);

            public ClipboardSetter()
            {
                var t = new System.Threading.Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) try { job(); } catch { } })
                    { IsBackground = true, Name = "TailRemote agent clipboard" };
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
            }

            public void Arrived(string text) => _jobs.TryAdd(() => Set(new DataObject(DataFormats.UnicodeText, text)));

            public void FilesArrived(string[] paths) => _jobs.TryAdd(() =>
            {
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
                        System.Threading.Thread.Sleep(100); // another program has it open
                    }
                }
            }
        }
    }
}
