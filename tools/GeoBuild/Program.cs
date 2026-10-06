using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// Builds the embedded map atlas (src/Core/Geo/atlas.gz) from the zips in tools/geodata (read as they are):
//   cb_2023_us_state_20m, cb_2023_us_county_20m     US Census cartographic boundaries (public domain)
//   2023_Gaz_place_national                          US Census gazetteer of places (public domain)
//   ne_10m_admin_0_countries, ne_10m_admin_1_...     Natural Earth (public domain)
//   cities15000                                      GeoNames (CC BY 4.0: credited in Help > About)
//
// Format (all inside one gzip stream, read by Core/Geo.cs GeoAtlas.Load):
//   "CPGEO" byte version(1)
//   3 area layers (countries, states/provinces, US counties): varint count, then per area
//     string code, string parent, string name, string aliases ("|"-joined),
//     varint rings, per ring: varint points, then zigzag-varint deltas of (lon, lat) in 1e-4 degrees.
//   places: varint count, per place: string country, string admin (US state code or ""),
//     string names ("|"-joined, first is the main one), zigzag-varint lat, lon (1e-4 degrees), varint weight.
// Strings are BinaryWriter strings (7-bit length + UTF-8).
static class Program
{
    const double CountryTolerance = 0.02, StateTolerance = 0.008, CountyTolerance = 0.003;
    const double Quantum = 10000.0;

    sealed class Area
    {
        public string Code = "", Parent = "", Name = "";
        public List<string> Aliases = new List<string>();
        public List<double[]> Rings = new List<double[]>();
    }

    sealed class Place
    {
        public string Country = "", Admin = "";
        public List<string> Names = new List<string>();
        public double Lat, Lon;
        public long Weight;
    }

