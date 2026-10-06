using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;
using CodeplugBuilder.Core.Radio;

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
            Text = Ui.AppName;
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
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Keys.None, (s, e) => Close()));
            var export = new ToolStripMenuItem("E&xport");
            export.DropDownItems.Add(ProjectItem(Item("&Export CSV files for the CPS...", Keys.Control | Keys.G, (s, e) => Generate())));
            export.DropDownItems.Add(Item("&How to load them into the CPS", Keys.None, (s, e) => ShowHowTo()));
            var radio = new ToolStripMenuItem("&Radio");
            radio.DropDownItems.Add(Item("&Read codeplug from radio...", Keys.Control | Keys.R, (s, e) => ImportFromRadio()));
            radio.DropDownItems.Add(ProjectItem(Item("&Write codeplug to radio...", Keys.None, (s, e) => WriteProjectToRadio())));
            radio.DropDownItems.Add(Item("Restore codeplug from a &backup...", Keys.None, (s, e) => RestoreRadioBackup()));
            radio.DropDownItems.Add(new ToolStripSeparator());
            radio.DropDownItems.Add(Item("Radio &settings (read and write)...", Keys.None, (s, e) => { using (var f = new RadioSettingsForm(null)) f.ShowDialog(this); }));
            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&Loading the files into the CPS", Keys.F1, (s, e) => ShowHowTo()));
            help.DropDownItems.Add(ProjectItem(Item("Check for &problems", Keys.None, (s, e) => { RefreshStatus(); IssuesDialog.ShowIssues(this, lastIssues); })));
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add(Item("&About", Keys.None, (s, e) => ShowAbout()));
            menu.Items.Add(file);
            menu.Items.Add(export);
            menu.Items.Add(radio);
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

            // ---------- bottom bar: the radio ----------
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                ColumnCount = 4,
                AutoSize = true,
                Padding = new Padding(Ui.S(10), Ui.S(6), Ui.S(10), Ui.S(8)),
                BackColor = SystemColors.ControlLight,
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, Ui.S(8), 3, 3) };
            lnkIssues = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, Ui.S(8), Ui.S(14), 3), LinkBehavior = LinkBehavior.HoverUnderline };
            lnkIssues.LinkClicked += (s, e) => IssuesDialog.ShowIssues(this, lastIssues);
            Button BarButton(string text, bool primary, EventHandler click)
            {
                var b = new Button
                {
                    Text = text,
                    AutoSize = true,
                    Font = primary ? Ui.BoldFont : Ui.BaseFont,
                    Padding = new Padding(Ui.S(12), Ui.S(4), Ui.S(12), Ui.S(4)),
                    Anchor = AnchorStyles.Right,
                    UseVisualStyleBackColor = true,
                };
                b.Click += click;
                return b;
            }
            bar.Controls.Add(lblStatus, 0, 0);
            bar.Controls.Add(lnkIssues, 1, 0);
            bar.Controls.Add(BarButton("Read from radio...", false, (s, e) => ImportFromRadio()), 2, 0);
            bar.Controls.Add(BarButton("Write to radio...", true, (s, e) => WriteProjectToRadio()), 3, 0);

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
                          "Check it on the Repeaters and Zones tabs, save it (File > Save), then use Write to radio (bottom right) or Export > Export CSV files for the CPS.";
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
            Text = mode == Mode.Start ? Ui.AppName
                 : mode == Mode.Wizard ? "New codeplug - " + Ui.AppName
                 : session.DisplayName + (session.Dirty ? " *" : "") + " - " + Ui.AppName;
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
            var answer = MessageBox.Show(this, "Save changes to " + session.DisplayName + "?", Ui.AppName,
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
                ImportFolder(Path.GetDirectoryName(dlg.FileName), "export", false);
            }
        }

        /// <summary>Makes a project from a folder of CPS CSVs: an Export All, or a read from the radio.</summary>
        void ImportFolder(string folder, string what, bool fromRadio)
        {
            {
                ImportResult result;
                try { result = CpsImporter.Import(folder); }
                catch (Exception ex) { Ui.Error(this, "Couldn't import that " + what + ":\n\n" + ex.Message); return; }

                var notes = new List<string>(result.Notes);
                if (fromRadio) notes.Insert(0, "The radio's memory and these CSV files were saved in " + folder + " (a backup of what was on the radio).");
                if (!fromRadio) try
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

        /// <summary>Radio > Read codeplug from radio: reads, keeps the image and CSVs as a backup, opens them as a project.</summary>
        void ImportFromRadio()
        {
            if (wizard != null) return;
            if (InWorkspace && !ConfirmDiscard()) return;
            string folder = RadioPort.NewReadFolder();
            MemoryImage img;
            try { img = RadioProgressDialog.Run(this, "Reading the radio", p => RadioPort.Read(RadioPort.Choose(null, null), p)); }
            catch (Exception ex) { Ui.Error(this, "Reading the radio failed:\n\n" + ex.Message); return; }
            try
            {
                Directory.CreateDirectory(folder);
                img.Save(Path.Combine(folder, "radio.img"));
                RadioCsv.WriteTo(RadioCsv.ToTables(RadioCodeplug.Decode(img), CpsFormat.BuiltIn()), folder);
            }
            catch (Exception ex) { Ui.Error(this, "Couldn't decode what was read:\n\n" + ex.Message); return; }
            ImportFolder(folder, "read from the radio", true);
        }

        /// <summary>
        /// Radio > Write codeplug to radio: reads the radio (kept as a backup), generates this project, writes its
        /// channels, zones, talkgroups, RX and scan lists and radio ID into that read (RadioEncoder), shows what changes,
        /// then sends it the way the BTECH CPS does and checks it. Settings on the radio stay as they are.
        /// </summary>
        void WriteProjectToRadio()
        {
            if (!InWorkspace) return;
            Validate(); // commits any cell being edited
            var p = session.Project;
            p.SyncZones();
            var issues = Validator.Validate(p, session.Format);
            var errors = issues.Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
            if (errors.Count > 0)
            {
                using (var d = new IssuesDialog("Fix these before writing to the radio:", errors, issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message), false))
                    d.ShowDialog(this);
                return;
            }

            GeneratedCodeplug g = null;
            WriteCodeplugToRadio("this project", (original, before) =>
            {
                // "Keep channels made in the CPS": what's on the radio is the base to keep them from.
                g = CodeplugGenerator.Generate(p, session.Format, p.Options.KeepCpsChannels ? CpsExport.Load(before) : null);
                if (g.ChannelList.Count + g.KeptChannels.Count > p.Options.MaxChannels)
                    throw new InvalidOperationException("This codeplug has " + (g.ChannelList.Count + g.KeptChannels.Count) + " channels; the radio holds " + p.Options.MaxChannels + ".");
                var notes = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message).Concat(g.Notes).ToList();
                return new KeyValuePair<Dictionary<string, CsvTable>, List<string>>(g.Files().ToDictionary(f => f.Key, f => f.Value), notes);
            }, () =>
            {
                // The radio now has these channel numbers: keep them, as after Generate.
                int numbered = CodeplugGenerator.KeepChannelNumbers(g);
                bool remembered = CodeplugGenerator.RememberOutput(p, g);
                if (numbered > 0 || remembered) session.MarkDirty();
                return numbered > 0 ? "\n\n" + Plural(numbered, "channel") + " got a channel number that will now stay the same; save the project (File > Save) to keep it." : "";
            });
        }

        /// <summary>Radio > Restore codeplug from a backup: writes the codeplug of a saved read (radio.img / before.img) back.</summary>
        void RestoreRadioBackup()
        {
            string path;
            using (var dlg = new OpenFileDialog
            {
                Title = "Pick a saved read of the radio (radio.img, or before.img from a write)",
                Filter = "Radio memory image (*.img)|*.img",
                InitialDirectory = Directory.Exists(RadioPort.ReadsFolder) ? RadioPort.ReadsFolder : AppSettings.DocumentsFolder,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            MemoryImage backup;
            try { backup = MemoryImage.Load(path); }
            catch (Exception ex) { Ui.Error(this, "Couldn't open that image:\n\n" + ex.Message); return; }
            string when = backup.ReadAtUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
            WriteCodeplugToRadio("the backup read " + when, (original, before) =>
            {
                var cp = RadioCodeplug.Decode(backup);
                var notes = cp.Warnings.ToList();
                notes.Insert(0, "Settings stay as they are on the radio now; only the channels, zones, talkgroups, RX and scan lists and radio IDs come from the backup.");
                return new KeyValuePair<Dictionary<string, CsvTable>, List<string>>(RadioCsv.ToTables(cp, CpsFormat.BuiltIn()), notes);
            }, () => "");
        }

        /// <summary>
        /// Shared by Write and Restore: read the radio, build the tables (<paramref name="build"/> gets the read and the
        /// folder with its CSVs), encode, review, write, verify. <paramref name="after"/> runs after a good write and
        /// returns extra text for the closing message.
        /// </summary>
        void WriteCodeplugToRadio(string what, Func<MemoryImage, string, KeyValuePair<Dictionary<string, CsvTable>, List<string>>> build, Func<string> after)
        {
            // 1. What's on the radio now: the base to write into, and the backup.
            string folder = RadioPort.NewReadFolder(" write");
            MemoryImage original;
            try { original = RadioProgressDialog.Run(this, "Reading the radio", pr => RadioPort.Read(RadioPort.Choose(null, null), pr)); }
            catch (Exception ex) { Ui.Error(this, "Reading the radio failed:\n\n" + ex.Message); return; }
            string before = Path.Combine(folder, "before");
            try
            {
                Directory.CreateDirectory(before);
                original.Save(Path.Combine(folder, "before.img"));
                RadioCsv.WriteTo(RadioCsv.ToTables(RadioCodeplug.Decode(original), CpsFormat.BuiltIn()), before);
            }
            catch (Exception ex) { Ui.Error(this, "Couldn't save or decode what was read:\n\n" + ex.Message); return; }

            // 2. The codeplug, written into that read.
            Dictionary<string, CsvTable> tables;
            List<string> notes;
            EncodeResult enc;
            int changed;
            try
            {
                var built = build(original, before);
                tables = built.Key;
                notes = built.Value;
                enc = RadioEncoder.Encode(original, tables, session.Format);
                if (enc.Errors.Count == 0)
                {
                    RadioWriter.BlocksToWrite(original, enc.Image); // refuses an image that doesn't hold everything a write sends
                }
                changed = enc.Errors.Count == 0 ? RadioWriter.ChangedBlocks(original, enc.Image).Count : 0;
            }
            catch (Exception ex) { Ui.Error(this, "Couldn't prepare the write:\n\n" + ex.Message + "\n\nNothing was written. What was read is in " + folder + "."); return; }
            if (enc.Errors.Count > 0)
            {
                using (var d = new IssuesDialog("This can't be written to the radio. Nothing was written.", enc.Errors, notes.Concat(enc.Notes), false))
                    d.ShowDialog(this);
                return;
            }
            if (changed == 0)
            {
                Ui.Info(this, "The radio already holds " + what + ". Nothing to write.");
                return;
            }

            // 3. Show what changes, then ask.
            var now = RadioCodeplug.Decode(original);
            var next = RadioCodeplug.Decode(enc.Image);
            var oldNames = new HashSet<string>(now.Channels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var newNames = new HashSet<string>(next.Channels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            var added = next.Channels.Where(c => !oldNames.Contains(c.Name)).Select(c => c.Name).ToList();
            var removed = now.Channels.Where(c => !newNames.Contains(c.Name)).Select(c => c.Name).ToList();
            if (added.Count > 0) lines.Add("New channels (" + added.Count + "): " + Shorten(added));
            if (removed.Count > 0) lines.Add("Channels taken off the radio (" + removed.Count + "): " + Shorten(removed));
            lines.AddRange(notes.Concat(enc.Notes).Distinct());
            string head = "Write " + what + " to the radio?\n\n" +
                          "On the radio now:  " + Counts(now) + "\n" +
                          "After writing:       " + Counts(next) + "\n\n" +
                          "This replaces the channels, zones, talkgroups, RX group lists, scan lists and radio IDs on the radio. Its settings stay as they are.";
            using (var d = new IssuesDialog(head, new string[0], lines, true, "Write to radio"))
                if (d.ShowDialog(this) != DialogResult.OK) return;
            string text = "Ready to write.\n\n" +
                          "  - Close the BTECH CPS if it's open.\n" +
                          "  - Don't touch the radio or unplug the cable until this is done (about half a minute; the radio restarts).\n\n" +
                          "What's on the radio now was saved in:\n" + folder + "\n" +
                          "Radio > Restore codeplug from a backup puts it back (pick before.img there). A CPS codeplug file (.rdt) is a good second backup.";
            if (MessageBox.Show(this, text, "Write to radio", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            // 4. Write and check.
            try
            {
                enc.Image.Save(Path.Combine(folder, "written.img"));
                RadioCsv.WriteTo(tables.ToDictionary(kv => kv.Key, kv => kv.Value), Path.Combine(folder, "written"));
            }
            catch (Exception ex) { Ui.Error(this, "Couldn't save the backup of what's being written:\n\n" + ex.Message + "\n\nNothing was written."); return; }
            WriteResult result;
            RadioWriter.Enabled = true;
            try
            {
                result = RadioProgressDialog.Run(this, "Writing to the radio", pr => RadioPort.Write(RadioPort.Choose(null, null), original, enc.Image, pr),
                    "Writing to the radio. Don't touch the radio or unplug the cable until this window closes.");
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); } catch { }
                bool wrote = ex.Message.Contains("after reconnecting") || ex.Message.Contains("couldn't reconnect");
                Ui.Error(this, (wrote
                    ? "The radio was written, but checking it afterwards failed:\n\n" + ex.Message + "\n\nUse Radio > Read codeplug from radio to see what it holds now."
                    : "Writing failed:\n\n" + ex.Message) +
                    "\n\nTo put the old codeplug back: Radio > Restore codeplug from a backup, and pick before.img in " + folder + ". If the radio misbehaves, write a saved codeplug with the BTECH CPS.");
                return;
            }
            finally
            {
                RadioWriter.Enabled = false;
            }
            try { File.WriteAllLines(Path.Combine(folder, "write.log"), result.Log.Concat(lines.Select(l => "Note: " + l))); } catch { }
            string extra = after();
            Ui.Info(this, "Done: " + Counts(next) + " written and checked on the radio." + extra +
                          "\n\nWhat was on the radio before, and what was written, are saved in:\n" + folder, "Write to radio");
        }

        static string Counts(RadioCodeplug cp) =>
            Plural(cp.Channels.Count, "channel") + ", " + Plural(cp.Zones.Count, "zone") + ", " + Plural(cp.Contacts.Count, "talkgroup") + ", " +
            Plural(cp.GroupLists.Count, "RX list") + ", " + Plural(cp.ScanLists.Count, "scan list");

        static string Shorten(List<string> names) =>
            string.Join(", ", names.Take(12)) + (names.Count > 12 ? " and " + (names.Count - 12) + " more" : "");

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
                "1. Click Export > Export CSV files for the CPS and save the .LST file list. The CSV files are saved next to it.\n\n" +
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
                Ui.AppName + " 1.3\n\n" +
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
