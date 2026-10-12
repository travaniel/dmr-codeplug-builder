using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    // RepeaterBook listings come from its API with the user's own token (RepeaterBookApi.ParseListings). DMR ones become
    // OnlineRepeaters like RadioID.net's (color code, no talkgroups); analog ones carry an AnalogListing with the tones.
    // Frequency is the repeater's output. Tones: "PL" is what the radio sends, "TSQ" what it listens for.

    /// <summary>One analog (FM) repeater listed on RepeaterBook.</summary>
    public sealed class AnalogListing
    {
        /// <summary>The callsign as listed.</summary>
        public string Name { get; set; } = "";
        /// <summary>Town and landmark ("Brownwood, Bangs Hill").</summary>
        public string Comment { get; set; } = "";
        public decimal RxMHz { get; set; }
        public decimal TxMHz { get; set; }
        public string ToneEncode { get; set; } = "Off";
        public string ToneDecode { get; set; } = "Off";
        public bool ToneSquelch { get; set; }
        /// <summary>"FM" (25 kHz) or "NFM" (12.5 kHz).</summary>
        public string Mode { get; set; } = "FM";
        public bool IsAnalog => Mode == "FM" || Mode == "NFM";

        /// <summary>"Brown County" (named like the built-in map), the state, and OPEN / CLOSED / PRIVATE / OFF-AIR.</summary>
        public string County { get; private set; } = "";
        public string State { get; private set; } = "";
        public string Status { get; private set; } = "";
        public string City { get; private set; } = "";
        public string Callsign => Naming.Clean(Name, 0).ToUpperInvariant();

        /// <summary>RepeaterBook's own ID for this repeater, and its state ID (FIPS, "48"), for the detail-page link.</summary>
        public string RepeaterBookId { get; set; } = "";
        public string RepeaterBookStateId { get; set; } = "";
        /// <summary>The repeater's page on RepeaterBook, or null without an ID.</summary>
        public string DetailUrl => RepeaterBookApi.DetailUrl(RepeaterBookStateId, RepeaterBookId);

        internal void SetPlace(string city, string county, string state, string status)
        {
            City = (city ?? "").Trim();
            County = (county ?? "").Trim();
            State = (state ?? "").Trim();
            Status = (status ?? "").Trim().ToUpperInvariant();
        }
    }

    public static class RepeaterBookImport
    {
        public const string Site = "RepeaterBook";

        /// <summary>
        /// The project's analog repeater that is this machine, or null: same frequencies and either its callsign (a word of
        /// the name or notes starting with it: "N0FAKE2" for N0FAKE) or the same town. Frequency pairs are reused across a
        /// state, so frequencies alone aren't enough.
        /// </summary>
        public static Repeater FindExisting(Project p, AnalogListing c)
        {
            string call = c.Callsign;
            bool Mentions(string text)
            {
                return call.Length >= 3 && (text ?? "").Split(new[] { ' ', ',', '|', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                                       .Any(w => w.StartsWith(call, StringComparison.OrdinalIgnoreCase));
            }
            return p.Repeaters.FirstOrDefault(r => !r.IsDigital && r.RxMHz == c.RxMHz && r.TxMHz == c.TxMHz &&
                (Mentions(r.Name) || Mentions(r.Notes) || (c.City.Length > 0 && string.Equals(r.City, c.City, StringComparison.OrdinalIgnoreCase))));
        }

        /// <summary>Why a listing can't be added, or null when it can.</summary>
        public static string Problem(AnalogListing c)
        {
            if (!c.IsAnalog) return c.Mode + " (not FM)";
            if (c.Status == "OFF-AIR") return "off the air (RepeaterBook)";
            if (!Validator.InRadioBand(c.RxMHz) || !Validator.InRadioBand(c.TxMHz)) return "outside the radio's bands";
            return null;
        }

        /// <summary>
        /// "K0TSTA Brownwood". A callsign with several machines in the same town gets the band instead ("K0TSTA VHF",
        /// "K0TSTA UHF"), the way hams usually name them.
        /// </summary>
        public static string ChannelName(AnalogListing c, IEnumerable<AnalogListing> all)
        {
            string call = c.Callsign;
            bool several = call.Length > 0 && all.Count(x => string.Equals(x.Callsign, call, StringComparison.OrdinalIgnoreCase) &&
                                                             string.Equals(x.City, c.City, StringComparison.OrdinalIgnoreCase)) > 1;
            if (several) return Naming.Fit(call + " " + (c.RxMHz < 300 ? "VHF" : "UHF"), 16);
            return Naming.Fit(call.Length == 0 ? c.City : c.City.Length == 0 ? call : call + " " + c.City, 16);
        }

        /// <summary>An analog repeater for a listing (no zone yet): frequencies, tones, bandwidth, place, and its RepeaterBook page in the notes.</summary>
        public static Repeater NewRepeater(AnalogListing c, string name, GeoLocation loc, string power = "High")
        {
            loc = loc ?? GeoLocation.Unknown;
            var r = Repeater.NewAnalog(name);
            r.RxMHz = c.RxMHz;
            r.TxMHz = c.TxMHz;
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
            r.Notes = Site + (c.DetailUrl != null ? " " + c.DetailUrl : "") + (c.Comment.Length > 0 ? " | " + c.Comment : "");
            return r;
        }

        /// <summary>
        /// Where a listing is: its town on the map. RepeaterBook names the county, and that wins: a town the map doesn't
        /// know (it has places of 15,000+ people), or one it finds in another county, is put at the county's middle, so
        /// county zones still come out right.
        /// </summary>
        public static GeoLocation Place(GeoAtlas atlas, string city, string county, string state, string country)
        {
            if (atlas == null) return null;
            var loc = atlas.Locate(city, state, country);
            if (string.IsNullOrEmpty(county) || loc.State == null) return loc;
            var c = atlas.FindCounty(loc.State, county);
            if (c == null || loc.County == c) return loc;
            return new GeoLocation
            {
                Country = loc.Country, State = loc.State, County = c,
                Lat = c.LabelLat, Lon = c.LabelLon, Precision = LocationPrecision.State,
            };
        }

        /// <summary>The zone to suggest for one-zone imports: the county most listings are in ("Brown Co"), else the first town, else "RepeaterBook".</summary>
        public static string SuggestZone(IEnumerable<OnlineRepeater> listings)
        {
            var county = listings.Select(l => l.Location?.County).Where(c => c != null).GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (county != null) return ZonePlanner.CountyZone(county.Name);
            string city = listings.Select(l => l.City).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            return city != null ? Naming.Fit(city, 16) : Site;
        }
    }
}
