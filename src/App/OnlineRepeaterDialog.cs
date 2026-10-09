using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Find DMR repeaters on RadioID.net by state, tick the ones you want, and add them to the project with
    /// their published talkgroups (named from BrandMeister's list) in a zone per city.
    /// </summary>
    sealed class OnlineRepeaterDialog : Form
    {
        readonly Session session;
        readonly ComboBox cboState, cboPower;
        readonly TextBox txtFilter, txtZone;
        readonly Button btnDownload, btnAdd;
        readonly Label lblStatus;
        readonly ListView list;
        readonly RadioButton optCity, optOne;
        readonly DataGridView gridDefaults;
        readonly CheckBox chkNoaa;

        List<OnlineRepeater> all = new List<OnlineRepeater>();
        readonly HashSet<OnlineRepeater> picked = new HashSet<OnlineRepeater>();
        Dictionary<int, string> bm = new Dictionary<int, string>();
        Dictionary<int, string> moreNames = new Dictionary<int, string>();
        string defaultsState;
        int sortColumn = 1;
        bool sortDescending, loading, creatingHandle, busy;

        public OnlineImportResult Result { get; private set; }
        public List<Repeater> NoaaAdded { get; private set; } = new List<Repeater>();

        public OnlineRepeaterDialog(Session session)
        {
            this.session = session;
            Text = "Find DMR repeaters online";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Ui.S(1160), area.Width * 95 / 100), Math.Min(Ui.S(800), area.Height * 95 / 100));
            MinimumSize = new Size(Ui.S(860), Ui.S(600));
            Padding = new Padding(Ui.S(10));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(Ui.Hint("DMR repeaters from RadioID.net, with the talkgroups their owners list there. Talkgroup names come from BrandMeister. " +
                                      "Pick a state, download, then tick the repeaters you want (use the filter to find a city or callsign).", Ui.S(1080)), 0, 0);

            // ---------- search row ----------
            cboState = Ui.Combo(true, BrandMeister.UsStates.Select(s => s.Key).ToArray());
            cboState.Width = Ui.S(170);
            cboState.Anchor = AnchorStyles.Left;
            cboState.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cboState.AutoCompleteSource = AutoCompleteSource.ListItems;
            btnDownload = Ui.Button("Download", (s, e) => Download());
            txtFilter = new TextBox { Width = Ui.S(220), Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) };
            Ui.SetCue(txtFilter, "city, callsign, network or MHz");
            lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.HintColor, Margin = new Padding(Ui.S(6), Ui.S(7), 3, 3) };
            var search = Ui.Row(Ui.Label("State"), cboState, btnDownload, Ui.Label("Filter"), txtFilter, lblStatus);
            search.WrapContents = false;
            root.Controls.Add(search, 0, 1);

            // ---------- results ----------
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                CheckBoxes = true,
                HideSelection = false,
                Margin = new Padding(3, 6, 3, 6),
            };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Callsign", Ui.S(90));
            list.Columns.Add("City", Ui.S(130));
            list.Columns.Add("Output MHz", Ui.S(84), HorizontalAlignment.Right);
            list.Columns.Add("Offset", Ui.S(58), HorizontalAlignment.Right);
            list.Columns.Add("CC", Ui.S(36), HorizontalAlignment.Right);
            list.Columns.Add("Network", Ui.S(130));
            list.Columns.Add("Talkgroups listed", Ui.S(390));
            list.Columns.Add("", Ui.S(110));
            root.Controls.Add(list, 0, 2);

            // ---------- options ----------
            var opts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var lblZones = Ui.Label("Zones", true);
            left.Controls.Add(lblZones, 0, 0);
            left.SetColumnSpan(lblZones, 2);
            optCity = new RadioButton { Text = "One zone per city (\"San Angelo\", \"Abilene\"...)", AutoSize = true, Checked = true, Margin = new Padding(3, 2, 3, 2) };
            left.Controls.Add(optCity, 0, 1);
            left.SetColumnSpan(optCity, 2);
            optOne = new RadioButton { Text = "All in one zone:", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
            txtZone = new TextBox { Width = Ui.S(170), MaxLength = 16, Anchor = AnchorStyles.Left, Enabled = false };
            left.Controls.Add(optOne, 0, 2);
            left.Controls.Add(txtZone, 1, 2);
            cboPower = Ui.Combo(false, Powers.Values);
            cboPower.SelectedItem = "High";
            cboPower.Width = Ui.S(90);
            cboPower.Anchor = AnchorStyles.Left;
            left.Controls.Add(Ui.Label("Transmit power"), 0, 3);
            left.Controls.Add(cboPower, 1, 3);
            chkNoaa = new CheckBox { Text = "Also add the 7 NOAA weather channels (receive only, zone \"Weather\")", AutoSize = true, Margin = new Padding(3, Ui.S(10), 3, 3) };
            chkNoaa.Checked = Presets.Noaa.Any(n => !session.Project.Repeaters.Any(r => !r.IsDigital && r.RxMHz == n.Value)) &&
                              !session.Project.Repeaters.Any(r => r.RxOnly && r.RxMHz >= 162.4m && r.RxMHz <= 162.55m);
            left.Controls.Add(chkNoaa, 0, 4);
            left.SetColumnSpan(chkNoaa, 2);
            opts.Controls.Add(left, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(Ui.S(10), 0, 0, 0) };
            right.Controls.Add(Ui.Label("Repeaters that don't list their talkgroups get these:", true));
            gridDefaults = new DataGridView
            {
                Dock = DockStyle.Top,
                Height = Ui.S(150),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
            };
            Ui.SetUpGrid(gridDefaults);
            var colUse = new DataGridViewCheckBoxColumn { HeaderText = "Use", FillWeight = 10 };
            var colName = new DataGridViewTextBoxColumn { HeaderText = "Talkgroup", FillWeight = 50, ReadOnly = true };
            var colId = new DataGridViewTextBoxColumn { HeaderText = "ID", FillWeight = 20, ReadOnly = true };
            var colSlot = new DataGridViewComboBoxColumn { HeaderText = "Slot", FillWeight = 14, FlatStyle = FlatStyle.Flat };
            colSlot.Items.AddRange("1", "2");
            gridDefaults.Columns.AddRange(colUse, colName, colId, colSlot);
            gridDefaults.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (gridDefaults.IsCurrentCellDirty) gridDefaults.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            gridDefaults.CellValueChanged += (s, e) => UpdateAddButton();
            gridDefaults.DataError += (s, e) => { e.ThrowException = false; };
            right.Controls.Add(gridDefaults);
            right.Controls.Add(Ui.Row(
                Ui.Button("Add talkgroup...", (s, e) => AddDefaultTalkgroup()),
                Ui.Hint("Repeaters vary; check each one's web page and fix slots in the repeater editor afterwards.", Ui.S(420))));
            opts.Controls.Add(right, 1, 0);
            root.Controls.Add(opts, 0, 3);

            // ---------- buttons ----------
            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, Ui.S(8), 0, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.Controls.Add(Ui.Row(
                Ui.Button("Tick all shown", (s, e) => SetAllShown(true)),
                Ui.Button("Untick all", (s, e) => { picked.Clear(); Refill(); })), 0, 0);
            btnAdd = new Button { Text = "Add repeaters", AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(Ui.S(10), Ui.S(3), Ui.S(10), Ui.S(3)), UseVisualStyleBackColor = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Ui.S(6), Ui.S(3), Ui.S(6), Ui.S(3)), UseVisualStyleBackColor = true };
            var right2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            right2.Controls.Add(btnAdd);
            right2.Controls.Add(cancel);
            buttons.Controls.Add(right2, 1, 0);
            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);
            CancelButton = cancel;

            // ---------- events ----------
            btnAdd.Click += (s, e) => AddPicked();
            chkNoaa.CheckedChanged += (s, e) => UpdateAddButton();
            optOne.CheckedChanged += (s, e) => { txtZone.Enabled = optOne.Checked; if (optOne.Checked && txtZone.Text.Length == 0) txtZone.Text = Naming.Clean(cboState.Text + " DMR", 16); };
            txtFilter.TextChanged += (s, e) => Refill();
            cboState.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Download(); } };
            list.ColumnClick += (s, e) =>
            {
                if (e.Column == sortColumn) sortDescending = !sortDescending;
                else { sortColumn = e.Column; sortDescending = false; }
                Refill();
            };
            // Windows raises ItemChecked for every item while the ListView creates its handle; ignore those.
            list.HandleCreated += (s, e) => { creatingHandle = true; list.BeginInvoke((Action)(() => creatingHandle = false)); };
            list.ItemChecked += (s, e) =>
            {
                if (loading || creatingHandle) return;
                var r = (OnlineRepeater)e.Item.Tag;
                if (e.Item.Checked) picked.Add(r); else picked.Remove(r);
                UpdateAddButton();
            };

            cboState.Text = AppSettings.Get("OnlineState") ?? "";
            FillDefaults(cboState.Text);
            UpdateAddButton();
            lblStatus.Text = "Pick your state and click Download.";
            Shown += async (s, e) =>
            {
                bm = await Online.BrandMeisterNamesAsync();
                if (IsDisposed) return;
                FillDefaults(cboState.Text, force: true);
                Refill();
                if (cboState.Text.Length > 0 && all.Count == 0) Download();
            };
        }

        async void Download()
        {
            string state = cboState.Text.Trim();
            if (state.Length == 0 || busy) { if (state.Length == 0) lblStatus.Text = "Pick a state first."; return; }
            busy = true;
            btnDownload.Enabled = false;
            UseWaitCursor = true;
            try
            {
                var progress = new Progress<string>(m => lblStatus.Text = m);
                var result = await Online.RepeatersAsync(state, progress);
                if (IsDisposed) return;
                all = result;
                try { moreNames = await Task.Run(() => Online.NameTalkgroups(result, bm, progress, System.Threading.CancellationToken.None)); } catch { }
                try
                {
                    lblStatus.Text = "Placing repeaters (DMR-MARC map positions)...";
                    var pos = await Online.RepeaterPositionsAsync();
                    await Task.Run(() => RadioId.ApplyMapPositions(result, pos, GeoAtlas.BuiltIn()));
                }
                catch { }
                if (IsDisposed) return;
                picked.Clear();
                AppSettings.Set("OnlineState", state);
                FillDefaults(state);
                Refill();
                int hidden = all.Count(r => !r.InRadioBand);
                lblStatus.Text = all.Count == 0
                    ? "RadioID.net lists no repeaters for \"" + state + "\". Check the spelling (full state or province name)."
                    : (all.Count - hidden) + " repeaters in " + state + (hidden > 0 ? " (" + hidden + " on bands the radio can't use are hidden)" : "") + ". Tick the ones you want.";
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                lblStatus.Text = "Download failed.";
                Ui.Error(this, "Couldn't download the repeater list:\n\n" + ex.Message);
            }
            finally
            {
                busy = false;
                if (!IsDisposed) { btnDownload.Enabled = true; UseWaitCursor = false; }
            }
        }

        IEnumerable<OnlineRepeater> Filtered()
        {
            var terms = txtFilter.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var items = all.Where(r => r.InRadioBand && terms.All(t => Matches(r, t)));
            Func<OnlineRepeater, IComparable> key;
            switch (sortColumn)
            {
                case 0: key = r => r.Callsign; break;
                case 2: key = r => r.RxMHz; break;
                case 3: key = r => r.Offset; break;
                case 4: key = r => r.ColorCode; break;
                case 5: key = r => r.Network; break;
                case 6: key = r => r.Talkgroups.Count; break;
                case 7: key = r => Status(r); break;
                default: key = r => r.City; break;
            }
            var ordered = sortDescending ? items.OrderByDescending(key) : items.OrderBy(key);
            return ordered.ThenBy(r => r.City).ThenBy(r => r.RxMHz).ThenBy(r => r.Callsign);
        }

        static bool Matches(OnlineRepeater r, string term)
        {
            return r.Callsign.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   r.City.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   r.Network.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   r.RxMHz.ToString("0.00000", CultureInfo.InvariantCulture).StartsWith(term, StringComparison.Ordinal);
        }

        string Status(OnlineRepeater r)
        {
            if (OnlineImporter.FindExisting(session.Project, r) != null) return "in your project";
            return r.Talkgroups.Count == 0 ? "none listed" : "";
        }

        void Refill()
        {
            loading = true;
            try
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var r in Filtered())
                {
                    string status = Status(r);
                    var item = new ListViewItem(r.Callsign) { Tag = r, Checked = picked.Contains(r) };
                    item.SubItems.Add(r.City);
                    item.SubItems.Add(r.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture));
                    item.SubItems.Add(r.Offset == 0 ? "simplex" : r.Offset.ToString("+0.0##;-0.0##", CultureInfo.InvariantCulture));
                    item.SubItems.Add(r.ColorCode.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(r.Network);
                    item.SubItems.Add(Summary(r));
                    item.SubItems.Add(status);
                    if (status == "in your project") item.ForeColor = SystemColors.GrayText;
                    item.ToolTipText = r.Details;
                    list.Items.Add(item);
                }
                list.EndUpdate();
            }
            finally { loading = false; }
            UpdateAddButton();
        }

        string Summary(OnlineRepeater r)
        {
            if (r.Talkgroups.Count == 0) return "";
            var parts = new List<string>();
            foreach (int slot in new[] { 1, 2 })
            {
                var names = r.Talkgroups.Where(t => t.Slot == slot).Select(t => OnlineImporter.TalkgroupName(r, t, bm, moreNames)).ToList();
                if (names.Count > 0) parts.Add("TS" + slot + ": " + string.Join(", ", names));
            }
            return string.Join("   ", parts);
        }

        void SetAllShown(bool on)
        {
            foreach (var r in Filtered())
                if (on && OnlineImporter.FindExisting(session.Project, r) == null) picked.Add(r);
                else if (!on) picked.Remove(r);
            Refill();
        }

        void FillDefaults(string state, bool force = false)
        {
            if (!force && string.Equals(state, defaultsState, StringComparison.OrdinalIgnoreCase)) return;
            var keep = DefaultChoices(onlyChecked: false).ToDictionary(c => c.Talkgroup.Id, c => c);
            defaultsState = state;
            gridDefaults.Rows.Clear();
            var suggestions = OnlineImporter.SuggestedDefaults(state, bm);
            foreach (var c in suggestions)
            {
                // Use the project's name for talkgroups it already has.
                var existing = session.Project.FindTalkgroup(c.Talkgroup.Id);
                if (existing != null) c.Talkgroup = existing.Clone();
                bool use = c.Talkgroup.Id != 91 && c.Talkgroup.Id != 93;
                AddDefaultRow(c, use);
            }
            foreach (var kv in keep.Where(k => !suggestions.Any(s => s.Talkgroup.Id == k.Key)))
                AddDefaultRow(kv.Value, true);
            gridDefaults.ClearSelection();
        }

        void AddDefaultRow(TalkgroupChoice c, bool use)
        {
            int i = gridDefaults.Rows.Add(use, c.Talkgroup.Name + (c.Talkgroup.CallType == CallTypes.Private ? " (private)" : ""),
                                          c.Talkgroup.Id.ToString(CultureInfo.InvariantCulture), c.Slot.ToString(CultureInfo.InvariantCulture));
            gridDefaults.Rows[i].Tag = c;
        }

        List<TalkgroupChoice> DefaultChoices(bool onlyChecked = true)
        {
            var result = new List<TalkgroupChoice>();
            foreach (DataGridViewRow row in gridDefaults.Rows)
            {
                var c = (TalkgroupChoice)row.Tag;
                if (onlyChecked && !(row.Cells[0].Value is bool b && b)) continue;
                c.Slot = (row.Cells[3].Value as string) == "2" ? 2 : 1;
                result.Add(c);
            }
            return result;
        }

        void AddDefaultTalkgroup()
        {
            string text = Prompt.Show(this, "Add a default talkgroup", "Talkgroup number (e.g. 31488):", "", 8);
            if (text == null) return;
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0 || id > Validator.MaxTalkgroupId)
            {
                Ui.Error(this, "Type a talkgroup number, e.g. 31488.");
                return;
            }
            if (DefaultChoices(false).Any(c => c.Talkgroup.Id == id)) return;
            var tg = session.Project.FindTalkgroup(id)?.Clone();
            if (tg == null)
            {
                string name = bm.TryGetValue(id, out string n) ? BrandMeister.ShortName(n) : "";
                if (name.Length == 0) name = "TG " + id.ToString(CultureInfo.InvariantCulture);
                tg = new Talkgroup(name, id, BrandMeister.IsPrivateCall(id, name) ? CallTypes.Private : CallTypes.Group);
            }
            AddDefaultRow(new TalkgroupChoice(tg.Name, tg.Id, 1, tg.CallType), true);
            UpdateAddButton();
        }

        void UpdateAddButton()
        {
            int defaults = DefaultChoices().Count;
            var addable = picked.Where(r => OnlineImporter.FindExisting(session.Project, r) == null).ToList();
            int channels = addable.Sum(r => r.Talkgroups.Count > 0 ? r.Talkgroups.Count : defaults);
            btnAdd.Text = addable.Count == 0
                ? (chkNoaa.Checked ? "Add the NOAA channels" : "Add repeaters")
                : "Add " + addable.Count + " repeater" + (addable.Count == 1 ? "" : "s") + " (" + channels + " channel" + (channels == 1 ? "" : "s") + ")";
            btnAdd.Enabled = addable.Count > 0 || chkNoaa.Checked;
        }

        void AddPicked()
        {
            var p = session.Project;
            var order = picked.OrderBy(r => r.City).ThenBy(r => r.RxMHz).ThenBy(r => r.Callsign).ToList();
            var o = new OnlineImportOptions
            {
                ZonePerCity = optCity.Checked,
                Zone = txtZone.Text,
                Power = (string)cboPower.SelectedItem ?? "High",
                DefaultTalkgroups = DefaultChoices(),
                MoreNames = moreNames,
            };
            if (optOne.Checked && Naming.Clean(o.Zone, 16).Length == 0) { Ui.Error(this, "Type a zone name."); return; }
            // Place them on the map too, so their county/state is kept (zones per county later, Add from map).
            try
            {
                var atlas = GeoAtlas.BuiltIn();
                foreach (var r in order) if (r.Location == null) r.Location = atlas.Locate(r.City, r.State, r.Country);
            }
            catch { }
            Online.PrepareForAdding(this, order); // BrandMeister's own talkgroups for its repeaters, as Add from map does
            Result = OnlineImporter.AddRepeaters(p, order, o, bm);
            if (chkNoaa.Checked) NoaaAdded = Presets.AddNoaaWeather(p);
            DialogResult = DialogResult.OK;
        }

        /// <summary>Shows the dialog and reports what was added. Returns the first added repeater (or null).</summary>
        public static Repeater Run(IWin32Window owner, Session session)
        {
            using (var d = new OnlineRepeaterDialog(session))
            {
                if (d.ShowDialog(owner) != DialogResult.OK || d.Result == null) return null;
                var r = d.Result;
                session.NotifyTalkgroupsChanged();
                var lines = new List<string>();
                if (r.NewTalkgroups.Count > 0)
                    lines.Add("New talkgroups: " + string.Join(", ", r.NewTalkgroups.Select(t => t.Name + " (" + t.Id + ")")) + ".");
                lines.AddRange(r.Notes);
                string head = "Added " + r.Added.Count + " repeater" + (r.Added.Count == 1 ? "" : "s") + " (" + r.Channels + " channels)" +
                              (d.NoaaAdded.Count > 0 ? " and " + d.NoaaAdded.Count + " NOAA weather channels" : "") +
                              ". Each repeater's zone, talkgroups and slots can be changed on the Repeaters tab.";
                using (var dlg = new IssuesDialog(head, new string[0], lines, false)) dlg.ShowDialog(owner);
                return r.Added.Concat(d.NoaaAdded).FirstOrDefault();
            }
        }
    }
}
