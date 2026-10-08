using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Sounds: which sound each event makes. One list per event: Default (the
    /// piano sound made for it), None, then every sound (Sounds.All). Moving through
    /// a list plays each sound as you reach it.
    /// </summary>
    internal sealed class SoundsForm : Form
    {
        private readonly Settings _settings;
        private readonly Dictionary<Sounds.Tone, ComboBox> _lists = new();
        private bool _ready;
        private const string DefaultItem = "Default";

        public SoundsForm(Settings settings)
        {
            _settings = settings;
            Text = "Sounds";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
            foreach (Sounds.Tone t in Enum.GetValues<Sounds.Tone>())
            {
                var label = new Label { Text = Sounds.Label(t), AutoSize = true, Anchor = AnchorStyles.Left };
                var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, AccessibleName = Sounds.Label(t).Replace("&", "") };
                // Default first (the piano sound made for this event), then None, then every sound.
                list.Items.Add(DefaultItem);
                foreach (string name in Sounds.All) list.Items.Add(name);
                int at = settings.SoundChoices.TryGetValue(t.ToString(), out var c) ? list.Items.IndexOf(c) : -1;
                list.SelectedIndex = at < 0 ? 0 : at;
                // Hear each sound as you reach it.
                var tone = t;
                list.SelectedIndexChanged += (_, _) =>
                {
                    if (_ready && list.SelectedItem is string s) Sounds.PlayNamed(s == DefaultItem ? Sounds.Default(tone) : s);
                };
                table.Controls.Add(label);
                table.Controls.Add(list);
                _lists[t] = list;
            }

            var buttons = new FlowLayoutPanel { AutoSize = true };
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => Save();
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ready = true;
        }

        private void Save()
        {
            _settings.SoundChoices = new Dictionary<string, string>();
            foreach (var (t, list) in _lists)
                if (list.SelectedItem is string s && s != DefaultItem) _settings.SoundChoices[t.ToString()] = s;
            try { _settings.Save(); } catch { }
        }
    }
}
