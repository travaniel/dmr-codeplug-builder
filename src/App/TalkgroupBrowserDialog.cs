using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Search BrandMeister's talkgroup list and pick talkgroups to add to the project.</summary>
    sealed class TalkgroupBrowserDialog : Form
    {
        readonly Project project;
        readonly TextBox txtSearch;
        readonly ListView list;
        readonly Label lblStatus;
        readonly Button btnAdd;
        readonly HashSet<int> picked = new HashSet<int>();
        Dictionary<int, string> names = new Dictionary<int, string>();
        bool loading, creatingHandle;

        /// <summary>Talkgroups to add (names already shortened to 16 characters).</summary>
        public List<Talkgroup> Selected { get; } = new List<Talkgroup>();

        public TalkgroupBrowserDialog(Project project)
        {
            this.project = project;
            Text = "BrandMeister talkgroups";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(Ui.S(720), Ui.S(640));
            MinimumSize = new Size(Ui.S(520), Ui.S(400));
            Padding = new Padding(Ui.S(10));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(Ui.Hint("Every talkgroup on the BrandMeister network. Search by name or number, tick the ones you want, and they're added to your talkgroup list " +
                                      "(names cut to the radio's 16 characters).", Ui.S(660)), 0, 0);

            txtSearch = new TextBox { Width = Ui.S(260), Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) };
            Ui.SetCue(txtSearch, "e.g. Texas, 3148, SOTA");
            lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.HintColor, Margin = new Padding(3, Ui.S(7), 3, 3), Text = "Loading..." };
            root.Controls.Add(Ui.Row(Ui.Label("Search"), txtSearch, lblStatus), 0, 1);

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false, Margin = new Padding(3, 6, 3, 6) };
            Ui.DoubleBuffer(list);
            list.Columns.Add("ID", Ui.S(80), HorizontalAlignment.Right);
            list.Columns.Add("BrandMeister name", Ui.S(300));
            list.Columns.Add("Name in your codeplug", Ui.S(170));
            list.Columns.Add("", Ui.S(90));
            root.Controls.Add(list, 0, 2);

            btnAdd = new Button { Text = "Add", AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(Ui.S(10), Ui.S(3), Ui.S(10), Ui.S(3)), UseVisualStyleBackColor = true, Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Ui.S(6), Ui.S(3), Ui.S(6), Ui.S(3)), UseVisualStyleBackColor = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Ui.S(6), 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(btnAdd);
            root.Controls.Add(buttons, 0, 3);
            Controls.Add(root);
            CancelButton = cancel;

            txtSearch.TextChanged += (s, e) => Refill();
            btnAdd.Click += (s, e) => Finish();
            list.HandleCreated += (s, e) => { creatingHandle = true; list.BeginInvoke((Action)(() => creatingHandle = false)); };
            list.ItemChecked += (s, e) =>
            {
                if (loading || creatingHandle) return;
                int id = (int)e.Item.Tag;
                if (e.Item.Checked) picked.Add(id); else picked.Remove(id);
                UpdateButton();
            };
            Shown += async (s, e) =>
            {
                UseWaitCursor = true;
                names = await Online.BrandMeisterNamesAsync();
                if (IsDisposed) return;
                UseWaitCursor = false;
                if (names.Count == 0)
                {
                    lblStatus.Text = "Couldn't download BrandMeister's list. Check your internet connection.";
                    return;
                }
                Refill();
                txtSearch.Focus();
            };
        }

        void Refill()
        {
            var terms = txtSearch.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = names
                .Where(kv => terms.All(t => kv.Value.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            kv.Key.ToString(CultureInfo.InvariantCulture).StartsWith(t, StringComparison.Ordinal)))
                .OrderBy(kv => kv.Key)
                .Take(1000)
                .ToList();
            loading = true;
            try
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var kv in rows)
                {
                    var existing = project.FindTalkgroup(kv.Key);
                    var item = new ListViewItem(kv.Key.ToString(CultureInfo.InvariantCulture)) { Tag = kv.Key, Checked = picked.Contains(kv.Key) };
                    item.SubItems.Add(kv.Value);
                    item.SubItems.Add(existing != null ? existing.Name : ShortName(kv.Key, kv.Value));
                    item.SubItems.Add(existing != null ? "in your list" : "");
                    if (existing != null) item.ForeColor = SystemColors.GrayText;
                    list.Items.Add(item);
                }
                list.EndUpdate();
            }
            finally { loading = false; }
            lblStatus.Text = rows.Count >= 1000 ? "Showing the first 1000 of " + names.Count + "; type to narrow it down." : rows.Count + " of " + names.Count + " talkgroups";
            UpdateButton();
        }

        static string ShortName(int id, string name)
        {
            string n = BrandMeister.ShortName(name);
            return n.Length > 0 ? n : "TG " + id.ToString(CultureInfo.InvariantCulture);
        }

        void UpdateButton()
        {
            int n = picked.Count(id => project.FindTalkgroup(id) == null);
            btnAdd.Enabled = n > 0;
            btnAdd.Text = n > 0 ? "Add " + n + " talkgroup" + (n == 1 ? "" : "s") : "Add";
        }

        void Finish()
        {
            foreach (int id in picked.OrderBy(x => x))
            {
                if (project.FindTalkgroup(id) != null || !names.TryGetValue(id, out string name)) continue;
                string n = ShortName(id, name);
                Selected.Add(new Talkgroup(n, id, BrandMeister.IsPrivateCall(id, n) ? CallTypes.Private : CallTypes.Group));
            }
            DialogResult = DialogResult.OK;
        }
    }
}
