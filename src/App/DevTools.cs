using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Developer self-test: CodeplugBuilder.exe --ui-walkthrough folder [callsign]
    /// Runs the start page and the whole new-codeplug wizard off screen (Texas, a few counties, zones, starter
    /// talkgroups) with the real downloads, saves a PNG of every page, generates the CSVs into folder\csv and
    /// writes walkthrough.log. Set CODEPLUGBUILDER_SETTINGS to a scratch folder so your settings stay untouched.
    /// </summary>
    static class UiWalkthrough
    {
        public static int Run(string folder, string callsign)
        {
            Directory.CreateDirectory(folder);
            var log = new List<string>();
            int shot = 0;
            bool ok = false;
            try
            {
                using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Opacity = 0,
                                          ClientSize = new Size(Ui.S(1200), Ui.S(800)), Font = Ui.BaseFont, Text = "walkthrough" })
                {
                    f.Show();
                    void Shot(string name)
                    {
                        Pump(300);
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size)); // whole window: DrawToBitmap includes the title bar
                            bmp.Save(Path.Combine(folder, (++shot).ToString("00") + "-" + name + ".png"), ImageFormat.Png);
                        }
                        log.Add("shot " + shot + ": " + name);
                    }

                    // ---- start page ----
                    var start = new StartPage { Dock = DockStyle.Fill };
                    f.Controls.Add(start);
                    Shot("start-page");
                    f.Controls.Remove(start);

                    // ---- wizard ----
                    var wiz = new NewCodeplugWizard { Dock = DockStyle.Fill };
                    f.Controls.Add(wiz);
                    var region = (RegionStep)wiz.Current;
                    if (!WaitFor(() => region.Picker.Map.Atlas != null, 10000)) throw new Exception("atlas didn't load");
                    log.Add("step 1 Next enabled before picking: " + wiz.NextEnabled + " (" + wiz.BlockerText + ")");
                    var atlas = region.Picker.Map.Atlas;
                    region.Picker.Map.ClickArea(atlas.Find("US-TX"));
                    Shot("region-texas");
                    log.Add("step 1 Next enabled: " + wiz.NextEnabled);
                    wiz.PressNext();

                    var radio = (RadioStep)wiz.Current;
                    log.Add("step 2 Next enabled with no name: " + wiz.NextEnabled + " (" + wiz.BlockerText + ")");
                    radio.CallBox.Text = callsign;
                    radio.PressLookup();
                    WaitFor(() => wiz.NextEnabled, 20000);
                    Shot("radio");
                    log.Add("step 2 after lookup: Radio ID " + wiz.State.RadioId + ", name \"" + wiz.State.RadioIdName + "\", Next " + wiz.NextEnabled);
                    if (!wiz.NextEnabled) throw new Exception("radio step: " + wiz.BlockerText);
                    wiz.PressNext();

                    var areas = (AreasStep)wiz.Current;
                    var sw = Stopwatch.StartNew();
                    if (!WaitFor(() => wiz.State.Download.Done, 120000)) throw new Exception("download didn't finish");
                    Pump(500);
                    log.Add("download: " + wiz.State.Download.Repeaters.Count + " repeaters in " + sw.ElapsedMilliseconds + " ms; errors: " + string.Join("; ", wiz.State.Download.Errors) +
                            "; BrandMeister names: " + wiz.State.Download.BrandMeisterNames.Count);
                    log.Add("step 3 Next before picking: " + wiz.NextEnabled + " (" + wiz.BlockerText + ")");
                    Shot("areas-map");
                    foreach (var county in new[] { "Tom Green County", "Taylor County", "Lubbock County" })
                        areas.Chooser.Picker.Map.ClickArea(atlas.Counties.First(c => c.Name == county && c.ParentCode == "US-TX"));
                    areas.Chooser.Picker.Map.ZoomToAreas(new[] { atlas.Find("US-TX") });
                    Shot("areas-picked");
                    log.Add("picked " + areas.Chooser.Picked().Count + " repeaters: " + string.Join(", ", areas.Chooser.Picked().Select(r => r.Callsign + " " + r.City)));
                    areas.Chooser.Tabs.SelectedIndex = 1;
                    Shot("areas-list");
                    areas.Chooser.Tabs.SelectedIndex = 0;
                    wiz.PressNext();

                    var zones = (ZonesStep)wiz.Current;
                    Shot("zones");
                    var p = wiz.State.Project;
                    log.Add("zones: " + string.Join(", ", p.Zones.Select(z => z.Name + " (" + p.ZoneChannelCount(z.Name) + ")")) + "; scheme " + wiz.State.Scheme);
                    wiz.PressNext();

                    var tgs = (ZoneTalkgroupsStep)wiz.Current;
                    Shot("zone-talkgroups-before");
                    int before = p.ChannelCount();
                    tgs.Editor.PressStarterSet();
                    foreach (var z in p.Zones.Where(z => z.Name != tgs.Editor.Zone && p.ZoneRepeaters(z.Name).Count > 0).ToList())
                        foreach (var t in p.FindZone(tgs.Editor.Zone).Talkgroups) p.AddZoneTalkgroup(z.Name, t.TalkgroupId, t.Slot); // what "Copy ticked to all zones" does after its confirmation
                    tgs.Editor.RefreshGrid();
                    // Tick an unticked talkgroup the way a user does (Space on the checkbox cell): this is the path
                    // that rebuilds the grid after CellValueChanged.
                    var grid = tgs.Editor.Grid;
                    var untickedRow = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => !(r.Cells[0].Value is bool on && on));
                    if (untickedRow != null)
                    {
                        int tickId = (int)untickedRow.Tag;
                        grid.Focus();
                        grid.CurrentCell = untickedRow.Cells[0];
                        PostKey(f, Keys.Space, grid);
                        Pump(600);
                        bool nowInSet = p.FindZone(tgs.Editor.Zone)?.FindTalkgroup(tickId) != null;
                        bool rowShowsTicked = grid.Rows.Cast<DataGridViewRow>().Any(r => (int)r.Tag == tickId && r.Cells[0].Value is bool b2 && b2);
                        log.Add("zone grid: Space on talkgroup " + tickId + " -> in the zone set: " + nowInSet + ", row shows ticked: " + rowShowsTicked);
                    }
                    Shot("zone-talkgroups-after");
                    log.Add("channels: " + before + " before the starter set, " + p.ChannelCount() + " after; zone sets: " +
                            string.Join("; ", p.Zones.Where(z => z.HasTalkgroups).Select(z => z.Name + " = " + string.Join(",", z.Talkgroups.Select(t => t.TalkgroupId + "/TS" + t.Slot)))));

                    Project finished = null;
                    wiz.Finished += (s, proj) => finished = proj;
                    wiz.PressNext();
                    if (finished == null) throw new Exception("Finish didn't finish: " + wiz.BlockerText);
                    f.Controls.Remove(wiz);

                    // ---- the workspace pages on the new project ----
                    var session = new Session { Format = CpsFormat.BuiltIn() };
                    session.Replace(finished, null, true);
                    var zp = new ZonesPage(session) { Dock = DockStyle.Fill };
                    f.Controls.Add(zp);
                    zp.Reload(null); // in the app this happens when the tab is shown
                    Pump(500);
                    Shot("zones-tab");
                    f.Controls.Remove(zp);
                    var rp = new RepeatersPage(session) { Dock = DockStyle.Fill };
                    f.Controls.Add(rp);
                    Shot("repeaters-tab");
                    f.Controls.Remove(rp);
                    var sp = new SettingsPage(session) { Dock = DockStyle.Fill };
                    f.Controls.Add(sp);
                    Shot("settings-tab");
                    f.Controls.Remove(sp);

                    // ---- generate ----
                    var issues = Validator.Validate(finished, session.Format);
                    log.AddRange(issues.Select(i => "validator: " + i));
                    var g = CodeplugGenerator.Generate(finished, session.Format);
                    log.AddRange(g.Notes.Select(n => "generator note: " + n));
                    string csv = Path.Combine(folder, "csv");
                    foreach (var file in g.WriteTo(csv)) log.Add("wrote " + Path.GetFileName(file));
                    log.Add("generated " + g.ChannelList.Count + " channels in " + g.ZoneList.Count + " zones, " + g.TalkGroups.Rows.Count + " talkgroups, " + g.RxGroupLists.Rows.Count + " RX group lists");
                    ProjectStore.Save(finished, Path.Combine(folder, "walkthrough.cpb"));
                    log.Add("memory: " + (GC.GetTotalMemory(true) / 1048576) + " MB managed, " + (Process.GetCurrentProcess().WorkingSet64 / 1048576) + " MB working set");
                    ok = issues.All(i => i.Severity != Severity.Error);

                    // ---- IssuesDialog: real window messages (key and mouse) into the modal dialog ----
                    log.Add("IssuesDialog, Escape key: " + DialogTest(f, false, d => PostKey(d, Keys.Escape)));
                    log.Add("IssuesDialog, Enter key: " + DialogTest(f, false, d => PostKey(d, Keys.Enter)));
                    log.Add("IssuesDialog, mouse click on Close: " + DialogTest(f, false, d => PostClick(Find<Button>(d, "Close"))));
                    log.Add("IssuesDialog, click in the text, then Escape: " + DialogTest(f, false, d => { PostClick(Find<TextBox>(d, null)); PostKey(d, Keys.Escape); }));
                    log.Add("IssuesDialog (Generate anyway), Escape: " + DialogTest(f, true, d => PostKey(d, Keys.Escape)));
                    log.Add("IssuesDialog (Generate anyway), click Generate anyway: " + DialogTest(f, true, d => PostClick(Find<Button>(d, "Generate anyway"))));
                    // Control: the same posted click on a bare dialog with one OK button.
                    using (var plain = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Opacity = 0, ShowInTaskbar = false })
                    {
                        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(20, 20) };
                        plain.Controls.Add(okButton);
                        bool timedOut = false;
                        var watchdog = new System.Windows.Forms.Timer { Interval = 4000 };
                        watchdog.Tick += (s, e) => { watchdog.Stop(); timedOut = true; plain.Close(); };
                        plain.Shown += (s, e) => { plain.BeginInvoke((Action)(() => PostClick(okButton))); watchdog.Start(); };
                        var r = plain.ShowDialog(f);
                        watchdog.Dispose();
                        log.Add("control dialog, mouse click on OK: " + (timedOut ? "DID NOT CLOSE" : "closed with " + r));
                    }
                    f.Close();
                }
            }
            catch (Exception ex)
            {
                log.Add("ERROR " + ex);
            }
            log.Add(ok ? "ok" : "FAILED");
            File.WriteAllLines(Path.Combine(folder, "walkthrough.log"), log);
            return ok ? 0 : 1;
        }

        /// <summary>Shows an IssuesDialog modally off screen, sends it input once it's up, and reports how it closed.</summary>
        static string DialogTest(Form owner, bool canContinue, Action<Form> input)
        {
            using (var d = new IssuesDialog("Imported 9 talkgroups and 3 zones. Save the project (File > Save) to keep it.", new string[0],
                                            new[] { "A note about something", "Another note" }, canContinue))
            {
                d.StartPosition = FormStartPosition.Manual;
                d.Location = new Point(-20000, -20000);
                d.Opacity = 0;
                string focus = "";
                var watchdog = new System.Windows.Forms.Timer { Interval = 4000 };
                watchdog.Tick += (s, e) => { watchdog.Stop(); d.Tag = "timeout"; d.Close(); };
                d.Shown += (s, e) =>
                {
                    focus = d.ActiveControl?.GetType().Name + " \"" + d.ActiveControl?.Text + "\"";
                    d.BeginInvoke((Action)(() => input(d)));
                    watchdog.Start();
                };
                var result = d.ShowDialog(owner);
                watchdog.Dispose();
                return (d.Tag as string == "timeout" ? "DID NOT CLOSE" : "closed with " + result) + " (focus was on " + focus + ")";
            }
        }

        static T Find<T>(Control root, string text) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                if (c is T t && (text == null || c.Text == text)) return t;
                var inner = Find<T>(c, text);
                if (inner != null) return inner;
            }
            return null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>A key press as Windows delivers it: WM_KEYDOWN/WM_KEYUP to the focused control.</summary>
        static void PostKey(Form d, Keys key, Control to = null)
        {
            var target = to ?? d.ActiveControl ?? d;
            PostMessage(target.Handle, 0x0100, (IntPtr)(int)key, (IntPtr)1);
            PostMessage(target.Handle, 0x0101, (IntPtr)(int)key, (IntPtr)unchecked((int)0xC0000001));
        }

        /// <summary>A left click in the middle of a control: WM_LBUTTONDOWN/WM_LBUTTONUP.</summary>
        static void PostClick(Control c)
        {
            if (c == null) return;
            var lp = (IntPtr)((c.Height / 2 << 16) | (c.Width / 2));
            PostMessage(c.Handle, 0x0201, (IntPtr)1, lp);
            PostMessage(c.Handle, 0x0202, IntPtr.Zero, lp);
        }

        static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(15); }
        }

        static bool WaitFor(Func<bool> condition, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (condition()) return true;
                Thread.Sleep(25);
            }
            return condition();
        }
    }

    /// <summary>
    /// Developer aid: CodeplugBuilder.exe --map-snapshot folder draws a few map views off screen into PNG files
    /// (and map-snapshot.log), so the drawing can be checked without driving the UI.
    /// </summary>
    static class MapSnapshots
    {
        public static int Run(string folder)
        {
            var log = new List<string>();
            Directory(folder);
            try
            {
                using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Opacity = 0, ClientSize = new Size(Ui.S(1000), Ui.S(680)), Font = Ui.BaseFont })
                {
                    var picker = new RegionPicker { Dock = DockStyle.Fill };
                    f.Controls.Add(picker);
                    f.Show();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    picker.Map.Atlas = GeoAtlas.BuiltIn();
                    log.Add("atlas " + sw.ElapsedMilliseconds + " ms");
                    var atlas = picker.Map.Atlas;
                    var tx = atlas.Find("US-TX");

                    void Shot(string name, Action setup)
                    {
                        setup();
                        Application.DoEvents();
                        var t = System.Diagnostics.Stopwatch.StartNew();
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size)); // whole window: DrawToBitmap includes the title bar
                            bmp.Save(System.IO.Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        log.Add(name + ": drawn in " + t.ElapsedMilliseconds + " ms");
                    }

                    Shot("1-world-states", () => { picker.Level = AreaLevel.State; picker.Map.ZoomWorld(); });
                    Shot("2-world-countries-picked", () =>
                    {
                        picker.Level = AreaLevel.Country;
                        picker.Map.SetSelected(new[] { atlas.Find("US"), atlas.Find("CA"), atlas.Find("DE") });
                        picker.Map.ZoomWorld();
                    });
                    Shot("3-north-america-states", () =>
                    {
                        picker.Level = AreaLevel.State;
                        picker.Map.SetSelected(new[] { tx, atlas.Find("US-OK"), atlas.Find("CA-ON") });
                        picker.Map.ZoomToAreas(new[] { atlas.Find("US-WA"), atlas.Find("US-FL"), atlas.Find("US-ME"), atlas.Find("US-CA") });
                    });
                    Shot("4-texas-counties", () =>
                    {
                        picker.Level = AreaLevel.County;
                        picker.Map.CanPick = a => a.Parent == tx;
                        var tomGreen = atlas.Counties.First(c => c.Name == "Tom Green County");
                        picker.Map.SetSelected(new[] { tomGreen, atlas.Counties.First(c => c.Name == "Harris County" && c.Parent == tx) });
                        picker.Map.Dots = atlas.Places.Where(p => p.Admin == "TX" && p.Weight == 2).Take(300).Select(p => new MapDot { Lon = p.Lon, Lat = p.Lat, Highlight = tomGreen.Contains(p.Lon, p.Lat) }).ToList();
                        picker.Map.Badge = a => a == tomGreen ? "3 repeaters" : null;
                        picker.Map.ZoomToAreas(new[] { tx });
                    });
                    Shot("5-europe-states", () =>
                    {
                        picker.Map.CanPick = null;
                        picker.Map.Dots = new List<MapDot>();
                        picker.Map.Badge = null;
                        picker.Level = AreaLevel.State;
                        picker.Map.SetSelected(new[] { atlas.Find("DE-BY") });
                        picker.Map.ZoomToAreas(new[] { atlas.Find("DE"), atlas.Find("FR"), atlas.Find("PL") });
                    });
                    f.Close();
                }
                log.Add("ok");
            }
            catch (Exception ex)
            {
                log.Add("ERROR " + ex);
            }
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "map-snapshot.log"), log);
            return log.Last() == "ok" ? 0 : 1;
        }

        static void Directory(string folder) { System.IO.Directory.CreateDirectory(folder); }
    }

    /// <summary>Developer aid: CodeplugBuilder.exe --map opens the map on its own.</summary>
    sealed class MapPreviewForm : Form
    {
        public MapPreviewForm()
        {
            Text = "Map preview - DMR Codeplug Builder";
            Font = Ui.BaseFont;
            Size = new Size(Ui.S(1100), Ui.S(760));
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(Ui.S(10));
            var picker = new RegionPicker { Dock = DockStyle.Fill };
            Controls.Add(picker);
            picker.Map.SelectionChanged += (s, e) =>
                picker.Status = picker.Map.Selected.Count + " picked: " + string.Join(", ", picker.Map.Selected.Select(a => a.Name));
            Shown += async (s, e) =>
            {
                await picker.LoadAtlasAsync();
                var tx = picker.Map.Atlas.Find("US-TX");
                picker.Map.Dots = picker.Map.Atlas.Places.Where(p => p.Country == "US" && p.Admin == "TX" && p.Weight == 2).Take(400)
                    .Select(p => new MapDot { Lon = p.Lon, Lat = p.Lat }).ToList();
                picker.Map.Badge = a => a == tx ? "400 places" : null;
            };
        }
    }
}
