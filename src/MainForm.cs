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

        private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _address = new();
        private readonly TextBox _port = new();
        private readonly TextBox _password = new() { UseSystemPasswordChar = true };
        private readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _streaming = new() { ReadOnly = true, TabStop = true, Text = "Not connected" };
        private readonly ComboBox _captureFrom = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private bool _fillingCapture;
        private readonly ComboBox _saved = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button _savePc = new() { Text = "Save t&his PC", AutoSize = true };
        private readonly Button _forgetPc = new() { Text = "&Forget saved PC", AutoSize = true };
        private readonly TextBox _listenPassword = new() { UseSystemPasswordChar = true };
        private readonly Button _sendClipboard = new() { Text = "Send the clip&board", AutoSize = true };
        private readonly Button _sendFiles = new() { Text = "Send f&iles", AutoSize = true };
        private readonly Button _sendFolder = new() { Text = "Send a folder and everything in i&t", AutoSize = true };
        // One bar per file moving right now (named after the file), under the overall bar.
        private readonly FlowLayoutPanel _fileBars = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false };
        // Files: right after Streaming, in the Tab order (a separate tab could only be reached with Control Tab).
        private readonly TextBox _transferStatus = new() { ReadOnly = true, TabStop = true };
        private readonly ProgressBar _transferBar = new() { Height = 22, Maximum = 100, AccessibleName = "File transfer progress" };
        private readonly Button _transferStop = new() { Text = "Stop the file transfer", AutoSize = true, Enabled = false };
        private readonly CheckBox _logging = new() { Text = "Enable lo&gging (writes TailRemote-log.txt next to TailRemote)", AutoSize = true };
        private readonly Button _sounds = new() { Text = "Sounds for connecting, clipboard and fi&les", AutoSize = true };
        private readonly CheckBox _speedUp = new() { Text = "Catch up b&y fast-forwarding the sound at 2x or 4x, same pitch (otherwise it skips ahead)", AutoSize = true, MaximumSize = new Size(560, 0) };
        private readonly CheckBox _startup = new() { Text = "Start &hosting when Windows starts (asks for administrator)", AutoSize = true };
        private readonly CheckBox _service = new() { Text = "Run as a Windows servi&ce: works at the lock screen, sign-in and UAC prompts, and Control Alt End sends Control Alt Delete", AutoSize = true, MaximumSize = new Size(560, 0) };
        private bool _settingService; // set while the checkbox is changed by code, not by the user
        private readonly Button _go = new() { AutoSize = true };
        private readonly Button _toggle = new() { Text = "Control &remote PC (Ctrl+Shift+Enter)", AutoSize = true };
        private readonly Button _update = new() { Text = "Check for &updates", AutoSize = true };
        private readonly Button _audioSetup = new() { Text = "Set up au&dio device", AutoSize = true };
        private readonly Button _audioRemove = new() { Text = "Remove audio de&vice", AutoSize = true };
        private readonly Button _portEditor = new() { Text = "Port &editor", AutoSize = true };
        private readonly Button _restart = new() { Text = "Restart remote PC and reco&nnect", AutoSize = true };
        private bool _expectRestart; // the remote PC was asked to restart: say when it is back

        private readonly Label _addressLabel, _deviceLabel, _savedLabel, _listenLabel, _captureLabel, _qualityLabel, _streamingLabel;
        private readonly FlowLayoutPanel _savedButtons = new() { AutoSize = true };
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
        private readonly Timer _retryTimer = new() { Interval = 3000 }; // Connect keeps trying every 3 seconds until Disconnect
        private bool _resumeRemote;     // was controlling the remote PC when the connection dropped
        private bool _quietModeChange;  // the reconnect message already says it

        public MainForm(bool autoHost, bool autoConnect, bool updated)
        {
            _autoHost = autoHost;
            _autoConnect = autoConnect;
            _updated = updated;
            Text = "TailRemote";
            Font = new Font("Segoe UI", 10f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            StartPosition = FormStartPosition.CenterScreen;

            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));

            _mode.Items.AddRange(new object[] { "Control another PC", "Host: let this PC be controlled" });
            AddRow(table, "&Mode", _mode);
            _savedLabel = AddRow(table, "&Saved PCs", _saved);
            _savedButtons.Controls.Add(_savePc);
            _savedButtons.Controls.Add(_forgetPc);
            table.Controls.Add(_savedButtons); table.SetColumnSpan(_savedButtons, 2);
            _addressLabel = AddRow(table, "&Address (name or IP)", _address);
            AddRow(table, "&Port", _port);
            AddRow(table, "Pass&word", _password);
            _listenLabel = AddRow(table, "Listen-&only password (optional: lets someone hear, not control)", _listenPassword);
            _deviceLabel = AddRow(table, "&Output device", _device);
            _qualityLabel = AddRow(table, "Sound &quality", _quality);
            _streamingLabel = AddRow(table, "Streaming", _streaming);
            AddRow(table, "Files", _transferStatus);
            AddRow(table, "File transfer progress", _transferBar);
            table.Controls.Add(_fileBars); table.SetColumnSpan(_fileBars, 2);
            table.Controls.Add(_transferStop); table.SetColumnSpan(_transferStop, 2);
            _transferBar.Dock = DockStyle.Fill;
            _captureLabel = AddRow(table, "C&apture sound from (the output other PCs hear)", _captureFrom);
            table.Controls.Add(_logging); table.SetColumnSpan(_logging, 2);
            table.Controls.Add(_speedUp); table.SetColumnSpan(_speedUp, 2);
            table.Controls.Add(_sounds); table.SetColumnSpan(_sounds, 2);
            _sounds.Anchor = AnchorStyles.Left;
            table.Controls.Add(_startup); table.SetColumnSpan(_startup, 2);
            table.Controls.Add(_service); table.SetColumnSpan(_service, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(_go);
            buttons.Controls.Add(_sendClipboard);
            buttons.Controls.Add(_sendFiles);
            buttons.Controls.Add(_sendFolder);
            buttons.Controls.Add(_toggle);
            buttons.Controls.Add(_restart);
            buttons.Controls.Add(_audioSetup);
            buttons.Controls.Add(_audioRemove);
            buttons.Controls.Add(_portEditor);
            buttons.Controls.Add(_update);
            table.Controls.Add(buttons); table.SetColumnSpan(buttons, 2);
            _transferStatus.Text = TransferIdle;
            _transferStop.Click += (_, _) =>
            {
                if (_client != null) _client.CancelTransfer();
                else Client.ForgetTransfers(); // waiting for a reconnection: stopped for good
                _host?.CancelTransfer();
            };
            Controls.Add(table);
            AcceptButton = _go;

            _mode.SelectedIndex = _settings.HostMode ? 1 : 0;
            _address.Text = _settings.Address;
            _port.Text = _settings.Port.ToString();
            _password.Text = _settings.Password;
            _listenPassword.Text = _settings.ListenPassword;
            _logging.Checked = _settings.Logging;
            _speedUp.Checked = _settings.CatchUpBySpeed;
            Sounds.Key = Math.Clamp(_settings.SoundKey, 0, 11);
            Sounds.Choice = t => _settings.SoundChoices.TryGetValue(t.ToString(), out var s) && (s == Sounds.RandomName || Sounds.All.Contains(s)) ? s : Sounds.DefaultName;
            _sounds.Click += (_, _) =>
            {
                Doing("choosing sounds");
                using var f = new SoundsForm(_settings);
                f.ShowDialog(this);
            };
            Sounds.Warm();
            _quality.Items.Add("Variable: follows the connection");
            foreach (var (kbps, _) in Protocol.OpusSteps) _quality.Items.Add("Locked at " + kbps + " kbit/s");
            _quality.SelectedIndex = Math.Clamp(_settings.SoundQuality + 1, 0, _quality.Items.Count - 1);
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

            _device.Items.Add("Windows default");
            _devices.Add(("", "Windows default"));
            foreach (var d in Wasapi.OutputDevices()) { _devices.Add(d); _device.Items.Add(d.Name); }
            int sel = _devices.FindIndex(d => d.Id == _settings.OutputDevice);
            _device.SelectedIndex = sel < 0 ? 0 : sel;
            _fillingCapture = true;
            foreach (var d in _devices) _captureFrom.Items.Add(d.Name);
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
            _toggle.Click += (_, _) => _keys?.Toggle();
            _update.Click += (_, _) => CheckForUpdates();
            _restart.Click += (_, _) => RestartRemote();
            _audioSetup.Click += (_, _) => SetUpAudio();
            _audioRemove.Click += (_, _) => RemoveAudio();
            _portEditor.Click += (_, _) => { SaveSettings(); using var f = new PortEditorForm(_settings); f.ShowDialog(this); };
            _startup.CheckedChanged += (_, _) => StartupChanged();
            _service.CheckedChanged += (_, _) => { if (!_settingService) ServiceChanged(); };
            _titleTimer.Tick += (_, _) => UpdateTitle();
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("&Open TailRemote", null, (_, _) => RestoreFromTray());
            trayMenu.Items.Add("&Stop hosting and exit", null, (_, _) => { _reallyExit = true; RestoreFromTray(); Close(); });
            _tray.ContextMenuStrip = trayMenu;
            _tray.Click += (_, e) => { if (e is not MouseEventArgs m || m.Button == MouseButtons.Left) RestoreFromTray(); };
            _retryTimer.Tick += (_, _) => { if (_client == null && !_connecting) Connect(quiet: true); };
            UpdateMode();
        }

        private static Label AddRow(TableLayoutPanel t, string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            c.AccessibleName = label.Replace("&", "");
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(l);
            t.Controls.Add(c);
            return l;
        }

        private bool HostMode => _mode.SelectedIndex == 1;

        private void UpdateMode()
        {
            bool host = HostMode;
            _addressLabel.Visible = _address.Visible = !host;
            _deviceLabel.Visible = _device.Visible = !host;
            _savedLabel.Visible = _saved.Visible = _savedButtons.Visible = !host;
            _listenLabel.Visible = _listenPassword.Visible = host;
            _captureLabel.Visible = _captureFrom.Visible = host;
            _speedUp.Visible = !host; // it is about how this PC plays the sound
            _sounds.Visible = !host; // a host's sounds would go out with its own sound
            _qualityLabel.Visible = _quality.Visible = !host; // the host always sends the best unless asked for less
            _streamingLabel.Visible = _streaming.Visible = !host;
            _toggle.Visible = !host;
            _restart.Visible = !host;
            _startup.Visible = host;
            _service.Visible = host;
            _startup.Enabled = !_service.Checked; // the service replaces the at-sign-in task
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool busy = _client != null || _host != null || _reconnecting;
            _mode.Enabled = !busy;
            if (HostMode) _go.Text = _service.Checked ? "Apply &settings to the service" : _host == null ? "&Start hosting" : "&Stop hosting";
            else _go.Text = _client == null && !_reconnecting ? "&Connect" : "Dis&connect";
            _toggle.Enabled = _client != null && !_client.ListenOnly;
            _restart.Enabled = _client != null && !_client.ListenOnly;
            SaveResumeState();
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
            var t = new System.Threading.Thread(() => { foreach (var job in jobs.GetConsumingEnumerable()) try { job(); } catch { } })
                { IsBackground = true, Name = "TailRemote clipboard" };
            t.SetApartmentState(System.Threading.ApartmentState.STA); // the clipboard needs it
            t.Start();
            return jobs;
        }

        private bool CanSend(out Client? client, out Host? host)
        {
            client = _client;
            host = _host;
            if (client == null && host == null) { Say("Connect, or start hosting, first."); return false; }
            if (client?.ListenOnly == true) { Say("Listeners cannot send anything."); return false; }
            if (host != null && !host.HasController) { Say("No one is controlling this PC, so there is no one to send to."); return false; }
            return true;
        }

        private void SendClipboard()
        {
            Doing("sending the clipboard");
            if (!CanSend(out var client, out var host)) return;
            ClipboardJobs.TryAdd(() =>
            {
                string[]? files = null;
                string? text = null;
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
                        break;
                    }
                    catch { System.Threading.Thread.Sleep(50); } // another program has it open
                }
                if (files is { Length: > 0 })
                {
                    client?.SendClipboardFiles(files);
                    host?.SendClipboardFiles(files);
                    Later(() => Tone(Sounds.Tone.ClipboardSent)); // the Files line says the rest, without speaking
                }
                else if (!string.IsNullOrEmpty(text))
                {
                    if ((long)text.Length * 3 > FileChannel.MaxText) { Later(() => Say("That is too much text to send: over 512 megabytes.")); return; }
                    client?.SendClipboard(text);
                    host?.SendClipboard(text);
                    Later(() => Tone(Sounds.Tone.ClipboardSent));
                }
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
                if (!CanSend(out var client, out var host)) return; // the connection may have closed meanwhile
                client?.SendFiles(paths);
                host?.SendFiles(paths);
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
                _idleTimer.Tick += (_, _) => { _idleTimer!.Stop(); if (_transferShown == null) { _transferStatus.Text = TransferIdle; _transferBar.Value = 0; } };
            }
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        /// <summary>
        /// One bar per file moving right now, each named after its file (NVDA reads the name and
        /// percent), with a line above it for the sizes. Bars are reused, never piled up; none
        /// when nothing is moving.
        /// </summary>
        private void ShowFileBars(System.Collections.Generic.IReadOnlyList<(string Name, long Done, long Total)> files)
        {
            int n = Math.Min(files.Count, 32);
            _fileBars.SuspendLayout();
            while (_fileBars.Controls.Count < n * 2)
            {
                _fileBars.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(540, 0) });
                _fileBars.Controls.Add(new ProgressBar { Width = 540, Height = 16, Maximum = 100 });
            }
            for (int i = 0; i < _fileBars.Controls.Count / 2; i++)
            {
                var label = (Label)_fileBars.Controls[i * 2];
                var bar = (ProgressBar)_fileBars.Controls[i * 2 + 1];
                bool shown = i < n;
                label.Visible = bar.Visible = shown;
                if (!shown) continue;
                var (name, done, total) = files[i];
                int percent = total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);
                label.Text = name + ": " + FileChannel.Size(done) + " of " + FileChannel.Size(total);
                bar.AccessibleName = name;
                bar.Value = percent;
            }
            _fileBars.Visible = n > 0;
            _fileBars.ResumeLayout();
        }

        /// <summary>The connection closed: whatever was going has stopped, and the Files line says so.</summary>
        private void TransferEnded(string why)
        {
            if (_transferShown == null) return;
            _transferShown = null;
            ShowFileBars(Array.Empty<(string, long, long)>());
            _transferStatus.Text = why;
            _transferBar.Value = 0;
            _transferStop.Enabled = false;
            IdleSoon();
        }

        /// <summary>How a transfer is going, either way: the Files line and bar. Never spoken, so a lot of them cannot flood NVDA; the sounds say they started and ended.</summary>
        private void ShowTransfer(FileChannel.Transfer t)
        {
            if (!t.Finished && !ReferenceEquals(t, _transferShown)) _transferShown = t;
            if (t.Finished)
            {
                if (_transferShown != null && !ReferenceEquals(t, _transferShown) && !_transferShown.Finished) return; // an older one ending: the newer one is what is going on
                _transferShown = null;
                ShowFileBars(Array.Empty<(string, long, long)>());
                _transferStatus.Text = t.Result ?? TransferIdle;
                IdleSoon();
                _transferBar.Value = t.Failed ? 0 : 100;
                _transferStop.Enabled = false;
                // The clipboard's own sounds play when it is sent and when it arrives; file sounds are
                // for Send files only; and stopping (or replacing) on purpose is never an error.
                if (t.Failed) { if (!t.Cancelled) Tone(Sounds.Tone.Error); }
                else if (!t.Clipboard) Tone(t.Outgoing ? Sounds.Tone.FileSent : Sounds.Tone.FileReceived);
                return;
            }
            string left = t.Left is TimeSpan l ? ", about " + (l.TotalSeconds < 60 ? Math.Max(1, (int)l.TotalSeconds) + " seconds" : (int)l.TotalMinutes + " minutes " + l.Seconds + " seconds") + " left" : "";
            _transferStatus.Text = (t.Outgoing ? "Sending " : "Receiving ") + t.What + ": " + FileChannel.Size(t.Done) + " of " + FileChannel.Size(t.Total) +
                (t.Waiting ? ", waiting for the connection to come back; it carries on from here." : ", " + FileChannel.Speed(t.BytesPerSecond) + left + ".");
            _transferBar.Value = t.Total <= 0 ? 0 : (int)Math.Clamp(t.Done * 100 / t.Total, 0, 100);
            ShowFileBars(t.Files);
            _transferStop.Enabled = true;
        }

        // ---- Saved PCs ----

        private bool _filling;

        private void FillSaved()
        {
            _filling = true;
            _saved.Items.Clear();
            _saved.Items.Add("None: type the address below");
            foreach (var pc in _settings.SavedPcs) _saved.Items.Add(pc);
            int i = _settings.SavedPcs.FindIndex(p => p.Address == _settings.Address && p.Port == _settings.Port);
            _saved.SelectedIndex = i + 1;
            _filling = false;
            _forgetPc.Enabled = _saved.SelectedIndex > 0;
        }

        private void UseSaved()
        {
            _forgetPc.Enabled = _saved.SelectedIndex > 0;
            if (_filling || _saved.SelectedItem is not SavedPc pc) return;
            _address.Text = pc.Address;
            _port.Text = pc.Port.ToString();
            _password.Text = Settings.Unprotect(pc.PasswordEnc);
            SaveSettings();
        }

        private void SavePc()
        {
            string address = _address.Text.Trim();
            if (address.Length == 0) { Say("Type the address first."); _address.Focus(); return; }
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
            if (_saved.SelectedItem is not SavedPc pc) return;
            _settings.SavedPcs.Remove(pc);
            _settings.Save();
            FillSaved();
            Say("Forgot " + pc + ".");
            _saved.Focus();
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
            _toggle.Text = remote ? "Control &this PC (Ctrl+Shift+Enter)" : "Control &remote PC (Ctrl+Shift+Enter)";
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
            }
            else if (_reconnecting) t = "TailRemote - reconnecting";
            else t = _host != null ? "TailRemote - hosting" : "TailRemote";
            if (Text != t) Text = t;
            // The Streaming line, read with Tab: what is coming in right now.
            string st = _client == null ? (_reconnecting ? "Not connected: trying again every 3 seconds" : "Not connected")
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
            Doing("pressing " + _go.Text.Replace("&", ""));
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
            string listen = _listenPassword.Text;
            if (listen.Length > 0 && (listen.Length < MinPasswordLength || listen == _password.Text))
            {
                Say(listen == _password.Text
                    ? "The listen-only password must be different from the main password, or empty."
                    : "The listen-only password needs at least " + MinPasswordLength + " characters, or leave it empty.");
                _listenPassword.Focus();
                _listenPassword.SelectAll();
                return;
            }
            if (!CheckPort(out int port)) return;
            PortEditorForm.Remember(_settings, port);
            try
            {
                _host = new Host(port, _password.Text, listen, msg => Later(() => Log(msg)),
                    _settings.CaptureDevice.Length == 0 ? null : _settings.CaptureDevice);
                _host.ClipboardReceived += text => Later(() => ClipboardArrived(text));
                _host.ClipboardFilesReceived += paths => Later(() => ClipboardFilesArrived(paths));
                _host.TransferProgress += t => Later(() => ShowTransfer(t));
                Say("Hosting on port " + port + ". Waiting for a connection." + (listen.Length > 0 ? " Listening with the listen-only password is on." : ""));
                if (AudioSetup.FinishQuietly()) Log("Finished setting up the TailRemote audio device.");
                else if (Wasapi.OutputDevices().Count == 0)
                {
                    // No sound output: nothing could be heard. Set one up straight away.
                    Log("This PC has no sound output, so setting up the TailRemote audio device.");
                    SetUpAudio();
                }
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
            Say(n == 0 ? "Type a password first. It needs at least " + MinPasswordLength + " characters."
                       : "The password needs at least " + MinPasswordLength + " characters. It has " + n + ".");
            _password.Focus();
            _password.SelectAll();
            return false;
        }

        /// <summary>Refuses a port that is not a number, or that would clash with something else.</summary>
        private bool CheckPort(out int port)
        {
            string? problem = Protocol.PortProblem(_port.Text.Trim(), out port);
            if (problem == null) return true;
            Say(problem);
            _port.Focus();
            _port.SelectAll();
            return false;
        }

        private void StopHost()
        {
            TransferEnded("Stopped: hosting stopped.");
            Doing("stopping hosting");
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
            if (address.Length == 0) { Say("Type the address first."); _address.Focus(); return; }
            if (!CheckPassword()) return;
            if (!CheckPort(out int port)) return;
            _connecting = true;
            int attempt = ++_attempt;
            bool hadFocus = _go.Focused;
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
                var c = await System.Threading.Tasks.Task.Run(() =>
                    Client.Connect(address, port, pw, player, msg => Later(() => Say(msg)), locked));
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
                string start = _expectRestart ? "The remote PC is back." : wasReconnecting ? "Reconnected." : "Connected.";
                _expectRestart = false;
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
                // Keeps trying every 3 seconds until Disconnect, except when trying again
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
                    Say("Could not connect: " + e.Message + " Trying again every 3 seconds until you press Disconnect.");
                }
            }
            catch { } // an abandoned attempt failing: nobody is waiting for it
            finally
            {
                _connecting = false;
                _go.Enabled = true;
                if (hadFocus) _go.Focus();
                UpdateButtons();
                UpdateTitle();
            }
        }

        private void Disconnect(string why, bool byUser)
        {
            // Pressing Disconnect stops transfers; a dropped connection does not: they carry on
            // when it reconnects (and say so on the Files line meanwhile).
            if (byUser)
            {
                Client.ForgetTransfers();
                TransferEnded("Stopped: you disconnected.");
            }
            Doing("disconnecting");
            if (_client == null) return;
            // Keys come back to this PC while the connection is down, so nothing is
            // typed into nowhere; remote control resumes by itself on reconnecting.
            _resumeRemote = !byUser && _keys?.Remote == true;
            _quietModeChange = _resumeRemote;
            _keys?.SetClient(null);
            _client.Dispose();
            _client = null;
            Tone(Sounds.Tone.Disconnected);
            if (byUser) { _expectRestart = false; Say(why); }
            else if (_expectRestart)
            {
                // Expected: keep trying until it is back, however long the restart takes.
                _reconnecting = true;
                _retryTimer.Start();
                Say("The remote PC is restarting. TailRemote reconnects as soon as it is back.");
            }
            else
            {
                // Dropped, or the host restarted after an update: try at once, then every 3 seconds.
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
            _attempt++;
            _reconnecting = false;
            _retryTimer.Stop();
            Say(why);
            UpdateButtons();
            UpdateTitle();
        }

        private async void CheckForUpdates()
        {
            Doing("checking for updates");
            _update.Enabled = false;
            Say("Checking for updates.");
            string args = "";
            try
            {
                var r = await Updater.CheckAsync();
                if (r == null) { Say("TailRemote " + Updater.Current + " is the latest version."); return; }
                string notes = r.Notes.Trim().Length > 0 ? r.Notes.Trim() : "No notes.";
                var answer = MessageBox.Show(this, "Version " + r.Version + " is available. You have " + Updater.Current + "." +
                    Environment.NewLine + Environment.NewLine + notes + Environment.NewLine + Environment.NewLine + "Update now? TailRemote restarts and carries on where it was.",
                    "TailRemote update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return;
                Say("Downloading version " + r.Version + ".");
                args = _host != null ? "--host" : _client != null || _reconnecting ? "--connect" : "";
                SaveSettings();
                // Let go of the port and the keyboard before the new copy starts.
                _keys?.SetClient(null);
                _client?.Dispose(); _client = null;
                _host?.Dispose(); _host = null;
                await Updater.InstallAsync(r, args);
                Close();
            }
            catch (Exception e)
            {
                Say("Update failed: " + e.Message);
                // Put back whatever was running before the update began.
                if (args == "--host" && _host == null) StartHost();
                else if (args == "--connect" && _client == null) Connect(quiet: false);
            }
            finally { if (!IsDisposed) _update.Enabled = true; }
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
                || (want && (!CheckPassword() || !CheckPort(out _))))
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
            if (!CheckPassword() || !CheckPort(out _)) return;
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

        /// <summary>Logs a line and speaks it.</summary>
        private void Say(string line)
        {
            Log(line);
            Speech.Speak(line);
        }

        /// <summary>While hosting, closing the window only hides it: hosting carries on from the notification area.</summary>
        private void HideToTray(bool announce)
        {
            _tray.Visible = true;
            Hide();
            if (announce) Speech.Speak("TailRemote is still hosting, in the notification area. Press Windows B to find it. Use its menu to stop hosting and exit.");
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
            if (e.CloseReason == CloseReason.UserClosing && _host != null && !_reallyExit)
            {
                e.Cancel = true;
                HideToTray(announce: true);
                return;
            }
            _tray.Visible = false;
            _settings.ResumeState = _host != null ? "host" : _client != null || _reconnecting ? "connect" : "";
            SaveSettings();
            _retryTimer.Stop();
            _keys?.SetClient(null);
            _client?.Dispose();
            _player?.Dispose();
            _host?.Dispose();
            _keys?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
