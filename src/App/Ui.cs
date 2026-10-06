using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodeplugBuilder.App
{
    /// <summary>Small helpers so every page is built the same way in code (no designer files).</summary>
    static class Ui
    {
        public const string AppName = "W6OZZ CPS";

        /// <summary>Screen DPI / 96, set at startup; used to scale the few fixed pixel sizes.</summary>
        public static float Scale = 1f;

        public static int S(int pixels) { return (int)Math.Round(pixels * Scale); }

        public static readonly Font BaseFont = SafeFont();
        public static readonly Font BoldFont = new Font(BaseFont, FontStyle.Bold);
        public static readonly Font HeadingFont = new Font(BaseFont.FontFamily, BaseFont.Size + 2f, FontStyle.Bold);
        public static readonly Color HintColor = Color.FromArgb(96, 96, 96);
        public static readonly Color AutoNameColor = Color.FromArgb(120, 120, 120);
        public static readonly Color ErrorBack = Color.FromArgb(255, 228, 228);
        /// <summary>Talkgroups a repeater carries because of its zone's set.</summary>
        public static readonly Color ZoneColor = Color.FromArgb(30, 90, 160);

        static Font SafeFont()
        {
            try
            {
                var f = new Font("Segoe UI", 9f);
                if (f.Name == "Segoe UI") return f;
            }
            catch { }
            return SystemFonts.MessageBoxFont;
        }

        public static Label Label(string text, bool bold = false)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 7, 6, 3),
                Font = bold ? BoldFont : BaseFont,
            };
        }

        public static Label Hint(string text, int maxWidth = 0)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = HintColor,
                Margin = new Padding(3, 2, 3, 6),
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
            };
            if (maxWidth > 0) l.MaximumSize = new Size(maxWidth, 0);
            return l;
        }

        public static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = HeadingFont, Margin = new Padding(3, 4, 3, 6) };
        }

        public static Button Button(string text, EventHandler onClick, int minWidth = 0)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6, 2, 6, 2),
                Margin = new Padding(3),
                UseVisualStyleBackColor = true,
            };
            if (minWidth > 0) b.MinimumSize = new Size(minWidth, 0);
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0),
                Dock = DockStyle.Fill,
            };
            p.Controls.AddRange(controls);
            return p;
        }

        public static ComboBox Combo(bool editable, params string[] items)
        {
            var c = new ComboBox
            {
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(3, 3, 12, 3),
            };
            c.Items.AddRange(items);
            return c;
        }

        public static TextBox Text(string cue = null)
        {
            var t = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 3, 12, 3) };
            if (cue != null) SetCue(t, cue);
            return t;
        }

        public static TableLayoutPanel Grid(int columns)
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = columns,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            return t;
        }

        /// <summary>Gray placeholder text in an empty TextBox (Windows only; ignored elsewhere).</summary>
        public static void SetCue(TextBox box, string cue)
        {
            void Apply()
            {
                try { SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, cue); } catch { }
            }
            if (box.IsHandleCreated) Apply(); else box.HandleCreated += (s, e) => Apply();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Parses "146.94", "146,940" or "146.940.0"-free input. Returns null when it isn't a number.</summary>
        public static decimal? ParseMHz(string text)
        {
            string s = (text ?? "").Trim().Replace(',', '.');
            if (s.Length == 0) return null;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) && v > 0 && v < 10000)
                return Math.Round(v, 5);
            return null;
        }

        public static string FormatMHz(decimal v)
        {
            return v <= 0 ? "" : v.ToString("0.000##", CultureInfo.InvariantCulture);
        }

        public static void Info(IWin32Window owner, string text, string title = Ui.AppName)
        {
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Error(IWin32Window owner, string text, string title = Ui.AppName)
        {
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static bool Confirm(IWin32Window owner, string text, string title = Ui.AppName)
        {
            return MessageBox.Show(owner, text, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        /// <summary>
        /// Sets a SplitContainer's splitter once it has its real size (setting it earlier is ignored or
        /// throws because a docked container starts out tiny).
        /// </summary>
        public static void InitSplitter(SplitContainer split, int distance, int panel1Min, int panel2Min)
        {
            bool done = false;
            EventHandler apply = null;
            apply = (s, e) =>
            {
                if (done || split.Width < distance + panel2Min + split.SplitterWidth) return;
                done = true;
                try
                {
                    split.SplitterDistance = distance;
                    split.Panel1MinSize = panel1Min;
                    split.Panel2MinSize = panel2Min;
                }
                catch { }
                split.SizeChanged -= apply;
            };
            split.SizeChanged += apply;
            split.HandleCreated += apply;
        }

        /// <summary>
        /// Grid defaults: the header row sizes itself to the font (the fixed default height clips the header
        /// text at 125%+ scaling on Windows), and double buffering.
        /// </summary>
        public static void SetUpGrid(DataGridView g)
        {
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            g.RowTemplate.Height = Math.Max(g.RowTemplate.Height, TextRenderer.MeasureText("Ag", g.Font).Height + S(6));
            DoubleBuffer(g);
        }

        /// <summary>Turns on double buffering for controls that flicker when repainted often.</summary>
        public static void DoubleBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(c, true, null);
            }
            catch { }
        }
    }
}
