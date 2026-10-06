using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Shows validation problems and generator notes. With canContinue, offers "Generate anyway".</summary>
    sealed class IssuesDialog : Form
    {
        public IssuesDialog(string heading, IEnumerable<string> errors, IEnumerable<string> warnings, bool canContinue, string continueText = "Generate anyway")
        {
            Text = "DMR Codeplug Builder";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(Ui.S(640), Ui.S(420));
            MinimumSize = new Size(Ui.S(420), Ui.S(260));
            Padding = new Padding(Ui.S(10));

            var errs = errors.ToList();
            var warns = warnings.ToList();
            var lines = new List<string>();
            if (errs.Count > 0)
            {
                lines.Add("MUST FIX (" + errs.Count + ")");
                lines.AddRange(errs.Select(e => "  - " + e));
                lines.Add("");
            }
            if (warns.Count > 0)
            {
                lines.Add("CHECK (" + warns.Count + ")");
                lines.AddRange(warns.Select(w => "  - " + w));
            }

            var head = new Label { Text = heading, AutoSize = false, Dock = DockStyle.Top, Font = Ui.BoldFont, Padding = new Padding(0, 0, 0, Ui.S(8)) };
            void SizeHead()
            {
                int w = Math.Max(Ui.S(200), ClientSize.Width - Padding.Horizontal);
                head.Height = TextRenderer.MeasureText(heading, Ui.BoldFont, new Size(w, 0), TextFormatFlags.WordBreak).Height + Ui.S(10);
            }
            Resize += (s, e) => SizeHead();
            Load += (s, e) => SizeHead();
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Text = string.Join("\r\n", lines),
                BackColor = SystemColors.Window,
                WordWrap = true,
            };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, Ui.S(8), 0, 0) };
            if (canContinue && errs.Count == 0)
            {
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var go = new Button { Text = continueText, DialogResult = DialogResult.OK, AutoSize = true, Font = Ui.BoldFont };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(go);
                AcceptButton = go;
                CancelButton = cancel;
            }
            else
            {
                var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
                buttons.Controls.Add(close);
                AcceptButton = close;
                CancelButton = close;
            }
            Controls.Add(box);
            Controls.Add(head);
            Controls.Add(buttons);
            box.BringToFront();
            ActiveControl = (Control)AcceptButton; // keep focus off the text so nothing is pre-selected
            Shown += (s, e) => { box.SelectionStart = 0; box.SelectionLength = 0; };
        }

        public static void ShowIssues(IWin32Window owner, List<Issue> issues)
        {
            var errors = issues.Where(i => i.Severity == Severity.Error).Select(i => i.Message);
            var warnings = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message);
            string head = issues.Count == 0 ? "No problems found." : "Things to look at before generating:";
            using (var d = new IssuesDialog(head, errors, warnings, false)) d.ShowDialog(owner);
        }
    }
}
