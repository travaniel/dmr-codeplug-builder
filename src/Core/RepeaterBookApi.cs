using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>
    /// What this program promised RepeaterBook in its API application (approved 2026-10 as a distributed, non-commercial
    /// radio-programming integration): who it is, how it credits RepeaterBook, the limits on requests, and the export
    /// parsing. The client (App/Online.RepeaterBook.cs) uses each user's own token, never a shared one, only for imports
    /// the user starts, at most <see cref="MaxStatesPerAction"/> states at a time; results live in memory only.
    /// </summary>
    public static class RepeaterBookApi
    {
        /// <summary>
        /// The User-Agent RepeaterBook approved for this program (API Apps: "UA: ...", "Match: prefix"), sent on every
        /// RepeaterBook request. Must stay exactly this: don't change it with the program version.
        /// </summary>
        public const string UserAgent = "W6OZZ-CPS/1.3 (BTECH DMR-6X2 PRO codeplug tool; contact: austinth11@gmail.com)";
        /// <summary>The User-Agent for RadioID.net and BrandMeister (no email address: that is for RepeaterBook only).</summary>
        public const string OtherSitesUserAgent = "W6OZZ-CPS/1.3 (+https://github.com/travaniel/dmr-codeplug-builder)";
        public const string TokenHeader = "X-RB-App-Token";
        public const string SiteUrl = "https://www.repeaterbook.com";
        /// <summary>Shown wherever RepeaterBook data is shown or delivered, with a link to <see cref="SiteUrl"/>.</summary>
        public const string Attribution = "Data courtesy of RepeaterBook.com.";

        /// <summary>One request at a time, at least this many seconds apart.</summary>
        public const double MinSecondsBetweenRequests = 2;
        /// <summary>States fetched by one click of the user.</summary>
        public const int MaxStatesPerAction = 5;
        /// <summary>Requests per installation in any rolling hour.</summary>
        public const int MaxRequestsPerHour = 30;
        public const int MaxPagesPerState = 10;
        /// <summary>First wait after a 429 without a Retry-After; doubles each time it repeats.</summary>
        public const int DefaultBackoffSeconds = 60;
        public const int MaxBackoffSeconds = 3600;

        /// <summary>Seconds to wait after a 429: the server's Retry-After, else the previous wait doubled (first one 60), at most an hour.</summary>
        public static int BackoffSeconds(int previousSeconds, int? retryAfterSeconds)
        {
            int s = retryAfterSeconds ?? (previousSeconds <= 0 ? DefaultBackoffSeconds : previousSeconds * 2);
            return Math.Max(1, Math.Min(MaxBackoffSeconds, s));
        }

        /// <summary>Where each user makes their own token for this program (RepeaterBook > API Apps).</summary>
        public const string TokenPageUrl = "https://www.repeaterbook.com/user/api_apps.php";
        /// <summary>Tokens users make for an approved distributed app start with this.</summary>
        public const string UserTokenPrefix = "rbuapp_";

        /// <summary>
        /// The repeaters of one mode (<see cref="AnalogMode"/> or <see cref="DmrMode"/>) in one US state (FIPS
        /// <paramref name="stateFips"/>), North America endpoint. Only the modes the radio uses are asked for.
        /// </summary>
        public static string ExportUrl(int stateFips, string mode, RepeaterBookQuery query = null)
        {
            string url = SiteUrl + "/api/export.php?state_id=" + stateFips.ToString("00", CultureInfo.InvariantCulture) + "&mode=" + Uri.EscapeDataString(mode);
            if (query == null) return url;
            if (query.County.Length > 0) url += "&county=" + Uri.EscapeDataString(query.CountyBare);
            if (query.City.Length > 0) url += "&city=" + Uri.EscapeDataString(query.City);
            if (query.Callsign.Length > 0) url += "&callsign=" + Uri.EscapeDataString(query.CallsignPattern);
            if (query.Frequency > 0) url += "&frequency=" + query.Frequency.ToString("0.0####", CultureInfo.InvariantCulture);
            return url;
        }

        public const string AnalogMode = "analog";
        /// <summary>Put in a DMR listing's details when RepeaterBook gives no color code (it then gets 1).</summary>
        public const string NoColorCode = "color code not listed";
        public const string DmrMode = "DMR";

        /// <summary>A repeater's page on RepeaterBook, or null without an ID.</summary>
        public static string DetailUrl(string stateId, string repeaterId)
        {
            if (string.IsNullOrWhiteSpace(repeaterId) || string.IsNullOrWhiteSpace(stateId)) return null;
            return SiteUrl + "/repeaters/details.php?state_id=" + Uri.EscapeDataString(stateId.Trim()) + "&ID=" + Uri.EscapeDataString(repeaterId.Trim());
        }

        /// <summary>
        /// A fingerprint of the token and User-Agent a refusal was for, so the program doesn't send the same pair again
        /// until the user changes the token (or a new version changes the User-Agent). Not the token itself.
        /// </summary>
        public static string Fingerprint(string token)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes((token ?? "").Trim() + "\n" + UserAgent));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>True when the text looks like a token at all (the server decides whether it works).</summary>
        public static bool LooksLikeToken(string token)
        {
            string t = (token ?? "").Trim();
            return t.Length >= 16 && t.Length <= 512 && t.All(ch => ch > ' ' && ch < 127);
        }

        /// <summary>
        /// What an unsuccessful answer means. <paramref name="status"/> is the HTTP status (0 when the server wasn't reached),
        /// <paramref name="body"/> the response text (RepeaterBook sends JSON errors when it can).
        /// </summary>
        public static RepeaterBookFailure Classify(int status, string body)
        {
            string code = ErrorCode(body);
            var f = new RepeaterBookFailure { Status = status, Code = code };
            if (status == 429 || code == "rate_limited")
            {
                f.Kind = RepeaterBookFailureKind.RateLimited;
                f.Message = "RepeaterBook says too many requests. The program waits before it asks again.";
            }
            else if (code.StartsWith("auth_", StringComparison.Ordinal) || code == "ua_mismatch" || status == 401 || status == 403)
            {
                f.Kind = RepeaterBookFailureKind.Refused;
                switch (code)
                {
                    case "auth_missing": f.Message = "RepeaterBook needs your token. Paste the token you made for this program on RepeaterBook."; break;
                    case "auth_invalid": f.Message = "RepeaterBook didn't accept the token: it looks wrong. Copy it again from RepeaterBook > API Apps."; break;
                    case "auth_inactive": f.Message = "RepeaterBook says the token (or this program's access) isn't active. Check it on RepeaterBook > API Apps."; break;
                    case "auth_revoked": f.Message = "RepeaterBook says access was revoked for this token. Make a new one on RepeaterBook > API Apps."; break;
                    case "auth_scope_denied": f.Message = "RepeaterBook says the token isn't approved for this kind of search. Check it on RepeaterBook > API Apps."; break;
                    case "ua_mismatch": f.Message = "RepeaterBook didn't accept this program's identity (User-Agent). This needs a program update; please report it."; break;
                    default: f.Message = "RepeaterBook refused the request (" + status + (code.Length > 0 ? ", " + code : "") + "). Check your token on RepeaterBook > API Apps."; break;
                }
            }
            else
            {
                f.Kind = RepeaterBookFailureKind.Other;
                f.Message = status == 0 ? "Couldn't reach RepeaterBook. Check your internet connection."
                          : "RepeaterBook answered " + status + (code.Length > 0 ? " (" + code + ")" : "") + ".";
            }
            return f;
        }

        /// <summary>The error code of a JSON error answer ("auth_invalid"), or "".</summary>
        public static string ErrorCode(string body)
        {
            object o;
            try { o = Json.Parse(body); } catch { return ""; }
            foreach (var key in new[] { "code", "error_code", "error", "status" })
            {
                var v = Json.Get(o, key);
                if (v is Dictionary<string, object>) v = Json.Get(v, "code");
                string s = Json.Str(v).Trim().ToLowerInvariant();
                if (s.StartsWith("auth_", StringComparison.Ordinal) || s == "ua_mismatch" || s == "rate_limited") return s;
            }
            return "";
        }

        /// <summary>
        /// The repeaters of one export answer as listings: <paramref name="mode"/> <see cref="DmrMode"/> gives DMR repeaters
        /// (color code and DMR ID, no talkgroups: RepeaterBook doesn't list them), <see cref="AnalogMode"/> analog ones
        /// (<see cref="OnlineRepeater.Analog"/> set). Rows of other modes or without a frequency are left out and counted in
        /// <paramref name="notes"/>. Each listing is placed on <paramref name="atlas"/> when given.
        /// Field names follow RepeaterBook's export JSON ("Rptr ID", "Frequency", "Input Freq", "PL", "TSQ", "Near", "DMR Color Code"...);
        /// matching ignores case, spaces and punctuation, and a few other spellings are accepted.
        /// </summary>
        public static List<OnlineRepeater> ParseListings(string json, string mode, List<string> notes = null, string fallbackState = null, GeoAtlas atlas = null)
        {
            bool dmr = string.Equals(mode, DmrMode, StringComparison.OrdinalIgnoreCase);
            var list = new List<OnlineRepeater>();
            object root = Json.Parse(json);
            List<object> rows = root as List<object> ?? Json.Arr(Field(root, "results", "data", "repeaters"));
            int skipped = 0;
            foreach (var row in rows)
            {
                var d = Json.Obj(row);
                if (d == null) continue;
                var f = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in d) f[Key(kv.Key)] = Json.Str(kv.Value).Trim();
                string Col(params string[] names)
                {
                    foreach (var name in names) if (f.TryGetValue(Key(name), out string v) && v.Length > 0) return v;
                    return "";
                }
                bool Yes(params string[] names)
                {
                    string v = Col(names).ToLowerInvariant();
                    return v == "yes" || v == "1" || v == "true";
                }

                if (!MHz(Col("Frequency", "Output Freq", "Output Frequency"), out decimal rx)) { skipped++; continue; }
                // A row says which modes it has; when the column is missing, trust the mode that was asked for.
                bool hasFlag = dmr ? f.ContainsKey(Key("DMR")) : f.ContainsKey(Key("FM Analog")) || f.ContainsKey(Key("Analog"));
                if (hasFlag && !(dmr ? Yes("DMR") : Yes("FM Analog", "Analog"))) { skipped++; continue; }
                decimal tx = MHz(Col("Input Freq", "Input Frequency", "Input"), out decimal input) ? input : rx;

                string call = Naming.Clean(Col("Callsign", "Call"), 0).ToUpperInvariant();
                string near = Col("Nearest City", "Near", "City", "Location");
                string landmark = Col("Landmark");
                string state = Col("State", "Province");
                if (state.Length == 0) state = fallbackState ?? "";
                string county = CountyName(Col("County"), state);
                string country = Col("Country");
                if (country.Length == 0) country = "United States";
                string use = Col("Use", "Use/Membership", "Membership");
                string status = Col("Operational Status", "Status");
                bool off = status.Length > 0 && status.IndexOf("on-air", StringComparison.OrdinalIgnoreCase) < 0 && status.IndexOf("on air", StringComparison.OrdinalIgnoreCase) < 0;
                string id = Col("Rptr ID", "Repeater ID", "ID"), stateId = Col("State ID", "StateID");
                string place = near + (landmark.Length > 0 && !string.Equals(landmark, near, StringComparison.OrdinalIgnoreCase) ? ", " + landmark : "");

                var r = new OnlineRepeater
                {
                    Callsign = call, City = near, State = state, Country = country, RxMHz = rx, TxMHz = tx,
                    Status = off ? "OFF-AIR" : use.ToUpperInvariant(), Source = RepeaterBookImport.Site, DetailUrl = DetailUrl(stateId, id),
                };
                if (dmr)
                {
                    int cc = Json.Int(Col("DMR Color Code", "Color Code", "CC"), -1);
                    bool noCc = cc < 0 || cc > 15;
                    r.ColorCode = noCc ? 1 : cc; // unlisted: 1, the usual one; the summary says to check it
                    r.DmrId = Json.Int(Col("DMR ID", "DMRID"), 0);
                    r.Network = Networks.Normalize(Col("DMR Network", "Network"));
                    r.Details = Site(place, Col("Notes", "Comment")) + (noCc ? " (" + NoColorCode + ")" : "");
                }
                else
                {
                    var c = new AnalogListing { Name = call, Comment = place, RxMHz = rx, TxMHz = tx, RepeaterBookId = id, RepeaterBookStateId = stateId };
                    c.ToneEncode = Tone(Col("PL", "PL/CTCSS Uplink", "CTCSS Uplink", "Uplink Tone", "CTCSS"));
                    c.ToneDecode = Tone(Col("TSQ", "PL/CTCSS TSQ Downlink", "CTCSS TSQ Downlink", "Downlink Tone"));
                    c.ToneSquelch = c.ToneDecode != "Off";
                    string bw = Col("FM Bandwidth", "Bandwidth");
                    c.Mode = bw.StartsWith("12.5", StringComparison.Ordinal) || bw.IndexOf("narrow", StringComparison.OrdinalIgnoreCase) >= 0 ? "NFM" : "FM";
                    c.SetPlace(near, county, state, r.Status);
                    r.Analog = c;
                    r.Network = "FM";
                    string tone = c.ToneEncode == "Off" ? "no tone" : c.ToneSquelch && c.ToneDecode == c.ToneEncode ? "tone " + c.ToneEncode + " both ways"
                                : c.ToneDecode == "Off" ? "tone " + c.ToneEncode : "tone " + c.ToneEncode + " / " + c.ToneDecode;
                    r.Details = Site(place, tone);
                }
                if (atlas != null)
                {
                    var loc = RepeaterBookImport.Place(atlas, near, county, state, country);
                    // RepeaterBook gives the site's coordinates ("Precise" 1 = surveyed, 0 = about the town): use them, keep the county.
                    if (loc != null && Coordinate(Col("Lat", "Latitude"), 90, out double lat) && Coordinate(Col("Long", "Lon", "Longitude"), 180, out double lon))
                        loc = new GeoLocation { Country = loc.Country, State = loc.State, County = loc.County, Lat = lat, Lon = lon,
                                                Precision = Col("Precise") == "1" ? LocationPrecision.Point : LocationPrecision.City };
                    r.Location = loc;
                }
                list.Add(r);
            }
            if (skipped > 0)
                notes?.Add(skipped + " listing" + (skipped == 1 ? " wasn't " : "s weren't ") + (dmr ? "DMR" : "analog FM") + " or had no frequency, so " +
                           (skipped == 1 ? "it was" : "they were") + " left out.");
            return list;
        }

        static bool Coordinate(string s, double limit, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v != 0 && Math.Abs(v) <= limit;
        }

        static string Site(string place, string more)
        {
            return RepeaterBookImport.Site + ": " + place + (place.Length > 0 && more.Length > 0 ? ", " : "") + more;
        }
        /// <summary>"Brown" → "Brown County" (Louisiana "Parish", Alaska "Borough"), the way CHIRP's comments and the map name them.</summary>
        public static string CountyName(string county, string state)
        {
            string c = (county ?? "").Trim();
            if (c.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(c, @"\s(County|Parish|Borough|Census Area|Municipality)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return c;
            string st = (state ?? "").Trim();
            if (st.Equals("Louisiana", StringComparison.OrdinalIgnoreCase)) return c + " Parish";
            if (st.Equals("Alaska", StringComparison.OrdinalIgnoreCase)) return c + " Borough";
            return c + " County";
        }

        static object Field(object o, params string[] keys)
        {
            foreach (var k in keys) { var v = Json.Get(o, k); if (v != null) return v; }
            return null;
        }

        static string Key(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name ?? "") if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        static bool MHz(string s, out decimal v)
        {
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v) && v > 0 && v < 10000;
        }

        /// <summary>A RepeaterBook tone field as the radio's tone: "88.5", "D023N", or "Off" (blank, "CSQ", or unreadable).</summary>
        static string Tone(string s)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0 || t.Equals("CSQ", StringComparison.OrdinalIgnoreCase)) return "Off";
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*(Hz|DCS|DPL)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string n = Tones.Normalize(t);
            if (n == "Off" || !Tones.IsValid(n))
            {
                // "023" alone is DCS in RepeaterBook's listings ("D023N" in the radio).
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-7]{3}$")) return "D" + t + "N";
                return "Off";
            }
            return n;
        }
    }

    /// <summary>
    /// Optional search terms inside the chosen states (RepeaterBook's export takes county, city, callsign and frequency;
    /// "%" is its wildcard). Empty = the whole state. <see cref="Matches"/> applies the same terms to the answer, so the
    /// list is right even when the server ignores one.
    /// </summary>
    public sealed class RepeaterBookQuery
    {
        public string County { get; set; } = "";
        public string City { get; set; } = "";
        public string Callsign { get; set; } = "";
        /// <summary>Output or input frequency in MHz; 0 = any.</summary>
        public decimal Frequency { get; set; }

        public bool IsEmpty => County.Length == 0 && City.Length == 0 && Callsign.Length == 0 && Frequency <= 0;

        /// <summary>"Brown County" → "Brown" (RepeaterBook's county names have no suffix).</summary>
        public string CountyBare => System.Text.RegularExpressions.Regex.Replace(County.Trim(), @"\s+(County|Parish|Borough|Co\.?)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>A callsign as a prefix search ("W5" → "W5%"), unless it has its own wildcard.</summary>
        public string CallsignPattern => Callsign.Contains("%") ? Callsign.ToUpperInvariant() : Callsign.ToUpperInvariant() + "%";

        /// <summary>Reads the typed terms; null with <paramref name="problem"/> set when one can't be used.</summary>
        public static RepeaterBookQuery Parse(string county, string city, string callsign, string frequency, out string problem)
        {
            problem = null;
            var q = new RepeaterBookQuery
            {
                County = (county ?? "").Trim(),
                City = (city ?? "").Trim(),
                Callsign = (callsign ?? "").Trim().Replace("*", "%"),
            };
            string f = (frequency ?? "").Trim();
            if (f.Length > 0)
            {
                if (!decimal.TryParse(f, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal mhz) || mhz < 1 || mhz > 10000)
                {
                    problem = "Type the frequency in MHz, like 146.94";
                    return null;
                }
                q.Frequency = mhz;
            }
            if (q.Callsign.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(q.Callsign, @"^[A-Za-z0-9/%]{1,12}$"))
            {
                problem = "A callsign has letters and numbers only (use * for \"anything\", like W5*).";
                return null;
            }
            return q;
        }

        /// <summary>True when a listing fits the terms (case-insensitive; the callsign as a prefix or with % wildcards; frequency to 1 kHz).</summary>
        public bool Matches(OnlineRepeater r)
        {
            if (County.Length > 0)
            {
                string want = CountyBare;
                string have = r.Analog != null ? r.Analog.County : r.Location?.County?.Name ?? "";
                have = System.Text.RegularExpressions.Regex.Replace(have, @"\s+(County|Parish|Borough)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (City.Length > 0 && (r.City ?? "").IndexOf(City.Replace("%", ""), StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (Callsign.Length > 0)
            {
                string rx = "^" + System.Text.RegularExpressions.Regex.Escape(CallsignPattern).Replace("%", ".*") + "$";
                if (!System.Text.RegularExpressions.Regex.IsMatch(r.Callsign ?? "", rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
            }
            if (Frequency > 0 && Math.Abs(r.RxMHz - Frequency) > 0.0005m && Math.Abs(r.TxMHz - Frequency) > 0.0005m) return false;
            return true;
        }

        /// <summary>"in Brown County, callsign W5*", for status lines; "" when empty.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (County.Length > 0) parts.Add(CountyBare + " County");
            if (City.Length > 0) parts.Add("near " + City);
            if (Callsign.Length > 0) parts.Add("callsign " + CallsignPattern.Replace("%", "*"));
            if (Frequency > 0) parts.Add(Frequency.ToString("0.0####", CultureInfo.InvariantCulture) + " MHz");
            return string.Join(", ", parts);
        }
    }

    public enum RepeaterBookFailureKind { Other, RateLimited, Refused }

    /// <summary>Why a RepeaterBook request didn't work, and what to tell the user (never contains the token).</summary>
    public sealed class RepeaterBookFailure
    {
        public RepeaterBookFailureKind Kind { get; set; }
        public int Status { get; set; }
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Keeps requests within <see cref="RepeaterBookApi.MinSecondsBetweenRequests"/> and
    /// <see cref="RepeaterBookApi.MaxRequestsPerHour"/>. Pass the saved history in (so the hourly cap survives restarts)
    /// and save <see cref="History"/> after each <see cref="Record"/>. Pure: the caller supplies the clock.
    /// </summary>
    public sealed class RequestBudget
    {
        readonly List<DateTime> sent;

        public RequestBudget(IEnumerable<DateTime> history = null)
        {
            sent = (history ?? Enumerable.Empty<DateTime>()).OrderBy(d => d).ToList();
        }

        /// <summary>Requests still remembered (the last hour matters; older ones are dropped on <see cref="Record"/>).</summary>
        public IReadOnlyList<DateTime> History => sent;

        /// <summary>How long to wait before the next request may go out (zero when it may go now).</summary>
        public TimeSpan WaitBeforeNext(DateTime now)
        {
            var wait = TimeSpan.Zero;
            if (sent.Count > 0)
            {
                var gap = sent[sent.Count - 1].AddSeconds(RepeaterBookApi.MinSecondsBetweenRequests) - now;
                if (gap > wait) wait = gap;
            }
            var recent = sent.Where(d => d > now.AddHours(-1)).ToList();
            if (recent.Count >= RepeaterBookApi.MaxRequestsPerHour)
            {
                // The hourly cap frees up when the oldest of the last MaxRequestsPerHour requests is an hour old.
                var frees = recent[recent.Count - RepeaterBookApi.MaxRequestsPerHour].AddHours(1) - now;
                if (frees > wait) wait = frees;
            }
            return wait;
        }

        /// <summary>True when a request would break the hourly cap (waiting a few seconds won't help).</summary>
        public bool HourlyCapReached(DateTime now)
        {
            return sent.Count(d => d > now.AddHours(-1)) >= RepeaterBookApi.MaxRequestsPerHour;
        }

        public void Record(DateTime now)
        {
            sent.Add(now);
            sent.RemoveAll(d => d <= now.AddHours(-1));
        }
    }
}
