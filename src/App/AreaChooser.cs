using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Picks repeaters from a downloaded region: click counties, states or countries on the map to take every
    /// repeater in them, or tick repeaters one by one on the List tab (also the ones the map couldn't place).
    /// </summary>
    sealed class AreaChooser : UserControl
    {
        public readonly RegionPicker Picker;
        readonly TabControl tabs;
        readonly ListView list;
        readonly ListSorter sorter;
        readonly TextBox txtFilter;
        readonly Label lblSummary, lblWaiting;
        readonly LinkLabel lblSources, lblRepeaterBook;
        readonly HashSet<OnlineRepeater> manualOn = new HashSet<OnlineRepeater>(), manualOff = new HashSet<OnlineRepeater>();
        readonly Dictionary<GeoArea, int> counts = new Dictionary<GeoArea, int>();
        RegionDownload download;
        Project project;
        HashSet<GeoArea> region = new HashSet<GeoArea>();
        HashSet<GeoArea> lastSelected = new HashSet<GeoArea>();
        List<OnlineRepeater> all = new List<OnlineRepeater>();
        bool loading, creatingHandle;

        /// <summary>The picked repeaters changed.</summary>
        public event EventHandler PickedChanged;
        internal TabControl Tabs => tabs;

        public AreaChooser()
        {
            Font = Ui.BaseFont;
            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Ui.S(12), Ui.S(4)) };

            Picker = new RegionPicker { Dock = DockStyle.Fill };
            Picker.AllowLevels(AreaLevel.Country, AreaLevel.State, AreaLevel.County);
            var mapPage = new TabPage("Map") { UseVisualStyleBackColor = true, Padding = new Padding(Ui.S(4)) };
            mapPage.Controls.Add(Picker);
            tabs.TabPages.Add(mapPage);

            txtFilter = new TextBox { Width = Ui.S(240), Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) };
            Ui.SetCue(txtFilter, "city, callsign, county, network or MHz");
            var filterRow = Ui.Row(Ui.Label("Filter"), txtFilter,
                Ui.Button("Tick all shown", (s, e) => SetShown(true)),
                Ui.Button("Untick all shown", (s, e) => SetShown(false)));
            filterRow.Dock = DockStyle.Top;
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Callsign", Ui.S(90));
            list.Columns.Add("City", Ui.S(130));
            list.Columns.Add("On the map", Ui.S(170));
            list.Columns.Add("Output MHz", Ui.S(84), HorizontalAlignment.Right);
            list.Columns.Add("CC", Ui.S(36), HorizontalAlignment.Right);
            list.Columns.Add("Network", Ui.S(110));
            list.Columns.Add("Talkgroups listed", Ui.S(110), HorizontalAlignment.Right);
            list.Columns.Add("", Ui.S(180));
            sorter = Ui.Sortable(list, Refill);
            var listPage = new TabPage("List") { UseVisualStyleBackColor = true, Padding = new Padding(Ui.S(4)) };
            listPage.Controls.Add(list);
            listPage.Controls.Add(filterRow);
            list.BringToFront();
            tabs.TabPages.Add(listPage);

            lblSummary = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = Ui.S(26), Font = Ui.BoldFont, TextAlign = ContentAlignment.MiddleLeft };
            lblWaiting = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.HintColor, Visible = false };

            // Where the listings come from, with links (RepeaterBook's terms: the credit and a link wherever its data is shown).
            lblSources = Links("Repeaters from RadioID.net and BrandMeister (DMR repeaters and their talkgroups) and RepeaterBook (analog FM repeaters, and DMR ones " +
                               "RadioID.net doesn't list). " + RepeaterBookApi.Attribution,
                               new[] { "RadioID.net", "https://radioid.net" }, new[] { "BrandMeister", "https://brandmeister.network" },
                               new[] { "RepeaterBook", RepeaterBookApi.SiteUrl }, new[] { "RepeaterBook.com", RepeaterBookApi.SiteUrl });
            lblRepeaterBook = new LinkLabel { Dock = DockStyle.Bottom, AutoSize = false, Height = Ui.S(24), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.HintColor };
            lblRepeaterBook.LinkClicked += (s, e) =>
            {
                if (!RepeaterBookTokenDialog.Show(FindForm())) return;
                ShowRepeaterBook();
                download?.FetchRegionRepeaterBook();
                FetchRepeaterBook();
            };

            Controls.Add(tabs);
            Controls.Add(lblWaiting);
            Controls.Add(lblRepeaterBook);
            Controls.Add(lblSources);
            Controls.Add(lblSummary);
            tabs.BringToFront();

            Picker.Map.SelectionChanged += (s, e) => AreasToggled();
            Picker.Map.HoverChanged += (s, e) => ShowHover();
            txtFilter.TextChanged += (s, e) => Refill();
            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedIndex == 1) Refill(); else UpdateMap(); };
            // Windows raises ItemChecked for every item while the ListView creates its handle; ignore those.
            list.HandleCreated += (s, e) => { creatingHandle = true; list.BeginInvoke((Action)(() => creatingHandle = false)); };
            list.ItemChecked += (s, e) =>
            {
                if (loading || creatingHandle || sorter.Sorting) return;
                var r = (OnlineRepeater)e.Item.Tag;
                if (InProject(r)) { loading = true; e.Item.Checked = false; loading = false; return; }
                SetPicked(r, e.Item.Checked);
                e.Item.SubItems[7].Text = Status(r);
                UpdateSummary();
                PickedChanged?.Invoke(this, EventArgs.Empty);
            };
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
            lblWaiting.Visible = waiting || failed;
            tabs.Visible = !waiting && !failed;
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

        /// <summary>
        /// Repeaters to offer: placed inside the region, or (when the map couldn't place them that precisely) in a
        /// country the region covers part of.
        /// </summary>
        bool InScope(OnlineRepeater r)
        {
            var loc = r.Location ?? GeoLocation.Unknown;
            var best = loc.County ?? loc.State ?? loc.Country;
            if (best != null && InRegion(best)) return true;
            if (loc.Country == null) return true; // unknown country: let the list show it
            return region.Any(a => a.CountryCode == loc.Country.Code) && (loc.State == null || loc.Precision < LocationPrecision.State);
        }

        bool InProject(OnlineRepeater r)
        {
            return project != null && OnlineImporter.FindExisting(project, r) != null;
        }

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

        /// <summary>The picked repeaters, ordered by place then frequency.</summary>
        public List<OnlineRepeater> Picked()
        {
            return all.Where(IsPicked)
                      .OrderBy(r => r.Location?.State?.Name ?? r.State).ThenBy(r => r.Location?.County?.Name ?? "").ThenBy(r => r.City)
                      .ThenBy(r => r.RxMHz).ThenBy(r => r.Callsign).ToList();
        }

        /// <summary>
        /// An area was clicked on or off. Ticks and unticks made on the List tab for repeaters inside it give way
        /// to the click; the ones elsewhere stay.
        /// </summary>
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
        /// each click is the user's request, up to <see cref="RepeaterBookApi.MaxStatesPerAction"/> states a batch, one batch at
        /// a time. Areas already clicked take the new listings at once (<see cref="Reload"/> follows the download's Changed).
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
            if (IsDisposed || d != download) return;
            Reload();
            PickedChanged?.Invoke(this, EventArgs.Empty);
            if (ok) FetchRepeaterBook(); // areas clicked while that ran
        }

        /// <summary>The RepeaterBook line under the map: the download's last news, else how to turn it on.</summary>
        void ShowRepeaterBook()
        {
            const string add = "Add your RepeaterBook token";
            if (!RepeaterBookToken.IsSet)
                SetLine("RepeaterBook: " + add + " to also bring in analog (FM) repeaters, and DMR ones RadioID.net doesn't list, for your states.", add);
            else
                SetLine(download?.RepeaterBookNote ?? "RepeaterBook: clicking a state or county also brings in its analog (FM) repeaters, and DMR ones RadioID.net doesn't list.", null);
        }

        /// <summary>
        /// Sets the RepeaterBook line, with <paramref name="link"/> (if any) as its only link. Setting a LinkLabel's text makes
        /// all of it one link, so the links are reset after the text.
        /// </summary>
        void SetLine(string text, string link)
        {
            if (lblRepeaterBook.InvokeRequired) { lblRepeaterBook.BeginInvoke((Action)(() => SetLine(text, link))); return; }
            lblRepeaterBook.Text = text;
            lblRepeaterBook.Links.Clear();
            int i = link == null ? -1 : text.IndexOf(link, StringComparison.Ordinal);
            if (i >= 0) lblRepeaterBook.Links.Add(i, link.Length);
        }
        /// <summary>A label with some words linked to web pages: pairs of { text, url }.</summary>
        static LinkLabel Links(string text, params string[][] links)
        {
            var l = new LinkLabel { Dock = DockStyle.Bottom, AutoSize = false, Height = Ui.S(24), TextAlign = ContentAlignment.MiddleLeft, Text = text };
            l.Links.Clear();
            int from = 0;
            foreach (var pair in links)
            {
                int i = text.IndexOf(pair[0], from, StringComparison.Ordinal);
                if (i < 0) continue;
                l.Links.Add(i, pair[0].Length, pair[1]);
                from = i + pair[0].Length;
            }
            l.LinkClicked += (s, e) => { if (e.Link.LinkData is string url) RepeaterBookTokenDialog.Open(l.FindForm(), url); };
            return l;
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
                lblSummary.ForeColor = Color.FromArgb(176, 84, 0);
                return;
            }
            lblSummary.ForeColor = SystemColors.ControlText;
            lblSummary.Text = picked.Count + " of " + all.Count + " repeater" + (all.Count == 1 ? "" : "s") + " picked" +
                              (picked.Count > 0 ? " (" + listed + " talkgroup channels they list themselves)" : "") +
                              (already > 0 ? ", " + already + " already in your project" : "") +
                              (unplaced > 0 ? ".   " + unplaced + " couldn't be placed exactly; find them on the List tab." : ".") + offAirText;
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
            var terms = txtFilter.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
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
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var r in Filtered())
                {
                    var item = new ListViewItem(r.Callsign) { Tag = r, Checked = IsPicked(r), ToolTipText = r.Details + (r.IsOffAir ? " / BrandMeister last heard it " + RepeaterHealth.DateText(r.OffAirSince.Value) : "") };
                    item.SubItems.Add(r.City);
                    item.SubItems.Add(Where(r));
                    item.SubItems.Add(r.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture));
                    item.SubItems.Add(r.IsAnalog ? "" : r.ColorCode.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(r.IsAnalog ? "FM, RepeaterBook" : FromRepeaterBook(r) ? "DMR, RepeaterBook" : r.Network);
                    item.SubItems.Add(r.IsAnalog ? "" : r.Talkgroups.Count == 0 ? "none" : r.Talkgroups.Count.ToString(CultureInfo.InvariantCulture) + (r.TalkgroupSource != null ? " (" + r.TalkgroupSource + ")" : ""));
                    if (r.IsAnalog && !InProject(r)) item.ForeColor = Color.FromArgb(20, 110, 55);
                    item.SubItems.Add(Status(r));
                    if (InProject(r) || (!Takeable(r) && !IsPicked(r))) item.ForeColor = SystemColors.GrayText;
                    list.Items.Add(item);
                }
                list.EndUpdate();
                sorter.Apply();
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
