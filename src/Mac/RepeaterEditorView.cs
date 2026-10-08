using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Edits one repeater (or the hotspot): frequencies, zone, tones, and which talkgroups it carries. The same fields and
    /// rules as the Windows RepeaterEditor.
    /// </summary>
    sealed class RepeaterEditorView : UserControl
    {
        /// <summary>Any field changed (the repeater list uses this to refresh its row).</summary>
        public event EventHandler Changed;

        readonly bool hotspot;
        Session session;
        Repeater rep;
        bool loading;
        int lastSlot = 1;

        readonly TextBox txtName, txtPrefix, txtRx, txtTx, txtNotes, txtSearch;
        readonly AutoCompleteBox cboZone, cboEnc, cboDec;
        readonly ComboBox cboMode, cboPower, cboOffset, cboBw;
        readonly NumericUpDown numCC;
        readonly CheckBox chkToneSquelch, chkRxOnly, chkEnabled, chkOffAir;
        string offAirDate; // kept while the box is unticked, so ticking it again puts the date back
        readonly Control lblPrefix, lblExample, lblMode, lblPower, lblCC, lblEnc, lblDec, lblBw, lblNotes, lblAnalogNote;
        readonly TextBlock example, problem;
        readonly Control grpTalkgroups, fields;
        readonly ListBox lstAvailable;
        readonly DataGrid grid;
        List<TgRow> rows = new List<TgRow>();

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

        /// <summary>A channel on the repeater, as the grid shows it.</summary>
        sealed class TgRow : RowBase
        {
            readonly RepeaterEditorView owner;
            public readonly RepeaterTalkgroup Entry;
            public TgRow(RepeaterEditorView owner, RepeaterTalkgroup e) { this.owner = owner; Entry = e; }
            Talkgroup Tg => owner.session.Project.FindTalkgroup(Entry.TalkgroupId);
            public string Talkgroup => Tg == null ? "(not in list)" : Tg.Name + (Tg.CallType == CallTypes.Private ? " (private)" : "") + (Entry.FromZone ? "  (zone)" : "");
            public string Id => Entry.TalkgroupId.ToString(CultureInfo.InvariantCulture);
            public string Slot => Entry.Slot == 2 ? "2" : "1";
            public string ChannelName
            {
                get { return owner.DisplayName(Entry, Tg); }
                set { owner.NameTyped(this, value); }
            }
        }

        public RepeaterEditorView(bool hotspotMode)
        {
            hotspot = hotspotMode;

            // ---------------- Fields ----------------
            txtName = new TextBox();
            cboZone = UiKit.Suggest(new string[0]);
            txtPrefix = new TextBox { Watermark = "e.g. W5FC" };
            lblPrefix = UiKit.Label("Channel prefix");
            example = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            lblExample = example;
            lblMode = UiKit.Label("Type");
            cboMode = new ComboBox { ItemsSource = new[] { "Digital (DMR)", "Analog (FM)" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            lblPower = UiKit.Label("Power");
            cboPower = new ComboBox { ItemsSource = Powers.Values, HorizontalAlignment = HorizontalAlignment.Stretch };
            txtRx = new TextBox();
            txtTx = new TextBox();
            cboOffset = new ComboBox { ItemsSource = OffsetNames, HorizontalAlignment = HorizontalAlignment.Stretch };
            lblCC = UiKit.Label("Color code");
            numCC = new NumericUpDown { Minimum = 0, Maximum = 15, Increment = 1, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            lblEnc = UiKit.Label("Tone encode");
            lblDec = UiKit.Label("Tone decode");
            cboEnc = UiKit.Suggest(new[] { "Off" }.Concat(Tones.Ctcss));
            cboDec = UiKit.Suggest(new[] { "Off" }.Concat(Tones.Ctcss));
            lblBw = UiKit.Label("Bandwidth");
            cboBw = new ComboBox { ItemsSource = new[] { "12.5K (narrow)", "25K (wide)" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            chkToneSquelch = new CheckBox { Content = "Tone squelch (only open on the decode tone)" };
            chkRxOnly = new CheckBox { Content = "Receive only (TX prohibit)" };
            chkEnabled = new CheckBox { Content = "Include in codeplug" };
            txtNotes = new TextBox { Watermark = "Optional notes (not sent to the radio)" };
            chkOffAir = new CheckBox { Foreground = Brushes.DarkOrange, IsVisible = false };
            lblNotes = UiKit.Label("Notes");
            problem = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(txtPrefix, "Channel names are made from this prefix plus the talkgroup name, e.g. \"W5FC Texas\". Leave it blank to name channels after the talkgroup only. Longer names are shortened to 16 characters; you can also type a custom name per talkgroup below.");
            ToolTip.SetTip(cboZone, "Pick an existing zone or type a new name. Every repeater with the same zone name goes into that zone.");
            ToolTip.SetTip(txtRx, "The frequency your radio listens on: the repeater's output.");
            ToolTip.SetTip(txtTx, "The frequency your radio transmits on: the repeater's input.");
            ToolTip.SetTip(cboOffset, "Pick an offset to fill in the transmit frequency from the receive frequency.");
            ToolTip.SetTip(chkEnabled, "Untick to keep this entry in the project but leave it out of the generated codeplug.");

            var f = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), RowSpacing = 6, ColumnSpacing = 8 };
            int r = 0;
            void Add(Control c, int col, int row, int span = 1)
            {
                Grid.SetColumn(c, col); Grid.SetRow(c, row); if (span > 1) Grid.SetColumnSpan(c, span);
                f.Children.Add(c);
                while (f.RowDefinitions.Count <= row) f.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            Add(UiKit.Label("Name"), 0, r); Add(txtName, 1, r, 3); r++;
            Add(UiKit.Label("Zone"), 0, r); Add(cboZone, 1, r); Add(lblPrefix, 2, r); Add(txtPrefix, 3, r); r++;
            Add(lblExample, 1, r, 3); r++;
            if (hotspot) { Add(lblPower, 0, r); Add(cboPower, 1, r); }
            else { Add(lblMode, 0, r); Add(cboMode, 1, r); Add(lblPower, 2, r); Add(cboPower, 3, r); }
            r++;
            Add(UiKit.Label("Receive MHz"), 0, r); Add(txtRx, 1, r); Add(UiKit.Label("Transmit MHz"), 2, r); Add(txtTx, 3, r); r++;
            Add(UiKit.Label("Offset"), 0, r); Add(cboOffset, 1, r); Add(lblCC, 2, r); Add(numCC, 3, r); r++;
            Add(lblEnc, 0, r); Add(cboEnc, 1, r); Add(lblDec, 2, r); Add(cboDec, 3, r); r++;
            Add(lblBw, 0, r); Add(cboBw, 1, r); Add(chkToneSquelch, 2, r, 2); r++;
            Add(chkRxOnly, 1, r); Add(chkEnabled, 2, r, 2); r++;
            Add(lblNotes, 0, r); Add(txtNotes, 1, r, 3); r++;
            Add(chkOffAir, 1, r, 3); r++;
            Add(problem, 1, r, 3);
            fields = f;

            // ---------------- Talkgroups ----------------
            txtSearch = new TextBox { Watermark = "Search name or ID" };
            lstAvailable = new ListBox { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, MinHeight = 160 };
            var left = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(txtSearch, Dock.Top);
            var leftHint = UiKit.Hint("Double-click to add. Click several to pick more than one.");
            DockPanel.SetDock(leftHint, Dock.Bottom);
            left.Children.Add(txtSearch);
            left.Children.Add(leftHint);
            left.Children.Add(new ScrollViewer { Content = lstAvailable });

            var mid = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            mid.Children.Add(UiKit.Button("Add on slot 1  >", () => AddSelected(1), 124));
            mid.Children.Add(UiKit.Button("Add on slot 2  >", () => AddSelected(2), 124));
            mid.Children.Add(UiKit.Button("<  Remove", RemoveSelected, 124));

            grid = UiKit.Grid(true);
            grid.Columns.Add(UiKit.Col("Talkgroup", "Talkgroup", true, 180));
            grid.Columns.Add(UiKit.Col("ID", "Id", true, 80));
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Slot",
                Width = new DataGridLength(80),
                CellTemplate = new FuncDataTemplate<TgRow>((row, ns) =>
                {
                    var box = new ComboBox { ItemsSource = new[] { "1", "2" }, SelectedItem = row?.Slot, MinWidth = 64 };
                    box.SelectionChanged += (s, e) =>
                    {
                        if (loading || row == null || !(box.SelectedItem is string v) || v == row.Slot) return;
                        row.Entry.Slot = v == "2" ? 2 : 1;
                        lastSlot = row.Entry.Slot;
                        foreach (var x in rows) x.Refresh(); // a talkgroup on both slots is named with its slot
                        Raise();
                    };
                    return box;
                }),
            });
            grid.Columns.Add(UiKit.Col("Channel name", "ChannelName", false, 220));
            grid.KeyDown += (s, e) => { if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; } };
            var gridButtons = UiKit.Row(UiKit.Button("Move up", () => MoveSelected(-1)), UiKit.Button("Move down", () => MoveSelected(1)), UiKit.Button("Switch slot", SwitchSlot));
            gridButtons.Margin = new Thickness(0, 6, 0, 0);

            var right = new DockPanel();
            DockPanel.SetDock(gridButtons, Dock.Bottom);
            right.Children.Add(gridButtons);
            right.Children.Add(grid);

            var tg = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,1.6*"), RowDefinitions = new RowDefinitions("Auto,*"), MinHeight = 240, Margin = new Thickness(0, 8, 0, 0) };
            var l1 = UiKit.Label("Available talkgroups", true); var l2 = UiKit.Label("Channels on this repeater", true);
            Grid.SetColumn(l2, 2);
            Grid.SetRow(left, 1); Grid.SetRow(mid, 1); Grid.SetColumn(mid, 1); Grid.SetRow(right, 1); Grid.SetColumn(right, 2);
            tg.Children.Add(l1); tg.Children.Add(l2); tg.Children.Add(left); tg.Children.Add(mid); tg.Children.Add(right);
            var grp = new StackPanel { Children = { UiKit.Label("Talkgroups on this repeater (each one becomes a channel)", true), tg } };
            grpTalkgroups = grp;
            lblAnalogNote = UiKit.Hint("Analog repeaters and simplex channels make one channel each, using the tone settings above.");
            lblAnalogNote.Margin = new Thickness(0, 8, 0, 0);

            var main = new StackPanel { Margin = new Thickness(12), Spacing = 6 };
            main.Children.Add(fields);
            main.Children.Add(grpTalkgroups);
            main.Children.Add(lblAnalogNote);
            Content = new ScrollViewer { Content = main, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

            if (hotspot)
            {
                lblMode.IsVisible = cboMode.IsVisible = false;
                chkEnabled.IsVisible = false;
                chkRxOnly.IsVisible = false;
                lblNotes.IsVisible = txtNotes.IsVisible = false;
            }
            WireEvents();
            Bind(null, null);
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
                IsEnabled = has;
                problem.Text = "";
                if (!has)
                {
                    txtName.Text = txtPrefix.Text = txtRx.Text = txtTx.Text = txtNotes.Text = "";
                    cboZone.Text = "";
                    rows = new List<TgRow>();
                    grid.ItemsSource = rows;
                    lstAvailable.ItemsSource = new List<TgItem>();
                    example.Text = "";
                    chkOffAir.IsVisible = false;
                    return;
                }
                txtName.Text = r.Name;
                RefillZones();
                cboZone.Text = r.Zone ?? "";
                txtPrefix.Text = r.Prefix;
                cboMode.SelectedIndex = r.IsDigital ? 0 : 1;
                cboPower.SelectedItem = Powers.Values.Contains(r.Power) ? r.Power : "High";
                txtRx.Text = UiKit.FormatMHz(r.RxMHz);
                txtTx.Text = UiKit.FormatMHz(r.TxMHz);
                SelectOffsetFromModel();
                numCC.Value = Math.Max(0, Math.Min(15, r.ColorCode));
                cboEnc.Text = Tones.Normalize(r.ToneEncode);
                cboDec.Text = Tones.Normalize(r.ToneDecode);
                cboBw.SelectedIndex = r.Bandwidth == Bandwidths.Narrow ? 0 : 1;
                chkToneSquelch.IsChecked = r.ToneSquelch;
                chkRxOnly.IsChecked = r.RxOnly;
                chkEnabled.IsChecked = r.Enabled;
                txtNotes.Text = r.Notes ?? "";
                offAirDate = r.OffAirSince;
                chkOffAir.IsVisible = !hotspot && !string.IsNullOrWhiteSpace(offAirDate);
                chkOffAir.Content = "Off the air? BrandMeister last heard it on " + offAirDate + ". Untick if you know it works.";
                chkOffAir.IsChecked = chkOffAir.IsVisible;
                lastSlot = r.Talkgroups.Count > 0 ? r.Talkgroups[r.Talkgroups.Count - 1].Slot : (hotspot ? 2 : 1);
                RefreshAvailable();
                RefreshGrid();
                UpdateVisibility();
                UpdateExample();
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

        public void FocusName() { txtName.Focus(); txtName.SelectAll(); }

        void Raise()
        {
            if (loading || rep == null) return;
            session?.MarkDirty();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        void UpdateVisibility()
        {
            bool digital = rep == null || rep.IsDigital;
            lblPrefix.IsVisible = txtPrefix.IsVisible = digital;
            lblCC.IsVisible = numCC.IsVisible = digital;
            lblEnc.IsVisible = cboEnc.IsVisible = lblDec.IsVisible = cboDec.IsVisible = !digital;
            lblBw.IsVisible = cboBw.IsVisible = chkToneSquelch.IsVisible = !digital;
            grpTalkgroups.IsVisible = digital;
            lblAnalogNote.IsVisible = !digital;
        }

        void RefillZones()
        {
            if (session == null) return;
            session.Project.SyncZones();
            cboZone.ItemsSource = session.Project.Zones.Select(z => z.Name).ToList();
        }

        void SelectOffsetFromModel()
        {
            if (rep == null || rep.RxMHz <= 0 || rep.TxMHz <= 0) { cboOffset.SelectedIndex = hotspot ? 0 : OffsetNames.Length - 1; return; }
            decimal diff = rep.TxMHz - rep.RxMHz;
            int idx = Array.FindIndex(OffsetValues, v => v.HasValue && v.Value == diff);
            cboOffset.SelectedIndex = idx >= 0 ? idx : OffsetNames.Length - 1;
        }

        void UpdateExample()
        {
            if (rep == null) return;
            if (!rep.IsDigital)
            {
                // The same shortening the generator uses (Naming.Fit), so this is the name the radio shows.
                string n = Naming.Fit(rep.Name, MaxName);
                example.Text = "Channel name: \"" + n + "\"" + (Naming.Clean(rep.Name, 0).Length > MaxName ? "  (shortened to " + MaxName + " characters)" : "");
                return;
            }
            var names = rep.Talkgroups
                .Select(e => new { e, t = session.Project.FindTalkgroup(e.TalkgroupId) })
                .Where(x => x.t != null).Take(2)
                .Select(x => "\"" + rep.AutoChannelName(x.e, x.t.Name, MaxName) + "\"").ToList();
            if (names.Count == 0) names.Add("\"" + Naming.AutoChannelName(rep.Prefix, "Talkgroup", MaxName) + "\"");
            example.Text = "Channels are named like " + string.Join(", ", names) + (string.IsNullOrWhiteSpace(rep.Prefix) ? "  (no prefix: talkgroup name only)" : "");
        }

        // ======================================================================
        // Field events
        // ======================================================================

        void WireEvents()
        {
            UiKit.OnText(txtName, () => { if (loading || rep == null) return; rep.Name = (txtName.Text ?? "").Trim(); UpdateExample(); Raise(); });
            UiKit.OnText(txtPrefix, () =>
            {
                if (loading || rep == null) return;
                rep.Prefix = (txtPrefix.Text ?? "").Trim();
                foreach (var x in rows) x.Refresh();
                UpdateExample();
                Raise();
            });
            UiKit.OnText(cboZone, () => { if (loading || rep == null) return; rep.Zone = (cboZone.Text ?? "").Trim(); Raise(); });
            cboZone.LostFocus += (s, e) => ApplyZone();
            cboZone.DropDownClosed += (s, e) => ApplyZone();
            cboMode.SelectionChanged += (s, e) =>
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
            cboPower.SelectionChanged += (s, e) => { if (loading || rep == null || !(cboPower.SelectedItem is string p)) return; rep.Power = p; Raise(); };

            UiKit.OnText(txtRx, () =>
            {
                if (loading || rep == null) return;
                decimal? v = UiKit.ParseMHz(txtRx.Text);
                if (v == null && (txtRx.Text ?? "").Trim().Length > 0) { problem.Text = "Type the receive frequency in MHz, e.g. 146.940"; return; }
                problem.Text = "";
                rep.RxMHz = v ?? 0;
                if (rep.TxMHz <= 0 && Validator.InRadioBand(rep.RxMHz) && !hotspot)
                {
                    // Nothing typed for transmit yet: suggest the usual offset for that band.
                    int idx = Array.IndexOf(OffsetValues, BandPlan.SuggestOffset(rep.RxMHz) ?? 0m);
                    if (idx >= 0) { loading = true; cboOffset.SelectedIndex = idx; loading = false; }
                }
                decimal? off = cboOffset.SelectedIndex >= 0 ? OffsetValues[cboOffset.SelectedIndex] : null;
                if (off.HasValue && rep.RxMHz > 0)
                {
                    rep.TxMHz = rep.RxMHz + off.Value;
                    loading = true; txtTx.Text = UiKit.FormatMHz(rep.TxMHz); loading = false;
                }
                CheckBand();
                Raise();
            });
            UiKit.OnText(txtTx, () =>
            {
                if (loading || rep == null) return;
                decimal? v = UiKit.ParseMHz(txtTx.Text);
                if (v == null && (txtTx.Text ?? "").Trim().Length > 0) { problem.Text = "Type the transmit frequency in MHz, e.g. 146.340"; return; }
                problem.Text = "";
                rep.TxMHz = v ?? 0;
                loading = true; SelectOffsetFromModel(); loading = false;
                CheckBand();
                Raise();
            });
            txtRx.LostFocus += (s, e) => { if (rep != null && UiKit.ParseMHz(txtRx.Text) != null) { loading = true; txtRx.Text = UiKit.FormatMHz(rep.RxMHz); loading = false; } };
            txtTx.LostFocus += (s, e) => { if (rep != null && UiKit.ParseMHz(txtTx.Text) != null) { loading = true; txtTx.Text = UiKit.FormatMHz(rep.TxMHz); loading = false; } };
            cboOffset.SelectionChanged += (s, e) =>
            {
                if (loading || rep == null || cboOffset.SelectedIndex < 0) return;
                decimal? off = OffsetValues[cboOffset.SelectedIndex];
                if (off.HasValue && rep.RxMHz > 0)
                {
                    rep.TxMHz = rep.RxMHz + off.Value;
                    loading = true; txtTx.Text = UiKit.FormatMHz(rep.TxMHz); loading = false;
                    CheckBand();
                    Raise();
                }
            };
            numCC.ValueChanged += (s, e) => { if (loading || rep == null) return; rep.ColorCode = (int)(numCC.Value ?? 0); Raise(); };

            UiKit.OnText(cboEnc, () => { if (loading || rep == null) return; rep.ToneEncode = Tones.Normalize(cboEnc.Text); CheckTone(cboEnc); Raise(); });
            UiKit.OnText(cboDec, () =>
            {
                if (loading || rep == null) return;
                bool wasOff = Tones.Normalize(rep.ToneDecode) == "Off";
                rep.ToneDecode = Tones.Normalize(cboDec.Text);
                CheckTone(cboDec);
                if (wasOff && rep.ToneDecode != "Off" && Tones.IsValid(rep.ToneDecode) && chkToneSquelch.IsChecked != true)
                    chkToneSquelch.IsChecked = true; // decoding a tone almost always means tone squelch
                Raise();
            });
            cboEnc.LostFocus += (s, e) => { if (rep != null && Tones.IsValid(cboEnc.Text)) { loading = true; cboEnc.Text = Tones.Normalize(cboEnc.Text); loading = false; } };
            cboDec.LostFocus += (s, e) => { if (rep != null && Tones.IsValid(cboDec.Text)) { loading = true; cboDec.Text = Tones.Normalize(cboDec.Text); loading = false; } };
            cboBw.SelectionChanged += (s, e) => { if (loading || rep == null) return; rep.Bandwidth = cboBw.SelectedIndex == 0 ? Bandwidths.Narrow : Bandwidths.Wide; Raise(); };
            chkToneSquelch.IsCheckedChanged += (s, e) => { if (loading || rep == null) return; rep.ToneSquelch = chkToneSquelch.IsChecked == true; Raise(); };
            chkRxOnly.IsCheckedChanged += (s, e) => { if (loading || rep == null) return; rep.RxOnly = chkRxOnly.IsChecked == true; Raise(); };
            chkEnabled.IsCheckedChanged += (s, e) => { if (loading || rep == null) return; rep.Enabled = chkEnabled.IsChecked == true; Raise(); };
            UiKit.OnText(txtNotes, () => { if (loading || rep == null) return; rep.Notes = txtNotes.Text; Raise(); });
            chkOffAir.IsCheckedChanged += (s, e) => { if (loading || rep == null) return; rep.OffAirSince = chkOffAir.IsChecked == true ? offAirDate : null; Raise(); };

            // Talkgroup assignment
            UiKit.OnText(txtSearch, RefreshAvailable);
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Down && lstAvailable.ItemCount > 0) { lstAvailable.Focus(); lstAvailable.SelectedIndex = 0; e.Handled = true; }
                else if (e.Key == Key.Enter) { AddSelected(lastSlot); e.Handled = true; }
            };
            lstAvailable.DoubleTapped += (s, e) => AddSelected(lastSlot);
            lstAvailable.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddSelected(lastSlot); e.Handled = true; } };
        }

        /// <summary>Once a zone is settled: the repeater takes on that zone's talkgroup set and drops channels the previous zone's set put on it.</summary>
        void ApplyZone()
        {
            if (session == null || rep == null || loading) return;
            session.Project.SyncZones();
            string sig = string.Join(",", rep.Talkgroups.Select(t => t.TalkgroupId + ":" + t.Slot));
            session.Project.ApplyZoneTalkgroups(rep);
            if (string.Join(",", rep.Talkgroups.Select(t => t.TalkgroupId + ":" + t.Slot)) == sig) return;
            RefreshGrid();
            UpdateExample();
            Raise();
        }

        void CheckBand()
        {
            foreach (var box in new[] { txtRx, txtTx })
            {
                decimal? v = UiKit.ParseMHz(box.Text);
                if (v.HasValue && !Validator.InRadioBand(v.Value)) { problem.Text = "Outside the radio's 136-174 / 400-480 MHz range"; return; }
            }
            problem.Text = "";
        }

        void CheckTone(AutoCompleteBox box)
        {
            problem.Text = Tones.IsValid(box.Text) ? "" : "Use Off, a CTCSS tone like 100.0, or a DCS code like D023N";
        }

        // ======================================================================
        // Talkgroup lists
        // ======================================================================

        void RefreshAvailable()
        {
            if (session == null) return;
            var selected = new HashSet<int>(lstAvailable.SelectedItems?.OfType<TgItem>().Select(i => i.Tg.Id) ?? Enumerable.Empty<int>());
            string q = (txtSearch.Text ?? "").Trim();
            var items = session.Project.Talkgroups
                .Where(t => q.Length == 0 || (t.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || t.Id.ToString(CultureInfo.InvariantCulture).StartsWith(q))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new TgItem(t)).ToList();
            lstAvailable.ItemsSource = items;
            foreach (var i in items.Where(i => selected.Contains(i.Tg.Id))) lstAvailable.SelectedItems?.Add(i);
        }

        void RefreshGrid(IEnumerable<RepeaterTalkgroup> select = null)
        {
            if (rep == null) return;
            bool l = loading;
            loading = true;
            try
            {
                var keep = select != null ? new HashSet<RepeaterTalkgroup>(select) : new HashSet<RepeaterTalkgroup>(SelectedEntries());
                rows = rep.Talkgroups.Select(e => new TgRow(this, e)).ToList();
                grid.ItemsSource = rows;
                grid.SelectedItems.Clear();
                foreach (var row in rows.Where(r => keep.Contains(r.Entry))) grid.SelectedItems.Add(row);
            }
            finally { loading = l; }
        }

        string DisplayName(RepeaterTalkgroup e, Talkgroup t)
        {
            if (!string.IsNullOrWhiteSpace(e.ChannelName)) return e.ChannelName;
            return t == null ? "" : rep.AutoChannelName(e, t.Name, MaxName);
        }

        /// <summary>A name typed in the grid: empty or the automatic name means "automatic".</summary>
        void NameTyped(TgRow row, string value)
        {
            if (loading || rep == null) return;
            var t = session.Project.FindTalkgroup(row.Entry.TalkgroupId);
            string typed = Naming.Clean(value, MaxName);
            string auto = t == null ? "" : rep.AutoChannelName(row.Entry, t.Name, MaxName);
            row.Entry.ChannelName = typed.Length == 0 || typed == auto ? null : typed;
            row.Refresh();
            Raise();
        }

        List<RepeaterTalkgroup> SelectedEntries()
        {
            return grid.SelectedItems.OfType<TgRow>().Select(r => r.Entry).OrderBy(e => rep.Talkgroups.IndexOf(e)).ToList();
        }

        void AddSelected(int slot)
        {
            if (rep == null) return;
            var picked = lstAvailable.SelectedItems.OfType<TgItem>().Select(i => i.Tg).ToList();
            if (picked.Count == 0 && lstAvailable.ItemCount == 1 && lstAvailable.ItemsSource is List<TgItem> only) picked.Add(only[0].Tg);
            if (picked.Count == 0) return;
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
            if (added.Count > 0) Raise();
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
                int i = list.IndexOf(e), j = i + delta;
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
