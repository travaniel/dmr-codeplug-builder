using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>The list of repeaters / analog channels on the left, the editor on the right.</summary>
    sealed class RepeatersTab : UserControl
    {
        readonly Session session;
        readonly Window owner;
        readonly DataGrid list;
        readonly ComboBox cboFilter;
        readonly RepeaterEditorView editor;
        readonly TextBlock empty;
        readonly Button btnUp, btnDown;
        List<RepRow> rows = new List<RepRow>();
        bool loading;

        const string AllZones = "All zones";

        sealed class RepRow : RowBase
        {
            public readonly Repeater R;
            readonly Action<RepRow> toggled;
            public RepRow(Repeater r, Action<RepRow> toggled) { R = r; this.toggled = toggled; }
            public bool On
            {
                get { return R.Enabled; }
                set { if (R.Enabled == value) return; R.Enabled = value; toggled(this); }
            }
            public string Name => string.IsNullOrWhiteSpace(R.Name) ? "(no name)" : R.Name;
            public string Zone => R.Zone;
            public string Type => R.IsDigital
                ? "DMR " + R.Talkgroups.Count + " TG" + (R.Talkgroups.Count == 1 ? "" : "s")
                : "FM" + (Tones.Normalize(R.ToneEncode) != "Off" ? " " + Tones.Normalize(R.ToneEncode) : "");
            public string Rx => R.RxMHz > 0 ? R.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) : "";
        }

        public RepeatersTab(Session session, Window owner)
        {
            this.session = session;
            this.owner = owner;

            cboFilter = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            list = UiKit.Grid(false);
            list.Columns.Add(new DataGridCheckBoxColumn { Header = "On", Binding = new Avalonia.Data.Binding("On"), Width = new DataGridLength(46) });
            list.Columns.Add(UiKit.Col("Name", "Name", true, 150));
            list.Columns.Add(UiKit.Col("Zone", "Zone", true, 100));
            list.Columns.Add(UiKit.Col("Type", "Type", true, 90));
            list.Columns.Add(UiKit.Col("RX MHz", "Rx", true, 70));

            btnUp = UiKit.Button("Up", () => MoveItem(-1));
            btnDown = UiKit.Button("Down", () => MoveItem(1));
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            void AddButton(string text, Action click) { var b = UiKit.Button(text, click); b.Margin = new Thickness(0, 0, 6, 6); buttons.Children.Add(b); }
            AddButton("Add DMR repeater", () => Add(Repeater.NewDigital("New repeater")));
            AddButton("Add analog", () => Add(Repeater.NewAnalog("New analog")));
            AddButton("Duplicate", Duplicate);
            AddButton("Add to zone...", async () => await AddToZone());
            AddButton("Delete", async () => await Delete());
            buttons.Children.Add(btnUp); btnUp.Margin = new Thickness(0, 0, 6, 6);
            buttons.Children.Add(btnDown); btnDown.Margin = new Thickness(0, 0, 6, 6);
            AddButton("Add from map...", async () => await AddFromMap());
            AddButton("Add NOAA weather", async () => await AddWeather());
            AddButton("Add simplex", async () => await AddSimplex());

            var filterRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            var show = UiKit.Label("Show");
            DockPanel.SetDock(show, Dock.Left);
            filterRow.Children.Add(show);
            filterRow.Children.Add(cboFilter);

            var left = new DockPanel { Width = 520, Margin = new Thickness(10, 8, 6, 8) };
            var head = UiKit.Heading("Repeaters and channels");
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(filterRow, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            left.Children.Add(head); left.Children.Add(filterRow); left.Children.Add(buttons); left.Children.Add(list);

            editor = new RepeaterEditorView(false);
            empty = new TextBlock
            {
                Text = "Click \"Add from map...\" to add DMR repeaters near you from RadioID.net and BrandMeister, or add one by hand with the buttons on the left, or File > Import from a CPS export to load your current codeplug.\n\n(Find online and From RepeaterBook come in a later step of the Mac port.)",
                Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap, TextAlignment = Avalonia.Media.TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420,
            };
            var right = new Grid { Children = { editor, empty } };

            var dock = new DockPanel();
            DockPanel.SetDock(left, Dock.Left);
            dock.Children.Add(left);
            dock.Children.Add(right);
            Content = dock;

            list.SelectionChanged += (s, e) => { if (!loading) BindSelected(); };
            cboFilter.SelectionChanged += (s, e) => { if (!loading) Reload(Selected); };
            cboFilter.DropDownOpened += (s, e) => RefillFilter();
            editor.Changed += (s, e) => { var sel = list.SelectedItem as RepRow; sel?.Refresh(); };
            session.Replaced += (s, e) => Reload(null);
            session.TalkgroupsChanged += (s, e) => editor.RefreshTalkgroups();
            Reload(null);
        }

        Repeater Selected => (list.SelectedItem as RepRow)?.R;

        string Filter => cboFilter.SelectedIndex > 0 ? cboFilter.SelectedItem as string : null;

        /// <summary>Rebuilds the list (after load, add, delete, reorder or zone changes).</summary>
        public void Reload(Repeater select)
        {
            loading = true;
            try
            {
                RefillFilter();
                string filter = Filter;
                // A zone shows its own repeaters and the ones with channels listed in it (favourites, talkgroup zones).
                var inZone = new HashSet<Repeater>(filter == null ? Enumerable.Empty<Repeater>()
                    : session.Project.Repeaters.Where(r => Project.SameZone(r.Zone, filter)).Concat(session.Project.ZoneChannels(filter).Select(c => c.Repeater)));
                rows = session.Project.Repeaters
                    .Where(r => !(filter == "(no zone)" && !string.IsNullOrWhiteSpace(r.Zone)))
                    .Where(r => filter == null || filter == "(no zone)" || inZone.Contains(r))
                    .Select(r => new RepRow(r, row => { if (editor.Repeater == row.R) editor.Bind(session, row.R); session.MarkDirty(); }))
                    .ToList();
                list.ItemsSource = rows;
                var pick = rows.FirstOrDefault(x => x.R == select) ?? (select == null && editor.Repeater == null ? rows.FirstOrDefault() : null);
                if (pick != null) { list.SelectedItem = pick; list.ScrollIntoView(pick, null); }
                btnUp.IsEnabled = btnDown.IsEnabled = filter == null;
            }
            finally { loading = false; }
            BindSelected();
        }

        void RefillFilter()
        {
            bool l = loading;
            loading = true;
            session.Project.SyncZones();
            string filter = Filter;
            var items = new List<string> { AllZones };
            items.AddRange(session.Project.Zones.Select(z => z.Name));
            if (session.Project.Repeaters.Any(r => string.IsNullOrWhiteSpace(r.Zone))) items.Add("(no zone)");
            cboFilter.ItemsSource = items;
            int fi = filter == null ? 0 : items.IndexOf(filter);
            cboFilter.SelectedIndex = fi < 0 ? 0 : fi;
            loading = l;
        }

        void BindSelected()
        {
            var r = Selected;
            editor.Bind(session, r);
            editor.IsVisible = r != null;
            empty.IsVisible = r == null;
        }

        void Add(Repeater r)
        {
            // New entries go into the zone currently shown (or the selected repeater's zone).
            r.Zone = Filter != null && Filter != "(no zone)" ? Filter : (Selected?.Zone ?? "");
            int at = Selected != null ? session.Project.Repeaters.IndexOf(Selected) + 1 : session.Project.Repeaters.Count;
            session.Project.Repeaters.Insert(at, r);
            session.Project.SyncZones();
            session.Project.ApplyZoneTalkgroups(r); // a DMR repeater added to a zone gets that zone's talkgroups
            session.MarkDirty();
            Reload(r);
            editor.FocusName();
        }

        /// <summary>Opens the map picker and shows what it added.</summary>
        async System.Threading.Tasks.Task AddFromMap()
        {
            var first = await AddFromMapWindow.Run(owner, session);
            if (first == null) return;
            cboFilter.SelectedIndex = 0;
            Reload(first);
        }

        async System.Threading.Tasks.Task AddWeather()
        {
            var added = Presets.AddNoaaWeather(session.Project);
            if (added.Count == 0) { await Dialogs.Info(owner, "You already have all 7 NOAA weather channels."); return; }
            session.MarkDirty();
            cboFilter.SelectedIndex = 0; // they go in the "Weather" zone, which another zone filter would hide
            Reload(added[0]);
        }

        async System.Threading.Tasks.Task AddSimplex()
        {
            var added = Presets.AddSimplex(session.Project);
            if (added.Count == 0) { await Dialogs.Info(owner, "You already have all the simplex channels."); return; }
            session.MarkDirty();
            session.NotifyTalkgroupsChanged(); // talkgroup 99 may be new
            cboFilter.SelectedIndex = 0;
            Reload(added[0]);
        }

        /// <summary>Puts the selected repeater's channels into another zone too (favourites).</summary>
        async System.Threading.Tasks.Task AddToZone()
        {
            var r = Selected;
            if (r == null) return;
            if (r.IsDigital && r.Talkgroups.Count == 0) { await Dialogs.Info(owner, "Add talkgroups to " + r.Name + " first: each one is a channel."); return; }
            var result = await ZoneMembersWindow.Run(owner, session, null, r);
            if (result.Value == 0) return;
            session.MarkDirty();
            await Dialogs.Info(owner, result.Value + " channel(s) of " + r.Name + " are now also in zone \"" + result.Key + "\" (Zones tab).");
        }

        void Duplicate()
        {
            var r = Selected;
            if (r == null) return;
            var copy = r.Clone();
            copy.Name = r.Name + " copy";
            copy.ChannelNumber = 0; // new channels get new numbers; the original keeps its own
            foreach (var e in copy.Talkgroups) { e.ChannelName = null; e.ChannelNumber = 0; }
            session.Project.Repeaters.Insert(session.Project.Repeaters.IndexOf(r) + 1, copy);
            session.MarkDirty();
            Reload(copy);
            editor.FocusName();
        }

        async System.Threading.Tasks.Task Delete()
        {
            var r = Selected;
            if (r == null) return;
            string what = r.IsDigital && r.Talkgroups.Count > 0 ? " and its " + r.Talkgroups.Count + " channels" : "";
            if (!await Dialogs.Ask(owner, "Delete \"" + r.Name + "\"" + what + "?", "Delete")) return;
            int i = rows.FindIndex(x => x.R == r);
            session.Project.Repeaters.Remove(r);
            session.MarkDirty();
            Repeater next = null;
            if (rows.Count > 1) next = rows[i + 1 < rows.Count ? i + 1 : i - 1].R;
            Reload(next);
        }

        void MoveItem(int delta)
        {
            var r = Selected;
            if (r == null || Filter != null) return;
            var all = session.Project.Repeaters;
            int i = all.IndexOf(r), j = i + delta;
            if (j < 0 || j >= all.Count) return;
            all.RemoveAt(i);
            all.Insert(j, r);
            session.MarkDirty();
            Reload(r);
        }
    }

    /// <summary>Your own MMDVM hotspot or local repeater: one switch plus the same editor the repeaters use.</summary>
    sealed class HotspotTab : UserControl
    {
        readonly Session session;
        readonly CheckBox chkEnabled;
        readonly RepeaterEditorView editor;
        bool loading;

        public HotspotTab(Session session)
        {
            this.session = session;
            chkEnabled = new CheckBox { Content = "Include my hotspot in the codeplug", FontWeight = Avalonia.Media.FontWeight.SemiBold };
            var top = new StackPanel { Margin = new Thickness(12, 10, 12, 0), Spacing = 6 };
            top.Children.Add(UiKit.Heading("MMDVM hotspot / local repeater"));
            top.Children.Add(chkEnabled);
            top.Children.Add(UiKit.Hint("Simplex hotspot (Pi-Star, WPSD): set Offset to Simplex; these normally carry everything on slot 2. " +
                                        "Duplex hotspot or MMDVM repeater: use its offset and both slots. Each talkgroup you add becomes a channel in the hotspot's zone."));
            editor = new RepeaterEditorView(true);
            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
            dock.Children.Add(editor);
            Content = dock;

            chkEnabled.IsCheckedChanged += (s, e) =>
            {
                if (loading) return;
                session.Project.HotspotEnabled = chkEnabled.IsChecked == true;
                if (session.Project.HotspotEnabled)
                {
                    // Switched on: it now belongs to its zone, so it takes on the zone's talkgroups.
                    session.Project.ApplyZoneTalkgroups(session.Project.Hotspot);
                    editor.Bind(session, session.Project.Hotspot);
                }
                editor.IsEnabled = session.Project.HotspotEnabled;
                session.MarkDirty();
            };
            editor.Changed += (s, e) => { };
            session.Replaced += (s, e) => Reload();
            session.TalkgroupsChanged += (s, e) => editor.RefreshTalkgroups();
            Reload();
        }

        public void Reload()
        {
            loading = true;
            chkEnabled.IsChecked = session.Project.HotspotEnabled;
            editor.Bind(session, session.Project.Hotspot);
            editor.IsEnabled = session.Project.HotspotEnabled;
            loading = false;
        }
    }
}
