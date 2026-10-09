using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Repeaters > Add route (roadmap item 11), same as the Windows RouteDialog: two towns and optionally a highway; the repeaters near the
    /// route in driving order; Add puts new ones in county zones and all of them in a route zone, with GPS zone switching along it.
    /// </summary>
    sealed class RouteWindow : Window
    {
        readonly Session session;
        readonly TextBox txtFrom, txtTo, txtHighway, txtZone;
        readonly NumericUpDown numCorridor;
        readonly TextBlock lblRoute;
        readonly RegionPickerView picker = new RegionPickerView();
        readonly DataGrid grid;
        readonly ComboBox cboPower;
        readonly CheckBox chkGps;
        readonly Button btnAdd;
        RoutePlan plan;
        RegionDownload download;
        List<StopRow> rows = new List<StopRow>();
        string suggestedZone;
        string routeText = "";

        public List<string> Notes { get; } = new List<string>();
        public Repeater FirstAdded { get; private set; }

        sealed class StopRow : RowBase
        {
            public RouteStop S;
            public Action Changed;
            bool pick;
            public bool Pick { get { return pick; } set { if (pick == value) return; pick = value; Changed?.Invoke(); } }
            public string Repeater { get; set; }
            public string Town { get; set; }
            public string Along { get; set; }
            public string Off { get; set; }
            public string Status { get; set; }
        }

        public RouteWindow(Session session)
        {
            this.session = session;
            Title = "Add a route";
            Width = 1240;
            Height = 840;
            MinWidth = 900;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            txtFrom = new TextBox { Width = 200, Watermark = "e.g. Brownwood, TX" };
            txtTo = new TextBox { Width = 200, Watermark = "e.g. Abilene, TX" };
            txtHighway = new TextBox { Width = 140, Watermark = "any, or e.g. US 183" };
            numCorridor = new NumericUpDown { Minimum = 2, Maximum = 80, Value = 15, Width = 120, FormatString = "0" };
            var find = UiKit.Button("Find route", async () => await FindRoute());
            find.FontWeight = FontWeight.SemiBold;
            find.IsDefault = true;
            var top = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var c in new Control[] { UiKit.Label("From"), txtFrom, UiKit.Label("To"), txtTo, UiKit.Label("Highway"), txtHighway, UiKit.Label("Repeaters within"), numCorridor, UiKit.Label("km of the road"), find })
            { c.Margin = new Thickness(0, 0, 8, 4); if (c is TextBlock t) t.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(c); }
            lblRoute = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6),
                                       Text = "Type two towns and click Find route. The route follows the built-in map's highways; repeaters near it are listed in driving order." };

            grid = UiKit.Grid(false);
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Add", Binding = new Avalonia.Data.Binding("Pick"), Width = new DataGridLength(50) });
            grid.Columns.Add(UiKit.Col("Repeater", "Repeater", true, 170));
            grid.Columns.Add(UiKit.Col("Town", "Town", true, 110));
            grid.Columns.Add(UiKit.Col("Along", "Along", true, 60));
            grid.Columns.Add(UiKit.Col("Off road", "Off", true, 70));
            grid.Columns.Add(UiKit.Col("", "Status", true, 110));
            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 8 };
            Grid.SetColumn(grid, 1);
            body.Children.Add(picker); body.Children.Add(grid);

            txtZone = new TextBox { Width = 150, MaxLength = 16 };
            cboPower = new ComboBox { ItemsSource = Powers.Values, SelectedItem = "High", MinWidth = 90 };
            chkGps = new CheckBox { Content = "GPS zone switching along the route", IsChecked = true, Margin = new Thickness(12, 0, 0, 0) };
            var opts = UiKit.Row(UiKit.Label("Route zone"), txtZone, UiKit.Label("Power for new repeaters"), cboPower, chkGps);
            btnAdd = new Button { Content = "Add route", FontWeight = FontWeight.SemiBold, IsEnabled = false };
            btnAdd.Click += (s, e) => AddRoute();
            var buttons = UiKit.Row(btnAdd, UiKit.Button("Cancel", () => Close(false)));
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            bottom.Children.Add(opts);
            var hint = UiKit.Hint("New repeaters go into zones by county (and get those zones' talkgroups); the route zone lists every ticked repeater's channels " +
                                  "in driving order. GPS zone switching puts the counties along the route first among its 32 entries.");
            var bottomBox = new StackPanel { Children = { bottom, hint } };
            var head = new StackPanel { Children = { top, lblRoute } };
            var dock = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(bottomBox, Dock.Bottom);
            dock.Children.Add(head); dock.Children.Add(bottomBox); dock.Children.Add(body);
            Content = dock;

            Opened += async (s, e) =>
            {
                await picker.LoadAtlasAsync();
                var home = session.Project.Home;
                if (home != null) { txtFrom.Text = home.Place ?? ""; picker.Map.ZoomTo(home.Longitude - 3, home.Latitude - 2, home.Longitude + 3, home.Latitude + 2); }
                else picker.Map.ZoomTo(-128, 22, -64, 52);
            };
            Closed += (s, e) => { if (download != null) { download.Changed -= OnDownload; download.Cancel(); } };
            numCorridor.ValueChanged += (s, e) => Fill(); // a wider or narrower corridor relists without finding the route again
        }

        async Task FindRoute()
        {
            var atlas = picker.Map.Atlas;
            if (atlas == null) return;
            string country = HomeLocation.DefaultCountry(session.Project);
            var a = HomeLocation.Parse(atlas, txtFrom.Text, country);
            var b = HomeLocation.Parse(atlas, txtTo.Text, country);
            if (a == null || b == null) { lblRoute.Text = "Couldn't find \"" + (a == null ? txtFrom.Text : txtTo.Text) + "\" on the map. Try \"Town, State\"."; return; }
            lblRoute.Text = "Finding the route...";
            string hw = (txtHighway.Text ?? "").Trim();
            plan = await Task.Run(() => RoutePlanner.Find(atlas, a.Latitude, a.Longitude, b.Latitude, b.Longitude, hw.Length > 0 ? hw : null));
            bool miles = session.Project.Home?.UsesMiles ?? Distances.UsesMiles(a.Country);
            string from = a.Place.Split(',')[0], to = b.Place.Split(',')[0];
            routeText = RoutePlanner.Describe(plan, from, to, miles);
            lblRoute.Text = routeText;
            if (string.IsNullOrWhiteSpace(txtZone.Text) || txtZone.Text == suggestedZone)
            {
                string digits = new string(hw.Where(char.IsDigit).ToArray());
                txtZone.Text = suggestedZone = Naming.Fit(digits.Length > 0 ? "Hwy " + digits : from + "-" + to, 16);
            }
            picker.Map.Route = plan.Points;
            picker.Map.ZoomTo(plan.Points.Min(p => p[1]) - 0.3, plan.Points.Min(p => p[0]) - 0.3, plan.Points.Max(p => p[1]) + 0.3, plan.Points.Max(p => p[0]) + 0.3);
            var states = RoutePlanner.StatesCrossed(atlas, plan.Points);
            if (download == null || !download.Areas.SequenceEqual(states))
            {
                if (download != null) { download.Changed -= OnDownload; download.Cancel(); }
                download = new RegionDownload(states);
                download.Changed += OnDownload;
                download.Start();
            }
            Fill();
        }

        void OnDownload(object sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (sender != download) return;
                if (download.Done) Fill();
                else lblRoute.Text = routeText + "  |  " + download.Status;
            });
        }

        void Fill()
        {
            if (plan == null) return;
            double corridor = (double)(numCorridor.Value ?? 15);
            var p = session.Project;
            var mine = RoutePlanner.AlongProject(plan.Points, p, corridor);
            var listings = download != null && download.Done ? download.Repeaters.Where(r => !r.IsAnalog && r.InRadioBand) : Enumerable.Empty<OnlineRepeater>();
            var fresh = RoutePlanner.Along(plan.Points, listings, corridor).Where(s => OnlineImporter.FindExisting(p, s.Listing) == null && !p.Repeaters.Any(r => r.SourceId > 0 && r.SourceId == s.Listing.DmrId));
            bool miles = p.Home?.UsesMiles ?? true;
            string Dist(double km) { return (miles ? km / Distances.KmPerMile : km).ToString("0", CultureInfo.InvariantCulture) + (miles ? " mi" : " km"); }
            rows = mine.Concat(fresh).OrderBy(s => s.AlongKm).Select(s =>
            {
                bool off = s.Listing != null && s.Listing.IsOffAir;
                var row = new StopRow
                {
                    S = s, Changed = UpdateMap,
                    Repeater = s.Repeater != null ? s.Repeater.Name : s.Listing.Callsign + " " + s.Listing.RxMHz.ToString("0.000", CultureInfo.InvariantCulture),
                    Town = s.Repeater != null ? s.Repeater.City ?? "" : s.Listing.City,
                    Along = Dist(s.AlongKm), Off = Dist(s.OffKm),
                    Status = s.Repeater != null ? "in the project" : off ? "off the air?" : "new",
                };
                row.Pick = !off;
                return row;
            }).ToList();
            grid.ItemsSource = rows;
            if (download != null && download.Done)
                lblRoute.Text = routeText + "  |  " + rows.Count + " repeater(s) within " + corridor + " km" + (download.Errors.Count > 0 ? " (download: " + string.Join("; ", download.Errors) + ")" : "");
            UpdateMap();
        }

        void UpdateMap()
        {
            picker.Map.Dots = rows.Select(r => new MapDot
            {
                Lat = r.S.Repeater != null ? r.S.Repeater.Latitude.Value : r.S.Listing.Location.Lat.Value,
                Lon = r.S.Repeater != null ? r.S.Repeater.Longitude.Value : r.S.Listing.Location.Lon.Value,
                Highlight = r.Pick, OffAir = r.S.Listing != null && r.S.Listing.IsOffAir, Tag = r.S,
            }).ToList();
            int n = rows.Count(r => r.Pick);
            btnAdd.IsEnabled = plan != null && n > 0;
            btnAdd.Content = n == 0 ? "Add route" : "Add route (" + n + " repeater" + (n == 1 ? "" : "s") + ")";
        }

        async void AddRoute()
        {
            var p = session.Project;
            string zone = Naming.Fit(txtZone.Text ?? "", 16);
            if (zone.Length == 0) { await Dialogs.Error(this, "Type a name for the route zone."); return; }
            var ticked = rows.Where(r => r.Pick).Select(r => r.S).ToList();
            var newOnes = ticked.Where(s => s.Listing != null).Select(s => s.Listing).ToList();
            var added = new List<Repeater>();
            if (newOnes.Count > 0)
            {
                await AreaChooserView.PrepareForAdding(this, newOnes); // BrandMeister's own talkgroups for its repeaters
                var o = new OnlineImportOptions
                {
                    Power = cboPower.SelectedItem as string ?? "High",
                    ZoneFor = r => ZonePlanner.ZoneName(r, ZoneScheme.County, zone),
                    Scheme = ZoneScheme.County,
                    MoreNames = download?.TalkgroupNames,
                };
                var result = OnlineImporter.AddRepeaters(p, newOnes, o, download?.BrandMeisterNames);
                added = result.Added;
                Notes.AddRange(result.Notes);
                if (result.NewTalkgroups.Count > 0) Notes.Add("New talkgroups: " + string.Join(", ", result.NewTalkgroups.Select(t => t.Name + " (" + t.Id + ")")) + ".");
            }
            var inOrder = new List<Repeater>();
            foreach (var s in ticked)
            {
                var r = s.Repeater ?? added.FirstOrDefault(x => x.SourceId > 0 && x.SourceId == s.Listing.DmrId) ?? OnlineImporter.FindExisting(p, s.Listing);
                if (r != null && !inOrder.Contains(r)) inOrder.Add(r);
            }
            RouteBuilder.Apply(p, zone, plan.Points, (double)(numCorridor.Value ?? 15), inOrder);
            bool gps = chkGps.IsChecked == true;
            if (gps) p.Options.GpsZoneSwitching = true;
            Notes.Insert(0, "Route zone \"" + zone + "\": " + inOrder.Count + " repeater(s) in driving order" + (added.Count > 0 ? ", " + added.Count + " of them new (zones by county)" : "") + ".");
            if (gps) Notes.Add("GPS zone switching is on (Settings): the counties along the route come first. Export writes GpsRoaming.CSV; turn on GPS and GPS Roaming in the radio.");
            FirstAdded = added.FirstOrDefault() ?? inOrder.FirstOrDefault();
            Close(true);
        }

        /// <summary>Shows the window; returns the first repeater to select, or null when nothing was added.</summary>
        public static async Task<Repeater> Run(Window owner, Session session)
        {
            var w = new RouteWindow(session);
            if (!await w.ShowDialog<bool>(owner)) return null;
            session.NotifyTalkgroupsChanged(); // also marks the project changed
            await Dialogs.List(owner, "Route added.", new string[0], w.Notes, false);
            return w.FirstAdded;
        }
    }
}
