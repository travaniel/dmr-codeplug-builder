using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>
    /// Reads talkgroups from a CSV: the CPS's own TalkGroups.CSV, or any simple list with an ID column and a
    /// name column (with or without a header row).
    /// </summary>
    public static class TalkgroupCsv
    {
        public static List<Talkgroup> Load(string path)
        {
            return Parse(CsvTable.Load(path));
        }

        public static List<Talkgroup> Parse(CsvTable t)
        {
            int idCol = t.IndexOfAny("Radio ID", "TG ID", "Talkgroup ID", "TGID", "TG", "Talkgroup", "ID", "Number", "DMR ID");
            int nameCol = t.IndexOfAny("Name", "Talkgroup Name", "TG Name", "Alias", "Description");
            int typeCol = t.IndexOfAny("Call Type", "Type");

            List<List<string>> rows;
            if (idCol >= 0 && nameCol >= 0)
            {
                rows = t.Rows;
            }
            else
            {
                // No recognisable header: the first line is data too. Guess the columns.
                rows = new List<List<string>> { t.Header }.Concat(t.Rows).ToList();
                int width = rows.Max(r => r.Count);
                idCol = Enumerable.Range(0, width)
                    .OrderByDescending(c => rows.Count(r => c < r.Count && int.TryParse(r[c].Trim(), out _)))
                    .First();
                nameCol = Enumerable.Range(0, width)
                    .Where(c => c != idCol)
                    .OrderByDescending(c => rows.Count(r => c < r.Count && r[c].Trim().Length > 0 && !int.TryParse(r[c].Trim(), out _)))
                    .DefaultIfEmpty(-1)
                    .First();
                typeCol = -1;
                if (nameCol < 0) return new List<Talkgroup>();
            }

            var list = new List<Talkgroup>();
            var seen = new HashSet<int>();
            foreach (var r in rows)
            {
                if (idCol >= r.Count || nameCol >= r.Count) continue;
                if (!int.TryParse(r[idCol].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0) continue;
                string name = r[nameCol].Trim();
                if (name.Length == 0 || !seen.Add(id)) continue;
                string type = typeCol >= 0 && typeCol < r.Count ? CallTypes.Normalize(r[typeCol]) : CallTypes.Group;
                list.Add(new Talkgroup(name, id, type));
            }
            return list;
        }
    }
}
