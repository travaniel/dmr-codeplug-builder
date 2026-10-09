using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Your radio ID, generation options, the CPS format in use, and radio limits.</summary>
    sealed class SettingsPage : UserControl
    {
        readonly Session session;
        readonly TextBox txtRadioName, txtRadioId, txtHome;
        readonly Label lblHome;
        readonly CheckBox chkRadioIdList, chkRxLists, chkScanLists, chkPolite, chkKeep, chkFavScan, chkLocalFm;
        readonly NumericUpDown numLocalMiles, numGpsMargin;
        readonly CheckBox chkGps;
        readonly ComboBox cboCallers;
        readonly TextBox txtCallerAreas;
        readonly CheckBox chkAprs;
        readonly TextBox txtAprsCall, txtAprsFreq;
        readonly NumericUpDown numAprsSsid;
        Label lblBase;
        readonly NumericUpDown numName, numZone, numRx, numScan;
        readonly Label lblFormat, lblNumbers;
        readonly ErrorProvider errors = new ErrorProvider { BlinkStyle = ErrorBlinkStyle.NeverBlink };
        readonly TableLayoutPanel stack;
        bool loading;

        public SettingsPage(Session session)
        {
            this.session = session;
            Font = Ui.BaseFont;
            AutoScroll = true;
            Padding = new Padding(Ui.S(10), Ui.S(8), Ui.S(10), Ui.S(8));

            stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int wrap = Ui.S(820);

            Span(Ui.Heading("Settings"));

            Section("Your radio");
            txtRadioName = new TextBox { Width = Ui.S(260), MaxLength = 16, Anchor = AnchorStyles.Left };
            txtRadioId = new TextBox { Width = Ui.S(140), MaxLength = 8, Anchor = AnchorStyles.Left };
            Pair("Radio ID name", txtRadioName);
            var idRow = Ui.Row(txtRadioId, Ui.Button("Look up by callsign...", async (s, e) => { if (await Online.LookUpRadioIdAsync(FindForm(), session)) Reload(); }));
            idRow.Dock = DockStyle.None;
            idRow.WrapContents = false;
            idRow.Anchor = AnchorStyles.Left;
            txtRadioId.Margin = new Padding(3, 5, 6, 3);
            Pair("DMR ID", idRow);
            Span(Ui.Hint("Every channel points at this Radio ID name, so it must match the name in the CPS's Radio ID List (yours is imported from your codeplug).", wrap));
            chkRadioIdList = Check("Also write RadioIDList.CSV with this name and DMR ID (replaces the CPS's Radio ID List)");

            Section("Home");
            txtHome = Ui.Text("e.g. Brownwood, TX");
            txtHome.Width = Ui.S(260);
            txtHome.Margin = new Padding(3, 5, 6, 3);
            lblHome = Ui.Label("");
            var homeRow = Ui.Row(txtHome, Ui.Button("Find on the map", (s, e) => FindHome()), Ui.Button("Clear", (s, e) => SetHome(null)), lblHome);
            homeRow.Dock = DockStyle.None;
            homeRow.WrapContents = false;
            txtHome.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindHome(); } };
            homeRow.Anchor = AnchorStyles.Left;
            Pair("Home town", homeRow);
            Span(Ui.Hint("Your home town: the Repeaters list shows each repeater's distance and direction from it, Zones > Sort by distance puts the " +
                         "nearest zones first, and talkgroup zones list the nearest repeaters first. Look up by callsign fills it from RadioID.net.", wrap));

            Section("Generated lists");
            chkRxLists = Check("Make a receive group list for each DMR repeater");
            Span(Ui.Hint("With this on, a repeater's channels also play every other talkgroup that repeater carries on the same slot, " +
                         "not just the channel's own talkgroup. Turn it off to hear only the selected talkgroup.", wrap));
            chkScanLists = Check("Make a scan list for each zone (up to 50 channels each)");
            Span(Ui.Hint("Each list copies the settings of a scan list made in the CPS (scan mode off, revert to the selected channel), " +
                         "and every channel in the zone scans its zone's list.", wrap));
            chkFavScan = Check("Favorites zones: their scan list keeps checking your home channel (hotspot, else the nearest repeater)");
            chkLocalFm = Check("Local FM scan list: analog repeaters near home, nearest first");
            numLocalMiles = new NumericUpDown { Minimum = 5, Maximum = 500, Increment = 5, Width = Ui.S(70), Anchor = AnchorStyles.Left };
            var milesRow = Ui.Row(Ui.Label("Local FM: within"), numLocalMiles, Ui.Label("miles of your home town"));
            milesRow.Dock = DockStyle.None;
            milesRow.WrapContents = false;
            Span(milesRow);
            Span(Ui.Hint("A channel can scan with several lists: its own zone's first, then Favorites and Local FM (Scan List 2, 3 in the CPS). " +
                         "The priority channel is checked every few seconds while scanning, so you don't miss your home repeater or hotspot.", wrap));

            Section("Transmitting");
            chkPolite = Check("Polite transmit: on a repeater, key up only when its slot is free");
            Span(Ui.Hint(PoliteTransmitHint, wrap));

            Section("GPS zone switching");
            chkGps = Check("Switch zones by GPS: write GpsRoaming.CSV with a circle around each area zone's repeaters (up to 32)");
            numGpsMargin = new NumericUpDown { Minimum = 1, Maximum = 100, Width = Ui.S(70), Anchor = AnchorStyles.Left };
            var gpsRow = Ui.Row(Ui.Label("Circle reaches"), numGpsMargin, Ui.Label("km past the zone's farthest repeater"));
            gpsRow.Dock = DockStyle.None;
            gpsRow.WrapContents = false;
            Span(gpsRow);
            Span(Ui.Hint(GpsHint, wrap));

            Section("Caller names");
            cboCallers = Ui.Combo(false, CallerChoices);
            cboCallers.Width = Ui.S(220);
            cboCallers.Anchor = AnchorStyles.Left;
            Pair("Caller list", cboCallers);
            txtCallerAreas = Ui.Text("e.g. Texas, Oklahoma  or  United States, Canada");
            txtCallerAreas.Width = Ui.S(420);
            txtCallerAreas.Anchor = AnchorStyles.Left;
            Pair("Countries or states", txtCallerAreas);
            Span(Ui.Hint(CallerHint, wrap));

            Section("APRS");
            chkAprs = Check("Write APRS.CSV with my APRS callsign, SSID and frequency");
            txtAprsCall = new TextBox { Width = Ui.S(120), MaxLength = 6, CharacterCasing = CharacterCasing.Upper, Anchor = AnchorStyles.Left };
            numAprsSsid = new NumericUpDown { Minimum = 0, Maximum = 15, Width = Ui.S(60), Anchor = AnchorStyles.Left };
            txtAprsFreq = new TextBox { Width = Ui.S(120), Anchor = AnchorStyles.Left };
            var aprsRow = Ui.Row(Ui.Label("Callsign"), txtAprsCall, Ui.Label("SSID"), numAprsSsid, Ui.Label("Frequency MHz"), txtAprsFreq);
            aprsRow.Dock = DockStyle.None;
            aprsRow.WrapContents = false;
            Span(aprsRow);
            Span(Ui.Hint(AprsHint, wrap));

            Section("Channels made in the CPS");
            chkKeep = Check("Keep channels, zones and talkgroups made in the CPS (merge with a CPS export)");
            lblBase = Ui.Label("");
            var baseRow = Ui.Row(lblBase, Ui.Button("Choose export...", (s, e) => ChooseBase()));
            baseRow.WrapContents = false;
            Span(baseRow);
            Span(Ui.Hint("Importing the generated files replaces the CPS's channel, zone and talkgroup lists, so anything made only in the CPS " +
                         "is lost. With this on, Generate reads a CPS export of your codeplug and writes back what was made there: channels " +
                         "(same numbers and settings), zones, and the talkgroups, receive group lists and radio IDs they use. Things this program " +
                         "made and you deleted here stay deleted. Before generating, run Tool > Export > Export All in the CPS into this folder.", wrap));

            Section("Channel numbers");
            lblNumbers = Ui.Label("");
            Span(lblNumbers);
            Span(Ui.Hint("Each channel keeps its number in the radio (from your CPS import, or from the first time it was generated), " +
                         "because APRS and hot keys in the CPS point at channel numbers. New channels take the lowest free numbers. " +
                         "Renumbering gives all channels 1, 2, 3... in zone order at the next Generate; check APRS and hot keys in the CPS afterwards.", wrap));
            Span(Ui.Row(Ui.Button("Renumber all channels...", (s, e) => Renumber())));

            Section("CPS file format");
            lblFormat = Ui.Label("");
            Span(lblFormat);
            Span(Ui.Hint("Generated rows are copies of your CPS's own rows, so every column this program doesn't manage keeps the CPS default. " +
                         "If a CPS update changes the CSV columns, run Tool > Export > Export All in the CPS and load that folder here.", wrap));
            Span(Ui.Row(
                Ui.Button("Load from CPS export...", (s, e) => LoadFormat()),
                Ui.Button("Use built-in (6X2 PRO CPS 1.22)", (s, e) => UseBuiltIn())));

            Section("Radio limits");
            var lg = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            for (int i = 0; i < 4; i++) lg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            numName = Num(8, 16);
            numZone = Num(1, 250);
            numRx = Num(1, 64);   // the radio's RX group list holds 64 talkgroups
            numScan = Num(1, 50);
            lg.Controls.Add(Ui.Label("Name length"), 0, 0); lg.Controls.Add(numName, 1, 0);
            lg.Controls.Add(Ui.Label("Channels per zone"), 2, 0); lg.Controls.Add(numZone, 3, 0);
            lg.Controls.Add(Ui.Label("Talkgroups per RX list"), 0, 1); lg.Controls.Add(numRx, 1, 1);
            lg.Controls.Add(Ui.Label("Channels per scan list"), 2, 1); lg.Controls.Add(numScan, 3, 1);
            Span(lg);
            Span(Ui.Hint("Defaults match the DMR-6X2 PRO: 16-character names, 4000 channels, 250 zones of up to 250 channels, 64 talkgroups per RX list " +
                         "and 50 channels per scan list. Bigger zones are split automatically.", wrap));

            Controls.Add(stack);

            txtRadioName.TextChanged += (s, e) => { if (loading) return; session.Project.RadioIdName = txtRadioName.Text.Trim(); CheckFields(); session.MarkDirty(); };
            txtRadioId.TextChanged += (s, e) =>
            {
                if (loading) return;
                string t = txtRadioId.Text.Trim();
                if (t.Length == 0) session.Project.RadioId = 0;
                else if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0) session.Project.RadioId = id;
                CheckFields();
                session.MarkDirty();
            };
            chkRadioIdList.CheckedChanged += (s, e) => { if (loading) return; session.Project.Options.WriteRadioIdList = chkRadioIdList.Checked; session.MarkDirty(); };
            chkRxLists.CheckedChanged += (s, e) => { if (loading) return; session.Project.Options.RxGroupListPerRepeater = chkRxLists.Checked; session.MarkDirty(); };
            chkScanLists.CheckedChanged += (s, e) => { chkFavScan.Enabled = chkLocalFm.Enabled = numLocalMiles.Enabled = chkScanLists.Checked; if (loading) return; session.Project.Options.ScanListPerZone = chkScanLists.Checked; session.MarkDirty(); };
            chkFavScan.CheckedChanged += (s, e) => { if (loading) return; session.Project.Options.FavoritesScanPriority = chkFavScan.Checked; session.MarkDirty(); };
            chkGps.CheckedChanged += (s, e) => { numGpsMargin.Enabled = chkGps.Checked; if (loading) return; session.Project.Options.GpsZoneSwitching = chkGps.Checked; session.MarkDirty(); };
            numGpsMargin.ValueChanged += (s, e) => { if (loading) return; session.Project.Options.GpsMarginKm = (int)numGpsMargin.Value; session.MarkDirty(); };
            chkLocalFm.CheckedChanged += (s, e) => { if (loading) return; session.Project.Options.LocalAnalogScanList = chkLocalFm.Checked; session.MarkDirty(); };
            numLocalMiles.ValueChanged += (s, e) => { if (loading) return; session.Project.Options.LocalAnalogMiles = (int)numLocalMiles.Value; session.MarkDirty(); };
            chkPolite.CheckedChanged += (s, e) => { if (loading) return; session.Project.Options.PoliteTransmit = chkPolite.Checked; session.MarkDirty(); };
            cboCallers.SelectedIndexChanged += (s, e) =>
            {
                txtCallerAreas.Enabled = cboCallers.SelectedIndex >= 2;
                if (loading) return;
                string v = CallerScopeValues[Math.Max(0, cboCallers.SelectedIndex)];
                session.Project.Options.CallerScope = v.Length == 0 ? null : v;
                session.MarkDirty();
            };
            chkAprs.CheckedChanged += (s, e) =>
            {
                txtAprsCall.Enabled = numAprsSsid.Enabled = txtAprsFreq.Enabled = chkAprs.Checked;
                if (loading) return;
                session.Project.Aprs = chkAprs.Checked ? ReadAprs() : null;
                session.MarkDirty();
            };
            EventHandler aprsEdited = (s, e) => { if (loading || !chkAprs.Checked) return; session.Project.Aprs = ReadAprs(); session.MarkDirty(); };
            txtAprsCall.TextChanged += aprsEdited;
            numAprsSsid.ValueChanged += aprsEdited;
            txtAprsFreq.TextChanged += aprsEdited;
            txtCallerAreas.TextChanged += (s, e) =>
            {
                if (loading) return;
                var areas = txtCallerAreas.Text.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                session.Project.Options.CallerAreas = areas.Count == 0 ? null : areas;
                session.MarkDirty();
            };
            chkKeep.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                if (chkKeep.Checked && CpsFormat.FindFile(session.Project.Options.BaseExportFolder, CpsFormat.ChannelFile) == null && !ChooseBase())
                {
                    loading = true; chkKeep.Checked = false; loading = false;
                    return;
                }
                session.Project.Options.KeepCpsChannels = chkKeep.Checked;
                session.MarkDirty();
                ShowBase();
            };
            numName.ValueChanged += (s, e) => { if (loading) return; session.Project.Options.MaxNameLength = (int)numName.Value; session.MarkDirty(); };
            numZone.ValueChanged += (s, e) => { if (loading) return; session.Project.Options.MaxZoneChannels = (int)numZone.Value; session.MarkDirty(); };
            numRx.ValueChanged += (s, e) => { if (loading) return; session.Project.Options.MaxRxGroupMembers = (int)numRx.Value; session.MarkDirty(); };
            numScan.ValueChanged += (s, e) => { if (loading) return; session.Project.Options.MaxScanListChannels = (int)numScan.Value; session.MarkDirty(); };

            session.Replaced += (s, e) => Reload();
            VisibleChanged += (s, e) => { if (Visible) Reload(); }; // other places change these too (the radio ID, channel numbers stored by Export or Write to radio)
            Reload();
        }

        void FindHome()
        {
            HomeLocation home;
            Cursor.Current = Cursors.WaitCursor;
            try { home = HomeLocation.Parse(GeoAtlas.BuiltIn(), txtHome.Text, HomeLocation.DefaultCountry(session.Project)); }
            finally { Cursor.Current = Cursors.Default; }
            if (home == null)
            {
                lblHome.Text = "Not found. Try \"Town, State\" or \"Town, Country\".";
                lblHome.ForeColor = System.Drawing.Color.Firebrick;
                return;
            }
            SetHome(home);
        }

        void SetHome(HomeLocation home)
        {
            session.Project.Home = home;
            loading = true;
            txtHome.Text = home?.Place ?? "";
            loading = false;
            ShowHome();
            session.MarkDirty();
        }

        void ShowHome()
        {
            var h = session.Project.Home;
            lblHome.ForeColor = Ui.HintColor;
            lblHome.Text = h == null ? "Not set" : h.Latitude.ToString("0.000", CultureInfo.InvariantCulture) + ", " + h.Longitude.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>The APRS settings as typed: the suggestion (symbol, path, gateway) with the user's callsign, SSID and frequency.</summary>
        AprsPlan ReadAprs()
        {
            var a = session.Project.Aprs ?? Aprs.Suggest(session.Project);
            a.Callsign = txtAprsCall.Text.Trim().ToUpperInvariant();
            a.Ssid = (int)numAprsSsid.Value;
            if (decimal.TryParse(txtAprsFreq.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal f) && f > 0) a.FrequencyMHz = f;
            return a;
        }

        // Same text on the Mac Settings tab (MainWindow).
        const string AprsHint =
            "Export adds APRS.CSV: your callsign and SSID (7 = handheld, 9 = mobile), the APRS frequency, the person symbol, path WIDE1-1,WIDE2-1, " +
            "fixed position off (the radio's GPS is used) and, in the US, BrandMeister's APRS gateway 310999 as a private call for digital reports. " +
            "Importing it in the CPS replaces all of its APRS settings, and the CPS also resets some the file doesn't hold (transmit delay, display " +
            "time, analog bandwidth, receive filters): check the CPS's APRS screen after importing. Beacons stay as they are (manual).";

        static readonly string[] CallerChoices = { "Off (keep the CPS's list)", "Whole world", "Countries", "US states" };
        static readonly string[] CallerScopeValues = { CallerScopes.Off, CallerScopes.World, CallerScopes.Countries, CallerScopes.UsStates };
        const string CallerHint =
            "Export adds DigitalContactList.CSV with DMR users from RadioID.net (downloaded once a week, about 17 MB), so the radio shows a caller's " +
            "callsign and name instead of a number. Importing it in the CPS replaces the caller list there. The whole world fits the 6X2 PRO (about 315,000 " +
            "users; it holds 500,000). Write to radio doesn't send caller names yet: import the file in the CPS and write from there.";

        // Same text on the Mac Settings tab (MainWindow).
        internal const string GpsHint =
            "With GPS on, the radio changes to a zone when you drive into its circle (centre of the zone's repeaters; nearest home first when there are more than 32). " +
            "In the radio (or the CPS's Optional Setting > GPS/Ranging) turn on GPS and GPS Roaming, and set the distance unit to meters: the radius is written in meters. " +
            "Export writes GpsRoaming.CSV, which replaces the radio's 32 entries; Write to radio doesn't send it yet. Only the zone changes, not the channel, " +
            "and while it is on the radio may switch back from a zone you picked by hand.";

        const string PoliteTransmitHint =
            "DMR repeater channels get TX permit \"Same Color Code\": the radio won't transmit while the repeater's slot carries another call. " +
            "The hotspot and DMR simplex channels stay on \"Always\" (a hotspot on a stricter setting can refuse to key) and analog channels on \"Off\". " +
            "Turned off, channels keep the CPS default (Always for DMR). Codeplugs imported from the CPS or saved before version 1.4 start with it off, " +
            "so their channels don't change.";

        void Span(Control c)
        {
            int row = stack.RowCount;
            stack.RowCount = row + 1;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(c, 0, row);
            stack.SetColumnSpan(c, 2);
        }

        void Pair(string label, Control field)
        {
            int row = stack.RowCount;
            stack.RowCount = row + 1;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(Ui.Label(label), 0, row);
            stack.Controls.Add(field, 1, row);
        }

        void Section(string title)
        {
            var l = Ui.Label(title, true);
            l.Margin = new Padding(3, Ui.S(16), 3, Ui.S(4));
            Span(l);
        }

        CheckBox Check(string text)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
            Span(c);
            return c;
        }

        static NumericUpDown Num(int min, int max)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Width = Ui.S(70), Margin = new Padding(3, 3, Ui.S(18), 3) };
        }

        public void Reload()
        {
            loading = true;
            try { Fill(session.Project); }
            finally { loading = false; }
        }

        void Fill(Project p)
        {
            txtRadioName.Text = p.RadioIdName;
            txtRadioId.Text = p.RadioId > 0 ? p.RadioId.ToString(CultureInfo.InvariantCulture) : "";
            chkRadioIdList.Checked = p.Options.WriteRadioIdList;
            txtHome.Text = p.Home?.Place ?? "";
            ShowHome();
            chkRxLists.Checked = p.Options.RxGroupListPerRepeater;
            chkScanLists.Checked = p.Options.ScanListPerZone;
            chkFavScan.Checked = p.Options.FavoritesScanPriority;
            chkGps.Checked = p.Options.GpsZoneSwitching;
            numGpsMargin.Value = Math.Max(1, Math.Min(100, p.Options.GpsMarginKm > 0 ? p.Options.GpsMarginKm : GpsRoaming.DefaultMarginKm));
            numGpsMargin.Enabled = p.Options.GpsZoneSwitching;
            chkLocalFm.Checked = p.Options.LocalAnalogScanList;
            numLocalMiles.Value = Math.Max(5, Math.Min(500, p.Options.LocalAnalogMiles > 0 ? p.Options.LocalAnalogMiles : GenerationOptions.DefaultLocalAnalogMiles));
            chkFavScan.Enabled = chkLocalFm.Enabled = numLocalMiles.Enabled = p.Options.ScanListPerZone;
            chkPolite.Checked = p.Options.PoliteTransmit;
            cboCallers.SelectedIndex = Math.Max(0, Array.IndexOf(CallerScopeValues, p.Options.CallerScope ?? ""));
            txtCallerAreas.Text = string.Join(", ", p.Options.CallerAreas ?? Enumerable.Empty<string>());
            txtCallerAreas.Enabled = cboCallers.SelectedIndex >= 2;
            chkAprs.Checked = p.Aprs != null;
            var aprs = p.Aprs ?? Aprs.Suggest(p);
            txtAprsCall.Text = aprs.Callsign;
            numAprsSsid.Value = Math.Max(0, Math.Min(15, aprs.Ssid));
            txtAprsFreq.Text = aprs.FrequencyMHz > 0 ? aprs.FrequencyMHz.ToString("0.000", CultureInfo.InvariantCulture) : "";
            txtAprsCall.Enabled = numAprsSsid.Enabled = txtAprsFreq.Enabled = chkAprs.Checked;
            numName.Value = Clamp(p.Options.MaxNameLength, numName);
            numZone.Value = Clamp(p.Options.MaxZoneChannels, numZone);
            numRx.Value = Clamp(p.Options.MaxRxGroupMembers, numRx);
            numScan.Value = Clamp(p.Options.MaxScanListChannels, numScan);
            chkKeep.Checked = p.Options.KeepCpsChannels;
            ShowFormat();
            ShowNumbers();
            ShowBase();
            CheckFields();
        }

        void ShowBase()
        {
            string folder = session.Project.Options.BaseExportFolder;
            string channels = CpsFormat.FindFile(folder, CpsFormat.ChannelFile);
            lblBase.Text = string.IsNullOrEmpty(folder) ? "Export: none chosen"
                         : channels == null ? "Export: " + folder + " (no Channel.CSV there)"
                         : "Export: " + folder + "  (exported " + File.GetLastWriteTime(channels).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + ")";
            lblBase.ForeColor = session.Project.Options.KeepCpsChannels && channels == null ? System.Drawing.Color.Firebrick : System.Drawing.SystemColors.ControlText;
        }

        /// <summary>Picks the CPS Export All folder to merge with. Returns false when cancelled.</summary>
        bool ChooseBase()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Pick the .LST or Channel.CSV of a CPS Export All of your codeplug",
                Filter = "CPS export (*.LST;Channel.CSV)|*.LST;Channel.CSV|All files (*.*)|*.*",
            })
            {
                string current = session.Project.Options.BaseExportFolder;
                if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dlg.InitialDirectory = current;
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return false;
                string folder = Path.GetDirectoryName(dlg.FileName);
                if (CpsFormat.FindFile(folder, CpsFormat.ChannelFile) == null) { Ui.Error(FindForm(), "There's no Channel.CSV in " + folder + "."); return false; }
                session.Project.Options.BaseExportFolder = folder;
                session.MarkDirty();
                ShowBase();
                return true;
            }
        }

        void ShowNumbers()
        {
            int kept = session.Project.StoredChannelNumbers().Count;
            lblNumbers.Text = kept == 0 ? "No channel numbers stored yet: the next Generate numbers the channels 1, 2, 3..."
                                        : kept + " channel" + (kept == 1 ? " keeps its" : "s keep their") + " number.";
        }

        void Renumber()
        {
            if (session.Project.StoredChannelNumbers().Count == 0) { Ui.Info(FindForm(), "There are no stored channel numbers; the next Generate numbers the channels 1, 2, 3... already."); return; }
            if (!Ui.Confirm(FindForm(), "Give every channel a new number (1, 2, 3... in zone order) the next time you generate?\n\n" +
                                        "Channel numbers used by APRS and hot keys in the CPS may then point at different channels; check them after importing."))
                return;
            session.Project.ClearChannelNumbers();
            session.MarkDirty();
            ShowNumbers();
        }

        static decimal Clamp(int v, NumericUpDown n)
        {
            return Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        void CheckFields()
        {
            errors.SetError(txtRadioName, string.IsNullOrWhiteSpace(txtRadioName.Text) ? "Required: must match your CPS Radio ID List" : "");
            string id = txtRadioId.Text.Trim();
            errors.SetError(txtRadioId, id.Length > 0 && !int.TryParse(id, out _) ? "Numbers only" : "");
        }

        void ShowFormat()
        {
            string src = AppSettings.Get("FormatSource");
            bool custom = File.Exists(Path.Combine(AppSettings.FormatFolder, CpsFormat.ChannelFile));
            lblFormat.Text = "Using: " + (custom ? "your CPS export" + (src != null ? " (" + src + ")" : "") : session.Format.Source);
        }

        void LoadFormat()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Pick the .LST or Channel.CSV from a CPS Export All",
                Filter = "CPS export (*.LST;Channel.CSV)|*.LST;Channel.CSV|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                string folder = Path.GetDirectoryName(dlg.FileName);
                try
                {
                    session.Format = AppSettings.SaveFormat(CpsFormat.FromFolder(folder), folder);
                    ShowFormat();
                    session.MarkDirty();
                    Ui.Info(FindForm(), "Generated files now follow the layout from:\n" + folder);
                }
                catch (Exception ex)
                {
                    Ui.Error(FindForm(), "That folder can't be used as a CPS format:\n\n" + ex.Message);
                }
            }
        }

        void UseBuiltIn()
        {
            session.Format = AppSettings.ClearFormat();
            ShowFormat();
            session.MarkDirty();
        }
    }
}
