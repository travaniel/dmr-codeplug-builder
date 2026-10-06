using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodeplugBuilder.Core
{
    /// <summary>
    /// Minimal JSON reader for the online APIs (no NuGet allowed). Objects become
    /// <c>Dictionary&lt;string, object&gt;</c>, arrays <c>List&lt;object&gt;</c>, numbers <c>decimal</c>
    /// (or <c>double</c> when out of range), plus string, bool and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Reader(text ?? "");
            p.SkipWhite();
            object v = p.ReadValue();
            p.SkipWhite();
            if (!p.AtEnd) throw p.Error("unexpected text after the end");
            return v;
        }

        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

        public static List<object> Arr(object o) { return o as List<object> ?? new List<object>(); }

        public static object Get(object obj, string key)
        {
            var d = Obj(obj);
            return d != null && d.TryGetValue(key, out object v) ? v : null;
        }

        /// <summary>The value as text: strings as they are, numbers in invariant form, null as "".</summary>
        public static string Str(object o)
        {
            if (o == null) return "";
            if (o is string s) return s;
            if (o is bool b) return b ? "true" : "false";
            if (o is decimal m) return m.ToString(CultureInfo.InvariantCulture);
            if (o is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            return "";
        }

        /// <summary>The value as an int (numbers or numeric strings); <paramref name="fallback"/> otherwise.</summary>
        public static int Int(object o, int fallback = 0)
        {
            if (o is decimal m && m >= int.MinValue && m <= int.MaxValue) return (int)m;
            if (o is string s && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
            return fallback;
        }

        sealed class Reader
        {
            readonly string s;
            int i;

            public Reader(string s) { this.s = s; }

            public bool AtEnd => i >= s.Length;

            public Exception Error(string what) { return new FormatException("Bad JSON at character " + i + ": " + what); }

            public void SkipWhite()
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n' || s[i] == '﻿')) i++;
            }

            public object ReadValue()
            {
                if (AtEnd) throw Error("unexpected end");
                char c = s[i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                throw Error("unexpected '" + c + "'");
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Error("expected " + word);
                i += word.Length;
            }

            Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; // {
                SkipWhite();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWhite();
                    if (AtEnd || s[i] != '"') throw Error("expected a property name");
                    string key = ReadString();
                    SkipWhite();
                    if (AtEnd || s[i] != ':') throw Error("expected ':'");
                    i++;
                    SkipWhite();
                    d[key] = ReadValue();
                    SkipWhite();
                    if (AtEnd) throw Error("unexpected end in object");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw Error("expected ',' or '}'");
                }
            }

            List<object> ReadArray()
            {
                var list = new List<object>();
                i++; // [
                SkipWhite();
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    SkipWhite();
                    list.Add(ReadValue());
                    SkipWhite();
                    if (AtEnd) throw Error("unexpected end in array");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw Error("expected ',' or ']'");
                }
            }

            string ReadString()
            {
                var sb = new StringBuilder();
                i++; // opening quote
                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Error("unterminated escape");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                                throw Error("bad \\u escape");
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: throw Error("bad escape \\" + e);
                    }
                }
            }

            object ReadNumber()
            {
                int start = i;
                if (s[i] == '-') i++;
                while (i < s.Length && "0123456789.eE+-".IndexOf(s[i]) >= 0) i++;
                string t = s.Substring(start, i - start);
                if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal m)) return m;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                throw Error("bad number " + t);
            }
        }
    }
}
