using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// The Mac version's main window: start page, then the workspace with tabs (talkgroups, repeaters, zones,
    /// settings), menus for files / export / the radio, and a status line. Milestone 1 of the port: the screens
    /// that need the map and wizard come next (see docs/MAC.md).
    /// </summary>
    sealed partial class MainWindow : Window
    {
        readonly Session session = new Session();
        readonly Border host = new Border();
        readonly TextBlock status = new TextBlock { Margin = new Thickness(12, 6), Opacity = 0.8 };
        readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        readonly List<MenuItem> projectItems = new List<MenuItem>();
        Control startPage, workspace;
        DataGrid talkgroupGrid;
        ZonesTab zonesTab;
        TextBox radioIdName, radioId, homeTown;
        TextBlock homeStatus;
        CheckBox politeTransmit, scanLists, favScan, localFm;
        NumericUpDown localMiles;
        ComboBox callerScope;
        TextBox callerAreas;
        CheckBox aprsOn;
        TextBox aprsCall, aprsFreq;
        NumericUpDown aprsSsid;
        bool loading, discardOnClose;
        List<Issue> lastIssues = new List<Issue>();

        static readonly KeyModifiers Cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        public MainWindow(string projectPath)
        {
            Title = Dialogs.AppName;
            Width = 1120;
            Height = 740;
            MinWidth = 760;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            session.Format = AppSettings.LoadFormat();
            session.Changed += (s, e) => { UpdateTitle(); statusTimer.Stop(); statusTimer.Start(); };
            session.Replaced += (s, e) => Rebind();
            statusTimer.Tick += (s, e) => { statusTimer.Stop(); RefreshStatus(); };

            startPage = BuildStartPage();
            workspace = BuildWorkspace();

            var root = new DockPanel();
            var menu = BuildMenu();
            DockPanel.SetDock(menu, Dock.Top);
            root.Children.Add(menu);
            var bar = new Border { Child = status, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Avalonia.Media.Brushes.Gray, Opacity = 1 };
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(host);
            Content = root;

            Closing += OnWindowClosing;
            Opened += async (s, e) => await LoadInitial(projectPath);
            ShowStart();
        }

        // ======================================================================
        // Menu
        // ======================================================================

        MenuItem Item(string header, Func<Task> click, Key key = Key.None, KeyModifiers mods = KeyModifiers.None, bool needsProject = false)
        {
            var item = new MenuItem { Header = header };
            if (key != Key.None) item.InputGesture = new KeyGesture(key, mods);
            item.Click += async (s, e) =>
            {
                try { await click(); }
                catch (Exception ex) { await Dialogs.Error(this, "Something went wrong:\n\n" + ex.Message + "\n\nYour project is still open; save it before trying again."); }
            };
            if (needsProject) projectItems.Add(item);
            return item;
        }

        Menu BuildMenu()
        {
            var file = new MenuItem { Header = "_File" };
            file.Items.Add(Item("_New codeplug (map wizard)...", StartWizard, Key.N, Cmd));
            file.Items.Add(Item("New _empty codeplug", NewEmpty));
            file.Items.Add(Item("_Open...", OpenProject, Key.O, Cmd));
            file.Items.Add(Item("_Save", Save, Key.S, Cmd, true));
            file.Items.Add(Item("Save _as...", SaveAs, Key.S, Cmd | KeyModifiers.Shift, true));
            file.Items.Add(new Separator());
            file.Items.Add(Item("_Import from a CPS export...", ImportFromCps));
            file.Items.Add(Item("Check repeaters for _updates...", CheckForUpdates, needsProject: true));

            var export = new MenuItem { Header = "_Export" };
            export.Items.Add(Item("Export _CSV files for the CPS...", Generate, Key.G, Cmd, true));
            export.Items.Add(Item("How to load them into the CPS", ShowHowTo));

            var radio = new MenuItem { Header = "_Radio" };
            radio.Items.Add(Item("_Read codeplug from radio...", ImportFromRadio, Key.R, Cmd));
            radio.Items.Add(Item("_Write codeplug to radio...", WriteProjectToRadio, Key.None, KeyModifiers.None, true));
            radio.Items.Add(Item("Re_store codeplug from a backup...", RestoreRadioBackup));
            radio.Items.Add(new Separator());
            radio.Items.Add(Item("Radio _port...", ChooseRadioPort));

            var help = new MenuItem { Header = "_Help" };
            help.Items.Add(Item("Check for _problems", async () => { RefreshStatus(); await ShowIssues(); }, Key.None, KeyModifiers.None, true));
            help.Items.Add(Item("_Diagnostics...", ShowDiagnostics));
            help.Items.Add(Item("_About", ShowAbout));

            var menu = new Menu();
            menu.Items.Add(file);
            menu.Items.Add(export);
            menu.Items.Add(radio);
            menu.Items.Add(help);
            UpdateMenus();
            return menu;
        }

        void ShowHome()
        {
            var h = session.Project.Home;
            homeStatus.Text = h == null ? "Not set" : h.Latitude.ToString("0.000", CultureInfo.InvariantCulture) + ", " + h.Longitude.ToString("0.000", CultureInfo.InvariantCulture);
        }

        void UpdateMenus()
        {
            bool open = host.Child == workspace;
            foreach (var i in projectItems) i.IsEnabled = open;
        }

        // ======================================================================
        // Pages
        // ======================================================================

        Control BuildStartPage()
        {
            Button Big(string text, string hint, Func<Task> click)
            {
                var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 12) };
                b.Content = new StackPanel { Children = { new TextBlock { Text = text, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold }, new TextBlock { Text = hint, Opacity = 0.75, TextWrapping = Avalonia.Media.TextWrapping.Wrap } } };
                b.Click += async (s, e) =>
                {
                    try { await click(); }
                    catch (Exception ex) { await Dialogs.Error(this, ex.Message); }
                };
                return b;
            }
            var panel = new StackPanel { Spacing = 10, Width = 520, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = Dialogs.AppName, FontSize = 28, FontWeight = Avalonia.Media.FontWeight.Bold });
            panel.Children.Add(new TextBlock { Text = "Build a codeplug for the BTECH DMR-6X2 PRO.", Opacity = 0.8, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(Big("Set up a new codeplug", "Pick your area on a map. DMR repeaters and their talkgroups come from RadioID.net and BrandMeister, then you choose zones and talkgroups.", StartWizard));
            panel.Children.Add(Big("Read from the radio", "Connect the programming cable, switch the radio on and read what's on it.", ImportFromRadio));
            panel.Children.Add(Big("Import from a CPS export", "Use the folder from the CPS's Tool > Export > Export All.", ImportFromCps));
            panel.Children.Add(Big("Open a project", "A .cpb file you saved before.", OpenProject));
            panel.Children.Add(Big("Start empty", "A blank codeplug to fill in by hand.", NewEmpty));
            return panel;
        }

        static DataGridTextColumn Col(string header, string path, bool readOnly = false, double width = 0)
        {
            var c = new DataGridTextColumn { Header = header, Binding = new Binding(path), IsReadOnly = readOnly };
            if (width > 0) c.Width = new DataGridLength(width);
            return c;
        }

        DataGrid NewGrid()
        {
            return new DataGrid { AutoGenerateColumns = false, CanUserSortColumns = true, CanUserResizeColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Extended };
        }

        Control BuildWorkspace()
        {
            // Talkgroups (editable)
            talkgroupGrid = NewGrid();
            talkgroupGrid.Columns.Add(Col("Name", "Name", false, 260));
            talkgroupGrid.Columns.Add(Col("ID", "Id", false, 110));
            talkgroupGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Call type",
                Width = new DataGridLength(160),
                CellTemplate = new FuncDataTemplate<Talkgroup>((t, ns) =>
                {
                    var box = new ComboBox { ItemsSource = CallTypes.Values, SelectedItem = t?.CallType, MinWidth = 130 };
                    box.SelectionChanged += (s, e) =>
                    {
                        if (loading || t == null || !(box.SelectedItem is string v) || v == t.CallType) return;
                        t.CallType = v;
                        session.NotifyTalkgroupsChanged();
                    };
                    return box;
                }),
            });
            talkgroupGrid.CellEditEnded += OnTalkgroupEdited;
            var add = new Button { Content = "Add talkgroup" };
            add.Click += (s, e) => AddTalkgroup();
            var del = new Button { Content = "Delete selected" };
            del.Click += async (s, e) => await DeleteTalkgroups();
            var tgBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8), Children = { add, del } };
            var tgPage = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(tgBar, Dock.Top);
            tgPage.Children.Add(tgBar);
            tgPage.Children.Add(talkgroupGrid);

            // Repeaters and zones (lists for now)

            // Settings
            radioIdName = new TextBox { Width = 260 };
            radioId = new TextBox { Width = 160 };
            radioIdName.LostFocus += (s, e) => { if (loading) return; session.Project.RadioIdName = (radioIdName.Text ?? "").Trim(); session.MarkDirty(); };
            radioId.LostFocus += (s, e) =>
            {
                if (loading) return;
                if (int.TryParse((radioId.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0) { session.Project.RadioId = id; session.MarkDirty(); }
                else radioId.Text = session.Project.RadioId.ToString(CultureInfo.InvariantCulture);
            };
            var settings = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
            settings.Children.Add(new TextBlock { Text = "Your radio ID (the CPS's Radio ID list)", FontWeight = Avalonia.Media.FontWeight.SemiBold });
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Name", Width = 60, VerticalAlignment = VerticalAlignment.Center }, radioIdName } });
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "DMR ID", Width = 60, VerticalAlignment = VerticalAlignment.Center }, radioId } });
            // Home town (same text as the Windows SettingsPage).
            homeTown = new TextBox { Width = 260, Watermark = "e.g. Brownwood, TX" };
            homeStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            var findHome = new Button { Content = "Find on the map" };
            var clearHome = new Button { Content = "Clear" };
            void SetHome(HomeLocation h)
            {
                session.Project.Home = h;
                homeTown.Text = h?.Place ?? "";
                ShowHome();
                session.MarkDirty();
            }
            void FindHome()
            {
                var h = HomeLocation.Parse(GeoAtlas.BuiltIn(), homeTown.Text, HomeLocation.DefaultCountry(session.Project));
                if (h == null) { homeStatus.Text = "Not found. Try \"Town, State\" or \"Town, Country\"."; return; }
                SetHome(h);
            }
            findHome.Click += (s, e) => FindHome();
            clearHome.Click += (s, e) => SetHome(null);
            homeTown.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; FindHome(); } };
            settings.Children.Add(new TextBlock { Text = "Home", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Home town", Width = 80, VerticalAlignment = VerticalAlignment.Center }, homeTown, findHome, clearHome, homeStatus } });
            var homeHint = UiKit.Hint("Your home town: the Repeaters list shows each repeater's distance and direction from it, Zones > Sort by distance puts the " +
                                      "nearest zones first, and talkgroup zones list the nearest repeaters first. The wizard's callsign lookup fills it from RadioID.net.");
            homeHint.MaxWidth = 720;
            homeHint.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Children.Add(homeHint);
            politeTransmit = new CheckBox { Content = "Polite transmit: on a repeater, key up only when its slot is free" };
            politeTransmit.IsCheckedChanged += (s, e) => { if (loading) return; session.Project.Options.PoliteTransmit = politeTransmit.IsChecked == true; session.MarkDirty(); };
            settings.Children.Add(new TextBlock { Text = "Transmitting", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            settings.Children.Add(politeTransmit);
            // Same text as the Windows SettingsPage.
            var politeHint = UiKit.Hint("DMR repeater channels get TX permit \"Same Color Code\": the radio won't transmit while the repeater's slot carries another call. " +
                                        "The hotspot and DMR simplex channels stay on \"Always\" (a hotspot on a stricter setting can refuse to key) and analog channels on \"Off\". " +
                                        "Turned off, channels keep the CPS default (Always for DMR). Codeplugs imported from the CPS or saved before version 1.4 start with it off, " +
                                        "so their channels don't change.");
            politeHint.MaxWidth = 720;
            politeHint.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Children.Add(politeHint);
            // Scan lists (same text as the Windows SettingsPage).
            scanLists = new CheckBox { Content = "Make a scan list for each zone (up to 50 channels each)" };
            favScan = new CheckBox { Content = "Favorites zones: their scan list keeps checking your home channel (hotspot, else the nearest repeater)" };
            localFm = new CheckBox { Content = "Local FM scan list: analog repeaters near home, nearest first" };
            localMiles = new NumericUpDown { Minimum = 5, Maximum = 500, Increment = 5, Width = 130, FormatString = "0" };
            scanLists.IsCheckedChanged += (s, e) => { favScan.IsEnabled = localFm.IsEnabled = localMiles.IsEnabled = scanLists.IsChecked == true; if (loading) return; session.Project.Options.ScanListPerZone = scanLists.IsChecked == true; session.MarkDirty(); };
            favScan.IsCheckedChanged += (s, e) => { if (loading) return; session.Project.Options.FavoritesScanPriority = favScan.IsChecked == true; session.MarkDirty(); };
            localFm.IsCheckedChanged += (s, e) => { if (loading) return; session.Project.Options.LocalAnalogScanList = localFm.IsChecked == true; session.MarkDirty(); };
            localMiles.ValueChanged += (s, e) => { if (loading) return; session.Project.Options.LocalAnalogMiles = (int)(localMiles.Value ?? GenerationOptions.DefaultLocalAnalogMiles); session.MarkDirty(); };
            settings.Children.Add(new TextBlock { Text = "Scan lists", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            settings.Children.Add(scanLists);
            settings.Children.Add(favScan);
            settings.Children.Add(localFm);
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Local FM: within", VerticalAlignment = VerticalAlignment.Center }, localMiles, new TextBlock { Text = "miles of your home town", VerticalAlignment = VerticalAlignment.Center } } });
            var scanHint = UiKit.Hint("A channel can scan with several lists: its own zone's first, then Favorites and Local FM (Scan List 2, 3 in the CPS). " +
                                      "The priority channel is checked every few seconds while scanning, so you don't miss your home repeater or hotspot.");
            scanHint.MaxWidth = 720;
            scanHint.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Children.Add(scanHint);
            // Caller names (same choices and text as the Windows SettingsPage).
            var callerValues = new[] { CallerScopes.Off, CallerScopes.World, CallerScopes.Countries, CallerScopes.UsStates };
            callerScope = new ComboBox { ItemsSource = new[] { "Off (keep the CPS's list)", "Whole world", "Countries", "US states" }, Width = 240 };
            callerAreas = new TextBox { Width = 420, Watermark = "e.g. Texas, Oklahoma  or  United States, Canada" };
            callerScope.SelectionChanged += (s, e) =>
            {
                callerAreas.IsEnabled = callerScope.SelectedIndex >= 2;
                if (loading) return;
                string v = callerValues[Math.Max(0, callerScope.SelectedIndex)];
                session.Project.Options.CallerScope = v.Length == 0 ? null : v;
                session.MarkDirty();
            };
            callerAreas.LostFocus += (s, e) =>
            {
                if (loading) return;
                var areas = (callerAreas.Text ?? "").Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                session.Project.Options.CallerAreas = areas.Count == 0 ? null : areas;
                session.MarkDirty();
            };
            settings.Children.Add(new TextBlock { Text = "Caller names", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Caller list", Width = 140, VerticalAlignment = VerticalAlignment.Center }, callerScope } });
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Countries or states", Width = 140, VerticalAlignment = VerticalAlignment.Center }, callerAreas } });
            var callerHint = UiKit.Hint("Export adds DigitalContactList.CSV with DMR users from RadioID.net (downloaded once a week, about 17 MB), so the radio shows a caller's " +
                                        "callsign and name instead of a number. Importing it in the CPS replaces the caller list there. The whole world fits the 6X2 PRO (about 315,000 " +
                                        "users; it holds 500,000). Write to radio doesn't send caller names yet: import the file in the CPS and write from there.");
            callerHint.MaxWidth = 720;
            callerHint.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Children.Add(callerHint);
            // APRS (same text as the Windows SettingsPage).
            aprsOn = new CheckBox { Content = "Write APRS.CSV with my APRS callsign, SSID and frequency" };
            aprsCall = new TextBox { Width = 120, MaxLength = 6 };
            aprsSsid = new NumericUpDown { Minimum = 0, Maximum = 15, Increment = 1, FormatString = "0", Width = 110 };
            aprsFreq = new TextBox { Width = 120 };
            void AprsEdited()
            {
                if (loading || aprsOn.IsChecked != true) return;
                session.Project.Aprs = ReadAprs();
                session.MarkDirty();
            }
            aprsOn.IsCheckedChanged += (s, e) =>
            {
                aprsCall.IsEnabled = aprsSsid.IsEnabled = aprsFreq.IsEnabled = aprsOn.IsChecked == true;
                if (loading) return;
                session.Project.Aprs = aprsOn.IsChecked == true ? ReadAprs() : null;
                session.MarkDirty();
            };
            aprsCall.LostFocus += (s, e) => AprsEdited();
            aprsFreq.LostFocus += (s, e) => AprsEdited();
            aprsSsid.ValueChanged += (s, e) => AprsEdited();
            settings.Children.Add(new TextBlock { Text = "APRS", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            settings.Children.Add(aprsOn);
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                new TextBlock { Text = "Callsign", VerticalAlignment = VerticalAlignment.Center }, aprsCall,
                new TextBlock { Text = "SSID", VerticalAlignment = VerticalAlignment.Center }, aprsSsid,
                new TextBlock { Text = "Frequency MHz", VerticalAlignment = VerticalAlignment.Center }, aprsFreq } });
            var aprsHint = UiKit.Hint("Export adds APRS.CSV: your callsign and SSID (7 = handheld, 9 = mobile), the APRS frequency, the person symbol, path WIDE1-1,WIDE2-1, " +
                                      "fixed position off (the radio's GPS is used) and, in the US, BrandMeister's APRS gateway 310999 as a private call for digital reports. " +
                                      "Importing it in the CPS replaces all of its APRS settings, and the CPS also resets some the file doesn't hold (transmit delay, display " +
                                      "time, analog bandwidth, receive filters): check the CPS's APRS screen after importing. Beacons stay as they are (manual).");
            aprsHint.MaxWidth = 720;
            aprsHint.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Children.Add(aprsHint);

            var tabs = new TabControl();
            zonesTab = new ZonesTab(session, this);
            tabs.Items.Add(new TabItem { Header = "Repeaters", Content = new RepeatersTab(session, this) });
            tabs.Items.Add(new TabItem { Header = "Hotspot", Content = new HotspotTab(session) });
            tabs.Items.Add(new TabItem { Header = "Talkgroups", Content = tgPage });
            tabs.Items.Add(new TabItem { Header = "Zones", Content = zonesTab });
            tabs.Items.Add(new TabItem { Header = "Settings", Content = new ScrollViewer { Content = settings } });
            // Counts on the Zones tab follow edits made on the other tabs.
            tabs.SelectionChanged += (s, e) => { if (e.Source == tabs && tabs.SelectedItem is TabItem t && t.Content == zonesTab) zonesTab.Refresh(); };
            return tabs;
        }

        void ShowStart() { DropWizard(); host.Child = startPage; UpdateMenus(); UpdateTitle(); status.Text = ""; }

        void ShowWorkspace() { DropWizard(); host.Child = workspace; UpdateMenus(); UpdateTitle(); RefreshStatus(); }

        // ---------------- New codeplug wizard ----------------

        NewCodeplugWizardView wizard;
        bool wizardFromWorkspace;

        void DropWizard()
        {
            if (wizard == null) return;
            wizard.CancelDownload();
            wizard = null;
        }

        async Task StartWizard()
        {
            if (wizard != null) return;
            if (InWorkspace && !await ConfirmDiscard()) return;
            wizardFromWorkspace = InWorkspace;
            wizard = new NewCodeplugWizardView(this);
            var w = wizard;
            w.Finished += (s, project) => FinishWizard(w, project);
            w.Cancelled += async (s, e) =>
            {
                if (w.HasProgress && !await Dialogs.Ask(this, "Leave the new codeplug setup? Nothing from it is saved.", "Leave")) return;
                if (wizardFromWorkspace) ShowWorkspace(); else ShowStart();
            };
            host.Child = w;
            status.Text = "";
            UpdateMenus();
            UpdateTitle();
        }

        async void FinishWizard(NewCodeplugWizardView w, Project p)
        {
            var notes = w.Data.Notes;
            session.Replace(p, null, true);
            ShowWorkspace();
            if (workspace is TabControl tc) tc.SelectedIndex = 0;
            int dmr = p.Repeaters.Count(r => r.IsDigital), fm = p.Repeaters.Count(r => !r.IsDigital);
            int zones = p.UsedZoneNames().Count;
            string head = "Your new codeplug has " + Plural(dmr, "DMR repeater") + (fm > 0 ? ", " + Plural(fm, "analog channel") : "") +
                          (p.HotspotEnabled ? ", your hotspot" : "") + " and " + p.ChannelCount() + " channels in " + Plural(zones, "zone") + ".\n\n" +
                          "Check it on the Repeaters and Zones tabs, save it (File > Save), then use Radio > Write codeplug to radio or Export > Export CSV files for the CPS.";
            await Dialogs.List(this, head, new string[0], notes, false);
        }

        bool InWorkspace => host.Child == workspace;

        /// <summary>Show the open project in every page.</summary>
        void Rebind()
        {
            loading = true;
            var p = session.Project;
            lastGoodId.Clear();
            foreach (var t in p.Talkgroups) lastGoodId[t] = t.Id;
            talkgroupGrid.ItemsSource = p.Talkgroups.ToList();
            radioIdName.Text = p.RadioIdName;
            homeTown.Text = p.Home?.Place ?? "";
            ShowHome();
            radioId.Text = p.RadioId.ToString(CultureInfo.InvariantCulture);
            politeTransmit.IsChecked = p.Options.PoliteTransmit;
            scanLists.IsChecked = p.Options.ScanListPerZone;
            favScan.IsChecked = p.Options.FavoritesScanPriority;
            localFm.IsChecked = p.Options.LocalAnalogScanList;
            localMiles.Value = Math.Max(5, Math.Min(500, p.Options.LocalAnalogMiles > 0 ? p.Options.LocalAnalogMiles : GenerationOptions.DefaultLocalAnalogMiles));
            favScan.IsEnabled = localFm.IsEnabled = localMiles.IsEnabled = p.Options.ScanListPerZone;
            callerScope.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { CallerScopes.Off, CallerScopes.World, CallerScopes.Countries, CallerScopes.UsStates }, p.Options.CallerScope ?? ""));
            callerAreas.Text = string.Join(", ", p.Options.CallerAreas ?? new List<string>());
            callerAreas.IsEnabled = callerScope.SelectedIndex >= 2;
            aprsOn.IsChecked = p.Aprs != null;
            var aprs = p.Aprs ?? Aprs.Suggest(p);
            aprsCall.Text = aprs.Callsign;
            aprsSsid.Value = Math.Max(0, Math.Min(15, aprs.Ssid));
            aprsFreq.Text = aprs.FrequencyMHz > 0 ? aprs.FrequencyMHz.ToString("0.000", CultureInfo.InvariantCulture) : "";
            aprsCall.IsEnabled = aprsSsid.IsEnabled = aprsFreq.IsEnabled = p.Aprs != null;
            loading = false;
        }

        /// <summary>The APRS settings as typed: the suggestion (symbol, path, gateway) with the user's callsign, SSID and frequency.</summary>
        AprsPlan ReadAprs()
        {
            var a = session.Project.Aprs ?? Aprs.Suggest(session.Project);
            a.Callsign = (aprsCall.Text ?? "").Trim().ToUpperInvariant();
            a.Ssid = (int)(aprsSsid.Value ?? 7);
            if (decimal.TryParse((aprsFreq.Text ?? "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal f) && f > 0) a.FrequencyMHz = f;
            return a;
        }

        void UpdateTitle()
        {
            Title = InWorkspace ? session.DisplayName + (session.Dirty ? " *" : "") + " - " + Dialogs.AppName : Dialogs.AppName;
        }

        void RefreshStatus()
        {
            if (!InWorkspace) return;
            var p = session.Project;
            try
            {
                lastIssues = Validator.Validate(p, session.Format);
                int errors = lastIssues.Count(i => i.Severity == Severity.Error), warnings = lastIssues.Count(i => i.Severity == Severity.Warning);
                status.Text = p.ChannelCount() + " channels in " + p.Zones.Count + " zones, " + Plural(p.Talkgroups.Count, "talkgroup") + ". " +
                              (errors > 0 ? Plural(errors, "problem") + " to fix before exporting" : warnings > 0 ? Plural(warnings, "thing") + " to check" : "No problems found") + ".";
            }
            catch (Exception ex) { status.Text = "Couldn't check the project: " + ex.Message; }
        }

        async Task ShowIssues()
        {
            var errors = lastIssues.Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
            var warnings = lastIssues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message).ToList();
            await Dialogs.List(this, errors.Count + warnings.Count == 0 ? "No problems found." : "What to check:", errors, warnings, false);
        }

        // ======================================================================
        // Talkgroups
        // ======================================================================

        void OnTalkgroupEdited(object sender, DataGridCellEditEndedEventArgs e)
        {
            if (loading || e.EditAction != DataGridEditAction.Commit || !(e.Row.DataContext is Talkgroup t)) return;
            // The grid already wrote the new value; follow an ID change through the repeaters and zones.
            if (e.Column.Header as string == "ID")
            {
                if (t.Id <= 0 || t.Id > Validator.MaxTalkgroupId || session.Project.Talkgroups.Any(x => x != t && x.Id == t.Id))
                {
                    _ = Dialogs.Error(this, t.Id <= 0 || t.Id > Validator.MaxTalkgroupId ? "Type a number, e.g. 91 or 3100." : "ID " + t.Id + " is already used by " + session.Project.Talkgroups.First(x => x != t && x.Id == t.Id).Name + ".");
                    t.Id = lastGoodId.TryGetValue(t, out int old) ? old : 0;
                    talkgroupGrid.ItemsSource = session.Project.Talkgroups.ToList();
                    return;
                }
                if (lastGoodId.TryGetValue(t, out int was)) session.Project.ChangeTalkgroupId(was, t.Id);
                lastGoodId[t] = t.Id;
            }
            else if (e.Column.Header as string == "Name") t.Name = Naming.Fit((t.Name ?? "").Trim(), 16);
            session.NotifyTalkgroupsChanged();
        }

        readonly Dictionary<Talkgroup, int> lastGoodId = new Dictionary<Talkgroup, int>();

        void AddTalkgroup()
        {
            var t = new Talkgroup("New talkgroup", 0);
            session.Project.Talkgroups.Add(t);
            talkgroupGrid.ItemsSource = session.Project.Talkgroups.ToList();
            talkgroupGrid.SelectedItem = t;
            talkgroupGrid.ScrollIntoView(t, talkgroupGrid.Columns[0]);
            session.NotifyTalkgroupsChanged();
        }

        async Task DeleteTalkgroups()
        {
            var picked = talkgroupGrid.SelectedItems.Cast<Talkgroup>().ToList();
            if (picked.Count == 0) return;
            int uses = picked.Sum(t => session.Project.CountTalkgroupUse(t.Id));
            if (!await Dialogs.Ask(this, "Delete " + Plural(picked.Count, "talkgroup") + (uses > 0 ? " and its " + Plural(uses, "channel") + " on the repeaters" : "") + "?", "Delete"))
                return;
            session.Project.DeleteTalkgroups(picked);
            talkgroupGrid.ItemsSource = session.Project.Talkgroups.ToList();
            session.NotifyTalkgroupsChanged();
        }

        // ======================================================================
        // Files
        // ======================================================================

        static readonly FilePickerFileType ProjectType = new FilePickerFileType("W6OZZ CPS project") { Patterns = new[] { "*.cpb" } };

        async Task LoadInitial(string path)
        {
            if (path == null) { path = AppSettings.Get("LastProject"); if (path != null && !File.Exists(path)) path = null; }
            if (path == null) return;
            try
            {
                session.Replace(ProjectStore.Load(path), path, false);
                AppSettings.Set("LastProject", path);
                ShowWorkspace();
            }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't open " + Path.GetFileName(path) + ":\n\n" + ex.Message); }
        }

        async Task<bool> ConfirmDiscard()
        {
            if (!InWorkspace || !session.Dirty) return true;
            return await Dialogs.Ask(this, "Throw away the changes to \"" + session.DisplayName + "\"? Save first with File > Save if you want to keep them.", "Discard changes");
        }

        async Task NewEmpty()
        {
            if (!await ConfirmDiscard()) return;
            session.Replace(new Project(), null, false);
            ShowWorkspace();
        }

        async Task OpenProject()
        {
            if (!await ConfirmDiscard()) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open project", AllowMultiple = false, FileTypeFilter = new[] { ProjectType } });
            if (files.Count == 0) return;
            string path = files[0].Path.LocalPath;
            Project p;
            try { p = ProjectStore.Load(path); }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't open that file:\n\n" + ex.Message); return; }
            session.Replace(p, path, false);
            AppSettings.Set("LastProject", path);
            ShowWorkspace();
        }

        async Task Save()
        {
            if (session.FilePath == null) { await SaveAs(); return; }
            session.Save(session.FilePath);
        }

        async Task SaveAs()
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save project",
                SuggestedFileName = (session.DisplayName == "Untitled" ? "My codeplug" : session.DisplayName) + ".cpb",
                DefaultExtension = "cpb",
                FileTypeChoices = new[] { ProjectType },
            });
            if (file == null) return;
            string path = file.Path.LocalPath;
            if (!path.EndsWith(".cpb", StringComparison.OrdinalIgnoreCase)) path += ".cpb";
            session.Save(path);
            AppSettings.Set("LastProject", path);
        }

        async Task<string> PickFolder(string title)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count == 0 ? null : folders[0].Path.LocalPath;
        }

        /// <summary>File > Check repeaters for updates (same as Windows): download, show the differences with ticks, apply the ticked ones.</summary>
        async Task CheckForUpdates()
        {
            var p = session.Project;
            int tracked = UpdateCheck.Tracked(p).Count;
            if (tracked == 0)
            {
                await Dialogs.Info(this, "None of this project's repeaters came from RadioID.net (Add from map or the wizard), so there is nothing to check. " +
                                         "Repeaters typed in by hand or imported from the CPS have no listing to compare with.");
                return;
            }
            Online.UpdateReport report;
            try { report = await Dialogs.Progress(this, "Check for updates", pr => Online.CheckForUpdates(p, pr, System.Threading.CancellationToken.None),
                                                  "Asking RadioID.net and BrandMeister about your " + tracked + " repeater(s)..."); }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't check for updates:\n\n" + ex.Message); return; }
            string last = p.LastUpdateCheck;
            p.LastUpdateCheck = RepeaterHealth.DateText(DateTime.Now);
            session.MarkDirty();
            var picked = await UpdatesWindow.Run(this, report, p, last);
            if (picked == null) return;
            var notes = UpdateCheck.Apply(p, picked.Value.Key, report.BrandMeisterNames);
            if (picked.Value.Value)
            {
                try { await Dialogs.Progress(this, "Caller names", pr => Online.CallerDatabaseFileAsync(true).Result, "Downloading RadioID.net's user list (about 17 MB)..."); notes.Add("Caller list downloaded again."); }
                catch (Exception ex) { notes.Add("Couldn't download the caller list: " + ex.Message); }
            }
            session.Replace(p, session.FilePath, true); // every tab shows the changes
            if (notes.Count > 0) await Dialogs.List(this, "Updated:", new string[0], notes, false);
        }

        async Task ImportFromCps()
        {
            if (!await ConfirmDiscard()) return;
            string folder = await PickFolder("Pick the folder from the CPS's Tool > Export > Export All");
            if (folder == null) return;
            await ImportFolder(folder, "export", false);
        }

        /// <summary>Makes a project from a folder of CPS CSVs: an Export All, or a read from the radio.</summary>
        async Task ImportFolder(string folder, string what, bool fromRadio)
        {
            ImportResult result;
            try { result = CpsImporter.Import(folder); }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't import that " + what + ":\n\n" + ex.Message); return; }
            var notes = new List<string>(result.Notes);
            if (fromRadio) notes.Insert(0, "The radio's memory and these CSV files were saved in " + folder + " (a backup of what was on the radio).");
            if (fromRadio) try { notes.AddRange(Aprs.ReadNotes(MemoryImage.Load(Path.Combine(folder, "radio.img")))); } catch { }
            if (!fromRadio)
                try
                {
                    var f = CpsFormat.FromFolder(folder);
                    if (!SameLayout(f, session.Format))
                    {
                        if (Directory.Exists(AppSettings.FormatFolder)) Directory.Delete(AppSettings.FormatFolder, true);
                        f.SaveTemplates(AppSettings.FormatFolder);
                        AppSettings.Set("FormatSource", folder);
                        session.Format = AppSettings.LoadFormat();
                        notes.Add("This export's CSV layout differs from the built-in one, so generated files now follow it.");
                    }
                }
                catch { }
            session.Replace(result.Project, null, true);
            ShowWorkspace();
            var p = result.Project;
            string head = "Imported " + Plural(p.Talkgroups.Count, "talkgroup") + ", " + Plural(p.Repeaters.Count(r => r.IsDigital), "DMR repeater") + ", " +
                          Plural(p.Repeaters.Count(r => !r.IsDigital), "analog channel") + (p.HotspotEnabled ? ", your hotspot" : "") +
                          " and " + Plural(p.Zones.Count, "zone") + ". Save the project (File > Save) to keep it.";
            await Dialogs.List(this, head, new string[0], notes, false);
        }

        static bool SameLayout(CpsFormat a, CpsFormat b)
        {
            return CpsFormat.Files.All(f => string.Join(",", a.Table(f).Header) == string.Join(",", b.Table(f).Header));
        }

        // ======================================================================
        // Export (the CPS's CSV files)
        // ======================================================================

        async Task Generate()
        {
            var p = session.Project;
            p.SyncZones();
            var issues = Validator.Validate(p, session.Format);
            var errors = issues.Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
            if (errors.Count > 0)
            {
                await Dialogs.List(this, "Fix these before exporting:", errors, issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message), false);
                return;
            }
            CpsExport mergeBase = null;
            if (p.Options.KeepCpsChannels)
            {
                try { mergeBase = CpsExport.Load(p.Options.BaseExportFolder); }
                catch (Exception ex)
                {
                    await Dialogs.Error(this, "\"Keep channels made in the CPS\" is on, but the CPS export couldn't be read:\n\n" + ex.Message +
                                              "\n\nIn the CPS, run Tool > Export > Export All again.");
                    return;
                }
            }
            GeneratedCodeplug g;
            try { g = CodeplugGenerator.Generate(p, session.Format, mergeBase); }
            catch (Exception ex) { await Dialogs.Error(this, ex.Message); return; }
            if (g.ChannelList.Count + g.KeptChannels.Count > p.Options.MaxChannels)
            {
                await Dialogs.Error(this, "This codeplug has " + (g.ChannelList.Count + g.KeptChannels.Count) + " channels; the radio holds " + p.Options.MaxChannels + ".");
                return;
            }
            var warnings = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message).Concat(g.Notes).Distinct().ToList();
            if (!string.IsNullOrEmpty(p.Options.CallerScope))
            {
                string note;
                try { note = await Dialogs.Progress(this, "Caller names", pr => Online.AttachCallers(g, p), "Getting RadioID.net's user list (about 17 MB, kept a week)..."); }
                catch (Exception ex) { note = "Caller names weren't added: " + ex.Message; }
                if (note != null) warnings.Add(note);
            }
            string summary = (g.ChannelList.Count + g.KeptChannels.Count) + " channels in " + g.ZoneList.Count + " zones";
            if (warnings.Count > 0 && !await Dialogs.List(this, "Ready to export " + summary + ". A few things to check first:", new string[0], warnings, true)) return;

            string folder = await PickFolder("Pick the folder for the CSV files and the .LST file list");
            if (folder == null) return;
            var existing = g.Files().Select(f => f.Key).Where(f => CpsFormat.FindFile(folder, f) != null).ToList();
            if (existing.Count > 0 && !await Dialogs.Ask(this, "This folder already has " + string.Join(", ", existing) + ".\n\nReplace them with the new files?", "Replace")) return;
            string lstName = Naming.Clean(session.DisplayName, 40) + ".LST";
            try { g.WriteTo(folder, lstName); }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't write the files:\n\n" + ex.Message); return; }
            if (p.Options.OutputFolder != folder) { p.Options.OutputFolder = folder; session.MarkDirty(); }
            int numbered = CodeplugGenerator.KeepChannelNumbers(g);
            bool remembered = CodeplugGenerator.RememberOutput(p, g);
            if (numbered > 0 || remembered) session.MarkDirty();
            await Dialogs.Info(this, "Saved " + summary + " to:\n" + folder + "\n\n" + string.Join(", ", g.Files().Select(f => f.Key)) + " and " + lstName + "\n\n" +
                                     "To load them: open your codeplug in the CPS, choose Tool > Import > Import From File List, pick " + lstName + ", click Import, then write to the radio." +
                                     (numbered > 0 ? "\n\n" + Plural(numbered, "channel") + " got a channel number that will now stay the same; save the project (File > Save) to keep it." : ""),
                "Codeplug saved");
        }

        Task ShowHowTo()
        {
            return Dialogs.Info(this,
                "1. Click Export > Export CSV files for the CPS and pick a folder. The CSV files and a .LST file list are saved there.\n\n" +
                "2. In the DMR-6X2 PRO CPS, open your current codeplug (or read it from the radio).\n\n" +
                "3. Tool > Import > Import From File List, pick the .LST, then click Import.\n" +
                "    This replaces the channels, zones, talk groups, receive group lists and radio ID list in the CPS, and the scan lists " +
                "when scan lists are on. Other settings stay as they are.\n\n" +
                "4. Check a few channels, save the codeplug, and write it to the radio.",
                "Loading the files into the CPS");
        }

        Task ShowAbout()
        {
            return Dialogs.Info(this, Dialogs.AppName + " (Mac version, in progress)\nBuilds CSV codeplugs for the BTECH DMR-6X2 PRO and talks to the radio directly.\n\n" +
                                      "DMR repeaters and IDs: RadioID.net; talkgroup names: BrandMeister.\n" +
                                      "Analog repeaters: from RepeaterBook CHIRP exports you import yourself. " + RepeaterBookApi.Attribution + " (" + RepeaterBookApi.SiteUrl + ")\n\n" +
                                      "Map data: US Census, Natural Earth (public domain), GeoNames (CC BY 4.0).", "About");
        }

        // ======================================================================
        // Closing
        // ======================================================================

        async void OnWindowClosing(object sender, WindowClosingEventArgs e)
        {
            if (discardOnClose) return;
            if (wizard != null)
            {
                if (!wizard.HasProgress) { DropWizard(); return; }
                e.Cancel = true;
                if (await Dialogs.Ask(this, "Leave the new codeplug setup? Nothing from it is saved.", "Leave")) { DropWizard(); discardOnClose = true; Close(); }
                return;
            }
            if (!InWorkspace || !session.Dirty) return;
            e.Cancel = true;
            if (await ConfirmDiscard()) { discardOnClose = true; Close(); }
        }

        static string Plural(int n, string word) { return n + " " + word + (n == 1 ? "" : "s"); }

        /// <summary>Developer aid (--snapshot file.png [--tab n]): once the window is up, saves a picture of it and quits.</summary>
        public void SnapshotThenExit(string png, int tab)
        {
            Opened += async (s, e) =>
            {
                await Task.Delay(1500); // the project loads and the grids fill
                if (InWorkspace && workspace is TabControl tabs && tab < tabs.ItemCount) tabs.SelectedIndex = tab;
                await Task.Delay(500);
                var size = new Avalonia.PixelSize((int)Width, (int)Height);
                using (var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size))
                {
                    bmp.Render(this);
                    bmp.Save(png);
                }
                discardOnClose = true;
                Close();
            };
        }
    }
}
