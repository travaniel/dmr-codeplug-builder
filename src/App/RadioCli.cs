using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CodeplugBuilder.Core;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Developer switches for talking to the radio (read only; not in the menus yet):
    ///   CodeplugBuilder.exe --radio-read outFolder [--port COM5] [--any-model]
    ///       reads the radio: outFolder\radio.img (raw memory, a backup) + the CPS CSVs + radio.log
    ///   CodeplugBuilder.exe --radio-decode radio.img outFolder
    ///       decodes a saved image again (after the decoder changed)
    ///   CodeplugBuilder.exe --radio-compare cpsExportFolder radioFolder
    ///       compares the CSVs with the CPS's own Export All of the same codeplug → radioFolder\compare.log
    /// The CSV folder imports like a CPS export: CodeplugBuilder.exe --import outFolder project.cpb
    /// </summary>
    static class RadioCli
    {
        public const string ImageFile = "radio.img";

        public static int Run(string[] args)
        {
            var log = new List<string>();
            string logPath = null;
            int code = 1;
            try
            {
                string cmd = args[0];
                if (cmd == "--radio-read" && args.Length >= 2)
                {
                    string folder = args[1];
                    Directory.CreateDirectory(folder);
                    logPath = Path.Combine(folder, "radio.log");
                    string port = RadioPort.Choose(Option(args, "--port"), log);
                    log.Add("Reading from " + port + "...");
                    var sw = Stopwatch.StartNew();
                    var img = RadioPort.Read(port, null, default(System.Threading.CancellationToken), args.Contains("--any-model"));
                    log.Add("Radio: " + img.Model + " " + img.Version + " (band code " + img.Bands + "), " + img.BlockCount + " blocks (" + img.BlockCount * 16 + " bytes) in " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                    string imgPath = Path.Combine(folder, ImageFile);
                    img.Save(imgPath);
                    log.Add("Saved " + imgPath);
                    Decode(img, folder, log);
                    code = 0;
                }
                else if (cmd == "--radio-decode" && args.Length >= 3)
                {
                    Directory.CreateDirectory(args[2]);
                    logPath = Path.Combine(args[2], "decode.log");
                    var img = MemoryImage.Load(args[1]);
                    log.Add("Image: " + img.Model + " " + img.Version + ", read " + img.ReadAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ", " + img.BlockCount + " blocks");
                    Decode(img, args[2], log);
                    code = 0;
                }
                else if (cmd == "--radio-settings" && args.Length >= 2)
                {
                    // --radio-settings radio.img [cpsExportFolder]  → settings.txt next to the image
                    var img = MemoryImage.Load(args[1]);
                    logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "settings.txt");
                    var values = RadioSettings.Read(img);
                    log.Add("Optional settings of " + img.Model + " " + img.Version + ", read " + img.ReadAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ": " + values.Count + " settings, " + values.Count(v => v.Def.Verified) + " checked against the CPS");
                    log.Add("");
                    log.Add(RadioSettings.Report(img).TrimEnd());
                    string csv = args.Length >= 3 ? CpsFormat.FindFile(args[2], "OptionalSetting.CSV") : null;
                    if (csv != null)
                    {
                        var diffs = RadioSettings.CompareWithCps(img, csv, out int compared);
                        log.Add("");
                        log.Add("Against " + csv + ": " + compared + " settings compared, " + diffs.Count + " differ.");
                        log.AddRange(diffs.Select(d => "  " + d));
                    }
                    code = 0;
                }
                else if (cmd == "--radio-set" && args.Length >= 4)
                {
                    // --radio-set radio.img edited.img Key=Value ...  (Value: a label like "Yellow", or the stored number)
                    var img = MemoryImage.Load(args[1]);
                    logPath = Path.ChangeExtension(Path.GetFullPath(args[2]), ".log");
                    foreach (string a in args.Skip(3))
                    {
                        int eq = a.IndexOf('=');
                        var d = eq > 0 ? RadioSettings.Find(a.Substring(0, eq)) : null;
                        if (d == null) throw new ArgumentException("Unknown setting \"" + a + "\". Use Key=Value with a key from RadioSettings.");
                        string value = a.Substring(eq + 1);
                        string before = RadioSettings.Read(img, d).Display;
                        if (d.Kind == SettingKind.Text) RadioSettings.WriteText(img, d, value);
                        else if (d.Kind == SettingKind.Choice)
                        {
                            var o = d.Options.FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
                            RadioSettings.Write(img, d, o.Value != null ? o.Key : long.Parse(value, CultureInfo.InvariantCulture));
                        }
                        else if (d.Kind == SettingKind.Flag) RadioSettings.Write(img, d, value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" ? 1 : 0);
                        else RadioSettings.Write(img, d, (long)Math.Round(decimal.Parse(value, CultureInfo.InvariantCulture) * 100000m));
                        log.Add(d.Label + ": " + before + " -> " + RadioSettings.Read(img, d).Display);
                    }
                    if (string.Equals(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Save the edited image under a new name; the read stays as the backup.");
                    img.Save(args[2]);
                    log.Add("Saved " + args[2]);
                    code = 0;
                }
                else if (cmd == "--radio-write-plan" && args.Length >= 4)
                {
                    // --radio-write-plan radio.img edited.img plan.txt : what a write would send, without the radio
                    var original = MemoryImage.Load(args[1]);
                    var edited = MemoryImage.Load(args[2]);
                    logPath = Path.ChangeExtension(Path.GetFullPath(args[3]), ".log");
                    var blocks = RadioWriter.BlocksToWrite(original, edited);
                    File.WriteAllLines(args[3], blocks.Select(b => b.ToString("X7") + " " + BitConverter.ToString(edited.Get(b, 16))));
                    log.Add(blocks.Count + " blocks would be written, " + RadioWriter.ChangedBlocks(original, edited).Count + " of them changed. List: " + args[3]);
                    code = 0;
                }
                else if (cmd == "--radio-write" && args.Length >= 3)
                {
                    // --radio-write radio.img edited.img --confirm [--port COM5]   (writing is off unless --confirm is given)
                    var original = MemoryImage.Load(args[1]);
                    var edited = MemoryImage.Load(args[2]);
                    logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2])), "radio-write.log");
                    if (!args.Contains("--confirm")) throw new InvalidOperationException("Add --confirm to really write to the radio (have a CPS codeplug file ready to restore from).");
                    RadioWriter.Enabled = true;
                    foreach (var d in RadioSettings.All.Where(x => original.Has(x.Address, x.Length)))
                    {
                        var a = RadioSettings.Read(original, d);
                        var b = RadioSettings.Read(edited, d);
                        if (a.Raw != b.Raw || a.Text != b.Text) log.Add("Setting " + d.Label + ": " + a.Display + " -> " + b.Display);
                    }
                    string port = RadioPort.Choose(Option(args, "--port"), log);
                    var sw = Stopwatch.StartNew();
                    var result = RadioPort.Write(port, original, edited);
                    log.AddRange(result.Log);
                    log.Add("Done in " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                    code = 0;
                }
                else if (cmd == "--radio-encode" && args.Length >= 4)
                {
                    // --radio-encode radio.img (project.cpb | csvFolder) outFolder [--merge-radio]
                    //   writes the codeplug into the read: outFolder\edited.img, the radio's own CSVs (outFolder\before),
                    //   the edited image decoded again (outFolder\after), plan.txt (what a write would send). No radio needed.
                    string folder = args[3];
                    Directory.CreateDirectory(folder);
                    logPath = Path.Combine(folder, "encode.log");
                    var original = MemoryImage.Load(args[1]);
                    var result = Encode(original, args[2], folder, args.Contains("--merge-radio"), log);
                    code = result.Errors.Count == 0 ? 0 : 3;
                }
                else if (cmd == "--radio-compare" && args.Length >= 3)
                {
                    logPath = Path.Combine(args[2], "compare.log");
                    var diffs = RadioCsv.Compare(args[1], args[2]);
                    log.Add("CPS export: " + args[1]);
                    log.Add("Radio read: " + args[2]);
                    log.Add(diffs.Count == 0 ? "No differences." : diffs.Count + " differences:");
                    log.AddRange(diffs);
                    // A summary by column shows which mappings are off.
                    var byColumn = diffs.Select(d => d.IndexOf('[') >= 0 ? d.Substring(0, d.IndexOf(" row ")) + " " + d.Substring(d.IndexOf('['), d.IndexOf(']') - d.IndexOf('[') + 1) : null)
                        .Where(k => k != null).GroupBy(k => k).OrderByDescending(g => g.Count());
                    if (byColumn.Any())
                    {
                        log.Add("");
                        log.Add("By column:");
                        foreach (var g in byColumn) log.Add("  " + g.Count() + "  " + g.Key);
                    }
                    code = diffs.Count == 0 ? 0 : 3;
                }
                else
                {
                    log.Add("Usage:");
                    log.Add("  CodeplugBuilder.exe --radio-read outFolder [--port COM5] [--any-model]");
                    log.Add("  CodeplugBuilder.exe --radio-decode radio.img outFolder");
                    log.Add("  CodeplugBuilder.exe --radio-compare cpsExportFolder radioFolder");
                    log.Add("  CodeplugBuilder.exe --radio-settings radio.img [cpsExportFolder]");
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
                try { File.WriteAllLines(logPath, log); } catch { }
            }
            return code;
        }

        static void Decode(MemoryImage img, string folder, List<string> log)
        {
            var cp = RadioCodeplug.Decode(img);
            var tables = RadioCsv.ToTables(cp, CpsFormat.BuiltIn());
            foreach (var f in RadioCsv.WriteTo(tables, folder)) log.Add("Wrote " + f);
            log.Add(cp.Channels.Count + " channels, " + cp.Zones.Count + " zones, " + cp.Contacts.Count + " contacts, " + cp.GroupLists.Count + " RX group lists, " + cp.ScanLists.Count + " scan lists, " + cp.RadioIds.Count + " radio IDs, " + cp.DtmfContacts.Count + " analog contacts");
            log.Add("Boot text: \"" + cp.BootLine1 + "\" / \"" + cp.BootLine2 + "\"");
            foreach (var w in cp.Warnings) log.Add("Warning: " + w);
        }

        /// <summary>
        /// Encodes a project (generated like Generate does) or a folder of CPS CSVs into <paramref name="original"/>,
        /// decodes the result again and compares it with what went in. Saves edited.img and plan.txt in <paramref name="folder"/>.
        /// </summary>
        public static EncodeResult Encode(MemoryImage original, string source, string folder, bool mergeRadio, List<string> log)
        {
            var format = CpsFormat.BuiltIn();
            string before = Path.Combine(folder, "before");
            RadioCsv.WriteTo(RadioCsv.ToTables(RadioCodeplug.Decode(original), format), before);
            log.Add("Radio read: " + original.Model + " " + original.Version + ", read " + original.ReadAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + " (its CSVs: " + before + ")");

            var tables = new Dictionary<string, CsvTable>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(source) && source.EndsWith(".cpb", StringComparison.OrdinalIgnoreCase))
            {
                var p = ProjectStore.Load(source);
                var g = CodeplugGenerator.Generate(p, format, mergeRadio ? CpsExport.Load(before) : null);
                foreach (var f in g.Files()) tables[f.Key] = f.Value;
                log.Add("Project " + source + ": " + g.Channels.Rows.Count + " channel rows" + (mergeRadio ? ", merged with what's on the radio" : ""));
                log.AddRange(g.Notes.Select(n => "Note: " + n));
            }
            else
            {
                foreach (string f in CpsFormat.Files)
                {
                    string path = CpsFormat.FindFile(source, f);
                    if (path != null) tables[f] = CsvTable.Load(path);
                }
                log.Add("CSV folder " + source + ": " + string.Join(", ", tables.Keys));
            }

            var result = RadioEncoder.Encode(original, tables, format);
            log.AddRange(result.Notes.Select(n => "Note: " + n));
            log.AddRange(result.Errors.Select(e => "Error: " + e));
            string imgPath = Path.Combine(folder, "edited.img");
            result.Image.Save(imgPath);
            log.Add("Saved " + imgPath);

            // Decode what was encoded and compare with what went in, table by table.
            var cp = RadioCodeplug.Decode(result.Image);
            log.AddRange(cp.Warnings.Select(w => "Decode warning: " + w));
            var back = RadioCsv.ToTables(cp, format);
            string after = Path.Combine(folder, "after");
            RadioCsv.WriteTo(back, after);
            int diffs = 0;
            foreach (var kv in result.Tables)
            {
                var d = RadioCsv.CompareTables(kv.Key, kv.Value, back[kv.Key])
                    .Where(x => RadioCsv.DecodedChannelColumns.Any(c => x.Contains("[" + c + "]")) || !x.StartsWith(CpsFormat.ChannelFile, StringComparison.OrdinalIgnoreCase)).ToList();
                diffs += d.Count;
                log.AddRange(d.Select(x => "Differs: " + x));
            }
            log.Add(diffs == 0 ? "Decoding the edited image gives back every table that went in." : diffs + " differences between what went in and the edited image decoded again.");

            var blocks = RadioWriter.BlocksToWrite(original, result.Image);
            File.WriteAllLines(Path.Combine(folder, "plan.txt"), blocks.Select(b => b.ToString("X7") + " " + BitConverter.ToString(result.Image.Get(b, 16))));
            log.Add("A write would send " + blocks.Count + " blocks, " + RadioWriter.ChangedBlocks(original, result.Image).Count + " of them changed (plan.txt).");
            log.Add(cp.Channels.Count + " channels, " + cp.Zones.Count + " zones, " + cp.Contacts.Count + " talkgroups, " + cp.GroupLists.Count + " RX group lists, " + cp.ScanLists.Count + " scan lists, " + cp.RadioIds.Count + " radio IDs");
            return result;
        }

        static string Option(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
