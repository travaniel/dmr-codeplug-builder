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
    /// Picks repeaters from a downloaded region: click counties, states or countries on the map to take every
    /// repeater in them, or tick repeaters one by one on the List tab (also the ones the map couldn't place).
    /// </summary>
    sealed class AreaChooser : UserControl
    {
        public readonly RegionPicker Picker;
        readonly TabControl tabs;
        readonly ListView list;
        readonly TextBox txtFilter;
        readonly Label lblSummary, lblWaiting;
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
            list.Columns.Add("", Ui.S(110));
            var listPage = new TabPage("List") { UseVisualStyleBackColor = true, Padding = new Padding(Ui.S(4)) };
            listPage.Controls.Add(list);
            listPage.Controls.Add(filterRow);
            list.BringToFront();
            tabs.TabPages.Add(listPage);

            lblSummary = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = Ui.S(26), Font = Ui.BoldFont, TextAlign = ContentAlignment.MiddleLeft };
            lblWaiting = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.HintColor, Visible = false };

            Controls.Add(tabs);
            Controls.Add(lblWaiting);
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
                if (loading || creatingHandle) return;
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
            foreach (var r in all)
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
            return manualOn.Contains(r) || InPickedArea(r);
        }

        void SetPicked(OnlineRepeater r, bool on)
        {
            manualOn.Remove(r);
            manualOff.Remove(r);
            bool byArea = InPickedArea(r);
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
            Picker.Map.Dots = all.Where(r => r.Location?.Lat != null)
                                 .Select(r => new MapDot { Lon = r.Location.Lon.Value, Lat = r.Location.Lat.Value, Highlight = IsPicked(r), Tag = r })
                                 .ToList();
        }

        void UpdateSummary()
        {
            var picked = all.Where(IsPicked).ToList();
            int listed = picked.Sum(r => r.Talkgroups.Count);
            int already = all.Count(InProject);
            int unplaced = all.Count(r => r.Location == null || r.Location.Lat == null);
            if (picked.Count == 0 && all.Count > 0)
            {
                int notPlaced = all.Count(r => r.Location == null || r.Location.Lat == null);
                lblSummary.Text = "Nothing picked yet: click a state or county on the map to take its repeaters (" + all.Count + " to choose from), or tick single ones on the List tab." +
                                  (notPlaced > 0 ? " " + notPlaced + " couldn't be placed exactly." : "");
                lblSummary.ForeColor = Color.FromArgb(176, 84, 0);
                return;
            }
            lblSummary.ForeColor = SystemColors.ControlText;
            lblSummary.Text = picked.Count + " of " + all.Count + " repeater" + (all.Count == 1 ? "" : "s") + " picked" +
                              (picked.Count > 0 ? " (" + listed + " talkgroup channels they list themselves)" : "") +
                              (already > 0 ? ", " + already + " already in your project" : "") +
                              (unplaced > 0 ? ".   " + unplaced + " couldn't be placed exactly; find them on the List tab." : ".");
        }

        string Status(OnlineRepeater r)
        {
            if (InProject(r)) return "in your project";
            return IsPicked(r) ? "picked" : "";
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
                    var item = new ListViewItem(r.Callsign) { Tag = r, Checked = IsPicked(r), ToolTipText = r.Details };
                    item.SubItems.Add(r.City);
                    item.SubItems.Add(Where(r));
                    item.SubItems.Add(r.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture));
                    item.SubItems.Add(r.ColorCode.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(r.Network);
                    item.SubItems.Add(r.Talkgroups.Count == 0 ? "none" : r.Talkgroups.Count.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(Status(r));
                    if (InProject(r)) item.ForeColor = SystemColors.GrayText;
                    list.Items.Add(item);
                }
                list.EndUpdate();
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
