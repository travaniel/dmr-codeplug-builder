using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CodeplugBuilder.Core.Radio
{
    /// <summary>
    /// Turns a codeplug read from the radio into the CSV files the CPS writes with Export All
    /// (Channel, Zone, TalkGroups, RadioIDList, ReceiveGroupCallList, ScanList). The folder can then be
    /// imported like a CPS export, and compared with a real export to check the decoding.
    /// Columns this program doesn't decode keep the CPS template's value.
    /// </summary>
    public static class RadioCsv
    {
        /// <summary>Channel.CSV columns filled from the radio. The rest come from the template row.</summary>
        public static readonly string[] DecodedChannelColumns =
        {
            "No.", "Channel Name", "Receive Frequency", "Transmit Frequency", "Channel Type", "Transmit Power", "Band Width",
            "CTCSS/DCS Decode", "CTCSS/DCS Encode", "Contact", "Contact Call Type", "Radio ID", "Busy channel Lock-Out/TX Permit",
            "Squelch Mode", "Optional Signal", "PTT ID", "Color Code", "Slot", "Receive Group List", "TX Prohibit", "Reverse",
            "Simplex TDMA", "TDMA Adaptive", "Extend Encryption Type", "Digital Encryption", "Call Confirmation", "Talk Around",
            "Work Alone", "Custom CTCSS", "Scan List 1", "Scan List 2", "Scan List 3", "Scan List 4", "Scan List 5", "Scan List 6",
            "Scan List 7", "Scan List 8", "Ranging", "Through Mode", "Exclude Channel From Roaming", "APRS Report Channel",
            "AES Digital Encryption", "Multiple Key", "Random Key", "ARC4",
        };

        // Value spellings by stored number. Only the first entry of most lists has been seen in a real
        // export so far; the rest are the AnyTone CPS's usual wording and need checking with --radio-compare.
        internal static readonly string[] ChannelTypes = { "A-Analog", "D-Digital", "A+D TX A", "D+A TX D" };
        internal static readonly string[] SquelchModes = { "Carrier", "CTCSS/DCS", "Optional Signaling", "CTCSS/DCS and Optional Signaling" };
        internal static readonly string[] OptionalSignals = { "Off", "DTMF", "2Tone", "5Tone" };
        internal static readonly string[] PttIds = { "Off", "Start", "End", "Start & End" };
        internal static readonly string[] DigitalPermit = { "Always", "Same Color Code", "Channel Free", "Different Color Code" };
        internal static readonly string[] AnalogPermit = { "Off", "Channel Free", "Different CTCSS/DCS", "Same CTCSS/DCS" };
        internal static readonly string[] CallTypeNames = { CallTypes.Private, CallTypes.Group, CallTypes.All };
        internal static readonly string[] Alerts = { "None", "Ring", "Online Alert" };

        public static Dictionary<string, CsvTable> ToTables(RadioCodeplug cp, CpsFormat format, int frequencyDecimals = 5)
        {
            var t = new Dictionary<string, CsvTable>();

            // Channels (and VFO rows), in number order.
            var ch = format.Channels.CloneHeader();
            foreach (var c in cp.Channels.OrderBy(c => c.Index).Concat(new[] { cp.VfoA, cp.VfoB }.Where(v => v != null)))
                ch.Rows.Add(ChannelRow(cp, c, format, ch, frequencyDecimals));
            t[CpsFormat.ChannelFile] = ch;

            var zn = format.Zones.CloneHeader();
            int zno = 0;
            foreach (var z in cp.Zones.OrderBy(z => z.Index))
            {
                var row = zn.NewRow(null);
                zn.Set(row, Num(++zno), "No.");
                zn.Set(row, z.Name, "Zone Name");
                zn.Set(row, string.Join("|", z.Members.Select(m => ChannelName(cp, m))), "Zone Channel Member");
                zn.Set(row, ChannelName(cp, z.ChannelA), "A Channel");
                zn.Set(row, ChannelName(cp, z.ChannelB), "B Channel");
                zn.Rows.Add(row);
            }
            t[CpsFormat.ZoneFile] = zn;

            var tg = format.TalkGroups.CloneHeader();
            int tno = 0;
            foreach (var c in cp.Contacts)
            {
                var row = tg.NewRow(null);
                tg.Set(row, Num(++tno), "No.");
                tg.Set(row, c.Id.ToString(CultureInfo.InvariantCulture), "Radio ID");
                tg.Set(row, c.Name, "Name");
                tg.Set(row, Pick(CallTypeNames, c.CallType), "Call Type");
                tg.Set(row, Pick(Alerts, c.Alert), "Call Alert");
                tg.Rows.Add(row);
            }
            t[CpsFormat.TalkGroupsFile] = tg;

            var rid = format.RadioIds.CloneHeader();
            foreach (var r in cp.RadioIds.OrderBy(r => r.Index))
            {
                var row = rid.NewRow(null);
                rid.Set(row, Num(r.Index + 1), "No.");
                rid.Set(row, r.Id.ToString(CultureInfo.InvariantCulture), "Radio ID");
                rid.Set(row, r.Name, "Name");
                rid.Rows.Add(row);
            }
            t[CpsFormat.RadioIdFile] = rid;

            var rg = format.RxGroupLists.CloneHeader();
            int gno = 0;
            foreach (var g in cp.GroupLists.OrderBy(g => g.Index))
            {
                var row = rg.NewRow(null);
                rg.Set(row, Num(++gno), "No.");
                rg.Set(row, g.Name, "Group Name");
                rg.Set(row, string.Join("|", g.Members().Select(m => cp.ContactAt(m)?.Name ?? "?" + (m + 1))), "Contact");
                rg.Rows.Add(row);
            }
            t[CpsFormat.RxGroupFile] = rg;

            var sl = format.ScanLists.CloneHeader();
            int sno = 0;
            foreach (var s in cp.ScanLists.OrderBy(s => s.Index))
            {
                var row = sl.NewRow(format.ScanTemplate);
                sl.Set(row, Num(++sno), "No.");
                sl.Set(row, s.Name, "Scan List Name");
                sl.Set(row, string.Join("|", s.Members().Select(m => ChannelName(cp, m))), "Scan Channel Member");
                sl.Set(row, s.ScanMode == 0 ? "Off" : Num(s.ScanMode), "Scan Mode");
                sl.Set(row, Pick(new[] { "Off", "Priority Channel Select1", "Priority Channel Select2", "Priority Channel Select1 + Priority Channel Select2" }, s.PrioritySelect), "Priority Channel Select");
                sl.Set(row, PriorityChannel(cp, s.Priority1), "Priority Channel 1");
                sl.Set(row, PriorityChannel(cp, s.Priority2), "Priority Channel 2");
                sl.Set(row, Pick(new[] { "Selected", "Selected + TalkBack", "", "", "Last Called", "Last Used" }, s.RevertChannel), "Revert Channel");
                sl.Set(row, Tenths(s.LookBackA), "Look Back Time A[s]");
                sl.Set(row, Tenths(s.LookBackB), "Look Back Time B[s]");
                sl.Set(row, Tenths(s.DropoutDelay), "Dropout Delay Time[s]");
                sl.Set(row, Tenths(s.Dwell), "Dwell Time[s]");
                sl.Rows.Add(row);
            }
            t[CpsFormat.ScanListFile] = sl;
            return t;
        }

        static List<string> ChannelRow(RadioCodeplug cp, RadioChannel c, CpsFormat format, CsvTable t, int decimals)
        {
            bool digital = c.Mode == 1 || c.Mode == 3;
            var template = c.IsVfo
                ? format.VfoRows.FirstOrDefault(v => CpsFormat.ChannelNumber(format.Channels, v) == c.Number) ?? (digital ? format.DigitalTemplate : format.AnalogTemplate)
                : digital ? format.DigitalTemplate : format.AnalogTemplate;
            var row = t.NewRow(CpsFormat.MapRow(format.Channels, template, t));
            var contact = cp.ContactAt(c.ContactIndex);
            string name = c.Name;
            if (c.IsVfo && name.Length == 0) name = c.Index == Dmr6x2Pro.MaxChannels ? "Channel VFO A" : "Channel VFO B";

            t.Set(row, Num(c.Number), "No.");
            t.Set(row, name, "Channel Name");
            t.Set(row, MHz(c.RxHz, decimals), "Receive Frequency");
            t.Set(row, MHz(c.TxHz, decimals), "Transmit Frequency");
            t.Set(row, Pick(ChannelTypes, c.Mode), "Channel Type");
            t.Set(row, Pick(Powers.Values, c.Power), "Transmit Power");
            t.Set(row, c.Wide ? Bandwidths.Wide : Bandwidths.Narrow, "Band Width");
            t.Set(row, c.RxTone, "CTCSS/DCS Decode");
            t.Set(row, c.TxTone, "CTCSS/DCS Encode");
            t.Set(row, contact?.Name ?? "", "Contact");
            t.Set(row, contact != null ? Pick(CallTypeNames, contact.CallType) : "", "Contact Call Type");
            t.Set(row, cp.RadioIds.FirstOrDefault(r => r.Index == c.RadioIdIndex)?.Name ?? "", "Radio ID");
            t.Set(row, Pick(digital ? DigitalPermit : AnalogPermit, c.TxPermit), "Busy channel Lock-Out/TX Permit");
            t.Set(row, Pick(SquelchModes, c.SquelchMode), "Squelch Mode");
            t.Set(row, Pick(OptionalSignals, c.OptionalSignaling), "Optional Signal");
            t.Set(row, Pick(PttIds, c.PttId), "PTT ID");
            t.Set(row, Num(c.ColorCode), "Color Code");
            t.Set(row, Num(c.Slot), "Slot");
            t.Set(row, c.GroupListIndex == 0xFF ? "None" : cp.GroupLists.FirstOrDefault(g => g.Index == c.GroupListIndex)?.Name ?? "None", "Receive Group List");
            t.Set(row, OnOff(c.RxOnly), "TX Prohibit");
            t.Set(row, OnOff(c.Reverse), "Reverse");
            t.Set(row, OnOff(c.SimplexTdma), "Simplex TDMA");
            t.Set(row, OnOff(c.AdaptiveTdma), "TDMA Adaptive");
            t.Set(row, c.EnhancedEncryption ? "Enhanced Encryption" : "Normal Encryption", "Extend Encryption Type");
            t.Set(row, c.DmrKeyIndex == 0 ? "Off" : Num(c.DmrKeyIndex), "Digital Encryption");
            t.Set(row, OnOff(c.CallConfirm), "Call Confirmation");
            t.Set(row, OnOff(c.Talkaround), "Talk Around");
            t.Set(row, OnOff(c.LoneWorker), "Work Alone");
            t.Set(row, (c.CustomCtcss / 10m).ToString("0.0", CultureInfo.InvariantCulture), "Custom CTCSS");
            for (int i = 0; i < 8; i++)
            {
                int s = c.ScanListIndex(i);
                t.Set(row, s == 0xFF ? "" : cp.ScanLists.FirstOrDefault(x => x.Index == s)?.Name ?? "", "Scan List " + (i + 1));
            }
            t.Set(row, OnOff(c.Ranging), "Ranging");
            t.Set(row, OnOff(c.ThroughMode), "Through Mode");
            t.Set(row, OnOff(c.ExcludeFromRoaming), "Exclude Channel From Roaming");
            t.Set(row, Num(c.AprsReportChannel + 1), "APRS Report Channel");
            t.Set(row, Num(c.AesKeyIndex), "AES Digital Encryption");
            t.Set(row, c.MultipleKey ? "1" : "0", "Multiple Key");
            t.Set(row, c.RandomKey ? "1" : "0", "Random Key");
            t.Set(row, Num(c.Arc4KeyIndex), "ARC4");
            return row;
        }

        /// <summary>Writes the six CSV files into <paramref name="folder"/>. Returns their paths.</summary>
        public static List<string> WriteTo(Dictionary<string, CsvTable> tables, string folder)
        {
            Directory.CreateDirectory(folder);
            var written = new List<string>();
            foreach (var kv in tables)
            {
                string path = Path.Combine(folder, kv.Key);
                kv.Value.Save(path);
                written.Add(path);
            }
            return written;
        }

        /// <summary>
        /// Compares two folders of CPS CSVs (for example a radio read and the CPS's own Export All of the same
        /// codeplug): rows are matched by "No.", fields by column name. Returns one line per difference.
        /// </summary>
        public static List<string> Compare(string expectedFolder, string actualFolder, IEnumerable<string> files = null)
        {
            var diffs = new List<string>();
            foreach (string file in files ?? CpsFormat.Files)
            {
                string pe = CpsFormat.FindFile(expectedFolder, file), pa = CpsFormat.FindFile(actualFolder, file);
                if (pe == null || pa == null)
                {
                    if (pe != pa) diffs.Add(file + ": only in " + (pe != null ? expectedFolder : actualFolder));
                    continue;
                }
                diffs.AddRange(CompareTables(file, CsvTable.Load(pe), CsvTable.Load(pa)));
            }
            return diffs;
        }

        public static List<string> CompareTables(string file, CsvTable expected, CsvTable actual)
        {
            var diffs = new List<string>();
            var e = expected.Rows.GroupBy(r => expected.Get(r, "No.")).ToDictionary(g => g.Key, g => g.First());
            var a = actual.Rows.GroupBy(r => actual.Get(r, "No.")).ToDictionary(g => g.Key, g => g.First());
            if (expected.IndexOf("No.") < 0)
            {
                e = expected.Rows.Select((r, i) => new { r, i }).ToDictionary(x => (x.i + 1).ToString(CultureInfo.InvariantCulture), x => x.r);
                a = actual.Rows.Select((r, i) => new { r, i }).ToDictionary(x => (x.i + 1).ToString(CultureInfo.InvariantCulture), x => x.r);
            }
            foreach (var key in e.Keys.Union(a.Keys).OrderBy(k => int.TryParse(k, out int n) ? n : int.MaxValue))
            {
                if (!a.ContainsKey(key)) { diffs.Add(file + " row " + key + ": missing from the radio read"); continue; }
                if (!e.ContainsKey(key)) { diffs.Add(file + " row " + key + ": only in the radio read (" + string.Join(", ", a[key].Take(3)) + ")"); continue; }
                foreach (string col in expected.Header)
                {
                    if (actual.IndexOf(col) < 0) continue;
                    string ev = expected.Get(e[key], col), av = actual.Get(a[key], col);
                    if (ev != av) diffs.Add(file + " row " + key + " [" + col + "]: CPS \"" + ev + "\", radio \"" + av + "\"");
                }
            }
            return diffs;
        }

        static string ChannelName(RadioCodeplug cp, int index)
        {
            var c = cp.ChannelAt(index);
            if (c == null) return "";
            if (c.IsVfo && c.Name.Length == 0) return index == Dmr6x2Pro.MaxChannels ? "Channel VFO A" : "Channel VFO B";
            return c.Name;
        }

        static string PriorityChannel(RadioCodeplug cp, int stored)
        {
            if (stored == 0xFFFF) return "Off";
            if (stored == 0) return "Current Channel";
            return ChannelName(cp, stored - 1);
        }

        static string Pick(IList<string> values, int index) =>
            index >= 0 && index < values.Count && values[index].Length > 0 ? values[index] : Num(index);

        static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
        static string OnOff(bool b) => b ? "On" : "Off";
        static string Tenths(int v) => (v / 10m).ToString("0.0", CultureInfo.InvariantCulture);

        static string MHz(long hz, int decimals) =>
            (hz / 1000000m).ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
