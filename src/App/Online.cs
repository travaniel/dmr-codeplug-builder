using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeplugBuilder.Core;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.App
{
    /// <summary>Downloads from RadioID.net and BrandMeister (parsing lives in Core/OnlineData.cs).</summary>
    static partial class Online
    {
        const string UserAgent = RepeaterBookApi.UserAgent; // the same string everywhere, as registered with RepeaterBook
        static Dictionary<int, string> bmCache;

        static string BmCacheFile => Path.Combine(AppSettings.Folder, "brandmeister-talkgroups.json");

        static Online()
        {
            // .NET 4.8 on Windows 10/11 already negotiates TLS 1.2+; this only matters on older systems.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            // .NET Framework allows 2 connections per host by default; talkgroup lookups run 6 at a time.
            if (ServicePointManager.DefaultConnectionLimit < 8) ServicePointManager.DefaultConnectionLimit = 8;
        }

        public static string Get(string url, int timeoutMs = 30000)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Accept = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                string host = new Uri(url).Host;
                if (http != null) throw new Exception(host + " answered " + (int)http.StatusCode + " " + http.StatusDescription + ".", ex);
                throw new Exception("Couldn't reach " + host + ". Check your internet connection.\n(" + ex.Message + ")", ex);
            }
        }

        /// <summary>Every repeater RadioID.net lists for a state or province (all pages).</summary>
        public static Task<List<OnlineRepeater>> RepeatersAsync(string state, IProgress<string> progress)
        {
            return Task.Run(() =>
            {
                var all = new List<OnlineRepeater>();
                int pages = 1;
                for (int page = 1; page <= pages && page <= 50; page++)
                {
                    progress?.Report("Downloading " + state + " from RadioID.net" + (pages > 1 ? " (page " + page + " of " + pages + ")" : "") + "...");
                    var p = RadioId.ParseRepeaters(Get(RadioId.RepeaterUrl(state, page)));
                    pages = Math.Max(1, p.Pages);
                    all.AddRange(p.Repeaters);
                }
                return all;
            });
        }

        public static Task<List<RadioIdUser>> LookupUserAsync(string callsign)
        {
            return Task.Run(() => RadioId.ParseUsers(Get(RadioId.UserUrl(callsign))));
        }

        /// <summary>
        /// BrandMeister talkgroup names (id → name). Downloaded at most once a week and kept in the settings
        /// folder, so it still works offline. Returns an empty map if neither the download nor the cache works.
        /// </summary>
        public static Task<Dictionary<int, string>> BrandMeisterNamesAsync(bool forceRefresh = false)
        {
            return Task.Run(() =>
            {
                if (bmCache != null && !forceRefresh) return bmCache;
                string file = BmCacheFile;
                bool fresh = File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays < 7;
                if (fresh && !forceRefresh)
                {
                    try { return bmCache = BrandMeister.ParseTalkgroups(File.ReadAllText(file, Encoding.UTF8)); } catch { }
                }
                try
                {
                    string json = Get(BrandMeister.TalkgroupUrl);
                    var map = BrandMeister.ParseTalkgroups(json);
                    if (map.Count > 0)
                    {
                        try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(file, json, Encoding.UTF8); } catch { }
                        return bmCache = map;
                    }
                }
                catch
                {
                    if (File.Exists(file))
                        try { return bmCache = BrandMeister.ParseTalkgroups(File.ReadAllText(file, Encoding.UTF8)); } catch { }
                }
                return new Dictionary<int, string>();
            });
        }

        /// <summary>
        /// Repeater positions from RadioID.net's DMR-MARC map (DMR ID → lat, lon). Downloaded at most once a week and kept
        /// in the settings folder as "id,lat,lon" lines (~200 KB). Returns an empty map if neither works; repeaters are
        /// then placed by city name as before.
        /// </summary>
        public static Task<Dictionary<int, double[]>> RepeaterPositionsAsync()
        {
            return Task.Run(() =>
            {
                lock (positionsLock)
                {
                    if (positions != null) return positions;
                    string file = Path.Combine(AppSettings.Folder, "radioid-positions.txt");
                    bool fresh = File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays < 7;
                    if (fresh && (positions = ReadPositions(file)).Count > 0) return positions;
                    try
                    {
                        var map = RadioId.ParseMapPositions(Get(RadioId.MapUrl, 60000));
                        if (map.Count > 0)
                        {
                            try
                            {
                                Directory.CreateDirectory(AppSettings.Folder);
                                File.WriteAllLines(file, map.Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture) + "," +
                                    kv.Value[0].ToString("R", CultureInfo.InvariantCulture) + "," + kv.Value[1].ToString("R", CultureInfo.InvariantCulture)), new UTF8Encoding(false));
                            }
                            catch { }
                            return positions = map;
                        }
                    }
                    catch { }
                    return positions = File.Exists(file) ? ReadPositions(file) : new Dictionary<int, double[]>();
                }
            });
        }

        /// <summary>
        /// BrandMeister's device list: its repeaters placed on the map, and every device ID heard in the last day (for
        /// <see cref="RepeaterHealth"/>). The 9.6 MB list is downloaded at most once a day and kept in the settings folder;
        /// the parsed result is kept for the run. Empty if neither works.
        /// </summary>
        public static Task<BrandMeisterDevices> BrandMeisterDevicesAsync(GeoAtlas atlas)
        {
            return Task.Run(() =>
            {
                lock (bmDevicesLock)
                {
                    if (bmDevices != null) return bmDevices;
                    string file = Path.Combine(AppSettings.Folder, "brandmeister-devices.json");
                    bool fresh = File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalHours < 24;
                    if (fresh)
                    {
                        try { return bmDevices = BrandMeister.ParseDevices(File.ReadAllText(file, Encoding.UTF8), atlas); } catch { }
                    }
                    try
                    {
                        string json = Get(BrandMeister.DeviceUrl, 120000);
                        var devices = BrandMeister.ParseDevices(json, atlas);
                        if (devices.Ids.Count > 0)
                        {
                            try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(file, json, new UTF8Encoding(false)); } catch { }
                            return bmDevices = devices;
                        }
                    }
                    catch { }
                    // An old copy still gives the repeaters, but not who is on the air now: no health check from it.
                    try
                    {
                        if (File.Exists(file))
                        {
                            var old = BrandMeister.ParseDevices(File.ReadAllText(file, Encoding.UTF8), atlas);
                            old.Ids.Clear();
                            return bmDevices = old;
                        }
                    }
                    catch { }
                    return new BrandMeisterDevices(); // not cached, so the next download tries again
                }
            });
        }

        static readonly object bmDevicesLock = new object();
        static BrandMeisterDevices bmDevices;

        /// <summary>
        /// When BrandMeister last heard each device (<see cref="BrandMeister.DeviceInfoUrl"/>), null for IDs it doesn't
        /// know. 6 requests at a time within BrandMeister's rate limit (<see cref="GetBrandMeister"/>); answers are kept a
        /// week in the settings folder (a repeater a year off the air doesn't change much in a week). IDs that couldn't be
        /// asked are left out of the result. <paramref name="priority"/>: the user is waiting (picked repeaters).
        /// </summary>
        public static Dictionary<int, DateTime?> LastSeen(IEnumerable<int> ids, bool priority, IProgress<ReadProgress> progress, CancellationToken token)
        {
            var result = new Dictionary<int, DateTime?>();
            var cache = DeviceCache("brandmeister-last-seen.txt", 7);
            var fetched = new Dictionary<int, string>();
            var todo = new List<int>();
            foreach (int id in ids.Distinct())
            {
                if (cache.TryGetValue(id, out string v)) result[id] = ParseCachedDate(v);
                else todo.Add(id);
            }
            if (todo.Count == 0) return result;
            int done = 0;
            progress?.Report(new ReadProgress { Done = 0, Total = todo.Count, What = "checking which repeaters BrandMeister still hears" });
            try
            {
                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = token }, id =>
                {
                    string v = null;
                    try
                    {
                        var seen = BrandMeister.ParseLastSeen(GetBrandMeister(BrandMeister.DeviceInfoUrl(id), priority, token));
                        v = seen.HasValue ? seen.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex.Message.Contains(" 404 ")) { v = ""; } // BrandMeister doesn't know the ID
                    catch { }
                    lock (fetched)
                    {
                        if (v != null) { fetched[id] = v; result[id] = ParseCachedDate(v); }
                        done++;
                        progress?.Report(new ReadProgress { Done = done, Total = todo.Count, What = "checking which repeaters BrandMeister still hears" });
                    }
                });
            }
            catch (OperationCanceledException) { }
            SaveDeviceCache("brandmeister-last-seen.txt", fetched);
            return result;
        }

        /// <summary>
        /// The static talkgroups of each device (<see cref="BrandMeister.StaticTalkgroupUrl"/>); an empty list when it has
        /// none. 6 requests at a time (with priority), kept a day. IDs that couldn't be asked are left out of the result.
        /// </summary>
        public static Dictionary<int, List<OnlineTalkgroup>> StaticTalkgroups(IEnumerable<int> ids, IProgress<ReadProgress> progress, CancellationToken token)
        {
            var result = new Dictionary<int, List<OnlineTalkgroup>>();
            var cache = DeviceCache("brandmeister-static-talkgroups.txt", 1);
            var fetched = new Dictionary<int, string>();
            var todo = new List<int>();
            foreach (int id in ids.Distinct())
            {
                if (cache.TryGetValue(id, out string v)) result[id] = ParseCachedTalkgroups(v);
                else todo.Add(id);
            }
            if (todo.Count == 0) return result;
            int done = 0;
            progress?.Report(new ReadProgress { Done = 0, Total = todo.Count, What = "getting talkgroups from BrandMeister" });
            try
            {
                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = token }, id =>
                {
                    List<OnlineTalkgroup> list = null;
                    try { list = BrandMeister.ParseStaticTalkgroups(GetBrandMeister(BrandMeister.StaticTalkgroupUrl(id), true, token)); }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    lock (fetched)
                    {
                        if (list != null)
                        {
                            fetched[id] = string.Join(";", list.Select(t => t.Id.ToString(CultureInfo.InvariantCulture) + ":" + t.Slot.ToString(CultureInfo.InvariantCulture)));
                            result[id] = list;
                        }
                        done++;
                        progress?.Report(new ReadProgress { Done = done, Total = todo.Count, What = "getting talkgroups from BrandMeister" });
                    }
                });
            }
            catch (OperationCanceledException) { }
            SaveDeviceCache("brandmeister-static-talkgroups.txt", fetched);
            return result;
        }

        /// <summary>
        /// Gets picked repeaters ready to go into a project: the BrandMeister-only ones not checked yet are checked
        /// (<see cref="RepeaterHealth"/>: an off-air one keeps its pick but is marked, so the project warns), then the ones on
        /// the air get BrandMeister's static talkgroups (<see cref="RepeaterHealth.UseStaticTalkgroups"/>); ones BrandMeister
        /// can't answer for keep their RadioID.net list. Runs on a worker thread; returns how many got BrandMeister's talkgroups.
        /// </summary>
        public static int PrepareForAdding(IEnumerable<OnlineRepeater> picked, IProgress<ReadProgress> progress, CancellationToken token = default(CancellationToken))
        {
            var list = picked.ToList();
            var health = HealthUnknown(list);
            if (health.Count > 0)
            {
                var seen = LastSeen(health.Select(r => r.BrandMeisterDeviceId), true, progress, token);
                var now = DateTime.UtcNow;
                foreach (var r in health)
                    if (seen.TryGetValue(r.BrandMeisterDeviceId, out var last)) { RepeaterHealth.Apply(r, last, now); MarkChecked(r); }
            }
            var ask = list.Where(StaticTalkgroupsUnknown).ToList();
            if (ask.Count == 0) return 0;
            var statics = StaticTalkgroups(ask.Select(r => r.BrandMeisterDeviceId), progress, token);
            int changed = 0;
            foreach (var r in ask)
                if (statics.TryGetValue(r.BrandMeisterDeviceId, out var tgs))
                {
                    lock (staticAsked) staticAsked.Add(r.BrandMeisterDeviceId);
                    if (RepeaterHealth.UseStaticTalkgroups(r, tgs)) changed++;
                }
            return changed;
        }

        /// <summary>True when <see cref="PrepareForAdding"/> has anything to ask BrandMeister (so the UI can skip its wait dialog).</summary>
        public static bool NeedsPreparing(IEnumerable<OnlineRepeater> picked)
        {
            var list = picked.ToList();
            return list.Any(StaticTalkgroupsUnknown) || HealthUnknown(list).Count > 0;
        }

        /// <summary>BrandMeister IDs whose static talkgroups were already asked for in this run (an empty answer leaves the listing as it was).</summary>
        static readonly HashSet<int> staticAsked = new HashSet<int>();

        static bool StaticTalkgroupsUnknown(OnlineRepeater r)
        {
            if (r.IsAnalog || r.IsOffAir || !r.OnlyBrandMeister || r.TalkgroupSource != null || r.BrandMeisterDeviceId <= 0) return false;
            lock (staticAsked) return !staticAsked.Contains(r.BrandMeisterDeviceId);
        }

        /// <summary>Listings whose health was checked in this run (by the region download or <see cref="PrepareForAdding"/>).</summary>
        static readonly HashSet<OnlineRepeater> healthChecked = new HashSet<OnlineRepeater>();

        public static void MarkChecked(OnlineRepeater r) { lock (healthChecked) healthChecked.Add(r); }

        /// <summary>Health candidates among these listings (<see cref="RepeaterHealth.Candidates"/>) not checked yet in this run.</summary>
        public static List<OnlineRepeater> HealthUnknown(IEnumerable<OnlineRepeater> listings)
        {
            var live = bmDevices;
            if (live == null) return new List<OnlineRepeater>();
            lock (healthChecked) return RepeaterHealth.Candidates(listings, live).Where(r => !healthChecked.Contains(r)).ToList();
        }

        // BrandMeister allows 120 API requests a minute per address (x-ratelimit-limit, checked 2026-10-08) and answers 429
        // after that. Background checks stop at BackgroundPerMinute so what the user waits for (priority) always has room.
        const int PriorityPerMinute = 100, BackgroundPerMinute = 70;
        static readonly Queue<DateTime> bmRequests = new Queue<DateTime>();

        static void WaitForBrandMeister(bool priority, CancellationToken token)
        {
            int limit = priority ? PriorityPerMinute : BackgroundPerMinute;
            while (true)
            {
                TimeSpan wait;
                lock (bmRequests)
                {
                    var now = DateTime.UtcNow;
                    while (bmRequests.Count > 0 && now - bmRequests.Peek() >= TimeSpan.FromMinutes(1)) bmRequests.Dequeue();
                    if (bmRequests.Count < limit) { bmRequests.Enqueue(now); return; }
                    // The oldest request in the window that has to drop out before there's room again.
                    wait = bmRequests.ElementAt(bmRequests.Count - limit) + TimeSpan.FromMinutes(1) - now + TimeSpan.FromMilliseconds(50);
                }
                if (token.WaitHandle.WaitOne(wait < TimeSpan.Zero ? TimeSpan.Zero : wait)) token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// A BrandMeister API request within the rate limit. A 500 (it happens now and then) is tried again after a second, a
        /// 429 after a minute; a 404 is passed on.
        /// </summary>
        static string GetBrandMeister(string url, bool priority, CancellationToken token)
        {
            for (int attempt = 1; ; attempt++)
            {
                WaitForBrandMeister(priority, token);
                try { return Get(url, 15000); }
                catch (Exception ex) when (attempt < 3 && !ex.Message.Contains(" 404 "))
                {
                    bool limited = ex.Message.Contains(" 429 ");
                    if (token.WaitHandle.WaitOne(limited ? TimeSpan.FromSeconds(61) : TimeSpan.FromSeconds(1))) token.ThrowIfCancellationRequested();
                }
            }
        }

        static readonly object deviceCacheLock = new object();

        /// <summary>"id,checked yyyy-MM-dd,value" lines in the settings folder; entries older than <paramref name="days"/> are dropped.</summary>
        static Dictionary<int, string> DeviceCache(string name, int days)
        {
            var map = new Dictionary<int, string>();
            lock (deviceCacheLock)
            {
                try
                {
                    string file = Path.Combine(AppSettings.Folder, name);
                    if (!File.Exists(file)) return map;
                    foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        var f = line.Split(new[] { ',' }, 3);
                        if (f.Length == 3 && int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) &&
                            DateTime.TryParseExact(f[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) &&
                            (DateTime.Now.Date - at).TotalDays < days)
                            map[id] = f[2];
                    }
                }
                catch { }
            }
            return map;
        }

        /// <summary>Stores what was just asked (<paramref name="fetched"/>) as checked today; other entries keep their date, and ones over 60 days old go.</summary>
        static void SaveDeviceCache(string name, Dictionary<int, string> fetched)
        {
            if (fetched.Count == 0) return;
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (deviceCacheLock)
            {
                try
                {
                    string file = Path.Combine(AppSettings.Folder, name);
                    var lines = new Dictionary<int, string>();
                    if (File.Exists(file))
                        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                        {
                            var f = line.Split(new[] { ',' }, 3);
                            if (f.Length == 3 && int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) &&
                                DateTime.TryParseExact(f[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) &&
                                (DateTime.Now.Date - at).TotalDays < 60)
                                lines[id] = line;
                        }
                    foreach (var kv in fetched) lines[kv.Key] = kv.Key.ToString(CultureInfo.InvariantCulture) + "," + today + "," + kv.Value;
                    Directory.CreateDirectory(AppSettings.Folder);
                    File.WriteAllLines(file, lines.OrderBy(kv => kv.Key).Select(kv => kv.Value), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        static DateTime? ParseCachedDate(string v)
        {
            return DateTime.TryParseExact(v ?? "", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }

        static List<OnlineTalkgroup> ParseCachedTalkgroups(string v)
        {
            var list = new List<OnlineTalkgroup>();
            foreach (string part in (v ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(':');
                if (f.Length == 2 && int.TryParse(f[0], out int id) && int.TryParse(f[1], out int slot)) list.Add(new OnlineTalkgroup { Id = id, Slot = slot, Description = "" });
            }
            return list;
        }

        static readonly object positionsLock = new object();
        static Dictionary<int, double[]> positions;

        static Dictionary<int, double[]> ReadPositions(string file)
        {
            var map = new Dictionary<int, double[]>();
            try
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    var f = line.Split(',');
                    if (f.Length == 3 && int.TryParse(f[0], out int id) &&
                        double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                        map[id] = new[] { lat, lon };
                }
            }
            catch { }
            return map;
        }

        /// <summary>
        /// Names for the talkgroups on these listings that BrandMeister and the owners don't name
        /// (<see cref="TalkgroupNames"/>): first from the listings themselves, then by looking the ID up on
        /// RadioID.net (a 7-digit ID as a user: "N0FTW TG"; a shorter one as a repeater: "W5LOS Luling"). Lookups are
        /// cached in the settings folder for 30 days; at most 150 per call, 6 at a time. Failures leave "TG 1234".
        /// </summary>
        public static Dictionary<int, string> NameTalkgroups(IEnumerable<OnlineRepeater> listings, IDictionary<int, string> bmNames, IProgress<string> progress, CancellationToken token)
        {
            var list = listings.ToList();
            var unnamed = TalkgroupNames.Unnamed(list, bmNames);
            var names = TalkgroupNames.FromListings(list, unnamed);
            var ask = unnamed.Where(id => !names.ContainsKey(id)).OrderBy(id => id).ToList();
            if (ask.Count == 0) return names;

            lock (lookupLock) LoadLookups();
            var todo = new List<int>();
            lock (lookupLock)
                foreach (int id in ask)
                {
                    if (!lookups.TryGetValue(id, out string known)) todo.Add(id);
                    else if (known.Length > 0) names[id] = known;
                }
            if (todo.Count > 150) todo = todo.Take(150).ToList();
            if (todo.Count > 0)
            {
                progress?.Report("Looking up " + todo.Count + " talkgroup name" + (todo.Count == 1 ? "" : "s") + " on RadioID.net...");
                try
                {
                    Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = token }, id =>
                    {
                        string name = null; // null: couldn't ask (not cached); "": RadioID doesn't know it
                        try
                        {
                            if (id >= 1000000)
                            {
                                var u = RadioId.ParseUsers(Get(RadioId.UserIdUrl(id), 10000)).FirstOrDefault(x => x.Id == id);
                                name = u != null && u.Callsign.Length > 0 ? TalkgroupNames.ForUser(u) : "";
                            }
                            else
                            {
                                var r = RadioId.ParseRepeaters(Get(RadioId.RepeaterIdUrl(id), 10000)).Repeaters.FirstOrDefault(x => x.DmrId == id);
                                name = r != null && r.Callsign.Length > 0 ? TalkgroupNames.ForRepeater(r.Callsign, r.City) : "";
                            }
                        }
                        catch { }
                        if (name == null) return;
                        lock (lookupLock)
                        {
                            lookups[id] = name;
                            if (name.Length > 0) names[id] = name;
                        }
                    });
                }
                catch (OperationCanceledException) { }
                lock (lookupLock) SaveLookups();
            }
            return names;
        }

        static readonly object lookupLock = new object();
        static Dictionary<int, string> lookups;
        static string LookupFile => Path.Combine(AppSettings.Folder, "talkgroup-lookups.txt");

        static void LoadLookups()
        {
            if (lookups != null) return;
            lookups = new Dictionary<int, string>();
            try
            {
                if (!File.Exists(LookupFile) || (DateTime.Now - File.GetLastWriteTime(LookupFile)).TotalDays > 30) return;
                foreach (string line in File.ReadAllLines(LookupFile, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && int.TryParse(line.Substring(0, eq), out int id)) lookups[id] = line.Substring(eq + 1);
                }
            }
            catch { }
        }

        static void SaveLookups()
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllLines(LookupFile, lookups.OrderBy(kv => kv.Key).Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture) + "=" + kv.Value), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