    static int Main(string[] args)
    {
        string src = args.Length > 0 ? args[0] : "tools/geodata";
        string dst = args.Length > 1 ? args[1] : "src/Core/Geo/atlas.gz";
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // ---------- countries ----------
        var countries = new Dictionary<string, Area>(StringComparer.OrdinalIgnoreCase);
        var a3ToCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawCountry = new Dictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, rings) in Read(src, "ne_10m_admin_0_countries"))
        {
            string a3 = row["ADM0_A3"];
            if (a3 == "ATA") continue; // Antarctica: no repeaters, and it ruins a Mercator map
            string code = Good(row["ISO_A2_EH"]) ? row["ISO_A2_EH"] : Good(row["ISO_A2"]) ? row["ISO_A2"] : a3;
            a3ToCode[a3] = code;
            if (!countries.TryGetValue(code, out var c))
                countries[code] = c = new Area { Code = code, Name = row["NAME"] };
            foreach (var f in new[] { "NAME", "ADMIN", "NAME_LONG", "FORMAL_EN", "NAME_SORT", "NAME_EN", "BRK_NAME", "GEOUNIT", "SUBUNIT", "NAME_CIAWF" })
                AddAlias(c, row[f]);
            foreach (var part in row["NAME_ALT"].Split('|')) AddAlias(c, part);
            if (!rawCountry.TryGetValue(code, out var raw)) rawCountry[code] = raw = new List<double[]>();
            raw.AddRange(rings);
        }
        foreach (var c in countries.Values) c.Rings.AddRange(Outline(rawCountry[c.Code], CountryTolerance));
        Console.WriteLine("countries: " + countries.Count + "; without an outline: " +
                          string.Join(", ", countries.Values.Where(c => c.Rings.Count == 0).Select(c => c.Code + " " + c.Name + " (" + rawCountry[c.Code].Sum(r => r.Length / 2) + " raw points)")));

        // ---------- states / provinces ----------
        var states = new List<Area>();
        var usFipsToCode = new Dictionary<string, string>();
        foreach (var (row, rings) in Read(src, "cb_2023_us_state_20m"))
        {
            var s = new Area { Code = "US-" + row["STUSPS"], Parent = "US", Name = row["NAME"] };
            AddAlias(s, row["STUSPS"]);
            s.Rings.AddRange(Outline(rings, StateTolerance));
            usFipsToCode[row["STATEFP"]] = s.Code;
            states.Add(s);
        }
        var seenCodes = new HashSet<string>(states.Select(s => s.Code), StringComparer.OrdinalIgnoreCase);
        foreach (var (row, rings) in Read(src, "ne_10m_admin_1_states_provinces"))
        {
            string country = Good(row["iso_a2"]) ? row["iso_a2"] : a3ToCode.TryGetValue(row["adm0_a3"], out string cc) ? cc : "";
            if (country == "" || country == "US" || country == "AQ" || !countries.ContainsKey(country)) continue;
            string code = row["iso_3166_2"];
            if (!Good(code) || code.EndsWith("~") || seenCodes.Contains(code)) code = row["adm1_code"];
            if (seenCodes.Contains(code)) code = code + "-" + states.Count;
            seenCodes.Add(code);
            var s = new Area { Code = code, Parent = country, Name = Good(row["name"]) ? row["name"] : row["name_en"] };
            foreach (var f in new[] { "name", "name_en", "woe_name", "gn_name", "gns_name" }) AddAlias(s, row[f]);
            foreach (var part in row["name_alt"].Split('|')) AddAlias(s, part);
            s.Rings.AddRange(Outline(rings, StateTolerance));
            if (s.Rings.Count > 0) states.Add(s);
        }
        Console.WriteLine("states/provinces: " + states.Count);

        // ---------- US counties ----------
        var counties = new List<Area>();
        foreach (var (row, rings) in Read(src, "cb_2023_us_county_20m"))
        {
            if (!usFipsToCode.TryGetValue(row["STATEFP"], out string parent)) continue;
            var c = new Area { Code = "US-" + row["GEOID"], Parent = parent, Name = row["NAMELSAD"] };
            AddAlias(c, row["NAME"]);
            c.Rings.AddRange(Outline(rings, CountyTolerance));
            if (c.Rings.Count > 0) counties.Add(c);
        }
        Console.WriteLine("US counties: " + counties.Count);

        // ---------- places ----------
        var places = new List<Place>();
        var gaz = Lines(src, "2023_Gaz_place_national", "2023_Gaz_place_national.txt");
        var head = gaz[0].Split('\t').Select(h => h.Trim()).ToList();
        int iUsps = head.IndexOf("USPS"), iName = head.IndexOf("NAME"), iFunc = head.IndexOf("FUNCSTAT"), iLat = head.IndexOf("INTPTLAT"), iLon = head.IndexOf("INTPTLONG");
        foreach (var line in gaz.Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length <= iLon) continue;
            string name = UsPlaceName(f[iName]);
            if (name.Length == 0) continue;
            var p = new Place { Country = "US", Admin = f[iUsps].Trim(), Lat = Num(f[iLat]), Lon = Num(f[iLon]), Weight = f[iFunc].Trim() == "A" ? 2 : 1 };
            p.Names.Add(name);
            places.Add(p);
        }
        int usPlaces = places.Count;
        foreach (var line in Lines(src, "cities15000", "cities15000.txt"))
        {
            var f = line.Split('\t');
            if (f.Length < 15) continue;
            string country = f[8].Trim();
            if (country == "US" || country == "PR" || !countries.ContainsKey(country)) continue;
            var p = new Place { Country = country, Lat = Num(f[4]), Lon = Num(f[5]), Weight = long.TryParse(f[14], out long pop) ? pop : 0 };
            AddName(p.Names, f[2]);
            AddName(p.Names, f[1]);
            // Alternate names are every language's spelling with no language tag. Keep Latin-script ones, local
            // spellings first: names with accents ("Koln" from "Köln", "München"), then spellings close to the
            // English one ("Muenchen"), then the rest. Airport codes like "MUC" are dropped.
            string ascii = f[2].Trim().ToLowerInvariant();
            var alts = f[3].Split(',').Select(a => a.Trim())
                .Where(a => a.Length > 1 && !(a.Length <= 4 && a == a.ToUpperInvariant()) && !a.Any(char.IsDigit) && Latin(a))
                .OrderBy(a => a.Any(ch => ch > 127) ? 0 : a.Length >= 2 && ascii.StartsWith(a.Substring(0, 2).ToLowerInvariant()) ? 1 : 2)
                .ToList();
            foreach (var a in alts)
            {
                if (p.Names.Count >= 12) break;
                AddName(p.Names, a);
            }
            if (p.Names.Count > 0) places.Add(p);
        }
        Console.WriteLine("places: " + usPlaces + " US (Census) + " + (places.Count - usPlaces) + " world (GeoNames)");

        // ---------- write ----------
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst)));
        using (var file = File.Create(dst))
        using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(Encoding.ASCII.GetBytes("CPGEO"));
            w.Write((byte)1);
            foreach (var layer in new[] { countries.Values.OrderBy(c => c.Code).ToList(), states, counties })
            {
                VarInt(w, layer.Count);
                foreach (var a in layer)
                {
                    w.Write(a.Code); w.Write(a.Parent); w.Write(a.Name);
                    w.Write(string.Join("|", a.Aliases.Where(x => !string.Equals(x, a.Name, StringComparison.OrdinalIgnoreCase))));
                    VarInt(w, a.Rings.Count);
                    foreach (var ring in a.Rings)
                    {
                        VarInt(w, ring.Length / 2);
                        long px = 0, py = 0;
                        for (int i = 0; i < ring.Length; i += 2)
                        {
                            long x = (long)Math.Round(ring[i] * Quantum), y = (long)Math.Round(ring[i + 1] * Quantum);
                            ZigZag(w, x - px); ZigZag(w, y - py);
                            px = x; py = y;
                        }
                    }
                }
            }
            places.Sort((a, b) => a.Country != b.Country ? string.CompareOrdinal(a.Country, b.Country) : string.CompareOrdinal(a.Admin, b.Admin));
            VarInt(w, places.Count);
            foreach (var p in places)
            {
                w.Write(p.Country); w.Write(p.Admin); w.Write(string.Join("|", p.Names));
                ZigZag(w, (long)Math.Round(p.Lat * Quantum)); ZigZag(w, (long)Math.Round(p.Lon * Quantum));
                VarInt(w, p.Weight);
            }
        }
        int points = countries.Values.Concat(states).Concat(counties).Sum(a => a.Rings.Sum(r => r.Length / 2));
        Console.WriteLine("points: " + points + ", wrote " + dst + " (" + new FileInfo(dst).Length.ToString("N0") + " bytes)");
        WriteRoads(src, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dst)), "roads.gz"));
        return 0;
    }

    // ---------- roads ----------
    // roads.gz (read by Core/Geo.cs GeoRoad.Load): "CPROD" byte version(1); varint count; per road: byte class
    // (0 major highway, 1 secondary highway), string label ("I 35", "US 83", "A7"), varint parts, per part: varint points,
    // zigzag-varint deltas of (lon, lat) in 1e-4 degrees. From Natural Earth ne_10m_roads (public domain).
    const double RoadTolerance = 0.004;

    static void WriteRoads(string src, string dst)
    {
        if (!File.Exists(Path.Combine(src, "ne_10m_roads.zip"))) { Console.WriteLine("no ne_10m_roads.zip: roads skipped"); return; }
        var roads = new List<(int Class, string Label, List<double[]> Parts)>();
        var types = new Dictionary<string, int>();
        var usRoads = new List<string>();
        foreach (var (row, parts) in Read(src, "ne_10m_roads"))
        {
            string type = row["type"];
            types[type] = types.TryGetValue(type, out int n) ? n + 1 : 1;
            int cls = type == "Major Highway" ? 0 : type == "Secondary Highway" ? 1 : -1;
            if (cls < 0 || row["featurecla"] != "Road") continue;
            string label = (row["prefix"] + " " + row["label"]).Trim();
            if (label.Length == 0 && row["sov_a3"] == "USA")
            {
                // The US rows carry only the route number in "name"; "level" says whether it's an Interstate or a US route.
                string num = row["name"].Trim();
                string level = row["level"];
                label = num.Length == 0 || num.Length > 4 || !num.All(char.IsDigit) ? "" : level == "Interstate" ? "I-" + num : level == "Federal" ? "US " + num : "";
            }
            if (label == "-99" || label.Length > 14 || !label.Any(char.IsDigit)) label = ""; // "US" alone is no help
            var lines = Simplify(parts, RoadTolerance, true, 2).ToList();
            if (lines.Count > 0) roads.Add((cls, label, lines));
            if (row["sov_a3"] == "USA" && cls >= 0) usRoads.Add(label);
        }
        Console.WriteLine("US roads: " + usRoads.Count + ", " + usRoads.Count(l => l.Length > 0) + " labeled: " + string.Join(" | ", usRoads.Where(l => l.Length > 0).Distinct().Take(20)));
        Console.WriteLine("road types: " + string.Join(", ", types.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)));
        Console.WriteLine("roads kept: " + roads.Count(r => r.Class == 0) + " major, " + roads.Count(r => r.Class == 1) + " secondary; samples: " +
                          string.Join(", ", roads.Where(r => r.Label.Length > 0).Take(8).Select(r => r.Label)));
        using (var file = File.Create(dst))
        using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(Encoding.ASCII.GetBytes("CPROD"));
            w.Write((byte)1);
            VarInt(w, roads.Count);
            foreach (var road in roads)
            {
                w.Write((byte)road.Class);
                w.Write(road.Label);
                VarInt(w, road.Parts.Count);
                foreach (var line in road.Parts)
                {
                    VarInt(w, line.Length / 2);
                    long px = 0, py = 0;
                    for (int i = 0; i < line.Length; i += 2)
                    {
                        long x = (long)Math.Round(line[i] * Quantum), y = (long)Math.Round(line[i + 1] * Quantum);
                        ZigZag(w, x - px); ZigZag(w, y - py);
                        px = x; py = y;
                    }
                }
            }
        }
        Console.WriteLine("wrote " + dst + " (" + new FileInfo(dst).Length.ToString("N0") + " bytes)");
    }

    static bool Good(string s) { return !string.IsNullOrWhiteSpace(s) && s != "-99" && s != "-1"; }

    static double Num(string s) { return double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture); }

    /// <summary>Only names a RadioID listing could plausibly use: Latin script, reasonably short.</summary>
    static bool Latin(string s)
    {
        return s.Length > 0 && s.Length <= 40 && s.All(ch => ch < 0x250 || ch == '’');
    }

    static void AddAlias(Area a, string s)
    {
        s = (s ?? "").Trim();
        if (!Good(s) || !Latin(s) || s.Contains('|')) return;
        if (!a.Aliases.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) a.Aliases.Add(s);
    }

    static void AddName(List<string> names, string s)
    {
        s = (s ?? "").Trim();
        if (s.Length < 2 || !Latin(s) || s.Contains('|')) return;
        if (!names.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) names.Add(s);
    }

    static readonly string[] UsSuffixes =
    {
        " city and borough", " unified government", " consolidated government", " metropolitan government", " metro government",
        " urban county", " municipality", " comunidad", " zona urbana", " plantation", " corporation", " borough", " village",
        " city", " town", " CDP",
    };

    /// <summary>"San Angelo city" → "San Angelo"; "Nashville-Davidson metropolitan government (balance)" → "Nashville-Davidson".</summary>
    static string UsPlaceName(string raw)
    {
        string n = raw.Trim();
        int paren = n.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) n = n.Substring(0, paren);
        foreach (var suf in UsSuffixes)
            if (n.EndsWith(suf, StringComparison.Ordinal) && n.Length > suf.Length) { n = n.Substring(0, n.Length - suf.Length); break; }
        return n.Trim();
    }

    // ---------------- shapefile + dbf ----------------

    /// <summary>A file inside one of the downloaded zips (read straight from the zip; nothing is extracted).</summary>
    static byte[] FromZip(string src, string zipName, string entryName)
    {
        string zip = Path.Combine(src, zipName + ".zip");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException(entryName + " isn't in " + zip);
            using (var s = entry.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    static string[] Lines(string src, string zipName, string entryName)
    {
        return Encoding.UTF8.GetString(FromZip(src, zipName, entryName)).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    }

    static IEnumerable<(Dictionary<string, string>, List<double[]>)> Read(string src, string name)
    {
        var rows = ReadDbf(FromZip(src, name, name + ".dbf"));
        var shapes = ReadShp(FromZip(src, name, name + ".shp"));
        if (rows.Count != shapes.Count) throw new Exception(name + ": " + rows.Count + " records but " + shapes.Count + " shapes");
        for (int i = 0; i < rows.Count; i++) yield return (rows[i], shapes[i]);
    }

    static List<List<double[]>> ReadShp(byte[] b)
    {
        var result = new List<List<double[]>>();
        int pos = 100;
        while (pos + 8 <= b.Length)
        {
            int words = (b[pos + 4] << 24) | (b[pos + 5] << 16) | (b[pos + 6] << 8) | b[pos + 7];
            int c = pos + 8;
            pos = c + words * 2;
            int type = BitConverter.ToInt32(b, c);
            var rings = new List<double[]>();
            if (type == 3 || type == 13 || type == 23 || type == 5 || type == 15 || type == 25) // polylines (roads) and polygons share a layout
            {
                int parts = BitConverter.ToInt32(b, c + 36), count = BitConverter.ToInt32(b, c + 40);
                int partsAt = c + 44, pointsAt = partsAt + 4 * parts;
                for (int p = 0; p < parts; p++)
                {
                    int start = BitConverter.ToInt32(b, partsAt + 4 * p);
                    int end = p + 1 < parts ? BitConverter.ToInt32(b, partsAt + 4 * (p + 1)) : count;
                    var ring = new double[(end - start) * 2];
                    for (int i = start; i < end; i++)
                    {
                        ring[(i - start) * 2] = BitConverter.ToDouble(b, pointsAt + 16 * i);
                        ring[(i - start) * 2 + 1] = BitConverter.ToDouble(b, pointsAt + 16 * i + 8);
                    }
                    rings.Add(ring);
                }
            }
            result.Add(rings);
        }
        return result;
    }

    static List<Dictionary<string, string>> ReadDbf(byte[] b)
    {
        int n = BitConverter.ToInt32(b, 4), headerLen = BitConverter.ToUInt16(b, 8), recLen = BitConverter.ToUInt16(b, 10);
        var fields = new List<(string Name, int Len)>();
        for (int o = 32; b[o] != 0x0D; o += 32) fields.Add((Encoding.ASCII.GetString(b, o, 11).TrimEnd('\0'), b[o + 16]));
        var rows = new List<Dictionary<string, string>>(n);
        for (int r = 0; r < n; r++)
        {
            int at = headerLen + r * recLen + 1;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields)
            {
                row[f.Name] = Encoding.UTF8.GetString(b, at, f.Len).Trim().TrimEnd('\0');
                at += f.Len;
            }
            rows.Add(row);
        }
        return rows;
    }

    // ---------------- simplification ----------------

    /// <summary>Simplified rings; an area small enough to vanish entirely (Monaco, Vatican, atolls) is redone at a finer tolerance.</summary>
    static List<double[]> Outline(List<double[]> rings, double tolerance)
    {
        var result = Simplify(rings, tolerance, false).ToList();
        if (result.Count == 0) result = Simplify(rings, tolerance / 20, true).ToList();
        if (result.Count == 0) result = Simplify(rings, 0, true).ToList();
        return result;
    }

    static IEnumerable<double[]> Simplify(List<double[]> rings, double tolerance, bool keepSpecks, int minPoints = 4)
    {
        foreach (var ring in rings)
        {
            int n = ring.Length / 2;
            if (n < minPoints) continue;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                minX = Math.Min(minX, ring[2 * i]); maxX = Math.Max(maxX, ring[2 * i]);
                minY = Math.Min(minY, ring[2 * i + 1]); maxY = Math.Max(maxY, ring[2 * i + 1]);
            }
            if (!keepSpecks && maxX - minX < tolerance * 2 && maxY - minY < tolerance * 2) continue; // speck of an island
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            int far = 0;
            double best = -1;
            for (int i = 1; i < n; i++)
            {
                double dx = ring[2 * i] - ring[0], dy = ring[2 * i + 1] - ring[1], d = dx * dx + dy * dy;
                if (d > best) { best = d; far = i; }
            }
            keep[far] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, far));
            stack.Push((far, n - 1));
            double tol2 = tolerance * tolerance;
            while (stack.Count > 0)
            {
                var (a, z) = stack.Pop();
                if (z - a < 2) continue;
                int idx = -1;
                double max = tol2;
                for (int i = a + 1; i < z; i++)
                {
                    double d = SegDist2(ring, i, a, z);
                    if (d > max) { max = d; idx = i; }
                }
                if (idx < 0) continue;
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, z));
            }
            var outPts = new List<double>();
            long lx = long.MinValue, ly = long.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                long qx = (long)Math.Round(ring[2 * i] * Quantum), qy = (long)Math.Round(ring[2 * i + 1] * Quantum);
                if (qx == lx && qy == ly) continue;
                lx = qx; ly = qy;
                outPts.Add(qx / Quantum); outPts.Add(qy / Quantum);
            }
            if (outPts.Count / 2 >= minPoints) yield return outPts.ToArray();
        }
    }

    static double SegDist2(double[] r, int p, int a, int b)
    {
        double ax = r[2 * a], ay = r[2 * a + 1], bx = r[2 * b], by = r[2 * b + 1], px = r[2 * p], py = r[2 * p + 1];
        double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / len));
        double cx = ax + t * dx - px, cy = ay + t * dy - py;
        return cx * cx + cy * cy;
    }

    static void VarInt(BinaryWriter w, long v)
    {
        ulong u = (ulong)v;
        while (u >= 0x80) { w.Write((byte)(u | 0x80)); u >>= 7; }
        w.Write((byte)u);
    }

    static void ZigZag(BinaryWriter w, long v) { VarInt(w, (v << 1) ^ (v >> 63)); }
}
