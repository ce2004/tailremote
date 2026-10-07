using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// The audio device setup window: a status line, a progress bar and one
    /// button. Runs in the elevated copy started by Set up audio device, so the
    /// one administrator prompt covers VB-Cable's installer and the renaming.
    /// Every step says what it is doing, and NVDA says it too.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private readonly Label _status = new() { AutoSize = false, Width = 420, Height = 60 };
        private readonly ProgressBar _bar = new() { Width = 420, Height = 22, Maximum = 100 };
        private readonly Button _button = new() { Text = "Cancel", AutoSize = true };
        private readonly CancellationTokenSource _cancel = new();
        private bool _done;

        public int Result { get; private set; } = 1;

        public SetupForm()
        {
            Text = "Setting up the TailRemote audio device";
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
                await AudioSetup.RunAsync(Report, _cancel.Token);
                _bar.Style = ProgressBarStyle.Blocks;
                _bar.Value = 100;
                Say("Done. The TailRemote audio device is ready and is the default output.");
                Result = 0;
            }
            catch (OperationCanceledException) { Say("Setup was cancelled. Nothing else was changed."); }
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
