using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CodeplugBuilder.Core
{
    // The built-in map: country, state/province and US county outlines plus a gazetteer of places, built by
    // tools/GeoBuild from US Census, Natural Earth (public domain) and GeoNames (CC BY 4.0) data and embedded
    // as CodeplugBuilder.Geo.atlas.gz. RadioID.net lists repeaters by free-text city/state/country with no
    // coordinates, so GeoAtlas.Locate turns those into a point (from the gazetteer) and the areas around it.

    public enum AreaLevel { Country = 0, State = 1, County = 2 }

    public sealed class GeoArea
    {
        public string Code { get; internal set; }
        public string ParentCode { get; internal set; }
        public string Name { get; internal set; }
        public string[] Aliases { get; internal set; }
        public AreaLevel Level { get; internal set; }
        /// <summary>Outline rings as interleaved lon, lat pairs. Holes are rings too (even-odd rule).</summary>
        public float[][] Rings { get; internal set; }
        public float MinLon { get; internal set; }
        public float MinLat { get; internal set; }
        public float MaxLon { get; internal set; }
        public float MaxLat { get; internal set; }
        /// <summary>Where to put the label: the middle of the largest ring's box.</summary>
        public float LabelLon { get; internal set; }
        public float LabelLat { get; internal set; }
        public GeoArea Parent { get; internal set; }
        public List<GeoArea> Children { get; } = new List<GeoArea>();

        /// <summary>ISO country code ("US", "DE"...) of this area.</summary>
        public string CountryCode => Level == AreaLevel.Country ? Code : Level == AreaLevel.State ? ParentCode : Parent?.ParentCode ?? "";

        /// <summary>"Tom Green County, Texas", "Bavaria, Germany", "Germany".</summary>
        public string FullName => Parent == null ? Name : Name + ", " + Parent.Name;

        public bool Contains(double lon, double lat)
        {
            if (lon < MinLon || lon > MaxLon || lat < MinLat || lat > MaxLat) return false;
            bool inside = false;
            foreach (var r in Rings)
            {
                int n = r.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = r[2 * i], yi = r[2 * i + 1], xj = r[2 * j], yj = r[2 * j + 1];
                    if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
                }
            }
            return inside;
        }

        public override string ToString() { return FullName; }
    }

    public sealed class GeoPlace
    {
        public string Country { get; internal set; }
        /// <summary>US state code ("TX") for US places; empty elsewhere.</summary>
        public string Admin { get; internal set; }
        public string[] Names { get; internal set; }
        public float Lat { get; internal set; }
        public float Lon { get; internal set; }
        public long Weight { get; internal set; }
        public string Name => Names.Length > 0 ? Names[0] : "";
    }

    public enum LocationPrecision { None = 0, Country = 1, State = 2, City = 3 }

    /// <summary>Where a repeater is, as far as its listing tells: a point (when the city was found) and the areas around it.</summary>
    public sealed class GeoLocation
    {
        public GeoArea Country { get; set; }
        public GeoArea State { get; set; }
        public GeoArea County { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public LocationPrecision Precision { get; set; }

        /// <summary>Nowhere (a fresh object each time, so callers can't change a shared one).</summary>
        public static GeoLocation Unknown => new GeoLocation();

        /// <summary>The area at exactly that level, or null when the listing didn't say.</summary>
        public GeoArea At(AreaLevel level)
        {
            return level == AreaLevel.Country ? Country : level == AreaLevel.State ? State : County;
        }

        /// <summary>True when this location lies in <paramref name="area"/> (or one of its sub-areas).</summary>
        public bool IsIn(GeoArea area)
        {
            if (area == null) return false;
            var mine = At(area.Level);
            return mine != null && mine == area;
        }
    }

    public sealed class GeoAtlas
    {
        public const string ResourceName = "CodeplugBuilder.Geo.atlas.gz";

        public List<GeoArea> Countries { get; } = new List<GeoArea>();
        public List<GeoArea> States { get; } = new List<GeoArea>();
        public List<GeoArea> Counties { get; } = new List<GeoArea>();
        public List<GeoPlace> Places { get; } = new List<GeoPlace>();

        readonly Dictionary<string, GeoArea> byCode = new Dictionary<string, GeoArea>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, GeoArea> countryByName = new Dictionary<string, GeoArea>();
        readonly Dictionary<string, GeoArea> stateByKey = new Dictionary<string, GeoArea>();
        readonly Dictionary<string, List<GeoPlace>> placesByKey = new Dictionary<string, List<GeoPlace>>();

        static readonly object builtInLock = new object();
        static GeoAtlas builtIn;

        /// <summary>The embedded atlas, loaded once (about a quarter of a second) and shared.</summary>
        public static GeoAtlas BuiltIn()
        {
            lock (builtInLock)
            {
                if (builtIn != null) return builtIn;
                using (var s = typeof(GeoAtlas).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null) throw new InvalidOperationException("Missing built-in map " + ResourceName);
                    return builtIn = Load(s);
                }
            }
        }

        public static GeoAtlas Load(Stream gzip)
        {
            var atlas = new GeoAtlas();
            using (var gz = new GZipStream(gzip, CompressionMode.Decompress, true))
            using (var r = new BinaryReader(new BufferedStream(gz, 1 << 16), Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(5)) != "CPGEO") throw new InvalidDataException("Not a map atlas");
                int version = r.ReadByte();
                if (version != 1) throw new InvalidDataException("Unknown map atlas version " + version);
                atlas.ReadLayer(r, AreaLevel.Country, atlas.Countries);
                atlas.ReadLayer(r, AreaLevel.State, atlas.States);
                atlas.ReadLayer(r, AreaLevel.County, atlas.Counties);
                int places = (int)VarInt(r);
                for (int i = 0; i < places; i++)
                {
                    var p = new GeoPlace
                    {
                        Country = r.ReadString(),
                        Admin = r.ReadString(),
                        Names = r.ReadString().Split('|'),
                    };
                    p.Lat = (float)(ZigZag(r) / Quantum);
                    p.Lon = (float)(ZigZag(r) / Quantum);
                    p.Weight = VarInt(r);
                    atlas.Places.Add(p);
                }
            }
            atlas.Index();
            return atlas;
        }

        const double Quantum = 10000.0;

        void ReadLayer(BinaryReader r, AreaLevel level, List<GeoArea> list)
        {
            int count = (int)VarInt(r);
            for (int i = 0; i < count; i++)
            {
                var a = new GeoArea { Level = level, Code = r.ReadString(), ParentCode = r.ReadString(), Name = r.ReadString() };
                string aliases = r.ReadString();
                a.Aliases = aliases.Length == 0 ? new string[0] : aliases.Split('|');
                int rings = (int)VarInt(r);
                a.Rings = new float[rings][];
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                double bestArea = -1;
                for (int k = 0; k < rings; k++)
                {
                    int n = (int)VarInt(r);
                    var pts = new float[n * 2];
                    long x = 0, y = 0;
                    float rMinX = float.MaxValue, rMinY = float.MaxValue, rMaxX = float.MinValue, rMaxY = float.MinValue;
                    for (int j = 0; j < n; j++)
                    {
                        x += ZigZag(r);
                        y += ZigZag(r);
                        float fx = (float)(x / Quantum), fy = (float)(y / Quantum);
                        pts[2 * j] = fx;
                        pts[2 * j + 1] = fy;
                        if (fx < rMinX) rMinX = fx;
                        if (fx > rMaxX) rMaxX = fx;
                        if (fy < rMinY) rMinY = fy;
                        if (fy > rMaxY) rMaxY = fy;
                    }
                    a.Rings[k] = pts;
                    minX = Math.Min(minX, rMinX); maxX = Math.Max(maxX, rMaxX);
                    minY = Math.Min(minY, rMinY); maxY = Math.Max(maxY, rMaxY);
                    double box = (double)(rMaxX - rMinX) * (rMaxY - rMinY);
                    if (box > bestArea)
                    {
                        bestArea = box;
                        a.LabelLon = (rMinX + rMaxX) / 2;
                        a.LabelLat = (rMinY + rMaxY) / 2;
                    }
                }
                a.MinLon = minX; a.MinLat = minY; a.MaxLon = maxX; a.MaxLat = maxY;
                list.Add(a);
            }
        }

        static long VarInt(BinaryReader r)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte b = r.ReadByte();
                result |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return (long)result;
                shift += 7;
            }
        }

        static long ZigZag(BinaryReader r)
        {
            ulong u = (ulong)VarInt(r);
            return (long)(u >> 1) ^ -(long)(u & 1);
        }

        void Index()
        {
            foreach (var a in Countries.Concat(States).Concat(Counties)) byCode[a.Code] = a;
            foreach (var a in States.Concat(Counties))
                if (byCode.TryGetValue(a.ParentCode, out var parent))
                {
                    a.Parent = parent;
                    parent.Children.Add(a);
                }
            foreach (var c in Countries)
                foreach (var n in new[] { c.Name, c.Code }.Concat(c.Aliases))
                {
                    string k = Key(n);
                    if (k.Length > 0 && !countryByName.ContainsKey(k)) countryByName[k] = c;
                }
            foreach (var kv in CountryAliases)
                if (byCode.TryGetValue(kv.Value, out var c)) countryByName[Key(kv.Key)] = c;
            foreach (var s in States)
            {
                int dash = s.Code.IndexOf('-');
                var names = new[] { s.Name, dash > 0 ? s.Code.Substring(dash + 1) : "" }.Concat(s.Aliases);
                foreach (var n in names)
                {
                    string k = s.ParentCode + "|" + Key(n);
                    if (k.Length > s.ParentCode.Length + 1 && !stateByKey.ContainsKey(k)) stateByKey[k] = s;
                }
            }
            foreach (var p in Places)
                foreach (var n in p.Names)
                {
                    string k = p.Country + "|" + Key(n);
                    if (!placesByKey.TryGetValue(k, out var list)) placesByKey[k] = list = new List<GeoPlace>(1);
                    if (!list.Contains(p)) list.Add(p);
                }
        }

        /// <summary>Spellings RadioID.net (and people) use that Natural Earth doesn't list.</summary>
        static readonly KeyValuePair<string, string>[] CountryAliases =
        {
            A("USA", "US"), A("U.S.A.", "US"), A("United States", "US"), A("America", "US"),
            A("UK", "GB"), A("Great Britain", "GB"), A("England", "GB"), A("Scotland", "GB"), A("Wales", "GB"), A("Northern Ireland", "GB"),
            A("Korea, Republic of", "KR"), A("Republic of Korea", "KR"), A("Korea", "KR"), A("Russian Federation", "RU"),
            A("Czech Republic", "CZ"), A("Czechia", "CZ"), A("Holland", "NL"), A("The Netherlands", "NL"), A("Deutschland", "DE"),
            A("Viet Nam", "VN"), A("Taiwan, Province of China", "TW"), A("Iran, Islamic Republic of", "IR"),
            A("Macedonia", "MK"), A("North Macedonia", "MK"), A("Slovak Republic", "SK"), A("Moldova, Republic of", "MD"),
            A("Brasil", "BR"), A("Espana", "ES"), A("Italia", "IT"), A("Polska", "PL"), A("Turkiye", "TR"), A("Osterreich", "AT"),
            A("Schweiz", "CH"), A("Suisse", "CH"), A("Belgique", "BE"), A("Danmark", "DK"), A("Norge", "NO"), A("Sverige", "SE"),
            A("Suomi", "FI"), A("Hrvatska", "HR"), A("Eire", "IE"), A("Republic of Ireland", "IE"),
        };

        static KeyValuePair<string, string> A(string name, string code) { return new KeyValuePair<string, string>(name, code); }

        public GeoArea Find(string code)
        {
            return code != null && byCode.TryGetValue(code, out var a) ? a : null;
        }

        /// <summary>A country by any common name or its ISO code ("United States", "USA", "US", "Deutschland").</summary>
        public GeoArea FindCountry(string name)
        {
            return countryByName.TryGetValue(Key(name), out var c) ? c : null;
        }

        /// <summary>A state/province of <paramref name="country"/> by name, alias or code. "A / B" tries each part.</summary>
        public GeoArea FindState(GeoArea country, string name)
        {
            if (country == null) return null;
            foreach (var part in Parts(name))
            {
                string k = Key(part);
                if (k.Length > 0 && stateByKey.TryGetValue(country.Code + "|" + k, out var s)) return s;
            }
            return null;
        }

        /// <summary>The area of the given level that contains the point, searching inside <paramref name="within"/> when given.</summary>
        public GeoArea AreaAt(double lon, double lat, AreaLevel level, GeoArea within = null)
        {
            IEnumerable<GeoArea> pool;
            if (within != null && within.Level < level)
            {
                pool = within.Children;
                if (level == AreaLevel.County && within.Level == AreaLevel.Country)
                    pool = within.Children.SelectMany(s => s.Children);
            }
            else pool = level == AreaLevel.Country ? Countries : level == AreaLevel.State ? States : Counties;
            foreach (var a in pool)
                if (a.Contains(lon, lat)) return a;
            return null;
        }

        /// <summary>The area nearest to the point among <paramref name="pool"/> (by label point), for points that land just outside every simplified outline.</summary>
        static GeoArea Nearest(IEnumerable<GeoArea> pool, double lon, double lat, double maxDegrees)
        {
            GeoArea best = null;
            double bestD = maxDegrees * maxDegrees;
            foreach (var a in pool)
            {
                double cx = Math.Max(a.MinLon, Math.Min(a.MaxLon, lon)), cy = Math.Max(a.MinLat, Math.Min(a.MaxLat, lat));
                double d = (cx - lon) * (cx - lon) + (cy - lat) * (cy - lat);
                if (d < bestD) { bestD = d; best = a; }
            }
            return best;
        }

        /// <summary>A place called <paramref name="city"/> in the country (and state, when known). "A/B", "A, TX" and "A - north" try each part.</summary>
        public GeoPlace FindPlace(GeoArea country, GeoArea state, string city)
        {
            if (country == null) return null;
            foreach (var part in Parts(city))
            {
                string k = Key(part);
                if (k.Length < 2) continue;
                if (!placesByKey.TryGetValue(country.Code + "|" + k, out var list)) continue;
                IEnumerable<GeoPlace> candidates = list;
                if (state != null)
                {
                    string admin = country.Code == "US" && state.Code.StartsWith("US-") ? state.Code.Substring(3) : null;
                    var inState = admin != null ? list.Where(p => p.Admin == admin).ToList() : list.Where(p => state.Contains(p.Lon, p.Lat)).ToList();
                    if (inState.Count > 0) candidates = inState;
                    else if (admin != null) continue; // a US city of that name, but in another state
                }
                return candidates.OrderByDescending(p => p.Weight).First();
            }
            return null;
        }

        /// <summary>Where a RadioID.net listing is: country, state/province, US county and a point when the city is known.</summary>
        public GeoLocation Locate(string city, string state, string country)
        {
            var c = FindCountry(country);
            var s = c != null ? FindState(c, state) : null;
            if (c == null)
            {
                // Some listings leave the country blank; a US state name is a safe guess.
                var us = Find("US");
                s = FindState(us, state);
                if (s != null) c = us;
            }
            if (c == null) return GeoLocation.Unknown;
            var loc = new GeoLocation { Country = c, State = s, Precision = s != null ? LocationPrecision.State : LocationPrecision.Country };
            var place = FindPlace(c, s, city);
            if (place == null) return loc;
            loc.Lat = place.Lat;
            loc.Lon = place.Lon;
            loc.Precision = LocationPrecision.City;
            var byPoint = AreaAt(place.Lon, place.Lat, AreaLevel.State, c) ?? Nearest(c.Children, place.Lon, place.Lat, 0.1);
            if (byPoint != null) loc.State = byPoint;
            if (loc.State != null && loc.State.Children.Count > 0)
                loc.County = AreaAt(place.Lon, place.Lat, AreaLevel.County, loc.State) ?? Nearest(loc.State.Children, place.Lon, place.Lat, 0.05);
            return loc;
        }

        static IEnumerable<string> Parts(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) yield break;
            yield return t;
            var pieces = t.Split(new[] { "/", ",", " - ", "(", ")", ";", " & ", " and " }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(p => p.Trim()).Where(p => p.Length > 1).ToList();
            if (pieces.Count > 1 || (pieces.Count == 1 && pieces[0] != t))
                foreach (var p in pieces) yield return p;
        }

        /// <summary>
        /// Comparison key for names: accents removed, lower case, punctuation as spaces, the usual
        /// abbreviations folded ("Saint" = "St.", "Mount" = "Mt", "Fort" = "Ft") and "ue" = "u" (umlauts).
        /// Only ever compared with other keys, so the folding just has to be the same on both sides.
        /// </summary>
        public static string Key(string s)
        {
            string t = Naming.Fold(s).ToLowerInvariant().Replace("&", " and ");
            var sb = new StringBuilder(t.Length);
            foreach (char ch in t) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            var words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                switch (words[i])
                {
                    case "saint": words[i] = "st"; break;
                    case "sainte": words[i] = "ste"; break;
                    case "mount": words[i] = "mt"; break;
                    case "fort": words[i] = "ft"; break;
                }
            }
            if (words.Count > 1 && words[0] == "the") words.RemoveAt(0);
            // German umlauts are often typed as ae/oe/ue ("Tangermuende"); fold those the same way as the accent.
            return string.Join(" ", words).Replace("ae", "a").Replace("oe", "o").Replace("ue", "u");
        }
    }
}
