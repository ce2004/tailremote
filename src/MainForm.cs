using System;
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
        private readonly CheckBox _tailscaleOnly = new() { Text = "Only accept &Tailscale connections", AutoSize = true };
        private readonly CheckBox _startup = new() { Text = "Start &hosting when Windows starts (asks for administrator)", AutoSize = true };
        private readonly Button _go = new() { AutoSize = true };
        private readonly Button _toggle = new() { Text = "Control &remote PC (Ctrl+Shift+Enter)", AutoSize = true };
        private readonly Button _update = new() { Text = "Check for &updates", AutoSize = true };
        private readonly Button _audioSetup = new() { Text = "Set up au&dio device", AutoSize = true };
        private readonly Button _audioRemove = new() { Text = "Remove audio de&vice", AutoSize = true };
        private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 160 };

        private readonly Label _addressLabel, _deviceLabel;
        private readonly System.Collections.Generic.List<(string Id, string Name)> _devices = new();

        private Client? _client;
        private Host? _host;
        private KeyCapture? _keys;
        private IntPtr _handle;
        private bool _connecting, _reconnecting;
        private int _attempt; // bumped to abandon a connection attempt still under way
        private readonly Timer _titleTimer = new() { Interval = 1000 };
        private readonly Timer _retryTimer = new() { Interval = 2000 };

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
            _addressLabel = AddRow(table, "&Address (Tailscale name or IP)", _address);
            AddRow(table, "&Port", _port);
            AddRow(table, "Pass&word", _password);
            _deviceLabel = AddRow(table, "&Output device", _device);
            table.Controls.Add(_tailscaleOnly); table.SetColumnSpan(_tailscaleOnly, 2);
            table.Controls.Add(_startup); table.SetColumnSpan(_startup, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(_go);
            buttons.Controls.Add(_toggle);
            buttons.Controls.Add(_audioSetup);
            buttons.Controls.Add(_audioRemove);
            buttons.Controls.Add(_update);
            table.Controls.Add(buttons); table.SetColumnSpan(buttons, 2);
            AddRow(table, "Status &log", _log);
            _log.Dock = DockStyle.Fill;
            Controls.Add(table);
            AcceptButton = _go;

            _mode.SelectedIndex = _settings.HostMode ? 1 : 0;
            _address.Text = _settings.Address;
            _port.Text = _settings.Port.ToString();
            _password.Text = _settings.Password;
            _tailscaleOnly.Checked = _settings.TailscaleOnly;
            _startup.Checked = Startup.IsEnabled();

            _device.Items.Add("Windows default");
            _devices.Add(("", "Windows default"));
            foreach (var d in Wasapi.OutputDevices()) { _devices.Add(d); _device.Items.Add(d.Name); }
            int sel = _devices.FindIndex(d => d.Id == _settings.OutputDevice);
            _device.SelectedIndex = sel < 0 ? 0 : sel;

            _mode.SelectedIndexChanged += (_, _) => UpdateMode();
            _go.Click += (_, _) => Go();
            _toggle.Click += (_, _) => _keys?.Toggle();
            _update.Click += (_, _) => CheckForUpdates();
            _audioSetup.Click += (_, _) => SetUpAudio();
            _audioRemove.Click += (_, _) => RemoveAudio();
            _startup.CheckedChanged += (_, _) => StartupChanged();
            _titleTimer.Tick += (_, _) => UpdateTitle();
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
            _toggle.Visible = !host;
            _tailscaleOnly.Visible = _startup.Visible = host;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool busy = _client != null || _host != null || _reconnecting;
            _mode.Enabled = !busy;
            if (HostMode) _go.Text = _host == null ? "&Start hosting" : "&Stop hosting";
            else _go.Text = _reconnecting ? "Stop re&connecting" : _client == null ? "&Connect" : "Dis&connect";
            _toggle.Enabled = _client != null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _handle = Handle;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _keys = new KeyCapture(() => _handle);
            _keys.ModeChanged += remote => BeginInvoke(() => ModeChanged(remote));
            _keys.NotConnected += () => BeginInvoke(() => Say("Not connected."));
            _titleTimer.Start();
            if (_updated) Say("Updated to version " + Updater.Current + ".");
            if (_autoHost)
            {
                _mode.SelectedIndex = 1;
                Go();
                WindowState = FormWindowState.Minimized;
            }
            else if (_autoConnect && !HostMode && _address.Text.Trim().Length > 0) Go();
        }

        private void ModeChanged(bool remote)
        {
            _toggle.Text = remote ? "Control &this PC (Ctrl+Shift+Enter)" : "Control &remote PC (Ctrl+Shift+Enter)";
            Say(remote ? "Controlling remote PC." : "Controlling this PC.");
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string t;
            if (_client != null)
            {
                t = "TailRemote - " + (_keys?.Remote == true ? "controlling remote" : "connected");
                if (_client.LastPingMs >= 0) t += ", ping " + _client.LastPingMs + " ms";
                int audio = _client.AudioDelayMs;
                if (audio >= 0) t += ", audio " + (audio + Math.Max(0, _client.LastPingMs) / 2) + " ms";
            }
            else if (_reconnecting) t = "TailRemote - reconnecting";
            else t = _host != null ? "TailRemote - hosting" : "TailRemote";
            if (Text != t) Text = t;
        }

        private void SaveSettings()
        {
            _settings.HostMode = HostMode;
            _settings.Address = _address.Text.Trim();
            if (int.TryParse(_port.Text.Trim(), out int port)) _settings.Port = port;
            _settings.Password = _password.Text;
            _settings.OutputDevice = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            _settings.TailscaleOnly = _tailscaleOnly.Checked;
            _settings.Save();
        }

        private void Go()
        {
            SaveSettings();
            if (HostMode) { if (_host == null) StartHost(); else StopHost(); }
            else if (_reconnecting) StopReconnecting("Stopped reconnecting.");
            else if (_client == null) Connect(quiet: false);
            else Disconnect("Disconnected.", byUser: true);
        }

        private void StartHost()
        {
            if (_password.Text.Length == 0) { Say("Set a password first."); _password.Focus(); return; }
            if (!CheckPort(out int port)) return;
            if (!Startup.FirewallRuleExists()) Log("Tip: if Windows asks about the firewall, allow TailRemote. Turning on Start hosting when Windows starts also adds the rule.");
            try
            {
                _host = new Host(port, _password.Text, _tailscaleOnly.Checked, msg => Later(() => Log(msg)));
                Say("Hosting on port " + port + ". Waiting for a connection.");
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
            if (!CheckPort(out int port)) return;
            _connecting = true;
            int attempt = ++_attempt;
            bool hadFocus = _go.Focused;
            if (!quiet) { _go.Enabled = false; Say("Connecting to " + address + "."); }
            string pw = _password.Text;
            string device = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            try
            {
                var c = await System.Threading.Tasks.Task.Run(() =>
                    Client.Connect(address, port, pw, device, msg => Later(() => Say(msg))));
                if (attempt != _attempt || HostMode || _client != null)
                {
                    // Stopped, switched to hosting, or already connected while this was under way.
                    c.Dispose();
                    return;
                }
                c.Disconnected += why => Later(() => Disconnect(why, byUser: false));
                _client = c;
                _keys?.SetClient(c);
                bool wasReconnecting = _reconnecting;
                _reconnecting = false;
                _retryTimer.Stop();
                Say(wasReconnecting ? "Reconnected. Press Control Shift Enter to control the remote PC." : "Connected. Press Control Shift Enter to control the remote PC.");
            }
            catch (Exception e) when (attempt == _attempt)
            {
                bool hopeless = e.Message.StartsWith("Wrong password") || e.Message.Contains("different TailRemote version");
                if (_reconnecting && hopeless) StopReconnecting("Stopped reconnecting: " + e.Message);
                else if (!quiet) Say("Could not connect: " + e.Message);
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
            if (_client == null) return;
            _keys?.SetClient(null);
            _client.Dispose();
            _client = null;
            if (byUser) Say(why);
            else
            {
                // Dropped, or the host restarted after an update: keep trying.
                _reconnecting = true;
                _retryTimer.Start();
                Say(why + " Reconnecting.");
            }
            UpdateButtons();
            UpdateTitle();
        }

        private void StopReconnecting(string why)
        {
            _attempt++;
            _reconnecting = false;
            _retryTimer.Stop();
            Say(why);
            UpdateButtons();
            UpdateTitle();
        }

        private async void CheckForUpdates()
        {
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

        private async void StartupChanged()
        {
            bool want = _startup.Checked;
            if (want == Startup.IsEnabled()) return;
            SaveSettings();
            Say("Windows asks for administrator permission.");
            if (await Startup.SetAsync(want)) Say(want ? "TailRemote will start hosting, as administrator, when you sign in. The firewall rule was added." : "TailRemote will no longer start with Windows.");
            else
            {
                Say("That needs administrator permission, and it was not given.");
                _startup.Checked = Startup.IsEnabled();
            }
        }

        private void Log(string line)
        {
            _log.AppendText((_log.TextLength > 0 ? Environment.NewLine : "") + DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            // Trim only while nobody is reading it, so the caret never jumps.
            if (!_log.Focused && _log.Lines.Length > 300) _log.Lines = _log.Lines[^200..];
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _settings.ResumeState = _host != null ? "host" : _client != null || _reconnecting ? "connect" : "";
            SaveSettings();
            _retryTimer.Stop();
            _keys?.SetClient(null);
            _client?.Dispose();
            _host?.Dispose();
            _keys?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
