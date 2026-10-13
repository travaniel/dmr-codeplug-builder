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
    /// Repeaters > Find online on the Mac (the Windows OnlineRepeaterDialog): DMR repeaters on RadioID.net by state, tick the ones
    /// you want, and add them with their published talkgroups (named from BrandMeister's list) in a zone per city.
    /// </summary>
    sealed class OnlineRepeaterWindow : Window
    {
        readonly Session session;
        readonly AutoCompleteBox cboState;
        readonly ComboBox cboPower;
        readonly TextBox txtFilter, txtZone;
        readonly Button btnDownload, btnAdd;
        readonly TextBlock lblStatus;
        readonly DataGrid list, gridDefaults;
        readonly GridSorter sorter;
        readonly RadioButton optCity, optOne;
        readonly CheckBox chkNoaa;

        List<OnlineRepeater> all = new List<OnlineRepeater>();
        readonly HashSet<OnlineRepeater> picked = new HashSet<OnlineRepeater>();
        Dictionary<int, string> bm = new Dictionary<int, string>();
        Dictionary<int, string> moreNames = new Dictionary<int, string>();
        List<DefaultRow> defaults = new List<DefaultRow>();
        string defaultsState;
        bool busy;

        public OnlineImportResult Result { get; private set; }
        public List<Repeater> NoaaAdded { get; private set; } = new List<Repeater>();

        sealed class Row : RowBase
        {
            readonly OnlineRepeaterWindow owner;
            public readonly OnlineRepeater R;
            public Row(OnlineRepeaterWindow owner, OnlineRepeater r) { this.owner = owner; R = r; }
            public bool Pick
            {
                get { return owner.picked.Contains(R); }
                set
                {
                    if (value && OnlineImporter.FindExisting(owner.session.Project, R) != null) { Refresh(); return; }
                    if (value) owner.picked.Add(R); else owner.picked.Remove(R);
                    owner.UpdateAddButton();
                }
            }
            public string Callsign => R.Callsign;
            public string City => R.City;
            public string Output => R.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture);
            public decimal OutputMHz => R.RxMHz;
            public string Offset => R.Offset == 0 ? "simplex" : R.Offset.ToString("+0.0##;-0.0##", CultureInfo.InvariantCulture);
            public decimal OffsetMHz => R.Offset;
            public int CC => R.ColorCode;
            public string Network => R.Network;
            public string Listed => owner.Summary(R);
            public int ListedCount => R.Talkgroups.Count;
            public string Status => owner.Status(R);
        }

        /// <summary>A talkgroup that repeaters with no published list get.</summary>
        sealed class DefaultRow : RowBase
        {
            public readonly TalkgroupChoice C;
            readonly Action changed;
            bool use;
            public DefaultRow(TalkgroupChoice c, bool use, Action changed) { C = c; this.use = use; this.changed = changed; }
            public bool Use { get { return use; } set { use = value; changed(); } }
            public string Name => C.Talkgroup.Name + (C.Talkgroup.CallType == CallTypes.Private ? " (private)" : "");
            public string Id => C.Talkgroup.Id.ToString(CultureInfo.InvariantCulture);
        }

        public OnlineRepeaterWindow(Session session)
        {
            this.session = session;
            Title = "Find DMR repeaters online";
            Width = 1160; Height = 800; MinWidth = 860; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(UiKit.Hint("DMR repeaters from RadioID.net, with the talkgroups their owners list there. Talkgroup names come from BrandMeister. " +
                                        "Pick a state, download, then tick the repeaters you want (use the filter to find a city or callsign)."));
            cboState = UiKit.Suggest(BrandMeister.UsStates.Select(s => s.Key), 180);
            SetState(AppSettings.Get("OnlineState") ?? "");
            cboState.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Download(); } };
            btnDownload = UiKit.Button("Download", Download);
            txtFilter = new TextBox { Width = 220, Watermark = "city, callsign, network or MHz" };
            lblStatus = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Text = "Pick your state and click Download." };
            top.Children.Add(UiKit.Row(UiKit.Label("State"), cboState, btnDownload, UiKit.Label("Filter"), txtFilter, lblStatus));

            list = UiKit.Grid(false);
            list.Columns.Add(new DataGridCheckBoxColumn { Header = "Add", Binding = new Avalonia.Data.Binding("Pick"), Width = new DataGridLength(66), CanUserSort = false });
            list.Columns.Add(UiKit.Col("Callsign", "Callsign", true, 90));
            list.Columns.Add(UiKit.Col("City", "City", true, 130));
            var output = UiKit.Col("Output MHz", "Output", true, 110); output.SortMemberPath = "OutputMHz"; list.Columns.Add(output);
            var offset = UiKit.Col("Offset", "Offset", true, 80); offset.SortMemberPath = "OffsetMHz"; list.Columns.Add(offset);
            list.Columns.Add(UiKit.Col("CC", "CC", true, 60));
            list.Columns.Add(UiKit.Col("Network", "Network", true, 130));
            var listed = UiKit.Col("Talkgroups listed", "Listed", true, 380); listed.SortMemberPath = "ListedCount"; list.Columns.Add(listed);
            list.Columns.Add(UiKit.Col("", "Status", true, 110));
            sorter = new GridSorter(list);

            // ---------- options ----------
            var group = "onlinezones" + GetHashCode();
            optCity = new RadioButton { Content = "One zone per city (\"San Angelo\", \"Abilene\"...)", GroupName = group, IsChecked = true };
            optOne = new RadioButton { Content = "All in one zone:", GroupName = group };
            txtZone = new TextBox { Width = 170, MaxLength = 16, IsEnabled = false };
            cboPower = new ComboBox { ItemsSource = Powers.Values, SelectedItem = "High", Width = 100 };
            chkNoaa = new CheckBox { Content = "Also add the 7 NOAA weather channels (receive only, zone \"Weather\")" };
            chkNoaa.IsChecked = Presets.Noaa.Any(n => !session.Project.Repeaters.Any(r => !r.IsDigital && r.RxMHz == n.Value)) &&
                                !session.Project.Repeaters.Any(r => r.RxOnly && r.RxMHz >= 162.4m && r.RxMHz <= 162.55m);
            var left = new StackPanel { Spacing = 6, Width = 440, Children =
            {
                UiKit.Label("Zones", true), optCity, UiKit.Row(optOne, txtZone), UiKit.Row(UiKit.Label("Transmit power"), cboPower), chkNoaa,
            } };

            gridDefaults = UiKit.Grid(false);
            gridDefaults.Height = 170;
            gridDefaults.Columns.Add(new DataGridCheckBoxColumn { Header = "Use", Binding = new Avalonia.Data.Binding("Use"), Width = new DataGridLength(66) });
            gridDefaults.Columns.Add(UiKit.Col("Talkgroup", "Name", true, 220));
            gridDefaults.Columns.Add(UiKit.Col("ID", "Id", true, 90));
            gridDefaults.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Slot",
                Width = new DataGridLength(90),
                CellTemplate = new FuncDataTemplate<DefaultRow>((row, ns) =>
                {
                    var box = new ComboBox { ItemsSource = new[] { "1", "2" }, SelectedItem = row?.C.Slot == 2 ? "2" : "1", MinWidth = 70 };
                    box.SelectionChanged += (s, e) => { if (row != null) row.C.Slot = box.SelectedItem as string == "2" ? 2 : 1; };
                    return box;
                }),
            });
            var right = new StackPanel { Spacing = 6, Children =
            {
                UiKit.Label("Repeaters that don't list their talkgroups get these:", true), gridDefaults,
                UiKit.Row(UiKit.Button("Add talkgroup...", async () => await AddDefaultTalkgroup()),
                          UiKit.Hint("Repeaters vary; check each one's web page and fix slots in the repeater editor afterwards.")),
            } };
            var opts = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(left, Dock.Left);
            opts.Children.Add(left); opts.Children.Add(right);

            // ---------- buttons ----------
            btnAdd = new Button { Content = "Add repeaters", IsDefault = true, FontWeight = FontWeight.SemiBold };
            btnAdd.Click += async (s, e) => await AddPicked();
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Click += (s, e) => Close(false);
            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { btnAdd, cancel } };
            DockPanel.SetDock(rightButtons, Dock.Right);
            buttons.Children.Add(rightButtons);
            buttons.Children.Add(UiKit.Row(UiKit.Button("Tick all shown", () => SetAllShown(true)), UiKit.Button("Untick all", () => { picked.Clear(); Refill(); })));

            var bottom = new StackPanel { Children = { opts, buttons } };
            var dock = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(bottom); dock.Children.Add(list);
            Content = dock;

            chkNoaa.IsCheckedChanged += (s, e) => UpdateAddButton();
            optOne.IsCheckedChanged += (s, e) =>
            {
                txtZone.IsEnabled = optOne.IsChecked == true;
                if (optOne.IsChecked == true && (txtZone.Text ?? "").Length == 0) txtZone.Text = Naming.Clean((cboState.Text ?? "") + " DMR", 16);
            };
            UiKit.OnText(txtFilter, Refill);
            FillDefaults(cboState.Text ?? "");
            UpdateAddButton();
            Opened += async (s, e) =>
            {
                bm = await Online.BrandMeisterNamesAsync();
                FillDefaults(cboState.Text ?? "", force: true);
                Refill();
                if ((cboState.Text ?? "").Length > 0 && all.Count == 0) Download();
            };
        }

        /// <summary>Dev check (--online-check): download a state, filter, tick everything shown; true when the download finished.</summary>
        internal async Task<bool> DevPick(string state, string filter)
        {
            SetState(state);
            Download();
            var t = DateTime.Now;
            while ((busy || all.Count == 0) && (DateTime.Now - t).TotalSeconds < 120) await Task.Delay(300);
            txtFilter.Text = filter;
            await Task.Delay(300);
            SetAllShown(true);
            return all.Count > 0;
        }

        internal Task DevAdd() { return AddPicked(); }

        /// <summary>Puts a state in the box (as the picked item when it is one, so the box shows it).</summary>
        void SetState(string state)
        {
            var match = BrandMeister.UsStates.Select(s => s.Key).FirstOrDefault(s => s.Equals(state, StringComparison.OrdinalIgnoreCase));
            if (match != null) cboState.SelectedItem = match;
            cboState.Text = match ?? state;
        }

        async void Download()
        {
            string state = (cboState.Text ?? "").Trim();
            if (state.Length == 0 || busy) { if (state.Length == 0) lblStatus.Text = "Pick a state first."; return; }
            busy = true;
            btnDownload.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(m => lblStatus.Text = m);
                var result = await Online.RepeatersAsync(state, progress);
                all = result;
                try { moreNames = await Task.Run(() => Online.NameTalkgroups(result, bm, progress, System.Threading.CancellationToken.None)); } catch { }
                try
                {
                    lblStatus.Text = "Placing repeaters (DMR-MARC map positions)...";
                    var pos = await Online.RepeaterPositionsAsync();
                    await Task.Run(() => RadioId.ApplyMapPositions(result, pos, GeoAtlas.BuiltIn()));
                }
                catch { }
                picked.Clear();
                AppSettings.Set("OnlineState", state);
                FillDefaults(state);
                Refill();
                int hidden = all.Count(r => !r.InRadioBand);
                lblStatus.Text = all.Count == 0
                    ? "RadioID.net lists no repeaters for \"" + state + "\". Check the spelling (full state or province name)."
                    : (all.Count - hidden) + " repeaters in " + state + (hidden > 0 ? " (" + hidden + " on bands the radio can't use are hidden)" : "") + ". Tick the ones you want.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Download failed.";
                await Dialogs.Error(this, "Couldn't download the repeater list:\n\n" + ex.Message);
            }
            finally
            {
                busy = false;
                btnDownload.IsEnabled = true;
            }
        }

        IEnumerable<OnlineRepeater> Filtered()
        {
            var terms = (txtFilter.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return all.Where(r => r.InRadioBand && terms.All(t => Matches(r, t)))
                      .OrderBy(r => r.City).ThenBy(r => r.RxMHz).ThenBy(r => r.Callsign);
        }

        static bool Matches(OnlineRepeater r, string term)
        {
            return r.Callsign.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   r.City.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   r.Network.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   r.RxMHz.ToString("0.00000", CultureInfo.InvariantCulture).StartsWith(term, StringComparison.Ordinal);
        }

        string Status(OnlineRepeater r)
        {
            if (OnlineImporter.FindExisting(session.Project, r) != null) return "in your project";
            return r.Talkgroups.Count == 0 ? "none listed" : "";
        }

        void Refill()
        {
            sorter.Show(Filtered().Select(r => new Row(this, r)).ToList());
            UpdateAddButton();
        }

        string Summary(OnlineRepeater r)
        {
            if (r.Talkgroups.Count == 0) return "";
            var parts = new List<string>();
            foreach (int slot in new[] { 1, 2 })
            {
                var names = r.Talkgroups.Where(t => t.Slot == slot).Select(t => OnlineImporter.TalkgroupName(r, t, bm, moreNames)).ToList();
                if (names.Count > 0) parts.Add("TS" + slot + ": " + string.Join(", ", names));
            }
            return string.Join("   ", parts);
        }

        void SetAllShown(bool on)
        {
            foreach (var r in Filtered())
                if (on && OnlineImporter.FindExisting(session.Project, r) == null) picked.Add(r);
                else if (!on) picked.Remove(r);
            Refill();
        }

        void FillDefaults(string state, bool force = false)
        {
            if (!force && string.Equals(state, defaultsState, StringComparison.OrdinalIgnoreCase)) return;
            var keep = defaults.ToDictionary(d => d.C.Talkgroup.Id, d => d.C);
            defaultsState = state;
            var rows = new List<DefaultRow>();
            var suggestions = OnlineImporter.SuggestedDefaults(state, bm);
            foreach (var c in suggestions)
            {
                // Use the project's name for talkgroups it already has.
                var existing = session.Project.FindTalkgroup(c.Talkgroup.Id);
                if (existing != null) c.Talkgroup = existing.Clone();
                rows.Add(new DefaultRow(c, c.Talkgroup.Id != 91 && c.Talkgroup.Id != 93, UpdateAddButton));
            }
            foreach (var kv in keep.Where(k => !suggestions.Any(s => s.Talkgroup.Id == k.Key)))
                rows.Add(new DefaultRow(kv.Value, true, UpdateAddButton));
            defaults = rows;
            gridDefaults.ItemsSource = defaults;
        }

        List<TalkgroupChoice> DefaultChoices() { return defaults.Where(d => d.Use).Select(d => d.C).ToList(); }

        async Task AddDefaultTalkgroup()
        {
            string text = await Dialogs.Prompt(this, "Add a default talkgroup", "Talkgroup number (e.g. 31488):", "", 8);
            if (text == null) return;
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0 || id > Validator.MaxTalkgroupId)
            {
                await Dialogs.Error(this, "Type a talkgroup number, e.g. 31488.");
                return;
            }
            if (defaults.Any(d => d.C.Talkgroup.Id == id)) return;
            var tg = session.Project.FindTalkgroup(id)?.Clone();
            if (tg == null)
            {
                string name = bm.TryGetValue(id, out string n) ? BrandMeister.ShortName(n) : "";
                if (name.Length == 0) name = "TG " + id.ToString(CultureInfo.InvariantCulture);
                tg = new Talkgroup(name, id, BrandMeister.IsPrivateCall(id, name) ? CallTypes.Private : CallTypes.Group);
            }
            defaults = defaults.Concat(new[] { new DefaultRow(new TalkgroupChoice(tg.Name, tg.Id, 1, tg.CallType), true, UpdateAddButton) }).ToList();
            gridDefaults.ItemsSource = defaults;
            UpdateAddButton();
        }

        void UpdateAddButton()
        {
            if (btnAdd == null) return;
            int defaultCount = DefaultChoices().Count;
            var addable = picked.Where(r => OnlineImporter.FindExisting(session.Project, r) == null).ToList();
            int channels = addable.Sum(r => r.Talkgroups.Count > 0 ? r.Talkgroups.Count : defaultCount);
            btnAdd.Content = addable.Count == 0
                ? (chkNoaa.IsChecked == true ? "Add the NOAA channels" : "Add repeaters")
                : "Add " + addable.Count + " repeater" + (addable.Count == 1 ? "" : "s") + " (" + channels + " channel" + (channels == 1 ? "" : "s") + ")";
            btnAdd.IsEnabled = addable.Count > 0 || chkNoaa.IsChecked == true;
        }

        async Task AddPicked()
        {
            var p = session.Project;
            var order = picked.OrderBy(r => r.City).ThenBy(r => r.RxMHz).ThenBy(r => r.Callsign).ToList();
            var o = new OnlineImportOptions
            {
                ZonePerCity = optCity.IsChecked == true,
                Zone = txtZone.Text ?? "",
                Power = cboPower.SelectedItem as string ?? "High",
                DefaultTalkgroups = DefaultChoices(),
                MoreNames = moreNames,
            };
            if (optOne.IsChecked == true && Naming.Clean(o.Zone, 16).Length == 0) { await Dialogs.Error(this, "Type a zone name."); return; }
            // Place them on the map too, so their county/state is kept (zones per county later, Add from map).
            try
            {
                var atlas = GeoAtlas.BuiltIn();
                foreach (var r in order) if (r.Location == null) r.Location = atlas.Locate(r.City, r.State, r.Country);
            }
            catch { }
            await AreaChooserView.PrepareForAdding(this, order); // BrandMeister's own talkgroups for its repeaters, as Add from map does
            Result = OnlineImporter.AddRepeaters(p, order, o, bm);
            if (chkNoaa.IsChecked == true) NoaaAdded = Presets.AddNoaaWeather(p);
            Close(true);
        }

        /// <summary>Shows the window and reports what was added. Returns the first added repeater (or null).</summary>
        public static async Task<Repeater> Run(Window owner, Session session)
        {
            var w = new OnlineRepeaterWindow(session);
            if (!await w.ShowDialog<bool>(owner) || w.Result == null) return null;
            var r = w.Result;
            if (r.Added.Count > 0 || w.NoaaAdded.Count > 0) session.MarkDirty();
            session.NotifyTalkgroupsChanged();
            var lines = new List<string>();
            if (r.NewTalkgroups.Count > 0)
                lines.Add("New talkgroups: " + string.Join(", ", r.NewTalkgroups.Select(t => t.Name + " (" + t.Id + ")")) + ".");
            lines.AddRange(r.Notes);
            string head = "Added " + r.Added.Count + " repeater" + (r.Added.Count == 1 ? "" : "s") + " (" + r.Channels + " channels)" +
                          (w.NoaaAdded.Count > 0 ? " and " + w.NoaaAdded.Count + " NOAA weather channels" : "") +
                          ". Each repeater's zone, talkgroups and slots can be changed on the Repeaters tab.";
            await Dialogs.List(owner, head, new string[0], lines, false);
            return r.Added.Concat(w.NoaaAdded).FirstOrDefault();
        }
    }
}
