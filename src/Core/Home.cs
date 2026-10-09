using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace CodeplugBuilder.Core
{
    /// <summary>Where the user lives: the point zones are ordered from and distances are measured from (<see cref="Project.Home"/>).</summary>
    [DataContract(Namespace = "")]
    public sealed class HomeLocation
    {
        /// <summary>"Brownwood, Texas": what the user typed or the RadioID lookup gave.</summary>
        [DataMember(Order = 1, EmitDefaultValue = false)] public string Place { get; set; }
        [DataMember(Order = 2)] public double Latitude { get; set; }
        [DataMember(Order = 3)] public double Longitude { get; set; }
        /// <summary>Map country code ("US"), for miles or kilometres.</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Country { get; set; }

        public bool UsesMiles => Distances.UsesMiles(Country);

        /// <summary>Finds a town on the built-in map ("Brownwood, TX", "Brownwood Texas", "Leeds, England, United Kingdom"). Null when not found.</summary>
        public static HomeLocation Find(GeoAtlas atlas, string city, string state, string country)
        {
            var loc = atlas?.Locate(city, state, country);
            if (loc?.Lat == null || loc.Lon == null) return null;
            string place = string.Join(", ", new[] { (city ?? "").Trim(), loc.State?.Name ?? (state ?? "").Trim() }.Where(s => s.Length > 0));
            return new HomeLocation { Place = place, Latitude = loc.Lat.Value, Longitude = loc.Lon.Value, Country = loc.Country?.Code };
        }

        /// <summary>The country most of the project's repeaters are in (a typed town is looked for there first); "United States" when none say.</summary>
        public static string DefaultCountry(Project p)
        {
            return p.Repeaters.Where(r => !string.IsNullOrEmpty(r.Country)).GroupBy(r => r.Country, StringComparer.OrdinalIgnoreCase)
                              .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "United States";
        }

        /// <summary>
        /// Reads a typed town: "City, State[, Country]" or "City State". Tries the words as city + state, then the whole text as a
        /// city in the default country. Null when the map doesn't know it.
        /// </summary>
        public static HomeLocation Parse(GeoAtlas atlas, string text, string defaultCountry = null)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || atlas == null) return null;
            var parts = text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (parts.Count >= 3) return Find(atlas, parts[0], parts[1], string.Join(", ", parts.Skip(2)));
            if (parts.Count == 2)
                return Find(atlas, parts[0], parts[1], defaultCountry) ?? Find(atlas, parts[0], parts[1], null) ?? Find(atlas, parts[0], null, parts[1]);
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = words.Length - 1; i >= 1; i--)
            {
                var hit = Find(atlas, string.Join(" ", words.Take(i)), string.Join(" ", words.Skip(i)), defaultCountry);
                if (hit != null) return hit;
            }
            return Find(atlas, text, null, defaultCountry);
        }
    }

    /// <summary>Great-circle distance and bearing, and how to show them.</summary>
    public static class Distances
    {
        const double EarthKm = 6371.0;
        public const double KmPerMile = 1.609344;

        public static double Km(double lat1, double lon1, double lat2, double lon2)
        {
            double r = Math.PI / 180;
            double dLat = (lat2 - lat1) * r, dLon = (lon2 - lon1) * r;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * EarthKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        /// <summary>Initial bearing from the first point to the second, 0-360 degrees (0 = north).</summary>
        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double r = Math.PI / 180;
            double y = Math.Sin((lon2 - lon1) * r) * Math.Cos(lat2 * r);
            double x = Math.Cos(lat1 * r) * Math.Sin(lat2 * r) - Math.Sin(lat1 * r) * Math.Cos(lat2 * r) * Math.Cos((lon2 - lon1) * r);
            return (Math.Atan2(y, x) / r + 360) % 360;
        }

        static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        public static string Compass(double bearing) { return Points[(int)Math.Round(((bearing % 360) + 360) % 360 / 45.0) % 8]; }

        /// <summary>Miles in the US, UK, Liberia and Myanmar; kilometres elsewhere. Unknown = miles (most users are in the US).</summary>
        public static bool UsesMiles(string countryCode)
        {
            return string.IsNullOrEmpty(countryCode) || countryCode == "US" || countryCode == "GB" || countryCode == "LR" || countryCode == "MM" ||
                   countryCode == "PR" || countryCode == "GU" || countryCode == "VI";
        }

        /// <summary>"12 mi NE", "3 km S", "here" (under half a unit).</summary>
        public static string Describe(double km, double bearing, bool miles)
        {
            double v = miles ? km / KmPerMile : km;
            if (v < 0.5) return "here";
            return (v < 10 ? v.ToString("0.#", CultureInfo.InvariantCulture) : Math.Round(v).ToString("0", CultureInfo.InvariantCulture)) +
                   (miles ? " mi " : " km ") + Compass(bearing);
        }
    }

    /// <summary>Zone order that matches use (roadmap item 8): home first, area zones nearest first, utilities last.</summary>
    public static class ZoneOrder
    {
        /// <summary>
        /// Group of a zone: 0 the hotspot's, 1 favourites, 2 area zones (by distance), 3 talkgroup zones, 4 utilities
        /// (simplex, weather, APRS).
        /// </summary>
        public static int Group(Project p, ZoneInfo z)
        {
            if (p.HotspotEnabled && Project.SameZone(z.Name, p.Hotspot.Zone)) return 0;
            string kind = p.ZoneKindOf(z);
            return kind == ZoneKinds.Favorites ? 1 : kind == ZoneKinds.Talkgroup ? 3 : kind == ZoneKinds.Utility ? 4 : 2;
        }

        /// <summary>Distance from home to the zone's nearest placed repeater, km; null when there is no home or no placed repeater.</summary>
        public static double? DistanceKm(Project p, ZoneInfo z)
        {
            if (p.Home == null) return null;
            // The zone's own repeaters count even before they have talkgroups (the wizard sorts before its talkgroup step).
            double? best = null;
            var reps = p.ActiveRepeaters().Where(r => Project.SameZone(r.Zone, z.Name)).Concat(p.ZoneChannels(z.Name).Select(c => c.Repeater));
            foreach (var r in reps)
            {
                double? d = p.DistanceKm(r);
                if (d != null && (best == null || d < best)) best = d;
            }
            return best;
        }

        /// <summary>
        /// Sorts <see cref="Project.Zones"/>: by <see cref="Group"/>, area zones nearest first (zones nothing places keep their
        /// order, after the placed ones), everything else in its current order. Returns true when the order changed.
        /// </summary>
        public static bool Sort(Project p)
        {
            p.SyncZones();
            var keyed = p.Zones.Select((z, i) => new { z, i, g = Group(p, z) }).Select(x => new { x.z, x.i, x.g, d = x.g == 2 ? DistanceKm(p, x.z) : null }).ToList();
            var sorted = keyed.OrderBy(x => x.g).ThenBy(x => x.d == null ? 1 : 0).ThenBy(x => x.d ?? 0).ThenBy(x => x.i).Select(x => x.z).ToList();
            if (sorted.SequenceEqual(p.Zones)) return false;
            p.Zones.Clear();
            p.Zones.AddRange(sorted);
            return true;
        }
    }

    /// <summary>
    /// Talkgroup order within a repeater (roadmap item 8, the RATS convention): local first, then state, regional, wide area,
    /// private calls last. Only when the user asks (<see cref="Sort"/>): the order is the channel order in the zone.
    /// </summary>
    public static class TalkgroupOrder
    {
        public const int Local = 0, State = 1, Regional = 2, WideArea = 3, PrivateCall = 4;

        public static int Rank(Repeater r, Talkgroup tg, int talkgroupId)
        {
            if (tg != null && !tg.IsGroupCall) return PrivateCall;
            int id = talkgroupId;
            if (id == 2 || id == 8 || id == 9 || id == 99 || (r.SourceId > 0 && id == r.SourceId)) return Local; // 99 = simplex
            if (id > 3100 && id < 3200) return State;               // US statewide: 31 + FIPS
            if (id >= 31000 && id < 32000) return State;            // US state sub-regions (31480 Texas North...)
            if (id == 3100 || id < 100) return WideArea;            // 91 Worldwide, 93 North America, 3100 USA
            return Regional;
        }

        /// <summary>Puts one repeater's talkgroups in RATS order (stable within each group). Returns true when it changed.</summary>
        public static bool Sort(Project p, Repeater r)
        {
            if (r == null || !r.IsDigital || r.Talkgroups.Count < 2) return false;
            var sorted = r.Talkgroups.Select((e, i) => new { e, i, k = Rank(r, p.FindTalkgroup(e.TalkgroupId), e.TalkgroupId) })
                                     .OrderBy(x => x.k).ThenBy(x => x.i).Select(x => x.e).ToList();
            if (sorted.SequenceEqual(r.Talkgroups)) return false;
            r.Talkgroups.Clear();
            r.Talkgroups.AddRange(sorted);
            return true;
        }

        public static string Describe(int rank)
        {
            return rank == Local ? "local" : rank == State ? "state" : rank == Regional ? "regional" : rank == WideArea ? "wide area" : "private call";
        }
    }
}
