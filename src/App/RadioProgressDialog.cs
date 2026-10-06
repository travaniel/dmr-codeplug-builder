using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// A small modal window with a progress bar while radio work runs in the background. It can't be closed
    /// halfway: a write must never be interrupted.
    /// </summary>
    sealed class RadioProgressDialog : Form
    {
        readonly Label text = new Label { AutoSize = true, Margin = new Padding(3, 3, 3, 8) };
        readonly ProgressBar bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill };
        bool done;

        RadioProgressDialog(string title, string message)
        {
            Text = title;
            Font = Ui.BaseFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.S(420), Ui.S(110));
            text.Text = message;
            var grid = Ui.Grid(1);
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(Ui.S(14));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(22)));
            grid.Controls.Add(text, 0, 0);
            grid.Controls.Add(bar, 0, 1);
            Controls.Add(grid);
            FormClosing += (s, e) => { if (!done) e.Cancel = true; };
        }

        void Report(ReadProgress p)
        {
            if (p.Total <= 0) return;
            bar.Style = ProgressBarStyle.Continuous;
            bar.Maximum = p.Total;
            bar.Value = Math.Min(p.Done, p.Total);
            if (!string.IsNullOrEmpty(p.What)) text.Text = Capitalize(p.What) + " (" + p.Done + " of " + p.Total + ")";
        }

        static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>Runs <paramref name="work"/> off the UI thread with this dialog up; rethrows its exception.</summary>
        public static T Run<T>(IWin32Window owner, string title, Func<IProgress<ReadProgress>, T> work, string message = "Talking to the radio. Don't touch the radio or unplug the cable.")
        {
            using (var d = new RadioProgressDialog(title, message))
            {
                var progress = new Progress<ReadProgress>(d.Report);
                T result = default(T);
                Exception error = null;
                d.Shown += async (s, e) =>
                {
                    try { result = await Task.Run(() => work(progress)); }
                    catch (Exception ex) { error = ex; }
                    d.done = true;
                    d.Close();
                };
                d.ShowDialog(owner);
                if (error != null) throw error;
                return result;
            }
        }
    }
}
