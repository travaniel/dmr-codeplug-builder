using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>One channel the generator will write.</summary>
    public sealed class GeneratedChannel
    {
        public string Name;
        public Repeater Repeater;
        /// <summary>The talkgroup entry this channel came from (null for analog channels).</summary>
        public RepeaterTalkgroup Entry;
        public Talkgroup Talkgroup;
        public string Zone;
        public string RxGroupList;
        /// <summary>The scan lists this channel scans with, in order (Channel.CSV Scan List 1-8): its zone's, then Favorites and Local FM.</summary>
        public List<string> ScanLists = new List<string>();
        public string ScanList => ScanLists.Count > 0 ? ScanLists[0] : null;
        /// <summary>The CPS channel number ("No."): the stored one, or the lowest free one for a new channel.</summary>
        public int Number;
        public bool IsDigital => Entry != null;

        /// <summary>Set for a channel kept from the CPS export in merge mode (it isn't the project's).</summary>
        public KeptChannel Kept;

        /// <summary>The number stored in the project for this channel (0 = none yet).</summary>
        public int StoredNumber => IsDigital ? Entry.ChannelNumber : Repeater.ChannelNumber;
    }

    public sealed class GeneratedZone
    {
        public string Name;
        public List<GeneratedChannel> Members = new List<GeneratedChannel>();
        public string AChannel;
        public string BChannel;
        /// <summary>The project's zone this was made from (null for zones kept from the CPS).</summary>
        public ZoneInfo Info;
        /// <summary>A zone made in the CPS, kept in merge mode.</summary>
        public bool FromCps;
    }

    public sealed class GeneratedCodeplug
    {
        public CsvTable Channels;
        public CsvTable Zones;
        public CsvTable TalkGroups;
        public CsvTable RxGroupLists;
        /// <summary>Null when scan lists are turned off.</summary>
        public CsvTable ScanLists;
        /// <summary>DigitalContactList.CSV (caller names), when the App attached one (<see cref="CallerDatabase"/>); null otherwise.</summary>
        public CsvTable Callers;
        /// <summary>APRS.CSV text (written as the CPS writes it, <see cref="Aprs.ToCsv"/>) when the project has APRS settings; null otherwise.</summary>
        public string AprsCsv;
        /// <summary>Null when there is no DMR ID to write.</summary>
        public CsvTable RadioIds;
        /// <summary>GpsRoaming.CSV when GPS zone switching is on (<see cref="Core.GpsRoaming"/>); null otherwise.</summary>
        public CsvTable GpsRoaming;
        public List<GpsZoneEntry> GpsEntries = new List<GpsZoneEntry>();

        public List<GeneratedChannel> ChannelList = new List<GeneratedChannel>();
        public List<GeneratedZone> ZoneList = new List<GeneratedZone>();
        /// <summary>Channels kept from the CPS export in merge mode (not in <see cref="ChannelList"/>).</summary>
        public List<KeptChannel> KeptChannels = new List<KeptChannel>();
        /// <summary>Renames, skipped entries and limits hit while generating.</summary>
        public List<string> Notes = new List<string>();

        public const string DefaultListFileName = "CodeplugBuilder.LST";

        /// <summary>The files to write, in the order the CPS imports them.</summary>
        public List<KeyValuePair<string, CsvTable>> Files()
        {
            var files = new List<KeyValuePair<string, CsvTable>>
            {
                new KeyValuePair<string, CsvTable>(CpsFormat.ChannelFile, Channels),
            };
            if (RadioIds != null) files.Add(new KeyValuePair<string, CsvTable>(CpsFormat.RadioIdFile, RadioIds));
            files.Add(new KeyValuePair<string, CsvTable>(CpsFormat.ZoneFile, Zones));
            if (ScanLists != null) files.Add(new KeyValuePair<string, CsvTable>(CpsFormat.ScanListFile, ScanLists));
            files.Add(new KeyValuePair<string, CsvTable>(CpsFormat.TalkGroupsFile, TalkGroups));
            files.Add(new KeyValuePair<string, CsvTable>(CpsFormat.RxGroupFile, RxGroupLists));
            if (Callers != null) files.Add(new KeyValuePair<string, CsvTable>(CallerDatabase.File, Callers));
            if (GpsRoaming != null) files.Add(new KeyValuePair<string, CsvTable>(Core.GpsRoaming.File, GpsRoaming));
            return files;
        }

        public string ListFileText()
        {
            var names = Files().Select(f => f.Key).ToList();
            if (AprsCsv != null) names.Add(Aprs.File);
            return CpsFormat.BuildListFile(names);
        }

        /// <summary>Writes the CSVs and the .LST file list. Returns the paths written.</summary>
        public List<string> WriteTo(string folder, string listFileName = DefaultListFileName)
        {
            Directory.CreateDirectory(folder);
            var written = new List<string>();
            foreach (var f in Files())
            {
                string path = Path.Combine(folder, f.Key);
                f.Value.Save(path);
                written.Add(path);
            }
            if (AprsCsv != null)
            {
                string aprs = Path.Combine(folder, Aprs.File);
                File.WriteAllText(aprs, AprsCsv, CsvTable.FileEncoding);
                written.Add(aprs);
            }
            string lst = Path.Combine(folder, listFileName);
            File.WriteAllText(lst, ListFileText(), CsvTable.FileEncoding);
            written.Add(lst);
            return written;
        }
    }

    public static class CodeplugGenerator
    {
        /// <param name="mergeBase">Merge mode: a CPS export whose channels made in the CPS are kept (see Merge.cs). Null: the project only.</param>
        public static GeneratedCodeplug Generate(Project p, CpsFormat f, CpsExport mergeBase = null)
        {
            var o = p.Options ?? new GenerationOptions();
            int max = o.MaxNameLength > 0 ? o.MaxNameLength : 16;
            var g = new GeneratedCodeplug();

            // ---- Talk groups -------------------------------------------------------------
            var tgNamer = new UniqueNamer(max, "Talkgroup");
            var tgNames = new Dictionary<int, string>();
            var tgById = new Dictionary<int, Talkgroup>();
            g.TalkGroups = f.TalkGroups.CloneHeader();
            var tgTemplate = f.TalkGroups.Rows.FirstOrDefault();
            foreach (var tg in p.Talkgroups)
            {
                if (tg.Id <= 0) { g.Notes.Add("Talkgroup \"" + tg.Name + "\" has no ID and was left out."); continue; }
                if (tgNames.ContainsKey(tg.Id)) { g.Notes.Add("Talkgroup ID " + tg.Id + " is listed twice; only the first (\"" + tgNames[tg.Id] + "\") was used."); continue; }
                string name = tgNamer.Claim(tg.Name);
                if (name != Naming.Fit(tg.Name, 0)) g.Notes.Add("Talkgroup \"" + tg.Name + "\" is written as \"" + name + "\".");
                tgNames[tg.Id] = name;
                tgById[tg.Id] = tg;

                var row = g.TalkGroups.NewRow(tgTemplate);
                g.TalkGroups.Set(row, Num(g.TalkGroups.Rows.Count + 1), "No.");
                g.TalkGroups.Set(row, tg.Id.ToString(CultureInfo.InvariantCulture), "Radio ID");
                g.TalkGroups.Set(row, name, "Name");
                g.TalkGroups.Set(row, CallTypes.Normalize(tg.CallType), "Call Type");
                if (tgTemplate == null) g.TalkGroups.Set(row, "None", "Call Alert");
                g.TalkGroups.Rows.Add(row);
            }

            // Analog channels still need a contact in the CPS; use the first talkgroup.
            Talkgroup firstTg = p.Talkgroups.FirstOrDefault(t => tgNames.ContainsKey(t.Id));
            string defaultContact = firstTg != null ? tgNames[firstTg.Id] : null;
            string defaultContactType = firstTg != null ? CallTypes.Normalize(firstTg.CallType) : CallTypes.Group;

            // ---- Channel names -------------------------------------------------------------
            var chNamer = new UniqueNamer(max);
            foreach (var v in f.VfoRows) chNamer.Reserve(f.Channels.Get(v, "Channel Name"));

            foreach (var r in p.ActiveRepeaters())
            {
                string label = string.IsNullOrWhiteSpace(r.Name) ? "(unnamed)" : r.Name;
                if (r.IsDigital)
                {
                    if (r.Talkgroups.Count == 0) g.Notes.Add(label + " has no talkgroups, so it has no channels.");
                    foreach (var e in r.Talkgroups)
                    {
                        if (!tgById.TryGetValue(e.TalkgroupId, out Talkgroup tg))
                        {
                            g.Notes.Add(label + ": talkgroup " + e.TalkgroupId + " isn't in the talkgroup list, so that channel was skipped.");
                            continue;
                        }
                        string desired = !string.IsNullOrWhiteSpace(e.ChannelName)
                            ? e.ChannelName
                            : r.AutoChannelName(e, tg.Name, max);
                        if (string.IsNullOrWhiteSpace(e.ChannelName) && chNamer.IsUsed(Naming.Fit(desired, max)))
                        {
                            // "WA State ARES" and "WA State ARES TAC" both cut to "NC7Q WA State AR": keep the last word.
                            // "Local" (TG 9) and the repeater's own "KC5EZZ Local" on the other slot: add the slot.
                            string alt = r.AutoChannelName(e, tg.Name, max, keepLastWord: true);
                            string clash = Naming.Fit(desired, max);
                            bool sameRepeater = g.ChannelList.Any(c => c.Repeater == r && string.Equals(c.Name, clash, StringComparison.OrdinalIgnoreCase));
                            if (chNamer.IsUsed(alt) && sameRepeater) alt = Naming.AutoChannelName(r.Prefix, tg.Name + " TS" + (e.Slot == 2 ? "2" : "1"), max, keepLastWord: true);
                            if (!chNamer.IsUsed(alt)) desired = alt;
                        }
                        string name = chNamer.Claim(desired);
                        if (!string.Equals(name, Naming.Fit(desired, max), StringComparison.OrdinalIgnoreCase))
                            g.Notes.Add("Channel name \"" + Naming.Fit(desired, max) + "\" was already taken, so " + label + " / " + tg.Name + " is \"" + name + "\".");
                        g.ChannelList.Add(new GeneratedChannel { Name = name, Repeater = r, Entry = e, Talkgroup = tg, Zone = r.Zone });
                    }
                }
                else
                {
                    string name = chNamer.Claim(r.Name);
                    if (!string.Equals(name, Naming.Fit(r.Name, max), StringComparison.OrdinalIgnoreCase))
                        g.Notes.Add("Channel name \"" + Naming.Fit(r.Name, max) + "\" was already taken, so it is written as \"" + name + "\".");
                    g.ChannelList.Add(new GeneratedChannel { Name = name, Repeater = r, Zone = r.Zone });
                }
            }

            if (g.ChannelList.Count > 0 && defaultContact == null)
                throw new InvalidOperationException("Add at least one talkgroup. The CPS needs a contact on every channel, even analog ones.");
            if (g.ChannelList.Count > o.MaxChannels)
                g.Notes.Add("This codeplug has " + g.ChannelList.Count + " channels; the radio holds " + o.MaxChannels + ".");
            var merge = mergeBase != null ? new Merge(p, mergeBase, g, f, max) : null;
            NumberChannels(p, g, merge?.ReservedNumbers);
            if (merge != null)
            {
                merge.PlaceNumbers();
                merge.AddTalkgroups(tgNamer, tgNames, tgTemplate);
                g.KeptChannels = merge.Kept;
                if (g.ChannelList.Count + merge.Kept.Count > o.MaxChannels)
                    g.Notes.Add("With the channels made in the CPS this codeplug has " + (g.ChannelList.Count + merge.Kept.Count) + " channels; the radio holds " + o.MaxChannels + ".");
            }

            // ---- Receive group lists (one per digital repeater) ----------------------------
            g.RxGroupLists = f.RxGroupLists.CloneHeader();
            var rgTemplate = f.RxGroupLists.Rows.FirstOrDefault();
            var rgNamer = new UniqueNamer(max, "RX List");
            var listBySet = new Dictionary<string, string>();
            int groupListsOver = 0;
            if (o.RxGroupListPerRepeater)
            {
                foreach (var r in p.ActiveRepeaters().Where(x => x.IsDigital))
                {
                    var members = g.ChannelList
                        .Where(c => c.Repeater == r && c.Talkgroup.IsGroupCall)
                        .Select(c => c.Talkgroup)
                        .Distinct()
                        .ToList();
                    if (members.Count == 0) continue;
                    if (members.Count > o.MaxRxGroupMembers)
                    {
                        g.Notes.Add(r.Name + " has " + members.Count + " talkgroups; its receive group list keeps the first " + o.MaxRxGroupMembers + ".");
                        members = members.Take(o.MaxRxGroupMembers).ToList();
                    }
                    // The radio holds 250 RX group lists. Past that, a repeater shares an earlier list with the same talkgroups, or has none.
                    string setKey = string.Join(",", members.Select(t => t.Id).OrderBy(x => x));
                    if (g.RxGroupLists.Rows.Count >= CodeplugBuilder.Core.Radio.Dmr6x2Pro.MaxGroupLists)
                    {
                        if (listBySet.TryGetValue(setKey, out string shared)) foreach (var c in g.ChannelList.Where(c => c.Repeater == r)) c.RxGroupList = shared;
                        else groupListsOver++;
                        continue;
                    }
                    string listName = rgNamer.Claim(r.Name);
                    if (!listBySet.ContainsKey(setKey)) listBySet[setKey] = listName;
                    var row = g.RxGroupLists.NewRow(rgTemplate);
                    g.RxGroupLists.Set(row, Num(g.RxGroupLists.Rows.Count + 1), "No.");
                    g.RxGroupLists.Set(row, listName, "Group Name");
                    g.RxGroupLists.Set(row, string.Join("|", members.Select(t => tgNames[t.Id])), "Contact");
                    g.RxGroupLists.Set(row, string.Join("|", members.Select(t => t.Id.ToString(CultureInfo.InvariantCulture))), "Contact TG/DMR ID");
                    g.RxGroupLists.Rows.Add(row);
                    foreach (var c in g.ChannelList.Where(c => c.Repeater == r)) c.RxGroupList = listName;
                }
                if (groupListsOver > 0)
                    g.Notes.Add("The radio holds " + CodeplugBuilder.Core.Radio.Dmr6x2Pro.MaxGroupLists + " RX group lists. " + groupListsOver + " repeater(s) beyond that have no RX group list (they receive only their channel's talkgroup); repeaters with the same talkgroups share a list.");
            }

            // ---- Zones ---------------------------------------------------------------------
            var order = new List<string>();
            foreach (var z in p.Zones)
                if (!string.IsNullOrWhiteSpace(z.Name) && !order.Any(x => Project.SameZone(x, z.Name))) order.Add(z.Name.Trim());
            foreach (var c in g.ChannelList)
                if (!string.IsNullOrWhiteSpace(c.Zone) && !order.Any(x => Project.SameZone(x, c.Zone))) order.Add(c.Zone.Trim());

            // A zone holds its listed members, its own repeaters' channels and its rule's (Project.ZoneChannels); one channel
            // can be in several zones, since the CPS links zones by channel name.
            var byKey = new Dictionary<object, GeneratedChannel>();
            foreach (var c in g.ChannelList) byKey[(object)c.Entry ?? c.Repeater] = c;
            var zoneNamer = new UniqueNamer(max, "Zone");
            foreach (string zone in order)
            {
                var members = p.ZoneChannels(zone).Select(c => byKey.TryGetValue(c.Key, out var gc) ? gc : null).Where(c => c != null).ToList();
                if (members.Count == 0) continue;
                var info = p.FindZone(zone);
                int size = o.MaxZoneChannels > 0 ? o.MaxZoneChannels : members.Count;
                int parts = (members.Count + size - 1) / size;
                if (parts > 1) g.Notes.Add("Zone \"" + zone + "\" has " + members.Count + " channels, so it was split into " + parts + " zones of up to " + size + ".");
                for (int k = 0; k < parts; k++)
                {
                    var chunk = members.Skip(k * size).Take(size).ToList();
                    var gz = new GeneratedZone
                    {
                        Name = zoneNamer.Claim(k == 0 ? zone : zone + " " + (k + 1)),
                        Members = chunk,
                        Info = info,
                    };
                    gz.AChannel = Pick(chunk, info?.AChannel, 0);
                    gz.BChannel = Pick(chunk, info?.BChannel, chunk.Count > 1 ? 1 : 0);
                    g.ZoneList.Add(gz);
                }
            }
            merge?.AddRxLists(rgNamer, rgTemplate);
            merge?.AddZones(zoneNamer, o.MaxZoneChannels);
            if (g.ZoneList.Count > o.MaxZones)
                g.Notes.Add("This codeplug has " + g.ZoneList.Count + " zones; the radio holds " + o.MaxZones + ".");

            g.Zones = f.Zones.CloneHeader();
            var znTemplate = f.Zones.Rows.FirstOrDefault();
            foreach (var z in g.ZoneList)
            {
                var row = g.Zones.NewRow(znTemplate);
                var a = z.Members.First(m => m.Name == z.AChannel);
                var b = z.Members.First(m => m.Name == z.BChannel);
                g.Zones.Set(row, Num(g.Zones.Rows.Count + 1), "No.");
                g.Zones.Set(row, z.Name, "Zone Name");
                g.Zones.Set(row, string.Join("|", z.Members.Select(m => m.Name)), "Zone Channel Member");
                g.Zones.Set(row, string.Join("|", z.Members.Select(m => f.FormatFrequency(m.Repeater.RxMHz))), "Zone Channel Member RX Frequency");
                g.Zones.Set(row, string.Join("|", z.Members.Select(m => f.FormatFrequency(m.Repeater.TxMHz))), "Zone Channel Member TX Frequency");
                g.Zones.Set(row, a.Name, "A Channel");
                g.Zones.Set(row, f.FormatFrequency(a.Repeater.RxMHz), "A Channel RX Frequency");
                g.Zones.Set(row, f.FormatFrequency(a.Repeater.TxMHz), "A Channel TX Frequency");
                g.Zones.Set(row, b.Name, "B Channel");
                g.Zones.Set(row, f.FormatFrequency(b.Repeater.RxMHz), "B Channel RX Frequency");
                g.Zones.Set(row, f.FormatFrequency(b.Repeater.TxMHz), "B Channel TX Frequency");
                g.Zones.Rows.Add(row);
            }

            // ---- Scan lists (optional, one per zone) ----------------------------------------
            if (o.ScanListPerZone)
            {
                g.ScanLists = f.ScanLists.CloneHeader();
                var scanNamer = new UniqueNamer(max, "Scan List");
                List<string> AddScanRow(string listName, List<GeneratedChannel> members)
                {
                    var row = f.ScanTemplate != null ? g.ScanLists.NewRow(f.ScanTemplate) : DefaultScanRow(g.ScanLists);
                    g.ScanLists.Set(row, Num(g.ScanLists.Rows.Count + 1), "No.");
                    g.ScanLists.Set(row, listName, "Scan List Name");
                    g.ScanLists.Set(row, string.Join("|", members.Select(m => m.Name)), "Scan Channel Member");
                    g.ScanLists.Set(row, string.Join("|", members.Select(m => f.FormatFrequency(m.Repeater.RxMHz))), "Scan Channel Member RX Frequency");
                    g.ScanLists.Set(row, string.Join("|", members.Select(m => f.FormatFrequency(m.Repeater.TxMHz))), "Scan Channel Member TX Frequency");
                    g.ScanLists.Rows.Add(row);
                    return row;
                }
                foreach (var z in g.ZoneList)
                {
                    var members = z.Members;
                    if (members.Count > o.MaxScanListChannels)
                    {
                        g.Notes.Add("Scan list for zone \"" + z.Name + "\" keeps the first " + o.MaxScanListChannels + " of its " + members.Count + " channels.");
                        members = members.Take(o.MaxScanListChannels).ToList();
                    }
                    string name = scanNamer.Claim(z.Name);
                    var row = AddScanRow(name, members);
                    // A Favorites zone's list watches the home channel (the hotspot, else the nearest repeater) while it scans,
                    // and is also each member's next scan list (their first stays their own zone's).
                    bool favorites = o.FavoritesScanPriority && z.Info != null && p.ZoneKindOf(z.Info) == ZoneKinds.Favorites;
                    if (favorites && members.Count > 0)
                    {
                        var priority = ScanPriority(p, members);
                        g.ScanLists.Set(row, ScanPriorityValues.Select1, "Priority Channel Select");
                        g.ScanLists.Set(row, priority.Name, "Priority Channel 1");
                    }
                    foreach (var m in members)
                        if (m.ScanLists.Count == 0 || (favorites && m.ScanLists.Count < MaxChannelScanLists && !m.ScanLists.Contains(name))) m.ScanLists.Add(name);
                }
                merge?.AddScanLists(scanNamer, t => f.ScanTemplate != null ? t.NewRow(f.ScanTemplate) : DefaultScanRow(t));

                // Local FM: analog repeaters near home, nearest first; each member's next scan list.
                if (o.LocalAnalogScanList)
                {
                    double miles = o.LocalAnalogMiles > 0 ? o.LocalAnalogMiles : GenerationOptions.DefaultLocalAnalogMiles;
                    var near = g.ChannelList.Where(c => !c.IsDigital && !c.Repeater.RxOnly && !Presets.IsPreset(c.Repeater))
                                            .Select(c => new { c, d = p.DistanceKm(c.Repeater) })
                                            .Where(x => x.d != null && x.d <= miles * Distances.KmPerMile)
                                            .OrderBy(x => x.d).Select(x => x.c).Take(o.MaxScanListChannels).ToList();
                    if (near.Count > 0) // none without a home town
                    {
                        string name = scanNamer.Claim("Local FM");
                        AddScanRow(name, near);
                        foreach (var m in near) if (m.ScanLists.Count < MaxChannelScanLists && !m.ScanLists.Contains(name)) m.ScanLists.Add(name);
                    }
                }
            }

            // ---- Channel rows --------------------------------------------------------------
            g.Channels = f.Channels.CloneHeader();
            string radioId = string.IsNullOrWhiteSpace(p.RadioIdName) ? null : Naming.Clean(p.RadioIdName, max);
            // Rows in channel-number order, like the CPS's own export; zones keep the project's order.
            var rows = new List<KeyValuePair<int, List<string>>>();
            foreach (var c in g.ChannelList)
            {
                var r = c.Repeater;
                var row = g.Channels.NewRow(c.IsDigital ? f.DigitalTemplate : f.AnalogTemplate);
                g.Channels.Set(row, Num(c.Number), "No.");
                g.Channels.Set(row, c.Name, "Channel Name");
                g.Channels.Set(row, f.FormatFrequency(r.RxMHz), "Receive Frequency");
                g.Channels.Set(row, f.FormatFrequency(r.TxMHz), "Transmit Frequency");
                g.Channels.Set(row, r.Power, "Transmit Power");
                g.Channels.Set(row, r.RxOnly ? "On" : "Off", "TX Prohibit", "PTT Prohibit");
                if (radioId != null) g.Channels.Set(row, radioId, "Radio ID");
                if (o.PoliteTransmit) g.Channels.Set(row, r.PoliteTxPermit(r == p.Hotspot), "Busy channel Lock-Out/TX Permit", "TX Permit");

                if (c.IsDigital)
                {
                    g.Channels.Set(row, Bandwidths.Narrow, "Band Width");
                    g.Channels.Set(row, "Off", "CTCSS/DCS Decode");
                    g.Channels.Set(row, "Off", "CTCSS/DCS Encode");
                    g.Channels.Set(row, tgNames[c.Talkgroup.Id], "Contact");
                    g.Channels.Set(row, CallTypes.Normalize(c.Talkgroup.CallType), "Contact Call Type");
                    g.Channels.Set(row, c.Talkgroup.Id.ToString(CultureInfo.InvariantCulture), "Contact TG/DMR ID");
                    g.Channels.Set(row, r.ColorCode.ToString(CultureInfo.InvariantCulture), "Color Code");
                    g.Channels.Set(row, c.Entry.Slot == 2 ? "2" : "1", "Slot");
                    g.Channels.Set(row, c.RxGroupList ?? "None", "Receive Group List");
                }
                else
                {
                    string decode = Tones.Normalize(r.ToneDecode);
                    g.Channels.Set(row, r.Bandwidth == Bandwidths.Narrow ? Bandwidths.Narrow : Bandwidths.Wide, "Band Width");
                    g.Channels.Set(row, decode, "CTCSS/DCS Decode");
                    g.Channels.Set(row, Tones.Normalize(r.ToneEncode), "CTCSS/DCS Encode");
                    g.Channels.Set(row, r.ToneSquelch && decode != "Off" ? "CTCSS/DCS" : "Carrier", "Squelch Mode");
                    g.Channels.Set(row, defaultContact, "Contact");
                    g.Channels.Set(row, defaultContactType, "Contact Call Type");
                    if (firstTg != null) g.Channels.Set(row, firstTg.Id.ToString(CultureInfo.InvariantCulture), "Contact TG/DMR ID");
                    g.Channels.Set(row, "None", "Receive Group List");
                }
                for (int k = 0; k < c.ScanLists.Count && k < MaxChannelScanLists; k++)
                {
                    if (k == 0) g.Channels.Set(row, c.ScanLists[k], "Scan List 1", "Scan List");
                    else g.Channels.Set(row, c.ScanLists[k], "Scan List " + (k + 1).ToString(CultureInfo.InvariantCulture));
                }
                rows.Add(new KeyValuePair<int, List<string>>(c.Number, row));
            }
            merge?.AddChannelRows(rows);
            foreach (var kv in rows.OrderBy(x => x.Key)) g.Channels.Rows.Add(kv.Value);

            // VFO A/B rows keep their own numbers; point them at contacts that still exist.
            foreach (var v in f.VfoRows)
            {
                var row = g.Channels.NewRow(v);
                if (defaultContact != null)
                {
                    g.Channels.Set(row, defaultContact, "Contact");
                    g.Channels.Set(row, defaultContactType, "Contact Call Type");
                    if (firstTg != null) g.Channels.Set(row, firstTg.Id.ToString(CultureInfo.InvariantCulture), "Contact TG/DMR ID");
                }
                if (radioId != null) g.Channels.Set(row, radioId, "Radio ID");
                g.Channels.Set(row, "None", "Receive Group List");
                g.Channels.Rows.Add(row);
            }

            // ---- Radio ID list -------------------------------------------------------------
            if (o.WriteRadioIdList && p.RadioId > 0 && radioId != null)
            {
                g.RadioIds = f.RadioIds.CloneHeader();
                var row = g.RadioIds.NewRow(f.RadioIds.Rows.FirstOrDefault());
                g.RadioIds.Set(row, "1", "No.");
                g.RadioIds.Set(row, p.RadioId.ToString(CultureInfo.InvariantCulture), "Radio ID");
                g.RadioIds.Set(row, radioId, "Name");
                g.RadioIds.Rows.Add(row);
            }
            merge?.AddRadioIds();
            if (p.Aprs != null) g.AprsCsv = Aprs.ToCsv(p.Aprs);

            // ---- GPS zone switching (GpsRoaming.CSV refers to zones by position, so it goes with every Zone.CSV) ----
            if (o.GpsZoneSwitching)
            {
                g.GpsEntries = Core.GpsRoaming.Plan(p, g.ZoneList, o.GpsMarginKm > 0 ? o.GpsMarginKm : Core.GpsRoaming.DefaultMarginKm, g.Notes);
                g.GpsRoaming = Core.GpsRoaming.ToTable(g.GpsEntries);
                if (g.GpsEntries.Count == 0)
                    g.Notes.Add("GPS zone switching is on, but no area zone has repeaters with a known position, so GpsRoaming.CSV switches nothing.");
            }

            return g;
        }

        /// <summary>
        /// Channel numbers: a channel keeps the number stored in the project (from the CPS import or an earlier
        /// Generate) when it's valid and not taken; the rest get the lowest numbers nobody holds, in output order.
        /// Numbers held by switched-off repeaters are left free for them. With nothing stored this is 1, 2, 3...
        /// </summary>
        static void NumberChannels(Project p, GeneratedCodeplug g, HashSet<int> alsoTaken)
        {
            var taken = new Dictionary<int, GeneratedChannel>();
            foreach (var c in g.ChannelList)
            {
                int n = c.StoredNumber;
                if (n <= 0) continue;
                if (n >= CpsFormat.FirstVfoNumber)
                    g.Notes.Add("Channel \"" + c.Name + "\" had number " + n + ", which the radio doesn't have; it gets a new number.");
                else if (taken.TryGetValue(n, out var holder))
                    g.Notes.Add("Channels \"" + holder.Name + "\" and \"" + c.Name + "\" both had number " + n + "; \"" + c.Name + "\" gets a new number.");
                else
                {
                    taken[n] = c;
                    c.Number = n;
                }
            }
            var reserved = p.StoredChannelNumbers(); // includes numbers of channels not generated this time
            if (alsoTaken != null) reserved.UnionWith(alsoTaken); // channels kept from the CPS (merge mode)
            int next = 1;
            foreach (var c in g.ChannelList.Where(x => x.Number == 0))
            {
                while (taken.ContainsKey(next) || reserved.Contains(next) || next == CpsFormat.FirstVfoNumber || next == CpsFormat.FirstVfoNumber + 1) next++;
                c.Number = next;
                taken[next] = c;
            }
        }

        /// <summary>
        /// Stores the numbers <paramref name="g"/> gave its channels back into the project, so they stay put next
        /// time. Returns how many channels got a new or different number (the project then needs saving).
        /// </summary>
        public static int KeepChannelNumbers(GeneratedCodeplug g)
        {
            int changed = 0;
            foreach (var c in g.ChannelList)
            {
                if (c.StoredNumber == c.Number) continue;
                if (c.IsDigital) c.Entry.ChannelNumber = c.Number; else c.Repeater.ChannelNumber = c.Number;
                changed++;
            }
            return changed;
        }

        /// <summary>
        /// After the files are written: records the project's channels (number + name), zones and talkgroups as
        /// this program's own, so merge mode won't bring them back from a CPS export once they're deleted here.
        /// Returns true when the project changed.
        /// </summary>
        public static bool RememberOutput(Project p, GeneratedCodeplug g)
        {
            return p.Remember(g.ChannelList.Select(c => new KnownChannel(c.Number, c.Name)),
                              g.ZoneList.Where(z => !z.FromCps).Select(z => z.Name),
                              p.Talkgroups.Select(t => t.Id));
        }

        /// <summary>Channel.CSV has Scan List 1-8.</summary>
        public const int MaxChannelScanLists = 8;

        /// <summary>
        /// The channel a Favorites scan list keeps watching (it must be one of the list's members, CPS 1.22e): a hotspot channel,
        /// else the member nearest home, else the first.
        /// </summary>
        public static GeneratedChannel ScanPriority(Project p, List<GeneratedChannel> members)
        {
            var hotspot = members.FirstOrDefault(m => m.Repeater == p.Hotspot);
            if (hotspot != null) return hotspot;
            var nearest = members.Select(m => new { m, d = p.DistanceKm(m.Repeater) }).Where(x => x.d != null).OrderBy(x => x.d).FirstOrDefault();
            return nearest?.m ?? members[0];
        }

        static string Pick(List<GeneratedChannel> chunk, string wanted, int fallbackIndex)
        {
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var hit = chunk.FirstOrDefault(c => string.Equals(c.Name, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.Name;
            }
            return chunk[Math.Min(fallbackIndex, chunk.Count - 1)].Name;
        }

        /// <summary>CPS defaults for a scan list when the export didn't contain one to copy.</summary>
        static List<string> DefaultScanRow(CsvTable t)
        {
            var row = t.NewRow(null);
            t.Set(row, "Off", "Scan Mode");
            t.Set(row, "Off", "Priority Channel Select");
            t.Set(row, "Off", "Priority Channel 1");
            t.Set(row, "Off", "Priority Channel 2");
            t.Set(row, "Selected", "Revert Channel");
            t.Set(row, "2.0", "Look Back Time A[s]");
            t.Set(row, "3.0", "Look Back Time B[s]");
            t.Set(row, "3.1", "Dropout Delay Time[s]");
            t.Set(row, "3.1", "Dwell Time[s]");
            return row;
        }

        static string Num(int n) { return n.ToString(CultureInfo.InvariantCulture); }
    }
}
