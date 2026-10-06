using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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

        /// <summary>
        /// "W5FC" + "TX Statewide" → "W5FC TX Statewi" (fits <paramref name="maxLength"/>). A talkgroup name that
        /// already starts with the prefix isn't prefixed twice: "W5FC" + "W5FC Local" → "W5FC Local".
        /// </summary>
        public static string AutoChannelName(string prefix, string talkgroupName, int maxLength)
        {
            string p = Clean(prefix, 0);
            string tg = Clean(talkgroupName, 0);
            if (p.Length == 0 || tg.Equals(p, StringComparison.OrdinalIgnoreCase) || tg.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                return Clean(tg, maxLength);
            string full = p + " " + tg;
            if (maxLength <= 0 || full.Length <= maxLength) return full;

            int room = maxLength - p.Length - 1;
            if (room >= 4) return p + " " + tg.Substring(0, Math.Min(room, tg.Length)).TrimEnd();

            // Long prefix: keep a few characters of each.
            int tgKeep = Math.Min(tg.Length, Math.Max(4, maxLength / 2 - 1));
            int pKeep = Math.Max(1, maxLength - tgKeep - 1);
            return Clean(p.Substring(0, Math.Min(pKeep, p.Length)) + " " + tg.Substring(0, tgKeep), maxLength);
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

        /// <summary>Returns <paramref name="desired"/> cleaned and made unique by adding " 2", " 3"... if needed.</summary>
        public string Claim(string desired)
        {
            string name = Naming.Clean(desired, maxLength);
            if (name.Length == 0) name = Naming.Clean(fallback, maxLength);
            if (used.Add(name)) return name;
            for (int n = 2; ; n++)
            {
                string suffix = " " + n;
                int keep = Math.Max(1, maxLength - suffix.Length);
                string stem = name.Length > keep ? name.Substring(0, keep).TrimEnd() : name;
                string candidate = stem + suffix;
                if (used.Add(candidate)) return candidate;
            }
        }
    }
}
