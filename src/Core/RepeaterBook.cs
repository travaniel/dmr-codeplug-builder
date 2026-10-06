using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    // RepeaterBook without the API: the user exports a search (a county, a city...) from repeaterbook.com in CHIRP
    // format and the app reads the file. Checked against a real export (Brown County, TX, 2026-10-06):
    //
    //   Location,Name,Frequency,Duplex,Offset,Tone,rToneFreq,cToneFreq,DtcsCode,DtcsPolarity,Mode,TStep,Comment
    //   1,"K5BWD",444.700000,+,5,TSQL,94.8,94.8,023,NN,FM,5,"Brownwood",
    //   7,"K5BWD",146.940000,-,0.6,Tone,94.8,88.5,023,NN,FM,5,"Brownwood",
    //
    // Name is the callsign, Frequency the repeater's output, Comment the city (sometimes "City, site"). No county,
    // state or color code; rows end with an extra comma. Tones follow CHIRP: "Tone" = encode rToneFreq only,
    // "TSQL" = encode and decode cToneFreq, "DTCS" = DtcsCode both ways, "Cross" = rToneFreq out, cToneFreq in.

    /// <summary>One channel from a CHIRP CSV (RepeaterBook's CHIRP export or CHIRP's own).</summary>
    public sealed class ChirpChannel
    {
        public int Row { get; set; }
        public string Name { get; set; } = "";
        /// <summary>The Comment column; in RepeaterBook exports the city.</summary>
        public string Comment { get; set; } = "";
        public decimal RxMHz { get; set; }
        public decimal TxMHz { get; set; }
        public bool RxOnly { get; set; }
        public string ToneEncode { get; set; } = "Off";
        public string ToneDecode { get; set; } = "Off";
        public bool ToneSquelch { get; set; }
        public string Mode { get; set; } = "FM";
        public bool IsAnalog => Mode == "FM" || Mode == "NFM";

        /// <summary>The city part of the comment: "Brownwood, Bangs Hill" → "Brownwood".</summary>
        public string City
        {
            get
            {
                string c = (Comment ?? "").Trim();
                int comma = c.IndexOf(',');
                return (comma > 0 ? c.Substring(0, comma) : c).Trim();
            }
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
                string dcs = "D" + Col("DtcsCode").PadLeft(3, '0');
                string pol = Col("DtcsPolarity").ToUpperInvariant();
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
                        c.ToneEncode = dcs + (pol.Length > 0 && pol[0] == 'R' ? "I" : "N");
                        c.ToneDecode = dcs + (pol.Length > 1 && pol[1] == 'R' ? "I" : "N");
                        c.ToneSquelch = true;
                        break;
                    case "cross":
                        c.ToneEncode = Tones.Normalize(rTone);
                        c.ToneDecode = Tones.Normalize(cTone);
                        c.ToneSquelch = c.ToneDecode != "Off";
                        break;
                }
                string mode = Col("Mode").ToUpperInvariant();
                c.Mode = mode.Length == 0 ? "FM" : mode;
                list.Add(c);
            }
            return list;
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

        /// <summary>The project's analog repeater on the same frequencies (the same machine, already there), or null.</summary>
        public static Repeater FindExisting(Project p, ChirpChannel c)
        {
            return p.Repeaters.FirstOrDefault(r => !r.IsDigital && r.RxMHz == c.RxMHz && (r.TxMHz == c.TxMHz || c.RxOnly));
        }

        /// <summary>Why a channel can't be added, or null when it can.</summary>
        public static string Problem(ChirpChannel c)
        {
            if (!c.IsAnalog) return c.Mode + " (not FM)";
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
        /// "K5BWD Brownwood"; a callsign with several machines in the list gets the band instead ("K5BWD VHF",
        /// "K5BWD UHF"), the way hams usually name them.
        /// </summary>
        public static string ChannelName(ChirpChannel c, IEnumerable<ChirpChannel> all)
        {
            string call = Naming.Clean(c.Name, 0);
            bool several = all.Count(x => string.Equals(Naming.Clean(x.Name, 0), call, StringComparison.OrdinalIgnoreCase)) > 1;
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
            foreach (var r in p.AllRepeaters()) names.Reserve(Naming.Clean(r.Name, 16));
            var skipped = new List<string>();
            foreach (var c in picked)
            {
                string problem = Problem(c);
                if (problem != null) { skipped.Add(c.Name + " " + c.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) + " (" + problem + ")"); continue; }
                var existing = FindExisting(p, c);
                if (existing != null) { skipped.Add(c.Name + " " + c.RxMHz.ToString("0.000", CultureInfo.InvariantCulture) + " (already in the project as \"" + existing.Name + "\")"); continue; }

                var loc = locate?.Invoke(c) ?? GeoLocation.Unknown;
                var r = Repeater.NewAnalog(names.Claim(ChannelName(c, all)));
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
                r.Zone = ZonePlanner.Canonical(spelling, Naming.Fit(zoneFor(c, loc), 16).Length > 0 ? Naming.Fit(zoneFor(c, loc), 16) : Site);
                p.Repeaters.Add(r);
                result.Added.Add(r);
            }
            p.SyncZones();
            if (skipped.Count > 0) result.Notes.Add("Skipped: " + string.Join("; ", skipped) + ".");
            return result;
        }
    }
}
