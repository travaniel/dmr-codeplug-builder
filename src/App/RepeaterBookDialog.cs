using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Repeaters > From RepeaterBook: downloads DMR and analog repeaters with the user's own RepeaterBook token
    /// (Online.RepeaterBook.cs: up to 5 states per click, listings in memory only while the dialog is open), lists them with
    /// ticks, and adds the ticked ones through <see cref="OnlineImporter.AddRepeaters"/>. The credit and detail links are
    /// RepeaterBook's terms.
    /// </summary>
    sealed class RepeaterBookDialog : Form
    {
        readonly Session session;
        readonly ComboBox cboPower;
        readonly ListView list;
        readonly ListSorter sorter;
        readonly Label lblFile, lblStatus, lblToken;
        readonly TextBox txtStates, txtCounty, txtCity, txtCall, txtMHz;
        readonly CheckBox chkDmr, chkAnalog, chkStarter;
        readonly Button btnDownload, btnStop, btnAdd;
        readonly LinkLabel linkDetail;
        readonly RadioButton optOne, optCity, optCounty;
        readonly TextBox txtZone;
        CancellationTokenSource downloadCancel;

        List<OnlineRepeater> all = new List<OnlineRepeater>();
        bool loading, creatingHandle, zoneTyped;

        public OnlineImportResult Result { get; private set; }

        public RepeaterBookDialog(Session session)
        {
            this.session = session;
            Text = "Add repeaters from RepeaterBook";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Ui.S(1060), area.Width * 95 / 100), Math.Min(Ui.S(740), area.Height * 95 / 100));
            MinimumSize = new Size(Ui.S(780), Ui.S(520));
            Padding = new Padding(Ui.S(10));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
            for (int i = 0; i < 10; i++) root.RowStyles.Add(i == 5 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));

            root.Controls.Add(Ui.Hint("Download DMR and analog (FM) repeaters from RepeaterBook with your own RepeaterBook token, up to " +
                                      RepeaterBookApi.MaxStatesPerAction + " states at a time, then tick the ones you want. RepeaterBook doesn't list " +
                                      "DMR talkgroups: DMR repeaters get your zone's talkgroups, or the starter set below.", Ui.S(980)), 0, 0);

            lblToken = Ui.Label("");
            var getToken = new LinkLabel { Text = "Get a token on RepeaterBook", AutoSize = true, Margin = new Padding(3, Ui.S(7), 3, 3) };
            getToken.LinkClicked += (s, e) => RepeaterBookTokenDialog.Open(this, RepeaterBookApi.TokenPageUrl);
            root.Controls.Add(Ui.Row(Ui.Label("Your RepeaterBook token:"), lblToken, Ui.Button("Paste token...", (s, e) => { RepeaterBookTokenDialog.Show(this); ShowToken(); }), getToken), 0, 1);

            txtStates = Ui.Text("e.g. Texas, Oklahoma");
            txtStates.Width = Ui.S(260);
            txtStates.Text = AppSettings.Get("RepeaterBookStates") ?? AppSettings.Get("OnlineState") ?? "";
            txtStates.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Download(); } };
            chkDmr = new CheckBox { Text = "DMR", AutoSize = true, Checked = AppSettings.Get("RepeaterBookDmr") != "0", Margin = new Padding(Ui.S(8), Ui.S(6), 3, 3) };
            chkAnalog = new CheckBox { Text = "Analog (FM)", AutoSize = true, Checked = AppSettings.Get("RepeaterBookAnalog") != "0", Margin = new Padding(3, Ui.S(6), Ui.S(8), 3) };
            btnDownload = Ui.Button("Download", (s, e) => Download());
            btnDownload.Font = Ui.BoldFont;
            btnStop = Ui.Button("Stop", (s, e) => downloadCancel?.Cancel());
            btnStop.Enabled = false;
            root.Controls.Add(Ui.Row(Ui.Label("States (up to " + RepeaterBookApi.MaxStatesPerAction + ")"), txtStates, chkDmr, chkAnalog, btnDownload, btnStop), 0, 2);

            // Optional terms inside those states (RepeaterBook's own search fields); Enter in any of them downloads.
            txtCounty = SearchBox("e.g. Brown", 140);
            txtCity = SearchBox("e.g. Brownwood", 140);
            txtCall = SearchBox("e.g. W5* or K5ABC", 130);
            txtMHz = SearchBox("e.g. 146.94", 90);
            root.Controls.Add(Ui.Row(Ui.Label("Narrow it down (optional):"), Ui.Label("County"), txtCounty, Ui.Label("City"), txtCity,
                                     Ui.Label("Callsign"), txtCall, Ui.Label("MHz"), txtMHz), 0, 3);

            lblFile = new Label { AutoSize = true, ForeColor = Ui.HintColor, Margin = new Padding(3, Ui.S(6), 3, 3), Text = "Type your states and click Download." };
            root.Controls.Add(lblFile, 0, 4);

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false, Margin = new Padding(3, 6, 3, 6) };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Callsign", Ui.S(90));
            list.Columns.Add("Type", Ui.S(50));
            list.Columns.Add("City", Ui.S(130));
            list.Columns.Add("County", Ui.S(110));
            list.Columns.Add("Output MHz", Ui.S(84), HorizontalAlignment.Right);
            list.Columns.Add("Input MHz", Ui.S(84), HorizontalAlignment.Right);
            list.Columns.Add("Color code / tone", Ui.S(130));
            list.Columns.Add("Use", Ui.S(70));
            list.Columns.Add("", Ui.S(220));
            sorter = Ui.Sortable(list, Refill);
            root.Controls.Add(list, 0, 5);

            // RepeaterBook's terms: the credit and a link wherever its data is shown, and each repeater's page when practical.
            var credit = new LinkLabel { Text = RepeaterBookApi.Attribution, AutoSize = true, Margin = new Padding(3, 0, Ui.S(16), Ui.S(4)) };
            credit.LinkClicked += (s, e) => RepeaterBookTokenDialog.Open(this, RepeaterBookApi.SiteUrl);
            linkDetail = new LinkLabel { Text = "Open the selected repeater on RepeaterBook", AutoSize = true, Enabled = false, Margin = new Padding(3, 0, 3, Ui.S(4)) };
            linkDetail.LinkClicked += (s, e) => OpenSelectedDetail();
            root.Controls.Add(Ui.Row(credit, linkDetail), 0, 6);

            lblStatus = new Label { AutoSize = true, ForeColor = Ui.HintColor, Margin = new Padding(3, 0, 3, Ui.S(6)) };
            root.Controls.Add(lblStatus, 0, 7);

            var opts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            optOne = new RadioButton { Text = "All in one zone:", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
            txtZone = new TextBox { Width = Ui.S(170), MaxLength = 16, Anchor = AnchorStyles.Left };
            optCounty = new RadioButton { Text = "One zone per county (towns the map doesn't place go in the zone above)", AutoSize = true, Checked = true, Margin = new Padding(3, 2, 3, 2) };
            optCity = new RadioButton { Text = "One zone per city", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            cboPower = Ui.Combo(false, Powers.Values);
            cboPower.SelectedItem = "High";
            cboPower.Width = Ui.S(90);
            cboPower.Anchor = AnchorStyles.Left;
            chkStarter = new CheckBox { Text = "DMR repeaters get the starter talkgroups (your state, USA, North America, Worldwide, Local on slot 2, Parrot)", AutoSize = true, Checked = true, Margin = new Padding(3, Ui.S(6), 3, 2) };
            opts.Controls.Add(optOne, 0, 0);
            opts.Controls.Add(txtZone, 1, 0);
            opts.Controls.Add(Ui.Row(Ui.Label("Transmit power"), cboPower), 2, 0);
            opts.Controls.Add(optCounty, 0, 1);
            opts.SetColumnSpan(optCounty, 3);
            opts.Controls.Add(optCity, 0, 2);
            opts.SetColumnSpan(optCity, 3);
            opts.Controls.Add(chkStarter, 0, 3);
            opts.SetColumnSpan(chkStarter, 3);
            root.Controls.Add(opts, 0, 8);

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, Ui.S(8), 0, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnAdd = new Button { Text = "Add repeaters", AutoSize = true, Font = Ui.BoldFont, Enabled = false, Padding = new Padding(Ui.S(10), Ui.S(3), Ui.S(10), Ui.S(3)), UseVisualStyleBackColor = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Ui.S(6), Ui.S(3), Ui.S(6), Ui.S(3)), UseVisualStyleBackColor = true };
            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            right.Controls.Add(btnAdd);
            right.Controls.Add(cancel);
            buttons.Controls.Add(right, 1, 0);
            root.Controls.Add(buttons, 0, 9);
            Controls.Add(root);
            CancelButton = cancel;

            btnAdd.Click += async (s, e) => await AddPicked();
            txtZone.TextChanged += (s, e) => { if (!loading) zoneTyped = true; UpdateStatus(); };
            foreach (var opt in new[] { optOne, optCounty, optCity }) opt.CheckedChanged += (s, e) => UpdateStatus();
            // Windows raises ItemChecked for every item while the ListView creates its handle; ignore those.
            list.HandleCreated += (s, e) => { creatingHandle = true; list.BeginInvoke((Action)(() => creatingHandle = false)); };
            list.ItemChecked += (s, e) => { if (!loading && !creatingHandle && !sorter.Sorting) UpdateStatus(); };
            list.SelectedIndexChanged += (s, e) => linkDetail.Enabled = Selected()?.DetailUrl != null;
            list.DoubleClick += (s, e) => OpenSelectedDetail();
            FormClosed += (s, e) => downloadCancel?.Cancel();
            ShowToken();
        }

        TextBox SearchBox(string cue, int width)
        {
            var box = Ui.Text(cue);
            box.Width = Ui.S(width);
            box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Download(); } };
            return box;
        }

        OnlineRepeater Selected()
        {
            return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as OnlineRepeater : null;
        }

        void OpenSelectedDetail()
        {
            string url = Selected()?.DetailUrl;
            if (url != null) RepeaterBookTokenDialog.Open(this, url);
        }

        void ShowToken()
        {
            lblToken.Text = RepeaterBookTokenDialog.Status;
            lblToken.ForeColor = RepeaterBookToken.IsSet ? SystemColors.ControlText : Ui.HintColor;
        }

        /// <summary>The typed states as full US state names (names or two-letter codes), or null after telling the user what's wrong.</summary>
        List<string> TypedStates()
        {
            var states = new List<string>();
            foreach (string part in txtStates.Text.Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                string name = BrandMeister.UsStates.Select(s => s.Key).FirstOrDefault(s => s.Equals(part, StringComparison.OrdinalIgnoreCase))
                           ?? Naming.StateCodes.Where(kv => kv.Value.Equals(part, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key)
                                               .FirstOrDefault(k => BrandMeister.UsStates.Any(s => s.Key == k));
                if (name == null) { Ui.Error(this, "\"" + part + "\" isn't a US state. Type state names or codes, like: Texas, OK"); return null; }
                if (!states.Contains(name)) states.Add(name);
            }
            if (states.Count == 0) { Ui.Error(this, "Type the states to download, like: Texas, Oklahoma"); return null; }
            if (states.Count > RepeaterBookApi.MaxStatesPerAction)
            {
                Ui.Error(this, "Up to " + RepeaterBookApi.MaxStatesPerAction + " states at a time (RepeaterBook's terms for this program). You typed " + states.Count + ".");
                return null;
            }
            return states;
        }

        /// <summary>Downloads the typed states with the user's token; the listings replace the list (kept in memory only).</summary>
        async void Download()
        {
            if (downloadCancel != null) return;
            var states = TypedStates();
            if (states == null) return;
            var modes = new List<string>();
            if (chkDmr.Checked) modes.Add(RepeaterBookApi.DmrMode);
            if (chkAnalog.Checked) modes.Add(RepeaterBookApi.AnalogMode);
            if (modes.Count == 0) { Ui.Error(this, "Tick DMR, Analog (FM), or both."); return; }
            var query = RepeaterBookQuery.Parse(txtCounty.Text, txtCity.Text, txtCall.Text, txtMHz.Text, out string problem);
            if (query == null) { Ui.Error(this, problem); return; }
            if (!RepeaterBookToken.IsSet) { RepeaterBookTokenDialog.Show(this); ShowToken(); if (!RepeaterBookToken.IsSet) return; }
            string blocked = Online.RepeaterBookBlocked();
            if (blocked != null) { Ui.Error(this, blocked); return; }

            AppSettings.Set("RepeaterBookStates", string.Join(", ", states));
            AppSettings.Set("RepeaterBookDmr", chkDmr.Checked ? "1" : "0");
            AppSettings.Set("RepeaterBookAnalog", chkAnalog.Checked ? "1" : "0");
            downloadCancel = new CancellationTokenSource();
            btnDownload.Enabled = false;
            btnStop.Enabled = true;
            lblFile.ForeColor = SystemColors.ControlText;
            RepeaterBookDownload got = null;
            try
            {
                got = await Online.RepeaterBookStatesAsync(states, modes, new Progress<string>(s => lblFile.Text = s), downloadCancel.Token, query);
            }
            catch (OperationCanceledException) { lblFile.Text = "Stopped."; }
            catch (Exception ex) { Ui.Error(this, "RepeaterBook download failed:\n\n" + ex.Message); }
            finally
            {
                downloadCancel = null;
                if (!IsDisposed) { btnDownload.Enabled = true; btnStop.Enabled = false; }
            }
            if (got == null || IsDisposed) return;
            if (got.Listings.Count > 0) LoadListings(got.Listings);
            lblFile.Text = "From RepeaterBook" + (query.IsEmpty ? "" : " (" + query.Describe() + ")") + ": " + string.Join(" ", got.Notes) +
                           (got.Listings.Count == 0 && got.Failure == null ? " Nothing matched." : "");
            if (got.Failure != null) Ui.Error(this, got.Failure.Message + (got.Listings.Count > 0 ? "\n\nWhat was downloaded before that is listed." : ""));
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
            Refill();
        }

        /// <summary>Why a listing can't be added (or isn't ticked), or null.</summary>
        string Problem(OnlineRepeater r)
        {
            if (r.Analog != null) return RepeaterBookImport.Problem(r.Analog);
            if (r.Status == "OFF-AIR") return "off the air (RepeaterBook)";
            if (!r.InRadioBand) return "outside the radio's bands";
            return null;
        }

        void Refill()
        {
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in all)
            {
                string problem = Problem(r);
                var existing = problem == null ? OnlineImporter.FindExisting(session.Project, r) : null;
                string signal;
                if (r.Analog != null)
                {
                    var c = r.Analog;
                    signal = c.ToneEncode == "Off" && c.ToneDecode == "Off" ? "no tone"
                           : c.ToneSquelch && c.ToneDecode == c.ToneEncode ? c.ToneEncode + " both ways"
                           : c.ToneDecode == "Off" ? c.ToneEncode + " out" : c.ToneEncode + " / " + c.ToneDecode;
                }
                else signal = "CC " + r.ColorCode.ToString(CultureInfo.InvariantCulture);
                var it = new ListViewItem(new[]
                {
                    r.Callsign, r.IsAnalog ? "FM" : "DMR", r.City,
                    r.Location?.County != null ? ZonePlanner.CountyZone(r.Location.County.Name) : "",
                    r.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture), r.TxMHz.ToString("0.0000", CultureInfo.InvariantCulture),
                    signal, r.Status, problem ?? (existing != null ? "already in project: " + existing.Name : ""),
                }) { Tag = r, Checked = problem == null && existing == null };
                if (problem != null || existing != null) it.ForeColor = Ui.HintColor;
                list.Items.Add(it);
            }
            list.EndUpdate();
            sorter.Apply();
            loading = false;
            UpdateStatus();
        }

        List<OnlineRepeater> Picked()
        {
            return list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (OnlineRepeater)i.Tag)
                       .Where(r => Problem(r) == null && OnlineImporter.FindExisting(session.Project, r) == null).ToList();
        }

        void UpdateStatus()
        {
            var picked = Picked();
            int n = picked.Count;
            btnAdd.Enabled = n > 0 && (Naming.Clean(txtZone.Text, 16).Length > 0 || optCity.Checked);
            btnAdd.Text = n == 0 ? "Add repeaters" : "Add " + n + " repeater" + (n == 1 ? "" : "s");
            int unplaced = all.Count(r => r.Location?.County == null);
            lblStatus.Text = all.Count == 0 ? "" : unplaced == 0 ? "Every repeater was placed on the map."
                : unplaced + " of " + all.Count + " repeaters couldn't be placed in a county; with one zone per county they go in the zone you type.";
        }

        internal async Task AddPicked()
        {
            var picked = Picked();
            if (picked.Count == 0) return;
            btnAdd.Enabled = false;
            IDictionary<int, string> bm = null;
            if (chkStarter.Checked && picked.Any(r => !r.IsAnalog))
                try { bm = await Online.BrandMeisterNamesAsync(); } catch { } // names only; the starter set works without them

            string one = Naming.Fit(txtZone.Text, 16);
            if (one.Length == 0) one = RepeaterBookImport.Site;
            var scheme = optCounty.Checked ? ZoneScheme.County : optCity.Checked ? ZoneScheme.City : (ZoneScheme?)null;
            string ZoneFor(Repeater rep)
            {
                if (optCity.Checked) return string.IsNullOrEmpty(rep.City) ? one : Naming.Fit(rep.City, 16);
                if (optCounty.Checked && !string.IsNullOrEmpty(rep.County)) return ZonePlanner.CountyZone(rep.County);
                return one;
            }

            // Each state's DMR repeaters get that state's statewide talkgroup in the starter set.
            var result = new OnlineImportResult();
            foreach (var group in picked.GroupBy(r => r.State))
            {
                var o = new OnlineImportOptions
                {
                    ZonePerCity = false, Zone = one, Power = (string)cboPower.SelectedItem ?? "High", ZoneFor = ZoneFor, Scheme = scheme,
                    DefaultTalkgroups = chkStarter.Checked ? OnlineImporter.SuggestedDefaults(group.Key, bm) : new List<TalkgroupChoice>(),
                };
                var r = OnlineImporter.AddRepeaters(session.Project, group, o, bm);
                result.Added.AddRange(r.Added);
                result.NewTalkgroups.AddRange(r.NewTalkgroups);
                result.Notes.AddRange(r.Notes);
            }
            if (picked.Any(r => !r.IsAnalog && (r.Details ?? "").Contains(RepeaterBookApi.NoColorCode)))
                result.Notes.Add("Some DMR repeaters don't list a color code on RepeaterBook, so they got color code 1: check them.");
            Result = result;
            DialogResult = DialogResult.OK;
        }

        /// <summary>Shows the dialog and reports what was added. Returns the first added repeater (or null).</summary>
        public static Repeater Run(IWin32Window owner, Session session)
        {
            using (var d = new RepeaterBookDialog(session))
            {
                if (d.ShowDialog(owner) != DialogResult.OK || d.Result == null) return null;
                var r = d.Result;
                if (r.Added.Count > 0) session.MarkDirty();
                if (r.NewTalkgroups.Count > 0) session.NotifyTalkgroupsChanged();
                int dmr = r.Added.Count(x => x.IsDigital), fm = r.Added.Count - dmr;
                string head = "Added " + (dmr > 0 ? dmr + " DMR" : "") + (dmr > 0 && fm > 0 ? " and " : "") + (fm > 0 ? fm + " analog" : "") +
                              (r.Added.Count == 0 ? "no" : "") + " repeater" + (r.Added.Count == 1 ? "" : "s") +
                              (r.Added.Count > 0 ? " to " + string.Join(", ", r.Added.Select(x => x.Zone).Distinct()) : "") +
                              ". Frequencies, tones and color codes come from RepeaterBook; check them on the Repeaters tab. " +
                              RepeaterBookApi.Attribution + " (" + RepeaterBookApi.SiteUrl + ")";
                using (var dlg = new IssuesDialog(head, new string[0], r.Notes, false)) dlg.ShowDialog(owner);
                return r.Added.FirstOrDefault();
            }
        }
    }
}
