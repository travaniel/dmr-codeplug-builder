using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>What one RepeaterBook import brought back: the listings (memory only), notes, and why it stopped early (or null).</summary>
    sealed class RepeaterBookDownload
    {
        public List<OnlineRepeater> Listings { get; } = new List<OnlineRepeater>();
        public List<string> Notes { get; } = new List<string>();
        public RepeaterBookFailure Failure { get; set; }
    }

    /// <summary>
    /// The user's own RepeaterBook token (made on RepeaterBook > API Apps for this program). Kept in the settings folder,
    /// encrypted for the Windows user (DPAPI); never in a project, an export, a log or a message.
    /// </summary>
    static class RepeaterBookToken
    {
        const string Key = "RepeaterBookToken";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("W6OZZ-CPS RepeaterBook token");

        public static bool IsSet => Get() != null;

        public static string Get()
        {
            string saved = AppSettings.Get(Key);
            if (string.IsNullOrEmpty(saved) || !saved.StartsWith("dpapi:", StringComparison.Ordinal)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(saved.Substring(6)), Entropy, DataProtectionScope.CurrentUser);
                string t = Encoding.UTF8.GetString(bytes);
                return t.Length > 0 ? t : null;
            }
            catch { return null; } // another Windows user's or a damaged value: as if none
        }

        /// <summary>Saves a new token (or clears it with null). A new token also clears an earlier refusal, so it is tried once.</summary>
        public static void Set(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length == 0) { AppSettings.Set(Key, null); }
            else
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
                AppSettings.Set(Key, "dpapi:" + Convert.ToBase64String(bytes));
            }
            AppSettings.Set(Online.RbRefusedKey, null);
            AppSettings.Set(Online.RbRefusedMessageKey, null);
        }
    }

    static partial class Online
    {
        internal const string RbRefusedKey = "RepeaterBookRefused";
        internal const string RbRefusedMessageKey = "RepeaterBookRefusedMessage";
        const string RbRequestsKey = "RepeaterBookRequests";
        const string RbBackoffUntilKey = "RepeaterBookBackoffUntil";
        const string RbBackoffSecondsKey = "RepeaterBookBackoffSeconds";
        static readonly object rbLock = new object();

        /// <summary>
        /// Why a RepeaterBook import can't start now (no token, a refusal for this token, a 429 wait, the hourly cap), or null
        /// when it can. Nothing is sent.
        /// </summary>
        public static string RepeaterBookBlocked()
        {
            string token = RepeaterBookToken.Get();
            if (token == null) return "Paste your RepeaterBook token first (RepeaterBook > API Apps, a token for this program).";
            if (AppSettings.Get(RbRefusedKey) == RepeaterBookApi.Fingerprint(token))
                return (AppSettings.Get(RbRefusedMessageKey) ?? "RepeaterBook refused this token.") +
                       " The program won't send it again: fix it on RepeaterBook, then paste the token again.";
            var until = BackoffUntil();
            if (until > DateTime.UtcNow)
                return "RepeaterBook asked the program to slow down. Try again after " + until.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture) + ".";
            var budget = new RequestBudget(RequestHistory());
            if (budget.HourlyCapReached(DateTime.UtcNow))
                return "This program has asked RepeaterBook " + RepeaterBookApi.MaxRequestsPerHour + " times in the last hour, its own limit. Try again in a while.";
            return null;
        }

        /// <summary>
        /// Downloads the repeaters of up to <see cref="RepeaterBookApi.MaxStatesPerAction"/> US states with the user's token, one
        /// request per state and mode (<see cref="RepeaterBookApi.DmrMode"/>, <see cref="RepeaterBookApi.AnalogMode"/>), one at a
        /// time, spaced and capped by <see cref="RequestBudget"/>. Stops at once on a 429 (and waits before any later request) or
        /// a refusal (not sent again with the same token). Results are only returned, never stored.
        /// </summary>
        public static Task<RepeaterBookDownload> RepeaterBookStatesAsync(IList<string> states, IList<string> modes, IProgress<string> progress, CancellationToken cancel,
                                                                        RepeaterBookQuery query = null)
        {
            return Task.Run(() =>
            {
                var result = new RepeaterBookDownload();
                if (states.Count > RepeaterBookApi.MaxStatesPerAction)
                    throw new ArgumentException("At most " + RepeaterBookApi.MaxStatesPerAction + " states at a time.");
                GeoAtlas atlas = null;
                try { atlas = GeoAtlas.BuiltIn(); } catch { }
                lock (rbLock)
                {
                    foreach (string state in states)
                    {
                        int fips = BrandMeister.UsStates.Where(s => s.Key.Equals(state, StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).FirstOrDefault();
                        if (fips == 0) { result.Notes.Add(state + " isn't a US state RepeaterBook's North America search knows; skipped."); continue; }
                        foreach (string mode in modes)
                        {
                            cancel.ThrowIfCancellationRequested();
                            string what = mode == RepeaterBookApi.DmrMode ? "DMR" : "analog";
                            progress?.Report("Asking RepeaterBook for " + what + " repeaters in " + state + "...");
                            string body = Fetch(RepeaterBookApi.ExportUrl(fips, mode, query), cancel, out var failure);
                            if (failure != null) { result.Failure = failure; return result; }
                            var notes = new List<string>();
                            List<OnlineRepeater> found;
                            try { found = RepeaterBookApi.ParseListings(body, mode, notes, state, atlas); }
                            catch (Exception ex) { result.Failure = new RepeaterBookFailure { Message = "RepeaterBook's answer for " + state + " couldn't be read (" + ex.Message + ")." }; return result; }
                            if (query != null && !query.IsEmpty) found = found.Where(query.Matches).ToList(); // in case the server ignored a term
                            result.Listings.AddRange(found);
                            result.Notes.Add(state + ": " + found.Count + " " + what + (notes.Count > 0 ? " (" + string.Join(" ", notes) + ")" : "") + ".");
                        }
                    }
                }
                return result;
            });
        }
        /// <summary>
        /// One request with the user's token, after the checks and the wait <see cref="RequestBudget"/> asks for. Returns the
        /// body, or null with <paramref name="failure"/> set (and remembered: 429 → wait, refusal → don't resend this token).
        /// </summary>
        static string Fetch(string url, CancellationToken cancel, out RepeaterBookFailure failure)
        {
            failure = null;
            string blocked = RepeaterBookBlocked();
            if (blocked != null) { failure = new RepeaterBookFailure { Kind = RepeaterBookFailureKind.Refused, Message = blocked }; return null; }
            string token = RepeaterBookToken.Get();

            var budget = new RequestBudget(RequestHistory());
            var wait = budget.WaitBeforeNext(DateTime.UtcNow);
            if (wait > TimeSpan.Zero && cancel.WaitHandle.WaitOne(wait)) cancel.ThrowIfCancellationRequested();
            budget.Record(DateTime.UtcNow);
            SaveRequestHistory(budget.History);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = RepeaterBookApi.UserAgent; // exactly the approved value
            req.Accept = "application/json";
            req.Headers[RepeaterBookApi.TokenHeader] = token;
            req.Timeout = req.ReadWriteTimeout = 60000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.AllowAutoRedirect = false; // never carry the token to another address
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = r.ReadToEnd();
                    if ((int)resp.StatusCode >= 300) { failure = Remember(RepeaterBookApi.Classify((int)resp.StatusCode, body), token, null); return null; }
                    // Some servers answer errors with 200 and a JSON error object.
                    if (RepeaterBookApi.ErrorCode(body).Length > 0) { failure = Remember(RepeaterBookApi.Classify(200, body), token, null); return null; }
                    AppSettings.Set(RbBackoffSecondsKey, null); // a good answer resets the 429 doubling
                    return body;
                }
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http == null) { failure = RepeaterBookApi.Classify(0, ""); return null; }
                string body = "";
                try { using (var r = new StreamReader(http.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd(); } catch { }
                failure = Remember(RepeaterBookApi.Classify((int)http.StatusCode, body), token, RetryAfter(http.Headers["Retry-After"]));
                return null;
            }
        }

        /// <summary>Keeps what a failure means for later requests: the 429 wait, or the refused token's fingerprint.</summary>
        static RepeaterBookFailure Remember(RepeaterBookFailure f, string token, int? retryAfter)
        {
            if (f.Kind == RepeaterBookFailureKind.RateLimited)
            {
                int.TryParse(AppSettings.Get(RbBackoffSecondsKey) ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int previous);
                int seconds = RepeaterBookApi.BackoffSeconds(previous, retryAfter);
                AppSettings.Set(RbBackoffSecondsKey, seconds.ToString(CultureInfo.InvariantCulture));
                AppSettings.Set(RbBackoffUntilKey, DateTime.UtcNow.AddSeconds(seconds).ToString("o", CultureInfo.InvariantCulture));
                f.Message += " (" + (seconds >= 120 ? seconds / 60 + " minutes" : seconds + " seconds") + ")";
            }
            else if (f.Kind == RepeaterBookFailureKind.Refused)
            {
                AppSettings.Set(RbRefusedKey, RepeaterBookApi.Fingerprint(token));
                AppSettings.Set(RbRefusedMessageKey, f.Message);
            }
            return f;
        }

        static int? RetryAfter(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            if (int.TryParse(header.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)) return Math.Max(1, s);
            if (DateTime.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                return Math.Max(1, (int)Math.Ceiling((when - DateTime.UtcNow).TotalSeconds));
            return null;
        }

        static DateTime BackoffUntil()
        {
            return DateTime.TryParse(AppSettings.Get(RbBackoffUntilKey) ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                ? d.ToUniversalTime() : DateTime.MinValue;
        }

        static IEnumerable<DateTime> RequestHistory()
        {
            foreach (string part in (AppSettings.Get(RbRequestsKey) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks)
                    yield return new DateTime(ticks, DateTimeKind.Utc);
        }

        static void SaveRequestHistory(IEnumerable<DateTime> sent)
        {
            AppSettings.Set(RbRequestsKey, string.Join(",", sent.Select(d => d.Ticks.ToString(CultureInfo.InvariantCulture))));
        }

        /// <summary>
        /// Developer check (--repeaterbook-sample): one state through the same client, reported as the field names, the first
        /// two listings and what the parser made of them. For checking the parser against a real answer; the token never
        /// appears in it.
        /// </summary>
        public static string RepeaterBookSample(string state, RepeaterBookQuery query = null)
        {
            int fips = BrandMeister.UsStates.Where(s => s.Key.Equals(state, StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).FirstOrDefault();
            if (fips == 0) return "Unknown US state: " + state;
            var sb = new StringBuilder();
            sb.AppendLine("User-Agent: " + RepeaterBookApi.UserAgent);
            foreach (string mode in new[] { RepeaterBookApi.AnalogMode, RepeaterBookApi.DmrMode })
            {
                string url = RepeaterBookApi.ExportUrl(fips, mode, query);
                sb.AppendLine();
                sb.AppendLine("=== GET " + url);
                string body = Fetch(url, CancellationToken.None, out var failure);
                if (failure != null) { sb.AppendLine("FAILED: " + failure.Kind + " " + failure.Status + " " + failure.Code + ": " + failure.Message); break; }
                object root = Json.Parse(body);
                sb.AppendLine("Top level: " + (root is List<object> ? "array" : string.Join(", ", (Json.Obj(root) ?? new Dictionary<string, object>()).Keys)));
                var rows = root as List<object> ?? Json.Arr(Json.Get(root, "results"));
                sb.AppendLine("Rows: " + rows.Count + "  count field: " + Json.Str(Json.Get(root, "count")));
                var keys = rows.Select(Json.Obj).Where(d => d != null).SelectMany(d => d.Keys).Distinct().ToList();
                sb.AppendLine("Fields (" + keys.Count + "): " + string.Join(" | ", keys));
                foreach (var row in rows.Take(2))
                {
                    sb.AppendLine("--- listing");
                    foreach (var kv in Json.Obj(row) ?? new Dictionary<string, object>()) sb.AppendLine("  " + kv.Key + " = " + Json.Str(kv.Value));
                }
                var notes = new List<string>();
                var parsed = RepeaterBookApi.ParseListings(body, mode, notes, state, GeoAtlas.BuiltIn());
                sb.AppendLine("--- parsed: " + parsed.Count + ". " + string.Join(" ", notes));
                if (query != null && !query.IsEmpty) sb.AppendLine("--- of those, matching " + query.Describe() + ": " + parsed.Count(query.Matches) + " (server filtered " + (parsed.All(query.Matches) ? "fully" : "NOT fully") + ")");
                foreach (var r in parsed.Take(5))
                    sb.AppendLine("  " + r.Callsign + " | " + r.City + " | " + (r.Location?.County?.Name ?? "-") + " | " + r.RxMHz + " / " + r.TxMHz + " | CC " + r.ColorCode +
                                  " | " + r.Network + " | " + r.Status + " | " + r.Details + " | " + r.DetailUrl);
                if (mode == RepeaterBookApi.DmrMode)
                    sb.AppendLine("Color codes: " + string.Join(", ", parsed.GroupBy(r => r.ColorCode).Select(g => g.Key + " x" + g.Count())));
                sb.AppendLine("Statuses: " + string.Join(", ", parsed.GroupBy(r => r.Status).Select(g => (g.Key.Length == 0 ? "(none)" : g.Key) + " x" + g.Count())));
            }
            return sb.ToString();
        }
    }
}