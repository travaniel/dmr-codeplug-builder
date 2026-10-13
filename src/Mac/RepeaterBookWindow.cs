using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Repeaters > From RepeaterBook on the Mac (the Windows RepeaterBookDialog): downloads DMR and analog repeaters with the
    /// user's own token (up to 5 states per click, optionally narrowed by county, city, callsign or frequency; listings in
    /// memory only while the window is open), lists them with ticks, and adds the ticked ones through OnlineImporter.
    /// The credit and the detail-page links are RepeaterBook's terms.
    /// </summary>
    sealed class RepeaterBookWindow : Window
    {
        readonly Session session;
        readonly TextBox txtStates, txtCounty, txtCity, txtCall, txtMHz, txtZone;
        readonly CheckBox chkDmr, chkAnalog, chkStarter;
        readonly RadioButton optOne, optCounty, optCity;
        readonly ComboBox cboPower;
        readonly TextBlock lblToken, lblFile, lblStatus;
        readonly Button btnDownload, btnStop, btnAdd;
        readonly TextBlock linkDetail;
        readonly DataGrid list;
        readonly GridSorter sorter;
        CancellationTokenSource downloadCancel;
        List<OnlineRepeater> all = new List<OnlineRepeater>();
        List<Row> rows = new List<Row>();
        bool loading, zoneTyped;

        public OnlineImportResult Result { get; private set; }

        sealed class Row : RowBase
        {
            readonly RepeaterBookWindow owner;
            public readonly OnlineRepeater R;
            public string Problem;
            public Repeater Existing;
            bool pick;
            public Row(RepeaterBookWindow owner, OnlineRepeater r, string problem, Repeater existing)
            {
                this.owner = owner; R = r; Problem = problem; Existing = existing; pick = problem == null && existing == null;
            }
            public bool Pick { get { return pick; } set { pick = value; owner.UpdateStatus(); } }
            public string Callsign => R.Callsign;
            public string Type => R.IsAnalog ? "FM" : "DMR";
            public string City => R.City;
            public string County => R.Location?.County != null ? ZonePlanner.CountyZone(R.Location.County.Name) : "";
            public string Output => R.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture);
            public decimal OutputMHz => R.RxMHz;
            public string Input => R.TxMHz.ToString("0.0000", CultureInfo.InvariantCulture);
            public decimal InputMHz => R.TxMHz;
            public string Signal
            {
                get
                {
                    if (R.Analog == null) return "CC " + R.ColorCode.ToString(CultureInfo.InvariantCulture);
                    var c = R.Analog;
                    return c.ToneEncode == "Off" && c.ToneDecode == "Off" ? "no tone"
                         : c.ToneSquelch && c.ToneDecode == c.ToneEncode ? c.ToneEncode + " both ways"
                         : c.ToneDecode == "Off" ? c.ToneEncode + " out" : c.ToneEncode + " / " + c.ToneDecode;
                }
            }
            public string Use => R.Status;
            public string Note => Problem ?? (Existing != null ? "already in project: " + Existing.Name : "");
        }

        public RepeaterBookWindow(Session session)
        {
            this.session = session;
            Title = "Add repeaters from RepeaterBook";
            Width = 1060; Height = 760; MinWidth = 780; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var top = new StackPanel { Spacing = 8 };
            top.Children.Add(UiKit.Hint("Download DMR and analog (FM) repeaters from RepeaterBook with your own RepeaterBook token, up to " +
                                        RepeaterBookApi.MaxStatesPerAction + " states at a time, then tick the ones you want. RepeaterBook doesn't list " +
                                        "DMR talkgroups: DMR repeaters get your zone's talkgroups, or the starter set below."));

            lblToken = UiKit.Label("");
            top.Children.Add(UiKit.Row(UiKit.Label("Your RepeaterBook token:"), lblToken,
                UiKit.Button("Paste token...", async () => { await RepeaterBookUi.ShowTokenWindow(this); ShowToken(); }),
                RepeaterBookUi.Link("Get a token on RepeaterBook", RepeaterBookApi.TokenPageUrl)));

            txtStates = new TextBox { Width = 260, Watermark = "e.g. Texas, Oklahoma", Text = AppSettings.Get("RepeaterBookStates") ?? AppSettings.Get("OnlineState") ?? "" };
            chkDmr = new CheckBox { Content = "DMR", IsChecked = AppSettings.Get("RepeaterBookDmr") != "0" };
            chkAnalog = new CheckBox { Content = "Analog (FM)", IsChecked = AppSettings.Get("RepeaterBookAnalog") != "0" };
            btnDownload = UiKit.Button("Download", Download);
            btnDownload.FontWeight = FontWeight.SemiBold;
            btnStop = UiKit.Button("Stop", () => downloadCancel?.Cancel());
            btnStop.IsEnabled = false;
            top.Children.Add(UiKit.Row(UiKit.Label("States (up to " + RepeaterBookApi.MaxStatesPerAction + ")"), txtStates, chkDmr, chkAnalog, btnDownload, btnStop));

            TextBox Search(string cue, double width) { var b = new TextBox { Width = width, Watermark = cue }; b.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Download(); } }; return b; }
            txtCounty = Search("e.g. Brown", 130);
            txtCity = Search("e.g. Brownwood", 130);
            txtCall = Search("e.g. W5* or K5ABC", 130);
            txtMHz = Search("e.g. 146.94", 90);
            txtStates.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Download(); } };
            top.Children.Add(UiKit.Row(UiKit.Label("Narrow it down (optional):"), UiKit.Label("County"), txtCounty, UiKit.Label("City"), txtCity,
                                       UiKit.Label("Callsign"), txtCall, UiKit.Label("MHz"), txtMHz));

            lblFile = new TextBlock { Text = "Type your states and click Download.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            top.Children.Add(lblFile);

            list = UiKit.Grid(false);
            list.Columns.Add(new DataGridCheckBoxColumn { Header = "Add", Binding = new Avalonia.Data.Binding("Pick"), Width = new DataGridLength(60), CanUserSort = false });
            list.Columns.Add(UiKit.Col("Callsign", "Callsign", true, 90));
            list.Columns.Add(UiKit.Col("Type", "Type", true, 74));
            list.Columns.Add(UiKit.Col("City", "City", true, 130));
            list.Columns.Add(UiKit.Col("County", "County", true, 110));
            var output = UiKit.Col("Output MHz", "Output", true, 118); output.SortMemberPath = "OutputMHz"; list.Columns.Add(output);
            var input = UiKit.Col("Input MHz", "Input", true, 110); input.SortMemberPath = "InputMHz"; list.Columns.Add(input);
            list.Columns.Add(UiKit.Col("Color code / tone", "Signal", true, 160));
            list.Columns.Add(UiKit.Col("Use", "Use", true, 92));
            list.Columns.Add(UiKit.Col("", "Note", true, 220));
            sorter = new GridSorter(list);
            list.SelectionChanged += (s, e) => ShowDetailLink();
            list.DoubleTapped += (s, e) => OpenSelectedDetail();

            // RepeaterBook's terms: the credit and a link wherever its data is shown, and each repeater's page when practical.
            linkDetail = RepeaterBookUi.Link("Open the selected repeater on RepeaterBook", OpenSelectedDetail);
            lblStatus = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            var credits = UiKit.Row(RepeaterBookUi.Link(RepeaterBookApi.Attribution, RepeaterBookApi.SiteUrl), linkDetail);
            credits.Spacing = 18;

            var group = "rbzones" + GetHashCode();
            optOne = new RadioButton { Content = "All in one zone:", GroupName = group };
            txtZone = new TextBox { Width = 170, MaxLength = 16 };
            optCounty = new RadioButton { Content = "One zone per county (towns the map doesn't place go in the zone above)", GroupName = group, IsChecked = true };
            optCity = new RadioButton { Content = "One zone per city", GroupName = group };
            cboPower = new ComboBox { ItemsSource = Powers.Values, SelectedItem = "High", Width = 100 };
            chkStarter = new CheckBox { Content = "DMR repeaters get the starter talkgroups (your state, USA, North America, Worldwide, Local on slot 2, Parrot)", IsChecked = true };
            var opts = new StackPanel { Spacing = 4, Children = { UiKit.Row(optOne, txtZone, UiKit.Label("Transmit power"), cboPower), optCounty, optCity, chkStarter } };

            btnAdd = new Button { Content = "Add repeaters", IsEnabled = false, IsDefault = true, FontWeight = FontWeight.SemiBold };
            btnAdd.Click += async (s, e) => await AddPicked();
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Click += (s, e) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { btnAdd, cancel } };

            var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0), Children = { credits, lblStatus, opts, buttons } };
            var dock = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(bottom); dock.Children.Add(list);
            Content = dock;

            UiKit.OnText(txtZone, () => { if (!loading) zoneTyped = true; UpdateStatus(); });
            foreach (var o in new[] { optOne, optCounty, optCity }) o.IsCheckedChanged += (s, e) => UpdateStatus();
            Closed += (s, e) => downloadCancel?.Cancel();
            ShowToken();
            ShowDetailLink();
        }

        void ShowToken()
        {
            lblToken.Text = RepeaterBookUi.Status;
            if (RepeaterBookToken.IsSet) lblToken.Foreground = Brushes.ForestGreen; else lblToken.ClearValue(TextBlock.ForegroundProperty); // null would hide the text
        }

        OnlineRepeater Selected => (list.SelectedItem as Row)?.R;

        void ShowDetailLink() { linkDetail.IsVisible = Selected?.DetailUrl != null; }

        void OpenSelectedDetail()
        {
            string url = Selected?.DetailUrl;
            if (url != null) RepeaterBookUi.Open(url);
        }

        /// <summary>The typed states as full US state names (names or two-letter codes); null with <paramref name="problem"/> set.</summary>
        List<string> TypedStates(out string problem)
        {
            problem = null;
            var states = new List<string>();
            foreach (string part in (txtStates.Text ?? "").Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                string name = BrandMeister.UsStates.Select(s => s.Key).FirstOrDefault(s => s.Equals(part, StringComparison.OrdinalIgnoreCase))
                           ?? Naming.StateCodes.Where(kv => kv.Value.Equals(part, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key)
                                               .FirstOrDefault(k => BrandMeister.UsStates.Any(s => s.Key == k));
                if (name == null) { problem = "\"" + part + "\" isn't a US state. Type state names or codes, like: Texas, OK"; return null; }
                if (!states.Contains(name)) states.Add(name);
            }
            if (states.Count == 0) { problem = "Type the states to download, like: Texas, Oklahoma"; return null; }
            if (states.Count > RepeaterBookApi.MaxStatesPerAction)
            {
                problem = "Up to " + RepeaterBookApi.MaxStatesPerAction + " states at a time (RepeaterBook's terms for this program). You typed " + states.Count + ".";
                return null;
            }
            return states;
        }

        /// <summary>Downloads the typed states with the user's token; the listings replace the list (kept in memory only).</summary>
        async void Download()
        {
            if (downloadCancel != null) return;
            var states = TypedStates(out string problem);
            if (states == null) { await Dialogs.Error(this, problem); return; }
            var modes = new List<string>();
            if (chkDmr.IsChecked == true) modes.Add(RepeaterBookApi.DmrMode);
            if (chkAnalog.IsChecked == true) modes.Add(RepeaterBookApi.AnalogMode);
            if (modes.Count == 0) { await Dialogs.Error(this, "Tick DMR, Analog (FM), or both."); return; }
            var query = RepeaterBookQuery.Parse(txtCounty.Text, txtCity.Text, txtCall.Text, txtMHz.Text, out problem);
            if (query == null) { await Dialogs.Error(this, problem); return; }
            if (!RepeaterBookToken.IsSet) { await RepeaterBookUi.ShowTokenWindow(this); ShowToken(); if (!RepeaterBookToken.IsSet) return; }
            string blocked = Online.RepeaterBookBlocked();
            if (blocked != null) { await Dialogs.Error(this, blocked); return; }

            AppSettings.Set("RepeaterBookStates", string.Join(", ", states));
            AppSettings.Set("RepeaterBookDmr", chkDmr.IsChecked == true ? "1" : "0");
            AppSettings.Set("RepeaterBookAnalog", chkAnalog.IsChecked == true ? "1" : "0");
            downloadCancel = new CancellationTokenSource();
            btnDownload.IsEnabled = false;
            btnStop.IsEnabled = true;
            RepeaterBookDownload got = null;
            try
            {
                got = await Online.RepeaterBookStatesAsync(states, modes, new Progress<string>(s => lblFile.Text = s), downloadCancel.Token, query);
            }
            catch (OperationCanceledException) { lblFile.Text = "Stopped."; }
            catch (Exception ex) { await Dialogs.Error(this, "RepeaterBook download failed:\n\n" + ex.Message); }
            finally
            {
                downloadCancel = null;
                btnDownload.IsEnabled = true;
                btnStop.IsEnabled = false;
            }
            if (got == null) return;
            if (got.Listings.Count > 0) LoadListings(got.Listings);
            lblFile.Text = "From RepeaterBook" + (query.IsEmpty ? "" : " (" + query.Describe() + ")") + ": " + string.Join(" ", got.Notes) +
                           (got.Listings.Count == 0 && got.Failure == null ? " Nothing matched." : "");
            if (got.Failure != null) await Dialogs.Error(this, got.Failure.Message + (got.Listings.Count > 0 ? "\n\nWhat was downloaded before that is listed." : ""));
        }

        /// <summary>Lists downloaded repeaters (they replace the list), sorted by state, county, town and callsign.</summary>
        internal void LoadListings(List<OnlineRepeater> listings)
        {
            all = listings.OrderBy(r => r.State).ThenBy(r => r.Location?.County?.Name ?? "~").ThenBy(r => r.City).ThenBy(r => r.Callsign).ToList();
            if (!zoneTyped)
            {
                loading = true;
                txtZone.Text = RepeaterBookImport.SuggestZone(all);
                loading = false;
            }
            rows = all.Select(r =>
            {
                string problem = r.Analog != null ? RepeaterBookImport.Problem(r.Analog) : r.Status == "OFF-AIR" ? "off the air (RepeaterBook)" : !r.InRadioBand ? "outside the radio's bands" : null;
                return new Row(this, r, problem, problem == null ? OnlineImporter.FindExisting(session.Project, r) : null);
            }).ToList();
            sorter.Show(rows);
            UpdateStatus();
        }

        List<OnlineRepeater> Picked()
        {
            return rows.Where(r => r.Pick && r.Problem == null && OnlineImporter.FindExisting(session.Project, r.R) == null).Select(r => r.R).ToList();
        }

        void UpdateStatus()
        {
            if (btnAdd == null) return;
            int n = Picked().Count;
            btnAdd.IsEnabled = n > 0 && (Naming.Clean(txtZone.Text ?? "", 16).Length > 0 || optCity.IsChecked == true);
            btnAdd.Content = n == 0 ? "Add repeaters" : "Add " + n + " repeater" + (n == 1 ? "" : "s");
            int unplaced = all.Count(r => r.Location?.County == null);
            lblStatus.Text = all.Count == 0 ? "" : unplaced == 0 ? "Every repeater was placed on the map."
                : unplaced + " of " + all.Count + " repeaters couldn't be placed in a county; with one zone per county they go in the zone you type.";
        }

        internal async Task AddPicked()
        {
            var picked = Picked();
            if (picked.Count == 0) return;
            btnAdd.IsEnabled = false;
            IDictionary<int, string> bm = null;
            if (chkStarter.IsChecked == true && picked.Any(r => !r.IsAnalog))
                try { bm = await Online.BrandMeisterNamesAsync(); } catch { } // names only; the starter set works without them

            string one = Naming.Fit(txtZone.Text ?? "", 16);
            if (one.Length == 0) one = RepeaterBookImport.Site;
            var scheme = optCounty.IsChecked == true ? ZoneScheme.County : optCity.IsChecked == true ? ZoneScheme.City : (ZoneScheme?)null;
            string ZoneFor(Repeater rep)
            {
                if (optCity.IsChecked == true) return string.IsNullOrEmpty(rep.City) ? one : Naming.Fit(rep.City, 16);
                if (optCounty.IsChecked == true && !string.IsNullOrEmpty(rep.County)) return ZonePlanner.CountyZone(rep.County);
                return one;
            }

            // Each state's DMR repeaters get that state's statewide talkgroup in the starter set.
            var result = new OnlineImportResult();
            foreach (var group in picked.GroupBy(r => r.State))
            {
                var o = new OnlineImportOptions
                {
                    ZonePerCity = false, Zone = one, Power = cboPower.SelectedItem as string ?? "High", ZoneFor = ZoneFor, Scheme = scheme,
                    DefaultTalkgroups = chkStarter.IsChecked == true ? OnlineImporter.SuggestedDefaults(group.Key, bm) : new List<TalkgroupChoice>(),
                };
                var r = OnlineImporter.AddRepeaters(session.Project, group, o, bm);
                result.Added.AddRange(r.Added);
                result.NewTalkgroups.AddRange(r.NewTalkgroups);
                result.Notes.AddRange(r.Notes);
            }
            if (picked.Any(r => !r.IsAnalog && (r.Details ?? "").Contains(RepeaterBookApi.NoColorCode)))
                result.Notes.Add("Some DMR repeaters don't list a color code on RepeaterBook, so they got color code 1: check them.");
            Result = result;
            Close(true);
        }

        /// <summary>Shows the window and reports what was added. Returns the first added repeater (or null).</summary>
        public static async Task<Repeater> Run(Window owner, Session session)
        {
            var w = new RepeaterBookWindow(session);
            if (!await w.ShowDialog<bool>(owner) || w.Result == null) return null;
            var r = w.Result;
            if (r.Added.Count > 0) session.MarkDirty();
            if (r.NewTalkgroups.Count > 0) session.NotifyTalkgroupsChanged();
            int dmr = r.Added.Count(x => x.IsDigital), fm = r.Added.Count - dmr;
            string head = "Added " + (dmr > 0 ? dmr + " DMR" : "") + (dmr > 0 && fm > 0 ? " and " : "") + (fm > 0 ? fm + " analog" : "") +
                          (r.Added.Count == 0 ? "no" : "") + " repeater" + (r.Added.Count == 1 ? "" : "s") +
                          (r.Added.Count > 0 ? " to " + string.Join(", ", r.Added.Select(x => x.Zone).Distinct()) : "") +
                          ". Frequencies, tones and color codes come from RepeaterBook; check them on the Repeaters tab. " +
                          RepeaterBookApi.Attribution + " (" + RepeaterBookApi.SiteUrl + ")";
            await Dialogs.List(owner, head, new string[0], r.Notes, false);
            return r.Added.FirstOrDefault();
        }
    }
}
