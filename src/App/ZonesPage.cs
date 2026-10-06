using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Zone order, renaming, and the A/B channels each zone opens on.</summary>
    sealed class ZonesPage : UserControl
    {
        readonly Session session;
        readonly ListView list;
        readonly ComboBox cboA, cboB;
        readonly ListView lstMembers;
        readonly Label lblZone;
        readonly TableLayoutPanel detail;
        readonly ZoneTalkgroupsEditor editor;
        readonly TabControl detailTabs;
        bool loading, editing, bmLoaded;
        GeneratedCodeplug preview;

        public ZonesPage(Session session)
        {
            this.session = session;
            Font = Ui.BaseFont;
            Padding = new Padding(Ui.S(10), Ui.S(8), Ui.S(10), Ui.S(8));

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            top.Controls.Add(Ui.Heading("Zones"));
            top.Controls.Add(Ui.Hint("Zones are built automatically: every repeater goes into the zone named on its Zone field, in the order " +
                                     "of the Repeaters list. Here you set the order zones appear on the radio, rename them, and pick talkgroups for a whole zone at once.", Ui.S(900)));

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = Ui.S(6) };

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Zone", Ui.S(150));
            list.Columns.Add("Repeaters", Ui.S(80), HorizontalAlignment.Right);
            list.Columns.Add("Channels", Ui.S(80), HorizontalAlignment.Right);
            var leftButtons = Ui.Row(
                Ui.Button("Up", (s, e) => MoveItem(-1)),
                Ui.Button("Down", (s, e) => MoveItem(1)),
                Ui.Button("Rename...", (s, e) => Rename()));
            leftButtons.Dock = DockStyle.Bottom;
            split.Panel1.Controls.Add(list);
            split.Panel1.Controls.Add(leftButtons);
            list.BringToFront();

            detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(Ui.S(8), 0, 0, 0) };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblZone = Ui.Heading("");
            cboA = Ui.Combo(false);
            cboB = Ui.Combo(false);
            lstMembers = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            lstMembers.ShowItemToolTips = true;
            lstMembers.Columns.Add("No.", Ui.S(50), HorizontalAlignment.Right);
            lstMembers.Columns.Add("Channel", Ui.S(150));
            lstMembers.Columns.Add("Talkgroup", Ui.S(150));
            lstMembers.Columns.Add("Slot", Ui.S(50));
            lstMembers.Columns.Add("RX MHz", Ui.S(80), HorizontalAlignment.Right);
            editor = new ZoneTalkgroupsEditor { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(4)) };
            detailTabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Ui.S(12), Ui.S(4)) };
            var tgPage = new TabPage("Talkgroups") { UseVisualStyleBackColor = true };
            tgPage.Controls.Add(editor);
            var chPage = new TabPage("Channels") { UseVisualStyleBackColor = true, Padding = new Padding(Ui.S(4)) };
            chPage.Controls.Add(lstMembers);
            detailTabs.TabPages.Add(tgPage);
            detailTabs.TabPages.Add(chPage);
            detail.Controls.Add(lblZone, 0, 0); detail.SetColumnSpan(lblZone, 2);
            detail.Controls.Add(Ui.Label("A side opens on"), 0, 1); detail.Controls.Add(cboA, 1, 1);
            detail.Controls.Add(Ui.Label("B side opens on"), 0, 2); detail.Controls.Add(cboB, 1, 2);
            detail.Controls.Add(detailTabs, 0, 3); detail.SetColumnSpan(detailTabs, 2);
            for (int i = 0; i < 3; i++) detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Panel2.Controls.Add(detail);

            Controls.Add(split);
            Controls.Add(top);
            split.BringToFront();
            Ui.InitSplitter(split, Ui.S(360), Ui.S(260), Ui.S(300));

            list.SelectedIndexChanged += (s, e) => { if (!loading) ShowDetail(); };
            list.DoubleClick += (s, e) => Rename();
            cboA.SelectedIndexChanged += (s, e) => SetSide(true);
            cboB.SelectedIndexChanged += (s, e) => SetSide(false);
            editor.Changed += (s, e) =>
            {
                session.NotifyTalkgroupsChanged(); // new talkgroups, and repeaters' channel lists changed
                editing = true;
                try { Reload(SelectedZone?.Name); } finally { editing = false; }
            };
            VisibleChanged += async (s, e) =>
            {
                if (!Visible) return;
                Reload(SelectedZone?.Name);
                if (!bmLoaded) { bmLoaded = true; editor.SetBrandMeister(await Online.BrandMeisterNamesAsync()); }
            };
            session.Replaced += (s, e) => { if (Visible) Reload(null); };
        }

        ZoneInfo SelectedZone => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as ZoneInfo : null;

        public void Reload(string select)
        {
            loading = true;
            try
            {
                session.Project.SyncZones();
                try { preview = CodeplugGenerator.Generate(session.Project, session.Format); }
                catch { preview = null; }

                list.BeginUpdate();
                list.Items.Clear();
                foreach (var z in session.Project.Zones)
                {
                    int reps = session.Project.ActiveRepeaters().Count(r => Project.SameZone(r.Zone, z.Name));
                    int chans = preview?.ChannelList.Count(c => Project.SameZone(c.Zone, z.Name)) ?? 0;
                    var item = new ListViewItem(z.Name) { Tag = z };
                    item.SubItems.Add(reps.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(chans == 0 ? "none" : chans.ToString(CultureInfo.InvariantCulture));
                    if (chans == 0) item.ForeColor = SystemColors.GrayText;
                    list.Items.Add(item);
                    if (select != null && Project.SameZone(z.Name, select)) item.Selected = true;
                }
                if (list.SelectedItems.Count == 0 && list.Items.Count > 0)
                    (list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.ForeColor != SystemColors.GrayText) ?? list.Items[0]).Selected = true; // first zone with channels
                list.EndUpdate();
            }
            finally { loading = false; }
            ShowDetail();
        }

        List<string> Members(string zone)
        {
            if (preview == null) return new List<string>();
            return preview.ChannelList.Where(c => Project.SameZone(c.Zone, zone)).Select(c => c.Name).ToList();
        }

        void ShowDetail()
        {
            var z = SelectedZone;
            detail.Visible = z != null;
            if (z == null) return;
            loading = true;
            try
            {
                var members = Members(z.Name);
                lblZone.Text = z.Name;
                cboA.BeginUpdate(); cboB.BeginUpdate();
                cboA.Items.Clear(); cboB.Items.Clear();
                foreach (var m in members) { cboA.Items.Add(m); cboB.Items.Add(m); }
                cboA.EndUpdate(); cboB.EndUpdate();
                var gz = preview?.ZoneList.FirstOrDefault(x => Project.SameZone(x.Name, z.Name));
                cboA.SelectedItem = gz?.AChannel;
                cboB.SelectedItem = gz?.BChannel;
                lstMembers.BeginUpdate();
                lstMembers.Items.Clear();
                foreach (var c in preview?.ChannelList.Where(c => Project.SameZone(c.Zone, z.Name)) ?? Enumerable.Empty<GeneratedChannel>())
                {
                    var item = new ListViewItem(c.Number.ToString(CultureInfo.InvariantCulture));
                    if (c.StoredNumber != c.Number) item.ToolTipText = "New channel: it keeps this number once you generate.";
                    item.SubItems.Add(c.Name);
                    item.SubItems.Add(c.IsDigital ? c.Talkgroup.Name : "FM" + (c.Repeater.RxOnly ? " (RX only)" : ""));
                    item.SubItems.Add(c.IsDigital ? c.Entry.Slot.ToString(CultureInfo.InvariantCulture) : "");
                    item.SubItems.Add(c.Repeater.RxMHz.ToString("0.000", CultureInfo.InvariantCulture));
                    lstMembers.Items.Add(item);
                }
                if (members.Count == 0) lstMembers.Items.Add(new ListViewItem(new[] { "", "(no channels: its repeaters are switched off or have no talkgroups)" }));
                lstMembers.EndUpdate();
                // The editor refreshes itself after its own edits; rebinding then would lose the grid's selection.
                if (!editing || editor.Zone == null || !Project.SameZone(editor.Zone, z.Name)) editor.Bind(session.Project, z.Name);
            }
            finally { loading = false; }
        }

        void SetSide(bool a)
        {
            if (loading) return;
            var z = SelectedZone;
            if (z == null) return;
            var box = a ? cboA : cboB;
            if (a) z.AChannel = box.SelectedItem as string; else z.BChannel = box.SelectedItem as string;
            session.MarkDirty();
        }

        void MoveItem(int delta)
        {
            var z = SelectedZone;
            if (z == null) return;
            var zones = session.Project.Zones;
            int i = zones.IndexOf(z), j = i + delta;
            if (j < 0 || j >= zones.Count) return;
            zones.RemoveAt(i);
            zones.Insert(j, z);
            session.MarkDirty();
            Reload(z.Name);
        }

        void Rename()
        {
            var z = SelectedZone;
            if (z == null) return;
            string name = Prompt.Show(FindForm(), "Rename zone", "New name for zone \"" + z.Name + "\" (16 characters max):", z.Name, 16);
            if (name == null) return;
            name = Naming.Clean(name, 16);
            if (name.Length == 0 || name == z.Name) return;
            if (session.Project.FindZone(name) != null && !Project.SameZone(name, z.Name) &&
                !Ui.Confirm(FindForm(), "A zone called \"" + name + "\" already exists. Merge \"" + z.Name + "\" into it?"))
                return;
            session.Project.RenameZone(z.Name, name);
            session.MarkDirty();
            Reload(name);
        }
    }

    /// <summary>A minimal one-line text prompt.</summary>
    static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string label, string value, int maxLength = 0)
        {
            using (var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = Ui.BaseFont,
                Padding = new Padding(Ui.S(10)),
            })
            {
                var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 3, 3, 6) });
                var box = new TextBox { Text = value ?? "", Width = Ui.S(320), MaxLength = maxLength > 0 ? maxLength : 32767 };
                t.Controls.Add(box);
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
                row.Controls.Add(cancel);
                row.Controls.Add(ok);
                t.Controls.Add(row);
                f.Controls.Add(t);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
