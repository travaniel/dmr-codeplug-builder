using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>Ways to sort repeaters into zones automatically.</summary>
    public enum ZoneScheme { City, County, State, Country, Band, Single }

    /// <summary>Automatic zone names for repeaters, from where they are (or their band).</summary>
    public static class ZonePlanner
    {
        public static readonly KeyValuePair<ZoneScheme, string>[] Choices =
        {
            new KeyValuePair<ZoneScheme, string>(ZoneScheme.County, "One zone per county (US)"),
            new KeyValuePair<ZoneScheme, string>(ZoneScheme.City, "One zone per city"),
            new KeyValuePair<ZoneScheme, string>(ZoneScheme.State, "One zone per state / province"),
            new KeyValuePair<ZoneScheme, string>(ZoneScheme.Country, "One zone per country"),
            new KeyValuePair<ZoneScheme, string>(ZoneScheme.Band, "By band (2 m / 70 cm)"),
            new KeyValuePair<ZoneScheme, string>(ZoneScheme.Single, "Everything in one zone"),
        };

        static readonly string[] CountySuffixes = { " County", " Parish", " Borough", " Census Area", " City and Borough", " Municipality", " Municipio", " city" };

        /// <summary>
        /// The zone a repeater would get under <paramref name="scheme"/>. Falls back to the next bigger area
        /// when the listing doesn't say (no county → state → country → "DMR"). Always a clean name of 16 or fewer.
        /// </summary>
        public static string ZoneName(Repeater r, ZoneScheme scheme, string single = "DMR")
        {
            string name = "";
            switch (scheme)
            {
                case ZoneScheme.City:
                    name = Fit(r.City);
                    break;
                case ZoneScheme.County:
                    name = CountyZone(r.County);
                    break;
                case ZoneScheme.Country:
                    name = Fit(r.Country);
                    break;
                case ZoneScheme.Band:
                    name = r.RxMHz >= 400m ? "70cm DMR" : r.RxMHz > 0 && r.RxMHz < 300m ? "2m DMR" : "";
                    break;
                case ZoneScheme.Single:
                    name = Fit(single);
                    break;
            }
            if (name.Length == 0 && scheme != ZoneScheme.Country && scheme != ZoneScheme.Single) name = Fit(r.State);
            if (name.Length == 0) name = Fit(r.Country);
            if (name.Length == 0) name = Fit(single);
            return name.Length == 0 ? "DMR" : name;
        }

        /// <summary>"Tom Green County" → "Tom Green Co"; "Los Angeles County" → "Los Angeles Co"; "Richmond city" → "Richmond".</summary>
        public static string CountyZone(string county)
        {
            string c = Naming.Clean(Naming.Fold(county), 0);
            if (c.Length == 0) return "";
            foreach (var suf in CountySuffixes)
                if (c.EndsWith(suf, StringComparison.Ordinal) && c.Length > suf.Length)
                {
                    string stem = c.Substring(0, c.Length - suf.Length).Trim();
                    return stem.Length + 3 <= 16 && suf == " County" ? stem + " Co" : Naming.Clean(stem, 16);
                }
            return Naming.Clean(c, 16);
        }

        static string Fit(string s) { return Naming.Clean(Naming.Fold(s), 16); }

        /// <summary>Zone names already in the project, for <see cref="Canonical"/>.</summary>
        public static Dictionary<string, string> Spellings(Project p)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in p.Zones) if (!string.IsNullOrWhiteSpace(z.Name) && !d.ContainsKey(z.Name.Trim())) d[z.Name.Trim()] = z.Name.Trim();
            return d;
        }

        /// <summary>
        /// One spelling per zone: "abilene" and "ABILENE" become whichever came first ("Abilene"). Zones match
        /// case-insensitively anyway; this keeps the Repeaters list tidy (RadioID's capitalization varies).
        /// </summary>
        public static string Canonical(Dictionary<string, string> spellings, string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return n;
            if (spellings.TryGetValue(n, out string first)) return first;
            spellings[n] = n;
            return n;
        }

        /// <summary>Re-zones the given repeaters and applies the zone talkgroup sets. New zones are added after the existing ones.</summary>
        public static void Apply(Project p, IEnumerable<Repeater> repeaters, ZoneScheme scheme, string single = "DMR")
        {
            var list = repeaters.ToList();
            var spelling = Spellings(p);
            foreach (var r in list) r.Zone = Canonical(spelling, ZoneName(r, scheme, single));
            p.SyncZones();
            foreach (var r in list) p.ApplyZoneTalkgroups(r);
        }
    }
}
