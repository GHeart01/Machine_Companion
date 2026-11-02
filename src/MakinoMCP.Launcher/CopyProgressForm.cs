using System;
using System.Drawing;
using System.Windows.Forms;

namespace MakinoMCP.Launcher
{
    // "Preparing MakinoMCP on this PC..." with a progress bar. No buttons; it closes itself when the copy ends.
    // System colours and fonts only, so it follows the classic light theme and High Contrast.
    internal sealed class CopyProgressForm : Form
    {
        private const int Steps = 1000;
        private readonly ProgressBar _bar;
        private long _lastTick;
        private volatile bool _finished;

        public CopyProgressForm()
        {
            Text = "MakinoMCP";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(20),
            };
            var label = new Label
            {
                Text = "Preparing MakinoMCP on this PC…",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12),
            };
            _bar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                Maximum = Steps,
                Width = 320,
                Height = 18,
                Margin = new Padding(0),
                AccessibleName = "Copying MakinoMCP to this PC",
                AccessibleRole = AccessibleRole.ProgressBar,
            };
            layout.Controls.Add(label, 0, 0);
            layout.Controls.Add(_bar, 0, 1);
            Controls.Add(layout);
        }

        // Called from the worker thread; throttled so a fast copy does not flood the UI thread.
        public void Report(long done, long total)
        {
            long now = Environment.TickCount;
            if (total <= 0 || (now - _lastTick < 80 && done < total)) return;
            _lastTick = now;
            int value = (int)Math.Min(Steps, done * Steps / total);
            Post(() =>
            {
                if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = value;
            });
        }

        public void Finish()
        {
            _finished = true;
            Post(Close);
        }

        // Alt+F4 and the like cannot end the window while the copy runs; Windows shutdown and Task Manager still can.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_finished && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing)
                e.Cancel = true;
            base.OnFormClosing(e);
        }

        private void Post(Action a)
        {
            try { if (IsHandleCreated) BeginInvoke(a); } catch (InvalidOperationException) { }
        }
    }
}
