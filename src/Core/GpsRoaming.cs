using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>One GPS zone-switching entry: inside the circle, the radio switches to the zone.</summary>
    public sealed class GpsZoneEntry
    {
        public string Zone;
        /// <summary>Position of the zone in Zone.CSV, 0-based (what GpsRoaming.CSV stores).</summary>
        public int ZoneIndex;
        public double Latitude, Longitude;
        public int RadiusMeters;
        /// <summary>Kilometres from home to the centre (null without a home).</summary>
        public double? FromHomeKm;
    }

    /// <summary>
    /// GPS zone switching (roadmap item 10, GpsRoaming.CSV, .LST section 22; format in HANDOFF 4): up to 32 circles, one per area zone,
    /// around the zone's placed repeaters. The radio switches to a zone when its GPS position is inside the circle (GPS and GPS Roaming
    /// must be on in the radio). The CSV refers to zones by position, so it is written with every Zone.CSV while the option is on.
    /// </summary>
    public static class GpsRoaming
    {
        public const string File = "GpsRoaming.CSV";
        public const int MaxEntries = 32;
        /// <summary>Added to the farthest repeater's distance from the centre: you reach a repeater from outside its town.</summary>
        public const int DefaultMarginKm = 15;
        public const int MinRadiusMeters = 5000;

        public static CsvTable BuiltInTemplate() { return CsvTable.Parse(CpsFormat.ReadResource(File)); }

        /// <summary>
        /// The entries for a generated codeplug: area zones (and route zones) with placed repeaters, a circle around them (centre = mean of
        /// the repeaters, radius = farthest repeater + margin). Nearest to home first when there is a home, else in zone order; at most 32.
        /// </summary>
        public static List<GpsZoneEntry> Plan(Project p, IList<GeneratedZone> zones, int marginKm = DefaultMarginKm, IList<string> notes = null)
        {
            var list = new List<GpsZoneEntry>();
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                if (z.Info == null || z.FromCps) continue;
                string kind = p.ZoneKindOf(z.Info);
                if (kind != ZoneKinds.Area) continue; // favourites, talkgroup and utility zones are wanted everywhere
                var reps = z.Members.Select(m => m.Repeater).Distinct().Where(r => r.Latitude.HasValue && r.Longitude.HasValue && r != p.Hotspot).ToList();
                if (reps.Count == 0) continue;
                double lat = reps.Average(r => r.Latitude.Value), lon = reps.Average(r => r.Longitude.Value);
                double far = reps.Max(r => Distances.Km(lat, lon, r.Latitude.Value, r.Longitude.Value));
                int radius = Math.Max(MinRadiusMeters, (int)Math.Round((far + marginKm) * 1000 / 100.0) * 100);
                list.Add(new GpsZoneEntry
                {
                    Zone = z.Name, ZoneIndex = i, Latitude = lat, Longitude = lon, RadiusMeters = radius,
                    FromHomeKm = p.Home != null ? Distances.Km(p.Home.Latitude, p.Home.Longitude, lat, lon) : (double?)null,
                });
            }
            // Zones along a route zone's road come first, in driving order (route by route); the rest nearest home first.
            var routes = p.Zones.Where(z => z.IsRoute).Select(z => new { pts = z.Route(), corridor = Math.Max(z.RouteCorridorKm, 1) }).ToList();
            var keyed = list.Select((e, i) =>
            {
                int route = int.MaxValue; double along = 0;
                for (int k = 0; k < routes.Count && route == int.MaxValue; k++)
                {
                    double off = RoutePlanner.DistanceTo(routes[k].pts, e.Latitude, e.Longitude, out double a);
                    if (off <= routes[k].corridor + e.RadiusMeters / 1000.0) { route = k; along = a; }
                }
                return new { e, i, route, along };
            });
            var ordered = keyed.OrderBy(x => x.route).ThenBy(x => x.route == int.MaxValue ? 0 : x.along).ThenBy(x => x.e.FromHomeKm ?? 0).ThenBy(x => x.i)
                               .Select(x => x.e).ToList();
            if (ordered.Count > MaxEntries)
            {
                notes?.Add("GPS zone switching: the radio holds " + MaxEntries + " entries, so only the first " + MaxEntries + " zones (along your routes, then " +
                           (p.Home != null ? "nearest home" : "in zone order") + ") switch automatically (" + (ordered.Count - MaxEntries) + " left out).");
                ordered = ordered.Take(MaxEntries).ToList();
            }
            return ordered;
        }

        /// <summary>GpsRoaming.CSV: the entries, then off rows up to 32 (copies of the template's rows).</summary>
        public static CsvTable ToTable(IList<GpsZoneEntry> entries, CsvTable template = null)
        {
            template = template ?? BuiltInTemplate();
            var t = template.CloneHeader();
            var off = template.Rows.FirstOrDefault();
            for (int i = 0; i < MaxEntries; i++)
            {
                var row = t.NewRow(off);
                if (i < entries.Count)
                {
                    var e = entries[i];
                    t.Set(row, "1", "OnOff");
                    t.Set(row, Num(e.ZoneIndex), "Zone");
                    Coordinate(e.Latitude, out int latDeg, out int latMin, out int latHund);
                    Coordinate(e.Longitude, out int lonDeg, out int lonMin, out int lonHund);
                    t.Set(row, Num(latDeg), "Latitude Degree");
                    t.Set(row, e.Latitude < 0 ? "1" : "0", "North or South");
                    t.Set(row, Num(lonDeg), "Longtitude Degree", "Longitude Degree");
                    t.Set(row, e.Longitude < 0 ? "1" : "0", "East or West");
                    t.Set(row, Minute(latMin), "Latitude Minute");
                    t.Set(row, Minute(latHund), "Latitude Minute1");
                    t.Set(row, Minute(lonMin), "Longtitude Minute", "Longitude Minute");
                    t.Set(row, Minute(lonHund), "Longtitude Minute1", "Longitude Minute1");
                    t.Set(row, Num(e.RadiusMeters), "Radius(Meter)", "Radius");
                }
                t.Rows.Add(row);
            }
            return t;
        }

        /// <summary>Degrees, whole minutes and hundredths of a minute (rounded), of |value|: 31.46383 → 31, 27, 83.</summary>
        public static void Coordinate(double value, out int degrees, out int minutes, out int hundredths)
        {
            long total = (long)Math.Round(Math.Abs(value) * 60 * 100); // hundredths of a minute
            degrees = (int)(total / 6000);
            minutes = (int)(total % 6000 / 100);
            hundredths = (int)(total % 100);
        }

        /// <summary>"27.00" (two digits, then ".00", as the CPS writes both minute columns).</summary>
        static string Minute(int v) { return v.ToString("00", CultureInfo.InvariantCulture) + ".00"; }

        static string Num(int v) { return v.ToString(CultureInfo.InvariantCulture); }
    }
}
