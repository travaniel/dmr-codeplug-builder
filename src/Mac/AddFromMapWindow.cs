using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Repeaters > Add from map: the area picker for an open project. New repeaters go into zones by county, city, state... or
    /// into one zone, and pick up that zone's talkgroup set automatically.
    /// </summary>
    sealed class AddFromMapWindow : Window
    {
        readonly Session session;
        readonly AreaChooserView chooser;
        readonly TextBlock lblRegion, lblDownload;
        readonly ComboBox cboScheme, cboPower;
        readonly AutoCompleteBox cboZone;
        readonly Button btnAdd;
        RegionDownload download;

        public OnlineImportResult Result { get; private set; }

        // For --addmap-check (dev): the download and chooser to look at and click.
        internal RegionDownload Download => download;
        internal AreaChooserView Chooser => chooser;

        public AddFromMapWindow(Session session)
        {
            this.session = session;
            Title = "Add repeaters from the map";
            Width = 1180;
            Height = 820;
            MinWidth = 860;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            lblRegion = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            lblDownload = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var top = UiKit.Row(UiKit.Label("Region"), lblRegion, UiKit.Button("Change region...", async () => await ChangeRegion()), lblDownload);
            top.Margin = new Thickness(0, 0, 0, 6);

            chooser = new AreaChooserView(this);

            cboScheme = new ComboBox { ItemsSource = ZonePlanner.Choices.Select(c => c.Value).ToList(), MinWidth = 230 };
            cboZone = UiKit.Suggest(session.Project.Zones.Select(z => z.Name), 170);
            cboPower = new ComboBox { ItemsSource = Powers.Values, SelectedItem = "High", MinWidth = 90 };
            var opts = UiKit.Row(UiKit.Label("Put new repeaters in"), cboScheme, cboZone, UiKit.Label("Power"), cboPower);

            btnAdd = new Button { Content = "Add repeaters", FontWeight = FontWeight.SemiBold, IsEnabled = false };
            btnAdd.Click += (s, e) => AddPicked();
            var cancel = UiKit.Button("Cancel", () => Close(false));
            var buttons = UiKit.Row(btnAdd, cancel);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            bottom.Children.Add(opts);
            var hint = UiKit.Hint("Repeaters land in the zone you choose and get that zone's ticked talkgroups (Zones tab) on top of the ones they list themselves.");
            hint.Margin = new Thickness(0, 4, 0, 0);

            var dock = new DockPanel { Margin = new Thickness(12) };
            var bottomBox = new StackPanel { Children = { bottom, hint } };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottomBox, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(bottomBox); dock.Children.Add(chooser);
            Content = dock;

            bool hasCounty = session.Project.Repeaters.Any(r => !string.IsNullOrEmpty(r.County));
            cboScheme.SelectedIndex = Array.FindIndex(ZonePlanner.Choices, c => c.Key == (hasCounty ? ZoneScheme.County : ZoneScheme.City));
            cboZone.Text = session.Project.Zones.FirstOrDefault()?.Name ?? "DMR";
            cboScheme.SelectionChanged += (s, e) => cboZone.IsEnabled = Scheme == ZoneScheme.Single;
            cboZone.IsEnabled = Scheme == ZoneScheme.Single;
            chooser.PickedChanged += (s, e) => UpdateAdd();
            Opened += async (s, e) => await Begin();
            Closed += (s, e) => { if (download != null) { download.Changed -= OnDownload; download.Cancel(); } };
        }

        ZoneScheme Scheme => cboScheme.SelectedIndex >= 0 ? ZonePlanner.Choices[cboScheme.SelectedIndex].Key : ZoneScheme.City;

        async Task Begin()
        {
            await chooser.Picker.LoadAtlasAsync();
            var atlas = chooser.Picker.Map.Atlas;
            var region = (AppSettings.Get("Region") ?? "").Split(',').Select(atlas.Find).Where(a => a != null).ToList();
            if (region.Count == 0)
            {
                // Guess from the project's repeaters (their states), else ask.
                region = session.Project.Repeaters.Select(r => atlas.Find(r.AreaCode)).Where(a => a != null)
                                .Select(a => a.Level == AreaLevel.County ? a.Parent : a).Distinct().ToList();
            }
            if (region.Count == 0)
            {
                region = await AskRegion();
                if (region == null) { Close(false); return; }
            }
            StartDownload(region);
        }

        async Task ChangeRegion()
        {
            var region = await AskRegion();
            if (region != null) StartDownload(region);
        }

        async Task<List<GeoArea>> AskRegion()
        {
            var d = new RegionWindow(download?.Areas);
            var ok = await d.ShowDialog<bool>(this);
            if (!ok || d.Picked.Count == 0) return null;
            AppSettings.Set("Region", string.Join(",", d.Picked.Select(a => a.Code)));
            return d.Picked;
        }

        void StartDownload(List<GeoArea> region)
        {
            if (download != null) { download.Changed -= OnDownload; download.Cancel(); }
            download = new RegionDownload(region);
            download.Changed += OnDownload;
            lblRegion.Text = RegionDownload.Describe(region);
            chooser.Bind(download, session.Project);
            download.Start();
            UpdateAdd();
        }

        void OnDownload(object sender, EventArgs e)
        {
            if (sender != download) return;
            lblDownload.Text = download.Done && download.Errors.Count > 0 ? download.Status + " " + string.Join("; ", download.Errors) : download.Status;
            lblDownload.Foreground = download.Done && download.Errors.Count > 0 ? Brushes.Firebrick : null;
            chooser.Reload();
            UpdateAdd();
        }

        void UpdateAdd()
        {
            int n = download != null && download.Done ? chooser.Picked().Count : 0;
            btnAdd.IsEnabled = n > 0;
            btnAdd.Content = n == 0 ? "Add repeaters" : "Add " + n + " repeater" + (n == 1 ? "" : "s");
        }

        async void AddPicked()
        {
            string single = Naming.Clean(cboZone.Text ?? "", 16);
            if (Scheme == ZoneScheme.Single && single.Length == 0) { await Dialogs.Error(this, "Type a zone name."); return; }
            var scheme = Scheme;
            var o = new OnlineImportOptions
            {
                Power = cboPower.SelectedItem as string ?? "High",
                ZoneFor = r => ZonePlanner.ZoneName(r, scheme, single.Length > 0 ? single : "DMR"),
                Scheme = scheme,
                MoreNames = download.TalkgroupNames,
            };
            var picked = chooser.Picked();
            await AreaChooserView.PrepareForAdding(this, picked); // BrandMeister's own talkgroups for its repeaters
            Result = OnlineImporter.AddRepeaters(session.Project, picked, o, download.BrandMeisterNames);
            Close(true);
        }

        /// <summary>Shows the window and reports what was added. Returns the first added repeater (or null).</summary>
        public static async Task<Repeater> Run(Window owner, Session session)
        {
            var d = new AddFromMapWindow(session);
            if (!await d.ShowDialog<bool>(owner) || d.Result == null) return null;
            var r = d.Result;
            session.NotifyTalkgroupsChanged();
            var lines = new List<string>();
            if (r.NewTalkgroups.Count > 0)
                lines.Add("New talkgroups: " + string.Join(", ", r.NewTalkgroups.Select(t => t.Name + " (" + t.Id + ")")) + ".");
            lines.AddRange(r.Notes);
            if (r.Added.Any(x => !x.IsDigital)) lines.Add(RepeaterBookApi.Attribution + " (" + RepeaterBookApi.SiteUrl + ")");
            int bare = r.Added.Count(x => x.Talkgroups.Count == 0);
            if (bare > 0) lines.Add(bare + " of them have no talkgroups yet. Tick some for their zones on the Zones tab.");
            await Dialogs.List(owner, "Added " + r.Added.Count + " repeater" + (r.Added.Count == 1 ? "" : "s") + " (" + r.Channels + " channels).", new string[0], lines, false);
            return r.Added.FirstOrDefault();
        }
    }

    /// <summary>Pick the states or countries to download (used by Add from map).</summary>
    sealed class RegionWindow : Window
    {
        readonly RegionPickerView picker = new RegionPickerView();
        readonly IEnumerable<GeoArea> initial;

        public List<GeoArea> Picked => picker.Map.Selected.OrderBy(a => a.Name).ToList();

        public RegionWindow(IEnumerable<GeoArea> current)
        {
            initial = current;
            Title = "Pick the region to download";
            Width = 1000;
            Height = 700;
            MinWidth = 700;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            picker.AllowLevels(AreaLevel.Country, AreaLevel.State);
            var buttons = UiKit.Row(UiKit.Button("Cancel", () => Close(false)), UiKit.Button("Download", () => Close(true)));
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 6, 0, 0);
            var dock = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(picker);
            Content = dock;
            Opened += async (s, e) =>
            {
                await picker.LoadAtlasAsync();
                var start = (initial ?? Enumerable.Empty<GeoArea>()).ToList();
                if (start.Count > 0)
                {
                    picker.Level = start.Any(a => a.Level == AreaLevel.Country) ? AreaLevel.Country : AreaLevel.State;
                    picker.Map.SetSelected(start);
                    picker.Map.ZoomToAreas(start);
                }
                else picker.Map.ZoomTo(-128, 22, -64, 52);
            };
        }
    }
}
