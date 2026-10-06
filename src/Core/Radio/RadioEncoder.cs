using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeplugBuilder.Core.Radio
{
    public sealed class EncodeResult
    {
        /// <summary>The radio's memory with the codeplug written in (a copy; the read stays untouched).</summary>
        public MemoryImage Image;
        /// <summary>The six tables that went in: the ones given, plus the radio's own for any not given.</summary>
        public Dictionary<string, CsvTable> Tables = new Dictionary<string, CsvTable>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Problems that make the image unsafe to write (a channel pointing at a contact that doesn't exist...).</summary>
        public List<string> Errors = new List<string>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Writes a codeplug, as the six CPS tables (Channel, Zone, TalkGroups, RadioIDList, ReceiveGroupCallList,
    /// ScanList), into a full read of the radio: the reverse of <see cref="RadioCodeplug.Decode"/> plus
    /// <see cref="RadioCsv.ToTables"/>. Like a CPS import, each table given replaces that whole list; a table not given
    /// keeps the radio's list, minus members that no longer exist. Every record starts as a copy of one the radio
    /// already holds (the same slot when it's the same kind, else one of its kind), so bytes this program doesn't
    /// decode keep the radio's values. Elements that are gone are blanked to FF, as the radio leaves them after a CPS
    /// write. Settings, APRS, roaming, messages and the analog address book are left alone.
    /// </summary>
    public static class RadioEncoder
    {
        static readonly string[] TableFiles =
        {
            CpsFormat.ChannelFile, CpsFormat.ZoneFile, CpsFormat.TalkGroupsFile, CpsFormat.RadioIdFile, CpsFormat.RxGroupFile, CpsFormat.ScanListFile,
        };

        static readonly string[] PrioritySelects = { "Off", "Priority Channel Select1", "Priority Channel Select2", "Priority Channel Select1 + Priority Channel Select2" };
        static readonly string[] Reverts = { "Selected", "Selected + TalkBack", "", "", "Last Called", "Last Used" };

        /// <summary>The CPS's write area per scan list and RX group list (more than the documented record).</summary>
        const int ScanListWriteSize = 0xC0, GroupListWriteSize = 0x130, ZoneMembersSize = 0x200;

        /// <summary>Bytes 0x84-0xBF of a scan list as CPS 1.22e left them (user's radio, 2026-10-06), for a radio that has none to copy.</summary>
        static byte[] DefaultScanTail()
        {
            var t = Enumerable.Repeat((byte)0xFF, ScanListWriteSize).ToArray();
            for (int i = 0x84; i <= 0x8A; i++) t[i] = 0x00;
            for (int i = 0x8B; i <= 0x90; i++) t[i] = 0x01;
            for (int i = 0xB6; i < ScanListWriteSize; i++) t[i] = 0x00;
            return t;
        }

        public static EncodeResult Encode(MemoryImage original, IDictionary<string, CsvTable> tables, CpsFormat format = null)
        {
            format = format ?? CpsFormat.BuiltIn();
            var res = new EncodeResult();
            var cp = RadioCodeplug.Decode(original);
            var current = RadioCsv.ToTables(cp, format);
            foreach (string f in TableFiles)
            {
                CsvTable given = null;
                if (tables != null) foreach (var kv in tables) if (string.Equals(kv.Key, f, StringComparison.OrdinalIgnoreCase) && kv.Value != null) given = kv.Value;
                res.Tables[f] = given ?? current[f];
                if (given == null) res.Notes.Add(f + " not given: the radio's own list is kept.");
            }
            var ch = res.Tables[CpsFormat.ChannelFile];
            // A scan list kept from the radio loses the channels that are gone, and goes when none are left
            // (what CPS 1.22e did on a Channel.CSV import, 2026-10-06).
            if (res.Tables[CpsFormat.ScanListFile] == current[CpsFormat.ScanListFile])
            {
                var names = new HashSet<string>(ch.Rows.Select(r => ch.Get(r, "Channel Name")), StringComparer.OrdinalIgnoreCase);
                var kept = current[CpsFormat.ScanListFile].CloneHeader();
                foreach (var row in current[CpsFormat.ScanListFile].Rows)
                {
                    var r = new List<string>(row);
                    var all = Members(kept.Get(r, "Scan Channel Member"));
                    var left = all.Where(names.Contains).ToList();
                    string listName = kept.Get(r, "Scan List Name");
                    if (left.Count == 0) { res.Notes.Add("Scan list \"" + listName + "\" on the radio has none of its channels left; removed."); continue; }
                    if (left.Count < all.Count) res.Notes.Add("Scan list \"" + listName + "\" on the radio: " + (all.Count - left.Count) + " of its channels are gone.");
                    kept.Set(r, string.Join("|", left), "Scan Channel Member");
                    kept.Rows.Add(r);
                }
                res.Tables[CpsFormat.ScanListFile] = kept;
            }
            var zn = res.Tables[CpsFormat.ZoneFile];
            var tg = res.Tables[CpsFormat.TalkGroupsFile];
            var rid = res.Tables[CpsFormat.RadioIdFile];
            var rg = res.Tables[CpsFormat.RxGroupFile];
            var sl = res.Tables[CpsFormat.ScanListFile];

            var img = original.Clone();
            var err = res.Errors;

            // ---- Names → slots ------------------------------------------------------------------------------
            var channelSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ch.Rows)
            {
                int no = CpsFormat.ChannelNumber(ch, row);
                string name = ch.Get(row, "Channel Name");
                if (no < 1 || (no > Dmr6x2Pro.MaxChannels && no != CpsFormat.FirstVfoNumber && no != CpsFormat.FirstVfoNumber + 1))
                {
                    err.Add("Channel \"" + name + "\" has number " + ch.Get(row, "No.") + ", outside 1-" + Dmr6x2Pro.MaxChannels + ".");
                    continue;
                }
                int slot = no >= CpsFormat.FirstVfoNumber ? Dmr6x2Pro.MaxChannels + (no - CpsFormat.FirstVfoNumber) : no - 1;
                if (channelSlot.ContainsKey(name)) err.Add("Two channels are named \"" + name + "\".");
                else channelSlot[name] = slot;
            }
            var contactSlot = Index(tg, "Name", (r, i) => i, err, "talkgroup");
            var radioIdIndex = Index(rid, "Name", (r, i) => Number(rid, r) - 1, err, "radio ID");
            var groupIndex = Index(rg, "Group Name", (r, i) => Number(rg, r) - 1, err, "RX group list");
            var scanIndex = Index(sl, "Scan List Name", (r, i) => Number(sl, r) - 1, err, "scan list");
            if (tg.Rows.Count > Dmr6x2Pro.MaxContacts) err.Add(tg.Rows.Count + " talkgroups; the radio holds " + Dmr6x2Pro.MaxContacts + ".");
            CheckRange(radioIdIndex, Dmr6x2Pro.MaxRadioIds, "radio ID", err);
            CheckRange(groupIndex, Dmr6x2Pro.MaxGroupLists, "RX group list", err);
            CheckRange(scanIndex, Dmr6x2Pro.MaxScanLists, "scan list", err);

            // ---- Blank what exists now ------------------------------------------------------------------------
            var oldChannels = cp.Channels.ToDictionary(c => c.Index);
            var oldContacts = cp.Contacts.ToDictionary(c => c.Index);
            var oldRadioIds = cp.RadioIds.ToDictionary(r => r.Index);
            var oldGroups = new Dictionary<int, byte[]>();
            foreach (var g in cp.GroupLists) oldGroups[g.Index] = original.Has(GroupAddr(g.Index), GroupListWriteSize) ? original.Get(GroupAddr(g.Index), GroupListWriteSize) : null;
            var oldScans = new Dictionary<int, byte[]>();
            foreach (var s in cp.ScanLists) oldScans[s.Index] = original.Has(Dmr6x2Pro.ScanListAddress(s.Index), ScanListWriteSize) ? original.Get(Dmr6x2Pro.ScanListAddress(s.Index), ScanListWriteSize) : null;
            var oldZones = new Dictionary<int, byte[]>();
            foreach (var z in cp.Zones) oldZones[z.Index] = original.Has(ZoneAddr(z.Index), ZoneMembersSize) ? original.Get(ZoneAddr(z.Index), ZoneMembersSize) : null;

            foreach (int i in Dmr6x2Pro.Enabled(original, Dmr6x2Pro.ChannelBitmap, Dmr6x2Pro.MaxChannels))
            {
                Fill(img, Dmr6x2Pro.ChannelAddress(i), Dmr6x2Pro.ChannelSize, 0xFF);
                Fill(img, Dmr6x2Pro.ChannelAddress(i) + Dmr6x2Pro.ChannelExtension, Dmr6x2Pro.ChannelSize, 0xFF);
            }
            foreach (int i in Dmr6x2Pro.Enabled(original, Dmr6x2Pro.ZoneBitmap, Dmr6x2Pro.MaxZones))
            {
                Fill(img, ZoneAddr(i), ZoneMembersSize, 0xFF);
                Fill(img, Dmr6x2Pro.ZoneNames + (uint)i * Dmr6x2Pro.ZoneNameStride, (int)Dmr6x2Pro.ZoneNameStride, 0xFF);
            }
            foreach (int i in Dmr6x2Pro.Enabled(original, Dmr6x2Pro.ScanListBitmap, Dmr6x2Pro.MaxScanLists)) Fill(img, Dmr6x2Pro.ScanListAddress(i), ScanListWriteSize, 0xFF);
            foreach (int i in Dmr6x2Pro.Enabled(original, Dmr6x2Pro.RadioIdBitmap, Dmr6x2Pro.MaxRadioIds)) Fill(img, Dmr6x2Pro.RadioIds + (uint)i * Dmr6x2Pro.RadioIdStride, (int)Dmr6x2Pro.RadioIdStride, 0xFF);
            foreach (int i in Dmr6x2Pro.Enabled(original, Dmr6x2Pro.GroupListBitmap, Dmr6x2Pro.MaxGroupLists)) Fill(img, GroupAddr(i), GroupListWriteSize, 0xFF);
            var existingContacts = Dmr6x2Pro.ExistingContacts(original).ToList();
            foreach (int i in existingContacts)
            {
                // Whole blocks, so the 00 padding after the old last contact goes too.
                uint start = MemoryImage.AlignDown(Dmr6x2Pro.ContactAddress(i));
                uint end = MemoryImage.AlignDown(Dmr6x2Pro.ContactAddress(i) + Dmr6x2Pro.ContactSize + MemoryImage.BlockSize - 1);
                Fill(img, start, (int)(end - start), 0xFF);
            }
            Fill(img, Dmr6x2Pro.ContactMap, 8 * (existingContacts.Count + 1), 0xFF);
            Fill(img, Dmr6x2Pro.ContactIndexList, 4 * Dmr6x2Pro.MaxContacts, 0xFF);

            ClearBits(img, Dmr6x2Pro.ChannelBitmap, Dmr6x2Pro.MaxChannels, false);   // bits 4000/4001 (VFO A/B) stay
            ClearBits(img, Dmr6x2Pro.ZoneBitmap, Dmr6x2Pro.MaxZones, false);
            ClearBits(img, Dmr6x2Pro.HiddenZoneBitmap, Dmr6x2Pro.MaxZones, false);
            ClearBits(img, Dmr6x2Pro.ScanListBitmap, Dmr6x2Pro.MaxScanLists, false);
            ClearBits(img, Dmr6x2Pro.RadioIdBitmap, Dmr6x2Pro.MaxRadioIds, false);
            ClearBits(img, Dmr6x2Pro.GroupListBitmap, Dmr6x2Pro.MaxGroupLists, false);
            ClearBits(img, Dmr6x2Pro.ContactBitmap, Dmr6x2Pro.MaxContacts, true);    // inverted: set = free

            // ---- Contacts, in list order: slot = position ---------------------------------------------------
            var mapEntries = new List<KeyValuePair<uint, int>>();
            for (int p = 0; p < tg.Rows.Count && p < Dmr6x2Pro.MaxContacts; p++)
            {
                var row = tg.Rows[p];
                var raw = oldContacts.TryGetValue(p, out var oc) ? (byte[])oc.Raw.Clone() : new byte[Dmr6x2Pro.ContactSize];
                int type = Array.IndexOf(RadioCsv.CallTypeNames, tg.Get(row, "Call Type"));
                if (type < 0) { err.Add("Talkgroup \"" + tg.Get(row, "Name") + "\" has call type \"" + tg.Get(row, "Call Type") + "\"."); type = 1; }
                if (!uint.TryParse(tg.Get(row, "Radio ID"), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id > 99999999)
                {
                    err.Add("Talkgroup \"" + tg.Get(row, "Name") + "\" has ID \"" + tg.Get(row, "Radio ID") + "\".");
                    id = 0;
                }
                raw[0] = (byte)type;
                Ascii(raw, 1, tg.Get(row, "Name"), 16);
                Bcd(raw, 0x23, id);
                raw[0x27] = (byte)Math.Max(0, Array.IndexOf(RadioCsv.Alerts, tg.Get(row, "Call Alert")));
                Put(img, Dmr6x2Pro.ContactAddress(p), raw);
                SetBit(img, Dmr6x2Pro.ContactBitmap, p, false);
                Put(img, Dmr6x2Pro.ContactIndexList + (uint)(4 * p), BitConverter.GetBytes((uint)p));
                // ID map: key = BCD ID shifted left once, +1 for a group call; sorted by key (seen on the user's radio).
                mapEntries.Add(new KeyValuePair<uint, int>(BcdValue(id) << 1 | (type == 1 ? 1u : 0u), p));
            }
            // The CPS pads the block holding the end of the last contact with 00.
            int contactCount = Math.Min(tg.Rows.Count, Dmr6x2Pro.MaxContacts);
            if (contactCount > 0)
            {
                uint end = Dmr6x2Pro.ContactAddress(contactCount - 1) + Dmr6x2Pro.ContactSize;
                int pad = (int)(MemoryImage.AlignDown(end + MemoryImage.BlockSize - 1) - end);
                if (pad > 0) Put(img, end, new byte[pad]);
            }
            var map = new List<byte>();
            foreach (var e in mapEntries.OrderBy(e => e.Key))
            {
                map.AddRange(BitConverter.GetBytes(e.Key));
                map.AddRange(BitConverter.GetBytes((uint)e.Value));
            }
            if (map.Count > 0) Put(img, Dmr6x2Pro.ContactMap, map.ToArray());

            // ---- Radio IDs ------------------------------------------------------------------------------------
            foreach (var row in rid.Rows)
            {
                int i = Number(rid, row) - 1;
                if (i < 0 || i >= Dmr6x2Pro.MaxRadioIds) continue;
                var raw = oldRadioIds.TryGetValue(i, out var o) ? (byte[])o.Raw.Clone() : new byte[Dmr6x2Pro.RadioIdStride];
                if (!uint.TryParse(rid.Get(row, "Radio ID"), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id > 99999999)
                    err.Add("Radio ID \"" + rid.Get(row, "Name") + "\" has ID \"" + rid.Get(row, "Radio ID") + "\".");
                Bcd(raw, 0, id);
                Ascii(raw, 5, rid.Get(row, "Name"), 16);
                Put(img, Dmr6x2Pro.RadioIds + (uint)i * Dmr6x2Pro.RadioIdStride, raw);
                SetBit(img, Dmr6x2Pro.RadioIdBitmap, i, true);
            }

            // ---- RX group lists ---------------------------------------------------------------------------------
            foreach (var row in rg.Rows)
            {
                int i = Number(rg, row) - 1;
                if (i < 0 || i >= Dmr6x2Pro.MaxGroupLists) continue;
                byte[] raw;
                if (oldGroups.TryGetValue(i, out var o) && o != null) raw = (byte[])o.Clone();
                else { raw = Enumerable.Repeat((byte)0xFF, GroupListWriteSize).ToArray(); Array.Clear(raw, 0x100, GroupListWriteSize - 0x100); }
                for (int m = 0; m < Dmr6x2Pro.MaxGroupListMembers; m++) BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(raw, 4 * m);
                var members = Members(rg.Get(row, "Contact"));
                if (members.Count > Dmr6x2Pro.MaxGroupListMembers) err.Add("RX group list \"" + rg.Get(row, "Group Name") + "\" has " + members.Count + " talkgroups; the radio holds " + Dmr6x2Pro.MaxGroupListMembers + ".");
                int n = 0;
                foreach (string m in members.Take(Dmr6x2Pro.MaxGroupListMembers))
                {
                    if (!contactSlot.TryGetValue(m, out int s)) { res.Notes.Add("RX group list \"" + rg.Get(row, "Group Name") + "\": talkgroup \"" + m + "\" doesn't exist, left out."); continue; }
                    BitConverter.GetBytes((uint)s).CopyTo(raw, 4 * n++);
                }
                Ascii(raw, 0x100, rg.Get(row, "Group Name"), 16);
                Put(img, GroupAddr(i), raw);
                SetBit(img, Dmr6x2Pro.GroupListBitmap, i, true);
            }

            // ---- Scan lists (members by channel slot) -----------------------------------------------------------
            byte[] scanBase = oldScans.Values.FirstOrDefault(v => v != null) ?? DefaultScanTail();
            // Each member also carries the index of the zone it's in (the first zone holding it): bytes 0x84-0xB5,
            // FF after the last member (CPS 1.22e, 2026-10-06; a member in no zone is a guess: FF).
            var zoneOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in zn.Rows.OrderBy(r => Number(zn, r)))
                foreach (string m in Members(zn.Get(row, "Zone Channel Member")))
                    if (!zoneOf.ContainsKey(m)) zoneOf[m] = Number(zn, row) - 1;
            foreach (var row in sl.Rows)
            {
                int i = Number(sl, row) - 1;
                if (i < 0 || i >= Dmr6x2Pro.MaxScanLists) continue;
                string name = sl.Get(row, "Scan List Name");
                var raw = (byte[])(oldScans.TryGetValue(i, out var o) && o != null ? o : scanBase).Clone();
                var members = Members(sl.Get(row, "Scan Channel Member")).Where(m =>
                {
                    if (channelSlot.ContainsKey(m)) return true;
                    res.Notes.Add("Scan list \"" + name + "\": channel \"" + m + "\" doesn't exist, left out.");
                    return false;
                }).ToList();
                if (members.Count > 50) err.Add("Scan list \"" + name + "\" has " + members.Count + " channels; the radio holds 50.");
                raw[0x00] = (byte)(sl.Get(row, "Scan Mode") == "Off" ? 0 : ParseInt(sl.Get(row, "Scan Mode"), 0));
                raw[0x01] = (byte)Math.Max(0, Array.IndexOf(PrioritySelects, sl.Get(row, "Priority Channel Select")));
                U16(raw, 0x02, PriorityChannel(sl.Get(row, "Priority Channel 1"), channelSlot));
                U16(raw, 0x04, PriorityChannel(sl.Get(row, "Priority Channel 2"), channelSlot));
                U16(raw, 0x06, Tenths(sl.Get(row, "Look Back Time A[s]")));
                U16(raw, 0x08, Tenths(sl.Get(row, "Look Back Time B[s]")));
                U16(raw, 0x0A, Tenths(sl.Get(row, "Dropout Delay Time[s]")));
                U16(raw, 0x0C, Tenths(sl.Get(row, "Dwell Time[s]")));
                int revert = Array.IndexOf(Reverts, sl.Get(row, "Revert Channel"));
                raw[0x0E] = (byte)(revert < 0 ? 0 : revert);
                Ascii(raw, 0x0F, name, 16);
                for (int m = 0; m < 50; m++)
                {
                    U16(raw, 0x20 + 2 * m, m < members.Count ? channelSlot[members[m]] : 0xFFFF);
                    raw[0x84 + m] = m < members.Count && zoneOf.TryGetValue(members[m], out int zi) ? (byte)zi : (byte)0xFF;
                }
                Put(img, Dmr6x2Pro.ScanListAddress(i), raw);
                SetBit(img, Dmr6x2Pro.ScanListBitmap, i, true);
            }

            // ---- Channels (and VFO A/B) ---------------------------------------------------------------------------
            foreach (var row in ch.Rows)
            {
                string name = ch.Get(row, "Channel Name");
                if (!channelSlot.TryGetValue(name, out int slot)) continue;
                bool vfo = slot >= Dmr6x2Pro.MaxChannels;
                int mode = Array.IndexOf(RadioCsv.ChannelTypes, ch.Get(row, "Channel Type"));
                if (mode < 0) { err.Add("Channel \"" + name + "\" has type \"" + ch.Get(row, "Channel Type") + "\"."); continue; }
                bool digital = mode == 1 || mode == 3;

                // Start from a record the radio holds: this slot if it's the same kind, else any channel of that kind.
                RadioChannel baseCh = vfo ? (slot == Dmr6x2Pro.MaxChannels ? cp.VfoA : cp.VfoB) : oldChannels.TryGetValue(slot, out var same) ? same : null;
                if (baseCh == null || IsDigital(baseCh) != digital)
                    baseCh = cp.Channels.FirstOrDefault(c => IsDigital(c) == digital)
                             ?? new[] { cp.VfoA, cp.VfoB }.FirstOrDefault(c => c != null && IsDigital(c) == digital)
                             ?? baseCh ?? cp.Channels.FirstOrDefault() ?? cp.VfoA;
                if (baseCh == null) { err.Add("The radio holds no channel to copy for \"" + name + "\" (read it again)."); continue; }
                var raw = (byte[])baseCh.Raw.Clone();
                var ext = baseCh.Extension != null ? (byte[])baseCh.Extension.Clone() : new byte[Dmr6x2Pro.ChannelSize];

                if (!TryHz(ch.Get(row, "Receive Frequency"), out long rx) || !TryHz(ch.Get(row, "Transmit Frequency"), out long tx))
                {
                    err.Add("Channel \"" + name + "\" has a frequency that isn't a number.");
                    continue;
                }
                Bcd(raw, 0x00, (uint)(rx / 10));
                int repeater = tx > rx ? 1 : tx < rx ? 2 : 0;
                // CPS 1.22e after a CSV import: a simplex VFO holds its TX frequency where a channel holds the offset.
                Bcd(raw, 0x04, (uint)((vfo && repeater == 0 ? tx : Math.Abs(tx - rx)) / 10));
                int power = Array.IndexOf(Powers.Values, ch.Get(row, "Transmit Power"));
                if (power < 0) { err.Add("Channel \"" + name + "\" has power \"" + ch.Get(row, "Transmit Power") + "\"."); power = 0; }
                bool wide = ch.Get(row, "Band Width") == Bandwidths.Wide;
                raw[0x08] = (byte)(raw[0x08] & 0x20 | repeater << 6 | (wide ? 1 : 0) << 4 | power << 2 | mode);

                int custom = ParseTenths(ch.Get(row, "Custom CTCSS"), raw[0x10] | raw[0x11] << 8);
                // Tone fields not in use are 0, as the CPS writes them after an import.
                raw[0x0A] = raw[0x0B] = 0;
                U16(raw, 0x0C, 0);
                U16(raw, 0x0E, 0);
                int rxSig = Tone(ch.Get(row, "CTCSS/DCS Decode"), raw, 0x0B, 0x0E, ref custom, name, err);
                int txSig = Tone(ch.Get(row, "CTCSS/DCS Encode"), raw, 0x0A, 0x0C, ref custom, name, err);
                raw[0x09] = (byte)(On(ch, row, "Talk Around") << 7 | On(ch, row, "Call Confirmation") << 6 | On(ch, row, "TX Prohibit") << 5
                                   | On(ch, row, "Reverse") << 4 | txSig << 2 | rxSig);
                U16(raw, 0x10, custom);
                raw[0x13] = (byte)ParseInt(ch.Get(row, "AES Digital Encryption"), raw[0x13]);

                string contact = ch.Get(row, "Contact");
                if (contactSlot.TryGetValue(contact, out int cs)) BitConverter.GetBytes((uint)cs).CopyTo(raw, 0x14);
                else err.Add("Channel \"" + name + "\" uses talkgroup \"" + contact + "\", which isn't in the talkgroup list.");
                string radioId = ch.Get(row, "Radio ID");
                if (radioIdIndex.TryGetValue(radioId, out int ri)) raw[0x18] = (byte)ri;
                else err.Add("Channel \"" + name + "\" uses radio ID \"" + radioId + "\", which isn't in the radio ID list.");

                raw[0x19] = (byte)(raw[0x19] & 0x8C | Pick(RadioCsv.SquelchModes, ch, row, "Squelch Mode") << 4 | Pick(RadioCsv.PttIds, ch, row, "PTT ID"));
                raw[0x1A] = (byte)(raw[0x1A] & 0xCC | Pick(RadioCsv.OptionalSignals, ch, row, "Optional Signal") << 4
                                   | Pick(digital ? RadioCsv.DigitalPermit : RadioCsv.AnalogPermit, ch, row, "Busy channel Lock-Out/TX Permit"));
                raw[0x1B] = (byte)(raw[0x1B] & 0xE2 | One(ch, row, "Random Key") << 4 | One(ch, row, "Multiple Key") << 3
                                   | On(ch, row, "Exclude Channel From Roaming") << 2 | On(ch, row, "Ranging"));
                string rgl = ch.Get(row, "Receive Group List");
                if (rgl == "None" || rgl.Length == 0) raw[0x1C] = 0xFF;
                else if (groupIndex.TryGetValue(rgl, out int gi)) raw[0x1C] = (byte)gi;
                else { res.Notes.Add("Channel \"" + name + "\": RX group list \"" + rgl + "\" doesn't exist, set to none."); raw[0x1C] = 0xFF; }
                raw[0x20] = (byte)ParseInt(ch.Get(row, "Color Code"), raw[0x20]);
                raw[0x21] = (byte)(raw[0x21] & 0x2A | On(ch, row, "Work Alone") << 7 | (ch.Get(row, "Extend Encryption Type") == "Enhanced Encryption" ? 1 : 0) << 6
                                   | On(ch, row, "TDMA Adaptive") << 4 | On(ch, row, "Simplex TDMA") << 2 | (ch.Get(row, "Slot") == "2" ? 1 : 0));
                string enc = ch.Get(row, "Digital Encryption");
                raw[0x22] = enc == "Off" ? (byte)0 : (byte)ParseInt(enc, raw[0x22]);
                Ascii(raw, 0x23, name, 16);
                raw[0x34] = (byte)(raw[0x34] & 0xFD | On(ch, row, "Through Mode") << 1);
                for (int n = 0; n < 8; n++)
                {
                    string s = ch.Get(row, "Scan List " + (n + 1));
                    if (s == null || s.Length == 0 || s == "None") raw[0x36 + n] = 0xFF;
                    else if (scanIndex.TryGetValue(s, out int si)) raw[0x36 + n] = (byte)si;
                    else { raw[0x36 + n] = 0xFF; res.Notes.Add("Channel \"" + name + "\": scan list \"" + s + "\" doesn't exist, set to none."); }
                }
                raw[0x3E] = (byte)(ParseInt(ch.Get(row, "APRS Report Channel"), raw[0x3E] + 1) - 1);
                ext[0] = (byte)ParseInt(ch.Get(row, "ARC4"), ext[0]);

                if (vfo)
                {
                    int v = slot - Dmr6x2Pro.MaxChannels;
                    Put(img, v == 0 ? Dmr6x2Pro.VfoA : Dmr6x2Pro.VfoB, raw);
                    Put(img, Dmr6x2Pro.VfoExtension + (uint)(v * Dmr6x2Pro.ChannelSize), ext);
                }
                else
                {
                    Put(img, Dmr6x2Pro.ChannelAddress(slot), raw);
                    Put(img, Dmr6x2Pro.ChannelAddress(slot) + Dmr6x2Pro.ChannelExtension, ext);
                    SetBit(img, Dmr6x2Pro.ChannelBitmap, slot, true);
                }
            }

            // ---- Zones (members by channel slot; A/B are positions in the member list) -------------------------
            foreach (var row in zn.Rows)
            {
                int i = Number(zn, row) - 1;
                string name = zn.Get(row, "Zone Name");
                if (i < 0 || i >= Dmr6x2Pro.MaxZones) { err.Add("Zone \"" + name + "\" has number " + zn.Get(row, "No.") + "."); continue; }
                var members = Members(zn.Get(row, "Zone Channel Member")).Where(m =>
                {
                    if (channelSlot.ContainsKey(m)) return true;
                    res.Notes.Add("Zone \"" + name + "\": channel \"" + m + "\" doesn't exist, left out.");
                    return false;
                }).ToList();
                if (members.Count > Dmr6x2Pro.MaxZoneMembers) err.Add("Zone \"" + name + "\" has " + members.Count + " channels; the radio holds " + Dmr6x2Pro.MaxZoneMembers + ".");
                var raw = oldZones.TryGetValue(i, out var o) && o != null ? (byte[])o.Clone() : Enumerable.Repeat((byte)0xFF, ZoneMembersSize).ToArray();
                for (int m = 0; m < Dmr6x2Pro.MaxZoneMembers; m++) U16(raw, 2 * m, m < members.Count ? channelSlot[members[m]] : 0xFFFF);
                Put(img, ZoneAddr(i), raw);
                var nameRaw = new byte[Dmr6x2Pro.ZoneNameStride];
                Ascii(nameRaw, 0, name, 16);
                Put(img, Dmr6x2Pro.ZoneNames + (uint)i * Dmr6x2Pro.ZoneNameStride, nameRaw);
                Put(img, Dmr6x2Pro.ZoneA + (uint)(2 * i), BitConverter.GetBytes((ushort)Math.Max(0, IndexOfName(members, zn.Get(row, "A Channel")))));
                Put(img, Dmr6x2Pro.ZoneB + (uint)(2 * i), BitConverter.GetBytes((ushort)Math.Max(0, IndexOfName(members, zn.Get(row, "B Channel")))));
                SetBit(img, Dmr6x2Pro.ZoneBitmap, i, true);
            }
            // Zone slots not in use: A = 0, B = 1 in the two A/B lists (CPS 1.22e after an import).
            var usedZones = new HashSet<int>(zn.Rows.Select(r => Number(zn, r) - 1));
            for (int i = 0; i < Dmr6x2Pro.MaxZones; i++)
            {
                if (usedZones.Contains(i)) continue;
                Put(img, Dmr6x2Pro.ZoneA + (uint)(2 * i), BitConverter.GetBytes((ushort)0));
                Put(img, Dmr6x2Pro.ZoneB + (uint)(2 * i), BitConverter.GetBytes((ushort)1));
            }

            // Settings that point into the channel and zone lists. CPS 1.22e set these to 0 after importing a new
            // channel list (2026-10-06), even where the old value was still valid: the two alarm revert channels
            // (uint16 slots) and general settings byte 0x1F (it held zone 4).
            foreach (uint a in new uint[] { 0x024C1406, 0x024C140E })
                if (img.Has(a, 2)) Put(img, a, new byte[2]);
            if (img.Has(Dmr6x2Pro.GeneralSettings + 0x1F, 1)) Put(img, Dmr6x2Pro.GeneralSettings + 0x1F, new byte[1]);

            res.Image = img;
            return res;
        }

        // ---- Helpers ------------------------------------------------------------------------------------------------

        static uint ZoneAddr(int i) => Dmr6x2Pro.ZoneChannels + (uint)i * Dmr6x2Pro.ZoneChannelsStride;
        static uint GroupAddr(int i) => Dmr6x2Pro.GroupLists + (uint)i * Dmr6x2Pro.GroupListStride;
        static bool IsDigital(RadioChannel c) => c.Mode == 1 || c.Mode == 3;

        static Dictionary<string, int> Index(CsvTable t, string col, Func<List<string>, int, int> slotOf, List<string> err, string what)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < t.Rows.Count; i++)
            {
                string name = t.Get(t.Rows[i], col);
                if (d.ContainsKey(name)) err.Add("Two " + what + "s are named \"" + name + "\".");
                else d[name] = slotOf(t.Rows[i], i);
            }
            return d;
        }

        static void CheckRange(Dictionary<string, int> d, int max, string what, List<string> err)
        {
            foreach (var kv in d)
                if (kv.Value < 0 || kv.Value >= max) err.Add("The " + what + " \"" + kv.Key + "\" has number " + (kv.Value + 1) + ", outside 1-" + max + ".");
            foreach (var g in d.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
                err.Add("Two " + what + "s have number " + (g.Key + 1) + ".");
        }

        static int Number(CsvTable t, List<string> row) => ParseInt(t.Get(row, "No."), 0);

        static List<string> Members(string s) => (s ?? "").Split('|').Where(m => m.Length > 0).ToList();

        static int IndexOfName(List<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++) if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static int PriorityChannel(string s, Dictionary<string, int> channelSlot)
        {
            if (s == null || s.Length == 0 || s == "Off") return 0xFFFF;
            if (s == "Current Channel") return 0;
            return channelSlot.TryGetValue(s, out int slot) ? slot + 1 : 0xFFFF;
        }

        static int Tone(string s, byte[] raw, int ctcssOff, int dcsOff, ref int custom, string channel, List<string> err)
        {
            if (s == null || s.Length == 0 || s == "Off") return 0;
            if (s[0] == 'D')
            {
                try
                {
                    int code = Convert.ToInt32(s.Substring(1, 3), 8) + (s.EndsWith("I") ? 512 : 0);
                    U16(raw, dcsOff, code);
                    return 2;
                }
                catch (Exception) { err.Add("Channel \"" + channel + "\" has tone \"" + s + "\"."); return 0; }
            }
            int idx = s == "62.5" ? 0 : Array.IndexOf(Tones.Ctcss, s) + 1;
            if (idx == 0 && s != "62.5")
            {
                if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal hz)) { err.Add("Channel \"" + channel + "\" has tone \"" + s + "\"."); return 0; }
                idx = 51;
                custom = (int)Math.Round(hz * 10);
            }
            raw[ctcssOff] = (byte)idx;
            return 1;
        }

        static int On(CsvTable t, List<string> row, string col) => t.Get(row, col) == "On" ? 1 : 0;
        static int One(CsvTable t, List<string> row, string col) => t.Get(row, col) == "1" ? 1 : 0;
        static int Pick(string[] values, CsvTable t, List<string> row, string col) => Math.Max(0, Array.IndexOf(values, t.Get(row, col)));

        static int ParseInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        static int Tenths(string s) =>
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) ? (int)Math.Round(v * 10) : 0;

        static int ParseTenths(string s, int fallback) =>
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) ? (int)Math.Round(v * 10) : fallback;

        static bool TryHz(string mhz, out long hz)
        {
            hz = 0;
            if (!decimal.TryParse(mhz, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) || v <= 0 || v >= 1000) return false;
            hz = (long)Math.Round(v * 1000000m);
            return true;
        }

        static void U16(byte[] raw, int off, int value)
        {
            raw[off] = (byte)value;
            raw[off + 1] = (byte)(value >> 8);
        }

        /// <summary>Clears <paramref name="max"/> bytes at <paramref name="off"/> and writes the name (ASCII, zero padded).</summary>
        static void Ascii(byte[] raw, int off, string s, int max)
        {
            Array.Clear(raw, off, max);
            var b = Encoding.ASCII.GetBytes(s ?? "");
            Buffer.BlockCopy(b, 0, raw, off, Math.Min(b.Length, max));
        }

        /// <summary>8 decimal digits, big-endian BCD.</summary>
        static void Bcd(byte[] raw, int off, uint value)
        {
            for (int i = 3; i >= 0; i--)
            {
                uint two = value % 100;
                value /= 100;
                raw[off + i] = (byte)((two / 10) << 4 | two % 10);
            }
        }

        /// <summary>The BCD bytes of <paramref name="value"/> read as one big-endian number (e.g. 93 → 0x93).</summary>
        static uint BcdValue(uint value)
        {
            var b = new byte[4];
            Bcd(b, 0, value);
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }

        /// <summary>Writes bytes; blocks never read from the radio start out erased (FF).</summary>
        static void Put(MemoryImage img, uint addr, byte[] data)
        {
            for (uint b = MemoryImage.AlignDown(addr); b < addr + (uint)data.Length; b += MemoryImage.BlockSize)
                if (!img.HasBlock(b)) img.PutBlock(b, Enumerable.Repeat((byte)0xFF, MemoryImage.BlockSize).ToArray());
            img.Set(addr, data);
        }

        static void Fill(MemoryImage img, uint addr, int length, byte value) => Put(img, addr, Enumerable.Repeat(value, length).ToArray());

        static void SetBit(MemoryImage img, uint bitmap, int i, bool value)
        {
            uint a = bitmap + (uint)(i / 8);
            byte v = img.HasBlock(MemoryImage.AlignDown(a)) ? img.U8(a) : (byte)0;
            v = value ? (byte)(v | 1 << (i % 8)) : (byte)(v & ~(1 << (i % 8)));
            Put(img, a, new[] { v });
        }

        /// <summary>Sets bits 0..count-1 of a bitmap to <paramref name="value"/>, leaving any bits after them.</summary>
        static void ClearBits(MemoryImage img, uint bitmap, int count, bool value)
        {
            for (int i = 0; i < count; i++) SetBit(img, bitmap, i, value);
        }
    }
}
