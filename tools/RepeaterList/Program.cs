using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using CodeplugBuilder.Core;

// Usage: dotnet run --project tools\RepeaterList -c Release -- <output folder> [cache folder]
// Merges RadioID.net's DMR-MARC repeater map with BrandMeister's repeater list (6-digit IDs only; 7+ digits are hotspots)
// and writes one CSV per US state (plus DC and territories), ordered by state.
static class Program
{
    sealed class Row
    {
        public string Callsign = "", City = "", State = "", County = "", Network = "", Sources = "", Status = "", Notes = "", Talkgroups = "";
        public decimal Out, In;
        public int ColorCode, BmId;
        public double Lat, Lon;
        public string LastSeen = "";
    }

    static double D(string s)
    {
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        return v;
    }

    static decimal M(string s)
    {
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v);
        return v;
    }

    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "repeaterlist";
        string cache = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "repeaterlist-cache");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(cache);
        var atlas = GeoAtlas.BuiltIn();

        string mapJson = Fetch("https://radioid.net/api/rptr/map/", Path.Combine(cache, "radioid-map.json"));
        string bmJson = Fetch("https://api.brandmeister.network/v2/device", Path.Combine(cache, "bm-device.json"));

        var rows = new List<Row>();
        int ridSeen = 0, ridUs = 0;
        foreach (var m in Json.Arr(Json.Get(Json.Parse(mapJson), "markers")))
        {
            ridSeen++;
            string country = Json.Str(Json.Get(m, "country"));
            double lat = D(Json.Str(Json.Get(m, "lat"))), lon = D(Json.Str(Json.Get(m, "lng")));
            var listed = atlas.Locate(Json.Str(Json.Get(m, "city")), Json.Str(Json.Get(m, "state")), country);
            var loc = atlas.LocateAt(lat, lon, listed.Country != null ? listed : null) ?? listed;
            if (loc.Country == null || loc.Country.Code != "US") continue;
            ridUs++;
            decimal freq = M(Json.Str(Json.Get(m, "frequency"))), off = M(Json.Str(Json.Get(m, "offset")));
            var tgs = new List<string>();
            foreach (var t in Json.Arr(Json.Get(m, "talkgroups")))
            {
                string s = t is Dictionary<string, object> ? Json.Str(Json.Get(t, "talkgroup")) : Json.Str(t);
                if (s.Length > 0) tgs.Add(s);
            }
            rows.Add(new Row
            {
                Callsign = Json.Str(Json.Get(m, "callsign")).Trim().ToUpperInvariant(),
                City = Json.Str(Json.Get(m, "city")).Trim(),
                State = loc.State != null ? loc.State.Name : Json.Str(Json.Get(m, "state")),
                County = loc.County != null ? loc.County.Name : "",
                Network = Networks.Normalize(Json.Str(Json.Get(m, "ipsc_network"))),
                Sources = "RadioID",
                Status = Json.Str(Json.Get(m, "status")),
                Out = freq, In = freq + off, ColorCode = Json.Int(Json.Get(m, "color_code")),
                Lat = lat, Lon = lon,
                Notes = Json.Str(Json.Get(m, "map_info")),
                Talkgroups = string.Join(" ", tgs),
            });
        }

        int bmSeen = 0, bmRepeaters = 0, bmUs = 0, bmMatched = 0;
        foreach (var d in Json.Arr(Json.Parse(bmJson)))
        {
            bmSeen++;
            int id = Json.Int(Json.Get(d, "id"));
            if (id < 100000 || id > 999999) continue; // hotspots and personal IDs are longer
            bmRepeaters++;
            double lat = D(Json.Str(Json.Get(d, "lat"))), lon = D(Json.Str(Json.Get(d, "lng")));
            var loc = atlas.LocateAt(lat, lon, null);
            if (loc == null || loc.Country == null || loc.Country.Code != "US") continue;
            bmUs++;
            decimal output = M(Json.Str(Json.Get(d, "tx"))), input = M(Json.Str(Json.Get(d, "rx")));
            string fullCall = Json.Str(Json.Get(d, "callsign")).Trim();
            string call = fullCall.Split(' ', '-')[0].ToUpperInvariant();
            string last = Json.Str(Json.Get(d, "last_seen"));
            var hit = rows.FirstOrDefault(r => r.BmId == 0 && r.Callsign == call && Math.Abs(r.Out - output) < 0.0125m);
            if (hit != null)
            {
                bmMatched++;
                hit.BmId = id; hit.Sources = "RadioID+BrandMeister"; hit.LastSeen = last;
                if (hit.Network.IndexOf("BrandMeister", StringComparison.OrdinalIgnoreCase) < 0) hit.Network = hit.Network.Length == 0 ? "BrandMeister" : hit.Network + "/BrandMeister";
                if (hit.ColorCode == 0) hit.ColorCode = Json.Int(Json.Get(d, "colorcode"));
                continue;
            }
            rows.Add(new Row
            {
                Callsign = call, City = Json.Str(Json.Get(d, "city")).Trim(),
                State = loc.State != null ? loc.State.Name : "", County = loc.County != null ? loc.County.Name : "",
                Network = "BrandMeister", Sources = "BrandMeister", BmId = id, LastSeen = last,
                Status = Json.Int(Json.Get(d, "status")) == 3 ? "connected" : "listed",
                Out = output, In = input, ColorCode = Json.Int(Json.Get(d, "colorcode")), Lat = lat, Lon = lon,
                Notes = fullCall != call ? fullCall : "",
            });
        }

        string[] header = { "State", "County", "City", "Callsign", "Output MHz", "Input MHz", "Offset MHz", "Color Code", "Network", "BrandMeister ID", "Source", "Status", "Last seen", "Latitude", "Longitude", "Talkgroups", "Notes" };
        var byState = rows.Where(r => r.State.Length > 0).GroupBy(r => r.State).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        int unplaced = rows.Count(r => r.State.Length == 0);
        foreach (var f in Directory.GetFiles(outDir, "*.csv")) File.Delete(f);
        var readme = new StringBuilder();
        int n = 0;
        foreach (var g in byState)
        {
            n++;
            var sb = new StringBuilder();
            sb.Append(string.Join(",", header.Select(Q))).Append("\r\n");
            foreach (var r in g.OrderBy(x => x.County, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.City, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Out))
                sb.Append(string.Join(",", new[]
                {
                    r.State, r.County, r.City, r.Callsign, F(r.Out), F(r.In), F(r.In - r.Out), r.ColorCode.ToString(CultureInfo.InvariantCulture),
                    r.Network, r.BmId == 0 ? "" : r.BmId.ToString(CultureInfo.InvariantCulture), r.Sources, r.Status, r.LastSeen,
                    r.Lat.ToString("0.#####", CultureInfo.InvariantCulture), r.Lon.ToString("0.#####", CultureInfo.InvariantCulture), r.Talkgroups, r.Notes,
                }.Select(Q))).Append("\r\n");
            File.WriteAllText(Path.Combine(outDir, n.ToString("00") + " " + g.Key + ".csv"), sb.ToString(), new UTF8Encoding(false));
            readme.Append(g.Key.PadRight(28) + g.Count().ToString().PadLeft(5) + "\r\n");
        }
        string summary = "US DMR repeaters, " + DateTime.Now.ToString("yyyy-MM-dd") + "\r\n" +
            "Sources: RadioID.net repeater map (" + ridUs + " US of " + ridSeen + ") and BrandMeister repeater list (" + bmUs + " US of " + bmRepeaters +
            " six-digit IDs out of " + bmSeen + " devices; hotspots excluded), " + bmMatched + " matched across both and merged.\r\n" +
            "Total: " + rows.Count + " repeaters" + (unplaced > 0 ? ", " + unplaced + " with no state (not written)" : "") + "\r\n\r\n";
        File.WriteAllText(Path.Combine(outDir, "README.txt"), summary + readme);
        Console.WriteLine(summary + readme);
        return 0;
    }

    static string F(decimal v) { return v.ToString("0.#####", CultureInfo.InvariantCulture); }
    static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }

    static string Fetch(string url, string cacheFile)
    {
        if (File.Exists(cacheFile) && DateTime.Now - File.GetLastWriteTime(cacheFile) < TimeSpan.FromHours(6)) return File.ReadAllText(cacheFile);
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DMR-Codeplug-Builder/1.3");
            string text = http.GetStringAsync(url).GetAwaiter().GetResult();
            File.WriteAllText(cacheFile, text);
            return text;
        }
    }
}
