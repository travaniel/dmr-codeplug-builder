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
        readonly Control chirpRow;
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
            public string CC => R.IsAnalog ? "" : R.ColorCode.ToString(CultureInfo.InvariantCulture);
            public string Network => R.IsAnalog ? "FM (analog)" : R.Network;
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
            list.Columns.Add(new DataGridCheckBoxColumn { Header = "Pick", Binding = new Avalonia.Data.Binding("Pick"), Width = new DataGridLength(54) });
            list.Columns.Add(UiKit.Col("Callsign", "Callsign", true, 90));
            list.Columns.Add(UiKit.Col("City", "City", true, 130));
            list.Columns.Add(UiKit.Col("On the map", "Where", true, 190));
            list.Columns.Add(UiKit.Col("Output MHz", "Output", true, 100));
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
            var chirpButton = UiKit.Button("Add analog repeaters from CHIRP files...", async () => await AddChirpFiles());
            chirpRow = UiKit.Row(chirpButton, new TextBlock { Text = "FM repeaters exported from RepeaterBook in CHIRP format, one file per state. They are listed on the List tab.", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
            var credit = new TextBlock { Text = RepeaterBookApi.Attribution, Foreground = Brushes.CornflowerBlue, TextDecorations = TextDecorations.Underline, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
            credit.PointerPressed += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepeaterBookApi.SiteUrl) { UseShellExecute = true }); } catch { } };
            ((StackPanel)chirpRow).Children.Add(credit);
            ((StackPanel)chirpRow).Margin = new Thickness(0, 6, 0, 0);

            var dock = new DockPanel();
            DockPanel.SetDock(lblSummary, Dock.Bottom); DockPanel.SetDock(chirpRow, Dock.Bottom);
            dock.Children.Add(lblSummary); dock.Children.Add(chirpRow);
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
            foreach (var r in all.Where(x => !x.IsAnalog)) // badges count DMR repeaters from RadioID.net and BrandMeister only
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
            return manualOn.Contains(r) || (InPickedArea(r) && !r.IsOffAir); // off-air repeaters only when ticked one by one
        }

        void SetPicked(OnlineRepeater r, bool on)
        {
            manualOn.Remove(r);
            manualOff.Remove(r);
            bool byArea = InPickedArea(r) && !r.IsOffAir;
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
            // RepeaterBook (analog) rows are listed on the List tab only, never drawn on the map.
            Picker.Map.Dots = all.Where(r => !r.IsAnalog && r.Location?.Lat != null)
                                 .Select(r => new MapDot { Lon = r.Location.Lon.Value, Lat = r.Location.Lat.Value, Highlight = IsPicked(r), Analog = r.IsAnalog, OffAir = r.IsOffAir, Tag = r })
                                 .ToList();
        }

        void UpdateSummary()
        {
            var picked = all.Where(IsPicked).ToList();
            int listed = picked.Sum(r => r.Talkgroups.Count);
            int already = all.Count(InProject);
            int unplaced = all.Count(r => r.Location == null || r.Location.Lat == null);
            int offAir = all.Count(r => r.IsOffAir && !IsPicked(r) && !InProject(r));
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
        /// with a wait window while anything has to be asked. On any failure they keep RadioID.net's lists. (Windows: Online.FetchStaticTalkgroups.)
        /// </summary>
        public static async Task PrepareForAdding(Window owner, List<OnlineRepeater> picked)
        {
            if (!Online.NeedsPreparing(picked)) return;
            try
            {
                await Dialogs.Progress(owner, "BrandMeister", pr => Online.PrepareForAdding(picked, pr),
                    "Asking BrandMeister about the picked repeaters: still on the air, and which talkgroups they carry...");
            }
            catch { }
        }

        string Status(OnlineRepeater r)
        {
            if (InProject(r)) return "in your project";
            string off = r.IsOffAir ? "off the air since " + r.OffAirSince.Value.Year.ToString(CultureInfo.InvariantCulture) : "";
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
                list.ItemsSource = rows;
            }
            finally { loading = false; }
        }

        void SetShown(bool on)
        {
            foreach (var r in Filtered()) if (!InProject(r)) SetPicked(r, on);
            Refill();
            Changed();
        }

        /// <summary>
        /// Adds analog repeaters from CHIRP files (RepeaterBook exports), one file per state, to the download: each line is placed
        /// at its town, and clicking areas then takes them along with the DMR repeaters.
        /// </summary>
        async Task AddChirpFiles()
        {
            if (download == null || !download.Done) { await Dialogs.Info(owner, "Wait for the repeater download to finish first."); return; }
            string downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick CHIRP files (one per state)",
                AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("CHIRP CSV") { Patterns = new[] { "*.csv" } } },
                SuggestedStartLocation = System.IO.Directory.Exists(downloads) ? await owner.StorageProvider.TryGetFolderFromPathAsync(downloads) : null,
            });
            if (picked.Count == 0) return;

            var atlas = GeoAtlas.BuiltIn();
            var regionStates = region.Where(a => a.Level == AreaLevel.State).Concat(region.Where(a => a.Level == AreaLevel.County).Select(a => a.Parent)).Distinct().ToList();
            var lines = new List<string>();
            foreach (var file in picked)
            {
                string path = file.Path.LocalPath;
                string name = System.IO.Path.GetFileName(path);
                List<ChirpChannel> channels;
                var notes = new List<string>();
                try { channels = ChirpCsv.Parse(CsvTable.Load(path), notes); }
                catch (Exception ex) { lines.Add(name + ": couldn't be read (" + ex.Message + ")."); continue; }
                if (channels.Count == 0) { lines.Add(name + ": no CHIRP channels. " + string.Join(" ", notes)); continue; }

                // The state: from the lines themselves, else the file or folder name, else ask.
                string state, country = "United States";
                string fromLines = channels.Where(c => c.State.Length > 0).GroupBy(c => c.State, StringComparer.OrdinalIgnoreCase)
                                           .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
                var fromName = RepeaterBookImport.StateFromName(path);
                if (fromLines != null) state = fromLines;
                else if (fromName.HasValue) { state = fromName.Value.Key; country = fromName.Value.Value; }
                else
                {
                    string typed = await Dialogs.Prompt(owner, "Which state?", name + " doesn't say which state it's for. State or province:", regionStates.Count == 1 ? regionStates[0].Name : "", 40);
                    if (string.IsNullOrWhiteSpace(typed)) { lines.Add(name + ": skipped (no state)."); continue; }
                    state = typed.Trim();
                }
                if (atlas.FindState(atlas.Find("US"), state) == null && atlas.FindState(atlas.Find("CA"), state) != null) country = "Canada";

                var listings = RepeaterBookImport.ToListings(channels, state, country, atlas);
                int placed = listings.Count(l => l.Location?.Lat != null);
                int outside = listings.Count(l => !InScope(l));
                int added = download.AddListings(listings);
                lines.Add(name + " (" + state + "): " + channels.Count + " lines, " + added + " added" +
                          (channels.Count - listings.Count > 0 ? ", " + (channels.Count - listings.Count) + " not FM or outside the radio's bands" : "") +
                          (listings.Count - added > 0 ? ", " + (listings.Count - added) + " already listed (as DMR or in another file)" : "") +
                          ", " + placed + " placed on the map" + (listings.Count - placed > 0 ? " (the rest are on the List tab)" : "") + "." +
                          (outside > 0 ? " " + outside + " are outside the area you downloaded, so they don't show; add " + state + " to the region to see them." : ""));
            }
            Reload();
            Changed();
            if (lines.Count > 0) await Dialogs.Info(owner, string.Join("\n\n", lines), "CHIRP files");
        }
    }
}
