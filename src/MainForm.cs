using System;
using System.Drawing;
using System.Windows.Forms;

namespace TailRemote
{
    internal sealed class MainForm : Form
    {
        private readonly Settings _settings = Settings.Load();
        private readonly bool _autoHost;

        private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _address = new();
        private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
        private readonly TextBox _password = new() { UseSystemPasswordChar = true };
        private readonly NumericUpDown _buffer = new() { Minimum = 5, Maximum = 1000, Increment = 5 };
        private readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _tailscaleOnly = new() { Text = "Only accept &Tailscale connections", AutoSize = true };
        private readonly CheckBox _startup = new() { Text = "Start &hosting when Windows starts (asks for administrator)", AutoSize = true };
        private readonly Button _go = new() { AutoSize = true };
        private readonly Button _toggle = new() { Text = "Control &remote PC (Ctrl+Shift+Enter)", AutoSize = true };
        private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 160 };

        private readonly Label _addressLabel, _bufferLabel, _deviceLabel;
        private readonly System.Collections.Generic.List<(string Id, string Name)> _devices = new();

        private Client? _client;
        private Host? _host;
        private KeyCapture? _keys;
        private IntPtr _handle;
        private readonly Timer _pingTimer = new() { Interval = 2000 };

        public MainForm(bool autoHost)
        {
            _autoHost = autoHost;
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
            _bufferLabel = AddRow(table, "Audio &buffer in milliseconds (lower means less delay)", _buffer);
            _deviceLabel = AddRow(table, "&Output device", _device);
            table.Controls.Add(_tailscaleOnly); table.SetColumnSpan(_tailscaleOnly, 2);
            table.Controls.Add(_startup); table.SetColumnSpan(_startup, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(_go);
            buttons.Controls.Add(_toggle);
            table.Controls.Add(buttons); table.SetColumnSpan(buttons, 2);
            AddRow(table, "Status &log", _log);
            _log.Dock = DockStyle.Fill;
            Controls.Add(table);
            AcceptButton = _go;

            _mode.SelectedIndex = _settings.HostMode ? 1 : 0;
            _address.Text = _settings.Address;
            _port.Value = Math.Clamp(_settings.Port, 1, 65535);
            _password.Text = _settings.Password;
            _buffer.Value = Math.Clamp(_settings.BufferMs, 5, 1000);
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
            _startup.CheckedChanged += (_, _) => StartupChanged();
            _pingTimer.Tick += (_, _) => UpdateTitle();
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
            _bufferLabel.Visible = _buffer.Visible = !host;
            _deviceLabel.Visible = _device.Visible = !host;
            _toggle.Visible = !host;
            _tailscaleOnly.Visible = _startup.Visible = host;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool busy = _client != null || _host != null;
            _mode.Enabled = !busy;
            _go.Text = HostMode ? (_host == null ? "&Start hosting" : "&Stop hosting") : (_client == null ? "&Connect" : "Dis&connect");
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
            if (_autoHost)
            {
                _mode.SelectedIndex = 1;
                Go();
                WindowState = FormWindowState.Minimized;
            }
        }

        private void ModeChanged(bool remote)
        {
            _toggle.Text = remote ? "Control &this PC (Ctrl+Shift+Enter)" : "Control &remote PC (Ctrl+Shift+Enter)";
            Say(remote ? "Controlling remote PC." : "Controlling this PC.");
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            if (_client == null) { Text = _host != null ? "TailRemote - hosting" : "TailRemote"; return; }
            string ping = _client.LastPingMs >= 0 ? ", " + _client.LastPingMs + " ms" : "";
            Text = "TailRemote - " + (_keys?.Remote == true ? "controlling remote" : "connected") + ping;
        }

        private void SaveSettings()
        {
            _settings.HostMode = HostMode;
            _settings.Address = _address.Text.Trim();
            _settings.Port = (int)_port.Value;
            _settings.Password = _password.Text;
            _settings.BufferMs = (int)_buffer.Value;
            _settings.OutputDevice = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            _settings.TailscaleOnly = _tailscaleOnly.Checked;
            _settings.Save();
        }

        private void Go()
        {
            SaveSettings();
            if (HostMode) { if (_host == null) StartHost(); else StopHost(); }
            else { if (_client == null) Connect(); else Disconnect("Disconnected."); }
        }

        private void StartHost()
        {
            if (_password.Text.Length == 0) { Say("Set a password first."); _password.Focus(); return; }
            if (!Startup.FirewallRuleExists()) Log("Tip: if Windows asks about the firewall, allow TailRemote. Turning on Start hosting when Windows starts also adds the rule.");
            try
            {
                _host = new Host((int)_port.Value, _password.Text, _tailscaleOnly.Checked, msg => BeginInvoke(() => Log(msg)));
                Say("Hosting on port " + _port.Value + ". Waiting for a connection.");
                if (!Startup.IsElevated()) Log("Not running as administrator, so keys cannot reach administrator windows. Start hosting when Windows starts runs it as administrator.");
            }
            catch (Exception e) { Say("Could not start hosting: " + e.Message); }
            UpdateButtons();
            UpdateTitle();
        }

        private void StopHost()
        {
            _host?.Dispose();
            _host = null;
            Say("Stopped hosting.");
            UpdateButtons();
            UpdateTitle();
        }

        private async void Connect()
        {
            string address = _address.Text.Trim();
            if (address.Length == 0) { Say("Type the address first."); _address.Focus(); return; }
            _go.Enabled = false;
            Say("Connecting to " + address + ".");
            string pw = _password.Text;
            int port = (int)_port.Value, buffer = (int)_buffer.Value;
            string device = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            try
            {
                var c = await System.Threading.Tasks.Task.Run(() =>
                    Client.Connect(address, port, pw, device, buffer, msg => BeginInvoke(() => Log(msg))));
                c.Disconnected += why => BeginInvoke(() => Disconnect(why));
                _client = c;
                _keys?.SetClient(c);
                _pingTimer.Start();
                Say("Connected. Press Control Shift Enter to control the remote PC.");
            }
            catch (Exception e) { Say("Could not connect: " + e.Message); }
            _go.Enabled = true;
            UpdateButtons();
            UpdateTitle();
        }

        private void Disconnect(string why)
        {
            if (_client == null) return;
            _keys?.SetClient(null);
            _client.Dispose();
            _client = null;
            _pingTimer.Stop();
            Say(why);
            UpdateButtons();
            UpdateTitle();
        }

        private void StartupChanged()
        {
            bool want = _startup.Checked;
            if (want == Startup.IsEnabled()) return;
            SaveSettings();
            if (Startup.Set(want)) Log(want ? "TailRemote will start hosting, as administrator, when you sign in. The firewall rule was added." : "TailRemote will no longer start with Windows.");
            else
            {
                Say("That needs administrator permission, and it was not given.");
                _startup.Checked = Startup.IsEnabled();
            }
        }

        private void Log(string line)
        {
            _log.AppendText((_log.TextLength > 0 ? Environment.NewLine : "") + DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (_log.Lines.Length > 300) _log.Lines = _log.Lines[^200..];
        }

        /// <summary>Logs a line and speaks it.</summary>
        private void Say(string line)
        {
            Log(line);
            Speech.Speak(line);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            _keys?.SetClient(null);
            _client?.Dispose();
            _host?.Dispose();
            _keys?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
