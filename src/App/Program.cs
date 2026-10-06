using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--generate" || args[0] == "--import" || args[0] == "--help"))
                return Cli.Run(args);
            if (args.Length > 0 && args[0].StartsWith("--radio-", StringComparison.Ordinal) && args[0] != "--radio-settings-ui" && args[0] != "--radio-settings-snapshot")
                return RadioCli.Run(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = Math.Max(1f, g.DpiX / 96f);
            }
            catch { }
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nYour project is still open; save it before trying again.",
                    Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);

            string path = null, tab = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--tab" && i + 1 < args.Length) tab = args[++i];
                else if (args[i] == "--map") { Application.Run(new MapPreviewForm()); return 0; }
                else if (args[i] == "--radio-settings-snapshot" && i + 2 < args.Length) return RadioSettingsForm.Snapshot(args[i + 1], args[i + 2]);
                else if (args[i] == "--radio-settings-ui") { Application.Run(new RadioSettingsForm(i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null)); return 0; }
                else if (args[i] == "--map-snapshot" && i + 1 < args.Length) return MapSnapshots.Run(args[i + 1]);
                else if (args[i] == "--repeaterbook-snapshot" && i + 3 < args.Length) return RepeaterBookSnapshot.Run(args[i + 1], args[i + 2], args[i + 3]);
                else if (args[i] == "--ui-walkthrough" && i + 1 < args.Length) return UiWalkthrough.Run(args[i + 1], i + 2 < args.Length ? args[i + 2] : "W6OZZ", i + 3 < args.Length ? args[i + 3] : null);
                else if (File.Exists(args[i])) path = Path.GetFullPath(args[i]);
            }
            Application.Run(new MainForm(path, tab));
            return 0;
        }
    }

    /// <summary>
    /// Command line, for scripting and testing:
    ///   CodeplugBuilder.exe --generate project.cpb outFolder [--format cpsExportFolder] [--merge cpsExportFolder]
    ///   CodeplugBuilder.exe --import cpsExportFolder project.cpb
    /// A log is written next to the output because a Windows GUI program has no console.
    /// </summary>
    static class Cli
    {
        public static int Run(string[] args)
        {
            var log = new List<string>();
            string logPath = null;
            int code;
            try
            {
                if (args[0] == "--generate" && args.Length >= 3)
                {
                    logPath = Path.Combine(args[2], "CodeplugBuilder.log");
                    var p = ProjectStore.Load(args[1]);
                    int fi = Array.IndexOf(args, "--format");
                    var format = fi > 0 && fi + 1 < args.Length ? CpsFormat.FromFolder(args[fi + 1]) : CpsFormat.BuiltIn();
                    // Merge mode: --merge exportFolder, or the project's own setting.
                    int mi = Array.IndexOf(args, "--merge");
                    string mergeFolder = mi > 0 && mi + 1 < args.Length ? args[mi + 1] : p.Options.KeepCpsChannels ? p.Options.BaseExportFolder : null;
                    var mergeBase = mergeFolder != null ? CpsExport.Load(mergeFolder) : null;
                    var issues = Validator.Validate(p, format);
                    log.AddRange(issues.Select(i => i.ToString()));
                    if (issues.Any(i => i.Severity == Severity.Error))
                    {
                        code = 2;
                    }
                    else
                    {
                        var g = CodeplugGenerator.Generate(p, format, mergeBase);
                        log.AddRange(g.Notes.Select(n => "Note: " + n));
                        foreach (var f in g.WriteTo(args[2])) log.Add("Wrote " + f);
                        log.Add(g.ChannelList.Count + " channels" + (g.KeptChannels.Count > 0 ? " + " + g.KeptChannels.Count + " kept from the CPS" : "") + ", " + g.ZoneList.Count + " zones, " + g.TalkGroups.Rows.Count + " talkgroups, " + g.RxGroupLists.Rows.Count + " RX group lists");
                        code = 0;
                    }
                }
                else if (args[0] == "--import" && args.Length >= 3)
                {
                    logPath = Path.ChangeExtension(args[2], ".log");
                    var r = CpsImporter.Import(args[1]);
                    ProjectStore.Save(r.Project, args[2]);
                    log.AddRange(r.Notes.Select(n => "Note: " + n));
                    log.Add("Saved " + args[2] + ": " + r.Project.Talkgroups.Count + " talkgroups, " + r.Project.Repeaters.Count + " repeaters/channels, " + r.Project.Zones.Count + " zones");
                    code = 0;
                }
                else
                {
                    log.Add("Usage:");
                    log.Add("  CodeplugBuilder.exe --generate project.cpb outFolder [--format cpsExportFolder] [--merge cpsExportFolder]");
                    log.Add("  CodeplugBuilder.exe --import cpsExportFolder project.cpb");
                    code = args[0] == "--help" ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                log.Add("Error: " + ex.Message);
                code = 1;
            }
            foreach (var l in log) Console.WriteLine(l);
            if (logPath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
                    File.WriteAllLines(logPath, log);
                }
                catch { }
            }
            return code;
        }
    }
}
