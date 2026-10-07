using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// The audio device setup and removal window: a status line, a progress bar
    /// and one button. Runs in the elevated copy started by the main window, so
    /// one administrator prompt covers everything it does.
    /// Every step says what it is doing, and NVDA says it too.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private readonly Label _status = new() { AutoSize = false, Width = 420, Height = 60 };
        private readonly ProgressBar _bar = new() { Width = 420, Height = 22, Maximum = 100 };
        private readonly Button _button = new() { Text = "Cancel", AutoSize = true };
        private readonly CancellationTokenSource _cancel = new();
        private bool _done;
        private readonly Func<Action<string, int>, CancellationToken, Task<string>> _work;

        public int Result { get; private set; } = 1;

        /// <summary>work reports (text, percent or -1) and returns the sentence to finish with.</summary>
        public SetupForm(string title, Func<Action<string, int>, CancellationToken, Task<string>> work)
        {
            _work = work;
            Text = title;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12) };
            _bar.AccessibleName = "Progress";
            panel.Controls.Add(_status);
            panel.Controls.Add(_bar);
            panel.Controls.Add(_button);
            Controls.Add(panel);
            CancelButton = _button;
            _button.Click += (_, _) => { if (_done) Close(); else _cancel.Cancel(); };
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _button.Focus();
            try
            {
                string finished = await _work(Report, _cancel.Token);
                _bar.Style = ProgressBarStyle.Blocks;
                _bar.Value = 100;
                Say(finished);
                Result = 0;
            }
            catch (OperationCanceledException) { Say("Cancelled."); }
            catch (Exception ex) { Say(ex.Message); }
            _done = true;
            _button.Text = "Close";
        }

        /// <summary>Status text, and a percentage (or -1 for "working, no percentage").</summary>
        private void Report(string text, int percent)
        {
            if (InvokeRequired) { BeginInvoke(() => Report(text, percent)); return; }
            if (percent < 0) _bar.Style = ProgressBarStyle.Marquee;
            else { _bar.Style = ProgressBarStyle.Blocks; _bar.Value = Math.Clamp(percent, 0, 100); }
            if (text != _status.Text) Say(text);
        }

        private void Say(string text)
        {
            _status.Text = text;
            _status.AccessibleName = text;
            Speech.Speak(text);
        }
    }
}
