using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeplugBuilder.Core
{
    /// <summary>
    /// A CSV table in the dialect the BTECH/AnyTone CPS reads and writes:
    /// every field double-quoted, comma separated, CRLF line endings, no BOM.
    /// </summary>
    public sealed class CsvTable
    {
        /// <summary>Latin-1 round-trips every byte; generated names are plain ASCII anyway.</summary>
        public static readonly Encoding FileEncoding = Encoding.GetEncoding(28591);

        public List<string> Header { get; } = new List<string>();
        public List<List<string>> Rows { get; } = new List<List<string>>();

        public CsvTable() { }

        public CsvTable(IEnumerable<string> header)
        {
            Header.AddRange(header);
        }

        /// <summary>Copy of the header with no rows: the starting point for generated files.</summary>
        public CsvTable CloneHeader()
        {
            return new CsvTable(Header);
        }

        /// <summary>Column index by name; exact (case-insensitive) first, then ignoring punctuation and spaces.</summary>
        public int IndexOf(string column)
        {
            for (int i = 0; i < Header.Count; i++)
                if (string.Equals(Header[i].Trim(), column, StringComparison.OrdinalIgnoreCase))
                    return i;
            string want = Normalize(column);
            for (int i = 0; i < Header.Count; i++)
                if (Normalize(Header[i]) == want)
                    return i;
            return -1;
        }

        /// <summary>Index of the first of several alternative column names that exists.</summary>
        public int IndexOfAny(params string[] columns)
        {
            foreach (string c in columns)
            {
                int i = IndexOf(c);
                if (i >= 0) return i;
            }
            return -1;
        }

        public bool Has(params string[] columns)
        {
            return IndexOfAny(columns) >= 0;
        }

        /// <summary>Sets a field in <paramref name="row"/> if one of the named columns exists. Returns false if none does.</summary>
        public bool Set(List<string> row, string value, params string[] columns)
        {
            int i = IndexOfAny(columns);
            if (i < 0) return false;
            while (row.Count < Header.Count) row.Add("");
            row[i] = value ?? "";
            return true;
        }

        /// <summary>Gets a field from <paramref name="row"/>, or "" when the column is missing.</summary>
        public string Get(List<string> row, params string[] columns)
        {
            int i = IndexOfAny(columns);
            if (i < 0 || i >= row.Count) return "";
            return row[i] ?? "";
        }

        /// <summary>A new row of the right width, copied from a template row (or empty).</summary>
        public List<string> NewRow(IList<string> template)
        {
            var row = new List<string>(Header.Count);
            for (int i = 0; i < Header.Count; i++)
                row.Add(template != null && i < template.Count ? template[i] ?? "" : "");
            return row;
        }

        public static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static CsvTable Parse(string text)
        {
            var records = ParseRecords(text);
            var table = new CsvTable();
            if (records.Count == 0) return table;
            table.Header.AddRange(records[0]);
            for (int i = 1; i < records.Count; i++)
            {
                var r = records[i];
                if (r.Count == 1 && r[0].Length == 0) continue; // blank line
                table.Rows.Add(r);
            }
            return table;
        }

        public static CsvTable Load(string path)
        {
            return Parse(File.ReadAllText(path, FileEncoding));
        }

        public void Save(string path)
        {
            File.WriteAllText(path, ToCsv(), FileEncoding);
        }

        public string ToCsv()
        {
            var sb = new StringBuilder();
            AppendRecord(sb, Header);
            foreach (var r in Rows) AppendRecord(sb, r);
            return sb.ToString();
        }

        static void AppendRecord(StringBuilder sb, IList<string> fields)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append((fields[i] ?? "").Replace("\"", "\"\"")).Append('"');
            }
            sb.Append("\r\n");
        }

        /// <summary>RFC 4180 style parser that also tolerates unquoted fields and bare LF line endings.</summary>
        static List<List<string>> ParseRecords(string text)
        {
            var records = new List<List<string>>();
            if (string.IsNullOrEmpty(text)) return records;
            if (text[0] == '﻿') text = text.Substring(1);

            var record = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            bool fieldStarted = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        inQuotes = false; i++; continue;
                    }
                    field.Append(c); i++; continue;
                }
                switch (c)
                {
                    case '"':
                        inQuotes = true; fieldStarted = true; i++;
                        break;
                    case ',':
                        record.Add(field.ToString()); field.Clear(); fieldStarted = false; i++;
                        break;
                    case '\r':
                    case '\n':
                        record.Add(field.ToString()); field.Clear(); fieldStarted = false;
                        records.Add(record); record = new List<string>();
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i += 2; else i++;
                        break;
                    default:
                        field.Append(c); fieldStarted = true; i++;
                        break;
                }
            }
            if (fieldStarted || field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                records.Add(record);
            }
            return records;
        }
    }
}
