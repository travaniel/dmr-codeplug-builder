using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>Channel.CSV "Busy channel Lock-Out/TX Permit" values (CPS 1.22e spellings, english.ini 20082/20088).</summary>
    public static class TxPermits
    {
        public const string Always = "Always";
        public const string SameColorCode = "Same Color Code";
        /// <summary>Analog: no busy-channel lockout.</summary>
        public const string Off = "Off";
    }

    /// <summary>ScanList.CSV priority spellings (CPS 1.22e, checked 2026-10-08, HANDOFF 4).</summary>
    public static class ScanPriorityValues
    {
        public const string Off = "Off";
        public const string Select1 = "Priority Channel Select1";
        public const string Select2 = "Priority Channel Select2";
        public const string Both = "Priority Channel Select1 + Priority Channel Select2";
        /// <summary>Priority Channel 1/2 = the channel the scan started on.</summary>
        public const string CurrentChannel = "Current Channel";
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
        /// <summary>
        /// "2022-07-19": BrandMeister hadn't heard this repeater for over a year when it was added (<see cref="RepeaterHealth"/>).
        /// Null when it was on the air, on other networks or typed in. The Validator warns; the editor can clear it.
        /// </summary>
        [DataMember(Order = 26, EmitDefaultValue = false)] public string OffAirSince { get; set; }
        /// <summary>
        /// Short id ("R12") that zone members (<see cref="ZoneMember"/>) use to point at this repeater. Null until something
        /// refers to it (<see cref="Project.EnsureRepeaterId"/>), so older files don't change.
        /// </summary>
        [DataMember(Order = 27, EmitDefaultValue = false)] public string Id { get; set; }

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

        /// <summary>The TX permit a <see cref="GenerationOptions.PoliteTransmit"/> codeplug gives this repeater's channels.</summary>
        public string PoliteTxPermit(bool isHotspot)
        {
            if (!IsDigital) return TxPermits.Off;
            return isHotspot || RxMHz == TxMHz ? TxPermits.Always : TxPermits.SameColorCode;
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

        /// <summary>
        /// The channel name for a talkgroup entry that has no name of its own: prefix + talkgroup name
        /// (<see cref="Naming.AutoChannelName"/>). A talkgroup this repeater carries on both slots gets the slot
        /// ("W5LOS Local TS1", "W5LOS Local TS2") so the two channels don't end up as "Local" and "Local 2".
        /// </summary>
        public string AutoChannelName(RepeaterTalkgroup e, string talkgroupName, int maxLength, bool keepLastWord = false)
        {
            if (Talkgroups.Count(x => x.TalkgroupId == e.TalkgroupId) < 2) return Naming.AutoChannelName(Prefix, talkgroupName, maxLength, keepLastWord);
            string slot = e.Slot == 2 ? "2" : "1";
            string name = Naming.AutoChannelName(Prefix, (talkgroupName ?? "") + " TS" + slot, maxLength, keepLastWord);
            // No room for "TS2" (a long ID): "W5LOS 3148422 S2".
            return name.EndsWith(" TS" + slot, StringComparison.Ordinal) ? name : Naming.AutoChannelName(Prefix, (talkgroupName ?? "") + " S" + slot, maxLength);
        }

        public Repeater Clone()
        {
            var r = (Repeater)MemberwiseClone();
            r.Talkgroups = Talkgroups.Select(t => t.Clone()).ToList();
            r.Id = null; // a copy is another repeater: zone members keep pointing at the original
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
        /// <summary><see cref="ZoneKinds"/>; null = worked out from what the zone holds (<see cref="Project.ZoneKindOf"/>).</summary>
        [DataMember(Order = 5, EmitDefaultValue = false)] public string Kind { get; set; }
        /// <summary>
        /// Channels in this zone besides the repeaters whose own zone it is: favourites, or channels a CPS zone shared with
        /// another zone. They come first, in this order, then the zone's own repeaters' channels that aren't listed. Null = none.
        /// </summary>
        [DataMember(Order = 6, EmitDefaultValue = false)] public List<ZoneMember> Members { get; set; }
        /// <summary>Talkgroup zone: every channel carrying one of these talkgroups (<see cref="ZoneKinds.Talkgroup"/>). Null = no rule.</summary>
        [DataMember(Order = 7, EmitDefaultValue = false)] public List<int> RuleTalkgroups { get; set; }
        /// <summary>Talkgroup zone: only repeaters whose own zone is one of these. Null = every repeater.</summary>
        [DataMember(Order = 8, EmitDefaultValue = false)] public List<string> RuleZones { get; set; }
        /// <summary>Talkgroup zone: only repeaters within this many miles of <see cref="Project.Home"/> (0 = no limit).</summary>
        [DataMember(Order = 9, EmitDefaultValue = false)] public double RuleMiles { get; set; }

        public ZoneInfo() { Name = ""; }
        public ZoneInfo(string name) { Name = name; }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Name = ""; }

        public bool HasTalkgroups => Talkgroups != null && Talkgroups.Count > 0;
        public bool HasMembers => Members != null && Members.Count > 0;
        public bool HasRule => RuleTalkgroups != null && RuleTalkgroups.Count > 0;

        /// <summary>A zone that stays even when no repeater has it as its own zone.</summary>
        public bool IsView => HasMembers || HasRule || Kind == ZoneKinds.Favorites || Kind == ZoneKinds.Talkgroup;

        public ZoneTalkgroup FindTalkgroup(int id)
        {
            return Talkgroups?.FirstOrDefault(t => t.TalkgroupId == id);
        }
    }

    /// <summary>What a zone is for. Every kind can also hold repeaters that have it as their own zone.</summary>
    public static class ZoneKinds
    {
        /// <summary>Repeaters of a place (county, city, band...): the zone named on each repeater.</summary>
        public const string Area = "Area";
        /// <summary>Hand-picked channels from anywhere (<see cref="ZoneInfo.Members"/>).</summary>
        public const string Favorites = "Favorites";
        /// <summary>A rule: one or more talkgroups on the repeaters of some zones (<see cref="ZoneInfo.RuleTalkgroups"/>).</summary>
        public const string Talkgroup = "Talkgroup";
        /// <summary>Simplex, weather, APRS, satellites.</summary>
        public const string Utility = "Utility";
        public static readonly string[] Values = { Area, Favorites, Talkgroup, Utility };
    }

    /// <summary>
    /// One channel in a zone's <see cref="ZoneInfo.Members"/>: a repeater (by <see cref="Repeater.Id"/>) and, for DMR, the
    /// talkgroup and slot. Names aren't used because automatic channel names change with prefixes and talkgroup names.
    /// </summary>
    [DataContract(Namespace = "")]
    public sealed class ZoneMember
    {
        [DataMember(Order = 1)] public string Repeater { get; set; }
        /// <summary>0 for an analog channel.</summary>
        [DataMember(Order = 2, EmitDefaultValue = false)] public int TalkgroupId { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public int Slot { get; set; }
        /// <summary>Which one, when the repeater has the same talkgroup on the same slot more than once (0 = the first).</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public int Nth { get; set; }

        public ZoneMember() { }
        public ZoneMember(string repeater, int talkgroupId, int slot) { Repeater = repeater; TalkgroupId = talkgroupId; Slot = slot; }
    }

    /// <summary>One channel of the project: an analog repeater (<see cref="Entry"/> null) or one talkgroup entry of a DMR repeater.</summary>
    public struct ChannelRef : IEquatable<ChannelRef>
    {
        public readonly Repeater Repeater;
        public readonly RepeaterTalkgroup Entry;
        public ChannelRef(Repeater r, RepeaterTalkgroup e) { Repeater = r; Entry = e; }
        /// <summary>The object that stands for the channel: the entry, or the analog repeater.</summary>
        public object Key => (object)Entry ?? Repeater;
        public bool Equals(ChannelRef o) { return ReferenceEquals(Key, o.Key); }
        public override bool Equals(object o) { return o is ChannelRef c && Equals(c); }
        public override int GetHashCode() { return Key == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Key); }
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
        /// <summary>
        /// Polite transmit (Channel.CSV TX permit): DMR repeater channels "Same Color Code", so the radio won't key over a busy
        /// slot of that repeater; the hotspot and DMR simplex "Always" (a hotspot on Channel Free stalls); analog "Off". When off,
        /// channels keep the template row's value (Always for DMR, Off for analog), as before 1.4. On for new projects, off for
        /// projects saved before 1.4 (it isn't in their file) and for CPS imports that weren't polite already, so their output
        /// doesn't change.
        /// </summary>
        [DataMember(Order = 13, EmitDefaultValue = false)] public bool PoliteTransmit { get; set; }
        /// <summary>
        /// Caller names (DigitalContactList.CSV) from RadioID.net: <see cref="CallerScopes"/>; null/empty = off (the default:
        /// importing the file replaces the radio's list). Attached on Export by the App (<see cref="CallerDatabase"/>).
        /// </summary>
        [DataMember(Order = 14, EmitDefaultValue = false)] public string CallerScope { get; set; }
        /// <summary>Countries or US states for <see cref="CallerScope"/> Countries / UsStates.</summary>
        [DataMember(Order = 15, EmitDefaultValue = false)] public List<string> CallerAreas { get; set; }
        /// <summary>
        /// A Favorites zone's scan list keeps watching the home channel (Priority Channel Select1 = the hotspot, else the member nearest
        /// home) and is each member's second scan list. Needs <see cref="ScanListPerZone"/>. On for new projects, off for older files.
        /// </summary>
        [DataMember(Order = 16, EmitDefaultValue = false)] public bool FavoritesScanPriority { get; set; }
        /// <summary>A "Local FM" scan list of the analog repeaters within <see cref="LocalAnalogMiles"/> of home, nearest first. Needs a home and <see cref="ScanListPerZone"/>.</summary>
        [DataMember(Order = 17, EmitDefaultValue = false)] public bool LocalAnalogScanList { get; set; }
        /// <summary>Radius of the Local FM list, miles (0 = <see cref="DefaultLocalAnalogMiles"/>).</summary>
        [DataMember(Order = 18, EmitDefaultValue = false)] public int LocalAnalogMiles { get; set; }

        public const int DefaultLocalAnalogMiles = 50;

        public GenerationOptions()
        {
            Init();
            // New projects only: Init (also run when a file loads) leaves these off, so older projects keep their output.
            PoliteTransmit = true;
            FavoritesScanPriority = true;
            LocalAnalogScanList = true;
        }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Init(); }

        void Init()
        {
            RxGroupListPerRepeater = true;
            ScanListPerZone = true;   // verified in CPS 1.22e; CpsImporter turns it off for imported codeplugs
            WriteRadioIdList = true;
            MaxNameLength = 16;
            MaxChannels = 4000;
            MaxZones = 250;
            MaxZoneChannels = 250;
            MaxRxGroupMembers = 64;
            MaxScanListChannels = 50;
        }

        /// <summary>
        /// Puts limits that are unset or bigger than the radio holds back to the radio's own (DMR-6X2 PRO: 16-character
        /// names, 4000 channels, 250 zones of 250, 64 talkgroups per RX group list, 50 channels per scan list).
        /// Version 1.3 and earlier let the Settings tab go up to 128 talkgroups per RX group list.
        /// </summary>
        public void KeepWithinRadioLimits()
        {
            int Fix(int value, int max) { return value <= 0 || value > max ? max : value; }
            MaxNameLength = Fix(MaxNameLength, 16);
            MaxChannels = Fix(MaxChannels, 4000);
            MaxZones = Fix(MaxZones, 250);
            MaxZoneChannels = Fix(MaxZoneChannels, 250);
            MaxRxGroupMembers = Fix(MaxRxGroupMembers, 64);
            MaxScanListChannels = Fix(MaxScanListChannels, 50);
        }
    }

    [DataContract(Namespace = "")]
    public sealed class Project
    {
        public const int CurrentFileVersion = 2;

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
        /// <summary>APRS identity for APRS.CSV (<see cref="Core.Aprs"/>); null = APRS.CSV isn't written and the CPS's APRS settings stay.</summary>
        [DataMember(Order = 13, EmitDefaultValue = false)] public AprsPlan Aprs { get; set; }
        /// <summary>Where the user lives (distances, zone order, nearest-first talkgroup zones). Null = not set; nothing changes.</summary>
        [DataMember(Order = 14, EmitDefaultValue = false)] public HomeLocation Home { get; set; }
        /// <summary>"2026-10-08": when Check for updates last ran on this project (<see cref="UpdateCheck"/>); null = never.</summary>
        [DataMember(Order = 15, EmitDefaultValue = false)] public string LastUpdateCheck { get; set; }

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
            Options.KeepWithinRadioLimits();
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
            foreach (var z in Zones)
            {
                if (z.Kind != null && !ZoneKinds.Values.Contains(z.Kind)) z.Kind = null;
                z.Members?.RemoveAll(m => m == null || string.IsNullOrEmpty(m.Repeater));
                z.RuleTalkgroups?.RemoveAll(id => id <= 0);
                z.RuleZones?.RemoveAll(string.IsNullOrWhiteSpace);
                if (z.RuleTalkgroups?.Count == 0) z.RuleTalkgroups = null;
                if (z.RuleZones?.Count == 0) z.RuleZones = null;
            }
            SyncZones();
            if (FileVersion < 2) KeepOldChannelNames();
            Presets.SortNoaaWeather(this);
            FileVersion = CurrentFileVersion;
        }

        /// <summary>
        /// Version 1.3 shortens long channel names instead of cutting them (<see cref="Naming.Fit"/>). Projects saved
        /// before that keep the names they had, so channels already in the radio and the CPS aren't renamed: a
        /// channel whose automatic name would change gets its old name as its own.
        /// </summary>
        void KeepOldChannelNames()
        {
            int max = Options.MaxNameLength > 0 ? Options.MaxNameLength : 16;
            foreach (var r in AllRepeaters())
            {
                if (!r.IsDigital)
                {
                    string cut = Naming.Clean(r.Name, max);
                    if (Naming.Fit(r.Name, max) != cut) r.Name = cut;
                    continue;
                }
                foreach (var e in r.Talkgroups)
                {
                    var tg = FindTalkgroup(e.TalkgroupId);
                    if (tg == null || !string.IsNullOrWhiteSpace(e.ChannelName)) continue;
                    string old = Naming.LegacyAutoChannelName(r.Prefix, tg.Name, max);
                    if (old != r.AutoChannelName(e, tg.Name, max)) e.ChannelName = old;
                }
            }
        }

        /// <summary>Kilometres from <see cref="Home"/> to a repeater; null when either has no position.</summary>
        public double? DistanceKm(Repeater r)
        {
            if (Home == null || r?.Latitude == null || r.Longitude == null) return null;
            return Distances.Km(Home.Latitude, Home.Longitude, r.Latitude.Value, r.Longitude.Value);
        }

        /// <summary>"12 mi NE" from home, or "" when either has no position.</summary>
        public string DistanceText(Repeater r)
        {
            double? km = DistanceKm(r);
            if (km == null) return "";
            return Distances.Describe(km.Value, Distances.Bearing(Home.Latitude, Home.Longitude, r.Latitude.Value, r.Longitude.Value), Home.UsesMiles);
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
        /// (or the hotspot) uses, keeping the existing order, and drops zones nothing uses any more. Favorites and
        /// talkgroup zones (<see cref="ZoneInfo.IsView"/>) stay; their members that point at deleted channels go.
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
            PruneZoneMembers();
            Zones.RemoveAll(z => !used.Contains((z.Name ?? "").Trim()) && !(z.IsView && (z.Name ?? "").Trim().Length > 0));
        }

        /// <summary>Renames a zone everywhere it is used.</summary>
        public void RenameZone(string oldName, string newName)
        {
            newName = (newName ?? "").Trim();
            foreach (var r in AllRepeaters())
                if (SameZone(r.Zone, oldName)) r.Zone = newName;
            foreach (var z in Zones)
                if (z.RuleZones != null)
                    z.RuleZones = z.RuleZones.Select(n => SameZone(n, oldName) ? newName : n).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = FindZone(newName);
            var info = FindZone(oldName);
            if (info != null && existing != null && existing != info)
            {
                // Merging: the combined zone keeps both talkgroup sets, both member lists and both rules.
                foreach (var t in info.Talkgroups ?? new List<ZoneTalkgroup>())
                    if (existing.FindTalkgroup(t.TalkgroupId) == null)
                        (existing.Talkgroups ?? (existing.Talkgroups = new List<ZoneTalkgroup>())).Add(new ZoneTalkgroup(t.TalkgroupId, t.Slot));
                foreach (var m in info.Members ?? new List<ZoneMember>())
                    if (!(existing.Members ?? new List<ZoneMember>()).Any(x => SameMember(x, m)))
                        (existing.Members ?? (existing.Members = new List<ZoneMember>())).Add(m);
                foreach (int id in info.RuleTalkgroups ?? new List<int>())
                    if (!(existing.RuleTalkgroups ?? new List<int>()).Contains(id))
                        (existing.RuleTalkgroups ?? (existing.RuleTalkgroups = new List<int>())).Add(id);
                if (existing.Kind == null) existing.Kind = info.Kind;
                Zones.Remove(info);
                foreach (var r in ZoneRepeaters(newName)) ApplyZoneTalkgroups(r);
            }
            else if (info != null) info.Name = newName;
            SyncZones();
        }

        // ------------------------------------------------------------------
        // Zones as views: favourites, talkgroup zones, a channel in several zones
        // ------------------------------------------------------------------

        /// <summary>What a zone is for: its stored <see cref="ZoneInfo.Kind"/>, or worked out from what it holds.</summary>
        public string ZoneKindOf(ZoneInfo z)
        {
            if (z == null) return ZoneKinds.Area;
            if (z.Kind != null) return z.Kind;
            if (z.HasRule) return ZoneKinds.Talkgroup;
            var own = AllRepeaters().Where(r => (r != Hotspot || HotspotEnabled) && SameZone(r.Zone, z.Name)).ToList();
            if (own.Count == 0) return z.HasMembers ? ZoneKinds.Favorites : ZoneKinds.Area;
            return own.All(Presets.IsPreset) ? ZoneKinds.Utility : ZoneKinds.Area;
        }

        /// <summary>Gives a repeater an <see cref="Repeater.Id"/> if it has none ("R1", "R2"...), so zone members can point at it.</summary>
        public string EnsureRepeaterId(Repeater r)
        {
            if (!string.IsNullOrEmpty(r.Id) && AllRepeaters().Count(x => x.Id == r.Id) == 1) return r.Id;
            int n = 1;
            var taken = new HashSet<string>(AllRepeaters().Where(x => x != r).Select(x => x.Id).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
            while (taken.Contains("R" + n.ToString(CultureInfo.InvariantCulture))) n++;
            return r.Id = "R" + n.ToString(CultureInfo.InvariantCulture);
        }

        public Repeater FindRepeaterById(string id)
        {
            return string.IsNullOrEmpty(id) ? null : AllRepeaters().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        static bool SameMember(ZoneMember a, ZoneMember b)
        {
            return string.Equals(a.Repeater, b.Repeater, StringComparison.OrdinalIgnoreCase) && a.TalkgroupId == b.TalkgroupId && a.Slot == b.Slot && a.Nth == b.Nth;
        }

        /// <summary>The zone member that stands for a channel (gives the repeater an id).</summary>
        public ZoneMember MemberFor(ChannelRef c)
        {
            string id = EnsureRepeaterId(c.Repeater);
            if (c.Entry == null) return new ZoneMember(id, 0, 0);
            var e = c.Entry;
            int nth = c.Repeater.Talkgroups.Where(x => x.TalkgroupId == e.TalkgroupId && x.Slot == e.Slot).ToList().IndexOf(e);
            return new ZoneMember(id, e.TalkgroupId, e.Slot == 2 ? 2 : 1) { Nth = Math.Max(0, nth) };
        }

        /// <summary>The channel a zone member points at: same repeater, talkgroup and slot, else the talkgroup on the other slot. Null when gone.</summary>
        public ChannelRef? Resolve(ZoneMember m)
        {
            var r = FindRepeaterById(m?.Repeater);
            if (r == null) return null;
            if (!r.IsDigital) return m.TalkgroupId == 0 ? new ChannelRef(r, null) : (ChannelRef?)null;
            var same = r.Talkgroups.Where(x => x.TalkgroupId == m.TalkgroupId && x.Slot == m.Slot).ToList();
            var e = (m.Nth >= 0 && m.Nth < same.Count ? same[m.Nth] : same.FirstOrDefault()) ?? r.Talkgroups.FirstOrDefault(x => x.TalkgroupId == m.TalkgroupId);
            return e == null ? (ChannelRef?)null : new ChannelRef(r, e);
        }

        /// <summary>Drops zone members whose repeater or talkgroup entry is gone.</summary>
        public void PruneZoneMembers()
        {
            foreach (var z in Zones)
            {
                if (z.Members == null) continue;
                z.Members.RemoveAll(m => Resolve(m) == null);
                if (z.Members.Count == 0) z.Members = null;
            }
        }

        /// <summary>The channels of one repeater that go into the codeplug, in output order (one for analog).</summary>
        public static IEnumerable<ChannelRef> ChannelsOf(Repeater r)
        {
            if (!r.IsDigital) { yield return new ChannelRef(r, null); yield break; }
            foreach (var e in r.Talkgroups) yield return new ChannelRef(r, e);
        }

        /// <summary>
        /// The channels a zone holds, in zone order: its <see cref="ZoneInfo.Members"/> first, then the channels of the repeaters
        /// whose own zone it is, then (talkgroup zones) every channel matching the rule. Each channel once; only repeaters that
        /// go into the codeplug. A zone with no members and no rule is exactly its repeaters' channels, as before 1.5.
        /// </summary>
        public List<ChannelRef> ZoneChannels(string zone)
        {
            var info = FindZone(zone);
            var active = new HashSet<Repeater>(ActiveRepeaters());
            var list = new List<ChannelRef>();
            var seen = new HashSet<ChannelRef>();
            void Add(ChannelRef c) { if (active.Contains(c.Repeater) && seen.Add(c)) list.Add(c); }
            foreach (var m in info?.Members ?? new List<ZoneMember>())
            {
                var c = Resolve(m);
                if (c != null) Add(c.Value);
            }
            foreach (var r in ActiveRepeaters().Where(r => SameZone(r.Zone, zone)))
                foreach (var c in ChannelsOf(r)) Add(c);
            if (info != null && info.HasRule)
            {
                // Nearest repeaters first when there is a home (unplaced ones after, in list order); a radius leaves out
                // repeaters farther away and those with no position.
                var ids = new HashSet<int>(info.RuleTalkgroups);
                var reps = ActiveRepeaters().Where(r => r.IsDigital && (info.RuleZones == null || info.RuleZones.Any(n => SameZone(n, r.Zone))))
                                            .Select((r, i) => new { r, i, d = DistanceKm(r) }).ToList();
                if (info.RuleMiles > 0 && Home != null) reps = reps.Where(x => x.d != null && x.d <= info.RuleMiles * Distances.KmPerMile).ToList();
                foreach (var x in reps.OrderBy(x => x.d == null ? 1 : 0).ThenBy(x => x.d ?? 0).ThenBy(x => x.i))
                    foreach (var e in x.r.Talkgroups.Where(e => ids.Contains(e.TalkgroupId))) Add(new ChannelRef(x.r, e));
            }
            return list;
        }

        /// <summary>
        /// Puts a channel into a zone (made as a Favorites zone if it doesn't exist). A channel already in the zone stays
        /// where it is. Returns false when it was there already.
        /// </summary>
        public bool AddToZone(string zone, ChannelRef c)
        {
            zone = (zone ?? "").Trim();
            if (zone.Length == 0 || c.Repeater == null) return false;
            var info = FindZone(zone);
            if (info == null) Zones.Add(info = new ZoneInfo(zone) { Kind = ZoneKinds.Favorites });
            if (ZoneChannels(info.Name).Contains(c)) return false;
            (info.Members ?? (info.Members = new List<ZoneMember>())).Add(MemberFor(c));
            return true;
        }

        /// <summary>
        /// Takes a listed channel out of a zone's <see cref="ZoneInfo.Members"/>. A channel that is there because its repeater's
        /// zone is this one (or a talkgroup rule) stays: change the repeater's zone or the rule instead. Returns true when removed.
        /// </summary>
        public bool RemoveFromZone(string zone, ChannelRef c)
        {
            var info = FindZone(zone);
            if (info?.Members == null) return false;
            int n = info.Members.RemoveAll(m => { var x = Resolve(m); return x != null && x.Value.Equals(c); });
            if (info.Members.Count == 0) info.Members = null;
            return n > 0;
        }

        /// <summary>Moves a listed member up or down within the zone's <see cref="ZoneInfo.Members"/>. Returns true when it moved.</summary>
        public bool MoveZoneMember(string zone, ChannelRef c, int delta)
        {
            var info = FindZone(zone);
            if (info?.Members == null) return false;
            int i = info.Members.FindIndex(m => { var x = Resolve(m); return x != null && x.Value.Equals(c); });
            int j = i + delta;
            if (i < 0 || j < 0 || j >= info.Members.Count) return false;
            var m0 = info.Members[i];
            info.Members.RemoveAt(i);
            info.Members.Insert(j, m0);
            return true;
        }

        /// <summary>Zones other than the repeater's own that hold this channel.</summary>
        public List<string> OtherZonesOf(ChannelRef c)
        {
            return Zones.Where(z => !SameZone(z.Name, c.Repeater.Zone) && (z.IsView) && ZoneChannels(z.Name).Contains(c)).Select(z => z.Name).ToList();
        }

        // ------------------------------------------------------------------
        // Zone talkgroup sets
        // ------------------------------------------------------------------

        /// <summary>The DMR repeaters (and the hotspot, when it's switched on) whose zone is <paramref name="zone"/>.</summary>
        public List<Repeater> ZoneRepeaters(string zone)
        {
            return AllRepeaters().Where(r => r.IsDigital && (r != Hotspot || HotspotEnabled) && SameZone(r.Zone, zone)).ToList();
        }

        /// <summary>
        /// True when the zone has DMR repeaters that network talkgroups make sense on: not a zone of only simplex preset
        /// channels (<see cref="Presets.AddSimplex"/>), which "copy to all zones" and the wizard's talkgroup step leave alone.
        /// </summary>
        public bool TakesZoneTalkgroups(string zone)
        {
            return ZoneRepeaters(zone).Any(r => !Presets.IsSimplex(r));
        }

        /// <summary>Channels a zone will hold: one per talkgroup on its included DMR repeaters, one per analog channel, plus its listed members and rule.</summary>
        public int ZoneChannelCount(string zone)
        {
            return ZoneChannels(zone).Count;
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

        /// <summary>Zone names that at least one repeater (or the enabled hotspot) uses, and favourites/talkgroup zones that hold channels.</summary>
        public HashSet<string> UsedZoneNames()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ActiveRepeaters())
            {
                string z = (r.Zone ?? "").Trim();
                if (z.Length > 0) used.Add(z);
            }
            foreach (var z in Zones)
                if (z.IsView && !used.Contains(z.Name.Trim()) && ZoneChannels(z.Name).Count > 0) used.Add(z.Name.Trim());
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
            {
                foreach (var t in z.Talkgroups ?? new List<ZoneTalkgroup>())
                    if (t.TalkgroupId == oldId) t.TalkgroupId = newId;
                foreach (var m in z.Members ?? new List<ZoneMember>())
                    if (m.TalkgroupId == oldId) m.TalkgroupId = newId;
                if (z.RuleTalkgroups != null) z.RuleTalkgroups = z.RuleTalkgroups.Select(id => id == oldId ? newId : id).Distinct().ToList();
            }
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
            foreach (var z in Zones)
            {
                z.RuleTalkgroups?.RemoveAll(set.Contains);
                if (z.RuleTalkgroups?.Count == 0) z.RuleTalkgroups = null;
            }
            PruneZoneMembers();
            foreach (var t in doomed) Talkgroups.Remove(t);
        }
    }
}
