using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    public enum UpdateKind
    {
        /// <summary>RadioID.net lists other frequencies.</summary>
        Frequency,
        ColorCode,
        /// <summary>The listing (or BrandMeister's static list) has talkgroups the repeater doesn't.</summary>
        TalkgroupsAdded,
        /// <summary>The repeater has talkgroups the listing no longer has (the user may have added them on purpose: not ticked).</summary>
        TalkgroupsDropped,
        /// <summary>BrandMeister hasn't heard it for over a year.</summary>
        OffAir,
        /// <summary>Marked off the air, but BrandMeister has heard it since.</summary>
        BackOnAir,
        /// <summary>RadioID.net no longer lists its ID.</summary>
        Delisted,
        /// <summary>A repeater in the project's areas that the project doesn't have.</summary>
        NewRepeater,
    }

    /// <summary>One difference "Check for updates" found, shown with a tick box (<see cref="Ticked"/> = the suggestion).</summary>
    public sealed class UpdateItem
    {
        public UpdateKind Kind { get; set; }
        /// <summary>The project repeater (null for <see cref="UpdateKind.NewRepeater"/>).</summary>
        public Repeater Repeater { get; set; }
        /// <summary>The current listing (RadioID.net, with BrandMeister's talkgroups when those are used).</summary>
        public OnlineRepeater Listing { get; set; }
        public List<OnlineTalkgroup> Talkgroups { get; set; } = new List<OnlineTalkgroup>();
        public List<RepeaterTalkgroup> Dropped { get; set; } = new List<RepeaterTalkgroup>();
        public DateTime? LastSeen { get; set; }
        /// <summary>"KC5EZZ San Angelo: 444.125 → 444.150 MHz".</summary>
        public string Text { get; set; }
        public bool Ticked { get; set; }
        /// <summary>Zone a new repeater goes into (the zone of a project repeater in the same county or state).</summary>
        public string Zone { get; set; }
    }

    /// <summary>
    /// "Check for updates" (roadmap item 12): compares the project's repeaters that came from RadioID.net (<see cref="Repeater.SourceId"/>)
    /// with their current listings and BrandMeister, and finds new repeaters in the project's counties. Pure: the App downloads
    /// (Online.CheckForUpdates) and applies the ticked items (<see cref="Apply"/>).
    /// </summary>
    public static class UpdateCheck
    {
        /// <summary>The DMR repeaters to look up: those added from RadioID.net (their listing ID).</summary>
        public static List<Repeater> Tracked(Project p)
        {
            return p.Repeaters.Where(r => r.IsDigital && r.SourceId > 0).ToList();
        }

        /// <summary>US states (by name) or other countries the tracked repeaters are in, to download whole (RadioID.net lists by state or country).</summary>
        public static List<string> Areas(Project p)
        {
            return p.Repeaters.Where(r => r.SourceId > 0)
                              .Select(r => r.Country == "United States" || (r.AreaCode ?? "").StartsWith("US", StringComparison.Ordinal) ? r.State : r.Country)
                              .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The differences. <paramref name="listings"/>: current RadioID.net listings by DMR ID (an ID mapped to null = asked and not
        /// listed any more; an ID missing = couldn't ask). <paramref name="lastSeen"/> and <paramref name="statics"/>: BrandMeister answers
        /// by device ID. <paramref name="area"/>: every listing in the project's states (new repeaters are looked for there).
        /// </summary>
        public static List<UpdateItem> Compare(Project p, IDictionary<int, OnlineRepeater> listings, IDictionary<int, DateTime?> lastSeen,
                                               IDictionary<int, List<OnlineTalkgroup>> statics, IEnumerable<OnlineRepeater> area,
                                               IDictionary<int, string> bmNames, DateTime now)
        {
            var items = new List<UpdateItem>();
            string Mhz(decimal v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
            foreach (var r in Tracked(p))
            {
                string label = string.IsNullOrWhiteSpace(r.Name) ? r.Prefix : r.Name;
                if (!listings.TryGetValue(r.SourceId, out var l)) continue;
                if (l == null)
                {
                    items.Add(new UpdateItem { Kind = UpdateKind.Delisted, Repeater = r, Text = label + ": RadioID.net no longer lists repeater ID " + r.SourceId + " (switch it off if it's gone)" });
                    continue;
                }
                if (l.InRadioBand && (l.RxMHz != r.RxMHz || l.TxMHz != r.TxMHz))
                    items.Add(new UpdateItem { Kind = UpdateKind.Frequency, Repeater = r, Listing = l, Ticked = true,
                                               Text = label + ": " + Mhz(r.RxMHz) + " / " + Mhz(r.TxMHz) + " → " + Mhz(l.RxMHz) + " / " + Mhz(l.TxMHz) + " MHz (output / input)" });
                if (l.ColorCode > 0 && l.ColorCode != r.ColorCode)
                    items.Add(new UpdateItem { Kind = UpdateKind.ColorCode, Repeater = r, Listing = l, Ticked = true, Text = label + ": color code " + r.ColorCode + " → " + l.ColorCode });

                int device = l.BrandMeisterDeviceId;
                DateTime? seen = null;
                bool known = device > 0 && lastSeen != null && lastSeen.TryGetValue(device, out seen) && seen.HasValue;
                if (known && seen.Value < now.AddDays(-RepeaterHealth.OffAirDays) && string.IsNullOrWhiteSpace(r.OffAirSince))
                    items.Add(new UpdateItem { Kind = UpdateKind.OffAir, Repeater = r, Listing = l, LastSeen = seen, Ticked = true,
                                               Text = label + ": BrandMeister last heard it " + RepeaterHealth.DateText(seen.Value) });
                else if (known && seen.Value >= now.AddDays(-RepeaterHealth.OffAirDays) && !string.IsNullOrWhiteSpace(r.OffAirSince))
                    items.Add(new UpdateItem { Kind = UpdateKind.BackOnAir, Repeater = r, Listing = l, LastSeen = seen, Ticked = true,
                                               Text = label + ": back on the air (BrandMeister heard it " + RepeaterHealth.DateText(seen.Value) + ")" });

                // Talkgroups: BrandMeister's static ones for BrandMeister-only repeaters when it has some, else what the owner lists.
                var source = l.Talkgroups;
                string from = RadioId.Site;
                if (device > 0 && statics != null && statics.TryGetValue(device, out var st) && st.Count > 0 && l.OnlyBrandMeister)
                {
                    var copy = new OnlineRepeater { Network = l.Network, Talkgroups = l.Talkgroups.ToList(), DmrId = l.DmrId };
                    if (RepeaterHealth.UseStaticTalkgroups(copy, st)) { source = copy.Talkgroups; from = "BrandMeister"; }
                }
                if (source.Count == 0) continue; // nothing listed: nothing to compare
                var own = r.Talkgroups.Where(e => !e.FromZone).ToList();
                var added = source.Where(t => !r.Talkgroups.Any(e => e.TalkgroupId == t.Id)).GroupBy(t => t.Id).Select(g => g.First()).ToList();
                if (added.Count > 0)
                    items.Add(new UpdateItem { Kind = UpdateKind.TalkgroupsAdded, Repeater = r, Listing = l, Talkgroups = added, Ticked = true,
                                               Text = label + ": " + from + " lists " + string.Join(", ", added.Select(t => Describe(p, l, t, bmNames))) });
                var dropped = own.Where(e => !source.Any(t => t.Id == e.TalkgroupId) && e.TalkgroupId != r.SourceId && !IsPrivate(p, e.TalkgroupId)).ToList();
                if (dropped.Count > 0)
                    items.Add(new UpdateItem { Kind = UpdateKind.TalkgroupsDropped, Repeater = r, Listing = l, Dropped = dropped,
                                               Text = label + ": not listed any more on " + from + ": " + string.Join(", ", dropped.Select(e => (p.FindTalkgroup(e.TalkgroupId)?.Name ?? "TG") + " (" + e.TalkgroupId + ")")) });
            }

            // New repeaters in the counties (or, abroad, the states) the project's repeaters are in.
            var places = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // area code → zone
            foreach (var r in p.Repeaters.Where(r => !string.IsNullOrEmpty(r.AreaCode) && !string.IsNullOrWhiteSpace(r.Zone) && !Presets.IsPreset(r)))
                if (!places.ContainsKey(r.AreaCode)) places[r.AreaCode] = r.Zone;
            var have = new HashSet<int>(p.Repeaters.Select(r => r.SourceId).Where(id => id > 0));
            foreach (var l in area ?? Enumerable.Empty<OnlineRepeater>())
            {
                if (l.IsAnalog || !l.InRadioBand || l.DmrId <= 0 || have.Contains(l.DmrId)) continue;
                string code = l.Location?.County?.Code ?? l.Location?.State?.Code;
                if (code == null || !places.TryGetValue(code, out string zone)) continue;
                if (OnlineImporter.FindExisting(p, l) != null) continue;
                int device = l.BrandMeisterDeviceId;
                if (device > 0 && lastSeen != null && lastSeen.TryGetValue(device, out var seen) && seen.HasValue && seen.Value < now.AddDays(-RepeaterHealth.OffAirDays)) continue;
                have.Add(l.DmrId);
                items.Add(new UpdateItem { Kind = UpdateKind.NewRepeater, Listing = l, Zone = zone,
                                           Text = "New: " + l.Callsign + " " + l.City + " " + Mhz(l.RxMHz) + " CC" + l.ColorCode + (l.Network.Length > 0 ? " (" + l.Network + ")" : "") + " → zone " + zone });
            }
            return items;
        }

        static bool IsPrivate(Project p, int id) { var t = p.FindTalkgroup(id); return t != null && !t.IsGroupCall; }

        static string Describe(Project p, OnlineRepeater l, OnlineTalkgroup t, IDictionary<int, string> bmNames)
        {
            string name = p.FindTalkgroup(t.Id)?.Name ?? OnlineImporter.TalkgroupName(l, t, bmNames);
            return name + " (" + t.Id.ToString(CultureInfo.InvariantCulture) + ", TS" + (t.Slot == 2 ? "2" : "1") + ")";
        }

        /// <summary>Applies the ticked items. Returns notes (what changed); new talkgroups get names like an online import.</summary>
        public static List<string> Apply(Project p, IEnumerable<UpdateItem> ticked, IDictionary<int, string> bmNames, string power = "High")
        {
            var notes = new List<string>();
            var result = new OnlineImportResult();
            var newOnes = new List<UpdateItem>();
            foreach (var it in ticked)
            {
                var r = it.Repeater;
                switch (it.Kind)
                {
                    case UpdateKind.Frequency: r.RxMHz = it.Listing.RxMHz; r.TxMHz = it.Listing.TxMHz; break;
                    case UpdateKind.ColorCode: r.ColorCode = it.Listing.ColorCode; break;
                    case UpdateKind.OffAir: r.OffAirSince = RepeaterHealth.DateText(it.LastSeen.Value); break;
                    case UpdateKind.BackOnAir: r.OffAirSince = null; break;
                    case UpdateKind.Delisted: r.Enabled = false; break;
                    case UpdateKind.TalkgroupsAdded:
                        foreach (var t in it.Talkgroups)
                        {
                            string name = OnlineImporter.TalkgroupName(it.Listing, t, bmNames);
                            var tg = OnlineImporter.Ensure(p, t.Id, name, BrandMeister.IsPrivateCall(t.Id, name) ? CallTypes.Private : CallTypes.Group, result);
                            if (!r.Talkgroups.Any(e => e.TalkgroupId == tg.Id)) r.Talkgroups.Add(new RepeaterTalkgroup(tg.Id, t.Slot == 2 ? 2 : 1));
                        }
                        break;
                    case UpdateKind.TalkgroupsDropped:
                        foreach (var e in it.Dropped) r.Talkgroups.Remove(e);
                        break;
                    case UpdateKind.NewRepeater: newOnes.Add(it); break;
                }
                if (it.Kind != UpdateKind.NewRepeater) notes.Add(it.Text);
            }
            foreach (var group in newOnes.GroupBy(i => i.Zone, StringComparer.OrdinalIgnoreCase))
            {
                var added = OnlineImporter.AddRepeaters(p, group.Select(i => i.Listing), new OnlineImportOptions { ZonePerCity = false, Zone = group.Key, Power = power }, bmNames);
                foreach (var r in added.Added) notes.Add("Added " + r.Name + " to zone " + r.Zone + " (" + (r.IsDigital ? r.Talkgroups.Count + " talkgroups" : "analog") + ")");
                notes.AddRange(added.Notes);
                result.NewTalkgroups.AddRange(added.NewTalkgroups);
            }
            if (result.NewTalkgroups.Count > 0)
                notes.Add("New talkgroups: " + string.Join(", ", result.NewTalkgroups.Select(t => t.Name + " (" + t.Id + ")").Distinct()));
            p.SyncZones();
            return notes;
        }
    }
}
