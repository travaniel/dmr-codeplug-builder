using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeplugBuilder.Core
{
    public static class Naming
    {
        /// <summary>
        /// Makes a name safe for the CPS: printable ASCII only, no quotes or '|' (the CPS uses '|' to
        /// separate zone members), single spaces, trimmed and cut to <paramref name="maxLength"/>.
        /// </summary>
        public static string Clean(string s, int maxLength)
        {
            var sb = new StringBuilder();
            bool lastSpace = false;
            foreach (char c in s ?? "")
            {
                char ch = c;
                if (ch == '\t' || ch == '|') ch = ' ';
                if (ch < 32 || ch > 126 || ch == '"') continue;
                if (ch == ' ')
                {
                    if (lastSpace || sb.Length == 0) continue;
                    lastSpace = true;
                }
                else lastSpace = false;
                sb.Append(ch);
            }
            string t = sb.ToString().Trim();
            if (maxLength > 0 && t.Length > maxLength) t = t.Substring(0, maxLength).TrimEnd();
            return t;
        }

        /// <summary>
        /// Accents and other marks removed so a name survives <see cref="Clean"/>: "Baden-W&#252;rttemberg" →
        /// "Baden-Wurttemberg", "Qu&#233;bec" → "Quebec". Letters with no plain form (sharp s, o with stroke...) are spelled out.
        /// </summary>
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                switch (c)
                {
                    case 'ß': sb.Append("ss"); break; // sharp s
                    case 'ø': sb.Append('o'); break;  // o with stroke
                    case 'Ø': sb.Append('O'); break;
                    case 'ł': sb.Append('l'); break;  // l with stroke
                    case 'Ł': sb.Append('L'); break;
                    case 'æ': sb.Append("ae"); break;
                    case 'Æ': sb.Append("AE"); break;
                    case 'đ': sb.Append('d'); break;  // d with stroke
                    case 'Đ': sb.Append('D'); break;
                    case 'ı': sb.Append('i'); break;  // dotless i
                    case '’': sb.Append('\''); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }


        // ---- Shortening names to fit -----------------------------------------------------------

        /// <summary>US states and Canadian provinces → postal codes ("Texas" → "TX"). Also used for zone names.</summary>
        public static readonly KeyValuePair<string, string>[] StateCodes =
        {
            P("District of Columbia", "DC"), P("Washington DC", "DC"), P("New Hampshire", "NH"), P("New Jersey", "NJ"), P("New Mexico", "NM"),
            P("New York", "NY"), P("North Carolina", "NC"), P("North Dakota", "ND"), P("South Carolina", "SC"), P("South Dakota", "SD"),
            P("West Virginia", "WV"), P("Rhode Island", "RI"), P("Alabama", "AL"), P("Alaska", "AK"), P("Arizona", "AZ"), P("Arkansas", "AR"),
            P("California", "CA"), P("Colorado", "CO"), P("Connecticut", "CT"), P("Delaware", "DE"), P("Florida", "FL"), P("Georgia", "GA"),
            P("Hawaii", "HI"), P("Idaho", "ID"), P("Illinois", "IL"), P("Indiana", "IN"), P("Iowa", "IA"), P("Kansas", "KS"), P("Kentucky", "KY"),
            P("Louisiana", "LA"), P("Maine", "ME"), P("Maryland", "MD"), P("Massachusetts", "MA"), P("Michigan", "MI"), P("Minnesota", "MN"),
            P("Mississippi", "MS"), P("Missouri", "MO"), P("Montana", "MT"), P("Nebraska", "NE"), P("Nevada", "NV"), P("Ohio", "OH"),
            P("Oklahoma", "OK"), P("Oregon", "OR"), P("Pennsylvania", "PA"), P("Tennessee", "TN"), P("Texas", "TX"), P("Utah", "UT"),
            P("Vermont", "VT"), P("Virginia", "VA"), P("Washington", "WA"), P("Wisconsin", "WI"), P("Wyoming", "WY"), P("Puerto Rico", "PR"),
            P("British Columbia", "BC"), P("New Brunswick", "NB"), P("Newfoundland and Labrador", "NL"), P("Nova Scotia", "NS"),
            P("Prince Edward Island", "PE"), P("Northwest Territories", "NT"), P("Alberta", "AB"), P("Manitoba", "MB"), P("Ontario", "ON"),
            P("Quebec", "QC"), P("Saskatchewan", "SK"), P("Yukon", "YT"), P("Nunavut", "NU"),
        };

        /// <summary>Word swaps tried in this order, only until the name fits. States come before the compass words ("West Virginia").</summary>
        static readonly KeyValuePair<string, string>[] Abbreviations = new[]
        {
            P("Talk Groups", "TGs"), P("Talkgroups", "TGs"), P("Talk Group", "TG"), P("Talkgroup", "TG"), P("Repeaters", "Rptrs"), P("Repeater", "Rptr"), P("Brandmeister", "BM"), P("Brand Meister", "BM"),
            P("County", "Co"), P("Parish", "Par"), P("Statewide", "State"), P("State Wide", "State"), P("State-wide", "State"),
            P("Nationwide", "Natl"), P("Worldwide", "WW"), P("World Wide", "WW"), P("World-wide", "WW"),
            P("Communications", "Comms"), P("Communication", "Comm"), P("Emergency", "Emerg"), P("Disconnect", "Discon"),
            P("Deactivate", "Deact"), P("International", "Intl"), P("National", "Natl"), P("Association", "Assn"), P("Amateur", "Amtr"),
            P("Mountain", "Mtn"), P("Regional", "Rgnl"), P("Region", "Rgn"), P("Network", "Net"), P("Weather", "WX"), P("Hurricane", "Hurr"),
            P("Aviation", "Avn"), P("English", "Eng"), P("Spanish", "Span"), P("Technical", "Tech"), P("Simplex", "Splx"), P("Linked", "Lnkd"),
            P("Saint", "St"), P("Fort", "Ft"), P("Mount", "Mt"), P("Springs", "Spgs"), P("Heights", "Hts"), P("Valley", "Vly"),
            P("Village", "Vlg"), P("Island", "Isl"), P("Center", "Ctr"), P("Central", "Cent"), P("Metropolitan", "Metro"),
            P("Northeast", "NE"), P("Northwest", "NW"), P("Southeast", "SE"), P("Southwest", "SW"),
            P("North East", "NE"), P("North West", "NW"), P("South East", "SE"), P("South West", "SW"),
        }.Concat(StateCodes).Concat(new[] { P("North", "N"), P("South", "S"), P("East", "E"), P("West", "W") }).ToArray();

        static readonly Regex[] AbbreviationPatterns = Abbreviations
            .Select(a => new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(a.Key).Replace(@"\ ", @"[\s-]+") + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
            .ToArray();

        static KeyValuePair<string, string> P(string a, string b) { return new KeyValuePair<string, string>(a, b); }

        /// <summary>
        /// Shortens a name to <paramref name="maxLength"/> the way a person would rather than cutting it off:
        /// drops "(notes)", then uses common abbreviations ("Talkgroup" → "TG", "Texas" → "TX", "North" → "N"), then
        /// cuts. Words with a digit at the end ("2", "TS2", an ID) are never cut, so "Net Talkgroup 1" and
        /// "Net Talkgroup 2" stay different; <paramref name="keepLastWord"/> keeps the last word whole too ("NC7Q WA State ARES TAC"
        /// → "NC7Q WA Stat TAC" rather than "NC7Q WA State AR"). A name that fits is only cleaned.
        /// </summary>
        public static string Fit(string s, int maxLength, bool keepLastWord = false)
        {
            string t = Clean(Fold(s), 0);
            if (maxLength <= 0 || t.Length <= maxLength) return t;

            string bare = Clean(Regex.Replace(t, @"\s*[\(\[][^\)\]]*[\)\]]", " "), 0);
            if (bare.Count(char.IsLetterOrDigit) >= 2) t = bare;
            if (t.Length <= maxLength) return t;

            for (int i = 0; i < Abbreviations.Length && t.Length > maxLength; i++)
                t = Clean(AbbreviationPatterns[i].Replace(t, Abbreviations[i].Value), 0);
            if (t.Length <= maxLength) return t;

            // Then cut, but never the numbers at the end ("2", "TS1", an ID): they tell otherwise equal names apart.
            var words = t.Split(' ');
            int tailStart = words.Length;
            while (tailStart > 1 && (words[tailStart - 1].Any(char.IsDigit) || keepLastWord && tailStart == words.Length)) tailStart--;
            string tail = string.Join(" ", words.Skip(tailStart));
            if (tail.Length == 0) return Clean(t, maxLength);
            string head = Clean(string.Join(" ", words.Take(tailStart)), maxLength - tail.Length - 1);
            if (maxLength - tail.Length - 1 >= 3 && head.Length > 0) return head + " " + tail;
            return Clean(tail.Length <= maxLength ? tail : t, maxLength);
        }

        /// <summary>
        /// "W5FC" + "TX Statewide" → "W5FC TX Statewide" when it fits, otherwise shortened with <see cref="Fit"/>
        /// ("W5FC" + "North America Talkgroup" → "W5FC N America TG"). The prefix is never shortened unless it
        /// leaves the talkgroup fewer than 4 characters. A talkgroup name that already starts with the prefix isn't
        /// prefixed twice ("W5FC" + "W5FC Local" → "W5FC Local"), nor with the callsign of its sister repeater
        /// ("W5LOS2" + "W5LOS Local" → "W5LOS2 Local").
        /// </summary>
        public static string AutoChannelName(string prefix, string talkgroupName, int maxLength, bool keepLastWord = false)
        {
            string p = Clean(prefix, 0);
            string tg = Clean(Fold(talkgroupName), 0);
            if (p.Length == 0 || tg.Equals(p, StringComparison.OrdinalIgnoreCase) || tg.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                return Fit(tg, maxLength, keepLastWord);
            string root = p.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (root.Length >= 3 && root.Length < p.Length && root.Any(char.IsDigit) && tg.StartsWith(root + " ", StringComparison.OrdinalIgnoreCase) && tg.Length > root.Length + 1)
                tg = tg.Substring(root.Length + 1);
            string full = p + " " + tg;
            if (maxLength <= 0 || full.Length <= maxLength) return full;

            int room = maxLength - p.Length - 1;
            if (room >= 4) return p + " " + Fit(tg, room, keepLastWord);

            // Long prefix: keep a few characters of each.
            string t = Fit(tg, Math.Max(4, maxLength / 2 - 1), keepLastWord);
            return Clean(Fit(p, Math.Max(1, maxLength - t.Length - 1)) + " " + t, maxLength);
        }

        /// <summary>
        /// How <see cref="AutoChannelName"/> worked before version 1.3 (cut, not shortened). Only for keeping the
        /// channel names of projects saved by older versions (<see cref="Project.Normalize"/>).
        /// </summary>
        public static string LegacyAutoChannelName(string prefix, string talkgroupName, int maxLength)
        {
            string p = Clean(prefix, 0);
            string tg = Clean(talkgroupName, 0);
            if (p.Length == 0 || tg.Equals(p, StringComparison.OrdinalIgnoreCase) || tg.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                return Clean(tg, maxLength);
            string full = p + " " + tg;
            if (maxLength <= 0 || full.Length <= maxLength) return full;

            int room = maxLength - p.Length - 1;
            if (room >= 4) return p + " " + tg.Substring(0, Math.Min(room, tg.Length)).TrimEnd();

            int tgKeep = Math.Min(tg.Length, Math.Max(4, maxLength / 2 - 1));
            int pKeep = Math.Max(1, maxLength - tgKeep - 1);
            return Clean(p.Substring(0, Math.Min(pKeep, p.Length)) + " " + tg.Substring(0, tgKeep), maxLength);
        }

        /// <summary>
        /// A talkgroup name no other talkgroup has (<paramref name="taken"/>, case-insensitive): the name, or the name
        /// with its ID ("Local 3166"), shortened so the ID is never cut. Empty names become "TG 1234".
        /// </summary>
        public static string UniqueTalkgroupName(string name, int id, Func<string, bool> taken, int maxLength = 16)
        {
            string ids = id.ToString(CultureInfo.InvariantCulture);
            string n = Fit(name, maxLength);
            if (n.Length == 0) n = "TG " + ids;
            if (!taken(n)) return n;
            string withId = Fit(Clean(Fold(name), 0) + " " + ids, maxLength);
            if (!taken(withId)) return withId;
            for (int k = 2; ; k++)
            {
                string c = Fit(n + " " + k.ToString(CultureInfo.InvariantCulture), maxLength);
                if (!taken(c)) return c;
            }
        }
    }

    /// <summary>Hands out names that are unique (case-insensitive) and fit the length limit.</summary>
    public sealed class UniqueNamer
    {
        readonly HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly int maxLength;
        readonly string fallback;

        public UniqueNamer(int maxLength, string fallback = "Channel")
        {
            this.maxLength = maxLength;
            this.fallback = fallback;
        }

        public bool IsUsed(string name) { return used.Contains(name); }

        public void Reserve(string name)
        {
            if (!string.IsNullOrEmpty(name)) used.Add(name);
        }

        /// <summary>
        /// Returns <paramref name="desired"/> shortened to fit (<see cref="Naming.Fit"/>) and made unique by adding
        /// " 2", " 3"... if needed. The number is never cut off: the rest of the name is shortened to make room.
        /// </summary>
        public string Claim(string desired)
        {
            string name = Naming.Fit(desired, maxLength);
            if (name.Length == 0) name = Naming.Fit(fallback, maxLength);
            if (used.Add(name)) return name;
            string full = Naming.Clean(Naming.Fold(desired), 0);
            if (full.Length == 0) full = name;
            for (int n = 2; ; n++)
            {
                string candidate = Naming.Fit(full + " " + n.ToString(CultureInfo.InvariantCulture), maxLength);
                if (used.Add(candidate)) return candidate;
            }
        }
    }
}
