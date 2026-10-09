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
        readonly TabPage tgPage, rulePage;
        readonly CheckedListBox lstRuleTalkgroups, lstRuleZones;
        readonly NumericUpDown numRuleMiles;
        readonly Button btnDelete;
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
                                     "of the Repeaters list. Here you set the order zones appear on the radio, rename them, and pick talkgroups for a whole zone at once. " +
                                     "A channel can also be in more zones: a Favorites zone holds channels you pick, a Talkgroup zone every channel of some talkgroups.", Ui.S(900)));

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = Ui.S(6) };

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Zone", Ui.S(140));
            list.Columns.Add("Kind", Ui.S(75));
            list.Columns.Add("Repeaters", Ui.S(70), HorizontalAlignment.Right);
            list.Columns.Add("Channels", Ui.S(70), HorizontalAlignment.Right);
            var newMenu = new ContextMenuStrip();
            newMenu.Items.Add("Favorites zone (channels you pick)...", null, (s, e) => NewZone(ZoneKinds.Favorites));
            newMenu.Items.Add("Talkgroup zone (every channel of some talkgroups)...", null, (s, e) => NewZone(ZoneKinds.Talkgroup));
            Button btnNew = null;
            btnNew = Ui.Button("New zone...", (s, e) => newMenu.Show(btnNew, new Point(0, btnNew.Height)));
            btnDelete = Ui.Button("Delete", (s, e) => DeleteZone());
            var leftButtons = Ui.Row(
                Ui.Button("Up", (s, e) => MoveItem(-1)),
                Ui.Button("Down", (s, e) => MoveItem(1)),
                Ui.Button("Rename...", (s, e) => Rename()),
                btnNew, btnDelete,
                Ui.Button("Sort by distance", (s, e) => SortZones()));
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
            lstMembers.Columns.Add("In this zone because", Ui.S(170));
            lstMembers.MultiSelect = false;
            lstMembers.HideSelection = false;
            editor = new ZoneTalkgroupsEditor { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(4)) };
            detailTabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Ui.S(12), Ui.S(4)) };
            tgPage = new TabPage("Talkgroups") { UseVisualStyleBackColor = true };
            tgPage.Controls.Add(editor);
            var chPage = new TabPage("Channels") { UseVisualStyleBackColor = true, Padding = new Padding(Ui.S(4)) };
            var memberButtons = Ui.Row(
                Ui.Button("Add channels...", (s, e) => AddChannels()),
                Ui.Button("Remove from zone", (s, e) => RemoveMember()),
                Ui.Button("Move up", (s, e) => MoveMember(-1)),
                Ui.Button("Move down", (s, e) => MoveMember(1)));
            memberButtons.Dock = DockStyle.Bottom;
            chPage.Controls.Add(lstMembers);
            chPage.Controls.Add(memberButtons);
            lstMembers.BringToFront();
            rulePage = new TabPage("Rule") { UseVisualStyleBackColor = true, Padding = new Padding(Ui.S(4)) };
            var rule = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            rule.Controls.Add(Ui.Hint("Every channel that carries one of the ticked talkgroups goes into this zone, nearest repeaters first " +
                                      "when a home town is set (Settings), else in the order of the Repeaters list. Tick zones on the right " +
                                      "to take only those zones' repeaters.", Ui.S(560)), 0, 0);
            rule.SetColumnSpan(rule.GetControlFromPosition(0, 0), 2);
            numRuleMiles = new NumericUpDown { Minimum = 0, Maximum = 2000, Increment = 10, Width = Ui.S(70) };
            var milesRow = Ui.Row(Ui.Label("Only repeaters within"), numRuleMiles, Ui.Label("miles of home (0 = any distance)"));
            milesRow.WrapContents = false;
            rule.Controls.Add(milesRow, 0, 3);
            rule.SetColumnSpan(milesRow, 2);
            numRuleMiles.ValueChanged += (s, e) => { if (!loading) SaveRule(); };
            rule.Controls.Add(Ui.Label("Talkgroups", true), 0, 1);
            rule.Controls.Add(Ui.Label("Only repeaters in these zones (none = all)", true), 1, 1);
            lstRuleTalkgroups = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            lstRuleZones = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            rule.Controls.Add(lstRuleTalkgroups, 0, 2);
            rule.Controls.Add(lstRuleZones, 1, 2);
            rule.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rule.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rule.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rule.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rulePage.Controls.Add(rule);
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
            lstRuleTalkgroups.ItemCheck += (s, e) => { if (!loading) BeginInvoke((Action)SaveRule); };
            lstRuleZones.ItemCheck += (s, e) => { if (!loading) BeginInvoke((Action)SaveRule); };
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
                    int chans = ZoneChannels(z.Name).Count;
                    var item = new ListViewItem(z.Name) { Tag = z };
                    item.SubItems.Add(z.IsRoute ? "Route" : session.Project.ZoneKindOf(z));
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

        /// <summary>The zone's channels as the generator writes them (own repeaters, listed members, rule).</summary>
        List<GeneratedChannel> ZoneChannels(string zone)
        {
            if (preview == null) return new List<GeneratedChannel>();
            var byKey = preview.ChannelList.ToDictionary(c => (object)c.Entry ?? c.Repeater);
            return session.Project.ZoneChannels(zone).Select(c => byKey.TryGetValue(c.Key, out var gc) ? gc : null).Where(c => c != null).ToList();
        }

        List<string> Members(string zone) { return ZoneChannels(zone).Select(c => c.Name).ToList(); }

        /// <summary>Why a channel is in the zone: its repeater's zone, the zone's list, or the talkgroup rule.</summary>
        string Reason(ZoneInfo z, GeneratedChannel c)
        {
            var key = new ChannelRef(c.Repeater, c.Entry);
            bool listed = z.Members != null && z.Members.Any(m => session.Project.Resolve(m)?.Equals(key) == true);
            if (Project.SameZone(c.Repeater.Zone, z.Name)) return "its repeater's zone";
            if (listed) return "added here (its zone: " + (string.IsNullOrWhiteSpace(c.Repeater.Zone) ? "none" : c.Repeater.Zone) + ")";
            return "talkgroup rule";
        }

        GeneratedChannel SelectedMember => lstMembers.SelectedItems.Count > 0 ? lstMembers.SelectedItems[0].Tag as GeneratedChannel : null;

        void NewZone(string kind)
        {
            string name = Prompt.Show(FindForm(), "New " + kind.ToLowerInvariant() + " zone", "Name of the new zone (16 characters max):",
                                      kind == ZoneKinds.Favorites ? "Favorites" : "", 16);
            if (name == null) return;
            name = Naming.Fit(name, 16);
            if (name.Length == 0) return;
            if (session.Project.FindZone(name) != null) { Ui.Error(FindForm(), "There is already a zone called \"" + name + "\"."); return; }
            session.Project.Zones.Add(new ZoneInfo(name) { Kind = kind });
            session.MarkDirty();
            Reload(name);
            if (kind == ZoneKinds.Favorites) AddChannels();
            else detailTabs.SelectedTab = rulePage;
        }

        /// <summary>Hotspot zone, favourites, area zones nearest first, talkgroup zones, then utilities (ZoneOrder).</summary>
        void SortZones()
        {
            var p = session.Project;
            if (p.Home == null &&
                !Ui.Confirm(FindForm(), "No home town is set (Settings tab), so area zones keep their order; only the groups move: hotspot, favorites, " +
                                        "areas, talkgroup zones, then simplex and weather.\n\nSort anyway?"))
                return;
            if (!ZoneOrder.Sort(p)) { Ui.Info(FindForm(), "The zones are already in that order."); return; }
            session.MarkDirty();
            Reload(SelectedZone?.Name);
        }

        void DeleteZone()
        {
            var z = SelectedZone;
            if (z == null) return;
            var own = session.Project.AllRepeaters().Where(r => Project.SameZone(r.Zone, z.Name)).ToList();
            if (own.Count > 0)
            {
                Ui.Info(FindForm(), own.Count + " repeater(s) have \"" + z.Name + "\" as their zone. Give them another zone on the Repeaters tab " +
                                    "(or rename this zone to merge it into another).");
                return;
            }
            if (!Ui.Confirm(FindForm(), "Delete zone \"" + z.Name + "\"? Its channels stay in their own zones.")) return;
            session.Project.Zones.Remove(z);
            session.MarkDirty();
            Reload(null);
        }

        void AddChannels()
        {
            var z = SelectedZone;
            if (z == null) return;
            using (var d = new ZoneMembersDialog(session, z.Name, null))
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK || d.Added == 0) return;
            }
            session.MarkDirty();
            Reload(z.Name);
            detailTabs.SelectedTab = (TabPage)lstMembers.Parent;
        }

        void RemoveMember()
        {
            var z = SelectedZone;
            var c = SelectedMember;
            if (z == null || c == null) return;
            if (Project.SameZone(c.Repeater.Zone, z.Name))
            {
                Ui.Info(FindForm(), "\"" + c.Name + "\" is in this zone because its repeater's zone is \"" + z.Name + "\". " +
                                    "Change the repeater's zone on the Repeaters tab to take it out.");
                return;
            }
            if (!session.Project.RemoveFromZone(z.Name, new ChannelRef(c.Repeater, c.Entry)))
            {
                Ui.Info(FindForm(), "\"" + c.Name + "\" is here because of the zone's talkgroup rule (Rule tab).");
                return;
            }
            session.MarkDirty();
            Reload(z.Name);
        }

        void MoveMember(int delta)
        {
            var z = SelectedZone;
            var c = SelectedMember;
            if (z == null || c == null) return;
            var key = new ChannelRef(c.Repeater, c.Entry);
            var p = session.Project;
            // Pin the current order of the listed and own channels first, so moving works for the zone's own channels too.
            // Channels there only by the talkgroup rule follow the rule and can't be moved.
            var order = ZoneChannels(z.Name).Where(x => Reason(z, x) != "talkgroup rule").Select(x => new ChannelRef(x.Repeater, x.Entry)).ToList();
            if (!order.Contains(key)) return;
            z.Members = order.Select(p.MemberFor).ToList();
            if (!p.MoveZoneMember(z.Name, key, delta)) return;
            session.MarkDirty();
            Reload(z.Name);
            foreach (ListViewItem item in lstMembers.Items)
                if (item.Tag is GeneratedChannel g && g.Repeater == c.Repeater && g.Entry == c.Entry) { item.Selected = true; item.EnsureVisible(); }
        }

        void SaveRule()
        {
            var z = SelectedZone;
            if (z == null || loading) return;
            z.RuleTalkgroups = lstRuleTalkgroups.CheckedItems.Cast<Talkgroup>().Select(t => t.Id).ToList();
            z.RuleZones = lstRuleZones.CheckedItems.Cast<string>().ToList();
            if (z.RuleTalkgroups.Count == 0) z.RuleTalkgroups = null;
            if (z.RuleZones.Count == 0) z.RuleZones = null;
            z.RuleMiles = (double)numRuleMiles.Value;
            if (z.Kind == null) z.Kind = ZoneKinds.Talkgroup;
            session.MarkDirty();
            editing = true;
            try { Reload(z.Name); } finally { editing = false; }
        }

        void ShowDetail()
        {
            var z = SelectedZone;
            detail.Visible = z != null;
            btnDelete.Enabled = z != null && !session.Project.AllRepeaters().Any(r => Project.SameZone(r.Zone, z.Name));
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
                foreach (var c in ZoneChannels(z.Name))
                {
                    var item = new ListViewItem(c.Number.ToString(CultureInfo.InvariantCulture)) { Tag = c };
                    if (c.StoredNumber != c.Number) item.ToolTipText = "New channel: it keeps this number once you generate.";
                    item.SubItems.Add(c.Name);
                    item.SubItems.Add(c.IsDigital ? c.Talkgroup.Name : "FM" + (c.Repeater.RxOnly ? " (RX only)" : ""));
                    item.SubItems.Add(c.IsDigital ? c.Entry.Slot.ToString(CultureInfo.InvariantCulture) : "");
                    item.SubItems.Add(c.Repeater.RxMHz.ToString("0.000", CultureInfo.InvariantCulture));
                    item.SubItems.Add(Reason(z, c));
                    lstMembers.Items.Add(item);
                }
                string kind = session.Project.ZoneKindOf(z);
                if (members.Count == 0)
                    lstMembers.Items.Add(new ListViewItem(new[] { "", kind == ZoneKinds.Favorites ? "(no channels yet: click Add channels...)"
                                                                     : kind == ZoneKinds.Talkgroup ? "(no channels: tick talkgroups on the Rule tab)"
                                                                     : "(no channels: its repeaters are switched off or have no talkgroups)" }));
                lstMembers.EndUpdate();

                // Talkgroup zones show their rule; zone talkgroup sets only make sense for zones with repeaters of their own.
                bool showRule = kind == ZoneKinds.Talkgroup;
                bool showSet = session.Project.ZoneRepeaters(z.Name).Count > 0 || kind == ZoneKinds.Area;
                if (showRule && !detailTabs.TabPages.Contains(rulePage)) detailTabs.TabPages.Add(rulePage);
                if (!showRule && detailTabs.TabPages.Contains(rulePage)) detailTabs.TabPages.Remove(rulePage);
                if (showSet && !detailTabs.TabPages.Contains(tgPage)) detailTabs.TabPages.Insert(0, tgPage);
                if (!showSet && detailTabs.TabPages.Contains(tgPage)) detailTabs.TabPages.Remove(tgPage);
                if (showRule)
                {
                    lstRuleTalkgroups.BeginUpdate();
                    lstRuleTalkgroups.Items.Clear();
                    foreach (var t in session.Project.Talkgroups.Where(t => t.IsGroupCall || (z.RuleTalkgroups?.Contains(t.Id) ?? false)))
                        lstRuleTalkgroups.Items.Add(t, z.RuleTalkgroups?.Contains(t.Id) ?? false);
                    lstRuleTalkgroups.EndUpdate();
                    lstRuleZones.BeginUpdate();
                    lstRuleZones.Items.Clear();
                    foreach (var other in session.Project.Zones.Where(x => x != z && session.Project.ZoneRepeaters(x.Name).Count > 0))
                        lstRuleZones.Items.Add(other.Name, z.RuleZones?.Any(n => Project.SameZone(n, other.Name)) ?? false);
                    lstRuleZones.EndUpdate();
                    numRuleMiles.Value = (decimal)Math.Max(0, Math.Min(2000, z.RuleMiles));
                }
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
            name = Naming.Fit(name, 16);
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
