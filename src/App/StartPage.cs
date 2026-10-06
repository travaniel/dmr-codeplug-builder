using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CodeplugBuilder.App
{
    /// <summary>First screen when there's no project to reopen: set up a new codeplug, import one, or open a project.</summary>
    sealed class StartPage : UserControl
    {
        public event EventHandler NewCodeplug, ImportCps, OpenProject, EmptyProject;
        public event EventHandler<string> OpenRecent;

        readonly LinkLabel lnkRecent;
        string recentPath;

        public StartPage()
        {
            Font = Ui.BaseFont;
            BackColor = SystemColors.Window;

            var stack = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Anchor = AnchorStyles.None, Padding = new Padding(Ui.S(20)) };
            var title = new Label { Text = Ui.AppName, AutoSize = true, Font = new Font(Ui.BaseFont.FontFamily, Ui.BaseFont.Size + 9f, FontStyle.Bold), Margin = new Padding(3, 0, 3, Ui.S(4)) };
            var sub = Ui.Hint("Codeplugs for the BTECH DMR-6X2 PRO, loaded into the CPS with Tool > Import > Import From File List.", Ui.S(560));
            sub.Margin = new Padding(3, 0, 3, Ui.S(18));
            stack.Controls.Add(title);
            stack.Controls.Add(sub);

            stack.Controls.Add(Choice("Set up a new codeplug",
                "Pick your area on a map. DMR repeaters and their talkgroups come from RadioID.net and BrandMeister, then you choose zones and talkgroups.",
                (s, e) => NewCodeplug?.Invoke(this, EventArgs.Empty), true));
            stack.Controls.Add(Choice("Import my current codeplug",
                "In the CPS: Tool > Export > Export All (Default CSV FileName). Then pick the .LST it wrote.",
                (s, e) => ImportCps?.Invoke(this, EventArgs.Empty), false));
            stack.Controls.Add(Choice("Open a project",
                "A .cpb file saved by this program.",
                (s, e) => OpenProject?.Invoke(this, EventArgs.Empty), false));

            lnkRecent = new LinkLabel { AutoSize = true, Margin = new Padding(3, Ui.S(14), 3, 3), Visible = false };
            lnkRecent.LinkClicked += (s, e) => { if (recentPath != null) OpenRecent?.Invoke(this, recentPath); };
            stack.Controls.Add(lnkRecent);
            var lnkEmpty = new LinkLabel { Text = "Start with an empty project instead", AutoSize = true, Margin = new Padding(3, Ui.S(6), 3, 3) };
            lnkEmpty.LinkClicked += (s, e) => EmptyProject?.Invoke(this, EventArgs.Empty);
            stack.Controls.Add(lnkEmpty);

            // Centered, and scrollable when the window is small.
            var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, AutoScroll = true };
            center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            center.Controls.Add(stack, 0, 0);
            Controls.Add(center);
            VisibleChanged += (s, e) => { if (Visible) ShowRecent(); };
        }

        Button Choice(string title, string text, EventHandler click, bool primary)
        {
            var b = new ChoiceButton(title, text, primary)
            {
                Width = Ui.S(560),
                Margin = new Padding(3, 3, 3, Ui.S(10)),
            };
            b.FitHeight();
            b.Click += click;
            return b;
        }

        /// <summary>A big button with a bold title line and a plain description under it.</summary>
        sealed class ChoiceButton : Button
        {
            readonly string title, description;
            readonly bool primary;
            static readonly Font TitleFont = new Font(Ui.BaseFont.FontFamily, Ui.BaseFont.Size + 2f, FontStyle.Bold);

            public ChoiceButton(string title, string description, bool primary)
            {
                this.title = title;
                this.description = description;
                this.primary = primary;
                Text = "";                 // drawn in OnPaint
                AccessibleName = title;
                AccessibleDescription = description;
                AutoSize = false;
                UseVisualStyleBackColor = true;
                Padding = new Padding(Ui.S(14), Ui.S(8), Ui.S(14), Ui.S(8));
            }

            int TextWidth => Width - Padding.Horizontal;
            const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

            public void FitHeight()
            {
                int h1 = TextRenderer.MeasureText(title, TitleFont, new Size(TextWidth, 0), Flags).Height;
                int h2 = TextRenderer.MeasureText(description, Ui.BaseFont, new Size(TextWidth, 0), Flags).Height;
                Height = h1 + Ui.S(4) + h2 + Padding.Vertical + Ui.S(6);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var r = new Rectangle(Padding.Left, Padding.Top, TextWidth, Height - Padding.Vertical);
                int h1 = TextRenderer.MeasureText(title, TitleFont, new Size(TextWidth, 0), Flags).Height;
                var titleColor = !Enabled ? SystemColors.GrayText : primary ? Color.FromArgb(20, 70, 150) : SystemColors.ControlText;
                TextRenderer.DrawText(e.Graphics, title, TitleFont, new Rectangle(r.X, r.Y, r.Width, h1), titleColor, Flags);
                TextRenderer.DrawText(e.Graphics, description, Ui.BaseFont, new Rectangle(r.X, r.Y + h1 + Ui.S(4), r.Width, r.Height - h1), Ui.HintColor, Flags);
            }
        }

        void ShowRecent()
        {
            recentPath = AppSettings.Get("LastProject");
            bool show = recentPath != null && File.Exists(recentPath);
            lnkRecent.Visible = show;
            if (show) lnkRecent.Text = "Open " + Path.GetFileName(recentPath) + " (last used)";
        }
    }
}
