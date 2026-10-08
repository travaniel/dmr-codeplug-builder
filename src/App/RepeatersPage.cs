using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>The list of repeaters / analog channels on the left, the editor on the right.</summary>
    sealed class RepeatersPage : UserControl
    {
        readonly Session session;
        readonly ListView list;
        readonly ComboBox cboFilter;
        readonly RepeaterEditor editor;
        readonly Label lblEmpty;
        readonly Button btnUp, btnDown;
        bool loading;

        const string AllZones = "All zones";

        public RepeatersPage(Session session)
        {
            this.session = session;
            Font = Ui.BaseFont;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterWidth = Ui.S(6),
            };

            // ---------- left: list ----------
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(Ui.S(6), Ui.S(6), 0, Ui.S(6)) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            left.Controls.Add(Ui.Heading("Repeaters and channels"), 0, 0);

            cboFilter = Ui.Combo(false, AllZones);
            cboFilter.Dock = DockStyle.Fill;
            cboFilter.Margin = new Padding(3);
            var filterRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filterRow.Controls.Add(Ui.Label("Show"), 0, 0);
            filterRow.Controls.Add(cboFilter, 1, 0);
            left.Controls.Add(filterRow, 0, 1);

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                CheckBoxes = true,
                GridLines = false,
                Margin = new Padding(3),
            };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Name", Ui.S(150));
            list.Columns.Add("Zone", Ui.S(92));
            list.Columns.Add("Type", Ui.S(84));
            list.Columns.Add("RX MHz", Ui.S(68), HorizontalAlignment.Right);
            left.Controls.Add(list, 0, 2);

            btnUp = Ui.Button("Up", (s, e) => MoveItem(-1));
            btnDown = Ui.Button("Down", (s, e) => MoveItem(1));
            var btnMap = Ui.Button("Add from map...", (s, e) => AddFromMap());
            btnMap.Font = Ui.BoldFont;
            var buttons = Ui.Row(
                btnMap,
                Ui.Button("Find online...", (s, e) => FindOnline()),
                Ui.Button("From RepeaterBook...", (s, e) => FromRepeaterBook()),
                Ui.Button("Add DMR repeater", (s, e) => Add(Repeater.NewDigital("New repeater"))),
                Ui.Button("Add analog", (s, e) => Add(Repeater.NewAnalog("New analog"))),
                Ui.Button("Duplicate", (s, e) => Duplicate()),
                Ui.Button("Delete", (s, e) => Delete()),
                btnUp, btnDown,
                Ui.Button("Add NOAA weather", (s, e) => AddWeather()),
                Ui.Button("Add simplex", (s, e) => AddSimplex()));
            left.Controls.Add(buttons, 0, 3);
            split.Panel1.Controls.Add(left);

            // ---------- right: editor ----------
            editor = new RepeaterEditor(false) { Dock = DockStyle.Fill };
            lblEmpty = new Label
            {
                Text = "Click \"Add from map...\" to add DMR repeaters near you from RadioID.net,\nor add one by hand with the buttons on the left.\n\n" +
                       "Tip: File > Import from CPS export loads your current codeplug.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Ui.HintColor,
            };
            split.Panel2.Controls.Add(editor);
            split.Panel2.Controls.Add(lblEmpty);
            Controls.Add(split);

            Ui.InitSplitter(split, Ui.S(430), Ui.S(320), Ui.S(480));

            list.SelectedIndexChanged += (s, e) => { if (!loading) BindSelected(); };
            // On Windows the ListView raises ItemChecked for every item while it (re)creates its handle,
            // which would toggle Enabled and mark a freshly opened project dirty. Ignore checks until the
            // handle creation has finished.
            bool creatingHandle = false;
            list.HandleCreated += (s, e) => { creatingHandle = true; list.BeginInvoke((Action)(() => creatingHandle = false)); };
            list.ItemChecked += (s, e) =>
            {
                if (loading || creatingHandle) return;
                var r = e.Item.Tag as Repeater;
                if (r == null || r.Enabled == e.Item.Checked) return;
                r.Enabled = e.Item.Checked;
                StyleItem(e.Item);
                if (editor.Repeater == r) editor.Bind(session, r);
                session.MarkDirty();
            };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) Delete(); };
            cboFilter.SelectedIndexChanged += (s, e) => { if (!loading) Reload(Selected); };
            cboFilter.DropDown += (s, e) => RefillFilter();
            editor.Changed += (s, e) =>
            {
                var item = list.SelectedItems.Count > 0 ? list.SelectedItems[0] : null;
                if (item != null) Fill(item, (Repeater)item.Tag);
            };

            session.Replaced += (s, e) => Reload(null);
            session.TalkgroupsChanged += (s, e) => editor.RefreshTalkgroups();
            Reload(null);
        }

        Repeater Selected => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Repeater : null;

        string Filter => cboFilter.SelectedIndex > 0 ? (string)cboFilter.SelectedItem : null;

        /// <summary>Rebuilds the list (after load, add, delete, reorder or zone changes).</summary>
        public void Reload(Repeater select)
        {
            loading = true;
            try
            {
                RefillFilter();
                string filter = Filter;

                list.BeginUpdate();
                list.Items.Clear();
                foreach (var r in session.Project.Repeaters)
                {
                    if (filter == "(no zone)" && !string.IsNullOrWhiteSpace(r.Zone)) continue;
                    if (filter != null && filter != "(no zone)" && !Project.SameZone(r.Zone, filter)) continue;
                    var item = new ListViewItem { Tag = r };
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    Fill(item, r);
                    list.Items.Add(item);
                    if (r == select) { item.Selected = true; item.Focused = true; }
                }
                list.EndUpdate();
                if (list.SelectedItems.Count == 0 && list.Items.Count > 0 && select == null && editor.Repeater == null)
                    list.Items[0].Selected = true;
                if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
                btnUp.Enabled = btnDown.Enabled = filter == null;
            }
            finally { loading = false; }
            BindSelected();
        }

        void RefillFilter()
        {
            bool l = loading;
            loading = true;
            session.Project.SyncZones();
            string filter = Filter;
            cboFilter.BeginUpdate();
            cboFilter.Items.Clear();
            cboFilter.Items.Add(AllZones);
            foreach (var z in session.Project.Zones) cboFilter.Items.Add(z.Name);
            if (session.Project.Repeaters.Any(r => string.IsNullOrWhiteSpace(r.Zone))) cboFilter.Items.Add("(no zone)");
            int fi = filter == null ? 0 : cboFilter.Items.IndexOf(filter);
            cboFilter.SelectedIndex = fi < 0 ? 0 : fi;
            cboFilter.EndUpdate();
            loading = l;
        }

        void Fill(ListViewItem item, Repeater r)
        {
            bool l = loading;
            loading = true;
            item.Text = string.IsNullOrWhiteSpace(r.Name) ? "(no name)" : r.Name;
            item.Checked = r.Enabled;
            item.SubItems[1].Text = r.Zone;
            item.SubItems[2].Text = r.IsDigital
                ? "DMR " + r.Talkgroups.Count + " TG" + (r.Talkgroups.Count == 1 ? "" : "s")
                : "FM" + (Tones.Normalize(r.ToneEncode) != "Off" ? " " + Tones.Normalize(r.ToneEncode) : "");
            item.SubItems[3].Text = r.RxMHz > 0 ? r.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) : "";
            StyleItem(item);
            loading = l;
        }

        static void StyleItem(ListViewItem item)
        {
            var r = (Repeater)item.Tag;
            item.ForeColor = r.Enabled ? SystemColors.WindowText : SystemColors.GrayText;
        }

        void BindSelected()
        {
            var r = Selected;
            editor.Bind(session, r);
            editor.Visible = r != null;
            lblEmpty.Visible = r == null;
        }

        void Add(Repeater r)
        {
            // New entries go into the zone currently shown (or the selected repeater's zone).
            r.Zone = Filter != null && Filter != "(no zone)" ? Filter : (Selected?.Zone ?? "");
            int at = Selected != null ? session.Project.Repeaters.IndexOf(Selected) + 1 : session.Project.Repeaters.Count;
            session.Project.Repeaters.Insert(at, r);
            session.Project.SyncZones();
            session.Project.ApplyZoneTalkgroups(r); // a DMR repeater added to a zone gets that zone's talkgroups
            session.MarkDirty();
            Reload(r);
            editor.FocusName();
        }

        /// <summary>Opens the map picker and shows what it added.</summary>
        public void AddFromMap()
        {
            var first = AddFromMapDialog.Run(FindForm(), session);
            if (first == null) return;
            cboFilter.SelectedIndex = 0;
            Reload(first);
        }

        /// <summary>Opens the RadioID.net repeater finder and shows what it added.</summary>
        public void FindOnline()
        {
            var first = OnlineRepeaterDialog.Run(FindForm(), session);
            if (first == null) return;
            cboFilter.SelectedIndex = 0;
            Reload(first);
        }

        /// <summary>Opens the RepeaterBook importer (CHIRP export) and shows what it added.</summary>
        public void FromRepeaterBook()
        {
            var first = RepeaterBookDialog.Run(FindForm(), session);
            if (first == null) return;
            cboFilter.SelectedIndex = 0;
            Reload(first);
        }

        void AddWeather()
        {
            var added = Presets.AddNoaaWeather(session.Project);
            if (added.Count == 0) { Ui.Info(FindForm(), "You already have all 7 NOAA weather channels."); return; }
            session.MarkDirty();
            cboFilter.SelectedIndex = 0; // they go in the "Weather" zone, which another zone filter would hide
            Reload(added[0]);
        }

        void AddSimplex()
        {
            var added = Presets.AddSimplex(session.Project);
            if (added.Count == 0) { Ui.Info(FindForm(), "You already have all the simplex channels."); return; }
            session.MarkDirty();
            session.NotifyTalkgroupsChanged(); // talkgroup 99 may be new
            cboFilter.SelectedIndex = 0;
            Reload(added[0]);
        }

        void Duplicate()
        {
            var r = Selected;
            if (r == null) return;
            var copy = r.Clone();
            copy.Name = r.Name + " copy";
            copy.ChannelNumber = 0; // new channels get new numbers; the original keeps its own
            foreach (var e in copy.Talkgroups) { e.ChannelName = null; e.ChannelNumber = 0; }
            session.Project.Repeaters.Insert(session.Project.Repeaters.IndexOf(r) + 1, copy);
            session.MarkDirty();
            Reload(copy);
            editor.FocusName();
        }

        void Delete()
        {
            var r = Selected;
            if (r == null) return;
            string what = r.IsDigital && r.Talkgroups.Count > 0 ? " and its " + r.Talkgroups.Count + " channels" : "";
            if (!Ui.Confirm(FindForm(), "Delete \"" + r.Name + "\"" + what + "?")) return;
            int i = list.SelectedIndices[0];
            session.Project.Repeaters.Remove(r);
            session.MarkDirty();
            Repeater next = null;
            if (list.Items.Count > 1) next = (Repeater)list.Items[i + 1 < list.Items.Count ? i + 1 : i - 1].Tag;
            Reload(next);
        }

        void MoveItem(int delta)
        {
            var r = Selected;
            if (r == null || Filter != null) return;
            var all = session.Project.Repeaters;
            int i = all.IndexOf(r), j = i + delta;
            if (j < 0 || j >= all.Count) return;
            all.RemoveAt(i);
            all.Insert(j, r);
            session.MarkDirty();
            Reload(r);
        }
    }
}
