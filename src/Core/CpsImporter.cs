using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CodeplugBuilder.Core
{
    public sealed class ImportResult
    {
        public Project Project;
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Turns a CPS "Export All" folder back into a project: talkgroups, repeaters (digital channels on
    /// the same frequency and color code are grouped), analog channels, the hotspot, and zones.
    /// Existing channel names are kept.
    /// </summary>
    public static class CpsImporter
    {
        sealed class Ch
        {
            public int No;
            public string Name;
            public decimal Rx, Tx;
            public bool Digital;
            public string Power, Bandwidth, Decode, Encode, Contact, Squelch, TxPermit;
            public int ColorCode, Slot;
            public bool TxProhibit;
            public string Zone = "";
            public int ZoneIndex = int.MaxValue, ZonePos = int.MaxValue;
        }

        public static ImportResult Import(string folder)
        {
            var result = new ImportResult();
            var notes = result.Notes;
            var p = new Project();
            // ScanList.CSV would replace the scan lists made in the CPS, so an imported codeplug starts with them off.
            p.Options.ScanListPerZone = false;
            p.Options.FavoritesScanPriority = false;
            p.Options.LocalAnalogScanList = false;
            result.Project = p;

            string channelPath = CpsFormat.FindFile(folder, CpsFormat.ChannelFile);
            if (channelPath == null)
                throw new FileNotFoundException("Channel.CSV wasn't found in " + folder + ". Use Tool → Export → Export All in the CPS first.");

            // Radio ID
            string ridPath = CpsFormat.FindFile(folder, CpsFormat.RadioIdFile);
            if (ridPath != null)
            {
                var rid = CsvTable.Load(ridPath);
                var first = rid.Rows.FirstOrDefault();
                if (first != null)
                {
                    p.RadioIdName = rid.Get(first, "Name");
                    int.TryParse(rid.Get(first, "Radio ID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);
                    p.RadioId = id;
                    if (rid.Rows.Count > 1) notes.Add("Your Radio ID List has " + rid.Rows.Count + " entries; only the first (" + p.RadioIdName + ") was imported.");
                }
            }

            // Talkgroups
            var tgByName = new Dictionary<string, Talkgroup>(StringComparer.OrdinalIgnoreCase);
            string tgPath = CpsFormat.FindFile(folder, CpsFormat.TalkGroupsFile);
            if (tgPath != null)
            {
                var t = CsvTable.Load(tgPath);
                foreach (var row in t.Rows)
                {
                    string name = t.Get(row, "Name").Trim();
                    if (!int.TryParse(t.Get(row, "Radio ID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0 || name.Length == 0)
                    {
                        notes.Add("Skipped a talkgroup row that had no name or ID.");
                        continue;
                    }
                    if (p.Talkgroups.Any(x => x.Id == id))
                    {
                        notes.Add("Talkgroup ID " + id + " (" + name + ") was listed twice; kept the first.");
                        continue;
                    }
                    var tg = new Talkgroup(name, id, CallTypes.Normalize(t.Get(row, "Call Type")));
                    p.Talkgroups.Add(tg);
                    if (!tgByName.ContainsKey(name)) tgByName[name] = tg;
                }
            }

            // Zones: membership, order, A/B selections. A channel's first zone becomes its repeater's own zone; the other
            // zones it is in list it as a member (see "Zones that share channels" below).
            var zoneOf = new Dictionary<string, Tuple<int, int, string>>(StringComparer.OrdinalIgnoreCase);
            var zoneMembers = new List<KeyValuePair<ZoneInfo, List<string>>>();
            string znPath = CpsFormat.FindFile(folder, CpsFormat.ZoneFile);
            if (znPath != null)
            {
                var z = CsvTable.Load(znPath);
                int zi = 0;
                foreach (var row in z.Rows)
                {
                    string zname = z.Get(row, "Zone Name").Trim();
                    if (zname.Length == 0) continue;
                    var members = z.Get(row, "Zone Channel Member").Split('|').Select(m => m.Trim()).Where(m => m.Length > 0)
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var info = new ZoneInfo(zname) { AChannel = z.Get(row, "A Channel"), BChannel = z.Get(row, "B Channel") };
                    p.Zones.Add(info);
                    zoneMembers.Add(new KeyValuePair<ZoneInfo, List<string>>(info, members));
                    for (int i = 0; i < members.Count; i++)
                        if (!zoneOf.ContainsKey(members[i])) zoneOf[members[i]] = Tuple.Create(zi, i, zname);
                    zi++;
                }
            }

            // Channels
            var ct = CsvTable.Load(channelPath);
            var chans = new List<Ch>();
            foreach (var row in ct.Rows)
            {
                int no = CpsFormat.ChannelNumber(ct, row);
                if (no >= CpsFormat.FirstVfoNumber) continue; // VFO A/B, handled by the format
                var c = new Ch
                {
                    No = no,
                    Name = ct.Get(row, "Channel Name").Trim(),
                    Rx = ParseMHz(ct.Get(row, "Receive Frequency")),
                    Tx = ParseMHz(ct.Get(row, "Transmit Frequency")),
                    Digital = ct.Get(row, "Channel Type").Trim().StartsWith("D", StringComparison.OrdinalIgnoreCase),
                    Power = Powers.Values.FirstOrDefault(v => string.Equals(v, ct.Get(row, "Transmit Power").Trim(), StringComparison.OrdinalIgnoreCase)) ?? "High",
                    Bandwidth = ct.Get(row, "Band Width").Trim().StartsWith("25") ? Bandwidths.Wide : Bandwidths.Narrow,
                    Decode = Tones.Normalize(ct.Get(row, "CTCSS/DCS Decode")),
                    Encode = Tones.Normalize(ct.Get(row, "CTCSS/DCS Encode")),
                    Contact = ct.Get(row, "Contact").Trim(),
                    Squelch = ct.Get(row, "Squelch Mode").Trim(),
                    TxPermit = ct.Get(row, "Busy channel Lock-Out/TX Permit", "TX Permit").Trim(),
                    ColorCode = ParseInt(ct.Get(row, "Color Code"), 1),
                    Slot = ParseInt(ct.Get(row, "Slot"), 1) == 2 ? 2 : 1,
                    TxProhibit = string.Equals(ct.Get(row, "TX Prohibit", "PTT Prohibit").Trim(), "On", StringComparison.OrdinalIgnoreCase),
                };
                if (c.Name.Length == 0) continue;
                if (zoneOf.TryGetValue(c.Name, out var zinfo))
                {
                    c.ZoneIndex = zinfo.Item1;
                    c.ZonePos = zinfo.Item2;
                    c.Zone = zinfo.Item3;
                }
                chans.Add(c);
            }

            // Keep zone order (and member order inside zones); channels in no zone go last.
            var ordered = chans.OrderBy(c => c.ZoneIndex).ThenBy(c => c.ZonePos).ThenBy(c => c.No).ToList();

            // Group digital channels into repeaters by frequency pair, color code and zone.
            var repeaters = new List<Repeater>();
            var groups = new Dictionary<string, Repeater>();
            var groupChannels = new Dictionary<Repeater, List<Ch>>();
            var channelByName = new Dictionary<string, ChannelRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in ordered)
            {
                if (!c.Digital)
                {
                    var a = Repeater.NewAnalog(c.Name);
                    a.RxMHz = c.Rx;
                    a.TxMHz = c.Tx;
                    a.Power = c.Power;
                    a.Bandwidth = c.Bandwidth;
                    a.ToneDecode = c.Decode;
                    a.ToneEncode = c.Encode;
                    a.ToneSquelch = c.Squelch.StartsWith("CTCSS", StringComparison.OrdinalIgnoreCase);
                    a.RxOnly = c.TxProhibit;
                    a.Zone = c.Zone;
                    a.ChannelNumber = c.No; // kept, so APRS and hot keys still point at the same channel
                    repeaters.Add(a);
                    channelByName[c.Name] = new ChannelRef(a, null);
                    continue;
                }
                if (!tgByName.TryGetValue(c.Contact, out Talkgroup tg))
                {
                    notes.Add("Channel \"" + c.Name + "\" uses contact \"" + c.Contact + "\", which isn't in TalkGroups.CSV; it was skipped.");
                    continue;
                }
                string key = c.Rx.ToString(CultureInfo.InvariantCulture) + "|" + c.Tx.ToString(CultureInfo.InvariantCulture) + "|" + c.ColorCode + "|" + c.Zone.ToLowerInvariant();
                if (!groups.TryGetValue(key, out Repeater r))
                {
                    r = Repeater.NewDigital(c.Name);
                    r.RxMHz = c.Rx;
                    r.TxMHz = c.Tx;
                    r.ColorCode = c.ColorCode;
                    r.Power = c.Power;
                    r.RxOnly = c.TxProhibit;
                    r.Zone = c.Zone;
                    groups[key] = r;
                    groupChannels[r] = new List<Ch>();
                    repeaters.Add(r);
                }
                groupChannels[r].Add(c);
                var entry = new RepeaterTalkgroup(tg.Id, c.Slot, c.Name) { ChannelNumber = c.No };
                r.Talkgroups.Add(entry);
                channelByName[c.Name] = new ChannelRef(r, entry);
            }

            // Name each digital repeater and work out a prefix for future channels.
            foreach (var kv in groupChannels)
            {
                var r = kv.Key;
                var list = kv.Value;
                var names = list.Select(c => c.Name).ToList();
                string common = CommonWordPrefix(names);
                if (list.Count == 1)
                {
                    r.Name = names[0];
                    r.Prefix = names[0].Length <= 8 ? names[0] : names[0].Split(' ')[0];
                }
                else if (common.Length > 0)
                {
                    r.Name = common;
                    r.Prefix = common;
                }
                else
                {
                    bool aloneInZone = r.Zone.Length > 0 && repeaters.Count(x => Project.SameZone(x.Zone, r.Zone)) == 1;
                    r.Name = aloneInZone ? r.Zone : r.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) + " CC" + r.ColorCode;
                    r.Prefix = "";
                }
                var powers = list.Select(c => c.Power).Distinct().ToList();
                if (powers.Count > 1)
                {
                    r.Power = list.GroupBy(c => c.Power).OrderByDescending(g => g.Count()).First().Key;
                    notes.Add("Channels of " + r.Name + " had different power levels; all now use " + r.Power + ".");
                }
                // Only keep custom channel names that differ from what the prefix would produce.
                foreach (var e in r.Talkgroups)
                {
                    var tg = p.FindTalkgroup(e.TalkgroupId);
                    string auto = r.AutoChannelName(e, tg.Name, p.Options.MaxNameLength);
                    if (string.Equals(auto, e.ChannelName, StringComparison.Ordinal)) e.ChannelName = null;
                }
            }

            // The largest simplex digital group with several talkgroups is the hotspot.
            var hotspot = groupChannels
                .Where(kv => kv.Key.RxMHz == kv.Key.TxMHz && kv.Value.Count >= 2)
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (hotspot != null)
            {
                repeaters.Remove(hotspot);
                hotspot.Name = "Hotspot";
                p.Hotspot = hotspot;
                p.HotspotEnabled = true;
                notes.Add("The " + hotspot.Talkgroups.Count + " digital channels on " + hotspot.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) + " MHz simplex were set up as your hotspot (Hotspot tab).");
            }

            p.Repeaters.AddRange(repeaters);

            // Zones that share channels, or list a repeater's channels out of the order the generator writes them: the zone
            // keeps its whole member list, so it comes back exactly. A zone of only shared channels is a Favorites zone.
            int shared = 0;
            foreach (var kv in zoneMembers)
            {
                var info = kv.Key;
                var wanted = kv.Value.Where(channelByName.ContainsKey).Select(n => channelByName[n]).ToList();
                if (wanted.SequenceEqual(p.ZoneChannels(info.Name))) continue;
                info.Members = wanted.Select(p.MemberFor).ToList();
                if (!p.AllRepeaters().Any(r => Project.SameZone(r.Zone, info.Name))) info.Kind = ZoneKinds.Favorites;
                shared += kv.Value.Count(n => zoneOf.TryGetValue(n, out var home) && !Project.SameZone(home.Item3, info.Name));
            }
            if (shared > 0)
                notes.Add(shared + " channel(s) are in more than one zone. Each belongs to the first zone it is in; the other zones list it (Zones tab).");

            // Polite transmit stays off (the template's values, so the round trip holds) unless the codeplug already follows it.
            p.Options.PoliteTransmit = chans.Any(c => c.Digital && c.Rx != c.Tx) && chans.All(c =>
                string.Equals(c.TxPermit, !c.Digital ? TxPermits.Off : c.Rx == c.Tx ? TxPermits.Always : TxPermits.SameColorCode, StringComparison.OrdinalIgnoreCase));

            string rgPath = CpsFormat.FindFile(folder, CpsFormat.RxGroupFile);
            if (rgPath != null && CsvTable.Load(rgPath).Rows.Count > 0)
                notes.Add("Receive group lists aren't imported; the program builds one per repeater when you generate.");
            string slPath = CpsFormat.FindFile(folder, CpsFormat.ScanListFile);
            if (slPath != null && CsvTable.Load(slPath).Rows.Count > 0)
                notes.Add("Scan lists aren't imported. Turn on \"scan list per zone\" in Settings to have them generated.");

            p.Normalize();
            // Everything imported is this program's from now on (merge mode keeps only what's made in the CPS later).
            p.Remember(chans.Select(c => new KnownChannel(c.No, c.Name)), p.Zones.Select(z => z.Name), p.Talkgroups.Select(t => t.Id));
            return result;
        }

        /// <summary>Leading whole words shared by every name ("W5FC TX", "W5FC Local" → "W5FC").</summary>
        public static string CommonWordPrefix(IList<string> names)
        {
            if (names.Count == 0) return "";
            var split = names.Select(n => n.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            var words = new List<string>();
            for (int i = 0; ; i++)
            {
                if (split.Any(s => s.Length <= i + 1)) break; // keep at least one word of each name
                string w = split[0][i];
                if (split.Any(s => !string.Equals(s[i], w, StringComparison.OrdinalIgnoreCase))) break;
                words.Add(w);
            }
            return string.Join(" ", words);
        }

        public static decimal ParseMHz(string s)
        {
            decimal.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v);
            return v;
        }

        static int ParseInt(string s, int fallback)
        {
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }
    }
}
