using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Get files from the remote PC: its folders and files in a list. Enter opens a folder,
    /// Backspace goes up, Space checks files and folders, and Get brings the checked ones (or
    /// the one you are on) here, into the received files folder, like Send files from there.
    /// </summary>
    internal sealed class RemoteFilesForm : Form
    {
        private readonly Client _client;
        private readonly Action<string> _say;
        private readonly TextBox _where = new() { ReadOnly = true, Width = 640, AccessibleName = "Folder on the remote PC" };
        private readonly ListView _list = new()
        {
            View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            Width = 640, Height = 360, AccessibleName = "Files and folders",
        };
        private readonly Button _get = new() { Text = "&Get the checked files and folders", AutoSize = true };
        private readonly Button _up = new() { Text = "&Up one folder (Backspace)", AutoSize = true };
        private readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private string _path = "";
        private bool _loading;

        private sealed record Entry(bool Folder, string Path, string Name, long Bytes, long Ticks);

        public RemoteFilesForm(Client client, Action<string> say)
        {
            _client = client;
            _say = say;
            Text = "Get files from the remote PC";
            Font = new System.Drawing.Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimizeBox = MaximizeBox = false;
            _list.Columns.Add("Name", 330);
            _list.Columns.Add("Size", 120);
            _list.Columns.Add("Modified", 170);
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            flow.Controls.Add(new Label { Text = "Folder on the remote PC", AutoSize = true });
            flow.Controls.Add(_where);
            flow.Controls.Add(new Label { Text = "&Files and folders (Enter opens a folder, Space checks, Backspace goes up)", AutoSize = true });
            flow.Controls.Add(_list);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(_get);
            buttons.Controls.Add(_up);
            buttons.Controls.Add(_close);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            CancelButton = _close;
            _get.Click += (_, _) => Get();
            _up.Click += (_, _) => Up();
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Open(); }
                else if (e.KeyCode == Keys.Back) { e.Handled = e.SuppressKeyPress = true; Up(); }
            };
            _list.DoubleClick += (_, _) => Open();
            Shown += async (_, _) => await LoadFolder("", null);
        }

        private Entry? Current => _list.FocusedItem?.Tag as Entry ?? _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as Entry;

        private async void Open()
        {
            if (Current is not Entry e) return;
            if (!e.Folder) { Get(); return; }
            await LoadFolder(e.Path, null);
        }

        private async void Up()
        {
            if (_path.Length == 0) return;
            // A drive's root goes back to the drives and usual folders; the folder just left is where you land.
            string trimmed = _path.TrimEnd('\\');
            string parent = trimmed.Length <= 2 ? "" : System.IO.Path.GetDirectoryName(trimmed) ?? "";
            await LoadFolder(parent, _path);
        }

        private async System.Threading.Tasks.Task LoadFolder(string path, string? focus)
        {
            if (_loading) return;
            _loading = true;
            UseWaitCursor = true;
            try
            {
                string text = await _client.ListFolderAsync(path);
                if (text.StartsWith("E\t")) { _say(text[2..].Trim()); return; }
                var entries = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<Entry>().ToList();
                _path = path;
                _where.Text = path.Length == 0 ? "Drives and usual folders" : path;
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var e in entries)
                {
                    var item = new ListViewItem(e.Name) { Tag = e };
                    item.SubItems.Add(e.Folder ? "folder" : FileChannel.Size(e.Bytes));
                    item.SubItems.Add(e.Ticks > 0 ? new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime().ToString("g") : "");
                    _list.Items.Add(item);
                }
                _list.EndUpdate();
                if (_list.Items.Count == 0) { _say("This folder is empty."); _list.Focus(); return; }
                var land = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => focus != null && string.Equals(((Entry)i.Tag!).Path.TrimEnd('\\'), focus.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    ?? _list.Items[0];
                land.Selected = land.Focused = true;
                land.EnsureVisible();
                _list.Focus();
                if (entries.Count >= RemoteTools.MaxEntries) _say("Only the first " + RemoteTools.MaxEntries + " are shown.");
            }
            catch (TimeoutException) { _say("The remote PC did not answer. Try again."); }
            catch (Exception e) { _say("Could not list the folder: " + e.Message); }
            finally
            {
                _loading = false;
                UseWaitCursor = false;
            }
        }

        private static Entry? Parse(string line)
        {
            var p = line.Split('\t');
            if (p.Length < 5 || (p[0] != "D" && p[0] != "F")) return null;
            long.TryParse(p[3], out long bytes);
            long.TryParse(p[4], out long ticks);
            return new Entry(p[0] == "D", p[1], p[2], bytes, ticks);
        }

        private void Get()
        {
            var chosen = _list.CheckedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag!).ToList();
            if (chosen.Count == 0 && Current is Entry e) chosen.Add(e);
            if (chosen.Count == 0) { _say("Check the files and folders to get first, with Space."); return; }
            // Whole drives are too much to mean: a folder on one is fine.
            if (_path.Length == 0 && chosen.Any(c => c.Path.TrimEnd('\\').Length <= 2)) { _say("Open the drive and choose folders or files in it."); return; }
            _client.Fetch(chosen.Select(c => c.Path));
            foreach (ListViewItem i in _list.CheckedItems) i.Checked = false;
            _say("Getting " + (chosen.Count == 1 ? chosen[0].Name : chosen.Count + " items") + " into " + FileChannel.Downloads + ". The Files line in the Clipboard menu shows how it goes.");
        }
    }

    /// <summary>Remote PC info: one line per fact, read with the arrows. Refresh asks again.</summary>
    internal sealed class RemoteInfoForm : Form
    {
        private readonly ListBox _lines = new() { Width = 560, Height = 260, AccessibleName = "About the remote PC" };

        public RemoteInfoForm(Client client, string first, Action<string> say)
        {
            Text = "Remote PC info";
            Font = new System.Drawing.Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimizeBox = MaximizeBox = false;
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            flow.Controls.Add(_lines);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            var refresh = new Button { Text = "&Refresh", AutoSize = true };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(refresh);
            buttons.Controls.Add(close);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            CancelButton = close;
            Fill(first);
            refresh.Click += async (_, _) =>
            {
                try { Fill(await client.RequestInfoAsync()); _lines.Focus(); say("Refreshed."); }
                catch { say("The remote PC did not answer."); }
            };
            Shown += (_, _) => _lines.Focus();
        }

        private void Fill(string text)
        {
            int at = Math.Max(0, _lines.SelectedIndex);
            _lines.Items.Clear();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) _lines.Items.Add(line);
            if (_lines.Items.Count > 0) _lines.SelectedIndex = Math.Min(at, _lines.Items.Count - 1);
        }
    }
}
