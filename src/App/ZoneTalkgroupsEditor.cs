using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// The talkgroups of one zone. Ticked = on every DMR repeater in the zone (the zone's talkgroup set, which
    /// also reaches repeaters added later); unticked rows are talkgroups only some repeaters list themselves.
    /// Search on the left adds talkgroups from the project or from BrandMeister's list.
    /// </summary>
    sealed class ZoneTalkgroupsEditor : UserControl
    {
        Project project;
        string zone;
        /// <summary>Talkgroups unticked in each zone, kept in the list (unticked) so they can be ticked again; "Remove from zone" forgets them.</summary>
        readonly Dictionary<string, HashSet<int>> unticked = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, string> bm = new Dictionary<int, string>();
        bool loading;

        readonly Label lblTitle, lblCount;
        readonly TextBox txtSearch;
        readonly ListBox lstFound;
        readonly DataGridView grid;
        readonly DataGridViewCheckBoxColumn colIn;
        readonly DataGridViewTextBoxColumn colName, colId, colOn;
        readonly DataGridViewComboBoxColumn colSlot;

        /// <summary>The project changed (talkgroups added, channels added or removed).</summary>
        public event EventHandler Changed;

        sealed class Found
        {
            public Talkgroup Existing;
            public int Id;
            public string Name;
            public override string ToString()
            {
                return Name + "  (" + Id.ToString(CultureInfo.InvariantCulture) + ")" + (Existing == null ? "  - BrandMeister" : "");
            }
        }

        public ZoneTalkgroupsEditor()
        {
            Font = Ui.BaseFont;

            lblTitle = Ui.Label("", true);
            lblTitle.Margin = new Padding(3, 3, 3, 2);
            var hint = Ui.Hint("Ticked talkgroups go on every DMR repeater in this zone, including repeaters you add to it later. " +
                               "Unticked ones are listed by only some of the repeaters themselves.", Ui.S(760));

            // ---------- left: search ----------
            txtSearch = new TextBox { Dock = DockStyle.Top };
            Ui.SetCue(txtSearch, "Find a talkgroup: name or number");
            lstFound = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
            var addRow = Ui.Row(
                Ui.Button("Add to zone  >", (s, e) => AddFound()),
                Ui.Button("Starter set", (s, e) => AddStarterSet()));
            addRow.Dock = DockStyle.Bottom;
            var left = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3) };
            left.Controls.Add(lstFound);
            left.Controls.Add(txtSearch);
            left.Controls.Add(addRow);
            lstFound.BringToFront();

            // ---------- right: grid ----------
            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
                Margin = new Padding(3),
            };
            Ui.SetUpGrid(grid);
            colIn = new DataGridViewCheckBoxColumn { HeaderText = "All", FillWeight = 9, ToolTipText = "Ticked: on every repeater in this zone" };
            colName = new DataGridViewTextBoxColumn { HeaderText = "Talkgroup", FillWeight = 40, ReadOnly = true };
            colId = new DataGridViewTextBoxColumn { HeaderText = "ID", FillWeight = 16, ReadOnly = true };
            colSlot = new DataGridViewComboBoxColumn { HeaderText = "Slot", FillWeight = 12, FlatStyle = FlatStyle.Flat };
            colSlot.Items.AddRange("1", "2", "1 + 2"); // "1 + 2" only shows a mix of listed slots; it can't be picked (CellBeginEdit)
            colOn = new DataGridViewTextBoxColumn { HeaderText = "Repeaters", FillWeight = 20, ReadOnly = true };
            colOn.DefaultCellStyle.ForeColor = Ui.HintColor;
            grid.Columns.AddRange(colIn, colName, colId, colSlot, colOn);
            var rightButtons = Ui.Row(
                Ui.Button("Remove from zone", (s, e) => RemoveSelected()),
                Ui.Button("Copy ticked to all zones", (s, e) => CopyToAllZones()));
            rightButtons.Dock = DockStyle.Bottom;
            var right = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3) };
            right.Controls.Add(grid);
            right.Controls.Add(rightButtons);
            grid.BringToFront();

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            body.Controls.Add(left, 0, 0);
            body.Controls.Add(right, 1, 0);

            lblCount = Ui.Hint("");
            lblCount.Dock = DockStyle.Bottom;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            top.Controls.Add(lblTitle);
            top.Controls.Add(hint);

            Controls.Add(body);
            Controls.Add(top);
            Controls.Add(lblCount);
            body.BringToFront();

            txtSearch.TextChanged += (s, e) => RefreshFound();
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { AddFound(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down && lstFound.Items.Count > 0) { lstFound.Focus(); lstFound.ClearSelected(); lstFound.SelectedIndex = 0; e.Handled = true; }
            };
            lstFound.DoubleClick += (s, e) => AddFound();
            lstFound.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddFound(); e.SuppressKeyPress = true; } };
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CellBeginEdit += (s, e) =>
            {
                // A talkgroup only some repeaters list has no zone slot to change.
                if (e.ColumnIndex == colSlot.Index && !(grid.Rows[e.RowIndex].Cells[colIn.Index].Value is bool b && b)) e.Cancel = true;
            };
            grid.EditingControlShowing += (s, e) =>
            {
                // Ticked rows choose slot 1 or 2; the "1 + 2" display value isn't a choice.
                if (e.Control is ComboBox c && c.Items.Contains("1 + 2")) c.Items.Remove("1 + 2");
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode) { RemoveSelected(); e.Handled = true; } };
            Bind(null, null);
        }

        public string Zone => zone;
        internal void PressStarterSet() { AddStarterSet(); }
        internal DataGridView Grid => grid; // --ui-walkthrough ticks a row with a real key press

        public void Bind(Project p, string zoneName)
        {
            project = p;
            zone = zoneName;
            bool has = p != null && zoneName != null && p.FindZone(zoneName) != null;
            foreach (Control c in Controls) c.Enabled = has;
            lblTitle.Text = has ? "Talkgroups in \"" + zoneName + "\"" : "Pick a zone";
            RefreshGrid();
            RefreshFound();
        }

        public void SetBrandMeister(Dictionary<int, string> names)
        {
            bm = names ?? new Dictionary<int, string>();
            RefreshFound();
        }

        /// <summary>Slot for a newly added talkgroup: simplex hotspots carry everything on slot 2, repeaters follow BrandMeister habits.</summary>
        int SlotFor(int id)
        {
            var reps = ZoneDigital();
            if (reps.Count > 0 && reps.All(r => r.RxMHz > 0 && r.RxMHz == r.TxMHz)) return 2;
            return Project.DefaultSlot(id);
        }

        List<Repeater> ZoneDigital()
        {
            return project == null || zone == null ? new List<Repeater>() : project.ZoneRepeaters(zone).Where(r => r.Enabled || r == project.Hotspot).ToList();
        }

        public void RefreshGrid()
        {
            loading = true;
            try
            {
                var keep = new HashSet<int>(grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (int)r.Tag));
                grid.Rows.Clear();
                var info = project?.FindZone(zone ?? "");
                if (info == null) { lblCount.Text = ""; return; }
                var reps = ZoneDigital();
                var ids = new List<int>();
                foreach (var t in info.Talkgroups ?? new List<ZoneTalkgroup>()) if (!ids.Contains(t.TalkgroupId)) ids.Add(t.TalkgroupId);
                foreach (var r in reps)
                    foreach (var e in r.Talkgroups) if (!ids.Contains(e.TalkgroupId)) ids.Add(e.TalkgroupId);
                HashSet<int> gone;
                if (unticked.TryGetValue(zone, out gone))
                    foreach (int id in gone) if (!ids.Contains(id) && project.FindTalkgroup(id) != null) ids.Add(id);
                foreach (int id in ids)
                {
                    var tg = project.FindTalkgroup(id);
                    var zt = info.FindTalkgroup(id);
                    int on = reps.Count(r => r.Talkgroups.Any(e => e.TalkgroupId == id));
                    var slots = reps.SelectMany(r => r.Talkgroups.Where(e => e.TalkgroupId == id).Select(e => e.Slot)).Distinct().ToList();
                    string slot = zt != null ? zt.Slot.ToString(CultureInfo.InvariantCulture) : slots.Count == 1 ? slots[0].ToString(CultureInfo.InvariantCulture) : "1 + 2";
                    string onText = reps.Count == 0 ? "" : on == reps.Count ? "all " + reps.Count : on + " of " + reps.Count;
                    int i = grid.Rows.Add(zt != null, tg == null ? "(not in your talkgroup list)" : tg.Name + (tg.CallType == CallTypes.Private ? " (private)" : ""),
                                          id.ToString(CultureInfo.InvariantCulture), slot, onText);
                    grid.Rows[i].Tag = id;
                    if (zt == null) grid.Rows[i].Cells[colSlot.Index].Style.ForeColor = Ui.HintColor;
                    if (keep.Contains(id)) grid.Rows[i].Selected = true;
                }
                if (keep.Count == 0) grid.ClearSelection();
                UpdateCount(info, reps);
            }
            finally { loading = false; }
        }

        void UpdateCount(ZoneInfo info, List<Repeater> reps)
        {
            int chans = project.ZoneChannelCount(zone);
            int max = Math.Max(1, project.Options.MaxZoneChannels);
            int total = project.ChannelCount();
            int bare = reps.Count(r => r.Talkgroups.Count == 0);
            string text = reps.Count + " DMR repeater" + (reps.Count == 1 ? "" : "s") + " in this zone, " + chans + " channel" + (chans == 1 ? "" : "s") +
                          (chans > max ? " (more than " + max + ": the radio gets it as " + ((chans + max - 1) / max) + " zones)" : "") +
                          ".   Whole codeplug: " + total + " of " + project.Options.MaxChannels + " channels.";
            if (bare > 0) text += "\n" + bare + " repeater" + (bare == 1 ? " has" : "s have") + " no talkgroups yet, so no channels: tick or add some here.";
            lblCount.Text = text;
            lblCount.ForeColor = total > project.Options.MaxChannels ? Color.Firebrick : bare > 0 ? Color.DarkGoldenrod : Ui.HintColor;
        }

        void RefreshFound()
        {
            lstFound.BeginUpdate();
            lstFound.Items.Clear();
            if (project != null)
            {
                string q = txtSearch.Text.Trim();
                var terms = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                bool Match(int id, string name)
                {
                    return terms.All(t => (name ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || id.ToString(CultureInfo.InvariantCulture).StartsWith(t, StringComparison.Ordinal));
                }
                var items = project.Talkgroups.Where(t => Match(t.Id, t.Name)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                                   .Select(t => new Found { Existing = t, Id = t.Id, Name = t.Name }).ToList();
                if (terms.Length > 0)
                    items.AddRange(bm.Where(kv => project.FindTalkgroup(kv.Key) == null && Match(kv.Key, kv.Value)).OrderBy(kv => kv.Key).Take(300)
                                     .Select(kv => new Found { Id = kv.Key, Name = kv.Value }));
                if (int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typed) && typed > 0 && typed <= Validator.MaxTalkgroupId && !items.Any(i => i.Id == typed))
                    items.Add(new Found { Id = typed, Name = "TG " + typed.ToString(CultureInfo.InvariantCulture) });
                foreach (var i in items) lstFound.Items.Add(i);
            }
            lstFound.EndUpdate();
        }

        /// <summary>The project's talkgroup with this ID, creating it (named from BrandMeister, shortened to 16) if needed.</summary>
        Talkgroup Ensure(int id, string fallbackName)
        {
            var tg = project.FindTalkgroup(id);
            if (tg != null) return tg;
            string name = bm.TryGetValue(id, out string n) ? BrandMeister.ShortName(n) : BrandMeister.ShortName(fallbackName ?? "");
            name = Naming.UniqueTalkgroupName(name, id, x => project.Talkgroups.Any(t => string.Equals(t.Name, x, StringComparison.OrdinalIgnoreCase)));
            tg = new Talkgroup(name, id, BrandMeister.IsPrivateCall(id, name) ? CallTypes.Private : CallTypes.Group);
            project.Talkgroups.Add(tg);
            return tg;
        }

        void AddFound()
        {
            if (project == null || zone == null) return;
            var picked = lstFound.SelectedItems.Cast<Found>().ToList();
            if (picked.Count == 0 && lstFound.Items.Count == 1) picked.Add((Found)lstFound.Items[0]);
            if (picked.Count == 0) { txtSearch.Focus(); return; }
            foreach (var f in picked)
            {
                var tg = Ensure(f.Id, f.Name);
                project.AddZoneTalkgroup(zone, tg.Id, SlotFor(tg.Id));
            }
            AfterChange();
            txtSearch.SelectAll();
        }

        /// <summary>Your statewide talkgroup, USA, Local and Parrot (BrandMeister), on their usual slots.</summary>
        void AddStarterSet()
        {
            if (project == null || zone == null) return;
            string state = ZoneDigital().Select(r => r.State).Where(s => !string.IsNullOrEmpty(s))
                                        .GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            foreach (var c in OnlineImporter.SuggestedDefaults(state, bm).Where(c => c.Talkgroup.Id != 91 && c.Talkgroup.Id != 93))
            {
                var tg = project.FindTalkgroup(c.Talkgroup.Id) ?? Ensure(c.Talkgroup.Id, c.Talkgroup.Name);
                if (c.Talkgroup.CallType == CallTypes.Private) tg.CallType = CallTypes.Private;
                project.AddZoneTalkgroup(zone, tg.Id, SlotFor(tg.Id));
            }
            AfterChange();
        }

        void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0 || project == null) return;
            var row = grid.Rows[e.RowIndex];
            int id = (int)row.Tag;
            if (e.ColumnIndex == colIn.Index)
            {
                bool on = row.Cells[colIn.Index].Value is bool b && b;
                if (on)
                {
                    string shown = row.Cells[colSlot.Index].Value as string;
                    int slot = shown == "1" ? 1 : shown == "2" ? 2 : SlotFor(id);
                    project.AddZoneTalkgroup(zone, id, slot);
                    HashSet<int> gone;
                    if (unticked.TryGetValue(zone, out gone)) gone.Remove(id);
                }
                else
                {
                    project.RemoveZoneTalkgroup(zone, id, alsoListed: false);
                    HashSet<int> gone;
                    if (!unticked.TryGetValue(zone, out gone)) unticked[zone] = gone = new HashSet<int>();
                    gone.Add(id);
                }
            }
            else if (e.ColumnIndex == colSlot.Index)
            {
                string v = row.Cells[colSlot.Index].Value as string;
                if (v == "1" || v == "2") project.SetZoneTalkgroupSlot(zone, id, v == "2" ? 2 : 1);
            }
            else return;
            // Rebuilding the rows from inside the grid's own CellValueChanged throws (reentrant
            // SetCurrentCellAddressCore), so refresh once the event has finished.
            BeginInvoke((Action)AfterChange);
        }

        void RemoveSelected()
        {
            if (project == null || zone == null) return;
            var ids = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (int)r.Tag).ToList();
            if (ids.Count == 0) return;
            int channels = ZoneDigital().Sum(r => r.Talkgroups.Count(t => ids.Contains(t.TalkgroupId)));
            if (channels > 0 && !Ui.Confirm(FindForm(), "Remove " + (ids.Count == 1 ? "this talkgroup" : ids.Count + " talkgroups") + " from every repeater in \"" + zone + "\"?\n\n" +
                                                       channels + " channel" + (channels == 1 ? "" : "s") + " will go, including ones the repeaters list themselves."))
                return;
            foreach (int id in ids)
            {
                project.RemoveZoneTalkgroup(zone, id);
                HashSet<int> gone;
                if (unticked.TryGetValue(zone, out gone)) gone.Remove(id);
            }
            AfterChange();
        }

        void CopyToAllZones()
        {
            var info = project?.FindZone(zone ?? "");
            if (info == null || !info.HasTalkgroups) { Ui.Info(FindForm(), "Tick some talkgroups in this zone first."); return; }
            var others = project.Zones.Where(z => z != info && project.TakesZoneTalkgroups(z.Name)).ToList();
            if (others.Count == 0) { Ui.Info(FindForm(), "There are no other zones with DMR repeaters."); return; }
            if (!Ui.Confirm(FindForm(), "Put this zone's " + info.Talkgroups.Count + " ticked talkgroup" + (info.Talkgroups.Count == 1 ? "" : "s") +
                                        " on every repeater in the other " + others.Count + " zone" + (others.Count == 1 ? "" : "s") + " too?"))
                return;
            foreach (var z in others)
                foreach (var t in info.Talkgroups) project.AddZoneTalkgroup(z.Name, t.TalkgroupId, t.Slot);
            AfterChange();
        }

        void AfterChange()
        {
            RefreshGrid();
            RefreshFound();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
