using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Everything the new-codeplug wizard collects; <see cref="Build"/> turns it into a project.</summary>
    sealed class WizardState
    {
        public List<GeoArea> Region = new List<GeoArea>();
        public RegionDownload Download;
        public string Callsign = "", RadioIdName = "", Power = "High";
        public int RadioId;
        public bool Hotspot, Noaa;
        public decimal HotspotRx, HotspotTx;
        public int HotspotCC = 1;
        public List<OnlineRepeater> Picked = new List<OnlineRepeater>();
        public ZoneScheme Scheme = ZoneScheme.County;
        public string SingleZone = "DMR";
        public Project Project;
        public List<string> Notes = new List<string>();
        string builtFrom;

        /// <summary>Raised (on the UI thread) when a download starts, progresses or finishes.</summary>
        public event EventHandler DownloadChanged;

        public void StartDownload(List<GeoArea> region)
        {
            // Same region and not failed: keep it (going Back and Next again shouldn't download twice).
            if (Download != null && Download.Areas.SequenceEqual(region) && !(Download.Done && Download.Errors.Count > 0)) return;
            StopDownload();
            Region = region;
            Download = new RegionDownload(region);
            Download.Changed += OnDownload;
            Download.Start();
            OnDownload(Download, EventArgs.Empty);
        }

        /// <summary>Stops listening to (and cancels) the current download, if any.</summary>
        public void StopDownload()
        {
            if (Download == null) return;
            Download.Changed -= OnDownload;
            Download.Cancel();
        }

        void OnDownload(object sender, EventArgs e) { DownloadChanged?.Invoke(this, EventArgs.Empty); }

        string BuildKey(List<OnlineRepeater> picked)
        {
            return string.Join(",", picked.Select(r => r.Callsign + r.RxMHz + r.ColorCode + r.DmrId)) + "|" + RadioIdName + "|" + RadioId + "|" +
                   Power + "|" + Hotspot + HotspotRx + HotspotTx + HotspotCC + "|" + Noaa;
        }

        /// <summary>True when <see cref="Build"/> with these picks would start the project over (losing zone and talkgroup edits).</summary>
        public bool WouldRebuild(List<OnlineRepeater> picked)
        {
            return Project != null && BuildKey(picked) != builtFrom;
        }

        /// <summary>True when the user has already made zone talkgroup choices a rebuild would throw away.</summary>
        public bool HasZoneWork => Project != null && Project.Zones.Any(z => z.HasTalkgroups);

        /// <summary>Makes the project from the picked repeaters (again, if anything that goes into it changed).</summary>
        public void Build()
        {
            string key = BuildKey(Picked);
            if (Project != null && key == builtFrom) return;
            builtFrom = key;

            bool usOnly = Picked.Count > 0 && Picked.All(r => r.Location?.Country?.Code == "US");
            Scheme = usOnly ? ZoneScheme.County : Picked.Count <= 12 ? ZoneScheme.Single : ZoneScheme.State;
            SingleZone = Naming.Clean(Naming.Fold(RegionDownload.Describe(Region)), 16);
            if (SingleZone.Length == 0 || Region.Count > 2) SingleZone = "DMR";

            var p = new Project { RadioId = RadioId, RadioIdName = RadioIdName };
            p.HotspotEnabled = Hotspot;
            if (Hotspot)
            {
                p.Hotspot.RxMHz = HotspotRx;
                p.Hotspot.TxMHz = HotspotTx > 0 ? HotspotTx : HotspotRx;
                p.Hotspot.ColorCode = HotspotCC;
            }
            var o = new OnlineImportOptions { Power = Power, ZoneFor = r => ZonePlanner.ZoneName(r, Scheme, SingleZone), Scheme = Scheme, MoreNames = Download?.TalkgroupNames };
            var result = OnlineImporter.AddRepeaters(p, Picked, o, Download?.BrandMeisterNames);
            if (Noaa) Presets.AddNoaaWeather(p);
            SortZones(p);
            Notes = result.Notes;
            Project = p;
        }

        /// <summary>Hotspot first, weather last, the rest by name.</summary>
        public static void SortZones(Project p)
        {
            p.SyncZones();
            var sorted = p.Zones.OrderBy(z => Project.SameZone(z.Name, p.Hotspot.Zone) ? 0 : Project.SameZone(z.Name, "Weather") ? 2 : 1)
                                .ThenBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
            p.Zones.Clear();
            p.Zones.AddRange(sorted);
        }
    }

    /// <summary>One page of the wizard.</summary>
    abstract class WizardStep : UserControl
    {
        protected readonly WizardState State;
        public abstract string Title { get; }
        public abstract string Hint { get; }

        /// <summary>Something changed that affects whether Next is allowed.</summary>
        public event EventHandler ValidChanged;

        protected WizardStep(WizardState state)
        {
            State = state;
            Font = Ui.BaseFont;
        }

        public virtual bool CanGoNext => true;
        /// <summary>Why Next is greyed out (shown beside it).</summary>
        public virtual string Blocker => "";
        public virtual void Arrive() { }
        /// <summary>Called before moving on; return false to stay.</summary>
        public virtual bool LeaveForward() { return true; }

        protected void RaiseValid() { ValidChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>
    /// File > New codeplug: region on a map (downloads start right away), your radio, the areas you want,
    /// zones, then the talkgroups in each zone. Hosted full-window by MainForm.
    /// </summary>
    sealed class NewCodeplugWizard : UserControl
    {
        readonly WizardState state = new WizardState();
        readonly List<WizardStep> steps;
        readonly Label lblStep, lblTitle, lblHint, lblDownload, lblBlocker;
        readonly ProgressBar progress;
        readonly Panel body;
        readonly Button btnBack, btnNext, btnCancel;
        int index = -1;

        public event EventHandler<Project> Finished;
        public event EventHandler Cancelled;

        public WizardState State => state;

        // For the --ui-walkthrough self-test.
        internal WizardStep Current => steps[index];
        internal bool NextEnabled => btnNext.Enabled;
        internal string BlockerText => lblBlocker.Text;
        internal void PressNext() { Next(); }

        public NewCodeplugWizard()
        {
            Font = Ui.BaseFont;
            Padding = new Padding(Ui.S(14), Ui.S(10), Ui.S(14), Ui.S(10));

            lblStep = new Label { AutoSize = true, ForeColor = Ui.HintColor, Margin = new Padding(3, 0, 3, 0) };
            lblTitle = new Label { AutoSize = true, Font = new Font(Ui.BaseFont.FontFamily, Ui.BaseFont.Size + 5f, FontStyle.Bold), Margin = new Padding(0, 2, 3, 2) };
            lblHint = Ui.Hint("", Ui.S(1100));
            var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0), Padding = new Padding(0, 0, 0, Ui.S(6)) };
            head.Controls.Add(lblStep);
            head.Controls.Add(lblTitle);
            head.Controls.Add(lblHint);
            Resize += (s, e) => lblHint.MaximumSize = new Size(Math.Max(Ui.S(300), Width - Padding.Horizontal - Ui.S(10)), 0);

            body = new Panel { Dock = DockStyle.Fill };

            lblDownload = new Label { AutoSize = true, ForeColor = Ui.HintColor, Anchor = AnchorStyles.Left, Margin = new Padding(3, Ui.S(8), 3, 3) };
            progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Width = Ui.S(120), Height = Ui.S(14), Anchor = AnchorStyles.Left, Margin = new Padding(3, Ui.S(9), 6, 3), Visible = false };
            lblBlocker = new Label { AutoSize = true, ForeColor = Color.DarkGoldenrod, Anchor = AnchorStyles.Right, Margin = new Padding(3, Ui.S(8), Ui.S(10), 3) };
            btnBack = new Button { Text = "< Back", AutoSize = true, Padding = new Padding(Ui.S(8), Ui.S(3), Ui.S(8), Ui.S(3)), UseVisualStyleBackColor = true, Anchor = AnchorStyles.Right };
            btnNext = new Button { Text = "Next >", AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(Ui.S(14), Ui.S(3), Ui.S(14), Ui.S(3)), UseVisualStyleBackColor = true, Anchor = AnchorStyles.Right };
            btnCancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(Ui.S(8), Ui.S(3), Ui.S(8), Ui.S(3)), UseVisualStyleBackColor = true, Anchor = AnchorStyles.Right, Margin = new Padding(Ui.S(14), 3, 3, 3) };
            var foot = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 6, Margin = new Padding(0), Padding = new Padding(0, Ui.S(8), 0, 0) };
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.Controls.Add(progress, 0, 0);
            foot.Controls.Add(lblDownload, 1, 0);
            foot.Controls.Add(lblBlocker, 2, 0);
            foot.Controls.Add(btnBack, 3, 0);
            foot.Controls.Add(btnNext, 4, 0);
            foot.Controls.Add(btnCancel, 5, 0);

            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(foot);
            body.BringToFront();

            steps = new List<WizardStep>
            {
                new RegionStep(state),
                new RadioStep(state),
                new AreasStep(state),
                new ZonesStep(state),
                new ZoneTalkgroupsStep(state),
            };
            foreach (var s in steps)
            {
                s.Dock = DockStyle.Fill;
                s.Visible = false;
                s.ValidChanged += (o, e) => UpdateButtons();
                body.Controls.Add(s);
            }

            btnBack.Click += (s, e) => Go(index - 1);
            btnNext.Click += (s, e) => Next();
            btnCancel.Click += (s, e) => Cancelled?.Invoke(this, EventArgs.Empty);
            state.DownloadChanged += (s, e) => ShowDownload();
            Go(0);
        }

        /// <summary>True once the user has done something worth asking about before throwing it away.</summary>
        public bool HasProgress => index > 0 || state.Region.Count > 0;

        public void CancelDownload() { state.StopDownload(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) state.StopDownload(); // no more progress reports into disposed controls
            base.Dispose(disposing);
        }

        void Next()
        {
            var step = steps[index];
            if (!step.CanGoNext || !step.LeaveForward()) return;
            if (index == steps.Count - 1)
            {
                state.Project.SyncZones(); // keep the zone order the user set on the zones page
                Finished?.Invoke(this, state.Project);
                return;
            }
            Go(index + 1);
        }

        void Go(int i)
        {
            if (i < 0 || i >= steps.Count) return;
            index = i;
            var step = steps[i];
            // Visible first, so maps and lists have their real size when the step sets itself up.
            foreach (var s in steps) s.Visible = s == step;
            lblStep.Text = "Step " + (i + 1) + " of " + steps.Count;
            lblTitle.Text = step.Title;
            lblHint.Text = step.Hint;
            PerformLayout();
            step.Arrive();
            UpdateButtons();
            step.SelectNextControl(step, true, true, true, true);
        }

        void UpdateButtons()
        {
            if (index < 0) return;
            var step = steps[index];
            btnBack.Enabled = index > 0;
            btnNext.Text = index == steps.Count - 1 ? "Finish" : "Next >";
            btnNext.Enabled = step.CanGoNext;
            lblBlocker.Text = step.CanGoNext ? "" : step.Blocker;
        }

        void ShowDownload()
        {
            var d = state.Download;
            progress.Visible = d != null && !d.Done;
            lblDownload.Text = d == null ? "" : d.Done && d.Errors.Count > 0 ? d.Status + " " + string.Join("; ", d.Errors) : d.Status;
            lblDownload.ForeColor = d != null && d.Done && d.Errors.Count > 0 ? Color.Firebrick : Ui.HintColor;
            UpdateButtons();
        }
    }

    // ==========================================================================
    // Step 1: region
    // ==========================================================================

    sealed class RegionStep : WizardStep
    {
        readonly RegionPicker picker;
        internal RegionPicker Picker => picker;
        bool loaded;

        public RegionStep(WizardState s) : base(s)
        {
            picker = new RegionPicker { Dock = DockStyle.Fill };
            picker.AllowLevels(AreaLevel.Country, AreaLevel.State);
            Controls.Add(picker);
            picker.Map.SelectionChanged += (o, e) => { ShowPicked(); RaiseValid(); };
            picker.LevelChanged += (o, e) => { picker.Map.SetSelected(new GeoArea[0]); ShowPicked(); RaiseValid(); };
        }

        public override string Title => "Where do you use your radio?";
        public override string Hint =>
            "Click the states, provinces or countries you'll be in. Their DMR repeaters download from RadioID.net in the background while you fill in the next page. " +
            "You'll pick the exact counties or areas after that. US states download on their own; anywhere else the whole country is downloaded.";

        public override bool CanGoNext => picker.Map.Selected.Count > 0;
        public override string Blocker => "Click at least one place on the map.";

        public override async void Arrive()
        {
            if (loaded) return;
            loaded = true;
            await picker.LoadAtlasAsync();
            if (IsDisposed) return;
            var atlas = picker.Map.Atlas;
            var saved = (AppSettings.Get("Region") ?? "").Split(',').Select(atlas.Find).Where(a => a != null).ToList();
            if (saved.Count > 0)
            {
                picker.Level = saved.Any(a => a.Level == AreaLevel.Country) ? AreaLevel.Country : AreaLevel.State;
                picker.Map.SetSelected(saved);
                picker.Map.ZoomToAreas(saved);
            }
            else
            {
                picker.Level = AreaLevel.State;
                picker.Map.ZoomTo(-128, 22, -64, 52); // most users so far are in North America; "Whole world" is one click away
            }
            ShowPicked();
            RaiseValid();
        }

        void ShowPicked()
        {
            var sel = picker.Map.Selected.ToList();
            picker.Status = sel.Count == 0 ? "Click a state, province or country to pick it; click again to drop it."
                : "Picked: " + string.Join(", ", sel.Select(a => a.Name)) + ". Next downloads " + string.Join(", ", RegionDownload.Queries(sel).Select(q => q.Value.Name)) + ".";
        }

        public override bool LeaveForward()
        {
            var region = picker.Map.Selected.OrderBy(a => a.Name).ToList();
            AppSettings.Set("Region", string.Join(",", region.Select(a => a.Code)));
            State.StartDownload(region);
            return true;
        }
    }

    // ==========================================================================
    // Step 2: your radio
    // ==========================================================================

    sealed class RadioStep : WizardStep
    {
        readonly TextBox txtCall, txtId, txtName, txtHsRx, txtHsTx;
        readonly Label lblLookup;
        readonly ComboBox cboPower;
        readonly CheckBox chkHotspot, chkNoaa;
        readonly NumericUpDown numCC;
        readonly Button btnLookup;
        readonly ErrorProvider errors = new ErrorProvider { BlinkStyle = ErrorBlinkStyle.NeverBlink };
        readonly Control hotspotFields;
        internal TextBox CallBox => txtCall;
        internal void PressLookup() { LookUp(); }
        bool loading;

        public RadioStep(WizardState s) : base(s)
        {
            AutoScroll = true;
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, Ui.S(4), 0, 0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int wrap = Ui.S(760);

            txtCall = new TextBox { Width = Ui.S(140), CharacterCasing = CharacterCasing.Upper, MaxLength = 10, Anchor = AnchorStyles.Left, Margin = new Padding(3, Ui.S(4), 6, 3) };
            btnLookup = Ui.Button("Look up on RadioID.net", (o, e) => LookUp());
            var callRow = Ui.Row(txtCall, btnLookup);
            callRow.Dock = DockStyle.None; callRow.WrapContents = false; callRow.Anchor = AnchorStyles.Left;
            lblLookup = Ui.Hint("Your callsign finds your DMR ID and fills in the two boxes below.", wrap);
            txtId = new TextBox { Width = Ui.S(140), MaxLength = 8, Anchor = AnchorStyles.Left };
            txtName = new TextBox { Width = Ui.S(220), MaxLength = 16, Anchor = AnchorStyles.Left };
            cboPower = Ui.Combo(false, Powers.Values);
            cboPower.Width = Ui.S(100);
            cboPower.Anchor = AnchorStyles.Left;

            Section(t, "Who you are");
            Pair(t, "Callsign", callRow);
            Span(t, lblLookup);
            Pair(t, "DMR ID", txtId);
            Pair(t, "Radio ID name", txtName);
            Span(t, Ui.Hint("Up to 16 characters, e.g. \"Austin W6OZZ\". Every channel uses it, and it goes into the CPS's Radio ID List.", wrap));
            Section(t, "Channels");
            Pair(t, "Transmit power", cboPower);
            chkNoaa = new CheckBox { Text = "Add the 7 NOAA weather channels (receive only, zone \"Weather\")", AutoSize = true, Margin = new Padding(3, Ui.S(6), 3, 3) };
            Span(t, chkNoaa);

            Section(t, "Hotspot (optional)");
            chkHotspot = new CheckBox { Text = "I have an MMDVM hotspot or my own repeater", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
            Span(t, chkHotspot);
            txtHsRx = new TextBox { Width = Ui.S(110), Anchor = AnchorStyles.Left };
            txtHsTx = new TextBox { Width = Ui.S(110), Anchor = AnchorStyles.Left };
            Ui.SetCue(txtHsTx, "same (simplex)");
            numCC = new NumericUpDown { Minimum = 0, Maximum = 15, Value = 1, Width = Ui.S(60), Anchor = AnchorStyles.Left };
            var hs = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(20), 0, 0, 0) };
            for (int i = 0; i < 6; i++) hs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            hs.Controls.Add(Ui.Label("Receive MHz"), 0, 0); hs.Controls.Add(txtHsRx, 1, 0);
            hs.Controls.Add(Ui.Label("Transmit MHz"), 2, 0); hs.Controls.Add(txtHsTx, 3, 0);
            hs.Controls.Add(Ui.Label("Color code"), 4, 0); hs.Controls.Add(numCC, 5, 0);
            hotspotFields = hs;
            Span(t, hs);
            Span(t, Ui.Hint("You pick the hotspot's talkgroups with the zones, two pages on. A simplex hotspot (Pi-Star, WPSD) usually carries everything on slot 2.", wrap));
            Controls.Add(t);

            txtCall.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LookUp(); } };
            txtCall.TextChanged += (o, e) => { if (!loading) State.Callsign = txtCall.Text.Trim(); };
            txtId.TextChanged += (o, e) => { if (!loading) Store(); };
            txtName.TextChanged += (o, e) => { if (!loading) Store(); };
            cboPower.SelectedIndexChanged += (o, e) => { if (!loading) State.Power = (string)cboPower.SelectedItem ?? "High"; };
            chkNoaa.CheckedChanged += (o, e) => { if (!loading) State.Noaa = chkNoaa.Checked; };
            chkHotspot.CheckedChanged += (o, e) => { hotspotFields.Enabled = chkHotspot.Checked; if (!loading) Store(); };
            txtHsRx.TextChanged += (o, e) => { if (!loading) Store(); };
            txtHsTx.TextChanged += (o, e) => { if (!loading) Store(); };
            numCC.ValueChanged += (o, e) => { if (!loading) Store(); };
        }

        static void Span(TableLayoutPanel t, Control c) { t.Controls.Add(c); t.SetColumnSpan(c, 2); }
        static void Pair(TableLayoutPanel t, string label, Control c) { t.Controls.Add(Ui.Label(label)); t.Controls.Add(c); }
        static void Section(TableLayoutPanel t, string title)
        {
            var l = Ui.Label(title, true);
            l.Margin = new Padding(3, Ui.S(14), 3, Ui.S(4));
            Span(t, l);
        }

        public override string Title => "Your radio";
        public override string Hint => "Who you are on the air. The repeaters for the places you picked are downloading meanwhile (see the bottom of the window).";

        public override bool CanGoNext => Naming.Clean(txtName.Text, 16).Length > 0 && !HotspotProblem();
        public override string Blocker => Naming.Clean(txtName.Text, 16).Length == 0 ? "Fill in a Radio ID name." : "Type the hotspot's receive frequency.";

        bool HotspotProblem()
        {
            return chkHotspot.Checked && (Ui.ParseMHz(txtHsRx.Text) == null || (txtHsTx.Text.Trim().Length > 0 && Ui.ParseMHz(txtHsTx.Text) == null));
        }

        public override void Arrive()
        {
            loading = true;
            if (State.Callsign.Length == 0) State.Callsign = AppSettings.Get("Callsign") ?? "";
            txtCall.Text = State.Callsign;
            txtId.Text = State.RadioId > 0 ? State.RadioId.ToString(CultureInfo.InvariantCulture) : "";
            txtName.Text = State.RadioIdName;
            cboPower.SelectedItem = State.Power;
            bool us = State.Region.Any(a => a.CountryCode == "US");
            chkNoaa.Visible = us;
            chkNoaa.Checked = us && State.Noaa;
            chkHotspot.Checked = State.Hotspot;
            hotspotFields.Enabled = State.Hotspot;
            txtHsRx.Text = State.HotspotRx > 0 ? Ui.FormatMHz(State.HotspotRx) : "";
            txtHsTx.Text = State.HotspotTx > 0 && State.HotspotTx != State.HotspotRx ? Ui.FormatMHz(State.HotspotTx) : "";
            numCC.Value = Math.Max(0, Math.Min(15, State.HotspotCC));
            loading = false;
            CheckFields();
        }

        void Store()
        {
            State.RadioId = int.TryParse(txtId.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0 ? id : 0;
            State.RadioIdName = Naming.Clean(txtName.Text, 16);
            State.Hotspot = chkHotspot.Checked;
            State.HotspotRx = Ui.ParseMHz(txtHsRx.Text) ?? 0;
            State.HotspotTx = Ui.ParseMHz(txtHsTx.Text) ?? 0;
            State.HotspotCC = (int)numCC.Value;
            CheckFields();
            RaiseValid();
        }

        void CheckFields()
        {
            string id = txtId.Text.Trim();
            errors.SetError(txtId, id.Length > 0 && !int.TryParse(id, out _) ? "Numbers only" : "");
            errors.SetError(txtHsRx, chkHotspot.Checked && txtHsRx.Text.Trim().Length > 0 && Ui.ParseMHz(txtHsRx.Text) == null ? "Type the frequency in MHz, e.g. 433.550" : "");
        }

        async void LookUp()
        {
            string call = txtCall.Text.Trim().ToUpperInvariant();
            if (call.Length < 3) { lblLookup.Text = "Type your callsign first."; return; }
            btnLookup.Enabled = false;
            lblLookup.ForeColor = Ui.HintColor;
            lblLookup.Text = "Looking up " + call + "...";
            try
            {
                var users = await Online.LookupUserAsync(call);
                if (IsDisposed) return;
                if (users.Count == 0)
                {
                    lblLookup.ForeColor = Color.Firebrick;
                    lblLookup.Text = "RadioID.net has no DMR ID for " + call + ". If you don't have one yet, register at radioid.net (it's free), or type it below.";
                    return;
                }
                var u = users[0];
                AppSettings.Set("Callsign", call);
                loading = true;
                txtId.Text = u.Id.ToString(CultureInfo.InvariantCulture);
                txtName.Text = u.SuggestedName(16);
                loading = false;
                Store();
                lblLookup.ForeColor = Color.ForestGreen;
                lblLookup.Text = "Found " + u.Callsign + ": " + (u.FirstName + " " + u.LastName).Trim() + ", " + u.City + ", " + u.State +
                                 (users.Count > 1 ? ". You have " + users.Count + " IDs (" + string.Join(", ", users.Select(x => x.Id)) + "); using the first." : ".");
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                lblLookup.ForeColor = Color.Firebrick;
                lblLookup.Text = "Couldn't look it up: " + ex.Message;
            }
            finally
            {
                if (!IsDisposed) btnLookup.Enabled = true;
            }
        }
    }

    // ==========================================================================
    // Step 3: areas
    // ==========================================================================

    sealed class AreasStep : WizardStep
    {
        readonly AreaChooser chooser;
        RegionDownload bound;
        internal AreaChooser Chooser => chooser;

        public AreasStep(WizardState s) : base(s)
        {
            chooser = new AreaChooser { Dock = DockStyle.Fill };
            Controls.Add(chooser);
            chooser.PickedChanged += (o, e) => RaiseValid();
            s.DownloadChanged += (o, e) =>
            {
                if (!Visible || State.Download != bound) return;
                chooser.Reload();
                RaiseValid();
            };
        }

        public override string Title => "Which repeaters?";
        public override string Hint =>
            "The first map only chose what to download. Here you choose what goes in the codeplug: click counties, states or countries to take every " +
            "repeater in them (switch the level above the map). Dots are repeaters; red ones are picked. " +
            "The List tab lets you tick or untick single repeaters, including ones the map couldn't place.";

        public override bool CanGoNext => State.Download != null && State.Download.Done && (chooser.Picked().Count > 0 || State.Hotspot);
        public override string Blocker =>
            State.Download == null || !State.Download.Done ? "Waiting for the download..." :
            State.Download.Errors.Count > 0 && State.Download.Repeaters.Count == 0 ? "The download failed: click Back, then Next to try again." :
            "Pick at least one repeater.";

        public override void Arrive()
        {
            if (bound != State.Download)
            {
                bound = State.Download;
                chooser.Bind(bound, null);
            }
            else chooser.Reload();
        }

        public override bool LeaveForward()
        {
            var picked = chooser.Picked();
            if (picked.Count == 0 &&
                !Ui.Confirm(FindForm(), "No repeaters are picked yet, so the codeplug would only have your hotspot" + (State.Noaa ? " and the weather channels" : "") + ".\n\n" +
                                        "To pick repeaters, click states or counties on this map (each click takes every repeater in it), or tick single ones on the List tab.\n\n" +
                                        "Go on without repeaters?"))
                return false;
            if (picked.Count > 300 &&
                !Ui.Confirm(FindForm(), "That's " + picked.Count + " repeaters. With a few talkgroups each, that can pass the radio's 4000 channels.\n\nGo on anyway? (You can trim zones and talkgroups next.)"))
                return false;
            if (State.HasZoneWork && State.WouldRebuild(picked) &&
                !Ui.Confirm(FindForm(), "The repeaters (or your radio details) changed, so the zones and the talkgroups you picked for them start over.\n\nGo on?"))
                return false;
            State.Picked = picked;
            State.Build();
            return true;
        }
    }

    // ==========================================================================
    // Step 4: zones
    // ==========================================================================

    sealed class ZonesStep : WizardStep
    {
        readonly List<RadioButton> options = new List<RadioButton>();
        readonly TextBox txtSingle;
        readonly ListView list;
        bool loading;

        public ZonesStep(WizardState s) : base(s)
        {
            var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = Ui.S(330), ColumnCount = 1, Padding = new Padding(0, 0, Ui.S(12), 0) };
            left.Controls.Add(Ui.Label("Make zones", true));
            foreach (var c in ZonePlanner.Choices)
            {
                var rb = new RadioButton { Text = c.Value, AutoSize = true, Tag = c.Key, Margin = new Padding(3, Ui.S(4), 3, 0) };
                rb.CheckedChanged += (o, e) => { if (rb.Checked && !loading) Rezone(); };
                options.Add(rb);
                left.Controls.Add(rb);
            }
            txtSingle = new TextBox { Width = Ui.S(200), MaxLength = 16, Margin = new Padding(Ui.S(22), 2, 3, 3) };
            txtSingle.TextChanged += (o, e) => { if (!loading && Scheme == ZoneScheme.Single) Rezone(); };
            // Typing a name means "everything in one zone".
            txtSingle.Enter += (o, e) => { var single = options.First(x => (ZoneScheme)x.Tag == ZoneScheme.Single); if (!single.Checked) single.Checked = true; };
            left.Controls.Add(txtSingle);
            left.Controls.Add(Ui.Hint("Repeaters the map couldn't place that precisely go into their state's or country's zone. " +
                                      "The radio holds 250 zones of up to 250 channels; bigger zones are split.", Ui.S(300)));

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Zone", Ui.S(200));
            list.Columns.Add("Repeaters", Ui.S(80), HorizontalAlignment.Right);
            list.Columns.Add("Channels so far", Ui.S(110), HorizontalAlignment.Right);
            list.Columns.Add("Includes", Ui.S(380));
            var buttons = Ui.Row(
                Ui.Button("Rename / merge...", (o, e) => Rename()),
                Ui.Button("Move up", (o, e) => MoveZone(-1)),
                Ui.Button("Move down", (o, e) => MoveZone(1)));
            buttons.Dock = DockStyle.Bottom;
            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(list);
            right.Controls.Add(buttons);
            list.BringToFront();

            Controls.Add(right);
            Controls.Add(left);
            list.DoubleClick += (o, e) => Rename();
        }

        public override string Title => "Zones";
        public override string Hint =>
            "A zone is a group of channels you switch between on the radio. Pick how to group the repeaters; you can rename, merge and reorder zones here " +
            "or later on the Zones tab. Talkgroups come next.";

        ZoneScheme Scheme => (ZoneScheme)(options.FirstOrDefault(o => o.Checked)?.Tag ?? ZoneScheme.County);

        public override void Arrive()
        {
            loading = true;
            foreach (var o in options) o.Checked = (ZoneScheme)o.Tag == State.Scheme;
            options.First(o => (ZoneScheme)o.Tag == ZoneScheme.County).Enabled = State.Picked.Any(r => r.Location?.Country?.Code == "US");
            txtSingle.Text = State.SingleZone;
            loading = false;
            Fill(null);
        }

        void Rezone()
        {
            var p = State.Project;
            string single = Naming.Clean(txtSingle.Text, 16).Length > 0 ? Naming.Clean(txtSingle.Text, 16) : "DMR";
            // Typing a new name for the one zone renames it (keeping its talkgroups), unless that name is taken.
            if (Scheme == ZoneScheme.Single && State.Scheme == ZoneScheme.Single)
            {
                if (Project.SameZone(single, State.SingleZone)) return;
                if (p.FindZone(State.SingleZone) != null && p.FindZone(single) == null)
                {
                    p.RenameZone(State.SingleZone, single);
                    State.SingleZone = single;
                    Fill(single);
                    return;
                }
            }
            // Zone talkgroup sets belong to zone names; regrouping makes new zones without them.
            if (State.HasZoneWork && Scheme != State.Scheme &&
                !Ui.Confirm(FindForm(), "Regrouping makes new zones, so the talkgroups you ticked for each zone are cleared.\n\nRegroup anyway?"))
            {
                loading = true;
                foreach (var o in options) o.Checked = (ZoneScheme)o.Tag == State.Scheme;
                loading = false;
                return;
            }
            State.Scheme = Scheme;
            State.SingleZone = single;
            ZonePlanner.Apply(p, p.Repeaters.Where(r => r.IsDigital), State.Scheme, State.SingleZone);
            WizardState.SortZones(p);
            Fill(null);
        }

        void Fill(string select)
        {
            var p = State.Project;
            if (p == null) return;
            p.SyncZones();
            var used = p.UsedZoneNames();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var z in p.Zones.Where(z => used.Contains(z.Name)))
            {
                var reps = p.ActiveRepeaters().Where(r => Project.SameZone(r.Zone, z.Name)).ToList();
                var item = new ListViewItem(z.Name) { Tag = z };
                item.SubItems.Add(reps.Count.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(p.ZoneChannelCount(z.Name).ToString(CultureInfo.InvariantCulture));
                var places = reps.Select(r => r == p.Hotspot ? "your hotspot" : !r.IsDigital ? r.Name : r.City).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                item.SubItems.Add(string.Join(", ", places.Take(8)) + (places.Count > 8 ? " and " + (places.Count - 8) + " more" : ""));
                list.Items.Add(item);
                if (select != null && Project.SameZone(z.Name, select)) item.Selected = true;
            }
            list.EndUpdate();
        }

        ZoneInfo Selected => list.SelectedItems.Count > 0 ? (ZoneInfo)list.SelectedItems[0].Tag : null;

        void Rename()
        {
            var z = Selected;
            if (z == null) return;
            string name = Prompt.Show(FindForm(), "Rename zone", "New name for \"" + z.Name + "\" (16 characters max; an existing zone's name merges them):", z.Name, 16);
            if (name == null) return;
            name = Naming.Fit(name, 16);
            if (name.Length == 0 || name == z.Name) return;
            State.Project.RenameZone(z.Name, name);
            Fill(name);
        }

        void MoveZone(int delta)
        {
            var z = Selected;
            if (z == null) return;
            var zones = State.Project.Zones;
            int i = zones.IndexOf(z), j = i + delta;
            if (j < 0 || j >= zones.Count) return;
            zones.RemoveAt(i);
            zones.Insert(j, z);
            Fill(z.Name);
        }
    }

    // ==========================================================================
    // Step 5: talkgroups per zone
    // ==========================================================================

    sealed class ZoneTalkgroupsStep : WizardStep
    {
        readonly ListView zones;
        readonly ZoneTalkgroupsEditor editor;
        bool loading;
        internal ZoneTalkgroupsEditor Editor => editor;

        public ZoneTalkgroupsStep(WizardState s) : base(s)
        {
            zones = new ListView { Dock = DockStyle.Left, Width = Ui.S(300), View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            Ui.DoubleBuffer(zones);
            zones.Columns.Add("Zone", Ui.S(150));
            zones.Columns.Add("Repeaters", Ui.S(70), HorizontalAlignment.Right);
            zones.Columns.Add("Channels", Ui.S(66), HorizontalAlignment.Right);
            editor = new ZoneTalkgroupsEditor { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(10), 0, 0, 0) };
            Controls.Add(editor);
            Controls.Add(zones);
            zones.SelectedIndexChanged += (o, e) => { if (!loading && zones.SelectedItems.Count > 0) editor.Bind(State.Project, ((ZoneInfo)zones.SelectedItems[0].Tag).Name); };
            editor.Changed += (o, e) => { RefreshCounts(); RaiseValid(); };
        }

        public override string Title => "Talkgroups in each zone";
        public override string Hint =>
            "Each talkgroup on a repeater becomes a channel. Repeaters already bring the talkgroups they list on RadioID.net; tick more for a whole zone at once. " +
            "Quick start: pick a zone, click Starter set, then Copy ticked to all zones.";

        public override bool CanGoNext => State.Project != null && State.Project.ChannelCount() > 0;
        public override string Blocker => "No channels yet: add talkgroups to a zone.";

        public override void Arrive()
        {
            editor.SetBrandMeister(State.Download?.BrandMeisterNames);
            loading = true;
            var p = State.Project;
            var used = p.UsedZoneNames();
            zones.BeginUpdate();
            zones.Items.Clear();
            foreach (var z in p.Zones.Where(z => used.Contains(z.Name) && p.ZoneRepeaters(z.Name).Count > 0))
            {
                var item = new ListViewItem(z.Name) { Tag = z };
                item.SubItems.Add("");
                item.SubItems.Add("");
                zones.Items.Add(item);
            }
            zones.EndUpdate();
            loading = false;
            RefreshCounts();
            if (zones.Items.Count > 0) zones.Items[0].Selected = true;
            else editor.Bind(p, null);
        }

        void RefreshCounts()
        {
            var p = State.Project;
            foreach (ListViewItem item in zones.Items)
            {
                var z = (ZoneInfo)item.Tag;
                int chans = p.ZoneChannelCount(z.Name);
                item.SubItems[1].Text = p.ZoneRepeaters(z.Name).Count.ToString(CultureInfo.InvariantCulture);
                item.SubItems[2].Text = chans.ToString(CultureInfo.InvariantCulture);
                item.ForeColor = chans == 0 ? Color.DarkGoldenrod : SystemColors.WindowText;
            }
        }
    }
}
