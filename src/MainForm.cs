using System;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace TailRemote
{
    internal sealed class MainForm : Form
    {
        private readonly Settings _settings = Settings.Load();
        private readonly bool _autoHost, _autoConnect, _updated;

        // The window is blank: everything is in the menu bar (Alt), File, Clipboard and Settings.
        private readonly MenuChoice _mode = new("&Mode");
        private readonly MenuText _address = new("&Address", "The other PC's Tailscale name, IP address or internet address:");
        private readonly MenuText _port = new("&Port", "The port (the same on both PCs; 47120 unless you changed it):");
        private readonly MenuText _password = new("Pass&word", "The password (at least 5 characters, the same on both PCs):", secret: true);
        private readonly MenuChoice _device = new("&Output device");
        private readonly MenuChoice _quality = new("Sound &quality");
        private readonly MenuStatus _streaming = new("Not connected");
        private readonly MenuChoice _captureFrom = new("C&apture sound from (what other PCs hear)");
        private bool _fillingCapture;
        private readonly MenuChoice _saved = new("Sa&ved PCs");
        private readonly ToolStripMenuItem _savePc = new("Save t&his PC");
        private readonly ToolStripMenuItem _forgetPc = new("&Forget the chosen saved PC");
        private readonly MenuText _listenPassword = new("&Listen-only password", "A second password that lets someone hear this PC but not control it. Leave it empty for none:", secret: true);
        private readonly ToolStripMenuItem _sendClipboard = new("Send the clip&board") { ShortcutKeys = Keys.Control | Keys.B };
        private readonly ToolStripMenuItem _sendFiles = new("Send f&iles") { ShortcutKeys = Keys.Control | Keys.I };
        private readonly ToolStripMenuItem _sendFolder = new("Send a folder and everything in i&t") { ShortcutKeys = Keys.Control | Keys.T };
        // Files: first in the Clipboard menu. While files move, it opens a line per file.
        private readonly MenuStatus _transferStatus = new("");
        private readonly ToolStripMenuItem _transferStop = new("&Stop the file transfer") { Enabled = false };
        // Where Send files and Send a folder from the other PC are saved: the menu item says where.
        private readonly ToolStripMenuItem _pickReceiveFolder = new("Pic&k where received files go");
        private readonly ToolStripMenuItem _logging = Menus.Check("Enable lo&gging (writes TailRemote-log.txt next to TailRemote)");
        private readonly ToolStripMenuItem _sounds = new("Sounds for connecting, clipboard and fi&les...");
        private readonly ToolStripMenuItem _speedUp = Menus.Check("Catch up b&y fast-forwarding the sound, same pitch (otherwise it skips ahead)");
        private readonly ToolStripMenuItem _startup = Menus.CheckAsking("Start &hosting when Windows starts (asks for administrator)...");
        private readonly ToolStripMenuItem _service = Menus.CheckAsking("Run as a Windows servi&ce: works at the lock screen, sign-in and UAC prompts; Control Alt End sends Control Alt Delete...");
        private bool _settingService; // set while the checkbox is changed by code, not by the user
        private readonly ToolStripMenuItem _go = new();
        private readonly ToolStripMenuItem _toggle = new("Control &remote PC") { ShortcutKeyDisplayString = "Ctrl+Shift+Enter" };
        private readonly ToolStripMenuItem _update = new("Check for &updates (you have " + Updater.Current + ")");
        private readonly ToolStripMenuItem _audioSetup = new("Set up au&dio device...");
        private readonly ToolStripMenuItem _audioRemove = new("Remove audio de&vice...");
        private readonly ToolStripMenuItem _portEditor = new("Port &editor...");
        private readonly ToolStripMenuItem _restart = new("Restart remote PC and reco&nnect...");
        private readonly ToolStripMenuItem _updateRemote = new("Update the remote P&C to this PC's version");
        private readonly ToolStripMenuItem _remoteInfo = new("Remote PC &info...") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.I };
        private readonly ToolStripMenuItem _getFiles = new("&Get files from the remote PC...") { ShortcutKeys = Keys.Control | Keys.G };
        private readonly ToolStripMenuItem _announceQuality = Menus.Check("A&nnounce when the sound quality changes");
        private Client? _seenClient;  // the connection the version and quality below belong to
        private int _qualitySaid;      // the bitrate step last announced
        private bool _expectRestart; // the remote PC was asked to restart: say when it is back
        private string? _expectUpdate; // the remote PC said it is updating TailRemote to this version: say when it is back

        // Host: this PC's addresses, to give the other PC. Enter copies the best one.
        private readonly ToolStripMenuItem _copyAddress = new();
        private string? _bestAddress;
        private readonly System.Collections.Generic.List<(string Id, string Name)> _devices = new();

        private readonly NotifyIcon _tray = new() { Text = "TailRemote, hosting", Icon = SystemIcons.Application };
        private bool _reallyExit; // Stop hosting and exit, from the tray: close for real
        private Client? _client;
        private Player? _player;       // kept between connections: no device opens or closes on connect
        private string? _playerDevice;
        private Host? _host;
        private KeyCapture? _keys;
        private IntPtr _handle;
        private bool _connecting, _reconnecting;
        private int _attempt; // bumped to abandon a connection attempt still under way
        private readonly Timer _titleTimer = new() { Interval = 1000 };
        private readonly Timer _retryTimer = new() { Interval = 1000 }; // Connect keeps trying every second until Disconnect (a PC coming back from a restart is caught at once)
        private bool _resumeRemote;     // was controlling the remote PC when the connection dropped
        private bool _quietModeChange;  // the reconnect message already says it

        public MainForm(bool autoHost, bool autoConnect, bool updated)
        {
            _autoHost = autoHost;
            _autoConnect = autoConnect;
            _updated = updated;
            Text = "TailRemote";
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(420, 120);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            _mode.Add("Control another PC");
            _mode.Add("Host: let this PC be controlled");
            _copyAddress.Click += (_, _) => CopyAddress();
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Later(FillMyAddress);
            FillMyAddress();
            _pickReceiveFolder.Click += (_, _) => PickReceiveFolder();
            _transferStatus.Text = TransferIdle;
            _transferStop.Click += (_, _) =>
            {
                if (_client != null) _client.CancelTransfer();
                else Client.ForgetTransfers(); // waiting for a reconnection: stopped for good
                _host?.CancelTransfer();
                _serviceLink?.CancelTransfer();
            };

            // The menu bar (Alt, then the arrows): File, Clipboard, Settings.
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.AddRange(new ToolStripItem[]
            {
                _go, _toggle, _restart, _remoteInfo, _updateRemote, _streaming.Item, new ToolStripSeparator(),
                _mode.Menu, _saved.Menu, _address.Item, _port.Item, _password.Item, _listenPassword.Item, _copyAddress,
                _savePc, _forgetPc, new ToolStripSeparator(),
                Menus.Action("E&xit", ExitForGood),
            });
            var clip = new ToolStripMenuItem("&Clipboard");
            clip.DropDownItems.AddRange(new ToolStripItem[]
            {
                _transferStatus.Item, _sendClipboard, _sendFiles, _sendFolder, _getFiles, _transferStop, new ToolStripSeparator(), _pickReceiveFolder,
            });
            var set = new ToolStripMenuItem("&Settings");
            set.DropDownItems.AddRange(new ToolStripItem[]
            {
                _quality.Menu, _announceQuality, _device.Menu, _captureFrom.Menu, _speedUp, _sounds, new ToolStripSeparator(),
                _startup, _service, _portEditor, _audioSetup, _audioRemove, new ToolStripSeparator(),
                _logging,
            });
            var bar = new MenuStrip { Dock = DockStyle.Top };
            bar.Items.AddRange(new ToolStripItem[] { file, clip, set, AboutMenu.Build(this, bar, _update) });
            Menus.Attach(bar);
            Controls.Add(bar);
            MainMenuStrip = bar;

            _mode.SelectedIndex = _settings.HostMode ? 1 : 0;
            _address.Text = _settings.Address;
            _port.Text = _settings.Port.ToString();
            _password.Text = _settings.Password;
            _listenPassword.Text = _settings.ListenPassword;
            _logging.Checked = _settings.Logging;
            _speedUp.Checked = _settings.CatchUpBySpeed;
            _announceQuality.Checked = _settings.AnnounceQuality;
            ApplyReceiveFolder();
            Sounds.Key = Math.Clamp(_settings.SoundKey, 0, 11);
            Sounds.Choice = t => _settings.SoundChoices.TryGetValue(t.ToString(), out var s) && (s == Sounds.RandomName || Sounds.All.Contains(s)) ? s : Sounds.DefaultName;
            _sounds.Click += (_, _) =>
            {
                Doing("choosing sounds");
                using var f = new SoundsForm(_settings);
                f.ShowDialog(this);
            };
            Sounds.Warm();
            _quality.Add("Variable: follows the connection");
            foreach (var (kbps, _) in Protocol.OpusSteps) _quality.Add("Locked at " + kbps + " kbit/s");
            _quality.SelectedIndex = Math.Clamp(_settings.SoundQuality + 1, 0, _quality.Count - 1);
            _quality.SelectedIndexChanged += (_, _) =>
            {
                SaveSettings();
                if (_client != null) _client.LockedStep = _quality.SelectedIndex - 1;
            };
            _speedUp.CheckedChanged += (_, _) => { SaveSettings(); if (_player != null) _player.SpeedUp = _speedUp.Checked; };
            DiagLog.Enabled = _settings.Logging;
            _startup.Checked = Startup.IsEnabled();
            _settingService = true;
            _service.Checked = ServiceHost.IsInstalled();
            _settingService = false;
            FillSaved();

            _device.Add("Windows default");
            _devices.Add(("", "Windows default"));
            foreach (var d in Wasapi.OutputDevices()) { _devices.Add(d); _device.Add(d.Name); }
            int sel = _devices.FindIndex(d => d.Id == _settings.OutputDevice);
            _device.SelectedIndex = sel < 0 ? 0 : sel;
            _fillingCapture = true;
            foreach (var d in _devices) _captureFrom.Add(d.Name);
            int cap = _devices.FindIndex(d => d.Id == _settings.CaptureDevice);
            _captureFrom.SelectedIndex = cap < 0 ? 0 : cap;
            _fillingCapture = false;
            _captureFrom.SelectedIndexChanged += (_, _) => CaptureChanged();

            _mode.SelectedIndexChanged += (_, _) => UpdateMode();
            _saved.SelectedIndexChanged += (_, _) => UseSaved();
            _savePc.Click += (_, _) => SavePc();
            _forgetPc.Click += (_, _) => ForgetPc();
            _sendClipboard.Click += (_, _) => SendClipboard();
            _sendFiles.Click += (_, _) => SendFiles(folder: false);
            _sendFolder.Click += (_, _) => SendFiles(folder: true);
            _logging.CheckedChanged += (_, _) =>
            {
                SaveSettings();
                DiagLog.Enabled = _logging.Checked;
                Say(_logging.Checked ? "Logging to " + DiagLog.FilePath + "." + (_service.Checked ? " Press Apply settings to the service to log there too." : "")
                                     : "Logging is off.");
            };
            _go.Click += (_, _) => Go();
            _address.Changed += SaveSettings;
            _port.Changed += SaveSettings;
            _password.Changed += SaveSettings;
            _listenPassword.Changed += SaveSettings;
            _toggle.Click += (_, _) => _keys?.Toggle();
            _update.Click += (_, _) => CheckForUpdates();
            _restart.Click += (_, _) => RestartRemote();
            _updateRemote.Click += (_, _) => UpdateRemote();
            _remoteInfo.Click += (_, _) => RemoteInfo();
            _getFiles.Click += (_, _) => GetFiles();
            _announceQuality.CheckedChanged += (_, _) => SaveSettings();
            _audioSetup.Click += (_, _) => SetUpAudio();
            _audioRemove.Click += (_, _) => RemoveAudio();
            _portEditor.Click += (_, _) => { SaveSettings(); using var f = new PortEditorForm(_settings); f.ShowDialog(this); };
            _startup.CheckedChanged += (_, _) => StartupChanged();
            _service.CheckedChanged += (_, _) => { if (!_settingService) ServiceChanged(); };
            _titleTimer.Tick += (_, _) => UpdateTitle();
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("&Open TailRemote", null, (_, _) => RestoreFromTray());
            _trayExit = (ToolStripMenuItem)trayMenu.Items.Add("&Stop hosting and exit", null, (_, _) => { _reallyExit = true; RestoreFromTray(); Close(); });
            _tray.ContextMenuStrip = trayMenu;
            _tray.Click += (_, e) => { if (e is not MouseEventArgs m || m.Button == MouseButtons.Left) RestoreFromTray(); };
            _retryTimer.Tick += (_, _) => { if (_client == null && !_connecting) Connect(quiet: true); };
            UpdateMode();
        }

        /// <summary>
        /// Host: the addresses the other PC can connect to. The LAN one (a network with a router),
        /// then Tailscale's, then the PC's name. The LAN address is what Copy IP address copies,
        /// or Tailscale's when there is no LAN.
        /// </summary>
        private void FillMyAddress()
        {
            var lan = new System.Collections.Generic.List<(string Ip, string Network)>();
            string? tailscale = null;
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var props = nic.GetIPProperties();
                    bool router = props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                    foreach (var a in props.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a.Address)) continue;
                        if (Protocol.IsTailscale(a.Address)) tailscale = a.Address.ToString();
                        else if (router && a.Address.GetAddressBytes()[0] != 169) lan.Add((a.Address.ToString(), nic.Name));
                    }
                }
            }
            catch { }
            var parts = lan.Select(l => l.Ip + " (" + l.Network + ")").ToList();
            if (tailscale != null) parts.Add("Tailscale " + tailscale);
            parts.Add("name " + Environment.MachineName);
            _bestAddress = lan.Count > 0 ? lan[0].Ip : tailscale;
            string text = "Cop&y this PC's IP address: " + string.Join(", ", parts).Replace("&", "&&");
            if (_copyAddress.Text != text) _copyAddress.Text = text;
            _copyAddress.Enabled = _bestAddress != null;
        }

        private void CopyAddress()
        {
            FillMyAddress();
            if (_bestAddress is not string ip) { Say("This PC has no network address right now."); return; }
            ClipboardJobs.TryAdd(() => SetClipboard(() => Clipboard.SetText(ip))); // on the clipboard's own thread, never the window's
            Say("Copied " + ip + ".");
        }

        private bool HostMode => _mode.SelectedIndex == 1;

        private void UpdateMode()
        {
            bool host = HostMode;
            _address.Item.Available = !host;
            _device.Menu.Available = !host;
            _saved.Menu.Available = _savePc.Available = _forgetPc.Available = !host;
            _listenPassword.Item.Available = host;
            _copyAddress.Available = host;
            if (host) FillMyAddress();
            _captureFrom.Menu.Available = host;
            _speedUp.Available = !host; // it is about how this PC plays the sound
            _sounds.Available = !host; // a host's sounds would go out with its own sound
            _quality.Menu.Available = !host; // the host always sends the best unless asked for less
            _streaming.Item.Available = !host;
            _toggle.Available = !host;
            _restart.Available = _updateRemote.Available = _remoteInfo.Available = _getFiles.Available = !host;
            _announceQuality.Available = !host;
            _startup.Available = host;
            _service.Available = host;
            _startup.Enabled = !_service.Checked; // the service replaces the at-sign-in task
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool busy = _client != null || _host != null || _reconnecting;
            _mode.Menu.Enabled = !busy;
            // Alt C and Alt S are the Clipboard and Settings menus, so the button uses other letters.
            if (HostMode) _go.Text = _service.Checked ? "Appl&y settings to the service" : _host == null ? "Start &hosting" : "Stop &hosting";
            else _go.Text = _client == null && !_reconnecting ? "Co&nnect" : "Disco&nnect";
            _toggle.Enabled = _client != null && !_client.ListenOnly;
            _restart.Enabled = _updateRemote.Enabled = _remoteInfo.Enabled = _getFiles.Enabled = _client != null && !_client.ListenOnly;
            EnsureServiceLink();
            SaveResumeState();
        }

        private ServiceLink.Client? _serviceLink;

        /// <summary>
        /// While the service hosts, this window still does the clipboard and sends files, through
        /// the service's agent: the same buttons and Files line as when this window hosts itself.
        /// </summary>
        private void EnsureServiceLink()
        {
            bool want = HostMode && _service.Checked;
            if (want && _serviceLink == null)
            {
                var link = new ServiceLink.Client { ReceiveFolder = _settings.ReceiveFolder };
                link.TextArrived += text => Later(() => ClipboardArrived(text));
                link.FilesArrived += paths => Later(() => ClipboardFilesArrived(paths));
                link.TransferProgress += t => Later(() => ShowTransfer(t));
                _serviceLink = link;
            }
            else if (!want && _serviceLink != null)
            {
                _serviceLink.Dispose();
                _serviceLink = null;
            }
        }

        /// <summary>
        /// Remembers at once whether this PC is hosting or connected, so an update or
        /// a crash comes back the same way. Not before the window is shown: until
        /// then the state from last time has not been used yet.
        /// </summary>
        private void SaveResumeState()
        {
            if (!_shown) return;
            string state = _host != null ? "host" : _client != null || _reconnecting || _connecting ? "connect" : "";
            if (state == _settings.ResumeState) return;
            _settings.ResumeState = state;
            try { _settings.Save(); } catch { }
        }

        private bool _shown;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _handle = Handle;
        }

        // ---- Send clipboard and Send files ----
        // Nothing is shared by itself. Send clipboard sends what is on this clipboard (text, or
        // copied files and folders) to the other PC's clipboard, ready for Control V there.
        // Send files sends chosen files to Downloads, TailRemote on the other PC.
        //
        // Clipboard work runs on a thread of its own: Windows' clipboard calls wait whenever
        // another program has it open, and on the window's thread that froze TailRemote.

        private static readonly System.Collections.Concurrent.BlockingCollection<Action> ClipboardJobs = StartClipboardThread();

        private static System.Collections.Concurrent.BlockingCollection<Action> StartClipboardThread()
        {
            var jobs = new System.Collections.Concurrent.BlockingCollection<Action>(64);
            var t = new System.Threading.Thread(() =>
            {
                foreach (var job in jobs.GetConsumingEnumerable())
                {
                    try { job(); }
                    catch (Exception e)
                    {
                        // Speech.Speak and DiagLog.Write are both thread-safe from any thread: a
                        // failed clipboard send must not vanish silently.
                        DiagLog.Write("clipboard job: " + e);
                        Speech.Speak("Could not finish the clipboard action: " + e.Message);
                    }
                }
            })
                { IsBackground = true, Name = "TailRemote clipboard" };
            t.SetApartmentState(System.Threading.ApartmentState.STA); // the clipboard needs it
            t.Start();
            return jobs;
        }

        private bool CanSend(out Client? client, out Host? host) => CanSend(out client, out host, out _);

        private bool CanSend(out Client? client, out Host? host, out ServiceLink.Client? link)
        {
            client = _client;
            host = _host;
            link = HostMode && _service.Checked ? _serviceLink : null;
            if (link != null)
            {
                if (!link.Connected)
                {
                    // An older service has no link to this window yet: it updates itself to this version.
                    if (ServiceHost.InstalledVersion() is Version v && v < Updater.Current)
                    {
                        OfferServiceUpdate();
                        Say("The TailRemote service is still on version " + v + " and is updating itself to " + Updater.Current + ". Try again in a minute.");
                    }
                    else Say("The TailRemote service is not answering. Check that it is running, or press Apply settings to the service.");
                    return false;
                }
                if (!link.HasController) { Say("No one is controlling this PC, so there is no one to send to."); return false; }
                return true;
            }
            if (client == null && host == null) { Say("Connect, or start hosting, first."); return false; }
            if (client?.ListenOnly == true) { Say("Listeners cannot send anything."); return false; }
            if (host != null && !host.HasController) { Say("No one is controlling this PC, so there is no one to send to."); return false; }
            return true;
        }

        private void SendClipboard()
        {
            Doing("sending the clipboard");
            if (!CanSend(out _, out _)) return;
            ClipboardJobs.TryAdd(() =>
            {
                string[]? files = null;
                string? text = null;
                bool inaccessible = false;
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (Clipboard.ContainsFileDropList())
                        {
                            var list = Clipboard.GetFileDropList();
                            files = new string[list.Count];
                            list.CopyTo(files, 0);
                        }
                        else if (Clipboard.ContainsText()) text = Clipboard.GetText();
                        inaccessible = false;
                        break;
                    }
                    catch { inaccessible = true; System.Threading.Thread.Sleep(50); } // another program has it open
                }
                if (files is { Length: > 0 })
                {
                    Later(() =>
                    {
                        if (!CanSend(out var client, out var host, out var link)) return; // the connection may have closed meanwhile
                        client?.SendClipboardFiles(files);
                        host?.SendClipboardFiles(files);
                        link?.SendClipboardFiles(files);
                        Tone(Sounds.Tone.ClipboardSent); // the Files line says the rest, without speaking
                    });
                }
                else if (!string.IsNullOrEmpty(text))
                {
                    if ((long)text.Length * 3 > FileChannel.MaxText) { Later(() => Say("That is too much text to send: over 512 megabytes.")); return; }
                    Later(() =>
                    {
                        if (!CanSend(out var client, out var host, out var link)) return; // the connection may have closed meanwhile
                        client?.SendClipboard(text);
                        host?.SendClipboard(text);
                        link?.SendClipboard(text);
                        Tone(Sounds.Tone.ClipboardSent);
                    });
                }
                else if (inaccessible) Later(() => Say("Could not read the clipboard: try again."));
                else Later(() => Say("The clipboard is empty: copy something first."));
            });
        }

        private bool _picking;

        /// <summary>
        /// The Windows file picker, on a thread of its own: it looks through every drive and
        /// add-on as it opens, and a slow one froze TailRemote when it ran on the window's
        /// thread (1.8.4, Brock). Null: nothing chosen.
        /// </summary>
        private static System.Threading.Tasks.Task<string[]?> PickFiles(bool folder)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<string[]?>();
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    if (folder)
                    {
                        using var pick = new FolderBrowserDialog { Description = "Choose a folder to send to the other PC, with everything in it", UseDescriptionForTitle = true };
                        done.TrySetResult(pick.ShowDialog() == DialogResult.OK ? new[] { pick.SelectedPath } : null);
                        return;
                    }
                    using var files = new OpenFileDialog { Multiselect = true, Title = "Choose files to send to the other PC" };
                    done.TrySetResult(files.ShowDialog() == DialogResult.OK ? files.FileNames : null);
                }
                catch (Exception e) { done.TrySetException(e); }
            }) { IsBackground = true, Name = "TailRemote file picker" };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            return done.Task;
        }

        /// <summary>Received files go where the setting says (Downloads\TailRemote when it is empty), here and in the service.</summary>
        private void ApplyReceiveFolder()
        {
            string folder = _settings.ReceiveFolder;
            FileChannel.DownloadsOverride = folder.Length > 0 ? folder : null;
            _pickReceiveFolder.Text = "Pic&k where received files go (now " + FileChannel.Downloads.Replace("&", "&&") + ")...";
            if (_serviceLink != null) _serviceLink.ReceiveFolder = folder;
        }

        private bool _pickingFolder;

        /// <summary>Pick where received files go: a folder picker on its own thread, like Send a folder.</summary>
        private async void PickReceiveFolder()
        {
            Doing("picking where received files go");
            if (_pickingFolder) return;
            _pickingFolder = true;
            try
            {
                var done = new System.Threading.Tasks.TaskCompletionSource<string?>();
                var t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        using var pick = new FolderBrowserDialog
                        {
                            Description = "Choose where files from the other PC are saved",
                            UseDescriptionForTitle = true,
                            SelectedPath = FileChannel.Downloads,
                        };
                        done.TrySetResult(pick.ShowDialog() == DialogResult.OK ? pick.SelectedPath : null);
                    }
                    catch (Exception e) { done.TrySetException(e); }
                }) { IsBackground = true, Name = "TailRemote folder picker" };
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
                string? chosen = await done.Task;
                if (chosen == null) return;
                _settings.ReceiveFolder = chosen;
                SaveSettings();
                ApplyReceiveFolder();
                Say("Received files will go to " + chosen + ".");
            }
            catch (Exception e) { Say("Could not choose the folder: " + e.Message); }
            finally { _pickingFolder = false; }
        }

        private async void SendFiles(bool folder)
        {
            Doing(folder ? "sending a folder" : "sending files");
            if (!CanSend(out _, out _)) return;
            if (_picking) { Say("The file picker is already open."); return; }
            try
            {
                _picking = true;
                string[]? paths;
                try { paths = await PickFiles(folder); }
                finally { _picking = false; }
                if (paths == null || paths.Length == 0) return;
                if (!CanSend(out var client, out var host, out var link)) return; // the connection may have closed meanwhile
                client?.SendFiles(paths);
                host?.SendFiles(paths);
                link?.SendFiles(paths);
            }
            catch (Exception e)
            {
                DiagLog.Write("send files: " + e);
                Say("Could not send the files: " + e.Message);
            }
        }

        /// <summary>Text the other PC sent with Send clipboard: onto this clipboard.</summary>
        private void ClipboardArrived(string text)
        {
            Doing("receiving the clipboard");
            if (text.Length == 0) return;
            Tone(Sounds.Tone.ClipboardReceived);
            ClipboardJobs.TryAdd(() => SetClipboard(() => Clipboard.SetDataObject(text, true, 2, 50)));
        }

        /// <summary>Files the other PC sent with Send clipboard, in the holding folder: onto this clipboard, so Control V pastes them.</summary>
        private void ClipboardFilesArrived(string[] paths)
        {
            if (paths.Length == 0) return;
            Tone(Sounds.Tone.ClipboardReceived);
            ClipboardJobs.TryAdd(() =>
            {
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(paths);
                var data = new DataObject();
                data.SetFileDropList(list);
                // Paste MOVES them out of the holding folder: on the same drive that is an instant rename,
                // so the files never take up their space twice.
                data.SetData("Preferred DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(2)));
                SetClipboard(() => Clipboard.SetDataObject(data, true, 2, 50));
            });
        }

        private static void SetClipboard(Action set)
        {
            for (int i = 0; i < 5; i++)
            {
                try { set(); return; }
                catch { System.Threading.Thread.Sleep(100); }
            }
        }

        // ---- Which output the host sends ----

        private void CaptureChanged()
        {
            Doing("changing the capture device");
            if (_fillingCapture) return;
            SaveSettings();
            string id = _settings.CaptureDevice;
            _host?.SetCaptureDevice(id.Length == 0 ? null : id);
            if (_service.Checked) Say("Press Apply settings to the service to use this there too.");
            else if (_host != null) Say("Now sending " + _devices[Math.Max(0, _captureFrom.SelectedIndex)].Name + ".");
        }

        // ---- The remote PC: update it, its info, get files from it, switch with Control 1 to 9 ----

        private bool CanUseRemote(out Client c)
        {
            c = _client!;
            if (_client == null || _client.ListenOnly) { Say(_client == null ? "Connect first." : "Listeners cannot do that."); return false; }
            if (!_client.CanRemoteTools) { Say("The remote PC's TailRemote is too old for that. Update it once with Check for updates on that PC."); return false; }
            return true;
        }

        private void UpdateRemote()
        {
            if (!CanUseRemote(out var c)) return;
            if (c.HostVersion is Version v && v >= Updater.Current)
            {
                Say("The remote PC already has TailRemote " + v + (v == Updater.Current ? ", the same as this PC." : ", newer than this PC's " + Updater.Current + "."));
                return;
            }
            c.RequestUpdate(Updater.Current);
            Say("Asked the remote PC to update to TailRemote " + Updater.Current + ".");
        }

        private async void RemoteInfo()
        {
            if (!CanUseRemote(out var c)) return;
            Doing("asking for remote PC info");
            string text;
            try { text = await c.RequestInfoAsync(); }
            catch { Say("The remote PC did not answer."); return; }
            if (_client != c) return;
            using var f = new RemoteInfoForm(c, text, Say);
            f.ShowDialog(this);
        }

        private void GetFiles()
        {
            if (!CanUseRemote(out var c)) return;
            Doing("getting files from the remote PC");
            using var f = new RemoteFilesForm(c, Say);
            f.ShowDialog(this);
        }

        /// <summary>
        /// Once a second while connected: says once if the remote PC has an older TailRemote, and,
        /// when turned on, when the sound quality goes down or back up.
        /// </summary>
        private void WatchConnection(Client c)
        {
            if (_seenClient != c)
            {
                if (c.HostVersion is not Version hv) return; // its features have not come yet
                _seenClient = c;
                _qualitySaid = c.AudioQuality;
                _updateRemote.Text = "Update the remote P&C to this PC's version (it has " + hv + ", this PC has " + Updater.Current + ")";
                if (hv < Updater.Current && !c.ListenOnly)
                    Speak("The remote PC has TailRemote " + hv + ", older than this PC's " + Updater.Current + ". File, Update the remote PC brings it up to date.");
                return;
            }
            int q = c.AudioQuality;
            if (q == _qualitySaid) return;
            bool lower = q > _qualitySaid; // higher steps are lower bitrates
            _qualitySaid = q;
            if (_announceQuality.Checked)
                Speak(q == 0 ? "Sound back to full quality." : "Sound " + (lower ? "lowered" : "raised") + " to " + Protocol.OpusSteps[q].Kbps + " kilobits.");
        }

        /// <summary>Control 1 to 9: connect to that saved PC, leaving the one connected now.</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && !e.Alt && !e.Shift && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
            {
                e.Handled = e.SuppressKeyPress = true;
                QuickConnect(e.KeyCode - Keys.D0);
            }
        }

        private void QuickConnect(int n)
        {
            if (HostMode) { Say("Control 1 to 9 connect to saved PCs, in Control another PC mode."); return; }
            if (n > _settings.SavedPcs.Count)
            {
                Say(_settings.SavedPcs.Count == 0 ? "There are no saved PCs yet. Use File, Save this PC." : "There is no saved PC " + n + ". There are " + _settings.SavedPcs.Count + ".");
                return;
            }
            if (_connecting && !_reconnecting) { Say("Still connecting. Try again in a moment."); return; }
            var pc = _settings.SavedPcs[n - 1];
            if (_client != null && _saved.SelectedIndex == n) { Say("Already connected to " + pc + "."); return; }
            if (_client != null) Disconnect("Leaving " + _address.Text.Trim() + ".", byUser: true);
            else if (_reconnecting) { _attempt++; _reconnecting = false; _retryTimer.Stop(); }
            _saved.SelectedIndex = n;
            UseSaved();
            Connect(quiet: false);
        }

        // ---- Restart ----

        private void RestartRemote()
        {
            var c = _client;
            if (c == null || c.ListenOnly) { Say("Connect first."); return; }
            if (!c.CanRestart) { Say("The remote PC's TailRemote is too old to restart it. Update it first."); return; }
            var answer = MessageBox.Show(this,
                "Restart the remote PC now? Programs there close as in a normal restart, and may ask to save first." + Environment.NewLine + Environment.NewLine +
                "TailRemote reconnects when it is back. That only happens if the remote PC starts hosting by itself: turn on Start hosting when Windows starts there, or the service.",
                "Restart remote PC", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            _expectRestart = true;
            c.RestartHost();
            Say("Asked the remote PC to restart.");
        }

        // ---- The Files line: how a transfer is going ----

        private const string TransferIdle = "Nothing being sent or received. Use Send the clipboard or Send files.";
        private FileChannel.Transfer? _transferShown;
        private System.Windows.Forms.Timer? _idleTimer;

        /// <summary>A finished line stays 15 seconds, then the Files line goes back to saying nothing is going.</summary>
        private void IdleSoon()
        {
            if (_idleTimer == null)
            {
                _idleTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
                _idleTimer.Tick += (_, _) => { _idleTimer!.Stop(); if (_transferShown == null) _transferStatus.Text = TransferIdle; };
            }
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        /// <summary>
        /// While several files move, the Files line opens a list with a line per file: its name,
        /// how much has moved, and the percent. Nothing to open when one or none is moving.
        /// </summary>
        private void ShowFileBars(System.Collections.Generic.IReadOnlyList<(string Name, long Done, long Total)> files)
        {
            var items = _transferStatus.Item.DropDownItems;
            int n = Math.Min(files.Count, 32);
            // One file alone is the Files line itself: no list to open.
            if (n < 2) n = 0;
            while (items.Count > n) items.RemoveAt(items.Count - 1);
            while (items.Count < n) items.Add(new ToolStripMenuItem { Tag = Menus.KeepOpenTag });
            for (int i = 0; i < n; i++)
            {
                var (name, done, total) = files[i];
                int percent = total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);
                string text = (name + ": " + FileChannel.Size(done) + " of " + FileChannel.Size(total) + ", " + percent + " percent").Replace("&", "&&");
                if (items[i].Text != text) items[i].Text = text;
            }
        }

        /// <summary>The connection closed: whatever was going has stopped, and the Files line says so.</summary>
        private void TransferEnded(string why)
        {
            if (_transferShown == null) return;
            _transferShown = null;
            _going.Clear();
            _ended.Clear();
            ShowFileBars(Array.Empty<(string, long, long)>());
            _transferStatus.Text = why;
            _transferStop.Enabled = false;
            IdleSoon();
        }

        // Every transfer going right now (several when this PC hosts many controlling PCs), and
        // those that ended while others still went: the Files line sums them up once all are done.
        private readonly System.Collections.Generic.Dictionary<int, FileChannel.Transfer> _going = new();
        private readonly System.Collections.Generic.List<FileChannel.Transfer> _ended = new();

        /// <summary>How transfers are going, either way: the Files line and bars. Never spoken, so a lot of them cannot flood NVDA; the sounds say they started and ended.</summary>
        private void ShowTransfer(FileChannel.Transfer t)
        {
            if (t.Finished) { _going.Remove(t.Serial); if (!_ended.Contains(t)) _ended.Add(t); }
            else _going[t.Serial] = t;
            _transferShown = _going.Values.FirstOrDefault();
            if (_going.Count == 0)
            {
                // All ended: one line for all of them.
                var ends = _ended.ToList();
                _ended.Clear();
                if (ends.Count == 0) return;
                ShowFileBars(Array.Empty<(string, long, long)>());
                _transferStatus.Text = Summary(ends);
                IdleSoon();
                _transferStop.Enabled = false;
                // The clipboard's own sounds play when it is sent and when it arrives; file sounds are
                // for Send files only; and stopping (or replacing) on purpose is never an error.
                if (ends.Any(e => e.Failed && !e.Cancelled)) Tone(Sounds.Tone.Error);
                else if (ends.Any(e => !e.Failed && !e.Clipboard)) Tone(ends[0].Outgoing ? Sounds.Tone.FileSent : Sounds.Tone.FileReceived);
                return;
            }
            var going = _going.Values.ToList();
            if (going.Count == 1 && _ended.Count == 0)
            {
                var g = going[0];
                string left = g.Left is TimeSpan l ? ", about " + Duration(l) + " left" : "";
                _transferStatus.Text = (g.Outgoing ? "Sending " : "Receiving ") + g.What + ": " + FileChannel.Size(g.Done) + " of " + FileChannel.Size(g.Total) +
                    (g.Waiting ? ", waiting for the connection to come back; it carries on from here." : ", " + FileChannel.Speed(g.BytesPerSecond) + left + ".");
                ShowFileBars(g.Files);
            }
            else
            {
                // Several at once (to or from several PCs): the totals, and one bar per PC.
                var all = going.Concat(_ended).ToList();
                long done = all.Sum(x => x.Finished ? x.Total : x.Done), total = all.Sum(x => x.Total);
                bool outgoing = going.All(x => x.Outgoing), incoming = going.All(x => !x.Outgoing);
                string names = string.Join(", ", going.Select(x => x.What).Distinct().Take(3));
                _transferStatus.Text = (outgoing ? "Sending " + names + " to " + all.Count + " PCs" : incoming ? "Receiving " + names + " from " + all.Count + " PCs" : "Moving " + all.Count + " transfers") +
                    (_ended.Count > 0 ? ", " + _ended.Count + " finished" : "") + ": " + FileChannel.Size(done) + " of " + FileChannel.Size(total) + ", " + FileChannel.Speed(going.Sum(x => x.BytesPerSecond)) +
                    // They go side by side, so the slowest one says when all are done.
                    (going.Select(x => x.Left).Where(x => x != null).Max() is TimeSpan most ? ", about " + Duration(most) + " left" : "") + ".";
                ShowFileBars(going.Select(x => ((x.Peer.Length > 0 ? x.Peer + ", " : "") + x.What, x.Done, x.Total)).ToList());
            }
            _transferStop.Enabled = true;
        }

        /// <summary>
        /// How long, in words, with the two largest parts that matter: "2 days 3 hours",
        /// "1 hour 20 minutes", "4 minutes 10 seconds", "12 seconds".
        /// </summary>
        internal static string Duration(TimeSpan t)
        {
            static string Part(long n, string unit) => n + " " + unit + (n == 1 ? "" : "s");
            long seconds = Math.Max(1, (long)Math.Round(t.TotalSeconds));
            long days = seconds / 86_400, hours = seconds / 3600 % 24, minutes = seconds / 60 % 60, secs = seconds % 60;
            if (days > 0) return Part(days, "day") + (hours > 0 ? " " + Part(hours, "hour") : "");
            if (hours > 0) return Part(hours, "hour") + (minutes > 0 ? " " + Part(minutes, "minute") : "");
            if (minutes > 0) return Part(minutes, "minute") + (secs > 0 ? " " + Part(secs, "second") : "");
            return Part(secs, "second");
        }

        /// <summary>One line for transfers that all ended: the one result, or how many PCs got it and what went wrong.</summary>
        private static string Summary(System.Collections.Generic.List<FileChannel.Transfer> ends)
        {
            if (ends.Count == 1) return ends[0].Result ?? TransferIdle;
            var good = ends.Where(e => !e.Failed).ToList();
            var bad = ends.Where(e => e.Failed).ToList();
            string what = string.Join(", ", ends.Select(e => e.What).Distinct().Take(3));
            string head = ends.All(e => e.Outgoing)
                ? (bad.Count == 0 ? "Sent " + what + " to all " + ends.Count + " PCs" : "Sent " + what + " to " + good.Count + " of " + ends.Count + " PCs")
                : (bad.Count == 0 ? "Received " + what + " from all " + ends.Count + " PCs" : "Received from " + good.Count + " of " + ends.Count + " PCs");
            if (good.Count > 0) head += ", " + FileChannel.Size(good.Sum(e => e.Total)) + " in all";
            if (bad.Count > 0) head += (bad[0].Outgoing ? ". Not sent to: " : ". Did not arrive from: ") + string.Join("; ", bad.Take(5).Select(e => (e.Peer.Length > 0 ? e.Peer + ": " : "") + e.Result));
            return head + ".";
        }

        // ---- Saved PCs ----

        private bool _filling;

        private void FillSaved()
        {
            _filling = true;
            _saved.Clear();
            _saved.Add("None: type the address");
            for (int n = 0; n < _settings.SavedPcs.Count; n++)
            {
                var item = _saved.Add(_settings.SavedPcs[n].ToString());
                if (n < 9) item.ShortcutKeyDisplayString = "Ctrl+" + (n + 1); // Control 1 to 9 connect to it (OnKeyDown)
            }
            int i = _settings.SavedPcs.FindIndex(p => p.Address == _settings.Address && p.Port == _settings.Port);
            _saved.SelectedIndex = i + 1;
            _filling = false;
            _forgetPc.Enabled = _saved.SelectedIndex > 0;
        }

        private SavedPc? ChosenSaved() => _saved.SelectedIndex > 0 && _saved.SelectedIndex <= _settings.SavedPcs.Count ? _settings.SavedPcs[_saved.SelectedIndex - 1] : null;

        private void UseSaved()
        {
            _forgetPc.Enabled = _saved.SelectedIndex > 0;
            if (_filling || ChosenSaved() is not SavedPc pc) return;
            _address.Text = pc.Address;
            _port.Text = pc.Port.ToString();
            _password.Text = Settings.Unprotect(pc.PasswordEnc);
            SaveSettings();
        }

        private void SavePc()
        {
            string address = _address.Text.Trim();
            if (address.Length == 0) { Later(() => _address.Ask(this, "Type the address first.")); return; }
            if (!CheckPassword() || !CheckPort(out int port)) return;
            var pc = _settings.SavedPcs.Find(p => string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase) && p.Port == port);
            bool isNew = pc == null;
            pc ??= new SavedPc { Address = address, Port = port };
            pc.PasswordEnc = Settings.Protect(_password.Text);
            if (isNew) _settings.SavedPcs.Add(pc);
            SaveSettings();
            FillSaved();
            Say(isNew ? "Saved " + pc + "." : "Updated " + pc + ".");
        }

        private void ForgetPc()
        {
            if (ChosenSaved() is not SavedPc pc) return;
            _settings.SavedPcs.Remove(pc);
            _settings.Save();
            FillSaved();
            Say("Forgot " + pc + ".");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _shown = true;
            StartWatchdog();
            _keys = new KeyCapture(() => _handle);
            _keys.ModeChanged += remote => Later(() => ModeChanged(remote));
            _keys.NotConnected += () => Later(() => Say("Not connected."));
            _titleTimer.Start();
            if (_updated) Say("Updated to version " + Updater.Current + ".");
            OfferServiceUpdate();
            if (_autoHost && _service.Checked) Log("The TailRemote service is hosting this PC, so this window does not.");
            else if (_autoHost)
            {
                _mode.SelectedIndex = 1;
                Go();
                BeginInvoke(() => HideToTray(announce: false)); // started with Windows: out of the way
            }
            else if (_autoConnect && !HostMode && _address.Text.Trim().Length > 0) Go();
        }

        private void ModeChanged(bool remote)
        {
            _toggle.Text = remote ? "Control &this PC" : "Control &remote PC";
            if (_quietModeChange) _quietModeChange = false; // the reconnect message says it
            else { Say(remote ? "Controlling remote PC." : "Controlling this PC."); Tone(remote ? Sounds.Tone.ControlRemote : Sounds.Tone.ControlLocal); }
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string t;
            if (_client != null)
            {
                t = "TailRemote - " + (_client.ListenOnly ? "listening" : _keys?.Remote == true ? "controlling remote" : "connected");
                if (_client.LastPingMs >= 0) t += ", ping " + _client.LastPingMs + " ms";
                int audio = _client.AudioDelayMs;
                if (audio >= 0) t += ", audio " + (audio + _client.PingForAudio / 2) + " ms";
                if (_client.ReducedSound is string reduced) t += ", sound at " + reduced;
                WatchConnection(_client);
            }
            else if (_reconnecting) t = "TailRemote - reconnecting";
            else if ((_host?.Connected ?? (_serviceLink?.Connected == true ? _serviceLink.Counts : null)) is (int controlling, int listening))
            {
                t = "TailRemote - hosting" + (_host == null ? " as the service" : "");
                // More than one PC: how many, and what they are doing.
                if (controlling + listening > 1)
                    t += ", " + (controlling + listening) + " PCs connected (" + controlling + " controlling" + (listening > 0 ? ", " + listening + " listening" : "") + ")";
            }
            else t = "TailRemote";
            if (Text != t) Text = t;
            // The Streaming line, read with Tab: what is coming in right now.
            string st = _client == null ? (_reconnecting ? "Not connected: trying again every second" : "Not connected")
                : "Streaming at " + Protocol.OpusSteps[_client.AudioQuality].Kbps + " kilobits per second" +
                  (_client.LockedStep >= 0 ? ", locked" : ", variable") +
                  (_client.AudioDelayMs >= 0 ? ", audio delay " + (_client.AudioDelayMs + _client.PingForAudio / 2) + " ms" : "") +
                  (_client.UdpBlocked ? ". NO SOUND: UDP port " + _client.Port + " is blocked between the PCs. On the remote PC, open it with Port editor, or check its firewall" : "");
            if (_streaming.Text != st) _streaming.Text = st;
        }

        private void SaveSettings()
        {
            _settings.HostMode = HostMode;
            _settings.Address = _address.Text.Trim();
            if (int.TryParse(_port.Text.Trim(), out int port)) _settings.Port = port;
            _settings.Password = _password.Text;
            _settings.OutputDevice = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            _settings.CaptureDevice = _devices[Math.Max(0, _captureFrom.SelectedIndex)].Id;
            _settings.ListenPassword = _listenPassword.Text;
            _settings.Logging = _logging.Checked;
            _settings.CatchUpBySpeed = _speedUp.Checked;
            _settings.AnnounceQuality = _announceQuality.Checked;
            _settings.SoundQuality = _quality.SelectedIndex - 1;
            _settings.Save();
        }

        // ---- Hang watchdog ----
        // Once a second a background thread asks the window to answer. If it has not for
        // 3 seconds, what it was doing goes to TailRemote-crash.txt, so a freeze can be found.
        private static volatile string _doing = "starting";
        private long _answeredAt = Environment.TickCount64;

        /// <summary>Notes what the window is doing, for the hang watchdog.</summary>
        private static void Doing(string what) => _doing = what;

        private void StartWatchdog()
        {
            new System.Threading.Thread(() =>
            {
                bool reported = false;
                while (!IsDisposed)
                {
                    System.Threading.Thread.Sleep(1000);
                    // Answering clears the label too: a report then always names what was really going on.
                    try { if (IsHandleCreated) BeginInvoke(() => { _answeredAt = Environment.TickCount64; _doing = "nothing in particular"; }); } catch { return; }
                    long stuck = Environment.TickCount64 - System.Threading.Interlocked.Read(ref _answeredAt);
                    if (stuck > 3000 && !reported)
                    {
                        reported = true;
                        Program.CrashNote("the window stopped responding for " + stuck / 1000 + " seconds while " + _doing);
                    }
                    else if (stuck < 1500) reported = false;
                }
            }) { IsBackground = true, Name = "TailRemote watchdog" }.Start();
        }

        private void Go()
        {
            Doing("pressing " + (_go.Text ?? "").Replace("&", ""));
            if (_connecting && !_reconnecting) return; // a second press while connecting would start a second connection
            SaveSettings();
            if (HostMode && _service.Checked) ApplyService();
            else if (HostMode) { if (_host == null) StartHost(); else StopHost(); }
            else if (_reconnecting) StopReconnecting("Disconnected.");
            else if (_client == null) Connect(quiet: false);
            else Disconnect("Disconnected.", byUser: true);
        }

        private async void StartHost()
        {
            Doing("starting hosting");
            if (ServiceHost.IsInstalled()) { Say("The TailRemote service is hosting this PC. Use Apply settings to the service instead."); return; }
            if (!CheckPassword()) return;
            if (!CheckListenPassword()) return;
            string listen = _listenPassword.Text;
            if (!CheckPort(out int port)) return;
            PortEditorForm.Remember(_settings, port);
            try
            {
                _host = new Host(port, _password.Text, listen, msg => Later(() => Log(msg)),
                    _settings.CaptureDevice.Length == 0 ? null : _settings.CaptureDevice);
                _host.ClipboardReceived += text => Later(() => ClipboardArrived(text));
                _host.ClipboardFilesReceived += paths => Later(() => ClipboardFilesArrived(paths));
                _host.TransferProgress += t => Later(() => ShowTransfer(t));
                _host.UpdateRequested += (version, reply) => Later(() => RemoteUpdate(version, reply));
                Say("Hosting on port " + port + ". Waiting for a connection." + (listen.Length > 0 ? " Listening with the listen-only password is on." : ""));
                // Audio settings and device names are never changed by themselves (they are other
                // programs' too): only Set up audio device does that, when pressed.
                if (Wasapi.OutputDevices().Count == 0)
                    Say("This PC has no sound output, so the other PC will hear nothing. Press Set up audio device if you want TailRemote to add one.");
                if (!Startup.IsElevated()) Log("Not running as administrator, so keys cannot reach administrator windows. Start hosting when Windows starts runs it as administrator.");
            }
            catch (Exception e) { Say("Could not start hosting: " + e.Message); }
            UpdateButtons();
            UpdateTitle();
            if (_host != null) await OfferToOpenPort(port);
        }

        /// <summary>If Windows Firewall would keep other PCs out, open the port (asking first unless already administrator).</summary>
        private async System.Threading.Tasks.Task OfferToOpenPort(int port)
        {
            if (await System.Threading.Tasks.Task.Run(() => Firewall.IsOpen(port))) return;
            if (Startup.IsElevated())
            {
                if (await System.Threading.Tasks.Task.Run(() => Firewall.Apply(true, port)) == 0) Log("Opened port " + port + " in Windows Firewall.");
                return;
            }
            var answer = MessageBox.Show(this, "Port " + port + " is not open in Windows Firewall, so other PCs may not be able to connect. Open it now? Windows asks for administrator permission. You can close it later in Port editor.",
                "Open port " + port, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            Say(await Firewall.SetAsync(port, true) ? "Port " + port + " is open." : "Port " + port + " was not opened: administrator permission was not given.");
        }

        public const int MinPasswordLength = 5;

        /// <summary>Refuses a password shorter than 5 characters.</summary>
        private bool CheckPassword()
        {
            int n = _password.Text.Length;
            if (n >= MinPasswordLength) return true;
            string why = n == 0 ? "Type a password first." : "The password needs at least " + MinPasswordLength + " characters. It has " + n + ".";
            Log(why);
            Later(() => _password.Ask(this, why));
            return false;
        }

        /// <summary>
        /// Refuses a listen-only password shorter than 5 characters, or the same as the main
        /// password. Empty is fine: it means no listen-only password. Shared by StartHost and the
        /// service paths (ServiceChanged/ApplyService), so the service cannot be enabled or applied
        /// with a listen-only password that hosting itself would refuse.
        /// </summary>
        private bool CheckListenPassword()
        {
            string listen = _listenPassword.Text;
            if (listen.Length == 0 || (listen.Length >= MinPasswordLength && listen != _password.Text)) return true;
            string why = listen == _password.Text
                ? "The listen-only password must be different from the main password, or empty."
                : "The listen-only password needs at least " + MinPasswordLength + " characters, or leave it empty.";
            Log(why);
            Later(() => _listenPassword.Ask(this, why));
            return false;
        }

        /// <summary>Refuses a port that is not a number, or that would clash with something else.</summary>
        private bool CheckPort(out int port)
        {
            string? problem = Protocol.PortProblem(_port.Text.Trim(), out port);
            if (problem == null) return true;
            Log(problem);
            Later(() => _port.Ask(this, problem));
            return false;
        }

        private void StopHost()
        {
            TransferEnded("Stopped: hosting stopped.");
            Doing("stopping hosting");
            _host?.Leave(Protocol.LeavingStopped);
            _host?.Dispose();
            _host = null;
            Say("Stopped hosting.");
            UpdateButtons();
            UpdateTitle();
        }

        /// <summary>Connects with the saved settings. Quiet attempts are reconnects: only success is spoken.</summary>
        private async void Connect(bool quiet)
        {
            string address = _address.Text.Trim();
            if (address.Length == 0) { Later(() => _address.Ask(this, "Type the address first.")); return; }
            if (!CheckPassword()) return;
            if (!CheckPort(out int port)) return;
            _connecting = true;
            int attempt = ++_attempt;
            if (!quiet) { _go.Enabled = false; Say("Connecting to " + address + "."); }
            string pw = _password.Text;
            string device = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            try
            {
                if (_player == null || _playerDevice != device)
                {
                    _player?.Dispose();
                    _player = new Player(device, msg => Later(() => Say(msg)));
                    _playerDevice = device;
                }
                _player.SpeedUp = _speedUp.Checked;
                var player = _player;
                int locked = _quality.SelectedIndex - 1; // from the very first sound
                // Trying again: a short wait for an answer, so a PC that is starting up is caught the
                // moment it hosts, instead of every 8 seconds or more.
                int answerMs = _reconnecting ? 2500 : 8000;
                var c = await System.Threading.Tasks.Task.Run(() =>
                    Client.Connect(address, port, pw, player, msg => Later(() => Say(msg)), locked, answerMs));
                if (attempt != _attempt || HostMode || _client != null)
                {
                    // Stopped, switched to hosting, or already connected while this was under way.
                    c.Dispose();
                    return;
                }
                c.Disconnected += why => Later(() => Disconnect(why, byUser: false));
                c.ClipboardReceived += text => Later(() => ClipboardArrived(text));
                c.ClipboardFilesReceived += paths => Later(() => ClipboardFilesArrived(paths));
                c.TransferProgress += t => Later(() => ShowTransfer(t));
                _client = c;
                if (!c.ListenOnly) _keys?.SetClient(c); // a listener never sends keys
                bool wasReconnecting = _reconnecting;
                _reconnecting = false;
                _retryTimer.Stop();
                string start = _expectUpdate != null ? "The remote PC is back" + (_expectUpdate.Length > 0 ? ", updated to " + _expectUpdate : "") + "."
                    : _expectRestart ? "The remote PC is back." : wasReconnecting ? "Reconnected." : "Connected.";
                _expectRestart = false;
                _expectUpdate = null;
                bool resume = _resumeRemote && !c.ListenOnly;
                _resumeRemote = false;
                if (resume)
                {
                    _quietModeChange = true;
                    _keys?.Toggle(); // straight back to controlling the remote PC
                }
                Tone(Sounds.Tone.Connected);
                Say(c.ListenOnly ? start + " Listen only: you hear the remote PC, but cannot control it."
                    : resume ? start + " Controlling the remote PC."
                    : start + " Press Control Shift Enter to control the remote PC.");
            }
            catch (Exception e) when (attempt == _attempt)
            {
                // Keeps trying every second until Disconnect, except when trying again
                // cannot help (and a wrong password tried again gets this PC blocked).
                // Only a wrong password stops it: the login is checked for damage, so that is real. A
                // "different version" can also be a damaged first message, so it keeps trying.
                bool hopeless = e.Message.StartsWith("Wrong password");
                if (hopeless) { if (_reconnecting) StopReconnecting("Stopped trying: " + e.Message); else Say("Could not connect: " + e.Message); }
                else if (!_reconnecting)
                {
                    _reconnecting = true;
                    _retryTimer.Start();
                    Tone(Sounds.Tone.Error);
                    Say("Could not connect: " + e.Message + " Trying again every second until you press Disconnect.");
                }
            }
            catch { } // an abandoned attempt failing: nobody is waiting for it
            finally
            {
                _connecting = false;
                _go.Enabled = true;
                UpdateButtons();
                UpdateTitle();
            }
        }

        private void Disconnect(string why, bool byUser)
        {
            // Transfers never end with the connection, dropped or disconnected: they wait (the Files
            // line says so) and carry on where they were on the next connection to this PC. Only
            // Stop the file transfer ends them.
            Doing("disconnecting");
            if (_client == null) return;
            // Keys come back to this PC while the connection is down, so nothing is
            // typed into nowhere; remote control resumes by itself on reconnecting.
            _resumeRemote = !byUser && _keys?.Remote == true;
            _quietModeChange = _resumeRemote;
            byte leaving = _client.Leaving;
            string leavingDetail = _client.LeavingDetail;
            _keys?.SetClient(null);
            _client.Dispose();
            _client = null;
            Tone(Sounds.Tone.Disconnected);
            if (!byUser && leaving is Protocol.LeavingRestarting or Protocol.LeavingShutdown) _expectRestart = true; // whoever asked for it
            if (byUser) { _expectRestart = false; _expectUpdate = null; Say(why); }
            else if (leaving == Protocol.LeavingUpdating)
            {
                // The host said it is updating: no error, just wait for it to come back.
                _expectUpdate = leavingDetail;
                _reconnecting = true;
                _retryTimer.Start();
                Say(why + " TailRemote reconnects as soon as it is back.");
            }
            else if (_expectRestart)
            {
                // Expected: keep trying until it is back, however long the restart takes.
                _reconnecting = true;
                _retryTimer.Start();
                Say(leaving == Protocol.LeavingShutdown ? why + " TailRemote reconnects if it comes back."
                    : "The remote PC is restarting. TailRemote reconnects as soon as it is back.");
            }
            else if (leaving == Protocol.LeavingStopped)
            {
                _reconnecting = true;
                _retryTimer.Start();
                Say(why + " Trying again every second until you press Disconnect.");
            }
            else
            {
                // Dropped, or the host restarted after an update: try at once, then every second.
                _reconnecting = true;
                _retryTimer.Start();
                Say(why + " Reconnecting.");
                Connect(quiet: true);
            }
            UpdateButtons();
            UpdateTitle();
        }

        /// <summary>A piano tone for an event, on the controlling PC only (a host's would be sent along with its sound).</summary>
        private void Tone(Sounds.Tone t)
        {
            if (!HostMode) Sounds.Play(t);
        }

        private void StopReconnecting(string why)
        {
            Tone(why.StartsWith("Disconnected") ? Sounds.Tone.Disconnected : Sounds.Tone.Error);
            _resumeRemote = false;
            _expectRestart = false;
            _expectUpdate = null;
            _attempt++;
            _reconnecting = false;
            _retryTimer.Stop();
            Say(why);
            UpdateButtons();
            UpdateTitle();
        }

        private bool _updating;

        private async void CheckForUpdates()
        {
            Doing("checking for updates");
            if (_updating) { Say("TailRemote is already updating."); return; }
            _update.Enabled = false;
            Say("Checking for updates.");
            try
            {
                var r = await Updater.CheckAsync();
                if (r == null) { Say("TailRemote " + Updater.Current + " is the latest version."); return; }
                string notes = r.Notes.Trim().Length > 0 ? r.Notes.Trim() : "No notes.";
                var answer = MessageBox.Show(this, "Version " + r.Version + " is available. You have " + Updater.Current + "." +
                    Environment.NewLine + Environment.NewLine + notes + Environment.NewLine + Environment.NewLine + "Update now? TailRemote restarts and carries on where it was.",
                    "TailRemote update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return;
                Say("Downloading version " + r.Version + ". Everything carries on until it is ready.");
                await InstallUpdate(r);
            }
            catch (Exception e) { Say("Update failed: " + e.Message); }
            finally { if (!IsDisposed) _update.Enabled = true; }
        }

        /// <summary>
        /// The controlling PC chose File, Update the remote PC: update to that version, which must
        /// be GitHub's newest (never anything else), with no questions here. reply goes back to it.
        /// </summary>
        private async void RemoteUpdate(string requested, Action<string> reply)
        {
            Doing("updating for the controlling PC");
            if (!Version.TryParse(requested, out var want)) return;
            if (_updating) { reply("TailRemote on the remote PC is already updating."); return; }
            if (want <= Updater.Current) { reply("The remote PC already has TailRemote " + Updater.Current + "."); return; }
            try
            {
                var r = await Updater.CheckAsync();
                if (r == null || r.Version != want)
                {
                    reply("The remote PC did not update: the newest TailRemote on GitHub is " + (r?.Version ?? Updater.Current) + ", not " + want + ".");
                    return;
                }
                reply("The remote PC is downloading TailRemote " + r.Version + ". Everything carries on until it is ready.");
                Log("The controlling PC asked to update TailRemote to " + r.Version + ".");
                await InstallUpdate(r);
            }
            catch (Exception e) { reply("The remote PC could not update: " + e.Message); }
        }

        /// <summary>
        /// Downloads and swaps in the new version, then closes. Hosting and the connection carry on
        /// until the new copy is in place. On failure, whatever was running is put back, then it throws.
        /// </summary>
        private async System.Threading.Tasks.Task InstallUpdate(Updater.Release r)
        {
            _updating = true;
            string args = "";
            try
            {
                SaveSettings();
                await Updater.InstallAsync(r, () =>
                {
                    // Only now, with the new copy in place: the connected PCs are told it is an
                    // update (they wait for it quietly), then the port and keyboard are let go
                    // before the new copy starts.
                    args = _host != null ? "--host" : _client != null || _reconnecting ? "--connect" : "";
                    _host?.Leave(Protocol.LeavingUpdating, r.Version.ToString());
                    _keys?.SetClient(null);
                    _client?.Dispose(); _client = null;
                    _host?.Dispose(); _host = null;
                    return args;
                });
            }
            catch
            {
                _updating = false;
                if (args == "--host" && _host == null) StartHost();
                else if (args == "--connect" && _client == null) Connect(quiet: false);
                throw;
            }
            _reallyExit = true;
            Close();
        }

        private async void SetUpAudio()
        {
            if (AudioSetup.IsReady()) { Say("The TailRemote audio device is already set up."); return; }
            bool have = AudioSetup.FindCable() != null;
            string plan = "Set up audio device gives this PC an output called TailRemote, so everything this PC plays can be heard on the other PC. It will:" + Environment.NewLine + Environment.NewLine +
                (have ? "" :
                "1. Download VB-Cable, a free virtual audio device by VB-Audio, from vb-audio.com, and check it is genuine." + Environment.NewLine +
                "2. Open VB-Cable's installer. You press Install Driver." + Environment.NewLine) +
                (have ? "1. " : "3. ") + "Name VB-Cable's output TailRemote and make it this PC's default output. Your speakers stop being the default." + Environment.NewLine +
                (have ? "2. " : "4. ") + "Turn off VB-Cable's extra output, CABLE In 16 Ch." + Environment.NewLine + Environment.NewLine +
                "Windows asks for administrator permission first. Remove audio device undoes all of it.";
            if (MessageBox.Show(this, plan, "Set up audio device", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            _audioSetup.Enabled = _audioRemove.Enabled = false;
            bool ok = await AudioSetup.RunElevatedAsync();
            Say(ok ? "The TailRemote audio device is set up." : "Audio device setup did not finish.");
            _audioSetup.Enabled = _audioRemove.Enabled = true;
        }

        private async void RemoveAudio()
        {
            if (!AudioSetup.Installed()) { Say("There is no TailRemote audio device on this PC."); return; }
            var other = AudioSetup.OtherOutput();
            string plan = "Remove audio device will:" + Environment.NewLine + Environment.NewLine +
                "1. " + (other != null ? "Make " + other.Value.Name + " the default output." : "There is no other output on this PC, so it will have no sound output afterwards.") + Environment.NewLine +
                "2. Move programs off the TailRemote device. Any program still using it is cut off." + Environment.NewLine +
                "3. Remove VB-Cable completely, including its driver. If you use VB-Cable for anything else, that stops working too." + Environment.NewLine + Environment.NewLine +
                "Windows asks for administrator permission first.";
            if (MessageBox.Show(this, plan, "Remove audio device", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            _audioSetup.Enabled = _audioRemove.Enabled = false;
            bool ok = await AudioSetup.RemoveElevatedAsync();
            Say(ok ? "The TailRemote audio device is removed." : "Removing the audio device did not finish.");
            _audioSetup.Enabled = _audioRemove.Enabled = true;
        }

        // ---- The Windows service ----

        private async void ServiceChanged()
        {
            bool want = _service.Checked;
            if (want == ServiceHost.IsInstalled()) return;
            string plan = want
                ? "Run TailRemote as a Windows service?" + Environment.NewLine + Environment.NewLine +
                  "The service starts with Windows, before anyone signs in, and has full system access. Whoever knows the TailRemote password can then use the lock screen, sign in, and approve administrator prompts on this PC, and Control Alt End sends Control Alt Delete." + Environment.NewLine + Environment.NewLine +
                  "It uses this window's port and passwords, opens the port in Windows Firewall, and replaces Start hosting when Windows starts. Windows asks for administrator permission."
                : "Remove the TailRemote service? This PC stops hosting until you start hosting here again. Windows asks for administrator permission.";
            if (MessageBox.Show(this, plan, want ? "Run as a Windows service" : "Remove the service", MessageBoxButtons.OKCancel,
                    want ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK
                || (want && (!CheckPassword() || !CheckPort(out _) || !CheckListenPassword())))
            {
                SetServiceBox(!want);
                return;
            }
            if (want && _host != null) StopHost(); // the service takes the port
            SaveSettings();
            Say("Windows asks for administrator permission.");
            bool ok = await ServiceHost.SetAsync(want);
            bool now = ServiceHost.IsInstalled();
            SetServiceBox(now);
            _startup.Checked = Startup.IsEnabled();
            if (ok && now == want)
                Say(want ? "The TailRemote service is running and hosting this PC on port " + _settings.Port + "." : "The TailRemote service is removed.");
            else
                Say((want ? "The service was not set up: " : "The service was not removed: ") + ServiceHost.LastError());
            UpdateMode();
        }

        private async void ApplyService()
        {
            if (!CheckPassword() || !CheckPort(out _) || !CheckListenPassword()) return;
            SaveSettings();
            Say("Giving the service these settings. Windows asks for administrator permission.");
            Say(await ServiceHost.SetAsync(true)
                ? "The service is hosting with these settings on port " + _settings.Port + "."
                : "The service was not changed: " + ServiceHost.LastError());
        }

        private void SetServiceBox(bool on)
        {
            _settingService = true;
            _service.Checked = on;
            _settingService = false;
        }

        /// <summary>
        /// The service never updates on its own. After this copy was updated with
        /// Check for updates, nudge it to follow, to the same version. No questions.
        /// </summary>
        private void OfferServiceUpdate()
        {
            var v = ServiceHost.InstalledVersion();
            if (v == null || !_service.Checked || v >= Updater.Current) return;
            if (ServiceHost.NudgeUpdate()) Log("The TailRemote service is updating itself to version " + Updater.Current + ".");
        }

        private async void StartupChanged()
        {
            bool want = _startup.Checked;
            if (want == Startup.IsEnabled()) return;
            SaveSettings();
            Say("Windows asks for administrator permission.");
            if (await Startup.SetAsync(want)) Say(want ? "TailRemote will start hosting, as administrator, when you sign in." : "TailRemote will no longer start with Windows.");
            else
            {
                Say("That needs administrator permission, and it was not given.");
                _startup.Checked = Startup.IsEnabled();
            }
        }

        private void Log(string line)
        {
            DiagLog.Write("window: " + line); // NVDA speaks it (Say); the log file keeps it when logging is on
        }

        /// <summary>Runs on the window's thread, from any thread; dropped once the window is gone.</summary>
        private void Later(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        /// <summary>Logs a line and speaks it after whatever is being said, without cutting it off.</summary>
        private void Speak(string line)
        {
            Log(line);
            Speech.Speak(line, interrupt: false);
        }

        /// <summary>Logs a line and speaks it.</summary>
        private void Say(string line)
        {
            Log(line);
            Speech.Speak(line);
        }

        private ToolStripMenuItem? _trayExit;

        /// <summary>
        /// While hosting or connected, closing the window only hides it: hosting, or the connection and
        /// its sound, carry on from the notification area.
        /// </summary>
        private void HideToTray(bool announce)
        {
            bool connected = _host == null && (_client != null || _reconnecting);
            if (connected && _keys?.Remote == true)
            {
                // The keyboard comes back to this PC first: nothing is typed into a window you cannot see.
                _quietModeChange = true;
                _keys.Toggle();
            }
            if (_trayExit != null) _trayExit.Text = connected ? "&Disconnect and exit" : "&Stop hosting and exit";
            _tray.Text = connected ? "TailRemote, connected" : "TailRemote, hosting";
            _tray.Visible = true;
            Hide();
            if (announce)
                Speech.Speak(connected
                    ? "TailRemote is still connected, and you still hear the other PC. It is in the notification area: press Windows B to find it. Use its menu to disconnect and exit."
                    : "TailRemote is still hosting, in the notification area. Press Windows B to find it. Use its menu to stop hosting and exit.");
        }

        /// <summary>File, Exit: closes for real, ending hosting or the connection (the X only hides to the tray then).</summary>
        private void ExitForGood()
        {
            _reallyExit = true;
            Close();
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            _tray.Visible = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && (_host != null || _client != null || _reconnecting) && !_reallyExit)
            {
                e.Cancel = true;
                HideToTray(announce: true);
                return;
            }
            _tray.Visible = false;
            _settings.ResumeState = _host != null ? "host" : _client != null || _reconnecting ? "connect" : "";
            SaveSettings();
            _retryTimer.Stop();
            _retryTimer.Dispose();
            _titleTimer.Stop();
            _titleTimer.Dispose();
            _idleTimer?.Stop();
            _idleTimer?.Dispose();
            _keys?.SetClient(null);
            _client?.Dispose();
            _player?.Dispose();
            _host?.Leave(e.CloseReason == CloseReason.WindowsShutDown ? Protocol.LeavingShutdown : Protocol.LeavingStopped);
            _host?.Dispose();
            _keys?.Dispose();
            _serviceLink?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
