using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>BrandMeister's device list (<see cref="BrandMeister.ParseDevices"/>): what's on the network now.</summary>
    public sealed class BrandMeisterDevices
    {
        /// <summary>Every device ID in the list (heard in the last day or so), hotspots included.</summary>
        public HashSet<int> Ids { get; } = new HashSet<int>();
        /// <summary>Every repeater (6-digit ID with frequencies), placed or not: callsign, output, <see cref="OnlineRepeater.BrandMeisterId"/>.</summary>
        public List<OnlineRepeater> Live { get; } = new List<OnlineRepeater>();
        /// <summary>The live repeaters the map could place, offered alongside RadioID.net's listings.</summary>
        public List<OnlineRepeater> Repeaters { get; } = new List<OnlineRepeater>();
    }

    /// <summary>
    /// Which listed repeaters look dead, and the talkgroups BrandMeister really has on them (roadmap 1.4 item 4). Only
    /// listings on BrandMeister alone are judged: a repeater on other networks too may be busy there. Pure: the App asks
    /// BrandMeister (<see cref="BrandMeister.DeviceInfoUrl"/>, <see cref="BrandMeister.StaticTalkgroupUrl"/>).
    /// </summary>
    public static class RepeaterHealth
    {
        /// <summary>Not heard on BrandMeister for this long = off the air.</summary>
        public const int OffAirDays = 365;

        /// <summary>The talkgroup source name set by <see cref="UseStaticTalkgroups"/>.</summary>
        public const string BrandMeisterSource = "BrandMeister";

        /// <summary>
        /// Listings to ask BrandMeister about: BrandMeister-only DMR listings with a BrandMeister-style ID that aren't in the
        /// device list, and have no live twin there either (same callsign, output within 12.5 kHz: a repeater that came
        /// back under a new ID).
        /// </summary>
        public static List<OnlineRepeater> Candidates(IEnumerable<OnlineRepeater> listings, BrandMeisterDevices live)
        {
            var list = new List<OnlineRepeater>();
            if (live == null || live.Ids.Count == 0) return list; // no device list: nothing to compare with
            foreach (var r in listings)
            {
                if (r.IsAnalog || r.BrandMeisterId > 0) continue; // devices from the list itself are live
                int id = r.BrandMeisterDeviceId;
                if (id <= 0 || live.Ids.Contains(id) || BrandMeister.IsListed(live.Live, r)) continue;
                list.Add(r);
            }
            return list;
        }

        /// <summary>
        /// Marks a listing off the air when BrandMeister last heard it more than <see cref="OffAirDays"/> before
        /// <paramref name="now"/>; null (unknown ID, no answer) leaves it alone. Returns true when it's off the air.
        /// </summary>
        public static bool Apply(OnlineRepeater r, DateTime? lastSeen, DateTime now)
        {
            r.OffAirSince = lastSeen.HasValue && lastSeen.Value < now.AddDays(-OffAirDays) ? lastSeen.Value.Date : (DateTime?)null;
            return r.IsOffAir;
        }

        /// <summary>"2022-07-19": how a date is stored on a project repeater (<see cref="Repeater.OffAirSince"/>).</summary>
        public static string DateText(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        /// <summary>
        /// Replaces a BrandMeister-only listing's owner-typed talkgroups with BrandMeister's static ones, keeping the owner's
        /// descriptions (names) where the IDs match. A static talkgroup on slot 0 (unknown) takes the owner's slot for it, else
        /// the usual one (<see cref="Project.DefaultSlot"/>). An empty static list leaves the listing as it was (many
        /// repeaters have only dynamic talkgroups). Returns true when the talkgroups now come from BrandMeister.
        /// </summary>
        public static bool UseStaticTalkgroups(OnlineRepeater r, IList<OnlineTalkgroup> statics)
        {
            if (r.TalkgroupSource == BrandMeisterSource) return true; // done already (the owner's list is gone)
            if (r.IsAnalog || !r.OnlyBrandMeister || statics == null || statics.Count == 0) return false;
            var list = new List<OnlineTalkgroup>();
            foreach (var t in statics)
            {
                var listed = r.Talkgroups.Where(o => o.Id == t.Id).ToList();
                int slot = t.Slot == 1 || t.Slot == 2 ? t.Slot : listed.Count > 0 ? listed[0].Slot : Project.DefaultSlot(t.Id);
                if (list.Any(x => x.Id == t.Id && x.Slot == slot)) continue;
                string description = (listed.FirstOrDefault(o => o.Slot == slot) ?? listed.FirstOrDefault())?.Description ?? "";
                list.Add(new OnlineTalkgroup { Id = t.Id, Slot = slot, Description = description });
            }
            r.Talkgroups = list;
            r.TalkgroupSource = BrandMeisterSource;
            return true;
        }
    }
}
