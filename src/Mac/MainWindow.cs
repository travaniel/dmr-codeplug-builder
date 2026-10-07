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
        TextBox radioIdName, radioId;
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
            file.Items.Add(Item("_New empty codeplug", NewEmpty, Key.N, Cmd));
            file.Items.Add(Item("_Open...", OpenProject, Key.O, Cmd));
            file.Items.Add(Item("_Save", Save, Key.S, Cmd, true));
            file.Items.Add(Item("Save _as...", SaveAs, Key.S, Cmd | KeyModifiers.Shift, true));
            file.Items.Add(new Separator());
            file.Items.Add(Item("_Import from a CPS export...", ImportFromCps));

            var export = new MenuItem { Header = "_Export" };
            export.Items.Add(Item("Export _CSV files for the CPS...", Generate, Key.G, Cmd, true));
            export.Items.Add(Item("How to load them into the CPS", ShowHowTo));

            var radio = new MenuItem { Header = "_Radio" };
            radio.Items.Add(Item("_Read codeplug from radio...", ImportFromRadio, Key.R, Cmd));
            radio.Items.Add(Item("_Write codeplug to radio...", WriteProjectToRadio, Key.None, KeyModifiers.None, true));
            radio.Items.Add(Item("Re_store codeplug from a backup...", RestoreRadioBackup));

            var help = new MenuItem { Header = "_Help" };
            help.Items.Add(Item("Check for _problems", async () => { RefreshStatus(); await ShowIssues(); }, Key.None, KeyModifiers.None, true));
            help.Items.Add(Item("_About", ShowAbout));

            var menu = new Menu();
            menu.Items.Add(file);
            menu.Items.Add(export);
            menu.Items.Add(radio);
            menu.Items.Add(help);
            UpdateMenus();
            return menu;
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
            panel.Children.Add(Big("Read from the radio", "Connect the programming cable, switch the radio on and read what's on it.", ImportFromRadio));
            panel.Children.Add(Big("Import from a CPS export", "Use the folder from the CPS's Tool > Export > Export All.", ImportFromCps));
            panel.Children.Add(Big("Open a project", "A .cpb file you saved before.", OpenProject));
            panel.Children.Add(Big("Start empty", "A blank codeplug to fill in by hand.", NewEmpty));
            panel.Children.Add(new TextBlock { Text = "The map-based new-codeplug wizard is not in the Mac version yet.", Opacity = 0.6, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
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

            var tabs = new TabControl();
            zonesTab = new ZonesTab(session, this);
            tabs.Items.Add(new TabItem { Header = "Repeaters", Content = new RepeatersTab(session, this) });
            tabs.Items.Add(new TabItem { Header = "Hotspot", Content = new HotspotTab(session) });
            tabs.Items.Add(new TabItem { Header = "Talkgroups", Content = tgPage });
            tabs.Items.Add(new TabItem { Header = "Zones", Content = zonesTab });
            tabs.Items.Add(new TabItem { Header = "Settings", Content = settings });
            // Counts on the Zones tab follow edits made on the other tabs.
            tabs.SelectionChanged += (s, e) => { if (e.Source == tabs && tabs.SelectedItem is TabItem t && t.Content == zonesTab) zonesTab.Refresh(); };
            return tabs;
        }

        void ShowStart() { host.Child = startPage; UpdateMenus(); UpdateTitle(); status.Text = ""; }

        void ShowWorkspace() { host.Child = workspace; UpdateMenus(); UpdateTitle(); RefreshStatus(); }

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
            radioId.Text = p.RadioId.ToString(CultureInfo.InvariantCulture);
            loading = false;
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
                "    This replaces the channels, zones, talk groups and receive group lists in the CPS. Other settings stay as they are.\n\n" +
                "4. Check a few channels, save the codeplug, and write it to the radio.",
                "Loading the files into the CPS");
        }

        Task ShowAbout()
        {
            return Dialogs.Info(this, Dialogs.AppName + " (Mac version, in progress)\nBuilds CSV codeplugs for the BTECH DMR-6X2 PRO and talks to the radio directly.\n\n" +
                                      "Map data: US Census, Natural Earth (public domain), GeoNames (CC BY 4.0).", "About");
        }

        // ======================================================================
        // Closing
        // ======================================================================

        async void OnWindowClosing(object sender, WindowClosingEventArgs e)
        {
            if (discardOnClose || !InWorkspace || !session.Dirty) return;
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
