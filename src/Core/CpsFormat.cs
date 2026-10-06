using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CodeplugBuilder.Core
{
    /// <summary>
    /// The exact CSV layout of one CPS version, learned from files the CPS exported
    /// (Tool → Export → Export All). Generated rows are copies of the CPS's own rows with
    /// only the fields this program manages changed, so columns it doesn't know about keep
    /// the CPS defaults.
    /// </summary>
    public sealed class CpsFormat
    {
        public const string ChannelFile = "Channel.CSV";
        public const string RadioIdFile = "RadioIDList.CSV";
        public const string ZoneFile = "Zone.CSV";
        public const string ScanListFile = "ScanList.CSV";
        public const string TalkGroupsFile = "TalkGroups.CSV";
        public const string RxGroupFile = "ReceiveGroupCallList.CSV";

        public static readonly string[] Files = { ChannelFile, RadioIdFile, ZoneFile, ScanListFile, TalkGroupsFile, RxGroupFile };

        /// <summary>Section numbers the CPS uses in its .LST file list (from an Export All of CPS 1.22).</summary>
        static readonly Dictionary<string, int> ListIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { ChannelFile, 0 }, { RadioIdFile, 1 }, { ZoneFile, 2 }, { ScanListFile, 3 },
            { TalkGroupsFile, 5 }, { RxGroupFile, 8 },
        };

        /// <summary>First channel number used by the VFO A/B rows in Channel.CSV.</summary>
        public const int FirstVfoNumber = 4001;

        public CsvTable Channels { get; private set; }
        public CsvTable RadioIds { get; private set; }
        public CsvTable Zones { get; private set; }
        public CsvTable ScanLists { get; private set; }
        public CsvTable TalkGroups { get; private set; }
        public CsvTable RxGroupLists { get; private set; }

        /// <summary>Where this format came from, for display.</summary>
        public string Source { get; private set; }

        public List<string> AnalogTemplate { get; private set; }
        public List<string> DigitalTemplate { get; private set; }
        public List<List<string>> VfoRows { get; private set; }
        /// <summary>A scan list row copied from the CPS, or null if the export had none.</summary>
        public List<string> ScanTemplate { get; private set; }

        /// <summary>Decimal places the CPS uses for frequencies (5 → "146.94000").</summary>
        public int FrequencyDecimals { get; private set; } = 5;

        CpsFormat() { }

        public CsvTable Table(string file)
        {
            switch (file)
            {
                case ChannelFile: return Channels;
                case RadioIdFile: return RadioIds;
                case ZoneFile: return Zones;
                case ScanListFile: return ScanLists;
                case TalkGroupsFile: return TalkGroups;
                case RxGroupFile: return RxGroupLists;
                default: throw new ArgumentException("Unknown CPS file " + file);
            }
        }

        /// <summary>The 6X2 Pro CPS 1.22 layout that ships inside the program.</summary>
        public static CpsFormat BuiltIn()
        {
            var tables = new Dictionary<string, CsvTable>();
            foreach (string f in Files) tables[f] = CsvTable.Parse(ReadResource(f));
            return Build(tables, null, "Built-in (BTECH DMR-6X2 PRO CPS 1.22)");
        }

        /// <summary>
        /// Loads the layout from a folder of CSVs exported by the CPS. Files that are missing fall back
        /// to the built-in layout. Throws <see cref="InvalidDataException"/> if a file isn't a CPS export.
        /// </summary>
        public static CpsFormat FromFolder(string folder)
        {
            var builtIn = BuiltIn();
            var tables = new Dictionary<string, CsvTable>();
            int found = 0;
            foreach (string f in Files)
            {
                string path = FindFile(folder, f);
                if (path != null)
                {
                    tables[f] = CsvTable.Load(path);
                    found++;
                }
                else tables[f] = builtIn.Table(f);
            }
            if (found == 0)
                throw new InvalidDataException("No CPS export files (Channel.CSV, Zone.CSV, ...) were found in " + folder + ".");
            return Build(tables, builtIn, folder);
        }

        /// <summary>Case-insensitive file lookup (the CPS writes upper-case .CSV).</summary>
        public static string FindFile(string folder, string fileName)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            string direct = Path.Combine(folder, fileName);
            if (File.Exists(direct)) return direct;
            foreach (string p in Directory.GetFiles(folder))
                if (string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        static CpsFormat Build(Dictionary<string, CsvTable> t, CpsFormat fallback, string source)
        {
            var f = new CpsFormat
            {
                Channels = t[ChannelFile],
                RadioIds = t[RadioIdFile],
                Zones = t[ZoneFile],
                ScanLists = t[ScanListFile],
                TalkGroups = t[TalkGroupsFile],
                RxGroupLists = t[RxGroupFile],
                Source = source,
            };

            Require(f.Channels, ChannelFile, "Channel Name", "Receive Frequency", "Transmit Frequency", "Channel Type");
            Require(f.Zones, ZoneFile, "Zone Name", "Zone Channel Member");
            Require(f.TalkGroups, TalkGroupsFile, "Radio ID", "Name", "Call Type");
            Require(f.RxGroupLists, RxGroupFile, "Group Name", "Contact");
            Require(f.ScanLists, ScanListFile, "Scan List Name", "Scan Channel Member");
            Require(f.RadioIds, RadioIdFile, "Radio ID", "Name");

            var normal = f.Channels.Rows.Where(r => ChannelNumber(f.Channels, r) < FirstVfoNumber).ToList();
            f.AnalogTemplate = normal.FirstOrDefault(r => f.Channels.Get(r, "Channel Type").StartsWith("A", StringComparison.OrdinalIgnoreCase));
            f.DigitalTemplate = normal.FirstOrDefault(r => f.Channels.Get(r, "Channel Type").StartsWith("D", StringComparison.OrdinalIgnoreCase));
            f.VfoRows = f.Channels.Rows.Where(r => ChannelNumber(f.Channels, r) >= FirstVfoNumber).ToList();
            f.ScanTemplate = f.ScanLists.Rows.FirstOrDefault();

            if (fallback != null)
            {
                if (f.AnalogTemplate == null) f.AnalogTemplate = MapRow(fallback.Channels, fallback.AnalogTemplate, f.Channels);
                if (f.DigitalTemplate == null) f.DigitalTemplate = MapRow(fallback.Channels, fallback.DigitalTemplate, f.Channels);
                if (f.VfoRows.Count == 0) f.VfoRows = fallback.VfoRows.Select(r => MapRow(fallback.Channels, r, f.Channels)).ToList();
            }
            if (f.AnalogTemplate == null || f.DigitalTemplate == null)
                throw new InvalidDataException(ChannelFile + " needs at least one analog and one digital channel to copy.");

            f.FrequencyDecimals = DetectDecimals(f.Channels.Get(f.DigitalTemplate, "Receive Frequency"));
            return f;
        }

        static void Require(CsvTable table, string file, params string[] columns)
        {
            var missing = columns.Where(c => table.IndexOf(c) < 0).ToList();
            if (missing.Count > 0)
                throw new InvalidDataException(file + " doesn't look like a CPS export (missing column " + string.Join(", ", missing) + ").");
        }

        public static int ChannelNumber(CsvTable channels, List<string> row)
        {
            int.TryParse(channels.Get(row, "No."), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
            return n;
        }

        /// <summary>Copies a row from one header layout to another by column name.</summary>
        public static List<string> MapRow(CsvTable from, List<string> row, CsvTable to)
        {
            if (row == null) return null;
            var result = to.NewRow(null);
            for (int i = 0; i < to.Header.Count; i++)
            {
                int j = from.IndexOf(to.Header[i]);
                if (j >= 0 && j < row.Count) result[i] = row[j];
            }
            return result;
        }

        static int DetectDecimals(string sample)
        {
            int dot = (sample ?? "").IndexOf('.');
            if (dot < 0) return 5;
            int d = sample.Length - dot - 1;
            return d >= 3 && d <= 6 ? d : 5;
        }

        public string FormatFrequency(decimal mhz)
        {
            return mhz.ToString("F" + FrequencyDecimals, CultureInfo.InvariantCulture);
        }

        /// <summary>Text of a .LST file list for Tool → Import → Import From File List.</summary>
        public static string BuildListFile(IEnumerable<string> fileNames)
        {
            var entries = fileNames
                .Select(f => new KeyValuePair<int, string>(ListIndex.TryGetValue(f, out int i) ? i : 99, f))
                .OrderBy(e => e.Key)
                .ToList();
            var sb = new StringBuilder();
            sb.Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            foreach (var e in entries)
                sb.Append(e.Key.ToString(CultureInfo.InvariantCulture)).Append(",\"").Append(e.Value).Append("\"\r\n");
            return sb.ToString();
        }

        /// <summary>Saves the learned layout (headers and template rows only, no channel data) to a folder.</summary>
        public void SaveTemplates(string folder)
        {
            Directory.CreateDirectory(folder);
            var ch = Channels.CloneHeader();
            ch.Rows.Add(new List<string>(AnalogTemplate));
            ch.Rows.Add(new List<string>(DigitalTemplate));
            foreach (var v in VfoRows) ch.Rows.Add(new List<string>(v));
            ch.Save(Path.Combine(folder, ChannelFile));

            var scan = ScanLists.CloneHeader();
            if (ScanTemplate != null) scan.Rows.Add(new List<string>(ScanTemplate));
            scan.Save(Path.Combine(folder, ScanListFile));

            Zones.CloneHeader().Save(Path.Combine(folder, ZoneFile));
            TalkGroups.CloneHeader().Save(Path.Combine(folder, TalkGroupsFile));
            RxGroupLists.CloneHeader().Save(Path.Combine(folder, RxGroupFile));
            RadioIds.CloneHeader().Save(Path.Combine(folder, RadioIdFile));
        }

        static string ReadResource(string file)
        {
            var asm = typeof(CpsFormat).Assembly;
            string name = "CodeplugBuilder.Templates." + file;
            using (var s = asm.GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("Missing built-in template " + name);
                using (var r = new StreamReader(s, CsvTable.FileEncoding))
                    return r.ReadToEnd();
            }
        }
    }
}
