using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
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
        }

        public static string Get(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Accept = "application/json";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
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
