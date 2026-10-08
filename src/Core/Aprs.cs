using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.Core
{
    /// <summary>What the radio's APRS should send (roadmap 1.4 item 2). Suggested from the project by <see cref="Aprs.Suggest"/>.</summary>
    public sealed class AprsPlan
    {
        public string Callsign = "";
        /// <summary>7 = handheld, 9 = mobile (APRS convention).</summary>
        public int Ssid = 7;
        /// <summary>Primary table "/" and symbol "[" (a person: the usual handheld symbol).</summary>
        public string SymbolTable = "/", Symbol = "[";
        public string Path = "WIDE1-1,WIDE2-1";
        /// <summary>The region's analog APRS frequency; 0 when there is none known for the region.</summary>
        public decimal FrequencyMHz;
        /// <summary>The 6X2 family's destination call (as on the user's radio).</summary>
        public string Destination = "APBT62";
        /// <summary>
        /// BrandMeister APRS gateway for digital reports (private call): 310999 for US masters. Not universal (each master has
        /// its own xxx999), so 0 outside the US.
        /// </summary>
        public int DigitalTalkgroup;
        /// <summary>Why a value is missing or a guess, for the UI.</summary>
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// APRS settings from what the project already knows. Pure; nothing here writes APRS.CSV or the radio yet: the meaning
    /// of APRS.CSV's channel1-8 / slot / Aprs Tg / Call Type columns and Channel.CSV's APRS Report Channel must be checked in
    /// the CPS first (HANDOFF 7), and the radio's APRS addresses (<see cref="RadioSettings"/>, 0x2501000) are unverified.
    /// </summary>
    public static class Aprs
    {
        /// <summary>The factory callsign the 6X2 PRO ships with in APRS (seen on the user's radio, HANDOFF 4).</summary>
        public const string FactoryCallsign = "BG6LKK";

        static readonly Regex CallsignPattern = new Regex(@"^(?=.*\d)(?=.*[A-Z])[A-Z0-9]{3,7}$", RegexOptions.Compiled);

        /// <summary>The word of a Radio ID name that looks like a callsign, last one first ("Austin W6OZZ" → "W6OZZ"); "" if none.</summary>
        public static string CallsignFrom(string radioIdName)
        {
            var words = (radioIdName ?? "").ToUpperInvariant().Split(new[] { ' ', '-', '/', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Reverse().FirstOrDefault(w => CallsignPattern.IsMatch(w)) ?? "";
        }

        /// <summary>Analog APRS frequency by country (<see cref="AmateurBands.CountryOf"/>); unknown country = North America's, like the band checks.</summary>
        public static decimal FrequencyFor(string country)
        {
            var bands = AmateurBands.For(country);
            if (bands.Country == "" || bands.Country == "US" || bands.Country == "CA" || country == "MX") return 144.390m;
            if (bands.Adjective == "European") return 144.800m;
            if (bands.Country == "AU") return 145.175m;
            return 0m;
        }

        public static AprsPlan Suggest(Project p)
        {
            var plan = new AprsPlan { Callsign = CallsignFrom(p.RadioIdName) };
            if (plan.Callsign.Length == 0) plan.Notes.Add("No callsign in the Radio ID name (\"" + p.RadioIdName + "\"): type it in.");
            string country = AmateurBands.CountryOf(p);
            plan.FrequencyMHz = FrequencyFor(country);
            if (plan.FrequencyMHz == 0) plan.Notes.Add("No APRS frequency known for this region: type it in.");
            if (country == "" || country == "US") plan.DigitalTalkgroup = 310999;
            else plan.Notes.Add("Digital APRS: your BrandMeister master's APRS gateway ID (xxx999) isn't known here.");
            return plan;
        }

        /// <summary>
        /// Notes about the APRS settings in a radio read: the factory callsign (and the rest of the factory values with it).
        /// Empty when the read doesn't hold the APRS block or the callsign isn't the factory one.
        /// </summary>
        public static List<string> ReadNotes(MemoryImage img)
        {
            var notes = new List<string>();
            var source = RadioSettings.Find("AprsSource");
            if (source == null || img == null || !img.Has(source.Address, source.Length)) return notes;
            string call = RadioSettings.Read(img, source).Text.Trim();
            if (string.Equals(call, FactoryCallsign, StringComparison.OrdinalIgnoreCase))
                notes.Add("APRS on this radio still has the factory callsign " + FactoryCallsign + " (and, on radios like this one, a factory position in " +
                          "China and 144.640 MHz). If you use APRS, set your own callsign, position and frequency in the CPS's APRS settings before turning beacons on.");
            return notes;
        }
    }
}
