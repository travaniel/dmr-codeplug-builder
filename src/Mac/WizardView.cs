using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>One page of the wizard.</summary>
    abstract class WizStep : UserControl
    {
        protected readonly WizardState State;
        protected readonly Window Owner;
        public abstract string Title { get; }
        public abstract string Hint { get; }

        /// <summary>Something changed that affects whether Next is allowed.</summary>
        public event EventHandler ValidChanged;

        protected WizStep(WizardState state, Window owner) { State = state; Owner = owner; }

        public virtual bool CanGoNext => true;
        /// <summary>Why Next is greyed out (shown beside it).</summary>
        public virtual string Blocker => "";
        public virtual void Arrive() { }
        /// <summary>Called before moving on; return false to stay.</summary>
        public virtual Task<bool> LeaveForward() { return Task.FromResult(true); }

        protected void RaiseValid() { ValidChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>
    /// File > New codeplug: region on a map (downloads start right away), your radio, the areas you want, zones, then the
    /// talkgroups in each zone. Hosted full-window by MainWindow. The same five steps and the same <see cref="WizardState"/>
    /// as the Windows NewCodeplugWizard.
    /// </summary>
    sealed class NewCodeplugWizardView : UserControl
    {
        readonly WizardState state = new WizardState();
        readonly List<WizStep> steps;
        readonly TextBlock lblStep, lblTitle, lblHint, lblDownload, lblBlocker;
        readonly ProgressBar progress;
        readonly Border body;
        readonly Button btnBack, btnNext, btnCancel;
        int index = -1;
        bool busy;

        public event EventHandler<Project> Finished;
        public event EventHandler Cancelled;

        public WizardState Data => state;

        internal WizStep Current => steps[index];
        internal bool NextEnabled => btnNext.IsEnabled;
        internal string BlockerText => lblBlocker.Text;
        internal async Task PressNext() { await Next(); }

        public NewCodeplugWizardView(Window owner)
        {
            lblStep = new TextBlock { Opacity = 0.7 };
            lblTitle = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 2, 0, 2) };
            lblHint = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Children = { lblStep, lblTitle, lblHint } };

            body = new Border();

            lblDownload = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            progress = new ProgressBar { IsIndeterminate = true, Width = 120, Height = 14, IsVisible = false, Margin = new Thickness(0, 0, 8, 0) };
            lblBlocker = new TextBlock { Foreground = Brushes.DarkGoldenrod, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            btnBack = new Button { Content = "< Back" };
            btnNext = new Button { Content = "Next >", FontWeight = FontWeight.SemiBold, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            btnCancel = new Button { Content = "Cancel", Margin = new Thickness(14, 0, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { lblBlocker, btnBack, btnNext, btnCancel } };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { progress, lblDownload } };
            var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            foot.Children.Add(right);
            foot.Children.Add(left);

            var dock = new DockPanel { Margin = new Thickness(16, 12) };
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(head); dock.Children.Add(foot); dock.Children.Add(body);
            Content = dock;

            steps = new List<WizStep>
            {
                new RegionStepView(state, owner),
                new RadioStepView(state, owner),
                new AreasStepView(state, owner),
                new ZonesStepView(state, owner),
                new ZoneTalkgroupsStepView(state, owner),
            };
            foreach (var s in steps) s.ValidChanged += (o, e) => UpdateButtons();

            btnBack.Click += (s, e) => Go(index - 1);
            btnNext.Click += async (s, e) => await Next();
            btnCancel.Click += (s, e) => Cancelled?.Invoke(this, EventArgs.Empty);
            state.DownloadChanged += (s, e) => ShowDownload();
            Go(0);
        }

        /// <summary>True once the user has done something worth asking about before throwing it away.</summary>
        public bool HasProgress => index > 0 || state.Region.Count > 0;

        public void CancelDownload() { state.StopDownload(); }

        async Task Next()
        {
            if (busy) return;
            var step = steps[index];
            if (!step.CanGoNext) return;
            busy = true;
            try
            {
                if (!await step.LeaveForward()) return;
                if (index == steps.Count - 1)
                {
                    state.Project.SyncZones(); // keep the zone order the user set on the zones page
                    Finished?.Invoke(this, state.Project);
                    return;
                }
                Go(index + 1);
            }
            finally { busy = false; }
        }

        void Go(int i)
        {
            if (i < 0 || i >= steps.Count) return;
            index = i;
            var step = steps[i];
            body.Child = step; // attached first, so maps and lists have their real size when the step sets itself up
            lblStep.Text = "Step " + (i + 1) + " of " + steps.Count;
            lblTitle.Text = step.Title;
            lblHint.Text = step.Hint;
            step.Arrive();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            if (index < 0) return;
            var step = steps[index];
            btnBack.IsEnabled = index > 0;
            btnNext.Content = index == steps.Count - 1 ? "Finish" : "Next >";
            btnNext.IsEnabled = step.CanGoNext;
            lblBlocker.Text = step.CanGoNext ? "" : step.Blocker;
        }

        void ShowDownload()
        {
            var d = state.Download;
            progress.IsVisible = d != null && !d.Done;
            lblDownload.Text = d == null ? "" : d.Done && d.Errors.Count > 0 ? d.Status + " " + string.Join("; ", d.Errors) : d.Status;
            lblDownload.Foreground = d != null && d.Done && d.Errors.Count > 0 ? Brushes.Firebrick : null;
            UpdateButtons();
        }
    }

    // ==========================================================================
    // Step 1: region
    // ==========================================================================

    sealed class RegionStepView : WizStep
    {
        readonly RegionPickerView picker = new RegionPickerView();
        bool loaded;
        internal RegionPickerView Picker => picker;

        public RegionStepView(WizardState s, Window owner) : base(s, owner)
        {
            picker.AllowLevels(AreaLevel.Country, AreaLevel.State);
            Content = picker;
            picker.Map.SelectionChanged += (o, e) => { ShowPicked(); RaiseValid(); };
            picker.LevelChanged += (o, e) => { picker.Map.SetSelected(new GeoArea[0]); ShowPicked(); RaiseValid(); };
        }

        public override string Title => "Where do you use your radio?";
        public override string Hint =>
            "Click the states, provinces or countries you'll be in. Their DMR repeaters download from RadioID.net and BrandMeister in the background while you fill in the next page. " +
            "You'll pick the exact counties or areas after that. US states download on their own; anywhere else the whole country is downloaded.";

        public override bool CanGoNext => picker.Map.Selected.Count > 0;
        public override string Blocker => "Click at least one place on the map.";

        public override async void Arrive()
        {
            if (loaded) return;
            loaded = true;
            await picker.LoadAtlasAsync();
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

        public override Task<bool> LeaveForward()
        {
            var region = picker.Map.Selected.OrderBy(a => a.Name).ToList();
            AppSettings.Set("Region", string.Join(",", region.Select(a => a.Code)));
            State.StartDownload(region);
            return Task.FromResult(true);
        }
    }

    // ==========================================================================
    // Step 2: your radio
    // ==========================================================================

    sealed class RadioStepView : WizStep
    {
        readonly TextBox txtCall, txtId, txtName, txtHsRx, txtHsTx;
        readonly TextBlock lblLookup, lblProblem;
        readonly ComboBox cboPower;
        readonly CheckBox chkHotspot, chkNoaa;
        readonly NumericUpDown numCC;
        readonly Button btnLookup;
        readonly Control hotspotFields;
        bool loading;
        internal TextBox CallBox => txtCall;
        internal async Task PressLookup() { await LookUp(); }

        public RadioStepView(WizardState s, Window owner) : base(s, owner)
        {
            txtCall = new TextBox { Width = 140, MaxLength = 10 };
            btnLookup = UiKit.Button("Look up on RadioID.net", async () => await LookUp());
            lblLookup = UiKit.Hint("Your callsign finds your DMR ID and fills in the two boxes below.");
            txtId = new TextBox { Width = 140, MaxLength = 8 };
            txtName = new TextBox { Width = 240, MaxLength = 16 };
            cboPower = new ComboBox { ItemsSource = Powers.Values, Width = 110 };
            chkNoaa = new CheckBox { Content = "Add the 7 NOAA weather channels (receive only, zone \"Weather\")" };
            chkHotspot = new CheckBox { Content = "I have an MMDVM hotspot or my own repeater" };
            txtHsRx = new TextBox { Width = 120 };
            txtHsTx = new TextBox { Width = 120, Watermark = "same (simplex)" };
            numCC = new NumericUpDown { Minimum = 0, Maximum = 15, Value = 1, Increment = 1, FormatString = "0", Width = 110 };
            lblProblem = new TextBlock { Foreground = Brushes.Firebrick };

            var hs = UiKit.Row(UiKit.Label("Receive MHz"), txtHsRx, UiKit.Label("Transmit MHz"), txtHsTx, UiKit.Label("Color code"), numCC);
            hs.Margin = new Thickness(22, 0, 0, 0);
            hotspotFields = hs;

            var p = new StackPanel { Spacing = 8, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            Control Section(string t) { var l = UiKit.Label(t, true); l.Margin = new Thickness(0, 12, 0, 2); return l; }
            Control Pair(string label, Control c) { var l = UiKit.Label(label); l.Width = 110; return UiKit.Row(l, c); }
            p.Children.Add(Section("Who you are"));
            p.Children.Add(Pair("Callsign", UiKit.Row(txtCall, btnLookup)));
            p.Children.Add(lblLookup);
            p.Children.Add(Pair("DMR ID", txtId));
            p.Children.Add(Pair("Radio ID name", txtName));
            p.Children.Add(UiKit.Hint("Up to 16 characters, e.g. \"Austin W6OZZ\". Every channel uses it, and it goes into the CPS's Radio ID List."));
            p.Children.Add(Section("Channels"));
            p.Children.Add(Pair("Transmit power", cboPower));
            p.Children.Add(chkNoaa);
            p.Children.Add(Section("Hotspot (optional)"));
            p.Children.Add(chkHotspot);
            p.Children.Add(hotspotFields);
            p.Children.Add(UiKit.Hint("You pick the hotspot's talkgroups with the zones, two pages on. A simplex hotspot (Pi-Star, WPSD) usually carries everything on slot 2."));
            p.Children.Add(lblProblem);
            Content = new ScrollViewer { Content = p };

            txtCall.KeyDown += async (o, e) => { if (e.Key == Key.Enter) { e.Handled = true; await LookUp(); } };
            UiKit.OnText(txtCall, () => { if (loading) return; var up = (txtCall.Text ?? "").ToUpperInvariant(); if (up != txtCall.Text) txtCall.Text = up; State.Callsign = up.Trim(); });
            UiKit.OnText(txtId, () => { if (!loading) Store(); });
            UiKit.OnText(txtName, () => { if (!loading) Store(); });
            cboPower.SelectionChanged += (o, e) => { if (!loading) State.Power = cboPower.SelectedItem as string ?? "High"; };
            chkNoaa.IsCheckedChanged += (o, e) => { if (!loading) State.Noaa = chkNoaa.IsChecked == true; };
            chkHotspot.IsCheckedChanged += (o, e) => { hotspotFields.IsEnabled = chkHotspot.IsChecked == true; if (!loading) Store(); };
            UiKit.OnText(txtHsRx, () => { if (!loading) Store(); });
            UiKit.OnText(txtHsTx, () => { if (!loading) Store(); });
            numCC.ValueChanged += (o, e) => { if (!loading) Store(); };
        }

        public override string Title => "Your radio";
        public override string Hint => "Who you are on the air. The repeaters for the places you picked are downloading meanwhile (see the bottom of the window).";

        public override bool CanGoNext => Naming.Clean(txtName.Text ?? "", 16).Length > 0 && !HotspotProblem();
        public override string Blocker => Naming.Clean(txtName.Text ?? "", 16).Length == 0 ? "Fill in a Radio ID name." : "Type the hotspot's receive frequency.";

        bool HotspotProblem()
        {
            return chkHotspot.IsChecked == true && (UiKit.ParseMHz(txtHsRx.Text) == null || ((txtHsTx.Text ?? "").Trim().Length > 0 && UiKit.ParseMHz(txtHsTx.Text) == null));
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
            chkNoaa.IsVisible = us;
            chkNoaa.IsChecked = us && State.Noaa;
            chkHotspot.IsChecked = State.Hotspot;
            hotspotFields.IsEnabled = State.Hotspot;
            txtHsRx.Text = State.HotspotRx > 0 ? UiKit.FormatMHz(State.HotspotRx) : "";
            txtHsTx.Text = State.HotspotTx > 0 && State.HotspotTx != State.HotspotRx ? UiKit.FormatMHz(State.HotspotTx) : "";
            numCC.Value = Math.Max(0, Math.Min(15, State.HotspotCC));
            loading = false;
            CheckFields();
        }

        void Store()
        {
            State.RadioId = int.TryParse((txtId.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0 ? id : 0;
            State.RadioIdName = Naming.Clean(txtName.Text ?? "", 16);
            State.Hotspot = chkHotspot.IsChecked == true;
            State.HotspotRx = UiKit.ParseMHz(txtHsRx.Text) ?? 0;
            State.HotspotTx = UiKit.ParseMHz(txtHsTx.Text) ?? 0;
            State.HotspotCC = (int)(numCC.Value ?? 1);
            CheckFields();
            RaiseValid();
        }

        void CheckFields()
        {
            string id = (txtId.Text ?? "").Trim();
            lblProblem.Text = id.Length > 0 && !int.TryParse(id, out _) ? "The DMR ID is numbers only."
                : chkHotspot.IsChecked == true && (txtHsRx.Text ?? "").Trim().Length > 0 && UiKit.ParseMHz(txtHsRx.Text) == null ? "Type the hotspot frequency in MHz, e.g. 433.550"
                : "";
        }

        async Task LookUp()
        {
            string call = (txtCall.Text ?? "").Trim().ToUpperInvariant();
            if (call.Length < 3) { lblLookup.Text = "Type your callsign first."; return; }
            btnLookup.IsEnabled = false;
            lblLookup.Foreground = null;
            lblLookup.Text = "Looking up " + call + "...";
            try
            {
                var users = await Online.LookupUserAsync(call);
                if (users.Count == 0)
                {
                    lblLookup.Foreground = Brushes.Firebrick;
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
                lblLookup.Foreground = Brushes.ForestGreen;
                lblLookup.Text = "Found " + u.Callsign + ": " + (u.FirstName + " " + u.LastName).Trim() + ", " + u.City + ", " + u.State +
                                 (users.Count > 1 ? ". You have " + users.Count + " IDs (" + string.Join(", ", users.Select(x => x.Id)) + "); using the first." : ".");
            }
            catch (Exception ex)
            {
                lblLookup.Foreground = Brushes.Firebrick;
                lblLookup.Text = "Couldn't look it up: " + ex.Message;
            }
            finally { btnLookup.IsEnabled = true; }
        }
    }

    // ==========================================================================
    // Step 3: areas
    // ==========================================================================

    sealed class AreasStepView : WizStep
    {
        readonly AreaChooserView chooser;
        RegionDownload bound;
        internal AreaChooserView Chooser => chooser;

        public AreasStepView(WizardState s, Window owner) : base(s, owner)
        {
            chooser = new AreaChooserView(owner);
            Content = chooser;
            chooser.PickedChanged += (o, e) => RaiseValid();
            s.DownloadChanged += (o, e) =>
            {
                if (!IsEffectivelyVisible || State.Download != bound) return;
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

        public override async Task<bool> LeaveForward()
        {
            var picked = chooser.Picked();
            if (picked.Count == 0 &&
                !await Dialogs.Ask(Owner, "No repeaters are picked yet, so the codeplug would only have your hotspot" + (State.Noaa ? " and the weather channels" : "") + ".\n\n" +
                                          "To pick repeaters, click states or counties on this map (each click takes every repeater in it), or tick single ones on the List tab.\n\n" +
                                          "Go on without repeaters?", "Go on"))
                return false;
            if (picked.Count > 300 &&
                !await Dialogs.Ask(Owner, "That's " + picked.Count + " repeaters. With a few talkgroups each, that can pass the radio's 4000 channels.\n\nGo on anyway? (You can trim zones and talkgroups next.)", "Go on"))
                return false;
            if (State.HasZoneWork && State.WouldRebuild(picked) &&
                !await Dialogs.Ask(Owner, "The repeaters (or your radio details) changed, so the zones and the talkgroups you picked for them start over.\n\nGo on?", "Go on"))
                return false;
            State.Picked = picked;
            State.Build();
            return true;
        }
    }

    // ==========================================================================
    // Step 4: zones
    // ==========================================================================

    sealed class ZonesStepView : WizStep
    {
        readonly List<RadioButton> options = new List<RadioButton>();
        readonly TextBox txtSingle;
        readonly DataGrid list;
        bool loading;

        sealed class ZRow
        {
            public ZoneInfo Zone;
            public string Name { get; set; }
            public string Repeaters { get; set; }
            public string Channels { get; set; }
            public string Includes { get; set; }
        }

        public ZonesStepView(WizardState s, Window owner) : base(s, owner)
        {
            var group = "zonescheme" + GetHashCode();
            var left = new StackPanel { Width = 330, Spacing = 4, Margin = new Thickness(0, 0, 12, 0) };
            left.Children.Add(UiKit.Label("Make zones", true));
            foreach (var c in ZonePlanner.Choices)
            {
                var rb = new RadioButton { Content = c.Value, Tag = c.Key, GroupName = group };
                rb.IsCheckedChanged += async (o, e) => { if (rb.IsChecked == true && !loading) await Rezone(); };
                options.Add(rb);
                left.Children.Add(rb);
            }
            txtSingle = new TextBox { Width = 200, MaxLength = 16, Margin = new Thickness(22, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            UiKit.OnText(txtSingle, async () => { if (!loading && Scheme == ZoneScheme.Single) await Rezone(); });
            // Typing a name means "everything in one zone".
            txtSingle.GotFocus += (o, e) => { var single = options.First(x => (ZoneScheme)x.Tag == ZoneScheme.Single); if (single.IsChecked != true) single.IsChecked = true; };
            left.Children.Add(txtSingle);
            left.Children.Add(UiKit.Hint("Repeaters the map couldn't place that precisely go into their state's or country's zone. The radio holds 250 zones of up to 250 channels; bigger zones are split."));

            list = UiKit.Grid(false);
            list.IsReadOnly = true;
            list.Columns.Add(UiKit.Col("Zone", "Name", true, 200));
            list.Columns.Add(UiKit.Col("Repeaters", "Repeaters", true, 90));
            list.Columns.Add(UiKit.Col("Channels so far", "Channels", true, 120));
            list.Columns.Add(UiKit.Col("Includes", "Includes", true, 380));
            var buttons = UiKit.Row(UiKit.Button("Rename / merge...", async () => await Rename()), UiKit.Button("Move up", () => MoveZone(-1)), UiKit.Button("Move down", () => MoveZone(1)));
            buttons.Margin = new Thickness(0, 6, 0, 0);
            var right = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            right.Children.Add(buttons); right.Children.Add(list);

            var dock = new DockPanel();
            DockPanel.SetDock(left, Dock.Left);
            dock.Children.Add(left); dock.Children.Add(right);
            Content = dock;
        }

        public override string Title => "Zones";
        public override string Hint =>
            "A zone is a group of channels you switch between on the radio. Pick how to group the repeaters; you can rename, merge and reorder zones here " +
            "or later on the Zones tab. Talkgroups come next.";

        ZoneScheme Scheme => (ZoneScheme)(options.FirstOrDefault(o => o.IsChecked == true)?.Tag ?? ZoneScheme.County);

        public override void Arrive()
        {
            loading = true;
            foreach (var o in options) o.IsChecked = (ZoneScheme)o.Tag == State.Scheme;
            options.First(o => (ZoneScheme)o.Tag == ZoneScheme.County).IsEnabled = State.Picked.Any(r => r.Location?.Country?.Code == "US");
            txtSingle.Text = State.SingleZone;
            loading = false;
            Fill(null);
        }

        async Task Rezone()
        {
            var p = State.Project;
            string single = Naming.Clean(txtSingle.Text ?? "", 16).Length > 0 ? Naming.Clean(txtSingle.Text, 16) : "DMR";
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
                !await Dialogs.Ask(Owner, "Regrouping makes new zones, so the talkgroups you ticked for each zone are cleared.\n\nRegroup anyway?", "Regroup"))
            {
                loading = true;
                foreach (var o in options) o.IsChecked = (ZoneScheme)o.Tag == State.Scheme;
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
            var rows = new List<ZRow>();
            foreach (var z in p.Zones.Where(z => used.Contains(z.Name)))
            {
                var reps = p.ActiveRepeaters().Where(r => Project.SameZone(r.Zone, z.Name)).ToList();
                var places = reps.Select(r => r == p.Hotspot ? "your hotspot" : !r.IsDigital ? r.Name : r.City).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                rows.Add(new ZRow
                {
                    Zone = z, Name = z.Name,
                    Repeaters = reps.Count.ToString(CultureInfo.InvariantCulture),
                    Channels = p.ZoneChannelCount(z.Name).ToString(CultureInfo.InvariantCulture),
                    Includes = string.Join(", ", places.Take(8)) + (places.Count > 8 ? " and " + (places.Count - 8) + " more" : ""),
                });
            }
            list.ItemsSource = rows;
            var pick = select != null ? rows.FirstOrDefault(r => Project.SameZone(r.Name, select)) : null;
            if (pick != null) list.SelectedItem = pick;
        }

        ZoneInfo Selected => (list.SelectedItem as ZRow)?.Zone;

        async Task Rename()
        {
            var z = Selected;
            if (z == null) return;
            string name = await Dialogs.Prompt(Owner, "Rename zone", "New name for \"" + z.Name + "\" (16 characters max; an existing zone's name merges them):", z.Name, 16);
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

    sealed class ZoneTalkgroupsStepView : WizStep
    {
        readonly DataGrid zones;
        readonly ZoneTalkgroupsView editor;
        bool loading;
        List<ZRow> rows = new List<ZRow>();
        internal ZoneTalkgroupsView Editor => editor;

        sealed class ZRow : RowBase
        {
            public ZoneInfo Zone;
            public Project P;
            public string Name => Zone.Name;
            public string Repeaters => P.ZoneRepeaters(Zone.Name).Count.ToString(CultureInfo.InvariantCulture);
            public string Channels => P.ZoneChannelCount(Zone.Name).ToString(CultureInfo.InvariantCulture);
        }

        public ZoneTalkgroupsStepView(WizardState s, Window owner) : base(s, owner)
        {
            zones = UiKit.Grid(false);
            zones.IsReadOnly = true;
            zones.Width = 310;
            zones.Columns.Add(UiKit.Col("Zone", "Name", true, 150));
            zones.Columns.Add(UiKit.Col("Repeaters", "Repeaters", true, 80));
            zones.Columns.Add(UiKit.Col("Channels", "Channels", true, 76));
            editor = new ZoneTalkgroupsView(owner) { Margin = new Thickness(10, 0, 0, 0) };
            var dock = new DockPanel();
            DockPanel.SetDock(zones, Dock.Left);
            dock.Children.Add(zones); dock.Children.Add(editor);
            Content = dock;
            zones.SelectionChanged += (o, e) => { if (!loading && zones.SelectedItem is ZRow r) editor.Bind(State.Project, r.Name); };
            editor.Changed += (o, e) => { foreach (var r in rows) r.Refresh(); RaiseValid(); };
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
            rows = p.Zones.Where(z => used.Contains(z.Name) && p.ZoneRepeaters(z.Name).Count > 0).Select(z => new ZRow { Zone = z, P = p }).ToList();
            zones.ItemsSource = rows;
            loading = false;
            if (rows.Count > 0) zones.SelectedItem = rows[0];
            else editor.Bind(p, null);
        }
    }
}
