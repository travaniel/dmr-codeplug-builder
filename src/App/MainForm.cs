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
    sealed class MainForm : Form
    {
        readonly Session session = new Session();
        readonly TabControl tabs;
        readonly RepeatersPage repeatersPage;
        readonly HotspotPage hotspotPage;
        readonly TalkgroupsPage talkgroupsPage;
        readonly ZonesPage zonesPage;
        readonly SettingsPage settingsPage;
        readonly Label lblStatus;
        readonly LinkLabel lnkIssues;
        readonly Timer statusTimer = new Timer { Interval = 400 };
        readonly Panel workspace;
        readonly StartPage startPage;
        readonly List<ToolStripItem> projectItems = new List<ToolStripItem>();
        NewCodeplugWizard wizard;
        bool wizardFromWorkspace;
        enum Mode { Start, Wizard, Workspace }
        Mode mode = Mode.Workspace;
        List<Issue> lastIssues = new List<Issue>();

        public MainForm(string projectPath, string startTab)
        {
            Text = "DMR Codeplug Builder";
            Font = Ui.BaseFont;
            var area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Ui.S(1240), area.Width * 94 / 100), Math.Min(Ui.S(860), area.Height * 94 / 100));
            MinimumSize = new Size(Ui.S(980), Ui.S(620));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            session.Format = AppSettings.LoadFormat();

            // ---------- menu ----------
            var menu = new MenuStrip { Font = Ui.BaseFont };
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&New codeplug...", Keys.Control | Keys.N, (s, e) => StartWizard()));
            file.DropDownItems.Add(Item("New &empty project", Keys.None, (s, e) => NewProject()));
            file.DropDownItems.Add(Item("&Open project...", Keys.Control | Keys.O, (s, e) => OpenProject()));
            file.DropDownItems.Add(ProjectItem(Item("&Save", Keys.Control | Keys.S, (s, e) => Save())));
            file.DropDownItems.Add(ProjectItem(Item("Save &as...", Keys.Control | Keys.Shift | Keys.S, (s, e) => SaveAs())));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("&Import from CPS export...", Keys.None, (s, e) => ImportFromCps()));
            file.DropDownItems.Add(ProjectItem(Item("&Generate CSV files...", Keys.Control | Keys.G, (s, e) => Generate())));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Keys.None, (s, e) => Close()));
            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&Loading the files into the CPS", Keys.F1, (s, e) => ShowHowTo()));
            help.DropDownItems.Add(ProjectItem(Item("Check for &problems", Keys.None, (s, e) => { RefreshStatus(); IssuesDialog.ShowIssues(this, lastIssues); })));
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add(Item("&About", Keys.None, (s, e) => ShowAbout()));
            menu.Items.Add(file);
            menu.Items.Add(help);
            MainMenuStrip = menu;

            // ---------- tabs ----------
            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Ui.S(14), Ui.S(5)) };
            repeatersPage = new RepeatersPage(session) { Dock = DockStyle.Fill };
            hotspotPage = new HotspotPage(session) { Dock = DockStyle.Fill };
            talkgroupsPage = new TalkgroupsPage(session) { Dock = DockStyle.Fill };
            zonesPage = new ZonesPage(session) { Dock = DockStyle.Fill };
            settingsPage = new SettingsPage(session) { Dock = DockStyle.Fill };
            AddTab("Repeaters", repeatersPage);
            AddTab("Hotspot", hotspotPage);
            AddTab("Talkgroups", talkgroupsPage);
            AddTab("Zones", zonesPage);
            AddTab("Settings", settingsPage);

            // ---------- bottom bar ----------
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                ColumnCount = 3,
                AutoSize = true,
                Padding = new Padding(Ui.S(10), Ui.S(6), Ui.S(10), Ui.S(8)),
                BackColor = SystemColors.ControlLight,
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, Ui.S(8), 3, 3) };
            lnkIssues = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, Ui.S(8), Ui.S(14), 3), LinkBehavior = LinkBehavior.HoverUnderline };
            lnkIssues.LinkClicked += (s, e) => IssuesDialog.ShowIssues(this, lastIssues);
            var btnGenerate = new Button
            {
                Text = "Generate CSV files...",
                AutoSize = true,
                Font = Ui.BoldFont,
                Padding = new Padding(Ui.S(12), Ui.S(4), Ui.S(12), Ui.S(4)),
                Anchor = AnchorStyles.Right,
                UseVisualStyleBackColor = true,
            };
            btnGenerate.Click += (s, e) => Generate();
            bar.Controls.Add(lblStatus, 0, 0);
            bar.Controls.Add(lnkIssues, 1, 0);
            bar.Controls.Add(btnGenerate, 2, 0);

            workspace = new Panel { Dock = DockStyle.Fill };
            workspace.Controls.Add(tabs);
            workspace.Controls.Add(bar);
            tabs.BringToFront();

            startPage = new StartPage { Dock = DockStyle.Fill, Visible = false };
            startPage.NewCodeplug += (s, e) => StartWizard();
            startPage.ImportCps += (s, e) => ImportFromCps();
            startPage.OpenProject += (s, e) => OpenProject();
            startPage.EmptyProject += (s, e) => { session.Replace(new Project(), null, false); ShowWorkspace(); };
            startPage.OpenRecent += (s, path) => OpenPath(path);

            Controls.Add(workspace);
            Controls.Add(startPage);
            Controls.Add(menu);
            workspace.BringToFront();
            startPage.BringToFront();

            session.Changed += (s, e) => { UpdateTitle(); statusTimer.Stop(); statusTimer.Start(); };
            statusTimer.Tick += (s, e) => { statusTimer.Stop(); RefreshStatus(); };
            FormClosing += (s, e) =>
            {
                if (wizard != null && wizard.Visible && wizard.HasProgress &&
                    !Ui.Confirm(this, "Leave the new codeplug setup? Nothing from it is saved.")) { e.Cancel = true; return; }
                if (!ConfirmDiscard()) e.Cancel = true;
                else wizard?.CancelDownload();
            };

            LoadInitial(projectPath);
            if (startTab != null)
                foreach (TabPage p in tabs.TabPages)
                    if (string.Equals(p.Text, startTab, StringComparison.OrdinalIgnoreCase)) tabs.SelectedTab = p;
        }

        static ToolStripMenuItem Item(string text, Keys keys, EventHandler click)
        {
            var i = new ToolStripMenuItem(text, null, click);
            if (keys != Keys.None) i.ShortcutKeys = keys;
            return i;
        }

        /// <summary>A menu item that only makes sense with a project open (off on the start page and in the wizard).</summary>
        ToolStripMenuItem ProjectItem(ToolStripMenuItem item)
        {
            projectItems.Add(item);
            return item;
        }

        // ======================================================================
        // Start page, wizard, workspace
        // ======================================================================

        void ShowWorkspace()
        {
            if (wizard != null) { Controls.Remove(wizard); wizard.Dispose(); wizard = null; }
            mode = Mode.Workspace;
            startPage.Visible = false;
            workspace.Visible = true;
            workspace.BringToFront();
            foreach (var i in projectItems) i.Enabled = true;
            UpdateTitle();
        }

        void ShowStart()
        {
            if (wizard != null) { Controls.Remove(wizard); wizard.Dispose(); wizard = null; }
            mode = Mode.Start;
            session.Replace(new Project(), null, false);
            workspace.Visible = false;
            startPage.Visible = true;
            startPage.BringToFront();
            foreach (var i in projectItems) i.Enabled = false;
            UpdateTitle();
        }

        // Not workspace.Visible: that reads false until the form itself is shown.
        bool InWorkspace => mode == Mode.Workspace;

        void StartWizard()
        {
            if (wizard != null) return;
            if (InWorkspace && !ConfirmDiscard()) return;
            wizardFromWorkspace = InWorkspace;
            wizard = new NewCodeplugWizard { Dock = DockStyle.Fill };
            wizard.Finished += (s, project) => FinishWizard(project);
            wizard.Cancelled += (s, e) =>
            {
                if (wizard.HasProgress && !Ui.Confirm(this, "Leave the new codeplug setup? Nothing from it is saved.")) return;
                wizard.CancelDownload();
                if (wizardFromWorkspace) ShowWorkspace(); else ShowStart();
            };
            Controls.Add(wizard);
            wizard.BringToFront();
            mode = Mode.Wizard;
            workspace.Visible = false;
            startPage.Visible = false;
            foreach (var i in projectItems) i.Enabled = false;
            UpdateTitle();
        }

        void FinishWizard(Project p)
        {
            var notes = wizard.State.Notes;
            session.Replace(p, null, true);
            ShowWorkspace();
            tabs.SelectedIndex = 0;
            int dmr = p.Repeaters.Count(r => r.IsDigital), fm = p.Repeaters.Count(r => !r.IsDigital);
            int zones = p.UsedZoneNames().Count;
            string head = "Your new codeplug has " + Plural(dmr, "DMR repeater") + (fm > 0 ? ", " + Plural(fm, "analog channel") : "") +
                          (p.HotspotEnabled ? ", your hotspot" : "") + " and " + p.ChannelCount() + " channels in " + Plural(zones, "zone") + ".\n\n" +
                          "Check it on the Repeaters and Zones tabs, save it (File > Save), then click Generate CSV files.";
            using (var d = new IssuesDialog(head, new string[0], notes, false)) d.ShowDialog(this);
        }

        void AddTab(string title, Control page)
        {
            var tp = new TabPage(title) { UseVisualStyleBackColor = true };
            tp.Controls.Add(page);
            tabs.TabPages.Add(tp);
        }

        void LoadInitial(string path)
        {
            if (path == null)
            {
                string last = AppSettings.Get("LastProject");
                if (last != null && File.Exists(last)) path = last;
            }
            if (path == null)
            {
                // First run: open a project shipped next to the program, if there is exactly one.
                try
                {
                    var here = Directory.GetFiles(Path.GetDirectoryName(Application.ExecutablePath), "*" + ProjectStore.Extension);
                    if (here.Length == 1) path = here[0];
                }
                catch { }
            }
            if (path != null)
            {
                try
                {
                    session.Replace(ProjectStore.Load(path), path, false);
                    AppSettings.Set("LastProject", path);
                    ShowWorkspace();
                    return;
                }
                catch (Exception ex)
                {
                    Ui.Error(this, "Couldn't open " + path + ":\n\n" + ex.Message);
                }
            }
            // Nothing to reopen: the start page (new codeplug, import, open).
            ShowStart();
        }

        void UpdateTitle()
        {
            Text = mode == Mode.Start ? "DMR Codeplug Builder"
                 : mode == Mode.Wizard ? "New codeplug - DMR Codeplug Builder"
                 : session.DisplayName + (session.Dirty ? " *" : "") + " - DMR Codeplug Builder";
        }

        void RefreshStatus()
        {
            var p = session.Project;
            try
            {
                var g = CodeplugGenerator.Generate(p, session.Format);
                int dmr = p.Repeaters.Count(r => r.Enabled && r.IsDigital);
                int fm = p.Repeaters.Count(r => r.Enabled && !r.IsDigital);
                lblStatus.Text = g.ChannelList.Count + " channels in " + g.ZoneList.Count + " zones   |   " +
                                 dmr + " DMR repeater" + (dmr == 1 ? "" : "s") + ", " + fm + " analog" +
                                 (p.HotspotEnabled ? ", hotspot" : "") + "   |   " + p.Talkgroups.Count + " talkgroups";
            }
            catch
            {
                lblStatus.Text = p.Repeaters.Count + " repeaters   |   " + p.Talkgroups.Count + " talkgroups";
            }
            lastIssues = Validator.Validate(p, session.Format);
            int errors = lastIssues.Count(i => i.Severity == Severity.Error);
            int warnings = lastIssues.Count - errors;
            if (errors > 0)
            {
                lnkIssues.Text = errors + " problem" + (errors == 1 ? "" : "s") + " to fix";
                lnkIssues.LinkColor = Color.Firebrick;
            }
            else if (warnings > 0)
            {
                lnkIssues.Text = warnings + " thing" + (warnings == 1 ? "" : "s") + " to check";
                lnkIssues.LinkColor = Color.DarkGoldenrod;
            }
            else
            {
                lnkIssues.Text = "Ready to generate";
                lnkIssues.LinkColor = Color.ForestGreen;
            }
        }

        // ======================================================================
        // Files
        // ======================================================================

        bool ConfirmDiscard()
        {
            if (!session.Dirty) return true;
            var answer = MessageBox.Show(this, "Save changes to " + session.DisplayName + "?", "DMR Codeplug Builder",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) return Save();
            return true;
        }

        void NewProject()
        {
            if (wizard != null) return;
            if (InWorkspace && !ConfirmDiscard()) return;
            session.Replace(new Project(), null, false);
            ShowWorkspace();
            tabs.SelectedIndex = 0;
        }

        void OpenProject()
        {
            if (wizard != null) return;
            if (InWorkspace && !ConfirmDiscard()) return;
            using (var dlg = new OpenFileDialog { Title = "Open project", Filter = ProjectStore.FileFilter, InitialDirectory = DefaultProjectFolder() })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                OpenPath(dlg.FileName);
            }
        }

        void OpenPath(string path)
        {
            try
            {
                session.Replace(ProjectStore.Load(path), path, false);
                AppSettings.Set("LastProject", path);
                ShowWorkspace();
            }
            catch (Exception ex)
            {
                Ui.Error(this, "Couldn't open that project:\n\n" + ex.Message);
            }
        }

        bool Save()
        {
            if (session.FilePath == null) return SaveAs();
            try
            {
                session.Save(session.FilePath);
                return true;
            }
            catch (Exception ex)
            {
                Ui.Error(this, "Couldn't save:\n\n" + ex.Message);
                return false;
            }
        }

        bool SaveAs()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "Save project",
                Filter = ProjectStore.FileFilter,
                DefaultExt = "cpb",
                FileName = session.DisplayName == "Untitled" ? "My codeplug" : session.DisplayName,
                InitialDirectory = session.FilePath != null ? Path.GetDirectoryName(session.FilePath) : DefaultProjectFolder(),
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                try
                {
                    session.Save(dlg.FileName);
                    AppSettings.Set("LastProject", dlg.FileName);
                    return true;
                }
                catch (Exception ex)
                {
                    Ui.Error(this, "Couldn't save:\n\n" + ex.Message);
                    return false;
                }
            }
        }

        static string DefaultProjectFolder()
        {
            try { Directory.CreateDirectory(AppSettings.DocumentsFolder); } catch { }
            return AppSettings.DocumentsFolder;
        }

        void ImportFromCps()
        {
            if (wizard != null) return;
            if (InWorkspace && !ConfirmDiscard()) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "Pick the .LST or Channel.CSV from your CPS export (Tool > Export > Export All)",
                Filter = "CPS export (*.LST;Channel.CSV)|*.LST;Channel.CSV|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string folder = Path.GetDirectoryName(dlg.FileName);
                ImportResult result;
                try { result = CpsImporter.Import(folder); }
                catch (Exception ex) { Ui.Error(this, "Couldn't import that export:\n\n" + ex.Message); return; }

                var notes = new List<string>(result.Notes);
                try
                {
                    var f = CpsFormat.FromFolder(folder);
                    if (!SameLayout(f, session.Format))
                    {
                        if (Directory.Exists(AppSettings.FormatFolder)) Directory.Delete(AppSettings.FormatFolder, true);
                        f.SaveTemplates(AppSettings.FormatFolder);
                        AppSettings.Set("FormatSource", folder);
                        session.Format = AppSettings.LoadFormat();
                        notes.Add("This export's CSV layout differs from the built-in one, so generated files now follow it (Settings tab).");
                    }
                }
                catch { }

                session.Replace(result.Project, null, true);
                ShowWorkspace();
                tabs.SelectedIndex = 0;
                var p = result.Project;
                string head = "Imported " + Plural(p.Talkgroups.Count, "talkgroup") + ", " + Plural(p.Repeaters.Count(r => r.IsDigital), "DMR repeater") + ", " +
                              Plural(p.Repeaters.Count(r => !r.IsDigital), "analog channel") + (p.HotspotEnabled ? ", your hotspot" : "") +
                              " and " + Plural(p.Zones.Count, "zone") + ". Save the project (File > Save) to keep it.";
                using (var d = new IssuesDialog(head, new string[0], notes, false)) d.ShowDialog(this);
            }
        }

        static string Plural(int n, string word)
        {
            return n + " " + word + (n == 1 ? "" : "s");
        }

        static bool SameLayout(CpsFormat a, CpsFormat b)
        {
            return CpsFormat.Files.All(f => string.Join(",", a.Table(f).Header) == string.Join(",", b.Table(f).Header));
        }

        // ======================================================================
        // Generate
        // ======================================================================

        void Generate()
        {
            Validate(); // commits any cell being edited
            var p = session.Project;
            p.SyncZones();
            var issues = Validator.Validate(p, session.Format);
            var errors = issues.Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
            if (errors.Count > 0)
            {
                using (var d = new IssuesDialog("Fix these before generating:", errors,
                           issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message), false))
                    d.ShowDialog(this);
                return;
            }

            // Merge mode: channels made in the CPS come from a CPS export (Settings).
            CpsExport mergeBase = null;
            if (p.Options.KeepCpsChannels)
            {
                try { mergeBase = CpsExport.Load(p.Options.BaseExportFolder); }
                catch (Exception ex)
                {
                    Ui.Error(this, "\"Keep channels made in the CPS\" is on (Settings tab), but the CPS export couldn't be read:\n\n" + ex.Message +
                                   "\n\nIn the CPS, run Tool > Export > Export All again, or choose the folder on the Settings tab.");
                    return;
                }
            }

            GeneratedCodeplug g;
            try { g = CodeplugGenerator.Generate(p, session.Format, mergeBase); }
            catch (Exception ex) { Ui.Error(this, ex.Message); return; }
            if (g.ChannelList.Count + g.KeptChannels.Count > p.Options.MaxChannels)
            {
                Ui.Error(this, "With the " + g.KeptChannels.Count + " channels made in the CPS this codeplug has " + (g.ChannelList.Count + g.KeptChannels.Count) +
                               " channels; the radio holds " + p.Options.MaxChannels + ".");
                return;
            }

            var warnings = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message).Concat(g.Notes).Distinct().ToList();
            if (mergeBase != null)
            {
                // An export older than the last generated codeplug may be missing what was made in the CPS since.
                DateTime lastGenerated = DateTime.MinValue;
                try
                {
                    if (!string.IsNullOrEmpty(p.Options.OutputFolder) && Directory.Exists(p.Options.OutputFolder))
                        foreach (var lst in Directory.GetFiles(p.Options.OutputFolder, "*.LST"))
                            if (File.GetLastWriteTime(lst) > lastGenerated) lastGenerated = File.GetLastWriteTime(lst);
                }
                catch { }
                if (lastGenerated > mergeBase.Exported.AddMinutes(1) && !Path.GetFullPath(p.Options.OutputFolder).Equals(Path.GetFullPath(mergeBase.Folder), StringComparison.OrdinalIgnoreCase))
                    warnings.Insert(0, "The CPS export (" + mergeBase.Exported.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + ") is older than your last generated codeplug (" +
                                       lastGenerated.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + "). If you've changed channels in the CPS since, export again first.");
            }
            string summary = (g.ChannelList.Count + g.KeptChannels.Count) + " channels in " + g.ZoneList.Count + " zones" +
                             (g.KeptChannels.Count > 0 ? " (" + g.KeptChannels.Count + " of them made in the CPS)" : "");
            if (warnings.Count > 0)
            {
                using (var d = new IssuesDialog("Ready to generate " + summary + ". A few things to check first:", new string[0], warnings, true))
                    if (d.ShowDialog(this) != DialogResult.OK) return;
            }

            string startDir = p.Options.OutputFolder;
            if (string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir))
            {
                startDir = Path.Combine(AppSettings.DocumentsFolder, "Generated");
                try { Directory.CreateDirectory(startDir); } catch { }
            }
            using (var dlg = new SaveFileDialog
            {
                Title = "Save the codeplug (the CSV files are saved next to this file list)",
                Filter = "CPS file list (*.LST)|*.LST",
                DefaultExt = "LST",
                FileName = Naming.Clean(session.DisplayName, 40) + ".LST",
                InitialDirectory = startDir,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string folder = Path.GetDirectoryName(dlg.FileName);
                var existing = g.Files().Select(f => f.Key).Where(f => CpsFormat.FindFile(folder, f) != null).ToList();
                if (existing.Count > 0 && !Ui.Confirm(this, "This folder already has " + string.Join(", ", existing) + ".\n\nReplace them with the new files?"))
                    return;
                try
                {
                    g.WriteTo(folder, Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex)
                {
                    Ui.Error(this, "Couldn't write the files:\n\n" + ex.Message);
                    return;
                }
                if (p.Options.OutputFolder != folder)
                {
                    p.Options.OutputFolder = folder;
                    session.MarkDirty();
                }
                // The channels now have these numbers in the radio: keep them, so APRS and hot keys stay right next time.
                int numbered = CodeplugGenerator.KeepChannelNumbers(g);
                bool remembered = CodeplugGenerator.RememberOutput(p, g); // merge mode: these are this program's from now on
                if (numbered > 0 || remembered) session.MarkDirty();
                string files = string.Join(", ", g.Files().Select(f => f.Key));
                var answer = MessageBox.Show(this,
                    "Saved " + summary + " to:\n" + folder + "\n\n" + files + " and " + Path.GetFileName(dlg.FileName) + "\n\n" +
                    "To load them: open your codeplug in the CPS, choose Tool > Import > Import From File List, pick " +
                    Path.GetFileName(dlg.FileName) + ", click Import, then write to the radio.\n\n" +
                    (numbered > 0 ? Plural(numbered, "channel") + " got a channel number that will now stay the same; save the project (File > Save) to keep it.\n\n" : "") +
                    "Open the folder now?",
                    "Codeplug saved", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                {
                    try { Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\""); } catch { }
                }
            }
        }

        void ShowHowTo()
        {
            Ui.Info(this,
                "1. Click \"Generate CSV files...\" and save the .LST file list. The CSV files are saved next to it.\n\n" +
                "2. In the DMR-6X2 PRO CPS, open your current codeplug (or read it from the radio).\n\n" +
                "3. Tool > Import > Import From File List, pick the .LST, then click Import.\n" +
                "    This replaces the channels, zones, talk groups and receive group lists in the CPS. Other settings stay as they are.\n\n" +
                "4. Check a few channels, save the codeplug, and write it to the radio.\n\n" +
                "If the file list won't load, use Tool > Import and pick each CSV yourself, in this order: " +
                "TalkGroups, ReceiveGroupCallList, Channel, ScanList (if made), Zone.",
                "Loading the files into the CPS");
        }

        void ShowAbout()
        {
            Ui.Info(this,
                "DMR Codeplug Builder 1.2\n\n" +
                "Builds CSV codeplug files for the BTECH DMR-6X2 PRO's CPS from your talkgroups, repeaters and hotspot.\n\n" +
                "Online data: DMR repeaters and IDs from RadioID.net, talkgroup names from BrandMeister.\n\n" +
                "Built-in map: boundaries from the US Census Bureau and Natural Earth (public domain); place names from " +
                "GeoNames (geonames.org), licensed under CC BY 4.0.\n\n" +
                "CPS format: " + session.Format.Source + "\n" +
                "Settings folder: " + AppSettings.Folder,
                "About");
        }
    }
}
