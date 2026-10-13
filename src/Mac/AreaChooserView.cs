using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Picks repeaters from a downloaded region: click counties, states or countries on the map to take every repeater in
    /// them, or tick repeaters one by one on the List tab (also the ones the map couldn't place). The Windows AreaChooser, in Avalonia.
    /// </summary>
    sealed class AreaChooserView : UserControl
    {
        public readonly RegionPickerView Picker;
        readonly Window owner;
        readonly TabControl tabs;
        readonly DataGrid list;
        readonly TextBox txtFilter;
        readonly TextBlock lblSummary, lblWaiting;
        readonly WrapPanel rbLine;
        readonly GridSorter sorter;
        readonly HashSet<OnlineRepeater> manualOn = new HashSet<OnlineRepeater>(), manualOff = new HashSet<OnlineRepeater>();
        readonly Dictionary<GeoArea, int> counts = new Dictionary<GeoArea, int>();
        RegionDownload download;
        Project project;
        HashSet<GeoArea> region = new HashSet<GeoArea>();
        HashSet<GeoArea> lastSelected = new HashSet<GeoArea>();
        List<OnlineRepeater> all = new List<OnlineRepeater>();
        List<ListRow> rows = new List<ListRow>();
        bool loading;

        /// <summary>The picked repeaters changed.</summary>
        public event EventHandler PickedChanged;

        sealed class ListRow : RowBase
        {
            readonly AreaChooserView owner;
            public readonly OnlineRepeater R;
            public ListRow(AreaChooserView owner, OnlineRepeater r) { this.owner = owner; R = r; }
            public bool Pick
            {
                get { return owner.IsPicked(R); }
                set { owner.RowTicked(this, value); }
            }
            public string Callsign => R.Callsign;
            public string City => R.City;
            public string Where => AreaChooserView.Where(R);
            public string Output => R.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture);
            public decimal OutputMHz => R.RxMHz; // sorts as a number
            public string CC => R.IsAnalog ? "" : R.ColorCode.ToString(CultureInfo.InvariantCulture);
            public string Network => R.IsAnalog ? "FM, RepeaterBook" : FromRepeaterBook(R) ? "DMR, RepeaterBook" : R.Network;
            public string Listed => R.IsAnalog ? "" : R.Talkgroups.Count == 0 ? "none" : R.Talkgroups.Count.ToString(CultureInfo.InvariantCulture) + (R.TalkgroupSource != null ? " (" + R.TalkgroupSource + ")" : "");
            public string State => owner.Status(R);
        }

        public AreaChooserView(Window owner)
        {
            this.owner = owner;
            Picker = new RegionPickerView();
            Picker.AllowLevels(AreaLevel.Country, AreaLevel.State, AreaLevel.County);

            txtFilter = new TextBox { Width = 260, Watermark = "city, callsign, county, network or MHz" };
            var filterRow = UiKit.Row(UiKit.Label("Filter"), txtFilter, UiKit.Button("Tick all shown", () => SetShown(true)), UiKit.Button("Untick all shown", () => SetShown(false)));
            filterRow.Margin = new Thickness(0, 0, 0, 6);
            list = UiKit.Grid(false);
            sorter = new GridSorter(list); // click a header to sort
            list.Columns.Add(new DataGridCheckBoxColumn { Header = "Pick", Binding = new Avalonia.Data.Binding("Pick"), Width = new DataGridLength(54), CanUserSort = false });
            list.Columns.Add(UiKit.Col("Callsign", "Callsign", true, 90));
            list.Columns.Add(UiKit.Col("City", "City", true, 130));
            list.Columns.Add(UiKit.Col("On the map", "Where", true, 190));
            var output = UiKit.Col("Output MHz", "Output", true, 100);
            output.SortMemberPath = "OutputMHz";
            list.Columns.Add(output);
            list.Columns.Add(UiKit.Col("CC", "CC", true, 40));
            list.Columns.Add(UiKit.Col("Network", "Network", true, 120));
            list.Columns.Add(UiKit.Col("Talkgroups listed", "Listed", true, 120));
            list.Columns.Add(UiKit.Col("", "State", true, 180));
            var listPage = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(filterRow, Dock.Top);
            listPage.Children.Add(filterRow);
            listPage.Children.Add(list);

            tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Map", Content = Picker });
            tabs.Items.Add(new TabItem { Header = "List", Content = listPage });

            lblSummary = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            lblWaiting = new TextBlock { TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, IsVisible = false, TextWrapping = TextWrapping.Wrap };

            // The RepeaterBook line (progress, result, or a link to add a token) and where the listings come from.
            rbLine = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var sources = RepeaterBookUi.Sources();
            var dock = new DockPanel();
            DockPanel.SetDock(lblSummary, Dock.Bottom);
            DockPanel.SetDock(sources, Dock.Bottom);
            DockPanel.SetDock(rbLine, Dock.Bottom);
            dock.Children.Add(lblSummary);
            dock.Children.Add(sources);
            dock.Children.Add(rbLine);
            dock.Children.Add(new Grid { Children = { tabs, lblWaiting } });
            Content = dock;

            Picker.Map.SelectionChanged += (s, e) => AreasToggled();
            Picker.Map.HoverChanged += (s, e) => ShowHover();
            UiKit.OnText(txtFilter, Refill);
            tabs.SelectionChanged += (s, e) => { if (e.Source != tabs) return; if (tabs.SelectedIndex == 1) Refill(); else UpdateMap(); };
        }

        /// <summary>Shows a download's repeaters. <paramref name="existing"/> marks repeaters already in that project (never picked).</summary>
        public void Bind(RegionDownload d, Project existing)
        {
            download = d;
            project = existing;
            manualOn.Clear();
            manualOff.Clear();
            region = new HashSet<GeoArea>(d?.Areas ?? Enumerable.Empty<GeoArea>());
            Picker.UseAtlas(GeoAtlas.BuiltIn()); // already loaded by whoever picked the region
            Picker.Map.SetSelected(new GeoArea[0]);
            lastSelected.Clear();
            Picker.Map.CanPick = InRegion;
            Picker.Map.Badge = Badge;
            Reload();
            if (region.Count > 0) Picker.Map.ZoomToAreas(region);
            Picker.Level = region.All(a => a.CountryCode == "US") ? AreaLevel.County : AreaLevel.State;
        }

        /// <summary>Call when the download has more results (or finished).</summary>
        public void Reload()
        {
            all = download == null ? new List<OnlineRepeater>() : download.Repeaters.Where(r => r.InRadioBand && InScope(r)).ToList();
            counts.Clear();
            // Badges count every repeater in an area ("18 repeaters"), RepeaterBook's too (only ones a click takes: not the off-air
            // ones). RepeaterBook rows are counted, never drawn as dots.
            foreach (var r in all.Where(x => !FromRepeaterBook(x) || Takeable(x)))
            {
                var loc = r.Location ?? GeoLocation.Unknown;
                foreach (var a in new[] { loc.Country, loc.State, loc.County })
                    if (a != null) counts[a] = (counts.TryGetValue(a, out int n) ? n : 0) + 1;
            }
            bool failed = download != null && download.Done && all.Count == 0 && download.Errors.Count > 0;
            bool waiting = download != null && !download.Done && all.Count == 0;
            lblWaiting.IsVisible = waiting || failed;
            tabs.IsVisible = !waiting && !failed;
            if (waiting) lblWaiting.Text = download.Status + "\n\nThe map fills in as soon as the download is done.";
            if (failed) lblWaiting.Text = "The repeater list couldn't be downloaded:\n" + string.Join("\n", download.Errors) + "\n\nCheck your internet connection and try again.";
            UpdateMap();
            if (tabs.SelectedIndex == 1) Refill();
            UpdateSummary();
            ShowRepeaterBook();
        }

        /// <summary>Areas inside the downloaded region (an area, or anything within it).</summary>
        bool InRegion(GeoArea a)
        {
            for (var x = a; x != null; x = x.Parent)
                if (region.Contains(x)) return true;
            return false;
        }

        /// <summary>Repeaters to offer: placed inside the region, or (when the map couldn't place them that precisely) in a country the region covers part of.</summary>
        bool InScope(OnlineRepeater r)
        {
            var loc = r.Location ?? GeoLocation.Unknown;
            var best = loc.County ?? loc.State ?? loc.Country;
            if (best != null && InRegion(best)) return true;
            if (loc.Country == null) return true; // unknown country: let the list show it
            return region.Any(a => a.CountryCode == loc.Country.Code) && (loc.State == null || loc.Precision < LocationPrecision.State);
        }

        bool InProject(OnlineRepeater r) { return project != null && OnlineImporter.FindExisting(project, r) != null; }

        bool InPickedArea(OnlineRepeater r)
        {
            var loc = r.Location;
            return loc != null && Picker.Map.Selected.Any(a => loc.IsIn(a));
        }

        public bool IsPicked(OnlineRepeater r)
        {
            if (InProject(r) || manualOff.Contains(r)) return false;
            return manualOn.Contains(r) || (InPickedArea(r) && Takeable(r)); // off-air repeaters only when ticked one by one
        }

        static bool FromRepeaterBook(OnlineRepeater r) { return r.Source == RepeaterBookImport.Site; }

        /// <summary>Taken by area clicks: not off the air (BrandMeister's health check or RepeaterBook's status), and addable.</summary>
        static bool Takeable(OnlineRepeater r)
        {
            if (r.IsOffAir) return false;
            if (FromRepeaterBook(r) && (r.Status == "OFF-AIR" || (r.Analog != null && RepeaterBookImport.Problem(r.Analog) != null))) return false;
            return true;
        }

        void SetPicked(OnlineRepeater r, bool on)
        {
            manualOn.Remove(r);
            manualOff.Remove(r);
            bool byArea = InPickedArea(r) && Takeable(r);
            if (on && !byArea) manualOn.Add(r);
            if (!on && byArea) manualOff.Add(r);
        }

        /// <summary>A box on the List tab was ticked or unticked.</summary>
        void RowTicked(ListRow row, bool on)
        {
            if (loading) return;
            if (InProject(row.R)) { row.Refresh(); return; }
            SetPicked(row.R, on);
            row.Refresh();
            UpdateMap();
            UpdateSummary();
            PickedChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The picked repeaters, ordered by place then frequency.</summary>
        public List<OnlineRepeater> Picked()
        {
            return all.Where(IsPicked)
                      .OrderBy(r => r.Location?.State?.Name ?? r.State).ThenBy(r => r.Location?.County?.Name ?? "").ThenBy(r => r.City)
                      .ThenBy(r => r.RxMHz).ThenBy(r => r.Callsign).ToList();
        }

        /// <summary>An area was clicked on or off. Ticks made on the List tab for repeaters inside it give way to the click.</summary>
        void AreasToggled()
        {
            var now = new HashSet<GeoArea>(Picker.Map.Selected);
            var toggled = now.Where(a => !lastSelected.Contains(a)).Concat(lastSelected.Where(a => !now.Contains(a))).ToList();
            lastSelected = now;
            bool Inside(OnlineRepeater r) { return r.Location != null && toggled.Any(a => r.Location.IsIn(a)); }
            manualOn.RemoveWhere(Inside);
            manualOff.RemoveWhere(Inside);
            Changed();
            FetchRepeaterBook();
        }

        /// <summary>
        /// RepeaterBook for the US states of the clicked areas that the download doesn't have yet, with the user's own token:
        /// each click is the user's request, up to <see cref="RepeaterBookApi.MaxStatesPerAction"/> states a batch, one batch
        /// at a time (the states picked on the first map arrive by themselves, RegionDownload.FetchRegionRepeaterBook).
        /// </summary>
        async void FetchRepeaterBook()
        {
            var d = download;
            if (d == null || !d.Done) return;
            if (!RepeaterBookToken.IsSet) { ShowRepeaterBook(); return; }
            var states = RegionDownload.UsStates(Picker.Map.Selected).Where(s => !d.HasRepeaterBook(s)).ToList();
            if (states.Count == 0)
            {
                if (Picker.Map.Selected.Any(a => a.Level == AreaLevel.Country && a.CountryCode == "US") && !d.RepeaterBookBusy)
                    SetLine("RepeaterBook: click states or counties (not the whole country) to bring in its repeaters.", null);
                return;
            }
            if (d.RepeaterBookBusy) return; // this runs again when that batch is done
            bool ok = await d.FetchRepeaterBookAsync(states);
            if (d != download) return;
            Reload();
            PickedChanged?.Invoke(this, EventArgs.Empty);
            if (ok) FetchRepeaterBook(); // areas clicked while that ran
        }

        /// <summary>The RepeaterBook line under the map: the download's last news, else how to turn it on.</summary>
        void ShowRepeaterBook()
        {
            if (!RepeaterBookToken.IsSet)
                SetLine("RepeaterBook: ", "Add your RepeaterBook token", " to also bring in analog (FM) repeaters, and DMR ones RadioID.net doesn't list, for your states.");
            else
                SetLine(download?.RepeaterBookNote ?? "RepeaterBook: clicking a state or county also brings in its analog (FM) repeaters, and DMR ones RadioID.net doesn't list.", null);
        }

        void SetLine(string text, string link, string after = "")
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { Avalonia.Threading.Dispatcher.UIThread.Post(() => SetLine(text, link, after)); return; }
            rbLine.Children.Clear();
            rbLine.Children.Add(new TextBlock { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            if (link == null) return;
            rbLine.Children.Add(RepeaterBookUi.Link(link, async () =>
            {
                if (!await RepeaterBookUi.ShowTokenWindow(owner)) return;
                ShowRepeaterBook();
                download?.FetchRegionRepeaterBook();
                FetchRepeaterBook();
            }));
            rbLine.Children.Add(new TextBlock { Text = after, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        }

        string Badge(GeoArea a)
        {
            if (!counts.TryGetValue(a, out int n) || n == 0) return null;
            if (!Picker.Map.Selected.Contains(a)) return n + (n == 1 ? " repeater" : " repeaters");
            int picked = all.Count(r => r.Location != null && r.Location.IsIn(a) && IsPicked(r));
            return picked == n ? n + " picked" : picked + " of " + n + " picked";
        }

        void ShowHover()
        {
            var a = Picker.Map.HoverArea;
            if (a == null) { Picker.Status = "Click areas to take every repeater in them. Wheel or + / - to zoom, drag to move."; return; }
            int n = counts.TryGetValue(a, out int c) ? c : 0;
            Picker.Status = a.FullName + ": " + (n == 0 ? "no repeaters listed" : n + " repeater" + (n == 1 ? "" : "s")) + (InRegion(a) ? "" : " (outside the region you downloaded)");
        }

        void Changed()
        {
            UpdateMap();
            UpdateSummary();
            PickedChanged?.Invoke(this, EventArgs.Empty);
        }

        void UpdateMap()
        {
            // RepeaterBook rows are listed on the List tab only, never drawn on the map (no map of RepeaterBook data).
            Picker.Map.Dots = all.Where(r => !FromRepeaterBook(r) && r.Location?.Lat != null)
                                 .Select(r => new MapDot { Lon = r.Location.Lon.Value, Lat = r.Location.Lat.Value, Highlight = IsPicked(r), Analog = r.IsAnalog, OffAir = r.IsOffAir, Tag = r })
                                 .ToList();
        }

        void UpdateSummary()
        {
            var picked = all.Where(IsPicked).ToList();
            int listed = picked.Sum(r => r.Talkgroups.Count);
            int already = all.Count(InProject);
            int unplaced = all.Count(r => r.Location == null || r.Location.Lat == null);
            int offAir = all.Count(r => !Takeable(r) && !IsPicked(r) && !InProject(r));
            string offAirText = offAir == 0 ? "" : "   " + offAir + " look off the air (grey) and aren't taken by clicks.";
            if (picked.Count == 0 && all.Count > 0)
            {
                lblSummary.Text = "Nothing picked yet: click a state or county on the map to take its repeaters (" + all.Count + " to choose from), or tick single ones on the List tab." +
                                  (unplaced > 0 ? " " + unplaced + " couldn't be placed exactly." : "") + offAirText;
                lblSummary.Foreground = Brushes.DarkOrange;
                return;
            }
            lblSummary.Foreground = null;
            lblSummary.Text = picked.Count + " of " + all.Count + " repeater" + (all.Count == 1 ? "" : "s") + " picked" +
                              (picked.Count > 0 ? " (" + listed + " talkgroup channels they list themselves)" : "") +
                              (already > 0 ? ", " + already + " already in your project" : "") +
                              (unplaced > 0 ? ".   " + unplaced + " couldn't be placed exactly; find them on the List tab." : ".") + offAirText;
        }

        /// <summary>
        /// Checks the picked BrandMeister-only repeaters and gives them BrandMeister's static talkgroups (<see cref="Online.PrepareForAdding"/>),
        /// with a wait window while anything has to be asked. On any failure, or Cancel, they keep RadioID.net's lists. (Windows: OnlineUi.cs.)
        /// </summary>
        public static async Task PrepareForAdding(Window owner, List<OnlineRepeater> picked)
        {
            if (!Online.NeedsPreparing(picked)) return;
            using (var cancel = new System.Threading.CancellationTokenSource())
            {
                try
                {
                    await Dialogs.Progress(owner, "BrandMeister", pr => Online.PrepareForAdding(picked, pr, cancel.Token),
                        "Asking BrandMeister about the picked repeaters: still on the air, and which talkgroups they carry...", cancel);
                }
                catch { }
            }
        }

        string Status(OnlineRepeater r)
        {
            if (InProject(r)) return "in your project";
            string off = r.IsOffAir ? "off the air since " + r.OffAirSince.Value.Year.ToString(CultureInfo.InvariantCulture)
                       : FromRepeaterBook(r) && !Takeable(r) ? (r.Status == "OFF-AIR" ? "off the air (RepeaterBook)" : RepeaterBookImport.Problem(r.Analog)) : "";
            return IsPicked(r) ? (off.Length > 0 ? "picked (" + off + ")" : "picked") : off;
        }

        static string Where(OnlineRepeater r)
        {
            var loc = r.Location ?? GeoLocation.Unknown;
            if (loc.County != null) return loc.County.Name + ", " + (loc.State?.Name ?? "");
            if (loc.State != null) return loc.State.Name + (loc.Precision >= LocationPrecision.City ? "" : " (city not found)");
            if (loc.Country != null) return loc.Country.Name + " (not placed)";
            return "(not placed)";
        }

        IEnumerable<OnlineRepeater> Filtered()
        {
            var terms = (txtFilter.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return all.Where(r => terms.All(t =>
                       r.Callsign.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       r.City.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       Where(r).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       r.Network.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       r.RxMHz.ToString("0.00000", CultureInfo.InvariantCulture).StartsWith(t, StringComparison.Ordinal)))
                      .OrderBy(r => Where(r)).ThenBy(r => r.City).ThenBy(r => r.RxMHz);
        }

        void Refill()
        {
            loading = true;
            try
            {
                rows = Filtered().Select(r => new ListRow(this, r)).ToList();
                sorter.Show(rows);
            }
            finally { loading = false; }
        }

        void SetShown(bool on)
        {
            foreach (var r in Filtered()) if (!InProject(r)) SetPicked(r, on);
            Refill();
            Changed();
        }
    }
}
