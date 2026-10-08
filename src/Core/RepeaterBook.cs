using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeplugBuilder.Core
{
    // RepeaterBook without the API, through CHIRP files. Two flavours, both checked on real files (2026-10-06):
    //
    // RepeaterBook's own "CHIRP" export (one county):
    //   Location,Name,Frequency,Duplex,Offset,Tone,rToneFreq,cToneFreq,DtcsCode,DtcsPolarity,Mode,TStep,Comment
    //   1,"K0TSTA",444.700000,+,5,TSQL,94.8,94.8,023,NN,FM,5,"Brownwood",
    // Name is the callsign, Comment the city ("Brownwood, Bangs Hill"); no county or state; rows end with an extra comma.
    //
    // CHIRP's own CSV after its RepeaterBook query (all of Texas, 1,365 lines: 1,025 FM, 173 DN, 124 DMR, 43 DV):
    //   Location,Name,Frequency,Duplex,Offset,Tone,rToneFreq,cToneFreq,DtcsCode,DtcsPolarity,RxDtcsCode,CrossMode,Mode,
    //   TStep,Skip,Power,Comment,URCALL,RPT1CALL,RPT2CALL,DVCODE
    //   0,St Davids Surgical Hospital,29.640000,-,0.100000,TSQL,88.5,110.9,023,NN,023,Tone->Tone,FM,5.00,,50W,
    //     "WD5EMS near Round Rock, Williamson County, Texas OPEN",,,,
    // Name is often a site or club name, not the callsign; the comment is "CALL near Town, X County, State STATUS notes"
    // (a dozen lines leave out the callsign). No color codes, so DMR lines are left to RadioID.
    //
    // Frequency is the repeater's output. Tones follow CHIRP: "Tone" = encode rToneFreq only, "TSQL" = cToneFreq both
    // ways, "DTCS" = DtcsCode both ways, "Cross" = CrossMode "TX->RX" (Tone = rToneFreq out / cToneFreq in, DTCS =
    // DtcsCode out / RxDtcsCode in; "Tone->Tone" when the column is missing).

    /// <summary>One channel from a CHIRP CSV (RepeaterBook's CHIRP export or CHIRP's own).</summary>
    public sealed class ChirpChannel
    {
        public int Row { get; set; }
        public string Name { get; set; } = "";
        /// <summary>The Comment column: the city (RepeaterBook export) or "CALL near Town, X County, State OPEN ..." (CHIRP).</summary>
        public string Comment { get; set; } = "";
        public decimal RxMHz { get; set; }
        public decimal TxMHz { get; set; }
        public bool RxOnly { get; set; }
        public string ToneEncode { get; set; } = "Off";
        public string ToneDecode { get; set; } = "Off";
        public bool ToneSquelch { get; set; }
        public string Mode { get; set; } = "FM";
        public bool IsAnalog => Mode == "FM" || Mode == "NFM";

        /// <summary>From a "CALL near Town, X County, State STATUS" comment; empty otherwise.</summary>
        public string County { get; private set; } = "";
        public string State { get; private set; } = "";
        public string Status { get; private set; } = "";
        string town = "", call = "";

        /// <summary>The town: from "near Town, ..." or the first part of a plain comment ("Brownwood, Bangs Hill" → "Brownwood").</summary>
        public string City
        {
            get
            {
                if (town.Length > 0) return town;
                string c = (Comment ?? "").Trim();
                int comma = c.IndexOf(',');
                return (comma > 0 ? c.Substring(0, comma) : c).Trim();
            }
        }

        /// <summary>The callsign: from the comment when it starts with one, else the Name column.</summary>
        public string Callsign => call.Length > 0 ? call : Naming.Clean(Name, 0);

        static readonly Regex Near = new Regex(
            @"^(?:(?<call>[A-Z0-9/]{3,10})\s+)?near\s+(?<town>.+?),\s*(?<county>[^,]+?\s(?:County|Parish|Borough|Census Area|Municipality)),\s*(?<state>[^,]+?)\s+(?<status>OPEN|CLOSED|PRIVATE|RESTRICTED|OFF-AIR|TESTING)\b",
            RegexOptions.IgnoreCase);
        static readonly Regex CallsignLike = new Regex(@"^[A-Z]{1,2}[0-9][A-Z]{1,4}$");

        /// <summary>Reads the parts of a CHIRP-style comment (called once the columns are set).</summary>
        internal void ReadComment()
        {
            var m = Near.Match((Comment ?? "").Trim());
            if (!m.Success) return;
            town = m.Groups["town"].Value.Trim();
            County = m.Groups["county"].Value.Trim();
            State = m.Groups["state"].Value.Trim();
            Status = m.Groups["status"].Value.ToUpperInvariant();
            string c = m.Groups["call"].Value.ToUpperInvariant();
            if (CallsignLike.IsMatch(c)) call = c;
        }
    }

    public static class ChirpCsv
    {
        /// <summary>True for a CHIRP channel list (header has Frequency, Duplex and Offset).</summary>
        public static bool IsChirp(CsvTable t)
        {
            return t.IndexOf("Frequency") >= 0 && t.IndexOf("Duplex") >= 0 && t.IndexOf("Offset") >= 0;
        }

        /// <summary>The channels in a CHIRP CSV. Rows that can't be read are reported in <paramref name="notes"/>.</summary>
        public static List<ChirpChannel> Parse(CsvTable t, List<string> notes = null)
        {
            var list = new List<ChirpChannel>();
            if (!IsChirp(t)) { notes?.Add("This isn't a CHIRP file (no Frequency, Duplex and Offset columns)."); return list; }
            int n = 1;
            foreach (var row in t.Rows)
            {
                n++;
                string Col(string name) { return (t.Get(row, name) ?? "").Trim(); }
                if (!TryMHz(Col("Frequency"), out decimal rx)) { notes?.Add("Line " + n + ": no frequency, skipped."); continue; }
                var c = new ChirpChannel { Row = n, Name = Col("Name"), Comment = Col("Comment"), RxMHz = rx, TxMHz = rx };
                string duplex = Col("Duplex").ToLowerInvariant();
                TryMHz(Col("Offset"), out decimal offset);
                if (duplex == "+") c.TxMHz = rx + offset;
                else if (duplex == "-") c.TxMHz = rx - offset;
                else if (duplex == "split" && offset > 0) c.TxMHz = offset;
                else if (duplex == "off") c.RxOnly = true;

                string rTone = Col("rToneFreq"), cTone = Col("cToneFreq");
                string pol = Col("DtcsPolarity").ToUpperInvariant();
                string txDcs = Dcs(Col("DtcsCode"), pol.Length > 0 ? pol[0] : 'N');
                string rxCode = Col("RxDtcsCode");
                switch (Col("Tone").ToLowerInvariant())
                {
                    case "tone":
                        c.ToneEncode = Tones.Normalize(rTone);
                        break;
                    case "tsql":
                        c.ToneEncode = c.ToneDecode = Tones.Normalize(cTone);
                        c.ToneSquelch = true;
                        break;
                    case "dtcs":
                        c.ToneEncode = txDcs;
                        c.ToneDecode = Dcs(Col("DtcsCode"), pol.Length > 1 ? pol[1] : 'N');
                        c.ToneSquelch = true;
                        break;
                    case "cross":
                        string cross = Col("CrossMode");
                        if (cross.Length == 0) cross = "Tone->Tone";
                        int arrow = cross.IndexOf("->", StringComparison.Ordinal);
                        string tx = arrow >= 0 ? cross.Substring(0, arrow).Trim().ToLowerInvariant() : "tone";
                        string rxm = arrow >= 0 ? cross.Substring(arrow + 2).Trim().ToLowerInvariant() : "tone";
                        c.ToneEncode = tx == "tone" ? Tones.Normalize(rTone) : tx == "dtcs" ? txDcs : "Off";
                        c.ToneDecode = rxm == "tone" ? Tones.Normalize(cTone)
                                     : rxm == "dtcs" ? Dcs(rxCode.Length > 0 ? rxCode : Col("DtcsCode"), pol.Length > 1 ? pol[1] : 'N') : "Off";
                        c.ToneSquelch = c.ToneDecode != "Off";
                        break;
                }
                string mode = Col("Mode").ToUpperInvariant();
                c.Mode = mode.Length == 0 ? "FM" : mode;
                c.ReadComment();
                list.Add(c);
            }
            return list;
        }

        static string Dcs(string code, char polarity)
        {
            return "D" + (code ?? "").Trim().PadLeft(3, '0') + (polarity == 'R' ? "I" : "N");
        }

        static bool TryMHz(string s, out decimal v)
        {
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v) && v > 0 && v < 10000;
        }
    }

    /// <summary>How imported repeaters are sorted into zones.</summary>
    public enum ImportZoning { OneZone, PerCity, PerCounty }

    public sealed class RepeaterBookResult
    {
        public List<Repeater> Added { get; } = new List<Repeater>();
        public List<string> Notes { get; } = new List<string>();
    }

    public static class RepeaterBookImport
    {
        public const string Site = "RepeaterBook";
        public const string HomeUrl = "https://www.repeaterbook.com/";

        /// <summary>
        /// The project's analog repeater that is this machine, or null: same frequencies and either its callsign (a word of
        /// the name or notes starting with it: "N0FAKE2" for N0FAKE) or the same town. Frequency pairs are reused across a
        /// state, so frequencies alone aren't enough.
        /// </summary>
        public static Repeater FindExisting(Project p, ChirpChannel c)
        {
            string call = c.Callsign;
            bool Mentions(string text)
            {
                return call.Length >= 3 && (text ?? "").Split(new[] { ' ', ',', '|', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                                       .Any(w => w.StartsWith(call, StringComparison.OrdinalIgnoreCase));
            }
            return p.Repeaters.FirstOrDefault(r => !r.IsDigital && r.RxMHz == c.RxMHz && (r.TxMHz == c.TxMHz || c.RxOnly) &&
                (Mentions(r.Name) || Mentions(r.Notes) || (c.City.Length > 0 && string.Equals(r.City, c.City, StringComparison.OrdinalIgnoreCase))));
        }

        /// <summary>Why a channel can't be added, or null when it can.</summary>
        public static string Problem(ChirpChannel c)
        {
            if (!c.IsAnalog) return c.Mode == "DMR" ? "DMR: those come from RadioID, with color codes" : c.Mode + " (not FM)";
            if (!Validator.InRadioBand(c.RxMHz) || (!c.RxOnly && !Validator.InRadioBand(c.TxMHz))) return "outside the radio's bands";
            return null;
        }

        /// <summary>
        /// Where a listing is: its city on the built-in map within <paramref name="state"/> (the export doesn't say).
        /// Small towns aren't on the map (it has places of 15,000+ people), so this can come back without a county.
        /// </summary>
        public static GeoLocation Locate(GeoAtlas atlas, ChirpChannel c, string state, string country = "United States")
        {
            return atlas == null ? GeoLocation.Unknown : atlas.Locate(c.City, state, country);
        }

        /// <summary>
        /// The zone to suggest for a whole export: the county most of its cities are in ("Brown Co"), else the first
        /// city, else "RepeaterBook".
        /// </summary>
        public static string SuggestZone(IEnumerable<GeoLocation> locations, IEnumerable<ChirpChannel> channels)
        {
            var county = locations.Where(l => l?.County != null).GroupBy(l => l.County).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (county != null) return ZonePlanner.CountyZone(county.Name);
            string city = channels.Select(c => c.City).FirstOrDefault(x => x.Length > 0);
            return city != null ? Naming.Fit(city, 16) : Site;
        }

        /// <summary>
        /// "K0TSTA Brownwood". A callsign with several machines in the same town gets the band instead ("K0TSTA VHF",
        /// "K0TSTA UHF"), the way hams usually name them.
        /// </summary>
        public static string ChannelName(ChirpChannel c, IEnumerable<ChirpChannel> all)
        {
            string call = c.Callsign;
            bool several = call.Length > 0 && all.Count(x => string.Equals(x.Callsign, call, StringComparison.OrdinalIgnoreCase) &&
                                                             string.Equals(x.City, c.City, StringComparison.OrdinalIgnoreCase)) > 1;
            if (several) return Naming.Fit(call + " " + Band(c.RxMHz), 16);
            return Naming.Fit(call.Length == 0 ? c.City : c.City.Length == 0 ? call : call + " " + c.City, 16);
        }

        static string Band(decimal mhz)
        {
            return mhz < 300 ? "VHF" : "UHF";
        }

        /// <summary>
        /// Adds the picked channels as analog repeaters (receive-only when the export says so), skipping ones already
        /// in the project. <paramref name="zoneFor"/> gives each one's zone; names are kept unique.
        /// </summary>
        public static RepeaterBookResult Add(Project p, IList<ChirpChannel> picked, IList<ChirpChannel> all, Func<ChirpChannel, GeoLocation, string> zoneFor,
                                             Func<ChirpChannel, GeoLocation> locate, string power = "High")
        {
            var result = new RepeaterBookResult();
            var spelling = ZonePlanner.Spellings(p);
            var names = new UniqueNamer(16, "Repeater");
            foreach (var r in p.AllRepeaters()) names.Reserve(Naming.Fit(r.Name, 16)); // as the generator will name them
            var skipped = new List<string>();
            foreach (var c in picked)
            {
                string problem = Problem(c);
                if (problem != null) { skipped.Add(c.Callsign + " " + c.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) + " (" + problem + ")"); continue; }
                var existing = FindExisting(p, c);
                if (existing != null) { skipped.Add(c.Callsign + " " + c.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) + " (already in the project as \"" + existing.Name + "\")"); continue; }

                var loc = locate?.Invoke(c) ?? GeoLocation.Unknown;
                var r = NewRepeater(c, names.Claim(ChannelName(c, all)), loc, power);
                string zone = Naming.Fit(zoneFor(c, loc), 16);
                r.Zone = ZonePlanner.Canonical(spelling, zone.Length > 0 ? zone : Site);
                p.Repeaters.Add(r);
                result.Added.Add(r);
            }
            p.SyncZones();
            if (skipped.Count > 0) result.Notes.Add("Skipped: " + string.Join("; ", skipped) + ".");
            return result;
        }

        /// <summary>An analog repeater for a CHIRP channel (no zone yet): frequencies, tones, bandwidth, place, notes.</summary>
        public static Repeater NewRepeater(ChirpChannel c, string name, GeoLocation loc, string power = "High")
        {
            loc = loc ?? GeoLocation.Unknown;
            var r = Repeater.NewAnalog(name);
            r.RxMHz = c.RxMHz;
            r.TxMHz = c.RxOnly ? c.RxMHz : c.TxMHz;
            r.RxOnly = c.RxOnly;
            r.ToneEncode = c.ToneEncode;
            r.ToneDecode = c.ToneDecode;
            r.ToneSquelch = c.ToneSquelch;
            r.Bandwidth = c.Mode == "NFM" ? Bandwidths.Narrow : Bandwidths.Wide;
            if (Powers.Values.Contains(power)) r.Power = power;
            r.City = string.IsNullOrWhiteSpace(c.City) ? null : c.City;
            r.State = loc.State?.Name;
            r.Country = loc.Country?.Name;
            r.County = loc.County?.Name;
            r.AreaCode = (loc.County ?? loc.State ?? loc.Country)?.Code;
            r.Latitude = loc.Lat.HasValue ? Math.Round(loc.Lat.Value, 4) : (double?)null;
            r.Longitude = loc.Lon.HasValue ? Math.Round(loc.Lon.Value, 4) : (double?)null;
            r.Notes = Site + " (CHIRP export)" + (c.Comment.Length > 0 ? " | " + c.Comment : "");
            return r;
        }

        /// <summary>
        /// The state or province a CHIRP file is for, from its name or its folder: "Texas.csv", "TX.csv",
        /// "rb_chirp_New_Mexico.csv", "Texas\rb_chirp_2610061815.csv". Returns the state's full name and its country,
        /// or null when the name doesn't say.
        /// </summary>
        public static KeyValuePair<string, string>? StateFromName(string path)
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(path ?? "");
            string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path ?? "") ?? "");
            foreach (string part in new[] { file, folder })
            {
                string spaced = Regex.Replace(part ?? "", @"(?<=[a-z])(?=[A-Z])", " "); // "TexasRepeaters" → "Texas Repeaters"
                string text = " " + Regex.Replace(spaced, @"[\s_\-\.]+", " ").Trim() + " ";
                // Full names first, longest first ("West Virginia" before "Virginia").
                foreach (var kv in Naming.StateCodes.Where(s => !s.Key.StartsWith("Washington DC")).OrderByDescending(s => s.Key.Length))
                    if (text.IndexOf(" " + kv.Key + " ", StringComparison.OrdinalIgnoreCase) >= 0) return State(kv);
                foreach (string token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (token.Length == 2 && token == token.ToUpperInvariant())
                        foreach (var kv in Naming.StateCodes)
                            if (kv.Value == token && kv.Key != "Washington DC") return State(kv);
            }
            return null;
        }

        static readonly HashSet<string> CanadianCodes = new HashSet<string> { "BC", "NB", "NL", "NS", "PE", "NT", "AB", "MB", "ON", "QC", "SK", "YT", "NU" };

        static KeyValuePair<string, string>? State(KeyValuePair<string, string> kv)
        {
            return new KeyValuePair<string, string>(kv.Key, CanadianCodes.Contains(kv.Value) ? "Canada" : "United States");
        }

        /// <summary>
        /// A CHIRP file's channels as map listings (<see cref="OnlineRepeater.Analog"/> set), each placed at its town in
        /// <paramref name="state"/>. Digital and out-of-band lines are left out.
        /// </summary>
        public static List<OnlineRepeater> ToListings(IEnumerable<ChirpChannel> channels, string state, string country, GeoAtlas atlas)
        {
            var list = new List<OnlineRepeater>();
            foreach (var c in channels)
            {
                if (Problem(c) != null) continue;
                string tone = c.ToneEncode == "Off" ? "no tone" : c.ToneSquelch && c.ToneDecode == c.ToneEncode ? "tone " + c.ToneEncode + " both ways"
                            : c.ToneDecode == "Off" ? "tone " + c.ToneEncode : "tone " + c.ToneEncode + " / " + c.ToneDecode;
                string st = c.State.Length > 0 ? c.State : state ?? "";
                list.Add(new OnlineRepeater
                {
                    Callsign = c.Callsign.ToUpperInvariant(),
                    City = c.City,
                    State = st,
                    Country = country ?? "",
                    Network = "FM",
                    Status = c.Status,
                    Details = Site + ": " + c.Comment + ", " + tone,
                    RxMHz = c.RxMHz,
                    TxMHz = c.RxOnly ? c.RxMHz : c.TxMHz,
                    Analog = c,
                    Location = Place(atlas, c, st, country),
                });
            }
            return list;
        }

        /// <summary>
        /// Where a CHIRP line is: its town on the map. When the comment names the county (CHIRP's RepeaterBook query
        /// does) that county wins: a town the map doesn't know, or one it finds in another county, is put at the
        /// county's middle instead, so county zones still come out right.
        /// </summary>
        public static GeoLocation Place(GeoAtlas atlas, ChirpChannel c, string state, string country)
        {
            if (atlas == null) return null;
            var loc = atlas.Locate(c.City, state, country);
            if (c.County.Length == 0 || loc.State == null) return loc;
            var county = atlas.FindCounty(loc.State, c.County);
            if (county == null || loc.County == county) return loc;
            return new GeoLocation
            {
                Country = loc.Country, State = loc.State, County = county,
                Lat = county.LabelLat, Lon = county.LabelLon, Precision = LocationPrecision.State,
            };
        }
    }
}
