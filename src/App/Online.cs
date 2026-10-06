using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Downloads from RadioID.net and BrandMeister (parsing lives in Core/OnlineData.cs).</summary>
    static class Online
    {
        const string UserAgent = "DMRCodeplugBuilder/1.2 (Windows; BTECH DMR-6X2 PRO codeplug tool)";
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

        /// <summary>
        /// Asks for a callsign, looks it up on RadioID.net and fills in the project's DMR ID and Radio ID name.
        /// Returns true when the project changed.
        /// </summary>
        public static async Task<bool> LookUpRadioIdAsync(IWin32Window owner, Session session)
        {
            var p = session.Project;
            string guess = "";
            var words = (p.RadioIdName ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0) guess = words[words.Length - 1];
            string call = Prompt.Show(owner, "Look up your DMR ID", "Your callsign (looked up on RadioID.net):", guess, 10);
            if (string.IsNullOrWhiteSpace(call)) return false;
            call = call.Trim().ToUpperInvariant();

            List<RadioIdUser> users;
            Cursor.Current = Cursors.WaitCursor;
            try { users = await LookupUserAsync(call); }
            catch (Exception ex) { Ui.Error(owner, "Couldn't look up " + call + ":\n\n" + ex.Message); return false; }
            finally { Cursor.Current = Cursors.Default; }

            if (users.Count == 0)
            {
                Ui.Error(owner, "RadioID.net has no DMR ID for " + call + ".\n\nIf you don't have one yet, register at radioid.net (it's free), then try again.");
                return false;
            }
            var u = users[0];
            string name = u.SuggestedName(p.Options.MaxNameLength);
            string others = users.Count > 1
                ? "\n\nYou have " + users.Count + " IDs: " + string.Join(", ", users.ConvertAll(x => x.Id.ToString())) + ". Using the first; change it on the Settings tab if needed."
                : "";
            if (!Ui.Confirm(owner, "Found " + u.Callsign + ": " + (u.FirstName + " " + u.LastName).Trim() + ", " + u.City + ", " + u.State +
                                   "\n\nDMR ID: " + u.Id + "\nRadio ID name: " + name + others +
                                   "\n\nUse these? Every channel will use this Radio ID name, and RadioIDList.CSV will contain it."))
                return false;
            p.RadioId = u.Id;
            p.RadioIdName = name;
            session.MarkDirty();
            return true;
        }
    }
}
