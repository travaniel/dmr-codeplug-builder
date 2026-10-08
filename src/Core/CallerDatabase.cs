using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeplugBuilder.Core
{
    /// <summary>One DMR user from RadioID.net's user database (<see cref="CallerDatabase.Url"/>).</summary>
    public sealed class Caller
    {
        public int Id;
        public string Callsign = "", FirstName = "", LastName = "", City = "", State = "", Country = "";

        /// <summary>First and last name, as one name: "Hans Juergen", "Pat Smith".</summary>
        public string Name => (FirstName + " " + LastName).Trim();
    }

    /// <summary>Which callers go into the radio's caller database (DigitalContactList.CSV). Roadmap 1.4 item 1.</summary>
    public static class CallerScopes
    {
        /// <summary>None: the CPS's own list stays (importing the file would replace it). The default.</summary>
        public const string Off = "";
        public const string World = "World";
        /// <summary><see cref="GenerationOptions.CallerAreas"/> holds country names ("United States", "Canada").</summary>
        public const string Countries = "Countries";
        /// <summary><see cref="GenerationOptions.CallerAreas"/> holds US state names ("Texas").</summary>
        public const string UsStates = "UsStates";
    }

    /// <summary>
    /// The radio's caller database: who a DMR ID belongs to, so the radio shows "W6OZZ Austin" instead of a number.
    /// Source: RadioID.net's daily user.csv. Pure: the App downloads and caches the file.
    /// Not written to the codeplug yet: DigitalContactList.CSV must start from a row the CPS exported (rule 2), and the
    /// user's radio has an empty list (HANDOFF 4). Once a real row is in hand it becomes a template and <see cref="ToTable"/> fills it.
    /// </summary>
    public static class CallerDatabase
    {
        /// <summary>Checked 2026-10-08: ~17 MB, rebuilt daily, header RADIO_ID,CALLSIGN,FIRST_NAME,LAST_NAME,CITY,STATE,COUNTRY, unquoted.</summary>
        public const string Url = "https://radioid.net/static/user.csv";

        /// <summary>The DMR-6X2 PRO holds 500,000 callers (the plain DMR-6X2 200,000).</summary>
        public const int Capacity = 500000;

        /// <summary>DigitalContactList.CSV's section in a CPS .LST.</summary>
        public const string File = "DigitalContactList.CSV";

        /// <summary>Reads user.csv line by line (quoted fields allowed). Bad lines are skipped.</summary>
        public static IEnumerable<Caller> Parse(TextReader reader)
        {
            string header = reader.ReadLine();
            if (header == null) yield break;
            var cols = SplitLine(header).Select(h => h.Trim().ToUpperInvariant()).ToList();
            int Col(string name) => cols.IndexOf(name);
            int id = Col("RADIO_ID"), call = Col("CALLSIGN"), first = Col("FIRST_NAME"), last = Col("LAST_NAME"),
                city = Col("CITY"), state = Col("STATE"), country = Col("COUNTRY");
            if (id < 0 || call < 0) yield break;
            string F(List<string> f, int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var f = SplitLine(line);
                if (!int.TryParse(F(f, id), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0 || n > Validator.MaxTalkgroupId) continue;
                yield return new Caller
                {
                    Id = n, Callsign = F(f, call).ToUpperInvariant(), FirstName = F(f, first), LastName = F(f, last),
                    City = F(f, city), State = F(f, state), Country = F(f, country),
                };
            }
        }

        static List<string> SplitLine(string line)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { list.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }

        /// <summary>
        /// The callers a scope takes (<see cref="CallerScopes"/>), each ID once (the first listing wins), in ascending ID order,
        /// at most <paramref name="capacity"/>. <paramref name="cut"/> says how many didn't fit.
        /// </summary>
        public static List<Caller> Select(IEnumerable<Caller> all, string scope, IEnumerable<string> areas, int capacity, out int cut)
        {
            cut = 0;
            var list = new List<Caller>();
            if (string.IsNullOrEmpty(scope)) return list;
            var names = new HashSet<string>((areas ?? Enumerable.Empty<string>()).Select(Key), StringComparer.Ordinal);
            bool Takes(Caller c)
            {
                switch (scope)
                {
                    case CallerScopes.World: return true;
                    case CallerScopes.Countries: return names.Contains(Key(c.Country));
                    case CallerScopes.UsStates: return Key(c.Country) == Key("United States") && names.Contains(Key(c.State));
                    default: return false;
                }
            }
            var seen = new HashSet<int>();
            foreach (var c in all)
                if (Takes(c) && seen.Add(c.Id)) list.Add(c);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (capacity > 0 && list.Count > capacity)
            {
                cut = list.Count - capacity;
                list.RemoveRange(capacity, cut);
            }
            return list;
        }

        static string Key(string s) { return Naming.Fold(s ?? "").Trim().ToLowerInvariant(); }

        /// <summary>
        /// DigitalContactList.CSV from a template table (header + one real CPS row): No. 1, 2, 3..., Radio ID, Callsign, Name,
        /// City, State, Country, Remarks cleared. Call Type and Call Alert keep the template row's values. Text is folded to
        /// ASCII (the CPS files are ASCII). Field length limits in the CPS are still unknown, so nothing is cut here.
        /// </summary>
        public static CsvTable ToTable(IList<Caller> callers, CsvTable template)
        {
            if (template == null || template.Rows.Count == 0) throw new InvalidOperationException("DigitalContactList.CSV needs a row exported by the CPS as its template.");
            var t = template.CloneHeader();
            var proto = template.Rows[0];
            int no = 0;
            foreach (var c in callers)
            {
                var row = t.NewRow(proto);
                t.Set(row, (++no).ToString(CultureInfo.InvariantCulture), "No.");
                t.Set(row, c.Id.ToString(CultureInfo.InvariantCulture), "Radio ID");
                t.Set(row, Ascii(c.Callsign), "Callsign");
                t.Set(row, Ascii(c.Name), "Name");
                t.Set(row, Ascii(c.City), "City");
                t.Set(row, Ascii(c.State), "State");
                t.Set(row, Ascii(c.Country), "Country");
                t.Set(row, "", "Remarks");
                t.Rows.Add(row);
            }
            return t;
        }

        /// <summary>Accents folded, then anything that isn't printable ASCII (or is " or |) dropped.</summary>
        public static string Ascii(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in Naming.Fold(s ?? ""))
                if (c >= 32 && c < 127 && c != '"' && c != '|') sb.Append(c);
            return sb.ToString().Trim();
        }
    }
}
