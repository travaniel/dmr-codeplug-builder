using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>The master talkgroup list (TalkGroups.CSV). Repeaters pick from this list.</summary>
    sealed class TalkgroupsPage : UserControl
    {
        readonly Session session;
        readonly DataGridView grid;
        readonly DataGridViewTextBoxColumn colName, colId, colUsed;
        readonly DataGridViewComboBoxColumn colType;
        readonly Label lblCount;
        bool loading;

        static readonly Talkgroup[] Common =
        {
            new Talkgroup("Local", 9),
            new Talkgroup("Worldwide", 91),
            new Talkgroup("North America", 93),
            new Talkgroup("USA Nationwide", 3100),
            new Talkgroup("Parrot", BrandMeister.Parrot, CallTypes.Private),
            new Talkgroup("Disconnect", 4000),
        };

        public TalkgroupsPage(Session session)
        {
            this.session = session;
            Font = Ui.BaseFont;
            Padding = new Padding(Ui.S(10), Ui.S(8), Ui.S(10), Ui.S(8));

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            top.Controls.Add(Ui.Heading("Talkgroups"));
            top.Controls.Add(Ui.Hint("Every talkgroup you might use, once. These become the CPS Talk Groups list, and repeaters pick from it. " +
                                     "IDs must be unique. Use Private Call for things like the Parrot echo test.", Ui.S(900)));

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
                BackgroundColor = System.Drawing.SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            };
            Ui.SetUpGrid(grid);
            colName = new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 50, MaxInputLength = 16 };
            colId = new DataGridViewTextBoxColumn { HeaderText = "Talkgroup / DMR ID", FillWeight = 22, MaxInputLength = 8 };
            colType = new DataGridViewComboBoxColumn { HeaderText = "Call type", FillWeight = 22, FlatStyle = FlatStyle.Flat };
            colType.Items.AddRange(CallTypes.Values.Cast<object>().ToArray());
            colUsed = new DataGridViewTextBoxColumn { HeaderText = "Channels", FillWeight = 14, ReadOnly = true };
            colUsed.DefaultCellStyle.ForeColor = Ui.HintColor;
            grid.Columns.AddRange(colName, colId, colType, colUsed);

            var commonMenu = new ContextMenuStrip();
            foreach (var t in Common)
            {
                var tg = t;
                commonMenu.Items.Add(tg.Name + "  (" + tg.Id + (tg.CallType == CallTypes.Private ? ", private" : "") + ")", null, (s, e) => AddCommon(new[] { tg }));
            }
            commonMenu.Items.Add(new ToolStripSeparator());
            commonMenu.Items.Add("Add all of these", null, (s, e) => AddCommon(Common));
            Button btnCommon = null;
            btnCommon = Ui.Button("Common BrandMeister talkgroups...", (s, e) => commonMenu.Show(btnCommon, 0, btnCommon.Height));

            lblCount = Ui.Hint("");
            var buttons = Ui.Row(
                Ui.Button("Add talkgroup", (s, e) => AddNew()),
                Ui.Button("Delete", (s, e) => DeleteSelected()),
                Ui.Button("Browse BrandMeister...", (s, e) => BrowseBrandMeister()),
                btnCommon,
                Ui.Button("Import from CSV...", (s, e) => ImportCsv()),
                Ui.Button("Sort by name", (s, e) => Sort(byId: false)),
                Ui.Button("Sort by ID", (s, e) => Sort(byId: true)),
                lblCount);
            buttons.Dock = DockStyle.Bottom;
            lblCount.Margin = new Padding(Ui.S(12), Ui.S(9), 3, 3);

            Controls.Add(grid);
            Controls.Add(top);
            Controls.Add(buttons);
            grid.BringToFront();

            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValidating += Grid_CellValidating;
            grid.CellEndEdit += (s, e) => { grid.Rows[e.RowIndex].ErrorText = ""; };
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode) { DeleteSelected(); e.Handled = true; } };

            session.Replaced += (s, e) => Reload();
            VisibleChanged += (s, e) =>
            {
                if (!Visible) return;
                // Other pages (Find online) can add talkgroups; pick those up when the tab is shown.
                bool same = grid.Rows.Count == session.Project.Talkgroups.Count &&
                            grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).SequenceEqual(session.Project.Talkgroups);
                if (same) RefreshUsage(); else Reload();
            };
            Reload();
        }

        public void Reload(IEnumerable<Talkgroup> select = null)
        {
            loading = true;
            try
            {
                var keep = new HashSet<Talkgroup>(select ?? grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (Talkgroup)r.Tag));
                grid.Rows.Clear();
                foreach (var t in session.Project.Talkgroups)
                {
                    int i = grid.Rows.Add(t.Name, t.Id > 0 ? t.Id.ToString(CultureInfo.InvariantCulture) : "", CallTypes.Normalize(t.CallType), "");
                    grid.Rows[i].Tag = t;
                }
                grid.ClearSelection();
                foreach (DataGridViewRow row in grid.Rows)
                    if (keep.Contains((Talkgroup)row.Tag)) row.Selected = true;
                RefreshUsage();
            }
            finally { loading = false; }
        }

        void RefreshUsage()
        {
            bool l = loading;
            loading = true;
            foreach (DataGridViewRow row in grid.Rows)
            {
                var t = (Talkgroup)row.Tag;
                int n = session.Project.CountTalkgroupUse(t.Id);
                row.Cells[colUsed.Index].Value = n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture);
            }
            lblCount.Text = session.Project.Talkgroups.Count + " talkgroups";
            loading = l;
        }

        void Grid_CellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            if (loading || e.RowIndex < 0) return;
            var t = (Talkgroup)grid.Rows[e.RowIndex].Tag;
            string v = (e.FormattedValue as string ?? "").Trim();
            if (e.ColumnIndex == colId.Index)
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0 || id > Validator.MaxTalkgroupId)
                {
                    grid.Rows[e.RowIndex].ErrorText = "Type a number, e.g. 91 or 3100";
                    e.Cancel = true;
                }
                else if (session.Project.Talkgroups.Any(x => x != t && x.Id == id))
                {
                    grid.Rows[e.RowIndex].ErrorText = "ID " + id + " is already used by " + session.Project.Talkgroups.First(x => x != t && x.Id == id).Name;
                    e.Cancel = true;
                }
            }
            else if (e.ColumnIndex == colName.Index && v.Length == 0)
            {
                grid.Rows[e.RowIndex].ErrorText = "Give the talkgroup a name";
                e.Cancel = true;
            }
        }

        void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0) return;
            var t = (Talkgroup)grid.Rows[e.RowIndex].Tag;
            string v = (grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value as string ?? "").Trim();
            if (e.ColumnIndex == colName.Index)
            {
                t.Name = Naming.Clean(v, 16);
            }
            else if (e.ColumnIndex == colId.Index)
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return;
                session.Project.ChangeTalkgroupId(t.Id, id);
                t.Id = id;
            }
            else if (e.ColumnIndex == colType.Index)
            {
                t.CallType = CallTypes.Normalize(v);
            }
            session.NotifyTalkgroupsChanged();
        }

        void AddNew()
        {
            var t = new Talkgroup("New talkgroup", 0);
            session.Project.Talkgroups.Add(t);
            Reload(new[] { t });
            session.NotifyTalkgroupsChanged();
            var row = grid.Rows.Cast<DataGridViewRow>().First(r => r.Tag == t);
            grid.FirstDisplayedScrollingRowIndex = row.Index;
            grid.CurrentCell = row.Cells[colName.Index];
            grid.BeginEdit(true);
        }

        void AddCommon(IEnumerable<Talkgroup> items)
        {
            var added = new List<Talkgroup>();
            foreach (var c in items)
            {
                if (session.Project.Talkgroups.Any(x => x.Id == c.Id)) continue;
                var t = c.Clone();
                if (session.Project.Talkgroups.Any(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase)))
                    t.Name = Naming.Clean(t.Name + " " + t.Id, 16); // e.g. a second "Parrot" becomes "Parrot 9990"
                session.Project.Talkgroups.Add(t);
                added.Add(t);
            }
            if (added.Count == 0) { Ui.Info(FindForm(), "Those talkgroups are already in your list."); return; }
            Reload(added);
            session.NotifyTalkgroupsChanged();
        }

        void BrowseBrandMeister()
        {
            using (var dlg = new TalkgroupBrowserDialog(session.Project))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.Selected.Count == 0) return;
                AddCommon(dlg.Selected);
            }
        }

        void DeleteSelected()
        {
            var sel = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (Talkgroup)r.Tag).ToList();
            if (sel.Count == 0) return;
            int uses = sel.Sum(t => session.Project.CountTalkgroupUse(t.Id));
            string msg = sel.Count == 1 ? "Delete talkgroup \"" + sel[0].Name + "\"?" : "Delete " + sel.Count + " talkgroups?";
            if (uses > 0) msg += "\n\nIt's used on " + uses + " channel" + (uses == 1 ? "" : "s") + "; those channels will be removed from their repeaters too.";
            if (!Ui.Confirm(FindForm(), msg)) return;
            session.Project.DeleteTalkgroups(sel);
            Reload(new Talkgroup[0]);
            session.NotifyTalkgroupsChanged();
        }

        void ImportCsv()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Import talkgroups",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                List<Talkgroup> list;
                try { list = TalkgroupCsv.Load(dlg.FileName); }
                catch (Exception ex) { Ui.Error(FindForm(), "Couldn't read that file:\n\n" + ex.Message); return; }
                if (list.Count == 0)
                {
                    Ui.Error(FindForm(), "No talkgroups found. The file needs an ID column and a name column, like the CPS's TalkGroups.CSV.");
                    return;
                }
                var added = new List<Talkgroup>();
                int skipped = 0;
                foreach (var t in list)
                {
                    if (session.Project.Talkgroups.Any(x => x.Id == t.Id)) { skipped++; continue; }
                    t.Name = Naming.Clean(t.Name, 16);
                    session.Project.Talkgroups.Add(t);
                    added.Add(t);
                }
                Reload(added);
                session.NotifyTalkgroupsChanged();
                Ui.Info(FindForm(), "Added " + added.Count + " talkgroup" + (added.Count == 1 ? "" : "s") + "." +
                                    (skipped > 0 ? "\n" + skipped + " were already in your list (same ID) and were skipped." : ""));
            }
        }

        void Sort(bool byId)
        {
            var sel = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (Talkgroup)r.Tag).ToList();
            var sorted = byId
                ? session.Project.Talkgroups.OrderBy(t => t.Id).ToList()
                : session.Project.Talkgroups.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            session.Project.Talkgroups.Clear();
            session.Project.Talkgroups.AddRange(sorted);
            Reload(sel);
            session.NotifyTalkgroupsChanged();
        }
    }
}
