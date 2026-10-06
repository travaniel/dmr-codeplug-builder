using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Edits one repeater (or the hotspot): frequencies, zone, tones, and which talkgroups it carries.</summary>
    sealed class RepeaterEditor : UserControl
    {
        /// <summary>Any field changed (the repeater list uses this to refresh its row).</summary>
        public event EventHandler Changed;

        readonly bool hotspot;
        Session session;
        Repeater rep;
        bool loading;
        int lastSlot = 1;

        readonly TextBox txtName, txtPrefix, txtRx, txtTx, txtNotes, txtSearch;
        readonly ComboBox cboZone, cboMode, cboPower, cboOffset, cboEnc, cboDec, cboBw;
        readonly NumericUpDown numCC;
        readonly CheckBox chkToneSquelch, chkRxOnly, chkEnabled;
        readonly Label lblPrefix, lblExample, lblMode, lblPower, lblCC, lblEnc, lblDec, lblBw, lblNotes, lblAnalogNote;
        readonly GroupBox grpTalkgroups;
        readonly ListBox lstAvailable;
        readonly DataGridView grid;
        readonly DataGridViewTextBoxColumn colTg, colId, colName;
        readonly DataGridViewComboBoxColumn colSlot;
        readonly ErrorProvider errors = new ErrorProvider { BlinkStyle = ErrorBlinkStyle.NeverBlink };
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000 };

        static readonly string[] OffsetNames = { "Simplex", "+0.600 MHz", "-0.600 MHz", "+5.000 MHz", "-5.000 MHz", "Custom" };
        static readonly decimal?[] OffsetValues = { 0m, 0.6m, -0.6m, 5m, -5m, null };

        sealed class TgItem
        {
            public readonly Talkgroup Tg;
            public TgItem(Talkgroup tg) { Tg = tg; }
            public override string ToString()
            {
                return Tg.Name + "  (" + Tg.Id + (Tg.CallType == CallTypes.Private ? ", private" : Tg.CallType == CallTypes.All ? ", all call" : "") + ")";
            }
        }

        public RepeaterEditor(bool hotspotMode)
        {
            hotspot = hotspotMode;
            Font = Ui.BaseFont;
            Padding = new Padding(Ui.S(8), Ui.S(4), Ui.S(8), Ui.S(8));

            // ---------------- Fields ----------------
            var f = Ui.Grid(4);
            f.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            f.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            f.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            f.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            txtName = Ui.Text();
            cboZone = Ui.Combo(true);
            txtPrefix = Ui.Text("e.g. W5FC");
            lblPrefix = Ui.Label("Channel prefix");
            lblExample = Ui.Hint("");
            lblMode = Ui.Label("Type");
            cboMode = Ui.Combo(false, "Digital (DMR)", "Analog (FM)");
            cboPower = Ui.Combo(false, Powers.Values);
            txtRx = Ui.Text();
            txtTx = Ui.Text();
            cboOffset = Ui.Combo(false, OffsetNames);
            lblCC = Ui.Label("Color code");
            numCC = new NumericUpDown { Minimum = 0, Maximum = 15, Width = Ui.S(60), Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) };
            lblEnc = Ui.Label("Tone encode");
            lblDec = Ui.Label("Tone decode");
            cboEnc = Ui.Combo(true, new[] { "Off" }.Concat(Tones.Ctcss).ToArray());
            cboDec = Ui.Combo(true, new[] { "Off" }.Concat(Tones.Ctcss).ToArray());
            lblBw = Ui.Label("Bandwidth");
            cboBw = Ui.Combo(false, "12.5K (narrow)", "25K (wide)");
            chkToneSquelch = new CheckBox { Text = "Tone squelch (only open on the decode tone)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            chkRxOnly = new CheckBox { Text = "Receive only (TX prohibit)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            chkEnabled = new CheckBox { Text = "Include in codeplug", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            txtNotes = Ui.Text("Optional notes (not sent to the radio)");

            int r = 0;
            Place(f, r, Ui.Label("Name"), txtName, null, null); f.SetColumnSpan(txtName, 3); r++;
            Place(f, r, Ui.Label("Zone"), cboZone, lblPrefix, txtPrefix); r++;
            f.Controls.Add(lblExample, 1, r); f.SetColumnSpan(lblExample, 3); r++;
            lblPower = Ui.Label("Power");
            if (hotspot) Place(f, r, lblPower, cboPower, null, null);
            else Place(f, r, lblMode, cboMode, lblPower, cboPower);
            r++;
            Place(f, r, Ui.Label("Receive MHz"), txtRx, Ui.Label("Transmit MHz"), txtTx); r++;
            Place(f, r, Ui.Label("Offset"), cboOffset, lblCC, numCC); r++;
            Place(f, r, lblEnc, cboEnc, lblDec, cboDec); r++;
            Place(f, r, lblBw, cboBw, null, null);
            f.Controls.Add(chkToneSquelch, 2, r); f.SetColumnSpan(chkToneSquelch, 2); r++;
            f.Controls.Add(chkRxOnly, 1, r);
            f.Controls.Add(chkEnabled, 2, r); f.SetColumnSpan(chkEnabled, 2); r++;
            lblNotes = Ui.Label("Notes");
            Place(f, r, lblNotes, txtNotes, null, null); f.SetColumnSpan(txtNotes, 3); r++;
            f.RowCount = r;
            for (int i = 0; i < r; i++) f.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tips.SetToolTip(txtPrefix, "Channel names are made from this prefix plus the talkgroup name, e.g. \"W5FC Texas\".\nLeave it blank to name channels after the talkgroup only.\nNames are cut to 16 characters; you can also type a custom name per talkgroup below.");
            tips.SetToolTip(cboZone, "Pick an existing zone or type a new name. Every repeater with the same zone name goes into that zone.");
            tips.SetToolTip(txtRx, "The frequency your radio listens on: the repeater's output.");
            tips.SetToolTip(txtTx, "The frequency your radio transmits on: the repeater's input.");
            tips.SetToolTip(cboOffset, "Pick an offset to fill in the transmit frequency from the receive frequency.");
            tips.SetToolTip(chkEnabled, "Untick to keep this entry in the project but leave it out of the generated codeplug.");

            // ---------------- Talkgroups ----------------
            grpTalkgroups = new GroupBox { Text = "Talkgroups on this repeater (each one becomes a channel)", Dock = DockStyle.Fill, Padding = new Padding(Ui.S(8), Ui.S(4), Ui.S(8), Ui.S(8)) };
            var tg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            tg.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tg.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tg.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tg.Controls.Add(Ui.Label("Available talkgroups", true), 0, 0);
            tg.Controls.Add(Ui.Label("Channels on this repeater", true), 2, 0);

            var left = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3) };
            txtSearch = new TextBox { Dock = DockStyle.Top };
            Ui.SetCue(txtSearch, "Search name or ID");
            lstAvailable = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
            left.Controls.Add(lstAvailable);
            left.Controls.Add(txtSearch);
            lstAvailable.BringToFront();
            tg.Controls.Add(left, 0, 1);

            var leftHint = new Label
            {
                Text = "Double-click to add. Ctrl-click to pick several.",
                ForeColor = Ui.HintColor,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = Ui.S(34),
                Margin = new Padding(3, 0, 3, 0),
            };
            tg.Controls.Add(leftHint, 0, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(6, 18, 6, 3) };
            int bw = Ui.S(124);
            buttons.Controls.Add(Ui.Button("Add on slot 1  >", (s, e) => AddSelected(1), bw));
            buttons.Controls.Add(Ui.Button("Add on slot 2  >", (s, e) => AddSelected(2), bw));
            buttons.Controls.Add(Ui.Button("<  Remove", (s, e) => RemoveSelected(), bw));
            tg.Controls.Add(buttons, 1, 1);

            var gridButtons = Ui.Row(
                Ui.Button("Move up", (s, e) => MoveSelected(-1)),
                Ui.Button("Move down", (s, e) => MoveSelected(1)),
                Ui.Button("Switch slot", (s, e) => SwitchSlot()));
            tg.Controls.Add(gridButtons, 2, 2);

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
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                Margin = new Padding(3),
            };
            Ui.SetUpGrid(grid);
            colTg = new DataGridViewTextBoxColumn { HeaderText = "Talkgroup", ReadOnly = true, FillWeight = 42 };
            colId = new DataGridViewTextBoxColumn { HeaderText = "ID", ReadOnly = true, FillWeight = 18 };
            colSlot = new DataGridViewComboBoxColumn { HeaderText = "Slot", FillWeight = 14, FlatStyle = FlatStyle.Flat };
            colSlot.Items.AddRange("1", "2");
            colName = new DataGridViewTextBoxColumn { HeaderText = "Channel name", FillWeight = 46, MaxInputLength = 16 };
            grid.Columns.AddRange(colTg, colId, colSlot, colName);
            colName.ToolTipText = "Gray = automatic (prefix + talkgroup). Type to use your own name; clear it to go back to automatic.";
            tg.Controls.Add(grid, 2, 1);
            grpTalkgroups.Controls.Add(tg);

            lblAnalogNote = Ui.Hint("Analog repeaters and simplex channels make one channel each, using the tone settings above.");
            lblAnalogNote.Dock = DockStyle.Top;
            lblAnalogNote.Padding = new Padding(0, Ui.S(8), 0, 0);

            var lower = new Panel { Dock = DockStyle.Fill };
            lower.Controls.Add(grpTalkgroups);
            lower.Controls.Add(lblAnalogNote);

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(f, 0, 0);
            main.Controls.Add(lower, 0, 1);
            Controls.Add(main);

            if (hotspot)
            {
                lblMode.Visible = cboMode.Visible = false;
                chkEnabled.Visible = false;
                chkRxOnly.Visible = false;
                lblNotes.Visible = txtNotes.Visible = false;
            }

            WireEvents();
            Bind(null, null);
        }

        static void Place(TableLayoutPanel t, int row, Control l1, Control c1, Control l2, Control c2)
        {
            if (l1 != null) t.Controls.Add(l1, 0, row);
            if (c1 != null) t.Controls.Add(c1, 1, row);
            if (l2 != null) t.Controls.Add(l2, 2, row);
            if (c2 != null) t.Controls.Add(c2, 3, row);
        }

        int MaxName => session?.Project.Options.MaxNameLength > 0 ? session.Project.Options.MaxNameLength : 16;

        // ======================================================================
        // Binding
        // ======================================================================

        public Repeater Repeater => rep;

        public void Bind(Session s, Repeater r)
        {
            session = s;
            rep = r;
            loading = true;
            try
            {
                bool has = r != null;
                foreach (Control c in Controls) c.Enabled = has;
                if (!has)
                {
                    txtName.Text = txtPrefix.Text = txtRx.Text = txtTx.Text = txtNotes.Text = "";
                    cboZone.Text = "";
                    grid.Rows.Clear();
                    lstAvailable.Items.Clear();
                    lblExample.Text = "";
                    errors.Clear();
                    return;
                }
                txtName.Text = r.Name;
                RefillZones();
                cboZone.Text = r.Zone;
                txtPrefix.Text = r.Prefix;
                cboMode.SelectedIndex = r.IsDigital ? 0 : 1;
                cboPower.SelectedItem = Powers.Values.Contains(r.Power) ? r.Power : "High";
                txtRx.Text = Ui.FormatMHz(r.RxMHz);
                txtTx.Text = Ui.FormatMHz(r.TxMHz);
                SelectOffsetFromModel();
                numCC.Value = Math.Max(0, Math.Min(15, r.ColorCode));
                cboEnc.Text = Tones.Normalize(r.ToneEncode);
                cboDec.Text = Tones.Normalize(r.ToneDecode);
                cboBw.SelectedIndex = r.Bandwidth == Bandwidths.Narrow ? 0 : 1;
                chkToneSquelch.Checked = r.ToneSquelch;
                chkRxOnly.Checked = r.RxOnly;
                chkEnabled.Checked = r.Enabled;
                txtNotes.Text = r.Notes ?? "";
                lastSlot = r.Talkgroups.Count > 0 ? r.Talkgroups[r.Talkgroups.Count - 1].Slot : (hotspot ? 2 : 1);
                errors.Clear();
                RefreshAvailable();
                RefreshGrid();
                UpdateVisibility();
                UpdateExample();
                // Setting Text on an editable combo selects it all, and Windows keeps showing the highlight.
                // (Combos without a handle yet are cleared from their HandleCreated, see WireEvents.)
                foreach (var c in new[] { cboZone, cboEnc, cboDec })
                    if (c.IsHandleCreated) c.SelectionLength = 0;
            }
            finally { loading = false; }
        }

        /// <summary>Call when the talkgroup list or names change elsewhere.</summary>
        public void RefreshTalkgroups()
        {
            if (rep == null) return;
            RefreshAvailable();
            RefreshGrid();
            UpdateExample();
        }

        public void FocusName()
        {
            txtName.Focus();
            txtName.SelectAll();
        }

        void Raise()
        {
            if (loading || rep == null) return;
            session?.MarkDirty();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        void UpdateVisibility()
        {
            bool digital = rep == null || rep.IsDigital;
            lblPrefix.Visible = txtPrefix.Visible = digital;
            lblExample.Visible = true;
            lblCC.Visible = numCC.Visible = digital;
            lblEnc.Visible = cboEnc.Visible = lblDec.Visible = cboDec.Visible = !digital;
            lblBw.Visible = cboBw.Visible = chkToneSquelch.Visible = !digital;
            grpTalkgroups.Visible = digital;
            lblAnalogNote.Visible = !digital;
        }

        void RefillZones()
        {
            if (session == null) return;
            string text = cboZone.Text;
            cboZone.BeginUpdate();
            cboZone.Items.Clear();
            session.Project.SyncZones();
            foreach (var z in session.Project.Zones) cboZone.Items.Add(z.Name);
            cboZone.EndUpdate();
            cboZone.Text = text;
        }

        void SelectOffsetFromModel()
        {
            if (rep == null || rep.RxMHz <= 0 || rep.TxMHz <= 0)
            {
                cboOffset.SelectedIndex = hotspot ? 0 : OffsetNames.Length - 1;
                return;
            }
            decimal diff = rep.TxMHz - rep.RxMHz;
            int idx = Array.FindIndex(OffsetValues, v => v.HasValue && v.Value == diff);
            cboOffset.SelectedIndex = idx >= 0 ? idx : OffsetNames.Length - 1;
        }

        void UpdateExample()
        {
            if (rep == null) return;
            if (!rep.IsDigital)
            {
                string n = Naming.Clean(rep.Name, MaxName);
                lblExample.Text = "Channel name: \"" + n + "\"" + (Naming.Clean(rep.Name, 0).Length > MaxName ? "  (cut to " + MaxName + " characters)" : "");
                return;
            }
            var names = rep.Talkgroups
                .Select(e => session.Project.FindTalkgroup(e.TalkgroupId))
                .Where(t => t != null)
                .Take(2)
                .Select(t => "\"" + Naming.AutoChannelName(rep.Prefix, t.Name, MaxName) + "\"")
                .ToList();
            if (names.Count == 0) names.Add("\"" + Naming.AutoChannelName(rep.Prefix, "Talkgroup", MaxName) + "\"");
            lblExample.Text = "Channels are named like " + string.Join(", ", names) + (string.IsNullOrWhiteSpace(rep.Prefix) ? "  (no prefix: talkgroup name only)" : "");
        }

        // ======================================================================
        // Field events
        // ======================================================================

        void WireEvents()
        {
            foreach (var c in new[] { cboZone, cboEnc, cboDec })
            {
                var box = c;
                box.HandleCreated += (s, e) => box.BeginInvoke((Action)(() => { if (!box.Focused) box.SelectionLength = 0; }));
            }
            txtName.TextChanged += (s, e) => { if (loading || rep == null) return; rep.Name = txtName.Text.Trim(); UpdateExample(); Raise(); };
            txtPrefix.TextChanged += (s, e) =>
            {
                if (loading || rep == null) return;
                rep.Prefix = txtPrefix.Text.Trim();
                RefreshGridNames();
                UpdateExample();
                Raise();
            };
            cboZone.TextChanged += (s, e) => { if (loading || rep == null) return; rep.Zone = cboZone.Text.Trim(); Raise(); };
            cboZone.DropDown += (s, e) => { bool l = loading; loading = true; RefillZones(); loading = l; };
            cboZone.Leave += (s, e) => ApplyZone();
            cboZone.SelectionChangeCommitted += (s, e) => BeginInvoke((Action)ApplyZone); // Text is updated after this event
            cboMode.SelectedIndexChanged += (s, e) =>
            {
                if (loading || rep == null) return;
                rep.Mode = cboMode.SelectedIndex == 1 ? Modes.Analog : Modes.Digital;
                if (!rep.IsDigital && rep.Bandwidth == Bandwidths.Narrow && rep.Talkgroups.Count > 0)
                {
                    rep.Bandwidth = Bandwidths.Wide;
                    loading = true; cboBw.SelectedIndex = 1; loading = false;
                }
                UpdateVisibility();
                UpdateExample();
                Raise();
            };
            cboPower.SelectedIndexChanged += (s, e) => { if (loading || rep == null) return; rep.Power = (string)cboPower.SelectedItem; Raise(); };

            txtRx.TextChanged += (s, e) =>
            {
                if (loading || rep == null) return;
                decimal? v = Ui.ParseMHz(txtRx.Text);
                if (v == null && txtRx.Text.Trim().Length > 0) { errors.SetError(txtRx, "Type the frequency in MHz, e.g. 146.940"); return; }
                errors.SetError(txtRx, "");
                rep.RxMHz = v ?? 0;
                if (rep.TxMHz <= 0 && Validator.InRadioBand(rep.RxMHz) && !hotspot)
                {
                    // Nothing typed for transmit yet: suggest the usual offset for that band.
                    int idx = Array.IndexOf(OffsetValues, SuggestOffset(rep.RxMHz));
                    if (idx >= 0) { loading = true; cboOffset.SelectedIndex = idx; loading = false; }
                }
                decimal? off = cboOffset.SelectedIndex >= 0 ? OffsetValues[cboOffset.SelectedIndex] : null;
                if (off.HasValue && rep.RxMHz > 0)
                {
                    rep.TxMHz = rep.RxMHz + off.Value;
                    loading = true; txtTx.Text = Ui.FormatMHz(rep.TxMHz); loading = false;
                    errors.SetError(txtTx, "");
                }
                CheckBand();
                Raise();
            };
            txtTx.TextChanged += (s, e) =>
            {
                if (loading || rep == null) return;
                decimal? v = Ui.ParseMHz(txtTx.Text);
                if (v == null && txtTx.Text.Trim().Length > 0) { errors.SetError(txtTx, "Type the frequency in MHz, e.g. 146.340"); return; }
                errors.SetError(txtTx, "");
                rep.TxMHz = v ?? 0;
                loading = true; SelectOffsetFromModel(); loading = false;
                CheckBand();
                Raise();
            };
            txtRx.Leave += (s, e) => { if (rep != null && Ui.ParseMHz(txtRx.Text) != null) { loading = true; txtRx.Text = Ui.FormatMHz(rep.RxMHz); loading = false; } };
            txtTx.Leave += (s, e) => { if (rep != null && Ui.ParseMHz(txtTx.Text) != null) { loading = true; txtTx.Text = Ui.FormatMHz(rep.TxMHz); loading = false; } };
            cboOffset.SelectedIndexChanged += (s, e) =>
            {
                if (loading || rep == null || cboOffset.SelectedIndex < 0) return;
                decimal? off = OffsetValues[cboOffset.SelectedIndex];
                if (off.HasValue && rep.RxMHz > 0)
                {
                    rep.TxMHz = rep.RxMHz + off.Value;
                    loading = true; txtTx.Text = Ui.FormatMHz(rep.TxMHz); loading = false;
                    errors.SetError(txtTx, "");
                    CheckBand();
                    Raise();
                }
            };
            numCC.ValueChanged += (s, e) => { if (loading || rep == null) return; rep.ColorCode = (int)numCC.Value; Raise(); };

            cboEnc.TextChanged += (s, e) => { if (loading || rep == null) return; rep.ToneEncode = Tones.Normalize(cboEnc.Text); CheckTone(cboEnc); Raise(); };
            cboDec.TextChanged += (s, e) =>
            {
                if (loading || rep == null) return;
                bool wasOff = Tones.Normalize(rep.ToneDecode) == "Off";
                rep.ToneDecode = Tones.Normalize(cboDec.Text);
                CheckTone(cboDec);
                if (wasOff && rep.ToneDecode != "Off" && Tones.IsValid(rep.ToneDecode) && !chkToneSquelch.Checked)
                    chkToneSquelch.Checked = true; // decoding a tone almost always means tone squelch
                Raise();
            };
            cboEnc.Leave += (s, e) => { if (rep != null && Tones.IsValid(cboEnc.Text)) { loading = true; cboEnc.Text = Tones.Normalize(cboEnc.Text); loading = false; } };
            cboDec.Leave += (s, e) => { if (rep != null && Tones.IsValid(cboDec.Text)) { loading = true; cboDec.Text = Tones.Normalize(cboDec.Text); loading = false; } };
            cboBw.SelectedIndexChanged += (s, e) => { if (loading || rep == null) return; rep.Bandwidth = cboBw.SelectedIndex == 0 ? Bandwidths.Narrow : Bandwidths.Wide; Raise(); };
            chkToneSquelch.CheckedChanged += (s, e) => { if (loading || rep == null) return; rep.ToneSquelch = chkToneSquelch.Checked; Raise(); };
            chkRxOnly.CheckedChanged += (s, e) => { if (loading || rep == null) return; rep.RxOnly = chkRxOnly.Checked; Raise(); };
            chkEnabled.CheckedChanged += (s, e) => { if (loading || rep == null) return; rep.Enabled = chkEnabled.Checked; Raise(); };
            txtNotes.TextChanged += (s, e) => { if (loading || rep == null) return; rep.Notes = txtNotes.Text; Raise(); };

            // Talkgroup assignment
            txtSearch.TextChanged += (s, e) => RefreshAvailable();
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down && lstAvailable.Items.Count > 0)
                {
                    lstAvailable.Focus();
                    lstAvailable.ClearSelected();
                    lstAvailable.SelectedIndex = 0;
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    AddSelected(lastSlot);
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            lstAvailable.DoubleClick += (s, e) => AddSelected(lastSlot);
            lstAvailable.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddSelected(lastSlot); e.Handled = e.SuppressKeyPress = true; } };

            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var entry = grid.Rows[e.RowIndex].Tag as RepeaterTalkgroup;
                if (entry == null) return;
                if (e.ColumnIndex == colName.Index && string.IsNullOrWhiteSpace(entry.ChannelName))
                    e.CellStyle.ForeColor = Ui.AutoNameColor;
                if (e.ColumnIndex == colTg.Index && entry.FromZone)
                    e.CellStyle.ForeColor = Ui.ZoneColor;
            };
            grid.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == colTg.Index && (grid.Rows[e.RowIndex].Tag as RepeaterTalkgroup)?.FromZone == true)
                    e.ToolTipText = "From the zone's talkgroups (Zones tab). It follows the zone: change it there for every repeater at once.";
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode) { RemoveSelected(); e.Handled = true; }
            };
        }

        /// <summary>
        /// Once a zone is settled (picked from the list or typed and left): the repeater takes on that zone's
        /// talkgroup set and drops channels the previous zone's set had put on it.
        /// </summary>
        void ApplyZone()
        {
            if (session == null || rep == null) return;
            session.Project.SyncZones();
            string sig = string.Join(",", rep.Talkgroups.Select(t => t.TalkgroupId + ":" + t.Slot));
            session.Project.ApplyZoneTalkgroups(rep);
            if (string.Join(",", rep.Talkgroups.Select(t => t.TalkgroupId + ":" + t.Slot)) == sig) return;
            RefreshGrid();
            UpdateExample();
            Raise();
        }

        /// <summary>US band-plan repeater offsets: 2 m below 147 MHz is -0.6, above is +0.6; 70 cm below 445 MHz is +5, above is -5.</summary>
        static decimal? SuggestOffset(decimal rx)
        {
            return BandPlan.SuggestOffset(rx) ?? 0m;
        }

        void CheckBand()
        {
            foreach (var box in new[] { txtRx, txtTx })
            {
                decimal? v = Ui.ParseMHz(box.Text);
                if (v.HasValue && !Validator.InRadioBand(v.Value))
                    errors.SetError(box, "Outside the radio's 136-174 / 400-480 MHz range");
                else if (v.HasValue)
                    errors.SetError(box, "");
            }
        }

        void CheckTone(ComboBox box)
        {
            errors.SetError(box, Tones.IsValid(box.Text) ? "" : "Use Off, a CTCSS tone like 100.0, or a DCS code like D023N");
        }

        // ======================================================================
        // Talkgroup lists
        // ======================================================================

        void RefreshAvailable()
        {
            if (session == null) return;
            var selected = new HashSet<int>(lstAvailable.SelectedItems.Cast<TgItem>().Select(i => i.Tg.Id));
            string q = txtSearch.Text.Trim();
            var items = session.Project.Talkgroups
                .Where(t => q.Length == 0
                            || (t.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || t.Id.ToString(CultureInfo.InvariantCulture).StartsWith(q))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new TgItem(t))
                .ToList();
            lstAvailable.BeginUpdate();
            lstAvailable.Items.Clear();
            foreach (var i in items) lstAvailable.Items.Add(i);
            for (int i = 0; i < lstAvailable.Items.Count; i++)
                if (selected.Contains(((TgItem)lstAvailable.Items[i]).Tg.Id)) lstAvailable.SetSelected(i, true);
            lstAvailable.EndUpdate();
        }

        void RefreshGrid(IEnumerable<RepeaterTalkgroup> select = null)
        {
            if (rep == null) return;
            bool l = loading;
            loading = true;
            try
            {
                var keep = select != null ? new HashSet<RepeaterTalkgroup>(select) : new HashSet<RepeaterTalkgroup>(SelectedEntries());
                grid.Rows.Clear();
                foreach (var e in rep.Talkgroups)
                {
                    var t = session.Project.FindTalkgroup(e.TalkgroupId);
                    string tgName = t == null ? "(not in list)" : t.Name + (t.CallType == CallTypes.Private ? " (private)" : "");
                    int i = grid.Rows.Add(tgName, e.TalkgroupId.ToString(CultureInfo.InvariantCulture), e.Slot == 2 ? "2" : "1", DisplayName(e, t));
                    grid.Rows[i].Tag = e;
                    if (t == null) grid.Rows[i].DefaultCellStyle.ForeColor = Color.Firebrick;
                }
                grid.ClearSelection();
                foreach (DataGridViewRow row in grid.Rows)
                    if (keep.Contains((RepeaterTalkgroup)row.Tag)) row.Selected = true;
            }
            finally { loading = l; }
        }

        string DisplayName(RepeaterTalkgroup e, Talkgroup t)
        {
            if (!string.IsNullOrWhiteSpace(e.ChannelName)) return e.ChannelName;
            return t == null ? "" : Naming.AutoChannelName(rep.Prefix, t.Name, MaxName);
        }

        void RefreshGridNames()
        {
            bool l = loading;
            loading = true;
            foreach (DataGridViewRow row in grid.Rows)
            {
                var e = (RepeaterTalkgroup)row.Tag;
                row.Cells[colName.Index].Value = DisplayName(e, session.Project.FindTalkgroup(e.TalkgroupId));
            }
            loading = l;
        }

        void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || rep == null || e.RowIndex < 0) return;
            var entry = (RepeaterTalkgroup)grid.Rows[e.RowIndex].Tag;
            var t = session.Project.FindTalkgroup(entry.TalkgroupId);
            if (e.ColumnIndex == colSlot.Index)
            {
                entry.Slot = (grid.Rows[e.RowIndex].Cells[colSlot.Index].Value as string) == "2" ? 2 : 1;
                lastSlot = entry.Slot;
                Raise();
            }
            else if (e.ColumnIndex == colName.Index)
            {
                string typed = Naming.Clean(grid.Rows[e.RowIndex].Cells[colName.Index].Value as string, MaxName);
                string auto = t == null ? "" : Naming.AutoChannelName(rep.Prefix, t.Name, MaxName);
                entry.ChannelName = typed.Length == 0 || typed == auto ? null : typed;
                loading = true;
                grid.Rows[e.RowIndex].Cells[colName.Index].Value = DisplayName(entry, t);
                loading = false;
                grid.InvalidateRow(e.RowIndex);
                Raise();
            }
        }

        List<RepeaterTalkgroup> SelectedEntries()
        {
            return grid.SelectedRows.Cast<DataGridViewRow>()
                .OrderBy(r => r.Index)
                .Select(r => (RepeaterTalkgroup)r.Tag)
                .ToList();
        }

        void AddSelected(int slot)
        {
            if (rep == null) return;
            var picked = lstAvailable.SelectedItems.Cast<TgItem>().Select(i => i.Tg).ToList();
            if (picked.Count == 0 && lstAvailable.Items.Count == 1) picked.Add(((TgItem)lstAvailable.Items[0]).Tg);
            if (picked.Count == 0)
            {
                Ui.Info(FindForm(), "Pick one or more talkgroups on the left first.\n\nNeed a talkgroup that isn't listed? Add it on the Talkgroups tab.");
                return;
            }
            var added = new List<RepeaterTalkgroup>();
            foreach (var t in picked)
            {
                if (rep.Talkgroups.Any(x => x.TalkgroupId == t.Id && x.Slot == slot)) continue;
                var entry = new RepeaterTalkgroup(t.Id, slot);
                rep.Talkgroups.Add(entry);
                added.Add(entry);
            }
            lastSlot = slot;
            RefreshGrid(added);
            UpdateExample();
            if (added.Count > 0)
            {
                var last = grid.Rows.Cast<DataGridViewRow>().LastOrDefault(r => r.Selected);
                if (last != null) grid.FirstDisplayedScrollingRowIndex = Math.Max(0, last.Index - 3);
                Raise();
            }
        }

        void RemoveSelected()
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            foreach (var e in sel) rep.Talkgroups.Remove(e);
            RefreshGrid(new RepeaterTalkgroup[0]);
            UpdateExample();
            Raise();
        }

        void MoveSelected(int delta)
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            var list = rep.Talkgroups;
            var order = delta < 0 ? sel : Enumerable.Reverse(sel).ToList();
            foreach (var e in order)
            {
                int i = list.IndexOf(e);
                int j = i + delta;
                if (j < 0 || j >= list.Count || sel.Contains(list[j])) continue;
                list.RemoveAt(i);
                list.Insert(j, e);
            }
            RefreshGrid(sel);
            UpdateExample();
            Raise();
        }

        void SwitchSlot()
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            foreach (var e in sel) e.Slot = e.Slot == 2 ? 1 : 2;
            RefreshGrid(sel);
            Raise();
        }
    }
}
