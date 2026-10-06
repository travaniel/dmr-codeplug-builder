using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace CodeplugBuilder.Core
{
    public static class CallTypes
    {
        public const string Group = "Group Call";
        public const string Private = "Private Call";
        public const string All = "All Call";
        public static readonly string[] Values = { Group, Private, All };

        /// <summary>Accepts "Group", "group call", "P", "Private Call"... and returns the CPS spelling.</summary>
        public static string Normalize(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("p")) return Private;
            if (t.StartsWith("a")) return All;
            return Group;
        }
    }

    public static class Modes
    {
        public const string Digital = "Digital";
        public const string Analog = "Analog";
    }

    public static class Powers
    {
        public static readonly string[] Values = { "Low", "Mid", "High", "Turbo" };
    }

    public static class Bandwidths
    {
        public const string Narrow = "12.5K";
        public const string Wide = "25K";
        public static readonly string[] Values = { Narrow, Wide };
    }

    [DataContract(Namespace = "")]
    public sealed class Talkgroup
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public int Id { get; set; }
        [DataMember(Order = 3)] public string CallType { get; set; }

        public Talkgroup() { Init(); }

        public Talkgroup(string name, int id, string callType = CallTypes.Group)
        {
            Init();
            Name = name;
            Id = id;
            CallType = callType;
        }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Init(); }

        void Init()
        {
            Name = "";
            CallType = CallTypes.Group;
        }

        public bool IsGroupCall => CallType == CallTypes.Group;

        public Talkgroup Clone() { return (Talkgroup)MemberwiseClone(); }

        public override string ToString() { return Name + " (" + Id + ")"; }
    }

    /// <summary>One talkgroup carried by a repeater: becomes one channel.</summary>
    [DataContract(Namespace = "")]
    public sealed class RepeaterTalkgroup
    {
        [DataMember(Order = 1)] public int TalkgroupId { get; set; }
        [DataMember(Order = 2)] public int Slot { get; set; }
        /// <summary>Optional custom channel name. Blank means "prefix + talkgroup name".</summary>
        [DataMember(Order = 3, EmitDefaultValue = false)] public string ChannelName { get; set; }
        /// <summary>Put here by the zone's talkgroup set (see <see cref="ZoneInfo.Talkgroups"/>), not chosen for this repeater alone.</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public bool FromZone { get; set; }
        /// <summary>
        /// The channel's number in the CPS (Channel.CSV "No.", 1-4000), kept so APRS and hot keys that point at
        /// channel numbers stay valid. 0 = not numbered yet: the next Generate gives it the lowest free number.
        /// </summary>
        [DataMember(Order = 5, EmitDefaultValue = false)] public int ChannelNumber { get; set; }

        public RepeaterTalkgroup() { Init(); }

        public RepeaterTalkgroup(int talkgroupId, int slot, string channelName = null)
        {
            Init();
            TalkgroupId = talkgroupId;
            Slot = slot;
            ChannelName = channelName;
        }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Init(); }

        void Init() { Slot = 1; }

        public RepeaterTalkgroup Clone() { return (RepeaterTalkgroup)MemberwiseClone(); }
    }

    /// <summary>A digital repeater (one channel per talkgroup), an analog repeater/simplex channel (one channel), or the hotspot.</summary>
    [DataContract(Namespace = "")]
    public sealed class Repeater
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        /// <summary>Short text put in front of each talkgroup name to make channel names, e.g. "W5FC".</summary>
        [DataMember(Order = 2)] public string Prefix { get; set; }
        [DataMember(Order = 3)] public string Zone { get; set; }
        [DataMember(Order = 4)] public string Mode { get; set; }
        /// <summary>Frequency the radio receives on (the repeater's output), MHz.</summary>
        [DataMember(Order = 5)] public decimal RxMHz { get; set; }
        /// <summary>Frequency the radio transmits on (the repeater's input), MHz.</summary>
        [DataMember(Order = 6)] public decimal TxMHz { get; set; }
        [DataMember(Order = 7)] public int ColorCode { get; set; }
        [DataMember(Order = 8)] public string Power { get; set; }
        [DataMember(Order = 9)] public string Bandwidth { get; set; }
        [DataMember(Order = 10)] public string ToneEncode { get; set; }
        [DataMember(Order = 11)] public string ToneDecode { get; set; }
        /// <summary>Squelch opens only on the decode tone (CPS "Squelch Mode" = CTCSS/DCS).</summary>
        [DataMember(Order = 12)] public bool ToneSquelch { get; set; }
        /// <summary>TX Prohibit (receive-only channel, e.g. NOAA weather).</summary>
        [DataMember(Order = 13)] public bool RxOnly { get; set; }
        [DataMember(Order = 14)] public bool Enabled { get; set; }
        [DataMember(Order = 15)] public List<RepeaterTalkgroup> Talkgroups { get; set; }
        [DataMember(Order = 16, EmitDefaultValue = false)] public string Notes { get; set; }

        // Where the repeater is (from RadioID.net and the built-in map). Optional: null for repeaters typed in by
        // hand or imported from the CPS. Used for "zone per county/city" and to show project repeaters on the map.
        [DataMember(Order = 17, EmitDefaultValue = false)] public string City { get; set; }
        [DataMember(Order = 18, EmitDefaultValue = false)] public string State { get; set; }
        [DataMember(Order = 19, EmitDefaultValue = false)] public string Country { get; set; }
        [DataMember(Order = 20, EmitDefaultValue = false)] public string County { get; set; }
        /// <summary>Map area codes: country "US", state "US-TX", county "US-48451" (see <see cref="GeoAtlas"/>).</summary>
        [DataMember(Order = 21, EmitDefaultValue = false)] public string AreaCode { get; set; }
        [DataMember(Order = 22, EmitDefaultValue = false)] public double? Latitude { get; set; }
        [DataMember(Order = 23, EmitDefaultValue = false)] public double? Longitude { get; set; }
        /// <summary>The repeater's own DMR ID on RadioID.net, when it was added from there.</summary>
        [DataMember(Order = 24, EmitDefaultValue = false)] public int SourceId { get; set; }
        /// <summary>CPS channel number of an analog repeater's one channel (digital ones number each talkgroup entry). 0 = not numbered yet.</summary>
        [DataMember(Order = 25, EmitDefaultValue = false)] public int ChannelNumber { get; set; }

        public Repeater() { Init(); }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Init(); }

        void Init()
        {
            Name = "";
            Prefix = "";
            Zone = "";
            Mode = Modes.Digital;
            ColorCode = 1;
            Power = "High";
            Bandwidth = Bandwidths.Narrow;
            ToneEncode = "Off";
            ToneDecode = "Off";
            Enabled = true;
            Talkgroups = new List<RepeaterTalkgroup>();
        }

        public bool IsDigital => Mode != Modes.Analog;

        public static Repeater NewDigital(string name = "New repeater")
        {
            return new Repeater { Name = name, Mode = Modes.Digital, Bandwidth = Bandwidths.Narrow, Power = "High" };
        }

        public static Repeater NewAnalog(string name = "New analog")
        {
            return new Repeater { Name = name, Mode = Modes.Analog, Bandwidth = Bandwidths.Wide, Power = "High" };
        }

        public static Repeater NewHotspot()
        {
            return new Repeater
            {
                Name = "Hotspot",
                Prefix = "HS",
                Zone = "Hotspot",
                Mode = Modes.Digital,
                ColorCode = 1,
                Power = "Low",
                Bandwidth = Bandwidths.Narrow,
            };
        }

        public Repeater Clone()
        {
            var r = (Repeater)MemberwiseClone();
            r.Talkgroups = Talkgroups.Select(t => t.Clone()).ToList();
            return r;
        }

        public override string ToString() { return Name; }
    }

    /// <summary>Zone order and the channels selected on the A and B sides when the zone is picked.</summary>
    [DataContract(Namespace = "")]
    public sealed class ZoneInfo
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2, EmitDefaultValue = false)] public string AChannel { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string BChannel { get; set; }
        /// <summary>
        /// Talkgroups every DMR repeater in this zone carries (null when there are none, so older files stay as
        /// they were). Kept in step by <see cref="Project.AddZoneTalkgroup"/> and friends; the generator only
        /// ever reads the repeaters' own lists.
        /// </summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public List<ZoneTalkgroup> Talkgroups { get; set; }

        public ZoneInfo() { Name = ""; }
        public ZoneInfo(string name) { Name = name; }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Name = ""; }

        public bool HasTalkgroups => Talkgroups != null && Talkgroups.Count > 0;

        public ZoneTalkgroup FindTalkgroup(int id)
        {
            return Talkgroups?.FirstOrDefault(t => t.TalkgroupId == id);
        }
    }

    /// <summary>A talkgroup in a zone's set, with the slot it goes on for repeaters that don't already carry it.</summary>
    [DataContract(Namespace = "")]
    public sealed class ZoneTalkgroup
    {
        [DataMember(Order = 1)] public int TalkgroupId { get; set; }
        [DataMember(Order = 2)] public int Slot { get; set; }

        public ZoneTalkgroup() { Slot = 1; }
        public ZoneTalkgroup(int talkgroupId, int slot) { TalkgroupId = talkgroupId; Slot = slot == 2 ? 2 : 1; }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Slot = 1; }
    }

    [DataContract(Namespace = "")]
    public sealed class GenerationOptions
    {
        [DataMember(Order = 1)] public bool RxGroupListPerRepeater { get; set; }
        [DataMember(Order = 2)] public bool ScanListPerZone { get; set; }
        [DataMember(Order = 3)] public bool WriteRadioIdList { get; set; }
        [DataMember(Order = 4)] public int MaxNameLength { get; set; }
        [DataMember(Order = 5)] public int MaxChannels { get; set; }
        [DataMember(Order = 6)] public int MaxZones { get; set; }
        [DataMember(Order = 7)] public int MaxZoneChannels { get; set; }
        [DataMember(Order = 8)] public int MaxRxGroupMembers { get; set; }
        [DataMember(Order = 9)] public int MaxScanListChannels { get; set; }
        [DataMember(Order = 10, EmitDefaultValue = false)] public string OutputFolder { get; set; }
        /// <summary>Merge mode: keep channels, zones and talkgroups made in the CPS (read from <see cref="BaseExportFolder"/>).</summary>
        [DataMember(Order = 11, EmitDefaultValue = false)] public bool KeepCpsChannels { get; set; }
        /// <summary>A CPS "Export All" of the codeplug as it is in the CPS now (for <see cref="KeepCpsChannels"/>).</summary>
        [DataMember(Order = 12, EmitDefaultValue = false)] public string BaseExportFolder { get; set; }

        public GenerationOptions() { Init(); }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Init(); }

        void Init()
        {
            RxGroupListPerRepeater = true;
            ScanListPerZone = false;
            WriteRadioIdList = true;
            MaxNameLength = 16;
            MaxChannels = 4000;
            MaxZones = 250;
            MaxZoneChannels = 250;
            MaxRxGroupMembers = 64;
            MaxScanListChannels = 50;
        }
    }

    [DataContract(Namespace = "")]
    public sealed class Project
    {
        public const int CurrentFileVersion = 1;

        [DataMember(Order = 1)] public int FileVersion { get; set; }
        /// <summary>Name of your entry in the CPS Radio ID List, e.g. "Austin W6OZZ". Every channel points at it.</summary>
        [DataMember(Order = 2)] public string RadioIdName { get; set; }
        /// <summary>Your DMR ID. Used for RadioIDList.CSV.</summary>
        [DataMember(Order = 3)] public int RadioId { get; set; }
        [DataMember(Order = 4)] public List<Talkgroup> Talkgroups { get; set; }
        [DataMember(Order = 5)] public List<Repeater> Repeaters { get; set; }
        [DataMember(Order = 6)] public bool HotspotEnabled { get; set; }
        [DataMember(Order = 7)] public Repeater Hotspot { get; set; }
        [DataMember(Order = 8)] public List<ZoneInfo> Zones { get; set; }
        [DataMember(Order = 9)] public GenerationOptions Options { get; set; }
        // What this program has imported or generated before, so merge mode can tell channels, zones and
        // talkgroups made in the CPS from ones deleted here (see Merge.cs). Null when empty.
        [DataMember(Order = 10, EmitDefaultValue = false)] public List<KnownChannel> KnownChannels { get; set; }
        [DataMember(Order = 11, EmitDefaultValue = false)] public List<string> KnownZones { get; set; }
        [DataMember(Order = 12, EmitDefaultValue = false)] public List<int> KnownTalkgroups { get; set; }

        public Project() { Init(); }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Init(); }

        void Init()
        {
            FileVersion = CurrentFileVersion;
            RadioIdName = "";
            Talkgroups = new List<Talkgroup>();
            Repeaters = new List<Repeater>();
            Hotspot = Repeater.NewHotspot();
            Zones = new List<ZoneInfo>();
            Options = new GenerationOptions();
        }

        /// <summary>Repairs nulls left by older or hand-edited files.</summary>
        public void Normalize()
        {
            if (RadioIdName == null) RadioIdName = "";
            if (Talkgroups == null) Talkgroups = new List<Talkgroup>();
            if (Repeaters == null) Repeaters = new List<Repeater>();
            if (Hotspot == null) Hotspot = Repeater.NewHotspot();
            if (Zones == null) Zones = new List<ZoneInfo>();
            if (Options == null) Options = new GenerationOptions();
            Talkgroups.RemoveAll(t => t == null);
            Repeaters.RemoveAll(r => r == null);
            Zones.RemoveAll(z => z == null);
            foreach (var t in Talkgroups)
            {
                t.Name = t.Name ?? "";
                t.CallType = CallTypes.Normalize(t.CallType);
            }
            foreach (var r in Repeaters.Concat(new[] { Hotspot }))
            {
                r.Name = r.Name ?? "";
                r.Prefix = r.Prefix ?? "";
                r.Zone = r.Zone ?? "";
                r.Mode = r.Mode == Modes.Analog ? Modes.Analog : Modes.Digital;
                r.Power = string.IsNullOrEmpty(r.Power) ? "High" : r.Power;
                r.Bandwidth = string.IsNullOrEmpty(r.Bandwidth) ? (r.IsDigital ? Bandwidths.Narrow : Bandwidths.Wide) : r.Bandwidth;
                r.ToneEncode = Tones.Normalize(r.ToneEncode);
                r.ToneDecode = Tones.Normalize(r.ToneDecode);
                if (r.Talkgroups == null) r.Talkgroups = new List<RepeaterTalkgroup>();
                r.Talkgroups.RemoveAll(t => t == null);
                foreach (var t in r.Talkgroups) if (t.Slot != 2) t.Slot = 1;
            }
            Hotspot.Mode = Modes.Digital;
            foreach (var z in Zones)
            {
                z.Name = z.Name ?? "";
                if (z.Talkgroups == null) continue;
                z.Talkgroups.RemoveAll(t => t == null);
                foreach (var t in z.Talkgroups) if (t.Slot != 2) t.Slot = 1;
                if (z.Talkgroups.Count == 0) z.Talkgroups = null;
            }
            SyncZones();
        }

        public Talkgroup FindTalkgroup(int id)
        {
            return Talkgroups.FirstOrDefault(t => t.Id == id);
        }

        /// <summary>Repeaters that go into the codeplug, in output order (hotspot last).</summary>
        public IEnumerable<Repeater> ActiveRepeaters()
        {
            foreach (var r in Repeaters)
                if (r.Enabled) yield return r;
            if (HotspotEnabled) yield return Hotspot;
        }

        /// <summary>Every repeater including the hotspot (whether or not enabled).</summary>
        public IEnumerable<Repeater> AllRepeaters()
        {
            return Repeaters.Concat(new[] { Hotspot });
        }

        public static bool SameZone(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public ZoneInfo FindZone(string name)
        {
            return Zones.FirstOrDefault(z => SameZone(z.Name, name));
        }

        /// <summary>
        /// Keeps <see cref="Zones"/> in step with the repeaters: adds an entry for every zone name a repeater
        /// (or the hotspot) uses, keeping the existing order, and drops zones nothing uses any more.
        /// </summary>
        public void SyncZones()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in AllRepeaters())
            {
                string z = (r.Zone ?? "").Trim();
                if (z.Length == 0) continue;
                used.Add(z);
                if (FindZone(z) == null) Zones.Add(new ZoneInfo(z));
            }
            Zones.RemoveAll(z => !used.Contains((z.Name ?? "").Trim()));
        }

        /// <summary>Renames a zone everywhere it is used.</summary>
        public void RenameZone(string oldName, string newName)
        {
            newName = (newName ?? "").Trim();
            foreach (var r in AllRepeaters())
                if (SameZone(r.Zone, oldName)) r.Zone = newName;
            var existing = FindZone(newName);
            var info = FindZone(oldName);
            if (info != null && existing != null && existing != info)
            {
                // Merging: the combined zone keeps both talkgroup sets.
                foreach (var t in info.Talkgroups ?? new List<ZoneTalkgroup>())
                    if (existing.FindTalkgroup(t.TalkgroupId) == null)
                        (existing.Talkgroups ?? (existing.Talkgroups = new List<ZoneTalkgroup>())).Add(new ZoneTalkgroup(t.TalkgroupId, t.Slot));
                Zones.Remove(info);
                foreach (var r in ZoneRepeaters(newName)) ApplyZoneTalkgroups(r);
            }
            else if (info != null) info.Name = newName;
            SyncZones();
        }

        // ------------------------------------------------------------------
        // Zone talkgroup sets
        // ------------------------------------------------------------------

        /// <summary>The DMR repeaters (and the hotspot, when it's switched on) whose zone is <paramref name="zone"/>.</summary>
        public List<Repeater> ZoneRepeaters(string zone)
        {
            return AllRepeaters().Where(r => r.IsDigital && (r != Hotspot || HotspotEnabled) && SameZone(r.Zone, zone)).ToList();
        }

        /// <summary>Channels a zone will hold: one per talkgroup on its included DMR repeaters, one per analog channel.</summary>
        public int ZoneChannelCount(string zone)
        {
            return ActiveRepeaters().Where(r => SameZone(r.Zone, zone)).Sum(r => r.IsDigital ? r.Talkgroups.Count : 1);
        }

        /// <summary>Channels in the whole codeplug (before the generator drops duplicates or splits zones).</summary>
        public int ChannelCount()
        {
            return ActiveRepeaters().Sum(r => r.IsDigital ? r.Talkgroups.Count : 1);
        }

        /// <summary>
        /// Usual BrandMeister slot for a talkgroup: wide-area talkgroups (91 Worldwide, 93 North America, 3100 USA,
        /// other short IDs) on slot 1; Local 9, regional 8, cluster 2, US statewide (31xx) and long local IDs on slot 2.
        /// </summary>
        public static int DefaultSlot(int talkgroupId)
        {
            if (talkgroupId == 2 || talkgroupId == 8 || talkgroupId == 9) return 2;
            if (talkgroupId > 3100 && talkgroupId < 3200) return 2;
            if (talkgroupId >= 10000) return 2;
            return 1;
        }

        /// <summary>
        /// Adds a talkgroup to a zone's set and to every repeater in the zone that doesn't carry it yet (on
        /// <paramref name="slot"/>). Repeaters that already have it, on either slot, keep theirs. Returns the
        /// number of channels added.
        /// </summary>
        public int AddZoneTalkgroup(string zone, int talkgroupId, int slot)
        {
            var info = FindZone(zone);
            if (info == null) return 0;
            slot = slot == 2 ? 2 : 1;
            var t = info.FindTalkgroup(talkgroupId);
            if (t == null) (info.Talkgroups ?? (info.Talkgroups = new List<ZoneTalkgroup>())).Add(t = new ZoneTalkgroup(talkgroupId, slot));
            int added = 0;
            foreach (var r in ZoneRepeaters(zone))
            {
                if (r.Talkgroups.Any(e => e.TalkgroupId == talkgroupId)) continue;
                r.Talkgroups.Add(new RepeaterTalkgroup(talkgroupId, t.Slot) { FromZone = true });
                added++;
            }
            return added;
        }

        /// <summary>
        /// Takes a talkgroup out of a zone's set. With <paramref name="alsoListed"/> it goes off every repeater in
        /// the zone (also where the repeater listed it itself); without, only the channels the set put there go
        /// and repeaters that list it keep it. Returns the number of channels removed.
        /// </summary>
        public int RemoveZoneTalkgroup(string zone, int talkgroupId, bool alsoListed = true)
        {
            var info = FindZone(zone);
            if (info?.Talkgroups != null)
            {
                info.Talkgroups.RemoveAll(t => t.TalkgroupId == talkgroupId);
                if (info.Talkgroups.Count == 0) info.Talkgroups = null;
            }
            int removed = 0;
            foreach (var r in ZoneRepeaters(zone))
                removed += r.Talkgroups.RemoveAll(e => e.TalkgroupId == talkgroupId && (alsoListed || e.FromZone));
            return removed;
        }

        /// <summary>Changes a zone talkgroup's slot, moving the channels the zone put there (not the ones repeaters listed themselves).</summary>
        public void SetZoneTalkgroupSlot(string zone, int talkgroupId, int slot)
        {
            var t = FindZone(zone)?.FindTalkgroup(talkgroupId);
            if (t == null) return;
            t.Slot = slot == 2 ? 2 : 1;
            foreach (var r in ZoneRepeaters(zone))
                foreach (var e in r.Talkgroups.Where(e => e.FromZone && e.TalkgroupId == talkgroupId)) e.Slot = t.Slot;
        }

        /// <summary>
        /// Brings one repeater in line with its zone: drops channels a previous zone's set put there, and adds the
        /// current zone's talkgroups it doesn't carry yet. Call after adding a repeater or changing its zone.
        /// </summary>
        public void ApplyZoneTalkgroups(Repeater r)
        {
            if (r == null || !r.IsDigital) return;
            var info = FindZone(r.Zone);
            r.Talkgroups.RemoveAll(e => e.FromZone && info?.FindTalkgroup(e.TalkgroupId) == null);
            if (info?.Talkgroups == null) return;
            foreach (var t in info.Talkgroups)
                if (!r.Talkgroups.Any(e => e.TalkgroupId == t.TalkgroupId))
                    r.Talkgroups.Add(new RepeaterTalkgroup(t.TalkgroupId, t.Slot) { FromZone = true });
        }

        /// <summary>
        /// Forgets every stored channel number, so the next Generate numbers the channels 1, 2, 3... in output
        /// order. Channel numbers in APRS settings and hot keys in the CPS may then point at other channels.
        /// </summary>
        public void ClearChannelNumbers()
        {
            foreach (var r in AllRepeaters())
            {
                r.ChannelNumber = 0;
                foreach (var e in r.Talkgroups) e.ChannelNumber = 0;
            }
        }

        /// <summary>
        /// Records channels, zones and talkgroups as this program's own (after an import or a Generate), so merge
        /// mode won't bring them back from a CPS export once they're deleted here. Returns true when anything was new.
        /// </summary>
        public bool Remember(IEnumerable<KnownChannel> channels, IEnumerable<string> zones, IEnumerable<int> talkgroupIds)
        {
            bool changed = false;
            // Every name a number has had stays known: a channel renamed or deleted here, or a number reused for
            // another channel, must not come back from an older export as "made in the CPS".
            var known = new List<KnownChannel>(KnownChannels ?? new List<KnownChannel>());
            foreach (var c in channels)
            {
                if (c.Number <= 0 || known.Any(k => k.Number == c.Number && string.Equals(k.Name, c.Name, StringComparison.OrdinalIgnoreCase))) continue;
                known.Add(new KnownChannel(c.Number, c.Name));
                changed = true;
            }
            KnownChannels = known.Count == 0 ? null : known.OrderBy(k => k.Number).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var z = new List<string>(KnownZones ?? new List<string>());
            foreach (var name in zones)
                if (!string.IsNullOrWhiteSpace(name) && !z.Any(x => SameZone(x, name))) { z.Add(name.Trim()); changed = true; }
            KnownZones = z.Count == 0 ? null : z;
            var t = new List<int>(KnownTalkgroups ?? new List<int>());
            foreach (int id in talkgroupIds)
                if (id > 0 && !t.Contains(id)) { t.Add(id); changed = true; }
            KnownTalkgroups = t.Count == 0 ? null : t;
            return changed;
        }

        /// <summary>Channel numbers already stored in the project (including switched-off repeaters, which may come back).</summary>
        public HashSet<int> StoredChannelNumbers()
        {
            var used = new HashSet<int>();
            foreach (var r in AllRepeaters())
            {
                if (!r.IsDigital && r.ChannelNumber > 0) used.Add(r.ChannelNumber);
                if (r.IsDigital) foreach (var e in r.Talkgroups) if (e.ChannelNumber > 0) used.Add(e.ChannelNumber);
            }
            return used;
        }

        /// <summary>Zone names that at least one repeater (or the enabled hotspot) uses.</summary>
        public HashSet<string> UsedZoneNames()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ActiveRepeaters())
            {
                string z = (r.Zone ?? "").Trim();
                if (z.Length > 0) used.Add(z);
            }
            return used;
        }

        public int CountTalkgroupUse(int talkgroupId)
        {
            return AllRepeaters().Sum(r => r.IsDigital ? r.Talkgroups.Count(t => t.TalkgroupId == talkgroupId) : 0);
        }

        /// <summary>A talkgroup got a new number: repeaters and zone sets follow it.</summary>
        public void ChangeTalkgroupId(int oldId, int newId)
        {
            if (oldId <= 0 || oldId == newId) return;
            foreach (var r in AllRepeaters())
                foreach (var e in r.Talkgroups.Where(x => x.TalkgroupId == oldId)) e.TalkgroupId = newId;
            foreach (var z in Zones)
                foreach (var t in z.Talkgroups ?? new List<ZoneTalkgroup>())
                    if (t.TalkgroupId == oldId) t.TalkgroupId = newId;
        }

        /// <summary>Deletes talkgroups along with their channels on every repeater and their place in zone sets.</summary>
        public void DeleteTalkgroups(IEnumerable<Talkgroup> talkgroups)
        {
            var doomed = talkgroups.ToList();
            var set = new HashSet<int>(doomed.Select(t => t.Id).Where(id => id > 0));
            foreach (var r in AllRepeaters()) r.Talkgroups.RemoveAll(e => set.Contains(e.TalkgroupId));
            foreach (var z in Zones)
            {
                if (z.Talkgroups == null) continue;
                z.Talkgroups.RemoveAll(t => set.Contains(t.TalkgroupId));
                if (z.Talkgroups.Count == 0) z.Talkgroups = null;
            }
            foreach (var t in doomed) Talkgroups.Remove(t);
        }
    }
}
