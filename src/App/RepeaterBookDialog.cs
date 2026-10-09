using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Repeaters > From RepeaterBook: opens repeaterbook.com in the browser, the user exports a search in CHIRP format,
    /// and the file (picked up from Downloads, or chosen) becomes analog repeaters with zones (Core/RepeaterBook.cs).
    /// No API token needed.
    /// </summary>
    sealed class RepeaterBookDialog : Form
    {
        readonly Session session;
        readonly ComboBox cboState, cboPower;
        readonly ListView list;
        readonly Label lblFile, lblStatus;
        readonly RadioButton optOne, optCity, optCounty;
        readonly TextBox txtZone;
        readonly Button btnAdd;
        readonly Timer watch = new Timer { Interval = 1500 };
        readonly DateTime opened = DateTime.Now;
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<ChirpChannel> all = new List<ChirpChannel>();
        readonly Dictionary<ChirpChannel, GeoLocation> where = new Dictionary<ChirpChannel, GeoLocation>();
        bool loading, creatingHandle, zoneTyped;

        public RepeaterBookResult Result { get; private set; }

        static string DownloadsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        public RepeaterBookDialog(Session session)
        {
            this.session = session;
            Text = "Add repeaters from RepeaterBook";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Ui.S(1000), area.Width * 95 / 100), Math.Min(Ui.S(720), area.Height * 95 / 100));
            MinimumSize = new Size(Ui.S(760), Ui.S(520));
            Padding = new Padding(Ui.S(10));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(Ui.Hint("1. Click Open RepeaterBook and sign in there. 2. Search for your county or city. 3. Export the results " +
                                      "in CHIRP format. The file is picked up from your Downloads folder as soon as it lands (or click Choose file). " +
                                      "Analog (FM) repeaters only; DMR repeaters come from Add from map / Find online. " + RepeaterBookApi.Attribution, Ui.S(940)), 0, 0);

            cboState = Ui.Combo(true, BrandMeister.UsStates.Select(s => s.Key).ToArray());
            cboState.Width = Ui.S(160);
            cboState.Anchor = AnchorStyles.Left;
            cboState.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cboState.AutoCompleteSource = AutoCompleteSource.ListItems;
            cboState.Text = AppSettings.Get("OnlineState") ?? "Texas";
            var open = Ui.Button("Open RepeaterBook", (s, e) => OpenSite());
            open.Font = Ui.BoldFont;
            root.Controls.Add(Ui.Row(open, Ui.Button("Choose file...", (s, e) => ChooseFile()),
                                     Ui.Label("State (the export doesn't say)"), cboState), 0, 1);

            lblFile = new Label { AutoSize = true, ForeColor = Ui.HintColor, Margin = new Padding(3, Ui.S(6), 3, 3), Text = "Waiting for a CHIRP export in " + DownloadsFolder + "..." };
            root.Controls.Add(lblFile, 0, 2);

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false, Margin = new Padding(3, 6, 3, 6) };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Channel name", Ui.S(140));
            list.Columns.Add("Callsign", Ui.S(80));
            list.Columns.Add("City", Ui.S(140));
            list.Columns.Add("Output MHz", Ui.S(84), HorizontalAlignment.Right);
            list.Columns.Add("Input MHz", Ui.S(84), HorizontalAlignment.Right);
            list.Columns.Add("Tone", Ui.S(100));
            list.Columns.Add("County", Ui.S(110));
            list.Columns.Add("", Ui.S(200));
            root.Controls.Add(list, 0, 3);

            var opts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            optOne = new RadioButton { Text = "All in one zone:", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 2) };
            txtZone = new TextBox { Width = Ui.S(170), MaxLength = 16, Anchor = AnchorStyles.Left };
            optCounty = new RadioButton { Text = "One zone per county (towns the map doesn't know go in the zone above)", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            optCity = new RadioButton { Text = "One zone per city", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            cboPower = Ui.Combo(false, Powers.Values);
            cboPower.SelectedItem = "High";
            cboPower.Width = Ui.S(90);
            cboPower.Anchor = AnchorStyles.Left;
            opts.Controls.Add(optOne, 0, 0);
            opts.Controls.Add(txtZone, 1, 0);
            opts.Controls.Add(Ui.Row(Ui.Label("Transmit power"), cboPower), 2, 0);
            opts.Controls.Add(optCounty, 0, 1);
            opts.SetColumnSpan(optCounty, 3);
            opts.Controls.Add(optCity, 0, 2);
            opts.SetColumnSpan(optCity, 3);
            lblStatus = new Label { AutoSize = true, ForeColor = Ui.HintColor, Margin = new Padding(3, 0, 3, Ui.S(6)) };
            root.Controls.Add(lblStatus, 0, 4);
            root.Controls.Add(opts, 0, 5);

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, Ui.S(8), 0, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnAdd = new Button { Text = "Add repeaters", AutoSize = true, Font = Ui.BoldFont, Enabled = false, Padding = new Padding(Ui.S(10), Ui.S(3), Ui.S(10), Ui.S(3)), UseVisualStyleBackColor = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Ui.S(6), Ui.S(3), Ui.S(6), Ui.S(3)), UseVisualStyleBackColor = true };
            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            right.Controls.Add(btnAdd);
            right.Controls.Add(cancel);
            buttons.Controls.Add(right, 1, 0);
            root.Controls.Add(buttons, 0, 6);
            Controls.Add(root);
            CancelButton = cancel;

            btnAdd.Click += (s, e) => AddPicked();
            txtZone.TextChanged += (s, e) => { if (!loading) zoneTyped = true; UpdateStatus(); };
            foreach (var opt in new[] { optOne, optCounty, optCity }) opt.CheckedChanged += (s, e) => UpdateStatus(); // per city needs no zone name
            cboState.SelectedIndexChanged += (s, e) => Relocate();
            cboState.Leave += (s, e) => Relocate();
            // Windows raises ItemChecked for every item while the ListView creates its handle; ignore those.
            list.HandleCreated += (s, e) => { creatingHandle = true; list.BeginInvoke((Action)(() => creatingHandle = false)); };
            list.ItemChecked += (s, e) => { if (!loading && !creatingHandle) UpdateStatus(); };
            watch.Tick += (s, e) => LookForDownload();
            Shown += (s, e) => watch.Start();
            FormClosed += (s, e) => watch.Dispose();
        }

        void OpenSite()
        {
            try { Process.Start(RepeaterBookImport.HomeUrl); }
            catch (Exception ex) { Ui.Error(this, "Couldn't open the browser:\n\n" + ex.Message + "\n\nGo to " + RepeaterBookImport.HomeUrl + " yourself."); }
        }

        void ChooseFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Pick the CHIRP export from RepeaterBook",
                Filter = "CHIRP CSV (*.csv)|*.csv|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(DownloadsFolder) ? DownloadsFolder : "",
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadFile(dlg.FileName, true);
            }
        }

        /// <summary>A CHIRP export that appeared in Downloads since the dialog opened.</summary>
        void LookForDownload()
        {
            try
            {
                if (!Directory.Exists(DownloadsFolder)) return;
                var newest = new DirectoryInfo(DownloadsFolder).GetFiles("*.csv")
                    .Where(f => f.LastWriteTime >= opened.AddSeconds(-5) && !seen.Contains(f.FullName))
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (newest == null) return;
                seen.Add(newest.FullName);
                LoadFile(newest.FullName, false);
            }
            catch { }
        }

        internal void LoadFile(string path, bool chosen)
        {
            List<ChirpChannel> found;
            var notes = new List<string>();
            try { found = ChirpCsv.Parse(CsvTable.Load(path), notes); }
            catch (Exception ex)
            {
                if (chosen) Ui.Error(this, "Couldn't read " + Path.GetFileName(path) + ":\n\n" + ex.Message);
                return;
            }
            if (found.Count == 0)
            {
                if (chosen) Ui.Error(this, Path.GetFileName(path) + " has no CHIRP channels. " + string.Join(" ", notes) +
                                           "\n\nOn RepeaterBook, export the search results in CHIRP format.");
                return; // some other CSV landed in Downloads: keep waiting
            }
            all = found;
            lblFile.Text = "From " + Path.GetFileName(path) + ": " + all.Count + " channel" + (all.Count == 1 ? "" : "s") + (notes.Count > 0 ? ". " + string.Join(" ", notes) : ".");
            lblFile.ForeColor = SystemColors.ControlText;
            Relocate();
            Activate();
        }

        /// <summary>Places every city on the built-in map within the chosen state, then refills the list.</summary>
        void Relocate()
        {
            if (all.Count == 0) return;
            where.Clear();
            GeoAtlas atlas = null;
            try { atlas = GeoAtlas.BuiltIn(); } catch { }
            foreach (var c in all) where[c] = RepeaterBookImport.Locate(atlas, c, cboState.Text.Trim());
            if (!zoneTyped)
            {
                loading = true;
                txtZone.Text = RepeaterBookImport.SuggestZone(where.Values, all);
                loading = false;
            }
            Refill();
        }

        void Refill()
        {
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var c in all)
            {
                string problem = RepeaterBookImport.Problem(c);
                var existing = problem == null ? RepeaterBookImport.FindExisting(session.Project, c) : null;
                string tone = c.ToneEncode == "Off" && c.ToneDecode == "Off" ? "none"
                            : c.ToneSquelch && c.ToneDecode == c.ToneEncode ? c.ToneEncode + " both ways"
                            : c.ToneDecode == "Off" ? c.ToneEncode + " out" : c.ToneEncode + " / " + c.ToneDecode;
                var it = new ListViewItem(new[]
                {
                    RepeaterBookImport.ChannelName(c, all), c.Name, c.Comment,
                    c.RxMHz.ToString("0.0000", CultureInfo.InvariantCulture),
                    c.RxOnly ? "receive only" : c.TxMHz.ToString("0.0000", CultureInfo.InvariantCulture),
                    tone, where.TryGetValue(c, out var loc) && loc.County != null ? ZonePlanner.CountyZone(loc.County.Name) : "",
                    problem ?? (existing != null ? "already in project: " + existing.Name : ""),
                }) { Tag = c, Checked = problem == null && existing == null };
                if (problem != null || existing != null) it.ForeColor = Ui.HintColor;
                list.Items.Add(it);
            }
            list.EndUpdate();
            loading = false;
            UpdateStatus();
        }

        List<ChirpChannel> Picked()
        {
            return list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (ChirpChannel)i.Tag).ToList();
        }

        void UpdateStatus()
        {
            int n = Picked().Count(c => RepeaterBookImport.Problem(c) == null && RepeaterBookImport.FindExisting(session.Project, c) == null);
            btnAdd.Enabled = n > 0 && (Naming.Clean(txtZone.Text, 16).Length > 0 || optCity.Checked);
            btnAdd.Text = n == 0 ? "Add repeaters" : "Add " + n + " repeater" + (n == 1 ? "" : "s");
            int unplaced = all.Count(c => where.TryGetValue(c, out var l) && l.County == null);
            lblStatus.Text = all.Count == 0 ? "" : unplaced == 0 ? "Every city was found on the map."
                : unplaced + " of " + all.Count + " towns aren't on the built-in map (it knows places of 15,000+ people); they go in the zone you type.";
        }

        internal void AddPicked()
        {
            string one = Naming.Fit(txtZone.Text, 16);
            if (one.Length == 0) one = RepeaterBookImport.Site;
            string ZoneFor(ChirpChannel c, GeoLocation loc)
            {
                if (optCity.Checked) return c.City.Length > 0 ? Naming.Fit(c.City, 16) : one;
                if (optCounty.Checked && loc.County != null) return ZonePlanner.CountyZone(loc.County.Name);
                return one;
            }
            Result = RepeaterBookImport.Add(session.Project, Picked(), all, ZoneFor,
                                            c => where.TryGetValue(c, out var l) ? l : GeoLocation.Unknown,
                                            (string)cboPower.SelectedItem ?? "High");
            AppSettings.Set("OnlineState", cboState.Text.Trim());
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
                string head = "Added " + r.Added.Count + " analog repeater" + (r.Added.Count == 1 ? "" : "s") +
                              (r.Added.Count > 0 ? " to " + string.Join(", ", r.Added.Select(x => x.Zone).Distinct()) : "") +
                              ". Tones and offsets come from RepeaterBook; check them on the Repeaters tab.";
                using (var dlg = new IssuesDialog(head, new string[0], r.Notes, false)) dlg.ShowDialog(owner);
                return r.Added.FirstOrDefault();
            }
        }
    }
}
