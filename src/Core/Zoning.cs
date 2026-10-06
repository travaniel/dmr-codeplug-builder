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
                    return stem.Length + 3 <= 16 && suf == " County" ? stem + " Co" : Naming.Fit(stem, 16);
                }
            return Naming.Fit(c, 16);
        }

        static string Fit(string s) { return Naming.Fit(s, 16); }

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
            SeparateStates(p, scheme, single);
            p.SyncZones();
            foreach (var r in list) p.ApplyZoneTalkgroups(r);
        }

        /// <summary>
        /// Counties and cities with the same name in different states ("Washington County" in Texas and Oklahoma)
        /// would land in one zone. Those zones get the state added instead: "Washington Co TX", "Washington Co OK".
        /// Only zones that still have their automatic name are touched; their talkgroup sets go to each new zone.
        /// </summary>
        public static void SeparateStates(Project p, ZoneScheme scheme, string single = "DMR")
        {
            if (scheme != ZoneScheme.County && scheme != ZoneScheme.City) return;
            string Separate(Repeater r, string auto) { return Naming.Fit(auto + " " + StateCode(r.State), 16); }
            var groups = p.Repeaters
                .Where(r => !string.IsNullOrWhiteSpace(r.Zone) && !string.IsNullOrWhiteSpace(r.State))
                .Select(r => new KeyValuePair<Repeater, string>(r, ZoneName(r, scheme, single)))
                .Where(x => Project.SameZone(x.Key.Zone, x.Value) || Project.SameZone(x.Key.Zone, Separate(x.Key, x.Value)))
                .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(x => x.Key.State.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .ToList();
            foreach (var g in groups)
            {
                var old = p.FindZone(g.Key);
                foreach (var x in g)
                {
                    string name = Separate(x.Key, g.Key);
                    var info = p.FindZone(name);
                    if (info == null)
                    {
                        info = new ZoneInfo(name);
                        if (old?.Talkgroups != null) info.Talkgroups = old.Talkgroups.Select(t => new ZoneTalkgroup(t.TalkgroupId, t.Slot)).ToList();
                        p.Zones.Add(info);
                    }
                    x.Key.Zone = info.Name;
                }
            }
            if (groups.Count > 0) p.SyncZones();
        }

        /// <summary>"Texas" → "TX", "Ontario" → "ON"; other states and provinces shortened.</summary>
        public static string StateCode(string state)
        {
            string s = Naming.Clean(Naming.Fold(state), 0);
            foreach (var kv in Naming.StateCodes)
                if (string.Equals(kv.Key, s, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return Naming.Fit(s, 6);
        }
    }
}
