using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.Core
{
    /// <summary>
    /// What the radio's APRS sends (roadmap 1.4 item 2). Suggested from the project by <see cref="Aprs.Suggest"/>; stored on
    /// <see cref="Project.Aprs"/> (null = APRS.CSV isn't written, the CPS's APRS settings stay).
    /// </summary>
    [DataContract(Namespace = "")]
    public sealed class AprsPlan
    {
        [DataMember(Order = 1)] public string Callsign { get; set; }
        /// <summary>7 = handheld, 9 = mobile (APRS convention).</summary>
        [DataMember(Order = 2)] public int Ssid { get; set; }
        /// <summary>Primary table "/" and symbol "[" (a person: the usual handheld symbol).</summary>
        [DataMember(Order = 3)] public string SymbolTable { get; set; }
        [DataMember(Order = 4)] public string Symbol { get; set; }
        [DataMember(Order = 5)] public string Path { get; set; }
        /// <summary>The region's analog APRS frequency; 0 when there is none known for the region.</summary>
        [DataMember(Order = 6)] public decimal FrequencyMHz { get; set; }
        /// <summary>The 6X2 family's destination call (as on the user's radio).</summary>
        [DataMember(Order = 7)] public string Destination { get; set; }
        /// <summary>
        /// BrandMeister APRS gateway for digital reports (private call): 310999 for US masters. Not universal (each master has
        /// its own xxx999), so 0 outside the US (APRS.CSV then keeps the CPS's talkgroups).
        /// </summary>
        [DataMember(Order = 8, EmitDefaultValue = false)] public int DigitalTalkgroup { get; set; }
        /// <summary>Why a value is missing or a guess, for the UI (not saved).</summary>
        public List<string> Notes { get; private set; }

        public AprsPlan() { Init(); }

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Init(); }

        void Init()
        {
            Callsign = "";
            Ssid = 7;
            SymbolTable = "/";
            Symbol = "[";
            Path = "WIDE1-1,WIDE2-1";
            Destination = "APBT62";
            Notes = new List<string>();
        }
    }

    /// <summary>
    /// APRS settings from what the project already knows, and APRS.CSV (.LST section 19) from the built-in template: the
    /// user's own CPS export with the factory callsign, position and text taken out. Meanings checked in CPS 1.22e
    /// (2026-10-08): APRS.CSV channelN = a channel number (4001 = VFO A), slotN 0 = the channel's slot / 1 / 2, Call TypeN
    /// 0 = private, 1 = group; Channel.CSV "APRS Report Channel" = which of those 8 rows (1-8), "AprsUpDateKind" 0 off /
    /// 1 analog / 2 digital. The radio's APRS fields (<see cref="RadioSettings"/>, 0x2501000) decode to the CPS's values.
    /// </summary>
    public static class Aprs
    {
        public const string File = "APRS.CSV";
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
        /// APRS.CSV for a plan, from the built-in template. Managed: your callsign and SSID, the analog frequency, symbol, path,
        /// destination, fixed beacon off (the radio's GPS gives the position), and with a <see cref="AprsPlan.DigitalTalkgroup"/> the
        /// 8 digital report rows' talkgroup as a private call. The rows' channels stay VFO A until a channel is chosen in the CPS.
        /// Written the way the CPS writes it: every field quoted, a stray comma at the end of both lines, CRLF.
        /// </summary>
        public static string ToCsv(AprsPlan plan)
        {
            var t = CsvTable.Parse(CpsFormat.ReadResource(File));
            var row = t.Rows[0];
            t.Set(row, CallerDatabase.Ascii(plan.Callsign).ToUpperInvariant(), "Your Call Sign");
            t.Set(row, Math.Max(0, Math.Min(15, plan.Ssid)).ToString(CultureInfo.InvariantCulture), "Your SSID");
            if (plan.FrequencyMHz > 0) t.Set(row, plan.FrequencyMHz.ToString("0.#####", CultureInfo.InvariantCulture), "Transmission Frequency [MHz]");
            t.Set(row, plan.SymbolTable ?? "/", "APRS Symbol Table");
            t.Set(row, plan.Symbol ?? "[", "APRS Map Icon");
            t.Set(row, plan.Path ?? "", "Digipeater Path");
            t.Set(row, string.IsNullOrWhiteSpace(plan.Destination) ? "APBT62" : plan.Destination.Trim(), "Destination Call Sign");
            t.Set(row, "0", "Fixed Location Beacon");
            if (plan.DigitalTalkgroup > 0)
                for (int i = 1; i <= 8; i++)
                {
                    string n = i.ToString(CultureInfo.InvariantCulture);
                    t.Set(row, plan.DigitalTalkgroup.ToString(CultureInfo.InvariantCulture), "Aprs Tg" + n);
                    t.Set(row, "0", "Call Type" + n);
                }
            var sb = new StringBuilder();
            foreach (var line in new[] { t.Header, row })
            {
                // The CPS ends both lines with a comma, which parses as one more empty field: leave it out, then add the comma.
                int n = line.Count > 0 && line[line.Count - 1] == "" ? line.Count - 1 : line.Count;
                for (int i = 0; i < n; i++) sb.Append('"').Append((line[i] ?? "").Replace("\"", "\"\"")).Append("\",");
                sb.Append("\r\n");
            }
            return sb.ToString();
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
