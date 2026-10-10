using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// The menu bar's behaviour for NVDA: Enter on a setting turns it on or off and the menu
    /// stays open, saying "checked" or "unchecked", so several can be changed in a row; Escape
    /// goes back a level. Actions (anything that opens a window or asks first) close the menu
    /// as usual.
    /// </summary>
    internal static class Menus
    {
        // Set while a stay-open item is being clicked: every menu in the open chain refuses to close.
        private static bool _keepOpen;

        /// <summary>A setting that is on or off. Enter flips it, the menu stays open and says which.</summary>
        public static ToolStripMenuItem Check(string text)
        {
            var item = new ToolStripMenuItem(text) { Tag = KeepOpenTag };
            item.Click += (_, _) =>
            {
                // Said before the setting's own handlers run, so their message follows it.
                Speech.Speak(item.Checked ? "unchecked" : "checked");
                item.Checked = !item.Checked;
            };
            return item;
        }

        /// <summary>A setting that is on or off but asks first (a window, administrator permission): the menu closes.</summary>
        public static ToolStripMenuItem CheckAsking(string text)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, _) => item.Checked = !item.Checked;
            return item;
        }

        public static ToolStripMenuItem Action(string text, Action clicked, Keys keys = Keys.None)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeys = keys };
            item.Click += (_, _) => clicked();
            return item;
        }

        internal static readonly object KeepOpenTag = new();

        /// <summary>Hooks every drop-down under the menu bar (and ones added later) so stay-open items keep it open.</summary>
        public static void Attach(MenuStrip bar)
        {
            foreach (ToolStripItem top in bar.Items)
                if (top is ToolStripMenuItem m) Attach(m);
        }

        public static void Attach(ToolStripMenuItem menu)
        {
            var dd = menu.DropDown;
            if (dd.Tag == KeepOpenTag) return;
            dd.Tag = KeepOpenTag;
            dd.ItemClicked += (_, e) =>
            {
                if (e.ClickedItem?.Tag != KeepOpenTag) return;
                _keepOpen = true;
                // Cleared once the click has been handled, whichever menus asked to close.
                dd.BeginInvoke(() => _keepOpen = false);
            };
            dd.Closing += (_, e) =>
            {
                if (_keepOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            dd.ItemAdded += (_, e) => { if (e.Item is ToolStripMenuItem sub) Attach(sub); };
            foreach (ToolStripItem i in menu.DropDownItems)
                if (i is ToolStripMenuItem sub) Attach(sub);
        }
    }

    /// <summary>
    /// A submenu that works like a drop-down list: one choice checked. Its name shows the choice
    /// ("Sound quality: Variable"), so NVDA reads the current one on the way past. Enter picks
    /// one, the menu stays open and says "checked".
    /// </summary>
    internal sealed class MenuChoice
    {
        public readonly ToolStripMenuItem Menu;
        private readonly string _title;
        private readonly List<string> _names = new();
        private int _index = -1;

        public event EventHandler? SelectedIndexChanged;

        public MenuChoice(string title)
        {
            _title = title;
            Menu = new ToolStripMenuItem(title);
        }

        public int Count => _names.Count;

        /// <summary>Empties the list, choosing nothing (no SelectedIndexChanged).</summary>
        public void Clear()
        {
            _names.Clear();
            _index = -1;
            Menu.DropDownItems.Clear();
            Menu.Text = _title;
        }

        public ToolStripMenuItem Add(string name)
        {
            int i = _names.Count;
            _names.Add(name);
            var item = new ToolStripMenuItem(name.Replace("&", "&&")) { Tag = Menus.KeepOpenTag };
            item.Click += (_, _) =>
            {
                Speech.Speak("checked");
                SelectedIndex = i;
            };
            Menu.DropDownItems.Add(item);
            return item;
        }

        public int SelectedIndex
        {
            get => _index;
            set
            {
                if (value < 0 || value >= _names.Count || value == _index) return;
                if (_index >= 0) ((ToolStripMenuItem)Menu.DropDownItems[_index]).Checked = false;
                _index = value;
                ((ToolStripMenuItem)Menu.DropDownItems[value]).Checked = true;
                Menu.Text = _title + ": " + _names[value].Replace("&", "&&");
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Something typed in (the address, port, a password) as a menu item that says it:
    /// "Address: myserver...". Enter opens a small box to change it. Passwords say only
    /// whether one is set.
    /// </summary>
    internal sealed class MenuText
    {
        public readonly ToolStripMenuItem Item;
        private readonly string _title, _prompt;
        private readonly bool _secret;
        private string _text = "";

        /// <summary>Changed by the person (not by code), after the box closed with OK.</summary>
        public event Action? Changed;

        public MenuText(string title, string prompt, bool secret = false)
        {
            _title = title;
            _prompt = prompt;
            _secret = secret;
            Item = new ToolStripMenuItem();
            Item.Click += (_, _) => Ask(Item.Owner?.FindForm() ?? Form.ActiveForm, null);
            Show();
        }

        public string Text
        {
            get => _text;
            set { _text = value ?? ""; Show(); }
        }

        private void Show() =>
            Item.Text = _title + ": " + (_secret ? (_text.Length == 0 ? "not set" : "set") : _text.Length == 0 ? "empty" : _text.Replace("&", "&&")) + "...";

        /// <summary>Opens the box. problem: why it is asked for (said first), or null when chosen from the menu.</summary>
        public void Ask(IWin32Window? owner, string? problem)
        {
            using var f = new TextForm(_title.Replace("&", ""), problem == null ? _prompt : problem + " " + _prompt, _text, _secret);
            if (f.ShowDialog(owner) != DialogResult.OK || f.Value == _text) return;
            Text = f.Value;
            Changed?.Invoke();
        }
    }

    /// <summary>The small box MenuText opens: what to type, the text, and Show password for passwords.</summary>
    internal sealed class TextForm : Form
    {
        private readonly TextBox _box = new() { Width = 360 };
        public string Value => _box.Text;

        public TextForm(string title, string prompt, string text, bool secret)
        {
            Text = title;
            Font = new System.Drawing.Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            var label = new Label { Text = prompt, AutoSize = true, MaximumSize = new System.Drawing.Size(360, 0) };
            _box.Text = text;
            _box.AccessibleName = prompt; // NVDA reads it with the box, including why it was asked for
            _box.UseSystemPasswordChar = secret;
            flow.Controls.Add(label);
            flow.Controls.Add(_box);
            if (secret)
            {
                var show = new CheckBox { Text = "&Show password", AutoSize = true };
                show.CheckedChanged += (_, _) => _box.UseSystemPasswordChar = !show.Checked;
                flow.Controls.Add(show);
            }
            var buttons = new FlowLayoutPanel { AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }
    }

    /// <summary>A line of information in a menu (Streaming, Files): Enter says it again, the menu stays open.</summary>
    internal sealed class MenuStatus
    {
        public readonly ToolStripMenuItem Item = new() { Tag = Menus.KeepOpenTag };
        private string _text = "";

        public MenuStatus(string text)
        {
            Item.Click += (_, _) => { if (!Item.HasDropDownItems) Speech.Speak(_text); };
            Text = text;
        }

        public string Text
        {
            get => _text;
            set
            {
                if (value == _text) return;
                _text = value;
                Item.Text = value.Replace("&", "&&");
            }
        }
    }
}
