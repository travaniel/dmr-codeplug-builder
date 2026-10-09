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
    /// Repeaters > Add route (roadmap item 11): two towns and optionally a highway; the route is found on the built-in highways, the
    /// repeaters within a corridor are listed in driving order (from RadioID.net, downloaded for the states the route crosses, and the
    /// project's own), and Add puts the new ones into county zones and all of them into a route zone, with GPS zone switching along it.
    /// </summary>
    sealed class RouteDialog : Form
    {
        readonly Session session;
        readonly TextBox txtFrom, txtTo, txtHighway, txtZone;
        readonly NumericUpDown numCorridor;
        readonly Label lblRoute;
        readonly RegionPicker picker;
        readonly ListView list;
        readonly ComboBox cboPower;
        readonly CheckBox chkGps;
        readonly Button btnAdd;
        RoutePlan plan;
        RegionDownload download;
        List<RouteStop> stops = new List<RouteStop>();
        bool filling;

        public List<string> Notes { get; } = new List<string>();
        public Repeater FirstAdded { get; private set; }

        public RouteDialog(Session session)
        {
            this.session = session;
            Text = "Add a route";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Ui.S(1240), area.Width * 95 / 100), Math.Min(Ui.S(840), area.Height * 95 / 100));
            MinimumSize = new Size(Ui.S(900), Ui.S(600));
            Padding = new Padding(Ui.S(10));

            txtFrom = Ui.Text("e.g. Brownwood, TX");
            txtTo = Ui.Text("e.g. Abilene, TX");
            txtHighway = Ui.Text("any, or e.g. US 183");
            foreach (var t in new[] { txtFrom, txtTo }) t.Width = Ui.S(200);
            txtHighway.Width = Ui.S(140);
            numCorridor = new NumericUpDown { Minimum = 2, Maximum = 80, Value = 15, Width = Ui.S(60) };
            var find = Ui.Button("Find route", async (s, e) => await FindRoute());
            find.Font = Ui.BoldFont;
            var top = Ui.Row(Ui.Label("From"), txtFrom, Ui.Label("To"), txtTo, Ui.Label("Highway"), txtHighway, Ui.Label("Repeaters within"), numCorridor, Ui.Label("km of the road"), find);
            top.Dock = DockStyle.Top;
            top.WrapContents = true;
            lblRoute = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.HintColor, Padding = new Padding(Ui.S(3), Ui.S(4), 0, Ui.S(4)),
                                   Text = "Type two towns and click Find route. The route follows the built-in map's highways; repeaters near it are listed in driving order." };

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = Ui.S(6) };
            picker = new RegionPicker { Dock = DockStyle.Fill };
            split.Panel1.Controls.Add(picker);
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Repeater", Ui.S(170));
            list.Columns.Add("Town", Ui.S(110));
            list.Columns.Add("Along", Ui.S(60), HorizontalAlignment.Right);
            list.Columns.Add("Off road", Ui.S(65), HorizontalAlignment.Right);
            list.Columns.Add("", Ui.S(110));
            split.Panel2.Controls.Add(list);

            txtZone = new TextBox { Width = Ui.S(150), MaxLength = 16 };
            cboPower = Ui.Combo(false, Powers.Values);
            cboPower.Width = Ui.S(80);
            cboPower.SelectedItem = "High";
            chkGps = new CheckBox { Text = "GPS zone switching along the route", AutoSize = true, Checked = true, Margin = new Padding(Ui.S(12), Ui.S(6), 3, 3) };
            var opts = Ui.Row(Ui.Label("Route zone"), txtZone, Ui.Label("Power for new repeaters"), cboPower, chkGps);
            opts.WrapContents = false;
            btnAdd = new Button { Text = "Add route", AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(Ui.S(10), Ui.S(3), Ui.S(10), Ui.S(3)), UseVisualStyleBackColor = true, Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, UseVisualStyleBackColor = true };
            var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            buttons.Controls.Add(btnAdd);
            buttons.Controls.Add(cancel);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2 };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(opts, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);
            bottom.Controls.Add(Ui.Hint("New repeaters go into zones by county (and get those zones' talkgroups); the route zone lists every ticked repeater's channels " +
                                        "in driving order. GPS zone switching puts the counties along the route first among its 32 entries.", Ui.S(1000)), 0, 1);

            Controls.Add(split);
            Controls.Add(lblRoute);
            Controls.Add(top);
            Controls.Add(bottom);
            split.BringToFront();
            CancelButton = cancel;
            AcceptButton = find;
            Ui.InitSplitter(split, Ui.S(720), Ui.S(400), Ui.S(360));

            list.ItemChecked += (s, e) => { if (!filling) UpdateMap(); };
            numCorridor.ValueChanged += (s, e) => Fill(); // a wider or narrower corridor relists without finding the route again
            btnAdd.Click += (s, e) => AddRoute();
            Shown += async (s, e) =>
            {
                await picker.LoadAtlasAsync();
                if (IsDisposed) return;
                var home = session.Project.Home;
                if (home != null) { txtFrom.Text = home.Place ?? ""; picker.Map.ZoomTo(home.Longitude - 3, home.Latitude - 2, home.Longitude + 3, home.Latitude + 2); }
                else picker.Map.ZoomTo(-128, 22, -64, 52);
            };
            FormClosed += (s, e) => { if (download != null) { download.Changed -= OnDownload; download.Cancel(); } };
        }

        string Country => HomeLocation.DefaultCountry(session.Project);

        // Dev check (--route-snapshot): drive the dialog without clicks.
        internal Task LoadMap() { return picker.LoadAtlasAsync(); }
        internal Task Find(string from, string to, string highway) { txtFrom.Text = from; txtTo.Text = to; txtHighway.Text = highway ?? ""; return FindRoute(); }
        internal bool Downloaded => download != null && download.Done;
        internal string RouteText => lblRoute.Text;
        internal int Listed => list.Items.Count;
        internal void Add() { AddRoute(); }

        async Task FindRoute()
        {
            var atlas = picker.Map.Atlas;
            if (atlas == null) return;
            var a = HomeLocation.Parse(atlas, txtFrom.Text, Country);
            var b = HomeLocation.Parse(atlas, txtTo.Text, Country);
            if (a == null || b == null) { lblRoute.Text = "Couldn't find " + (a == null ? "\"" + txtFrom.Text + "\"" : "\"" + txtTo.Text + "\"") + " on the map. Try \"Town, State\"."; lblRoute.ForeColor = Color.Firebrick; return; }
            lblRoute.ForeColor = Ui.HintColor;
            lblRoute.Text = "Finding the route...";
            string hw = txtHighway.Text.Trim();
            plan = await Task.Run(() => RoutePlanner.Find(atlas, a.Latitude, a.Longitude, b.Latitude, b.Longitude, hw.Length > 0 ? hw : null));
            if (IsDisposed) return;
            bool miles = session.Project.Home?.UsesMiles ?? Distances.UsesMiles(a.Country);
            string from = a.Place.Split(',')[0], to = b.Place.Split(',')[0];
            lblRoute.Text = RoutePlanner.Describe(plan, from, to, miles);
            if (txtZone.Text.Trim().Length == 0 || txtZone.Tag as string == txtZone.Text) // only while it's still our suggestion
            {
                string digits = new string(hw.Where(char.IsDigit).ToArray());
                txtZone.Text = Naming.Fit(digits.Length > 0 ? "Hwy " + digits : from + "-" + to, 16); // like the user's own "Hwy 183"
                txtZone.Tag = txtZone.Text;
            }
            picker.Map.Route = plan.Points;
            double minLat = plan.Points.Min(p => p[0]), maxLat = plan.Points.Max(p => p[0]), minLon = plan.Points.Min(p => p[1]), maxLon = plan.Points.Max(p => p[1]);
            picker.Map.ZoomTo(minLon - 0.3, minLat - 0.3, maxLon + 0.3, maxLat + 0.3);

            // Download the states the route crosses (cached for the run), then list what is near the road.
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
            if (IsDisposed || sender != download) return;
            if (InvokeRequired) { BeginInvoke((EventHandler)OnDownload, sender, e); return; } // the download may report from its worker thread
            if (download.Done) Fill();
            else lblRoute.Text = (plan != null ? lblRoute.Text.Split('|')[0].TrimEnd() + "  |  " : "") + download.Status;
        }

        void Fill()
        {
            if (plan == null) return;
            double corridor = (double)numCorridor.Value;
            var p = session.Project;
            var mine = RoutePlanner.AlongProject(plan.Points, p, corridor);
            var listings = download != null && download.Done ? download.Repeaters.Where(r => !r.IsAnalog && r.InRadioBand) : Enumerable.Empty<OnlineRepeater>();
            var fresh = RoutePlanner.Along(plan.Points, listings, corridor).Where(s => OnlineImporter.FindExisting(p, s.Listing) == null && !p.Repeaters.Any(r => r.SourceId > 0 && r.SourceId == s.Listing.DmrId));
            stops = mine.Concat(fresh).OrderBy(s => s.AlongKm).ToList();
            bool miles = p.Home?.UsesMiles ?? true;
            string Dist(double km) { return (miles ? km / Distances.KmPerMile : km).ToString("0", CultureInfo.InvariantCulture) + (miles ? " mi" : " km"); }
            filling = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var s in stops)
            {
                bool off = s.Listing != null && s.Listing.IsOffAir;
                var item = new ListViewItem(s.Repeater != null ? s.Repeater.Name : s.Listing.Callsign + " " + s.Listing.RxMHz.ToString("0.000", CultureInfo.InvariantCulture)) { Tag = s, Checked = !off };
                item.SubItems.Add(s.Repeater != null ? s.Repeater.City ?? "" : s.Listing.City);
                item.SubItems.Add(Dist(s.AlongKm));
                item.SubItems.Add(Dist(s.OffKm));
                item.SubItems.Add(s.Repeater != null ? "in the project" : off ? "off the air?" : "new");
                if (s.Repeater != null) item.ForeColor = Ui.ZoneColor;
                else if (off) item.ForeColor = SystemColors.GrayText;
                list.Items.Add(item);
            }
            list.EndUpdate();
            filling = false;
            if (download != null && download.Done)
                lblRoute.Text = lblRoute.Text.Split('|')[0].TrimEnd() + "  |  " + stops.Count + " repeater(s) within " + numCorridor.Value + " km" + (download.Errors.Count > 0 ? " (download: " + string.Join("; ", download.Errors) + ")" : "");
            UpdateMap();
        }

        List<RouteStop> Ticked => list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (RouteStop)i.Tag).ToList();

        void UpdateMap()
        {
            var ticked = new HashSet<RouteStop>(Ticked);
            picker.Map.Dots = stops.Select(s => new MapDot
            {
                Lat = s.Repeater != null ? s.Repeater.Latitude.Value : s.Listing.Location.Lat.Value,
                Lon = s.Repeater != null ? s.Repeater.Longitude.Value : s.Listing.Location.Lon.Value,
                Highlight = ticked.Contains(s), OffAir = s.Listing != null && s.Listing.IsOffAir, Tag = s,
            }).ToList();
            btnAdd.Enabled = plan != null && ticked.Count > 0;
            btnAdd.Text = ticked.Count == 0 ? "Add route" : "Add route (" + ticked.Count + " repeater" + (ticked.Count == 1 ? "" : "s") + ")";
        }

        void AddRoute()
        {
            var p = session.Project;
            string zone = Naming.Fit(txtZone.Text, 16);
            if (zone.Length == 0) { Ui.Error(this, "Type a name for the route zone."); return; }
            var ticked = Ticked;
            var newOnes = ticked.Where(s => s.Listing != null).Select(s => s.Listing).ToList();
            var added = new List<Repeater>();
            if (newOnes.Count > 0)
            {
                Online.PrepareForAdding(this, newOnes); // BrandMeister's own talkgroups for its repeaters
                var o = new OnlineImportOptions
                {
                    Power = (string)cboPower.SelectedItem ?? "High",
                    ZoneFor = r => ZonePlanner.ZoneName(r, ZoneScheme.County, zone),
                    Scheme = ZoneScheme.County,
                    MoreNames = download?.TalkgroupNames,
                };
                var result = OnlineImporter.AddRepeaters(p, newOnes, o, download?.BrandMeisterNames);
                added = result.Added;
                Notes.AddRange(result.Notes);
                if (result.NewTalkgroups.Count > 0) Notes.Add("New talkgroups: " + string.Join(", ", result.NewTalkgroups.Select(t => t.Name + " (" + t.Id + ")")) + ".");
            }
            // Driving order: the project's own repeaters and the ones just added (matched back by their listing).
            var inOrder = new List<Repeater>();
            foreach (var s in ticked)
            {
                var r = s.Repeater ?? added.FirstOrDefault(x => x.SourceId > 0 && x.SourceId == s.Listing.DmrId) ?? OnlineImporter.FindExisting(p, s.Listing);
                if (r != null && !inOrder.Contains(r)) inOrder.Add(r);
            }
            RouteBuilder.Apply(p, zone, plan.Points, (double)numCorridor.Value, inOrder);
            if (chkGps.Checked) p.Options.GpsZoneSwitching = true;
            Notes.Insert(0, "Route zone \"" + zone + "\": " + inOrder.Count + " repeater(s) in driving order" + (added.Count > 0 ? ", " + added.Count + " of them new (zones by county)" : "") + ".");
            if (chkGps.Checked) Notes.Add("GPS zone switching is on (Settings): the counties along the route come first. Export writes GpsRoaming.CSV; turn on GPS and GPS Roaming in the radio.");
            FirstAdded = added.FirstOrDefault() ?? inOrder.FirstOrDefault();
            DialogResult = DialogResult.OK;
        }
    }
}
