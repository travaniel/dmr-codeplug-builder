using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeplugBuilder.Core
{
    public enum Severity { Error, Warning }

    public sealed class Issue
    {
        public Severity Severity { get; }
        public string Message { get; }

        public Issue(Severity severity, string message)
        {
            Severity = severity;
            Message = message;
        }

        public override string ToString()
        {
            return (Severity == Severity.Error ? "Error: " : "Warning: ") + Message;
        }
    }

    public static class Validator
    {
        public const int MaxTalkgroupId = 16776415;

        public static bool InRadioBand(decimal mhz)
        {
            return (mhz >= 136m && mhz <= 174m) || (mhz >= 400m && mhz <= 480m);
        }

        /// <summary>Problems that would stop the CPS import (errors) or that the user should know about (warnings).</summary>
        public static List<Issue> Validate(Project p, CpsFormat format = null)
        {
            var issues = new List<Issue>();
            void Error(string m) { issues.Add(new Issue(Severity.Error, m)); }
            void Warn(string m) { issues.Add(new Issue(Severity.Warning, m)); }

            var active = p.ActiveRepeaters().ToList();
            bool anyChannels = active.Any(r => !r.IsDigital || r.Talkgroups.Count > 0);

            if (string.IsNullOrWhiteSpace(p.RadioIdName))
                Error("Enter your Radio ID name on the Settings tab. It must match the name in the CPS Radio ID List.");
            if (p.Options.WriteRadioIdList && p.RadioId <= 0)
                Warn("Your DMR ID isn't set (Settings tab), so RadioIDList.CSV won't be written. The CPS's existing Radio ID List must then contain \"" + p.RadioIdName + "\".");
            if (p.RadioId < 0 || p.RadioId > MaxTalkgroupId)
                Error("DMR ID " + p.RadioId + " is out of range.");

            // Talkgroups
            if (anyChannels && p.Talkgroups.Count == 0)
                Error("Add at least one talkgroup. The CPS needs a contact on every channel, even analog ones.");
            foreach (var tg in p.Talkgroups)
            {
                if (string.IsNullOrWhiteSpace(tg.Name)) Error("A talkgroup with ID " + tg.Id + " has no name.");
                if (tg.Id <= 0 || tg.Id > MaxTalkgroupId) Error("Talkgroup \"" + tg.Name + "\" has an invalid ID (" + tg.Id + ").");
            }
            foreach (var dup in p.Talkgroups.GroupBy(t => t.Id).Where(g => g.Count() > 1))
                Error("Talkgroup ID " + dup.Key + " is used by more than one talkgroup (" + string.Join(", ", dup.Select(t => t.Name)) + "). The CPS rejects duplicates.");
            foreach (var dup in p.Talkgroups.GroupBy(t => (t.Name ?? "").Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
                Warn("More than one talkgroup is named \"" + dup.Key + "\"; extra copies get a number added.");

            // Repeaters
            var ids = new HashSet<int>(p.Talkgroups.Select(t => t.Id));
            var bands = AmateurBands.For(AmateurBands.CountryOf(p));
            foreach (var r in active)
            {
                string label = Label(p, r);
                if (string.IsNullOrWhiteSpace(r.Name)) Error(label + " needs a name.");
                if (r.RxMHz <= 0 || r.TxMHz <= 0)
                {
                    Error(label + " needs both a receive and a transmit frequency.");
                }
                else
                {
                    if (!InRadioBand(r.RxMHz)) Warn(label + ": receive frequency " + r.RxMHz + " MHz is outside the radio's 136-174 / 400-480 MHz range.");
                    if (!InRadioBand(r.TxMHz)) Warn(label + ": transmit frequency " + r.TxMHz + " MHz is outside the radio's 136-174 / 400-480 MHz range.");
                    foreach (string m in SafetyWarnings(p, r, label, bands)) Warn(m);
                }
                if (r.IsDigital)
                {
                    if (r.ColorCode < 0 || r.ColorCode > 15) Error(label + ": color code must be 0-15.");
                    if (r.Talkgroups.Count == 0) Warn(label + " has no talkgroups, so it won't get any channels.");
                    foreach (var e in r.Talkgroups.Where(e => !ids.Contains(e.TalkgroupId)))
                        Warn(label + " uses talkgroup " + e.TalkgroupId + ", which isn't in the talkgroup list.");
                    foreach (var dup in r.Talkgroups.GroupBy(e => new { e.TalkgroupId, e.Slot }).Where(g => g.Count() > 1))
                        Warn(label + " has talkgroup " + dup.Key.TalkgroupId + " on slot " + dup.Key.Slot + " more than once.");
                }
                else
                {
                    if (!Tones.IsValid(r.ToneEncode)) Error(label + ": \"" + r.ToneEncode + "\" isn't a CTCSS tone or DCS code.");
                    if (!Tones.IsValid(r.ToneDecode)) Error(label + ": \"" + r.ToneDecode + "\" isn't a CTCSS tone or DCS code.");
                }
                if (string.IsNullOrWhiteSpace(r.Zone))
                    Warn(label + " isn't in a zone, so you can only reach its channels in channel mode.");
                if (!string.IsNullOrWhiteSpace(r.OffAirSince))
                    Warn(label + " may be off the air: BrandMeister last heard it on " + r.OffAirSince.Trim() + ". Untick it, or if you know it works, " +
                         "untick \"Off the air?\" in the repeater editor.");
            }
            // (A hotspot that's switched on without talkgroups is reported in the loop above.)

            // Counts that depend on the generated output
            if (issues.All(i => i.Severity != Severity.Error))
            {
                try
                {
                    var g = CodeplugGenerator.Generate(p, format ?? CpsFormat.BuiltIn());
                    if (g.ChannelList.Count > p.Options.MaxChannels)
                        Error("This codeplug has " + g.ChannelList.Count + " channels; the radio holds " + p.Options.MaxChannels + ".");
                    if (g.ZoneList.Count > p.Options.MaxZones)
                        Error("This codeplug has " + g.ZoneList.Count + " zones; the radio holds " + p.Options.MaxZones + ".");
                    if (g.TalkGroups.Rows.Count > 10000)
                        Error("The radio holds 10,000 talkgroups; this list has " + g.TalkGroups.Rows.Count + ".");
                }
                catch (Exception ex)
                {
                    Error(ex.Message);
                }
            }
            return issues;
        }

        static string Label(Project p, Repeater r)
        {
            return r == p.Hotspot ? "Hotspot" : (string.IsNullOrWhiteSpace(r.Name) ? "A repeater with no name" : "\"" + r.Name + "\"");
        }

        /// <summary>Just the safety warnings (<see cref="Validate"/> includes them), for the repeaters and hotspot that go into the codeplug.</summary>
        public static List<string> SafetyWarnings(Project p)
        {
            var bands = AmateurBands.For(AmateurBands.CountryOf(p));
            return p.ActiveRepeaters().Where(r => r.RxMHz > 0 && r.TxMHz > 0).SelectMany(r => SafetyWarnings(p, r, Label(p, r), bands)).ToList();
        }

        /// <summary>
        /// Where a channel that can transmit shouldn't: outside the amateur bands (a MURS, GMRS or business frequency),
        /// and for the hotspot and DMR simplex, the satellite sub-bands, APRS and calling frequencies and (US) the
        /// segments Part 97.201(b) closes to auxiliary stations. Analog simplex isn't checked against the satellite
        /// sub-bands: FM satellite uplinks are legitimately there.
        /// </summary>
        static IEnumerable<string> SafetyWarnings(Project p, Repeater r, string label, AmateurBands bands)
        {
            if (r.RxOnly) yield break;
            if (InRadioBand(r.TxMHz) && !bands.CanTransmit(r.TxMHz))
                yield return label + " transmits on " + AmateurBands.Mhz(r.TxMHz) + " MHz, outside " + bands.Describe() + ". Other services " +
                             "(MURS, GMRS, business) need their own license and a radio certified for them. If it's for listening, tick Receive only.";

            bool hotspot = r == p.Hotspot;
            if (!hotspot && !(r.IsDigital && r.RxMHz == r.TxMHz)) yield break;
            // A hotspot transmits on the radio's receive frequency; the radio on its transmit frequency.
            var frequencies = hotspot ? new[] { r.RxMHz, r.TxMHz }.Distinct() : new[] { r.TxMHz };
            foreach (decimal f in frequencies)
            {
                var spot = bands.NearKeepClear(f);
                var satellite = AmateurBands.SatelliteBand(f);
                var closed = hotspot ? bands.HotspotExcludedBand(f) : null;
                if (spot != null)
                    yield return label + " is on " + AmateurBands.Mhz(f) + " MHz, " + (f == spot.MHz ? "" : "next to ") + spot.What + " (" +
                                 AmateurBands.Mhz(spot.MHz) + "). Move it to a quiet simplex frequency.";
                else if (satellite != null)
                    yield return label + " is on " + AmateurBands.Mhz(f) + " MHz, in the " + satellite + " MHz satellite sub-band. Satellite uplinks are " +
                                 "weak and AMSAT has traced interference to hotspots there; move it outside 145.8-146 and 435-438 MHz.";
                else if (closed != null)
                    yield return label + " is on " + AmateurBands.Mhz(f) + " MHz, in " + closed + " MHz, where a hotspot (an auxiliary station) may not " +
                                 "transmit (FCC Part 97.201(b)).";
            }
        }
    }
}
