using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Your radio ID, generation options, the CPS format in use, and radio limits.</summary>
    sealed class SettingsPage : UserControl
    {
        readonly Session session;
        readonly TextBox txtRadioName, txtRadioId;
        readonly CheckBox chkRadioIdList, chkRxLists, chkScanLists, chkPolite, chkKeep;
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

            Section("Generated lists");
            chkRxLists = Check("Make a receive group list for each DMR repeater");
            Span(Ui.Hint("With this on, a repeater's channels also play every other talkgroup that repeater carries on the same slot, " +
                         "not just the channel's own talkgroup. Turn it off to hear only the selected talkgroup.", wrap));
            chkScanLists = Check("Make a scan list for each zone (up to 50 channels each)");
            Span(Ui.Hint("Each list copies the settings of a scan list made in the CPS (scan mode off, revert to the selected channel), " +
                         "and every channel in the zone scans its zone's list.", wrap));

            Section("Transmitting");
            chkPolite = Check("Polite transmit: on a repeater, key up only when its slot is free");
            Span(Ui.Hint(PoliteTransmitHint, wrap));

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
            chkScanLists.CheckedChanged += (s, e) => { if (loading) return; session.Project.Options.ScanListPerZone = chkScanLists.Checked; session.MarkDirty(); };
            chkPolite.CheckedChanged += (s, e) => { if (loading) return; session.Project.Options.PoliteTransmit = chkPolite.Checked; session.MarkDirty(); };
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

        // Same text on the Mac Settings tab (MainWindow).
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
            var p = session.Project;
            txtRadioName.Text = p.RadioIdName;
            txtRadioId.Text = p.RadioId > 0 ? p.RadioId.ToString(CultureInfo.InvariantCulture) : "";
            chkRadioIdList.Checked = p.Options.WriteRadioIdList;
            chkRxLists.Checked = p.Options.RxGroupListPerRepeater;
            chkScanLists.Checked = p.Options.ScanListPerZone;
            chkPolite.Checked = p.Options.PoliteTransmit;
            numName.Value = Clamp(p.Options.MaxNameLength, numName);
            numZone.Value = Clamp(p.Options.MaxZoneChannels, numZone);
            numRx.Value = Clamp(p.Options.MaxRxGroupMembers, numRx);
            numScan.Value = Clamp(p.Options.MaxScanListChannels, numScan);
            chkKeep.Checked = p.Options.KeepCpsChannels;
            ShowFormat();
            ShowNumbers();
            ShowBase();
            CheckFields();
            loading = false;
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
                    var f = CpsFormat.FromFolder(folder);
                    if (Directory.Exists(AppSettings.FormatFolder)) Directory.Delete(AppSettings.FormatFolder, true);
                    f.SaveTemplates(AppSettings.FormatFolder);
                    AppSettings.Set("FormatSource", folder);
                    session.Format = AppSettings.LoadFormat();
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
            try { if (Directory.Exists(AppSettings.FormatFolder)) Directory.Delete(AppSettings.FormatFolder, true); } catch { }
            AppSettings.Set("FormatSource", null);
            session.Format = CpsFormat.BuiltIn();
            ShowFormat();
            session.MarkDirty();
        }
    }
}
