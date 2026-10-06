using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeplugBuilder.Core
{
    // Online data sources. Everything here is pure parsing and project building, so it can be tested
    // offline; the App does the downloading (see App/Online.cs).
    //
    // RadioID.net  https://radioid.net/api/dmr/repeater/?state=Texas&page=2   (free, no key, 200 per page)
    //              https://radioid.net/api/dmr/user/?callsign=W6OZZ
    // BrandMeister https://api.brandmeister.network/v2/talkgroup               ({"91":"World-wide", ...})

    public sealed class OnlineTalkgroup
    {
        public int Id { get; set; }
        public int Slot { get; set; }
        public string Description { get; set; }
    }

    /// <summary>One DMR repeater as listed on RadioID.net.</summary>
    public sealed class OnlineRepeater
    {
        public string Callsign { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string Country { get; set; } = "";
        /// <summary>Cleaned-up network name, e.g. "BrandMeister" (see <see cref="Networks.Normalize"/>).</summary>
        public string Network { get; set; } = "";
        public string Status { get; set; } = "";
        public string Details { get; set; } = "";
        public string Trustee { get; set; } = "";
        /// <summary>The repeater's own DMR ID (RadioID's "locator").</summary>
        public int DmrId { get; set; }
        /// <summary>The repeater's output = what the radio receives.</summary>
        public decimal RxMHz { get; set; }
        /// <summary>The repeater's input = what the radio transmits on.</summary>
        public decimal TxMHz { get; set; }
        public int ColorCode { get; set; }
        /// <summary>Talkgroups the owner published, in listing order.</summary>
        public List<OnlineTalkgroup> Talkgroups { get; set; } = new List<OnlineTalkgroup>();
        /// <summary>Where it is on the built-in map (set by <see cref="GeoAtlas.Locate"/>; null until then).</summary>
        public GeoLocation Location { get; set; }

        public bool IsBrandMeister => Network.IndexOf("BrandMeister", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Both frequencies are inside the DMR-6X2's bands (136-174 and 400-480 MHz).</summary>
        public bool InRadioBand => Validator.InRadioBand(RxMHz) && Validator.InRadioBand(TxMHz);

        public decimal Offset => TxMHz - RxMHz;
    }

    public sealed class RadioIdPage
    {
        public int Count { get; set; }
        public int Page { get; set; }
        public int Pages { get; set; }
        public List<OnlineRepeater> Repeaters { get; set; } = new List<OnlineRepeater>();
    }

    public sealed class RadioIdUser
    {
        public int Id { get; set; }
        public string Callsign { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string Country { get; set; } = "";

        /// <summary>"Austin W6OZZ" (first name + callsign), or just the callsign when that doesn't fit.</summary>
        public string SuggestedName(int maxLength = 16)
        {
            string full = Naming.Clean(FirstName + " " + Callsign, 0);
            return full.Length <= maxLength ? full : Naming.Clean(Callsign, maxLength);
        }
    }

    public static class RadioId
    {
        public const string Site = "RadioID.net";

        public static string RepeaterUrl(string state, int page = 1)
        {
            return "https://radioid.net/api/dmr/repeater/?state=" + Uri.EscapeDataString((state ?? "").Trim()) +
                   (page > 1 ? "&page=" + page.ToString(CultureInfo.InvariantCulture) : "");
        }

        /// <summary>Every repeater in a country (checked 2026-10-06: "United States" is 26 pages, "Canada" 3).</summary>
        public static string CountryRepeaterUrl(string country, int page = 1)
        {
            return "https://radioid.net/api/dmr/repeater/?country=" + Uri.EscapeDataString((country ?? "").Trim()) +
                   (page > 1 ? "&page=" + page.ToString(CultureInfo.InvariantCulture) : "");
        }

        public static string UserUrl(string callsign)
        {
            return "https://radioid.net/api/dmr/user/?callsign=" + Uri.EscapeDataString((callsign ?? "").Trim().ToUpperInvariant());
        }

        public static RadioIdPage ParseRepeaters(string json)
        {
            var root = Json.Parse(json);
            var page = new RadioIdPage
            {
                Count = Json.Int(Json.Get(root, "count")),
                Page = Json.Int(Json.Get(root, "page"), 1),
                Pages = Json.Int(Json.Get(root, "pages"), 1),
            };
            foreach (var item in Json.Arr(Json.Get(root, "results")))
            {
                var r = new OnlineRepeater
                {
                    Callsign = Json.Str(Json.Get(item, "callsign")).Trim().ToUpperInvariant(),
                    City = Json.Str(Json.Get(item, "city")).Trim(),
                    State = Json.Str(Json.Get(item, "state")).Trim(),
                    Country = Json.Str(Json.Get(item, "country")).Trim(),
                    Network = Networks.Normalize(Json.Str(Json.Get(item, "ipsc_network"))),
                    Status = Json.Str(Json.Get(item, "status")).Trim(),
                    Details = PlainText(Json.Str(Json.Get(item, "details"))),
                    Trustee = string.Join(", ", Json.Arr(Json.Get(item, "trustee")).Select(Json.Str).Where(t => t.Length > 0)),
                    DmrId = Json.Int(Json.Get(item, "locator")),
                    ColorCode = Math.Max(0, Math.Min(15, Json.Int(Json.Get(item, "color_code"), 1))),
                };
                decimal rx = ParseMHz(Json.Str(Json.Get(item, "frequency")));
                if (rx <= 0) continue;
                r.RxMHz = rx;
                r.TxMHz = rx + BandPlan.ParseOffset(Json.Str(Json.Get(item, "offset")), rx);
                foreach (var t in Json.Arr(Json.Get(item, "talkgroups")))
                {
                    int id = Json.Int(Json.Get(t, "talkgroup"));
                    if (id <= 0 || id > Validator.MaxTalkgroupId) continue;
                    int slot = Json.Int(Json.Get(t, "timeslot"), 1) == 2 ? 2 : 1;
                    if (r.Talkgroups.Any(x => x.Id == id && x.Slot == slot)) continue;
                    r.Talkgroups.Add(new OnlineTalkgroup { Id = id, Slot = slot, Description = Json.Str(Json.Get(t, "description")).Trim() });
                }
                page.Repeaters.Add(r);
            }
            return page;
        }

        public static List<RadioIdUser> ParseUsers(string json)
        {
            var list = new List<RadioIdUser>();
            foreach (var item in Json.Arr(Json.Get(Json.Parse(json), "results")))
            {
                int id = Json.Int(Json.Get(item, "radio_id"));
                if (id <= 0) id = Json.Int(Json.Get(item, "id"));
                if (id <= 0) continue;
                list.Add(new RadioIdUser
                {
                    Id = id,
                    Callsign = Json.Str(Json.Get(item, "callsign")).Trim().ToUpperInvariant(),
                    FirstName = Json.Str(Json.Get(item, "fname")).Trim(),
                    LastName = Json.Str(Json.Get(item, "surname")).Trim(),
                    City = Json.Str(Json.Get(item, "city")).Trim(),
                    State = Json.Str(Json.Get(item, "state")).Trim(),
                    Country = Json.Str(Json.Get(item, "country")).Trim(),
                });
            }
            return list;
        }

        static decimal ParseMHz(string s)
        {
            return decimal.TryParse((s ?? "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) && v > 0 && v < 10000
                ? Math.Round(v, 5) : 0m;
        }

        /// <summary>RadioID "details" are HTML snippets; keep the text on one line.</summary>
        static string PlainText(string html)
        {
            string t = Regex.Replace(html ?? "", @"<br\s*/?>", " / ", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]*>", " ");
            t = t.Replace("&amp;", "&").Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }
    }

    public static class BrandMeister
    {
        public const string TalkgroupUrl = "https://api.brandmeister.network/v2/talkgroup";
        public const int Parrot = 310997;

        /// <summary>{"91":"World-wide", ...} → id → name.</summary>
        public static Dictionary<int, string> ParseTalkgroups(string json)
        {
            var map = new Dictionary<int, string>();
            var obj = Json.Obj(Json.Parse(json));
            if (obj == null) return map;
            foreach (var kv in obj)
                if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0)
                    map[id] = Json.Str(kv.Value).Trim();
            return map;
        }

        /// <summary>"Texas - 10 Minute Limit" → "Texas"; long names lose a " - suffix" or "(note)" before being cut.</summary>
        public static string ShortName(string name, int maxLength = 16)
        {
            string n = Naming.Clean(name, 0);
            if (n.Length > maxLength)
            {
                int dash = n.IndexOf(" - ", StringComparison.Ordinal);
                if (dash >= 3) n = n.Substring(0, dash).Trim();
            }
            if (n.Length > maxLength)
            {
                string bare = Regex.Replace(n, @"\s*\([^)]*\)", "").Trim();
                if (bare.Length >= 3) n = bare;
            }
            return Naming.Clean(n, maxLength);
        }

        public static bool IsPrivateCall(int id, string name)
        {
            return id == Parrot || id == 9990 || (name ?? "").IndexOf("parrot", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>US states with their FIPS codes. BrandMeister's statewide talkgroup is 31 + FIPS (Texas 48 → 3148).</summary>
        public static readonly KeyValuePair<string, int>[] UsStates =
        {
            S("Alabama", 1), S("Alaska", 2), S("Arizona", 4), S("Arkansas", 5), S("California", 6), S("Colorado", 8),
            S("Connecticut", 9), S("Delaware", 10), S("District of Columbia", 11), S("Florida", 12), S("Georgia", 13),
            S("Hawaii", 15), S("Idaho", 16), S("Illinois", 17), S("Indiana", 18), S("Iowa", 19), S("Kansas", 20),
            S("Kentucky", 21), S("Louisiana", 22), S("Maine", 23), S("Maryland", 24), S("Massachusetts", 25),
            S("Michigan", 26), S("Minnesota", 27), S("Mississippi", 28), S("Missouri", 29), S("Montana", 30),
            S("Nebraska", 31), S("Nevada", 32), S("New Hampshire", 33), S("New Jersey", 34), S("New Mexico", 35),
            S("New York", 36), S("North Carolina", 37), S("North Dakota", 38), S("Ohio", 39), S("Oklahoma", 40),
            S("Oregon", 41), S("Pennsylvania", 42), S("Rhode Island", 44), S("South Carolina", 45), S("South Dakota", 46),
            S("Tennessee", 47), S("Texas", 48), S("Utah", 49), S("Vermont", 50), S("Virginia", 51), S("Washington", 53),
            S("West Virginia", 54), S("Wisconsin", 55), S("Wyoming", 56),
        };

        static KeyValuePair<string, int> S(string name, int fips) { return new KeyValuePair<string, int>(name, fips); }

        /// <summary>BrandMeister statewide talkgroup for a US state name, or 0.</summary>
        public static int StateTalkgroup(string state)
        {
            foreach (var kv in UsStates)
                if (string.Equals(kv.Key, (state ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return 3100 + kv.Value;
            return 0;
        }
    }

    public static class Networks
    {
        /// <summary>RadioID's network field is free text ("BM", "brandmeister", "Brandmister"...). Returns known names joined with "/".</summary>
        public static string Normalize(string raw)
        {
            string t = Regex.Replace((raw ?? "").Trim(), @"\s+", " ");
            if (t.Length == 0) return "";
            string low = t.ToLowerInvariant();
            var found = new List<string>();
            if (low.Contains("brand") || Regex.IsMatch(low, @"(^|[^a-z])bm([^a-z]|$)")) found.Add("BrandMeister");
            if (low.Contains("tgif")) found.Add("TGIF");
            if (low.Contains("marc")) found.Add("DMR-MARC");
            if (low.Contains("dmr+") || low.Contains("dmrplus")) found.Add("DMR+");
            if (low.Contains("freedmr")) found.Add("FreeDMR");
            if (low.Contains("lone")) found.Add("Lonestar");
            if (low.Contains("cbridge") || low.Contains("c-bridge")) found.Add("c-Bridge");
            if (found.Count > 0) return string.Join("/", found);
            return t == low ? char.ToUpperInvariant(t[0]) + t.Substring(1) : t; // "mixed" → "Mixed"
        }
    }

    public static class BandPlan
    {
        /// <summary>US band-plan repeater offsets: 2 m below 147 MHz is -0.6, above is +0.6; 70 cm below 445 MHz is +5, above is -5.</summary>
        public static decimal? SuggestOffset(decimal rx)
        {
            if (rx >= 144m && rx < 148m) return rx < 147m ? -0.6m : 0.6m;
            if (rx >= 420m && rx < 450m) return rx < 445m ? 5m : -5m;
            return null;
        }

        /// <summary>
        /// Reads an offset like "+5.000", "-0.600", "5", "+5.00 MHz". A missing sign is taken from the band plan
        /// when the size matches (e.g. "5.000" at 442 MHz → +5). Blank or unreadable means the band plan's offset.
        /// </summary>
        public static decimal ParseOffset(string text, decimal rx)
        {
            string t = (text ?? "").Trim().ToLowerInvariant().Replace("mhz", "").Replace(" ", "").Replace(',', '.');
            decimal? suggested = SuggestOffset(rx);
            if (t.Length == 0 || !decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v))
                return suggested ?? 0m;
            bool signed = t[0] == '+' || t[0] == '-';
            if (Math.Abs(v) >= 100m) v /= 1000m; // "5000" was typed in kHz
            if (!signed && v != 0 && suggested.HasValue && Math.Abs(suggested.Value) == v) return suggested.Value;
            return v;
        }
    }

    /// <summary>A talkgroup to put on a repeater, with its definition in case the project doesn't have it yet.</summary>
    public sealed class TalkgroupChoice
    {
        public Talkgroup Talkgroup { get; set; }
        public int Slot { get; set; }

        public TalkgroupChoice(string name, int id, int slot, string callType = CallTypes.Group)
        {
            Talkgroup = new Talkgroup(name, id, callType);
            Slot = slot;
        }
    }

    public sealed class OnlineImportOptions
    {
        /// <summary>True: each repeater's zone is its city. False: every repeater goes into <see cref="Zone"/>.</summary>
        public bool ZonePerCity { get; set; } = true;
        public string Zone { get; set; } = "";
        public string Power { get; set; } = "High";
        /// <summary>Put on repeaters that have no talkgroups listed on RadioID.net.</summary>
        public List<TalkgroupChoice> DefaultTalkgroups { get; set; } = new List<TalkgroupChoice>();
        /// <summary>Optional: picks each new repeater's zone (after its location is set), overriding the two options above.</summary>
        public Func<Repeater, string> ZoneFor { get; set; }
    }

    public sealed class OnlineImportResult
    {
        public List<Repeater> Added { get; } = new List<Repeater>();
        public List<Talkgroup> NewTalkgroups { get; } = new List<Talkgroup>();
        public List<string> Notes { get; } = new List<string>();
        public int Channels => Added.Sum(r => r.Talkgroups.Count);
    }

    public static class OnlineImporter
    {
        /// <summary>
        /// Suggested default talkgroups for repeaters that don't publish theirs (BrandMeister style): your state,
        /// USA, North America, Worldwide on slot 1, Local on slot 2, and Parrot. <paramref name="names"/> (optional)
        /// supplies BrandMeister's names.
        /// </summary>
        public static List<TalkgroupChoice> SuggestedDefaults(string state, IDictionary<int, string> names = null)
        {
            string N(int id, string fallback)
            {
                return names != null && names.TryGetValue(id, out string n) && BrandMeister.ShortName(n).Length > 0 ? BrandMeister.ShortName(n) : fallback;
            }
            var list = new List<TalkgroupChoice>();
            int st = BrandMeister.StateTalkgroup(state);
            if (st > 0) list.Add(new TalkgroupChoice(N(st, Naming.Clean(state, 16)), st, 1));
            list.Add(new TalkgroupChoice("USA Nationwide", 3100, 1));
            list.Add(new TalkgroupChoice(N(93, "North America"), 93, 1));
            list.Add(new TalkgroupChoice("Worldwide", 91, 1));
            list.Add(new TalkgroupChoice(N(9, "Local"), 9, 2));
            list.Add(new TalkgroupChoice("Parrot", BrandMeister.Parrot, 1, CallTypes.Private));
            return list;
        }

        /// <summary>
        /// The project's existing copy of this repeater, if any: a digital repeater on the same output frequency
        /// and color code whose prefix or name starts with the same callsign. (Different repeaters across a state
        /// often share a frequency pair and color code, so frequency alone isn't enough.)
        /// </summary>
        public static Repeater FindExisting(Project p, OnlineRepeater r)
        {
            bool SameCall(string s) { return r.Callsign.Length > 0 && (s ?? "").Trim().StartsWith(r.Callsign, StringComparison.OrdinalIgnoreCase); }
            return p.Repeaters.FirstOrDefault(x => x.IsDigital && x.RxMHz == r.RxMHz && x.ColorCode == r.ColorCode && (SameCall(x.Prefix) || SameCall(x.Name)));
        }

        /// <summary>
        /// Name for a published talkgroup: BrandMeister's (on BrandMeister-only repeaters, or when the owner gave
        /// none), the owner's description, or "TG 1234". Multi-network repeaters keep the owner's description,
        /// since the same number can mean something else on DMR-MARC or TGIF.
        /// </summary>
        public static string TalkgroupName(OnlineRepeater r, OnlineTalkgroup t, IDictionary<int, string> bmNames)
        {
            string bmName = bmNames != null && bmNames.TryGetValue(t.Id, out string bm) ? BrandMeister.ShortName(bm) : "";
            string desc = BrandMeister.ShortName(t.Description);
            if (bmName.Length > 0 && (r.Network == "BrandMeister" || desc.Length == 0)) return bmName;
            if (desc.Length > 0) return desc;
            if (t.Id == r.DmrId && r.Callsign.Length > 0) return Naming.Clean(r.Callsign + " Local", 16);
            return "TG " + t.Id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Adds the picked repeaters to the project as DMR repeaters (one channel per talkgroup), adding any
        /// talkgroups the project doesn't have yet. Repeaters already in the project (same output frequency and
        /// color code) are skipped.
        /// </summary>
        public static OnlineImportResult AddRepeaters(Project p, IEnumerable<OnlineRepeater> picked, OnlineImportOptions o, IDictionary<int, string> bmNames)
        {
            var result = new OnlineImportResult();
            var spelling = ZonePlanner.Spellings(p);
            var skipped = new List<string>();
            var defaulted = new List<string>();
            var empty = new List<string>();
            foreach (var r in picked)
            {
                if (!r.InRadioBand) { skipped.Add(r.Callsign + " (outside the radio's bands)"); continue; }
                var existing = FindExisting(p, r);
                if (existing != null) { skipped.Add(r.Callsign + " (already in the project as \"" + existing.Name + "\")"); continue; }

                // One callsign often runs several repeaters. The second gets prefix "KC5EZZ2" (channels "KC5EZZ2 Texas"
                // rather than the generator's "KC5EZZ Texas 2"), and both names show the frequency.
                string prefix = UniquePrefix(p, r.Callsign);
                bool shared = prefix != r.Callsign || p.Repeaters.Any(x => x.IsDigital && Naming.Clean(x.Prefix, 0) == r.Callsign);
                var rep = Repeater.NewDigital(Naming.Clean(r.Callsign + " " + r.City + (shared ? " " + r.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) : ""), 0));
                rep.Prefix = prefix;
                if (shared)
                    foreach (var other in p.Repeaters.Where(x => x.IsDigital && x.SourceId > 0 && Naming.Clean(x.Prefix, 0) == r.Callsign &&
                                                                 x.Name == Naming.Clean(r.Callsign + " " + x.City, 0)))
                        other.Name = Naming.Clean(other.Name + " " + other.RxMHz.ToString("0.000", CultureInfo.InvariantCulture), 0);
                rep.Zone = o.ZonePerCity
                    ? FirstNonEmpty(Naming.Clean(r.City, 16), Naming.Clean(r.State, 16), "DMR")
                    : FirstNonEmpty(Naming.Clean(o.Zone, 16), "DMR");
                rep.RxMHz = r.RxMHz;
                rep.TxMHz = r.TxMHz;
                rep.ColorCode = r.ColorCode;
                if (Powers.Values.Contains(o.Power)) rep.Power = o.Power;
                rep.Notes = Notes(r);
                SetLocation(rep, r);
                if (o.ZoneFor != null) rep.Zone = FirstNonEmpty(Naming.Clean(o.ZoneFor(rep), 16), rep.Zone);
                rep.Zone = ZonePlanner.Canonical(spelling, rep.Zone);

                if (r.Talkgroups.Count > 0)
                {
                    foreach (var t in r.Talkgroups)
                    {
                        string name = TalkgroupName(r, t, bmNames);
                        var tg = Ensure(p, t.Id, name, BrandMeister.IsPrivateCall(t.Id, name) ? CallTypes.Private : CallTypes.Group, result);
                        Put(rep, tg.Id, t.Slot);
                    }
                }
                else if (o.DefaultTalkgroups.Count > 0)
                {
                    foreach (var c in o.DefaultTalkgroups)
                    {
                        var tg = Ensure(p, c.Talkgroup.Id, c.Talkgroup.Name, c.Talkgroup.CallType, result);
                        Put(rep, tg.Id, c.Slot);
                    }
                    defaulted.Add(r.Callsign);
                }
                else empty.Add(r.Callsign);

                p.Repeaters.Add(rep);
                result.Added.Add(rep);
            }
            p.SyncZones();
            foreach (var rep in result.Added) p.ApplyZoneTalkgroups(rep);

            if (defaulted.Count > 0)
                result.Notes.Add(defaulted.Count + " repeater" + (defaulted.Count == 1 ? " doesn't" : "s don't") + " list talkgroups on RadioID.net, so they got your default set: " +
                                 string.Join(", ", defaulted) + ". Check each repeater's web page for which talkgroups it carries on which slot.");
            if (empty.Count > 0)
                result.Notes.Add("No talkgroups yet (none listed, no defaults picked): " + string.Join(", ", empty) + ". Add some in the repeater editor.");
            if (skipped.Count > 0)
                result.Notes.Add("Skipped: " + string.Join("; ", skipped) + ".");
            return result;
        }

        static string Notes(OnlineRepeater r)
        {
            var parts = new List<string> { RadioId.Site + (r.DmrId > 0 ? " repeater ID " + r.DmrId.ToString(CultureInfo.InvariantCulture) : "") };
            if (r.Network.Length > 0) parts.Add(r.Network);
            if (r.Trustee.Length > 0) parts.Add("trustee " + r.Trustee);
            if (r.Details.Length > 0) parts.Add(r.Details.Length > 200 ? r.Details.Substring(0, 200) + "..." : r.Details);
            return string.Join(" | ", parts);
        }

        /// <summary>The callsign, or callsign + 2, 3... when another DMR repeater in the project already uses it as its prefix.</summary>
        static string UniquePrefix(Project p, string callsign)
        {
            var used = new HashSet<string>(p.Repeaters.Where(x => x.IsDigital).Select(x => Naming.Clean(x.Prefix, 0)), StringComparer.OrdinalIgnoreCase);
            if (!used.Contains(callsign)) return callsign;
            for (int n = 2; ; n++)
                if (!used.Contains(callsign + n.ToString(CultureInfo.InvariantCulture))) return callsign + n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Copies the listing's place (and what the map made of it) onto the project repeater.</summary>
        public static void SetLocation(Repeater rep, OnlineRepeater r)
        {
            rep.City = NullIfEmpty(r.City);
            rep.State = NullIfEmpty(r.State);
            rep.Country = NullIfEmpty(r.Country);
            rep.SourceId = r.DmrId;
            var loc = r.Location;
            if (loc == null) return;
            if (loc.State != null) rep.State = loc.State.Name;
            if (loc.Country != null) rep.Country = loc.Country.Name;
            rep.County = loc.County?.Name;
            rep.AreaCode = (loc.County ?? loc.State ?? loc.Country)?.Code;
            rep.Latitude = loc.Lat.HasValue ? Math.Round(loc.Lat.Value, 4) : (double?)null;
            rep.Longitude = loc.Lon.HasValue ? Math.Round(loc.Lon.Value, 4) : (double?)null;
        }

        static string NullIfEmpty(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        static void Put(Repeater rep, int id, int slot)
        {
            if (!rep.Talkgroups.Any(x => x.TalkgroupId == id && x.Slot == slot))
                rep.Talkgroups.Add(new RepeaterTalkgroup(id, slot));
        }

        /// <summary>The project's talkgroup with this ID, or a new one with a unique name.</summary>
        static Talkgroup Ensure(Project p, int id, string name, string callType, OnlineImportResult result)
        {
            var tg = p.FindTalkgroup(id);
            if (tg != null) return tg;
            string n = Naming.Clean(name, 16);
            if (n.Length == 0) n = "TG " + id.ToString(CultureInfo.InvariantCulture);
            bool Taken(string x) { return p.Talkgroups.Any(t => string.Equals(t.Name, x, StringComparison.OrdinalIgnoreCase)); }
            if (Taken(n)) n = Naming.Clean(n + " " + id.ToString(CultureInfo.InvariantCulture), 16);
            if (Taken(n))
            {
                var namer = new UniqueNamer(16, "TG");
                foreach (var t in p.Talkgroups) namer.Reserve(t.Name);
                n = namer.Claim(n);
            }
            tg = new Talkgroup(n, id, CallTypes.Normalize(callType));
            p.Talkgroups.Add(tg);
            result.NewTalkgroups.Add(tg);
            return tg;
        }

        static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        }
    }

    public static class Presets
    {
        /// <summary>NOAA Weather Radio, WX1-WX7 (receive only).</summary>
        public static readonly KeyValuePair<string, decimal>[] Noaa =
        {
            new KeyValuePair<string, decimal>("WX1", 162.550m), new KeyValuePair<string, decimal>("WX2", 162.400m),
            new KeyValuePair<string, decimal>("WX3", 162.475m), new KeyValuePair<string, decimal>("WX4", 162.425m),
            new KeyValuePair<string, decimal>("WX5", 162.450m), new KeyValuePair<string, decimal>("WX6", 162.500m),
            new KeyValuePair<string, decimal>("WX7", 162.525m),
        };

        /// <summary>Adds the NOAA weather channels the project doesn't already have (same frequency), lowest frequency first.</summary>
        public static List<Repeater> AddNoaaWeather(Project p, string zone = "Weather")
        {
            var added = new List<Repeater>();
            foreach (var kv in Noaa.OrderBy(x => x.Value))
            {
                if (p.Repeaters.Any(r => !r.IsDigital && r.RxMHz == kv.Value)) continue;
                var r = Repeater.NewAnalog("NOAA " + kv.Key);
                r.Zone = zone;
                r.RxMHz = r.TxMHz = kv.Value;
                r.RxOnly = true;
                r.Bandwidth = Bandwidths.Wide;
                r.Notes = "NOAA Weather Radio " + kv.Key + " (receive only)";
                p.Repeaters.Add(r);
                added.Add(r);
            }
            p.SyncZones();
            return added;
        }
    }
}
