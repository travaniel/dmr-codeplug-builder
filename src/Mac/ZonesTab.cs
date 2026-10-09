using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Zone order, renaming, the A/B channels each zone opens on, the channels in it and its talkgroup set; Favorites and
    /// Talkgroup zones (a channel in several zones). Same as the Windows ZonesPage.
    /// </summary>
    sealed class ZonesTab : UserControl
    {
        readonly Session session;
        readonly Window owner;
        readonly DataGrid list, members;
        readonly ComboBox cboA, cboB;
        readonly TextBlock lblZone;
        readonly Control detail;
        readonly ZoneTalkgroupsView editor;
        readonly TabControl tabs;
        readonly TabItem tgTab, chTab, ruleTab;
        readonly StackPanel ruleTalkgroups, ruleZones;
        readonly NumericUpDown ruleMiles;
        readonly Button btnDelete;
        bool loading, editing, bmLoaded;
        GeneratedCodeplug preview;
        List<ZoneRow> zoneRows = new List<ZoneRow>();

        sealed class ZoneRow : RowBase
        {
            public ZoneInfo Zone;
            public string Name => Zone.Name;
            public string Kind { get; set; }
            public string Repeaters { get; set; }
            public string Channels { get; set; }
        }

        sealed class MemberRow
        {
            public GeneratedChannel C;
            public string No { get; set; }
            public string Channel { get; set; }
            public string Talkgroup { get; set; }
            public string Slot { get; set; }
            public string Rx { get; set; }
            public string Why { get; set; }
        }

        public ZonesTab(Session session, Window owner)
        {
            this.session = session;
            this.owner = owner;

            list = UiKit.Grid(false);
            list.Columns.Add(UiKit.Col("Zone", "Name", true, 140));
            list.Columns.Add(UiKit.Col("Kind", "Kind", true, 80));
            list.Columns.Add(UiKit.Col("Repeaters", "Repeaters", true, 70));
            list.Columns.Add(UiKit.Col("Channels", "Channels", true, 70));
            btnDelete = UiKit.Button("Delete", async () => await DeleteZone());
            var leftButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var b in new[]
            {
                UiKit.Button("Up", () => MoveItem(-1)), UiKit.Button("Down", () => MoveItem(1)), UiKit.Button("Rename...", async () => await Rename()),
                UiKit.Button("New favorites zone...", async () => await NewZone(ZoneKinds.Favorites)),
                UiKit.Button("New talkgroup zone...", async () => await NewZone(ZoneKinds.Talkgroup)), btnDelete,
                UiKit.Button("Sort by distance", async () => await SortZones()),
            })
            { b.Margin = new Thickness(0, 0, 6, 6); leftButtons.Children.Add(b); }
            var left = new DockPanel { Width = 400, Margin = new Thickness(10, 8, 6, 8) };
            var heading = UiKit.Heading("Zones");
            var hint = UiKit.Hint("Zones are built from each repeater's Zone field, in the order of the Repeaters list. Set the order they appear on the radio, rename them, and pick talkgroups for a whole zone at once. A channel can also be in more zones: a Favorites zone holds channels you pick, a Talkgroup zone every channel of some talkgroups.");
            DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(hint, Dock.Top); DockPanel.SetDock(leftButtons, Dock.Bottom);
            hint.Margin = new Thickness(0, 0, 0, 8);
            left.Children.Add(heading); left.Children.Add(hint); left.Children.Add(leftButtons); left.Children.Add(list);

            lblZone = UiKit.Heading("");
            cboA = new ComboBox { MinWidth = 240 };
            cboB = new ComboBox { MinWidth = 240 };
            members = UiKit.Grid(false);
            members.IsReadOnly = true;
            members.Columns.Add(UiKit.Col("No.", "No", true, 50));
            members.Columns.Add(UiKit.Col("Channel", "Channel", true, 160));
            members.Columns.Add(UiKit.Col("Talkgroup", "Talkgroup", true, 150));
            members.Columns.Add(UiKit.Col("Slot", "Slot", true, 50));
            members.Columns.Add(UiKit.Col("RX MHz", "Rx", true, 80));
            members.Columns.Add(UiKit.Col("In this zone because", "Why", true, 200));
            var memberButtons = UiKit.Row(UiKit.Button("Add channels...", async () => await AddChannels()), UiKit.Button("Remove from zone", async () => await RemoveMember()),
                                          UiKit.Button("Move up", () => MoveMember(-1)), UiKit.Button("Move down", () => MoveMember(1)));
            memberButtons.Margin = new Thickness(0, 6, 0, 0);
            var chPanel = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(memberButtons, Dock.Bottom);
            chPanel.Children.Add(memberButtons); chPanel.Children.Add(members);
            editor = new ZoneTalkgroupsView(owner);

            ruleTalkgroups = new StackPanel { Spacing = 2 };
            ruleZones = new StackPanel { Spacing = 2 };
            var rule = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 12, RowSpacing = 6, Margin = new Thickness(4) };
            var ruleHint = UiKit.Hint("Every channel that carries one of the ticked talkgroups goes into this zone, nearest repeaters first when a home town is set (Settings), else in the order of the Repeaters list. Tick zones on the right to take only those zones' repeaters.");
            Grid.SetColumnSpan(ruleHint, 2);
            var l1 = UiKit.Label("Talkgroups", true); Grid.SetRow(l1, 1);
            var l2 = UiKit.Label("Only repeaters in these zones (none = all)", true); Grid.SetRow(l2, 1); Grid.SetColumn(l2, 1);
            var s1 = new ScrollViewer { Content = ruleTalkgroups }; Grid.SetRow(s1, 2);
            var s2 = new ScrollViewer { Content = ruleZones }; Grid.SetRow(s2, 2); Grid.SetColumn(s2, 1);
            rule.Children.Add(ruleHint); rule.Children.Add(l1); rule.Children.Add(l2); rule.Children.Add(s1); rule.Children.Add(s2);
            ruleMiles = new NumericUpDown { Minimum = 0, Maximum = 2000, Increment = 10, Width = 130, FormatString = "0" };
            ruleMiles.ValueChanged += (s, e) => { if (!loading) SaveRule(); };
            var milesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { UiKit.Label("Only repeaters within"), ruleMiles, UiKit.Label("miles of home (0 = any distance)") } };
            Grid.SetRow(milesRow, 3); Grid.SetColumnSpan(milesRow, 2);
            rule.Children.Add(milesRow);

            tabs = new TabControl();
            tgTab = new TabItem { Header = "Talkgroups", Content = editor };
            chTab = new TabItem { Header = "Channels", Content = chPanel };
            ruleTab = new TabItem { Header = "Rule", Content = rule };
            tabs.Items.Add(tgTab);
            tabs.Items.Add(chTab);

            var ab = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            var la = UiKit.Label("A side opens on"); var lb = UiKit.Label("B side opens on");
            Grid.SetRow(lb, 1); Grid.SetColumn(cboA, 1); Grid.SetColumn(cboB, 1); Grid.SetRow(cboB, 1);
            ab.Children.Add(la); ab.Children.Add(lb); ab.Children.Add(cboA); ab.Children.Add(cboB);
            var d = new DockPanel { Margin = new Thickness(6, 8, 10, 8) };
            DockPanel.SetDock(lblZone, Dock.Top); DockPanel.SetDock(ab, Dock.Top);
            d.Children.Add(lblZone); d.Children.Add(ab); d.Children.Add(tabs);
            detail = d;

            var dock = new DockPanel();
            DockPanel.SetDock(left, Dock.Left);
            dock.Children.Add(left);
            dock.Children.Add(detail);
            Content = dock;

            list.SelectionChanged += (s, e) => { if (!loading) ShowDetail(); };
            cboA.SelectionChanged += (s, e) => SetSide(true);
            cboB.SelectionChanged += (s, e) => SetSide(false);
            editor.Changed += (s, e) =>
            {
                session.NotifyTalkgroupsChanged(); // new talkgroups, and repeaters' channel lists changed
                editing = true;
                try { Reload(SelectedZone?.Name); } finally { editing = false; }
            };
            session.Replaced += (s, e) => Reload(null);
            AttachedToVisualTree += async (s, e) =>
            {
                Reload(SelectedZone?.Name);
                if (!bmLoaded) { bmLoaded = true; try { editor.SetBrandMeister(await Online.BrandMeisterNamesAsync()); } catch { } }
            };
        }

        /// <summary>Called when the tab is shown, so counts reflect edits made on the other tabs.</summary>
        public void Refresh() { Reload(SelectedZone?.Name); }

        ZoneInfo SelectedZone => (list.SelectedItem as ZoneRow)?.Zone;
        GeneratedChannel SelectedMember => (members.SelectedItem as MemberRow)?.C;

        /// <summary>The zone's channels as the generator writes them (own repeaters, listed members, rule).</summary>
        List<GeneratedChannel> ZoneChannels(string zone)
        {
            if (preview == null) return new List<GeneratedChannel>();
            var byKey = preview.ChannelList.ToDictionary(c => (object)c.Entry ?? c.Repeater);
            return session.Project.ZoneChannels(zone).Select(c => byKey.TryGetValue(c.Key, out var gc) ? gc : null).Where(c => c != null).ToList();
        }

        /// <summary>Why a channel is in the zone: its repeater's zone, the zone's list, or the talkgroup rule.</summary>
        string Reason(ZoneInfo z, GeneratedChannel c)
        {
            if (Project.SameZone(c.Repeater.Zone, z.Name)) return "its repeater's zone";
            var key = new ChannelRef(c.Repeater, c.Entry);
            if (z.Members != null && z.Members.Any(m => session.Project.Resolve(m)?.Equals(key) == true))
                return "added here (its zone: " + (string.IsNullOrWhiteSpace(c.Repeater.Zone) ? "none" : c.Repeater.Zone) + ")";
            return "talkgroup rule";
        }

        public void Reload(string select)
        {
            loading = true;
            try
            {
                session.Project.SyncZones();
                try { preview = CodeplugGenerator.Generate(session.Project, session.Format); }
                catch { preview = null; }
                zoneRows = new List<ZoneRow>();
                foreach (var z in session.Project.Zones)
                {
                    int reps = session.Project.ActiveRepeaters().Count(r => Project.SameZone(r.Zone, z.Name));
                    int chans = ZoneChannels(z.Name).Count;
                    zoneRows.Add(new ZoneRow
                    {
                        Zone = z, Kind = z.IsRoute ? "Route" : session.Project.ZoneKindOf(z), Repeaters = reps.ToString(CultureInfo.InvariantCulture),
                        Channels = chans == 0 ? "none" : chans.ToString(CultureInfo.InvariantCulture),
                    });
                }
                list.ItemsSource = zoneRows;
                var pick = select != null ? zoneRows.FirstOrDefault(r => Project.SameZone(r.Name, select)) : null;
                pick = pick ?? zoneRows.FirstOrDefault(r => r.Channels != "none") ?? zoneRows.FirstOrDefault(); // first zone with channels
                if (pick != null) list.SelectedItem = pick;
            }
            finally { loading = false; }
            ShowDetail();
        }

        void ShowDetail()
        {
            var z = SelectedZone;
            detail.IsVisible = z != null;
            btnDelete.IsEnabled = z != null && !session.Project.AllRepeaters().Any(r => Project.SameZone(r.Zone, z.Name));
            if (z == null) return;
            loading = true;
            try
            {
                var channels = ZoneChannels(z.Name);
                var names = channels.Select(c => c.Name).ToList();
                lblZone.Text = z.Name;
                cboA.ItemsSource = names; cboB.ItemsSource = names;
                var gz = preview?.ZoneList.FirstOrDefault(x => Project.SameZone(x.Name, z.Name));
                cboA.SelectedItem = gz?.AChannel;
                cboB.SelectedItem = gz?.BChannel;
                members.ItemsSource = channels.Select(c => new MemberRow
                {
                    C = c,
                    No = c.Number.ToString(CultureInfo.InvariantCulture),
                    Channel = c.Name,
                    Talkgroup = c.IsDigital ? c.Talkgroup.Name : "FM" + (c.Repeater.RxOnly ? " (RX only)" : ""),
                    Slot = c.IsDigital ? c.Entry.Slot.ToString(CultureInfo.InvariantCulture) : "",
                    Rx = c.Repeater.RxMHz.ToString("0.000", CultureInfo.InvariantCulture),
                    Why = Reason(z, c),
                }).ToList();

                // Talkgroup zones show their rule; zone talkgroup sets only make sense for zones with repeaters of their own.
                string kind = session.Project.ZoneKindOf(z);
                bool showRule = kind == ZoneKinds.Talkgroup;
                bool showSet = session.Project.ZoneRepeaters(z.Name).Count > 0 || kind == ZoneKinds.Area;
                if (showRule && !tabs.Items.Contains(ruleTab)) tabs.Items.Add(ruleTab);
                if (!showRule && tabs.Items.Contains(ruleTab)) tabs.Items.Remove(ruleTab);
                if (showSet && !tabs.Items.Contains(tgTab)) tabs.Items.Insert(0, tgTab);
                if (!showSet && tabs.Items.Contains(tgTab)) tabs.Items.Remove(tgTab);
                if (tabs.SelectedItem == null) tabs.SelectedIndex = 0;
                if (showRule) FillRule(z);
                // The editor refreshes itself after its own edits; rebinding then would lose its selection.
                if (!editing || editor.Zone == null || !Project.SameZone(editor.Zone, z.Name)) editor.Bind(session.Project, z.Name);
            }
            finally { loading = false; }
        }

        void FillRule(ZoneInfo z)
        {
            ruleMiles.Value = (decimal)Math.Max(0, Math.Min(2000, z.RuleMiles));
            ruleTalkgroups.Children.Clear();
            foreach (var t in session.Project.Talkgroups.Where(t => t.IsGroupCall || (z.RuleTalkgroups?.Contains(t.Id) ?? false)))
            {
                var box = new CheckBox { Content = t.ToString(), IsChecked = z.RuleTalkgroups?.Contains(t.Id) ?? false, Tag = t.Id };
                box.IsCheckedChanged += (s, e) => SaveRule();
                ruleTalkgroups.Children.Add(box);
            }
            ruleZones.Children.Clear();
            foreach (var other in session.Project.Zones.Where(x => x != z && session.Project.ZoneRepeaters(x.Name).Count > 0))
            {
                var box = new CheckBox { Content = other.Name, IsChecked = z.RuleZones?.Any(n => Project.SameZone(n, other.Name)) ?? false, Tag = other.Name };
                box.IsCheckedChanged += (s, e) => SaveRule();
                ruleZones.Children.Add(box);
            }
        }

        void SaveRule()
        {
            var z = SelectedZone;
            if (z == null || loading) return;
            z.RuleTalkgroups = ruleTalkgroups.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (int)b.Tag).ToList();
            z.RuleZones = ruleZones.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
            if (z.RuleTalkgroups.Count == 0) z.RuleTalkgroups = null;
            if (z.RuleZones.Count == 0) z.RuleZones = null;
            z.RuleMiles = (double)(ruleMiles.Value ?? 0);
            if (z.Kind == null) z.Kind = ZoneKinds.Talkgroup;
            session.MarkDirty();
            editing = true;
            try { Reload(z.Name); } finally { editing = false; }
        }

        async Task NewZone(string kind)
        {
            string name = await Dialogs.Prompt(owner, "New " + kind.ToLowerInvariant() + " zone", "Name of the new zone (16 characters max):",
                                               kind == ZoneKinds.Favorites ? "Favorites" : "", 16);
            if (name == null) return;
            name = Naming.Fit(name, 16);
            if (name.Length == 0) return;
            if (session.Project.FindZone(name) != null) { await Dialogs.Error(owner, "There is already a zone called \"" + name + "\"."); return; }
            session.Project.Zones.Add(new ZoneInfo(name) { Kind = kind });
            session.MarkDirty();
            Reload(name);
            if (kind == ZoneKinds.Favorites) { tabs.SelectedItem = chTab; await AddChannels(); }
            else tabs.SelectedItem = ruleTab;
        }

        /// <summary>Hotspot zone, favourites, area zones nearest first, talkgroup zones, then utilities (ZoneOrder).</summary>
        async Task SortZones()
        {
            var p = session.Project;
            if (p.Home == null &&
                !await Dialogs.Ask(owner, "No home town is set (Settings tab), so area zones keep their order; only the groups move: hotspot, favorites, " +
                                          "areas, talkgroup zones, then simplex and weather.\n\nSort anyway?", "Sort"))
                return;
            if (!ZoneOrder.Sort(p)) { await Dialogs.Info(owner, "The zones are already in that order."); return; }
            session.MarkDirty();
            Reload(SelectedZone?.Name);
        }

        async Task DeleteZone()
        {
            var z = SelectedZone;
            if (z == null) return;
            int own = session.Project.AllRepeaters().Count(r => Project.SameZone(r.Zone, z.Name));
            if (own > 0)
            {
                await Dialogs.Info(owner, own + " repeater(s) have \"" + z.Name + "\" as their zone. Give them another zone on the Repeaters tab (or rename this zone to merge it into another).");
                return;
            }
            if (!await Dialogs.Ask(owner, "Delete zone \"" + z.Name + "\"? Its channels stay in their own zones.", "Delete")) return;
            session.Project.Zones.Remove(z);
            session.MarkDirty();
            Reload(null);
        }

        async Task AddChannels()
        {
            var z = SelectedZone;
            if (z == null) return;
            var result = await ZoneMembersWindow.Run(owner, session, z.Name, null);
            if (result.Value == 0) return;
            session.MarkDirty();
            Reload(z.Name);
            tabs.SelectedItem = chTab;
        }

        async Task RemoveMember()
        {
            var z = SelectedZone;
            var c = SelectedMember;
            if (z == null || c == null) return;
            if (Project.SameZone(c.Repeater.Zone, z.Name))
            {
                await Dialogs.Info(owner, "\"" + c.Name + "\" is in this zone because its repeater's zone is \"" + z.Name + "\". Change the repeater's zone on the Repeaters tab to take it out.");
                return;
            }
            if (!session.Project.RemoveFromZone(z.Name, new ChannelRef(c.Repeater, c.Entry)))
            {
                await Dialogs.Info(owner, "\"" + c.Name + "\" is here because of the zone's talkgroup rule (Rule tab).");
                return;
            }
            session.MarkDirty();
            Reload(z.Name);
        }

        void MoveMember(int delta)
        {
            var z = SelectedZone;
            var c = SelectedMember;
            if (z == null || c == null) return;
            var key = new ChannelRef(c.Repeater, c.Entry);
            var p = session.Project;
            // Pin the current order of the listed and own channels first, so moving works for the zone's own channels too.
            // Channels there only by the talkgroup rule follow the rule and can't be moved.
            var order = ZoneChannels(z.Name).Where(x => Reason(z, x) != "talkgroup rule").Select(x => new ChannelRef(x.Repeater, x.Entry)).ToList();
            if (!order.Contains(key)) return;
            z.Members = order.Select(p.MemberFor).ToList();
            if (!p.MoveZoneMember(z.Name, key, delta)) return;
            session.MarkDirty();
            Reload(z.Name);
            var row = (members.ItemsSource as IEnumerable<MemberRow>)?.FirstOrDefault(m => m.C.Repeater == c.Repeater && m.C.Entry == c.Entry);
            if (row != null) { members.SelectedItem = row; members.ScrollIntoView(row, null); }
        }

        void SetSide(bool a)
        {
            if (loading) return;
            var z = SelectedZone;
            if (z == null) return;
            var box = a ? cboA : cboB;
            if (a) z.AChannel = box.SelectedItem as string; else z.BChannel = box.SelectedItem as string;
            session.MarkDirty();
        }

        void MoveItem(int delta)
        {
            var z = SelectedZone;
            if (z == null) return;
            var zones = session.Project.Zones;
            int i = zones.IndexOf(z), j = i + delta;
            if (j < 0 || j >= zones.Count) return;
            zones.RemoveAt(i);
            zones.Insert(j, z);
            session.MarkDirty();
            Reload(z.Name);
        }

        async Task Rename()
        {
            var z = SelectedZone;
            if (z == null) return;
            string name = await Dialogs.Prompt(owner, "Rename zone", "New name for zone \"" + z.Name + "\" (16 characters max):", z.Name, 16);
            if (name == null) return;
            name = Naming.Fit(name, 16);
            if (name.Length == 0 || name == z.Name) return;
            if (session.Project.FindZone(name) != null && !Project.SameZone(name, z.Name) &&
                !await Dialogs.Ask(owner, "A zone called \"" + name + "\" already exists. Merge \"" + z.Name + "\" into it?", "Merge"))
                return;
            session.Project.RenameZone(z.Name, name);
            session.MarkDirty();
            Reload(name);
        }
    }

    /// <summary>
    /// The talkgroups of one zone. Ticked = on every DMR repeater in the zone (the zone's talkgroup set, which also reaches
    /// repeaters added later); unticked rows are talkgroups only some repeaters list themselves. Search adds talkgroups from
    /// the project or from BrandMeister's list.
    /// </summary>
    sealed class ZoneTalkgroupsView : UserControl
    {
        readonly Window owner;
        Project project;
        string zone;
        /// <summary>Talkgroups unticked in each zone, kept in the list (unticked) so they can be ticked again; "Remove from zone" forgets them.</summary>
        readonly Dictionary<string, HashSet<int>> unticked = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, string> bm = new Dictionary<int, string>();
        bool loading;
        List<ZtRow> rows = new List<ZtRow>();

        readonly TextBlock lblTitle, lblCount;
        readonly TextBox txtSearch;
        readonly ListBox lstFound;
        readonly DataGrid grid;

        public event EventHandler Changed;

        sealed class Found
        {
            public Talkgroup Existing;
            public int Id;
            public string Name;
            public override string ToString() { return Name + "  (" + Id.ToString(CultureInfo.InvariantCulture) + ")" + (Existing == null ? "  - BrandMeister" : ""); }
        }

        sealed class ZtRow : RowBase
        {
            readonly ZoneTalkgroupsView owner;
            public int TgId;
            public string TalkgroupText, SlotText, OnText;
            public bool Listed;
            public ZtRow(ZoneTalkgroupsView owner) { this.owner = owner; }
            public bool All
            {
                get { return Listed; }
                set { if (Listed == value) return; Listed = value; owner.Ticked(this, value); }
            }
            public string Talkgroup => TalkgroupText;
            public string Id => TgId.ToString(CultureInfo.InvariantCulture);
            public string Slot => SlotText;
            public string Repeaters => OnText;
        }

        public ZoneTalkgroupsView(Window owner)
        {
            this.owner = owner;
            lblTitle = new TextBlock { FontWeight = FontWeight.SemiBold };
            var hint = UiKit.Hint("Ticked talkgroups go on every DMR repeater in this zone, including repeaters you add to it later. Unticked ones are listed by only some of the repeaters themselves.");
            lblCount = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

            txtSearch = new TextBox { Watermark = "Find a talkgroup: name or number" };
            lstFound = new ListBox { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, MinHeight = 140 };
            var addRow = UiKit.Row(UiKit.Button("Add to zone  >", AddFound), UiKit.Button("Starter set", AddStarterSet));
            addRow.Margin = new Thickness(0, 6, 0, 0);
            var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(txtSearch, Dock.Top); DockPanel.SetDock(addRow, Dock.Bottom);
            left.Children.Add(txtSearch); left.Children.Add(addRow); left.Children.Add(lstFound);

            grid = UiKit.Grid(true);
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "All", Binding = new Avalonia.Data.Binding("All"), Width = new DataGridLength(50) });
            grid.Columns.Add(UiKit.Col("Talkgroup", "Talkgroup", true, 190));
            grid.Columns.Add(UiKit.Col("ID", "Id", true, 80));
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Slot",
                Width = new DataGridLength(90),
                CellTemplate = new FuncDataTemplate<ZtRow>((row, ns) =>
                {
                    var box = new ComboBox { ItemsSource = new[] { "1", "2" }, MinWidth = 64, IsEnabled = row != null && row.Listed };
                    if (row != null) box.SelectedItem = row.SlotText;
                    if (row != null && !row.Listed) return new TextBlock { Text = row.SlotText, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
                    box.SelectionChanged += (s, e) =>
                    {
                        if (loading || row == null || !(box.SelectedItem is string v) || v == row.SlotText) return;
                        project.SetZoneTalkgroupSlot(zone, row.TgId, v == "2" ? 2 : 1);
                        AfterChange();
                    };
                    return box;
                }),
            });
            grid.Columns.Add(UiKit.Col("Repeaters", "Repeaters", true, 90));
            grid.KeyDown += async (s, e) => { if (e.Key == Key.Delete) { e.Handled = true; await RemoveSelected(); } };
            var rightButtons = UiKit.Row(UiKit.Button("Remove from zone", async () => await RemoveSelected()), UiKit.Button("Copy ticked to all zones", async () => await CopyToAllZones()));
            rightButtons.Margin = new Thickness(0, 6, 0, 0);
            var right = new DockPanel();
            DockPanel.SetDock(rightButtons, Dock.Bottom);
            right.Children.Add(rightButtons); right.Children.Add(grid);

            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1.6*") };
            Grid.SetColumn(right, 1);
            body.Children.Add(left); body.Children.Add(right);

            var top = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(lblTitle); top.Children.Add(hint);
            var dock = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(lblCount, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(lblCount); dock.Children.Add(body);
            Content = dock;

            UiKit.OnText(txtSearch, RefreshFound);
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { AddFound(); e.Handled = true; }
                else if (e.Key == Key.Down && lstFound.ItemCount > 0) { lstFound.Focus(); lstFound.SelectedIndex = 0; e.Handled = true; }
            };
            lstFound.DoubleTapped += (s, e) => AddFound();
            Bind(null, null);
        }

        public string Zone => zone;
        internal void PressStarterSet() { AddStarterSet(); }

        public void Bind(Project p, string zoneName)
        {
            project = p;
            zone = zoneName;
            bool has = p != null && zoneName != null && p.FindZone(zoneName) != null;
            IsEnabled = has;
            lblTitle.Text = has ? "Talkgroups in \"" + zoneName + "\"" : "Pick a zone";
            RefreshGrid();
            RefreshFound();
        }

        public void SetBrandMeister(Dictionary<int, string> names)
        {
            bm = names ?? new Dictionary<int, string>();
            RefreshFound();
        }

        /// <summary>Slot for a newly added talkgroup: simplex hotspots carry everything on slot 2, repeaters follow BrandMeister habits.</summary>
        int SlotFor(int id)
        {
            var reps = ZoneDigital();
            if (reps.Count > 0 && reps.All(r => r.RxMHz > 0 && r.RxMHz == r.TxMHz)) return 2;
            return Project.DefaultSlot(id);
        }

        List<Repeater> ZoneDigital()
        {
            return project == null || zone == null ? new List<Repeater>() : project.ZoneRepeaters(zone).Where(r => r.Enabled || r == project.Hotspot).ToList();
        }

        public void RefreshGrid()
        {
            loading = true;
            try
            {
                var keep = new HashSet<int>(grid.SelectedItems.OfType<ZtRow>().Select(r => r.TgId));
                rows = new List<ZtRow>();
                var info = project?.FindZone(zone ?? "");
                if (info == null) { grid.ItemsSource = rows; lblCount.Text = ""; return; }
                var reps = ZoneDigital();
                var ids = new List<int>();
                foreach (var t in info.Talkgroups ?? new List<ZoneTalkgroup>()) if (!ids.Contains(t.TalkgroupId)) ids.Add(t.TalkgroupId);
                foreach (var r in reps)
                    foreach (var e in r.Talkgroups) if (!ids.Contains(e.TalkgroupId)) ids.Add(e.TalkgroupId);
                if (unticked.TryGetValue(zone, out var gone))
                    foreach (int id in gone) if (!ids.Contains(id) && project.FindTalkgroup(id) != null) ids.Add(id);
                foreach (int id in ids)
                {
                    var tg = project.FindTalkgroup(id);
                    var zt = info.FindTalkgroup(id);
                    int on = reps.Count(r => r.Talkgroups.Any(e => e.TalkgroupId == id));
                    var slots = reps.SelectMany(r => r.Talkgroups.Where(e => e.TalkgroupId == id).Select(e => e.Slot)).Distinct().ToList();
                    rows.Add(new ZtRow(this)
                    {
                        TgId = id,
                        Listed = zt != null,
                        TalkgroupText = tg == null ? "(not in your talkgroup list)" : tg.Name + (tg.CallType == CallTypes.Private ? " (private)" : ""),
                        SlotText = zt != null ? zt.Slot.ToString(CultureInfo.InvariantCulture) : slots.Count == 1 ? slots[0].ToString(CultureInfo.InvariantCulture) : "1 + 2",
                        OnText = reps.Count == 0 ? "" : on == reps.Count ? "all " + reps.Count : on + " of " + reps.Count,
                    });
                }
                grid.ItemsSource = rows;
                grid.SelectedItems.Clear();
                foreach (var r in rows.Where(r => keep.Contains(r.TgId))) grid.SelectedItems.Add(r);
                UpdateCount(reps);
            }
            finally { loading = false; }
        }

        void UpdateCount(List<Repeater> reps)
        {
            int chans = project.ZoneChannelCount(zone);
            int max = Math.Max(1, project.Options.MaxZoneChannels);
            int total = project.ChannelCount();
            int bare = reps.Count(r => r.Talkgroups.Count == 0);
            string text = reps.Count + " DMR repeater" + (reps.Count == 1 ? "" : "s") + " in this zone, " + chans + " channel" + (chans == 1 ? "" : "s") +
                          (chans > max ? " (more than " + max + ": the radio gets it as " + ((chans + max - 1) / max) + " zones)" : "") +
                          ".   Whole codeplug: " + total + " of " + project.Options.MaxChannels + " channels.";
            if (bare > 0) text += "\n" + bare + " repeater" + (bare == 1 ? " has" : "s have") + " no talkgroups yet, so no channels: tick or add some here.";
            lblCount.Text = text;
            lblCount.Foreground = total > project.Options.MaxChannels ? Brushes.Firebrick : bare > 0 ? Brushes.DarkGoldenrod : null;
        }

        void RefreshFound()
        {
            var items = new List<Found>();
            if (project != null)
            {
                string q = (txtSearch.Text ?? "").Trim();
                var terms = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                bool Match(int id, string name) => terms.All(t => (name ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || id.ToString(CultureInfo.InvariantCulture).StartsWith(t, StringComparison.Ordinal));
                items = project.Talkgroups.Where(t => Match(t.Id, t.Name)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                               .Select(t => new Found { Existing = t, Id = t.Id, Name = t.Name }).ToList();
                if (terms.Length > 0)
                    items.AddRange(bm.Where(kv => project.FindTalkgroup(kv.Key) == null && Match(kv.Key, kv.Value)).OrderBy(kv => kv.Key).Take(300)
                                     .Select(kv => new Found { Id = kv.Key, Name = kv.Value }));
                if (int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typed) && typed > 0 && typed <= Validator.MaxTalkgroupId && !items.Any(i => i.Id == typed))
                    items.Add(new Found { Id = typed, Name = "TG " + typed.ToString(CultureInfo.InvariantCulture) });
            }
            lstFound.ItemsSource = items;
        }

        /// <summary>The project's talkgroup with this ID, creating it (named from BrandMeister, shortened to 16) if needed.</summary>
        Talkgroup Ensure(int id, string fallbackName)
        {
            var tg = project.FindTalkgroup(id);
            if (tg != null) return tg;
            string name = bm.TryGetValue(id, out string n) ? BrandMeister.ShortName(n) : BrandMeister.ShortName(fallbackName ?? "");
            name = Naming.UniqueTalkgroupName(name, id, x => project.Talkgroups.Any(t => string.Equals(t.Name, x, StringComparison.OrdinalIgnoreCase)));
            tg = new Talkgroup(name, id, BrandMeister.IsPrivateCall(id, name) ? CallTypes.Private : CallTypes.Group);
            project.Talkgroups.Add(tg);
            return tg;
        }

        void AddFound()
        {
            if (project == null || zone == null) return;
            var picked = lstFound.SelectedItems.OfType<Found>().ToList();
            if (picked.Count == 0 && lstFound.ItemCount == 1 && lstFound.ItemsSource is List<Found> only) picked.Add(only[0]);
            if (picked.Count == 0) { txtSearch.Focus(); return; }
            foreach (var f in picked)
            {
                var tg = Ensure(f.Id, f.Name);
                project.AddZoneTalkgroup(zone, tg.Id, SlotFor(tg.Id));
            }
            AfterChange();
            txtSearch.SelectAll();
        }

        /// <summary>Your statewide talkgroup, USA, Local and Parrot (BrandMeister), on their usual slots.</summary>
        void AddStarterSet()
        {
            if (project == null || zone == null) return;
            string state = ZoneDigital().Select(r => r.State).Where(s => !string.IsNullOrEmpty(s))
                                        .GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            foreach (var c in OnlineImporter.SuggestedDefaults(state, bm).Where(c => c.Talkgroup.Id != 91 && c.Talkgroup.Id != 93))
            {
                var tg = project.FindTalkgroup(c.Talkgroup.Id) ?? Ensure(c.Talkgroup.Id, c.Talkgroup.Name);
                if (c.Talkgroup.CallType == CallTypes.Private) tg.CallType = CallTypes.Private;
                project.AddZoneTalkgroup(zone, tg.Id, SlotFor(tg.Id));
            }
            AfterChange();
        }

        /// <summary>A row's "All" box was ticked or unticked.</summary>
        void Ticked(ZtRow row, bool on)
        {
            if (loading || project == null) return;
            if (on)
            {
                int slot = row.SlotText == "1" ? 1 : row.SlotText == "2" ? 2 : SlotFor(row.TgId);
                project.AddZoneTalkgroup(zone, row.TgId, slot);
                if (unticked.TryGetValue(zone, out var gone)) gone.Remove(row.TgId);
            }
            else
            {
                project.RemoveZoneTalkgroup(zone, row.TgId, alsoListed: false);
                if (!unticked.TryGetValue(zone, out var gone)) unticked[zone] = gone = new HashSet<int>();
                gone.Add(row.TgId);
            }
            // Rebuild the rows after the grid is done with this edit.
            Avalonia.Threading.Dispatcher.UIThread.Post(AfterChange);
        }

        async Task RemoveSelected()
        {
            if (project == null || zone == null) return;
            var ids = grid.SelectedItems.OfType<ZtRow>().Select(r => r.TgId).ToList();
            if (ids.Count == 0) return;
            int channels = ZoneDigital().Sum(r => r.Talkgroups.Count(t => ids.Contains(t.TalkgroupId)));
            if (channels > 0 && !await Dialogs.Ask(owner, "Remove " + (ids.Count == 1 ? "this talkgroup" : ids.Count + " talkgroups") + " from every repeater in \"" + zone + "\"?\n\n" +
                                                         channels + " channel" + (channels == 1 ? "" : "s") + " will go, including ones the repeaters list themselves.", "Remove"))
                return;
            foreach (int id in ids)
            {
                project.RemoveZoneTalkgroup(zone, id);
                if (unticked.TryGetValue(zone, out var gone)) gone.Remove(id);
            }
            AfterChange();
        }

        async Task CopyToAllZones()
        {
            var info = project?.FindZone(zone ?? "");
            if (info == null || !info.HasTalkgroups) { await Dialogs.Info(owner, "Tick some talkgroups in this zone first."); return; }
            var others = project.Zones.Where(z => z != info && project.TakesZoneTalkgroups(z.Name)).ToList();
            if (others.Count == 0) { await Dialogs.Info(owner, "There are no other zones with DMR repeaters."); return; }
            int count = info.Talkgroups.Count;
            if (!await Dialogs.Ask(owner, "Tick this zone's " + count + " talkgroup" + (count == 1 ? "" : "s") + " (same slots) in the other " + others.Count +
                                          " zone" + (others.Count == 1 ? "" : "s") + " too: " + string.Join(", ", others.Select(z => z.Name)) + "?", "Copy"))
                return;
            int added = others.Sum(z => project.CopyZoneTalkgroups(zone, z.Name));
            AfterChange();
            await Dialogs.Info(owner, Project.CopyZoneTalkgroupsReport(count, others.Count, added));
        }

        void AfterChange()
        {
            RefreshGrid();
            RefreshFound();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
