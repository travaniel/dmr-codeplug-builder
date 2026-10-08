using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>A frequency range in MHz, both ends included.</summary>
    public sealed class FrequencyRange
    {
        public decimal Low { get; }
        public decimal High { get; }

        public FrequencyRange(decimal low, decimal high) { Low = low; High = high; }

        public bool Contains(decimal mhz) { return mhz >= Low && mhz <= High; }

        /// <summary>"144-148", "145.8-146".</summary>
        public override string ToString() { return AmateurBands.Mhz(Low, "0.###") + "-" + AmateurBands.Mhz(High, "0.###"); }
    }

    /// <summary>A frequency a hotspot or DMR simplex channel should keep off: APRS, a calling frequency, the ISS.</summary>
    public sealed class KeepClearFrequency
    {
        public decimal MHz { get; }
        /// <summary>"the APRS frequency", "the national 2 m FM calling frequency"...</summary>
        public string What { get; }

        public KeepClearFrequency(decimal mhz, string what) { MHz = mhz; What = what; }
    }

    /// <summary>
    /// The amateur 2 m and 70 cm allocations (the only ones inside the radio's 136-174 / 400-480 MHz) of one country,
    /// and the frequencies a hotspot or DMR simplex channel should stay off there. Used by <see cref="Validator"/> for
    /// its safety warnings. A country not in the table gets the widest allocation anywhere (144-148, 420-450 MHz), so
    /// "outside the amateur bands" is never said about a frequency that is amateur somewhere.
    /// </summary>
    public sealed class AmateurBands
    {
        /// <summary>ISO country code, or "" when the project's country isn't known.</summary>
        public string Country { get; }
        /// <summary>"US", "Canadian", "European"... or "" for the worldwide fallback.</summary>
        public string Adjective { get; }
        public IReadOnlyList<FrequencyRange> Transmit { get; }
        /// <summary>Segments a hotspot must stay out of besides the satellite sub-bands (US: Part 97.201(b) for auxiliary stations).</summary>
        public IReadOnlyList<FrequencyRange> HotspotExcluded { get; }
        public IReadOnlyList<KeepClearFrequency> KeepClear { get; }

        /// <summary>Amateur-satellite sub-bands, worldwide: no terrestrial hotspots or DMR simplex here.</summary>
        public static readonly IReadOnlyList<FrequencyRange> Satellite = new[] { new FrequencyRange(145.8m, 146.0m), new FrequencyRange(435m, 438m) };

        /// <summary>A hotspot or DMR simplex channel this close (MHz) to a <see cref="KeepClear"/> frequency gets a warning.</summary>
        public const decimal KeepClearSpacing = 0.0125m;

        AmateurBands(string country, string adjective, FrequencyRange[] transmit, FrequencyRange[] hotspotExcluded, KeepClearFrequency[] keepClear)
        {
            Country = country;
            Adjective = adjective;
            Transmit = transmit;
            HotspotExcluded = hotspotExcluded;
            KeepClear = keepClear;
        }

        static readonly KeepClearFrequency Iss = new KeepClearFrequency(145.825m, "the ISS APRS digipeater");

        static readonly KeepClearFrequency[] NorthAmerica =
        {
            new KeepClearFrequency(144.390m, "the APRS frequency"),
            Iss,
            new KeepClearFrequency(146.520m, "the national 2 m FM calling frequency"),
            new KeepClearFrequency(446.000m, "the national 70 cm FM calling frequency"),
        };

        static readonly KeepClearFrequency[] Europe =
        {
            new KeepClearFrequency(144.800m, "the APRS frequency"),
            new KeepClearFrequency(145.500m, "the 2 m FM calling frequency"),
            Iss,
            new KeepClearFrequency(433.500m, "the 70 cm FM calling frequency"),
        };

        // CEPT countries: IARU Region 1 band edges, 144-146 and 430-440 MHz.
        static readonly HashSet<string> European = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AD", "AL", "AT", "BA", "BE", "BG", "BY", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FO", "FR", "GB", "GG",
            "GI", "GR", "HR", "HU", "IE", "IM", "IS", "IT", "JE", "LI", "LT", "LU", "LV", "MC", "MD", "ME", "MK", "MT", "NL",
            "NO", "PL", "PT", "RO", "RS", "RU", "SE", "SI", "SK", "SM", "UA", "VA", "XK",
        };

        static FrequencyRange[] Bands(decimal top2m, decimal low70cm, decimal top70cm)
        {
            return new[] { new FrequencyRange(144m, top2m), new FrequencyRange(low70cm, top70cm) };
        }

        /// <summary>The rules for an ISO country code ("US", "DE"); null, "" or an unknown code gives the worldwide fallback.</summary>
        public static AmateurBands For(string country)
        {
            string c = (country ?? "").Trim().ToUpperInvariant();
            switch (c)
            {
                case "US":
                case "PR": case "VI": case "GU": case "AS": case "MP":
                    return new AmateurBands("US", "US", Bands(148m, 420m, 450m), UsAuxiliaryExcluded, NorthAmerica);
                case "CA":
                    return new AmateurBands("CA", "Canadian", Bands(148m, 430m, 450m), new FrequencyRange[0], NorthAmerica);
                case "AU":
                    return new AmateurBands("AU", "Australian", Bands(148m, 420m, 450m), new FrequencyRange[0],
                        new[] { new KeepClearFrequency(145.175m, "the APRS frequency"), Iss });
                case "NZ":
                    return new AmateurBands("NZ", "New Zealand", Bands(148m, 430m, 440m), new FrequencyRange[0], new[] { Iss });
                case "JP":
                    return new AmateurBands("JP", "Japanese", Bands(146m, 430m, 440m), new FrequencyRange[0], new[] { Iss });
            }
            if (European.Contains(c))
                return new AmateurBands(c, "European", Bands(146m, 430m, 440m), new FrequencyRange[0], Europe);
            // Unknown: the widest allocation, and North America's frequencies (most projects without locations are
            // imported from a US user's CPS).
            return new AmateurBands("", "", Bands(148m, 420m, 450m), UsAuxiliaryExcluded, NorthAmerica);
        }

        // Part 97.201(b): an auxiliary station (a hotspot) may not transmit on 144.0-144.5, 145.8-146.0, 431-433 or
        // 435-438 MHz. The satellite parts are checked for every country; these are the rest.
        static readonly FrequencyRange[] UsAuxiliaryExcluded = { new FrequencyRange(144.0m, 144.5m), new FrequencyRange(431m, 433m) };

        /// <summary>
        /// The project's country: the one most of its repeaters are in (their map <see cref="Repeater.AreaCode"/>), or ""
        /// when none has a location (typed in by hand or imported from the CPS).
        /// </summary>
        public static string CountryOf(Project p)
        {
            return p.AllRepeaters()
                .Select(r => CountryCode(r.AreaCode))
                .Where(c => c.Length > 0)
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)
                .FirstOrDefault() ?? "";
        }

        /// <summary>"US-48451" → "US", "DE" → "DE".</summary>
        public static string CountryCode(string areaCode)
        {
            string a = (areaCode ?? "").Trim();
            int dash = a.IndexOf('-');
            string c = dash >= 0 ? a.Substring(0, dash) : a;
            return c.Length == 2 && char.IsLetter(c[0]) && char.IsLetter(c[1]) ? c.ToUpperInvariant() : "";
        }

        public bool CanTransmit(decimal mhz) { return Transmit.Any(b => b.Contains(mhz)); }

        public static FrequencyRange SatelliteBand(decimal mhz) { return Satellite.FirstOrDefault(b => b.Contains(mhz)); }

        public FrequencyRange HotspotExcludedBand(decimal mhz) { return HotspotExcluded.FirstOrDefault(b => b.Contains(mhz)); }

        /// <summary>The keep-clear frequency 12.5 kHz or less from <paramref name="mhz"/> (on it, or the next channel, which overlaps a 25 kHz FM signal), or null.</summary>
        public KeepClearFrequency NearKeepClear(decimal mhz)
        {
            return KeepClear.FirstOrDefault(k => Math.Abs(k.MHz - mhz) <= KeepClearSpacing);
        }

        /// <summary>"the US amateur bands (144-148, 420-450 MHz)" or "the amateur bands (144-148, 420-450 MHz)".</summary>
        public string Describe()
        {
            return "the " + (Adjective.Length > 0 ? Adjective + " " : "") + "amateur bands (" + string.Join(", ", Transmit) + " MHz)";
        }

        /// <summary>A frequency for messages: "154.570", "433.5125"; invariant culture, so never "154,570".</summary>
        public static string Mhz(decimal mhz, string format = "0.000##")
        {
            return mhz.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
