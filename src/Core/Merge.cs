using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace CodeplugBuilder.Core
{
    // Merge mode ("keep channels made in the CPS"). Each file the CPS imports replaces its whole list, so a
    // channel made by hand in the CPS disappears on the next import unless the generated files contain it.
    // With a fresh Export All of the codeplug as the base, everything in it that this program doesn't manage is
    // written back as it was: channels, the zones made in the CPS, and the talkgroups, receive group lists, scan
    // lists and radio IDs they use. "Managed" means the project produces it now or produced/imported it before
    // (Project.KnownChannels/KnownZones/KnownTalkgroups), so things deleted here stay deleted.

    /// <summary>A channel this program imported or generated: its CPS number and the name it had.</summary>
    [DataContract(Namespace = "")]
    public sealed class KnownChannel
    {
        [DataMember(Order = 1)] public int Number { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }

        public KnownChannel() { Name = ""; }
        public KnownChannel(int number, string name) { Number = number; Name = name ?? ""; }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Name = ""; }
    }

    /// <summary>The tables of a CPS "Export All" folder. Files that are missing are empty tables.</summary>
    public sealed class CpsExport
    {
        public string Folder { get; private set; }
        /// <summary>When Channel.CSV was written (the export date).</summary>
        public DateTime Exported { get; private set; }
        public CsvTable Channels { get; private set; }
        public CsvTable Zones { get; private set; }
        public CsvTable TalkGroups { get; private set; }
        public CsvTable RxGroupLists { get; private set; }
        public CsvTable ScanLists { get; private set; }
        public CsvTable RadioIds { get; private set; }

        public static CpsExport Load(string folder)
        {
            string channels = CpsFormat.FindFile(folder, CpsFormat.ChannelFile);
            if (channels == null) throw new FileNotFoundException("Channel.CSV wasn't found in " + folder + ". In the CPS, use Tool > Export > Export All first.");
            CsvTable Opt(string file)
            {
                string path = CpsFormat.FindFile(folder, file);
                return path != null ? CsvTable.Load(path) : new CsvTable();
            }
            return new CpsExport
            {
                Folder = folder,
                Exported = File.GetLastWriteTime(channels),
                Channels = CsvTable.Load(channels),
                Zones = Opt(CpsFormat.ZoneFile),
                TalkGroups = Opt(CpsFormat.TalkGroupsFile),
                RxGroupLists = Opt(CpsFormat.RxGroupFile),
                ScanLists = Opt(CpsFormat.ScanListFile),
                RadioIds = Opt(CpsFormat.RadioIdFile),
            };
        }
    }

    /// <summary>A channel from the base export that was made in the CPS, written back as it was.</summary>
    public sealed class KeptChannel
    {
        public int Number;
        public string Name;
        internal List<string> Row;
        /// <summary>Stands in for it in zone member lists.</summary>
        internal GeneratedChannel Member;
    }

    /// <summary>Works out what to keep from a base export and adds it to a codeplug being generated.</summary>
    sealed class Merge
    {
        readonly Project p;
        readonly CpsExport b;
        readonly GeneratedCodeplug g;
        readonly CpsFormat f;
        readonly int max;
        readonly Dictionary<string, KeptChannel> keptByName = new Dictionary<string, KeptChannel>(StringComparer.OrdinalIgnoreCase);
        // Base name → name in the generated files (renamed to avoid a clash, or the project's name for the same talkgroup ID).
        readonly Dictionary<string, string> tgRename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> rxRename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> scanRename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<KeptChannel> Kept { get; } = new List<KeptChannel>();

        static readonly string[] ScanColumns = Enumerable.Range(1, 8).Select(i => "Scan List " + i).ToArray();

        public Merge(Project p, CpsExport b, GeneratedCodeplug g, CpsFormat f, int max)
        {
            this.p = p;
            this.b = b;
            this.g = g;
            this.f = f;
            this.max = max;

            var ours = new HashSet<string>(g.ChannelList.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>((p.KnownChannels ?? new List<KnownChannel>()).Select(k => k.Number + "|" + k.Name), StringComparer.OrdinalIgnoreCase);
            var stored = p.StoredChannelNumbers();
            foreach (var row in b.Channels.Rows)
            {
                int n = CpsFormat.ChannelNumber(b.Channels, row);
                string name = b.Channels.Get(row, "Channel Name").Trim();
                if (n <= 0 || n >= CpsFormat.FirstVfoNumber || name.Length == 0) continue;
                if (ours.Contains(name)) continue;                              // the project writes it
                if (known.Contains(n + "|" + name)) continue;                   // ours once: renamed or deleted here since
                if (stored.Contains(n))
                {
                    // The project's slot: most likely its channel, renamed or edited in the CPS. The project's version wins.
                    var holder = g.ChannelList.FirstOrDefault(c => c.StoredNumber == n);
                    if (holder != null)
                        g.Notes.Add("Channel " + n + " is \"" + name + "\" in the CPS export but \"" + holder.Name + "\" here; this program's channel replaces it. " +
                                    "(To keep a change made in the CPS, make it here too.)");
                    continue;
                }
                var k = new KeptChannel { Number = n, Name = name, Row = row };
                k.Member = new GeneratedChannel
                {
                    Name = name,
                    Number = n,
                    Kept = k,
                    Repeater = new Repeater
                    {
                        Name = name,
                        RxMHz = CpsImporter.ParseMHz(b.Channels.Get(row, "Receive Frequency")),
                        TxMHz = CpsImporter.ParseMHz(b.Channels.Get(row, "Transmit Frequency")),
                    },
                };
                Kept.Add(k);
                keptByName[name] = k;
            }
        }

        /// <summary>Numbers the kept channels hold, so new channels from the project don't take them.</summary>
        public HashSet<int> ReservedNumbers => new HashSet<int>(Kept.Select(k => k.Number));

        /// <summary>After the project's channels are numbered: a kept channel on a number a project channel holds moves to a free one.</summary>
        public void PlaceNumbers()
        {
            var taken = new HashSet<int>(g.ChannelList.Select(c => c.Number));
            var reserved = p.StoredChannelNumbers(); // also the numbers of switched-off repeaters, which may come back
            foreach (var k in Kept.Where(k => taken.Contains(k.Number)).ToList())
            {
                int old = k.Number;
                int n = 1;
                while (taken.Contains(n) || reserved.Contains(n) || Kept.Any(x => x != k && x.Number == n)) n++;
                k.Number = k.Member.Number = n;
                g.Notes.Add("\"" + k.Name + "\" (made in the CPS) was on channel " + old + ", which belongs to \"" +
                            g.ChannelList.First(c => c.Number == old).Name + "\" here; it moves to channel " + n + ".");
            }
        }

        IEnumerable<string> KeptRxListNames()
        {
            return Kept.Select(k => b.Channels.Get(k.Row, "Receive Group List").Trim())
                       .Where(n => n.Length > 0 && !n.Equals("None", StringComparison.OrdinalIgnoreCase))
                       .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Talkgroups from the base that the project doesn't have (by ID): the ones kept channels or kept receive
        /// group lists use, and ones made in the CPS (not in <see cref="Project.KnownTalkgroups"/>). Clashing names
        /// get a number.
        /// </summary>
        public void AddTalkgroups(UniqueNamer namer, Dictionary<int, string> tgNames, List<string> template)
        {
            var needed = new HashSet<string>(Kept.Select(k => b.Channels.Get(k.Row, "Contact").Trim()), StringComparer.OrdinalIgnoreCase);
            var rxNames = new HashSet<string>(KeptRxListNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var row in b.RxGroupLists.Rows.Where(r => rxNames.Contains(b.RxGroupLists.Get(r, "Group Name").Trim())))
                foreach (var m in b.RxGroupLists.Get(row, "Contact").Split('|')) if (m.Trim().Length > 0) needed.Add(m.Trim());
            var known = new HashSet<int>(p.KnownTalkgroups ?? new List<int>());
            var added = new List<string>();
            foreach (var row in b.TalkGroups.Rows)
            {
                string name = b.TalkGroups.Get(row, "Name").Trim();
                if (!int.TryParse(b.TalkGroups.Get(row, "Radio ID").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0 || name.Length == 0) continue;
                if (tgNames.TryGetValue(id, out string ourName))
                {
                    if (!string.Equals(ourName, name, StringComparison.Ordinal)) tgRename[name] = ourName; // same talkgroup, the project's name
                    continue;
                }
                if (!needed.Contains(name) && known.Contains(id)) continue; // deleted here, and nothing kept uses it
                string final = namer.Claim(name);
                if (final != name) tgRename[name] = final;
                tgNames[id] = final;
                var r = g.TalkGroups.NewRow(template);
                foreach (var col in b.TalkGroups.Header) g.TalkGroups.Set(r, b.TalkGroups.Get(row, col), col);
                g.TalkGroups.Set(r, (g.TalkGroups.Rows.Count + 1).ToString(CultureInfo.InvariantCulture), "No.");
                g.TalkGroups.Set(r, final, "Name");
                g.TalkGroups.Rows.Add(r);
                added.Add(final);
            }
            if (added.Count > 0) g.Notes.Add("Talkgroups kept from the CPS: " + string.Join(", ", added) + ".");
        }

        string Tg(string name) { return tgRename.TryGetValue(name, out string n) ? n : name; }

        /// <summary>Receive group lists the kept channels use (renamed if one of ours has the name).</summary>
        public void AddRxLists(UniqueNamer namer, List<string> template)
        {
            foreach (string listName in KeptRxListNames())
            {
                var row = b.RxGroupLists.Rows.FirstOrDefault(r => string.Equals(b.RxGroupLists.Get(r, "Group Name").Trim(), listName, StringComparison.OrdinalIgnoreCase));
                if (row == null) continue;
                string final = namer.Claim(listName);
                if (final != listName) rxRename[listName] = final;
                var r = g.RxGroupLists.NewRow(template);
                foreach (var col in b.RxGroupLists.Header) g.RxGroupLists.Set(r, b.RxGroupLists.Get(row, col), col);
                g.RxGroupLists.Set(r, (g.RxGroupLists.Rows.Count + 1).ToString(CultureInfo.InvariantCulture), "No.");
                g.RxGroupLists.Set(r, final, "Group Name");
                g.RxGroupLists.Set(r, string.Join("|", b.RxGroupLists.Get(row, "Contact").Split('|').Select(m => Tg(m.Trim())).Where(m => m.Length > 0)), "Contact");
                g.RxGroupLists.Rows.Add(r);
            }
        }

        /// <summary>
        /// Kept channels stay in the project's zones they were in (at the end); zones made in the CPS come back
        /// with their members (channels that still exist).
        /// </summary>
        public void AddZones(UniqueNamer namer, int maxZoneChannels)
        {
            var known = new HashSet<string>(p.KnownZones ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var ourByName = g.ChannelList.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            bool tracked = (p.KnownChannels?.Count ?? 0) > 0;
            var ourZones = g.ZoneList.ToList();
            foreach (var row in b.Zones.Rows)
            {
                string zone = b.Zones.Get(row, "Zone Name").Trim();
                if (zone.Length == 0) continue;
                var names = b.Zones.Get(row, "Zone Channel Member").Split('|').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
                var ourZone = ourZones.FirstOrDefault(z => string.Equals(z.Name, zone, StringComparison.OrdinalIgnoreCase));
                if (ourZone != null)
                {
                    foreach (var k in names.Where(keptByName.ContainsKey).Select(n => keptByName[n]))
                    {
                        if (ourZone.Members.Contains(k.Member)) continue;
                        if (maxZoneChannels > 0 && ourZone.Members.Count >= maxZoneChannels)
                        {
                            g.Notes.Add("Zone \"" + zone + "\" is full, so \"" + k.Name + "\" (made in the CPS) isn't in it any more.");
                            continue;
                        }
                        ourZone.Members.Add(k.Member);
                    }
                    continue;
                }
                bool hasKept = names.Any(keptByName.ContainsKey);
                if (known.Contains(zone)) continue;    // the project's zone once; renamed or removed here since
                if (!tracked && !hasKept) continue;   // can't tell yet whose it is; only keep it for channels made in the CPS
                var members = names.Select(n => keptByName.TryGetValue(n, out var k) ? k.Member : ourByName.TryGetValue(n, out var c) ? c : null)
                                   .Where(m => m != null).Distinct().ToList();
                if (members.Count == 0) continue;
                if (maxZoneChannels > 0 && members.Count > maxZoneChannels) members = members.Take(maxZoneChannels).ToList();
                string a = b.Zones.Get(row, "A Channel").Trim(), bb = b.Zones.Get(row, "B Channel").Trim();
                var gz = new GeneratedZone
                {
                    Name = namer.Claim(zone),
                    Members = members,
                    AChannel = members.Any(m => m.Name == a) ? a : members[0].Name,
                    BChannel = members.Any(m => m.Name == bb) ? bb : members[Math.Min(1, members.Count - 1)].Name,
                    FromCps = true,
                };
                g.ZoneList.Add(gz);
                g.Notes.Add("Zone \"" + gz.Name + "\" (made in the CPS) is kept.");
            }
        }

        /// <summary>When the codeplug writes ScanList.CSV (which replaces the CPS's), the scan lists kept channels use come along.</summary>
        public void AddScanLists(UniqueNamer namer, Func<CsvTable, List<string>> newRow)
        {
            var wanted = Kept.SelectMany(k => ScanColumns.Select(c => b.Channels.Get(k.Row, c).Trim()))
                             .Where(n => n.Length > 0 && !n.Equals("None", StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var byName = new Dictionary<string, GeneratedChannel>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in g.ChannelList.Concat(Kept.Select(k => k.Member))) if (!byName.ContainsKey(c.Name)) byName[c.Name] = c;
            foreach (string listName in wanted)
            {
                var row = b.ScanLists.Rows.FirstOrDefault(r => string.Equals(b.ScanLists.Get(r, "Scan List Name").Trim(), listName, StringComparison.OrdinalIgnoreCase));
                if (row == null) continue;
                string final = namer.Claim(listName);
                if (final != listName) scanRename[listName] = final;
                var r = newRow(g.ScanLists);
                foreach (var col in b.ScanLists.Header) g.ScanLists.Set(r, b.ScanLists.Get(row, col), col);
                g.ScanLists.Set(r, (g.ScanLists.Rows.Count + 1).ToString(CultureInfo.InvariantCulture), "No.");
                g.ScanLists.Set(r, final, "Scan List Name");
                // Members that still exist; the frequency columns follow them, so they stay in step after a channel was dropped.
                var members = b.ScanLists.Get(row, "Scan Channel Member").Split('|').Select(m => m.Trim()).Where(byName.ContainsKey).Select(m => byName[m]).ToList();
                g.ScanLists.Set(r, string.Join("|", members.Select(m => m.Name)), "Scan Channel Member");
                g.ScanLists.Set(r, string.Join("|", members.Select(m => f.FormatFrequency(m.Repeater.RxMHz))), "Scan Channel Member RX Frequency");
                g.ScanLists.Set(r, string.Join("|", members.Select(m => f.FormatFrequency(m.Repeater.TxMHz))), "Scan Channel Member TX Frequency");
                g.ScanLists.Rows.Add(r);
            }
        }

        /// <summary>The kept channels' rows: the CPS's own values, with references renamed where needed.</summary>
        public void AddChannelRows(List<KeyValuePair<int, List<string>>> rows)
        {
            foreach (var k in Kept)
            {
                bool digital = b.Channels.Get(k.Row, "Channel Type").Trim().StartsWith("D", StringComparison.OrdinalIgnoreCase);
                var row = g.Channels.NewRow(digital ? f.DigitalTemplate : f.AnalogTemplate);
                foreach (var col in b.Channels.Header) g.Channels.Set(row, b.Channels.Get(k.Row, col), col); // every column the CPS wrote
                g.Channels.Set(row, k.Number.ToString(CultureInfo.InvariantCulture), "No.");
                g.Channels.Set(row, Tg(b.Channels.Get(k.Row, "Contact").Trim()), "Contact");
                string rx = b.Channels.Get(k.Row, "Receive Group List").Trim();
                if (rxRename.TryGetValue(rx, out string rxNew)) g.Channels.Set(row, rxNew, "Receive Group List");
                if (g.ScanLists != null)
                    foreach (var col in ScanColumns)
                    {
                        string s = b.Channels.Get(k.Row, col).Trim();
                        if (scanRename.TryGetValue(s, out string sNew)) g.Channels.Set(row, sNew, col);
                    }
                rows.Add(new KeyValuePair<int, List<string>>(k.Number, row));
            }
            if (Kept.Count > 0)
                g.Notes.Insert(0, "Keeping " + Kept.Count + " channel" + (Kept.Count == 1 ? "" : "s") + " made in the CPS (export of " +
                                  b.Exported.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + "): " + string.Join(", ", Kept.Select(k => k.Name + " (" + k.Number + ")")) + ".");
        }

        /// <summary>When RadioIDList.CSV is written (it replaces the CPS's list), the CPS's other radio IDs stay in it.</summary>
        public void AddRadioIds()
        {
            if (g.RadioIds == null) return;
            var names = new HashSet<string>(g.RadioIds.Rows.Select(r => g.RadioIds.Get(r, "Name").Trim()), StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(g.RadioIds.Rows.Select(r => g.RadioIds.Get(r, "Radio ID").Trim()));
            foreach (var row in b.RadioIds.Rows)
            {
                string name = b.RadioIds.Get(row, "Name").Trim(), id = b.RadioIds.Get(row, "Radio ID").Trim();
                if (name.Length == 0 || names.Contains(name) || ids.Contains(id)) continue;
                var r = g.RadioIds.NewRow(g.RadioIds.Rows.FirstOrDefault()); // the generated file's layout; values set by column name
                foreach (var col in b.RadioIds.Header) g.RadioIds.Set(r, b.RadioIds.Get(row, col), col);
                g.RadioIds.Set(r, (g.RadioIds.Rows.Count + 1).ToString(CultureInfo.InvariantCulture), "No.");
                g.RadioIds.Rows.Add(r);
                names.Add(name);
                ids.Add(id);
            }
        }
    }
}
