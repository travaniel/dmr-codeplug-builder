using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeplugBuilder.Core.Radio
{
    /// <summary>Base for a decoded record: a copy of its raw bytes plus typed views of the fields.</summary>
    public abstract class RadioRecord
    {
        /// <summary>0-based slot in the radio's table.</summary>
        public int Index;
        public byte[] Raw;

        protected RadioRecord(int index, byte[] raw)
        {
            Index = index;
            Raw = raw;
        }

        protected int U8(int off) => Raw[off];
        protected int U16(int off) => Raw[off] | Raw[off + 1] << 8;
        protected uint U32(int off) => (uint)(Raw[off] | Raw[off + 1] << 8 | Raw[off + 2] << 16 | Raw[off + 3] << 24);
        /// <summary><paramref name="width"/> bits of byte <paramref name="off"/> starting at bit <paramref name="lsb"/> (0 = least significant).</summary>
        protected int Bits(int off, int lsb, int width) => Raw[off] >> lsb & ((1 << width) - 1);
        protected bool Flag(int off, int bit) => (Raw[off] >> bit & 1) != 0;
        protected uint Bcd(int off) => RadioCodec.BcdBigEndian(Raw, off);
        protected string Text(int off, int max) => RadioCodec.AsciiZ(Raw, off, max);
    }

    public static class RadioCodec
    {
        /// <summary>4-byte big-endian BCD (8 digits), as used for frequencies (10 Hz units) and DMR IDs.</summary>
        public static uint BcdBigEndian(byte[] b, int off)
        {
            uint v = 0;
            for (int i = 0; i < 4; i++)
            {
                int hi = b[off + i] >> 4, lo = b[off + i] & 0xF;
                if (hi > 9 || lo > 9) throw new FormatException("Not a BCD number: " + BitConverter.ToString(b, off, 4));
                v = v * 100 + (uint)(hi * 10 + lo);
            }
            return v;
        }

        public static bool IsBcd(byte[] b, int off)
        {
            for (int i = 0; i < 4; i++)
                if ((b[off + i] >> 4) > 9 || (b[off + i] & 0xF) > 9) return false;
            return true;
        }

        public static string AsciiZ(byte[] b, int off, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < max && off + i < b.Length; i++)
            {
                byte c = b[off + i];
                if (c == 0) break;
                sb.Append((char)c);
            }
            return sb.ToString();
        }

        /// <summary>CTCSS table index → Hz text in the CPS spelling. Index 0 is 62.5, then the standard tones.</summary>
        public static string Ctcss(int index)
        {
            if (index == 0) return "62.5";
            if (index >= 1 && index <= Tones.Ctcss.Length) return Tones.Ctcss[index - 1];
            return null;
        }

        /// <summary>Stored DCS value (the octal code's value, +512 when inverted) → "D023N".</summary>
        public static string Dcs(int stored)
        {
            bool inverted = stored >= 512;
            int code = inverted ? stored - 512 : stored;
            return "D" + Convert.ToString(code, 8).PadLeft(3, '0') + (inverted ? "I" : "N");
        }
    }

    /// <summary>A channel (0x40 bytes) and its extension (0x40 bytes).</summary>
    public sealed class RadioChannel : RadioRecord
    {
        public byte[] Extension;
        public RadioChannel(int index, byte[] raw, byte[] extension) : base(index, raw) { Extension = extension; }

        /// <summary>CPS channel number ("No."): slot + 1; 4001/4002 for VFO A/B.</summary>
        public int Number => Index + 1;
        public bool IsVfo => Index >= Dmr6x2Pro.MaxChannels;

        public long RxHz => Bcd(0x00) * 10L;
        public long OffsetHz => Bcd(0x04) * 10L;
        /// <summary>0 simplex, 1 TX above RX, 2 TX below RX.</summary>
        public int RepeaterMode => Bits(0x08, 6, 2);
        public long TxHz => RepeaterMode == 1 ? RxHz + OffsetHz : RepeaterMode == 2 ? RxHz - OffsetHz : RxHz;
        public bool Wide => Flag(0x08, 4);
        /// <summary>0 low, 1 mid, 2 high, 3 turbo.</summary>
        public int Power => Bits(0x08, 2, 2);
        /// <summary>0 analog, 1 digital, 2 analog + digital RX, 3 digital + analog RX.</summary>
        public int Mode => Bits(0x08, 0, 2);
        public bool Talkaround => Flag(0x09, 7);
        public bool CallConfirm => Flag(0x09, 6);
        public bool RxOnly => Flag(0x09, 5);
        public bool Reverse => Flag(0x09, 4);
        /// <summary>0 off, 1 CTCSS, 2 DCS.</summary>
        public int TxSignaling => Bits(0x09, 2, 2);
        public int RxSignaling => Bits(0x09, 0, 2);
        public int TxCtcssIndex => U8(0x0A);
        public int RxCtcssIndex => U8(0x0B);
        public int TxDcs => U16(0x0C);
        public int RxDcs => U16(0x0E);
        /// <summary>Custom CTCSS in 0.1 Hz.</summary>
        public int CustomCtcss => U16(0x10);
        public int TwoToneDecodeIndex => U8(0x12);
        public int AesKeyIndex => U8(0x13);
        public int ContactIndex => (int)U32(0x14);
        public int RadioIdIndex => U8(0x18);
        /// <summary>0 carrier, 1 CTCSS/DCS, 2 optional signaling, 3 both.</summary>
        public int SquelchMode => Bits(0x19, 4, 3);
        /// <summary>0 off, 1 start, 2 end, 3 both.</summary>
        public int PttId => Bits(0x19, 0, 2);
        /// <summary>0 off, 1 DTMF, 2 2-tone, 3 5-tone.</summary>
        public int OptionalSignaling => Bits(0x1A, 4, 2);
        /// <summary>0 always, 1 color code, 2 channel free.</summary>
        public int TxPermit => Bits(0x1A, 0, 2);
        public bool Arc4Encryption => Flag(0x1B, 6);
        public bool RandomKey => Flag(0x1B, 4);
        public bool MultipleKey => Flag(0x1B, 3);
        public bool ExcludeFromRoaming => Flag(0x1B, 2);
        public bool SimplexMode => Flag(0x1B, 1);
        public bool Ranging => Flag(0x1B, 0);
        /// <summary>RX group list index, 0xFF = none.</summary>
        public int GroupListIndex => U8(0x1C);
        public int TwoToneIdIndex => U8(0x1D);
        public int FiveToneIdIndex => U8(0x1E);
        public int DtmfIdIndex => U8(0x1F);
        public int ColorCode => U8(0x20);
        public bool LoneWorker => Flag(0x21, 7);
        public bool EnhancedEncryption => Flag(0x21, 6);
        public bool RxAprs => Flag(0x21, 5);
        public bool AdaptiveTdma => Flag(0x21, 4);
        public bool SimplexTdma => Flag(0x21, 2);
        public bool SmsConfirm => Flag(0x21, 1);
        /// <summary>1 or 2.</summary>
        public int Slot => Flag(0x21, 0) ? 2 : 1;
        public int DmrKeyIndex => U8(0x22);
        public string Name => Text(0x23, 16);
        public bool DataAckDisable => Flag(0x34, 2);
        public bool ThroughMode => Flag(0x34, 1);
        /// <summary>Scan list index for list slot 0-7, 0xFF = none.</summary>
        public int ScanListIndex(int n) => U8(0x36 + n);
        public int AprsReportChannel => U8(0x3E);
        public bool DmrAprsRx => Flag(0x3F, 5);
        public bool DmrAprsPtt => Flag(0x3F, 4);
        public int FmAprsPttMode => Bits(0x3F, 2, 2);
        /// <summary>0 off, 1 FM APRS, 2 DMR APRS.</summary>
        public int AprsMode => Bits(0x3F, 0, 2);
        public int Arc4KeyIndex => Extension != null ? Extension[0] : 0;

        public string RxTone => Tone(RxSignaling, RxCtcssIndex, RxDcs);
        public string TxTone => Tone(TxSignaling, TxCtcssIndex, TxDcs);

        string Tone(int mode, int ctcss, int dcs)
        {
            if (mode == 1)
            {
                if (ctcss == 51) return (CustomCtcss / 10m).ToString("0.0", CultureInfo.InvariantCulture);
                return RadioCodec.Ctcss(ctcss) ?? "Off";
            }
            if (mode == 2) return RadioCodec.Dcs(dcs);
            return "Off";
        }
    }

    /// <summary>A digital contact: talkgroup, private call or all call (0x64 bytes).</summary>
    public sealed class RadioContact : RadioRecord
    {
        public RadioContact(int index, byte[] raw) : base(index, raw) { }
        /// <summary>0 private, 1 group, 2 all call.</summary>
        public int CallType => U8(0x00);
        public string Name => Text(0x01, 16);
        public uint Id => RadioCodec.IsBcd(Raw, 0x23) ? Bcd(0x23) : 0;
        /// <summary>0 none, 1 ring, 2 online alert.</summary>
        public int Alert => U8(0x27);
    }

    /// <summary>One of the radio's own DMR IDs (0x20 bytes).</summary>
    public sealed class RadioIdEntry : RadioRecord
    {
        public RadioIdEntry(int index, byte[] raw) : base(index, raw) { }
        public uint Id => RadioCodec.IsBcd(Raw, 0) ? Bcd(0) : 0;
        public string Name => Text(0x05, 16);
    }

    public sealed class RadioZone : RadioRecord
    {
        public string Name = "";
        /// <summary>Member channel slots (0-based), in order.</summary>
        public List<int> Members = new List<int>();
        /// <summary>Position in <see cref="Members"/> selected on side A and B (0xFFFF or out of range = none).</summary>
        public int A = 0xFFFF, B = 0xFFFF;

        /// <summary>Channel slot selected on side A (or B), or -1.</summary>
        public int ChannelA => A < Members.Count ? Members[A] : -1;
        public int ChannelB => B < Members.Count ? Members[B] : -1;
        public bool Hidden;
        public RadioZone(int index, byte[] members) : base(index, members) { }
    }

    /// <summary>RX group list (0x120 bytes): up to 64 contact slots and a name.</summary>
    public sealed class RadioGroupList : RadioRecord
    {
        public RadioGroupList(int index, byte[] raw) : base(index, raw) { }
        public string Name => Text(0x100, 16);
        public IEnumerable<int> Members()
        {
            for (int i = 0; i < Dmr6x2Pro.MaxGroupListMembers; i++)
            {
                uint c = U32(i * 4);
                if (c != 0xFFFFFFFF) yield return (int)c;
            }
        }
    }

    /// <summary>Scan list (0x90 bytes).</summary>
    public sealed class RadioScanList : RadioRecord
    {
        public RadioScanList(int index, byte[] raw) : base(index, raw) { }
        public int ScanMode => U8(0x00);
        /// <summary>0 none, 1 priority 1, 2 priority 2, 3 both.</summary>
        public int PrioritySelect => U8(0x01);
        /// <summary>Channel slot + 1, 0 = current channel, 0xFFFF = off.</summary>
        public int Priority1 => U16(0x02);
        public int Priority2 => U16(0x04);
        /// <summary>Times in 100 ms.</summary>
        public int LookBackA => U16(0x06);
        public int LookBackB => U16(0x08);
        public int DropoutDelay => U16(0x0A);
        public int Dwell => U16(0x0C);
        /// <summary>0 selected, 1 selected + talkback, 4 last called, 5 last used.</summary>
        public int RevertChannel => U8(0x0E);
        public string Name => Text(0x0F, 16);
        public IEnumerable<int> Members()
        {
            for (int i = 0; i < 50; i++)
            {
                int c = U16(0x20 + 2 * i);
                if (c != 0xFFFF) yield return c;
            }
        }
    }

    /// <summary>Analog address book entry (0x18 bytes).</summary>
    public sealed class RadioDtmfContact : RadioRecord
    {
        public RadioDtmfContact(int index, byte[] raw) : base(index, raw) { }
        public string Name => Text(0x08, 15);
        public string Number
        {
            get
            {
                const string digits = "0123456789ABCD*#";
                int len = Math.Min(U8(0x07), 14);
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    int b = Raw[i / 2];
                    sb.Append(digits[i % 2 == 0 ? b >> 4 : b & 0xF]);
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// The codeplug decoded from a <see cref="MemoryImage"/>. Raw bytes stay in the records, so fields
    /// that aren't decoded yet are still there for a later write.
    /// </summary>
    public sealed class RadioCodeplug
    {
        public string Model = "", Version = "";
        public List<RadioChannel> Channels = new List<RadioChannel>();
        public RadioChannel VfoA, VfoB;
        public List<RadioZone> Zones = new List<RadioZone>();
        /// <summary>Contacts in the CPS list order (the radio's order list when it's consistent, else slot order).</summary>
        public List<RadioContact> Contacts = new List<RadioContact>();
        public List<RadioIdEntry> RadioIds = new List<RadioIdEntry>();
        public List<RadioGroupList> GroupLists = new List<RadioGroupList>();
        public List<RadioScanList> ScanLists = new List<RadioScanList>();
        public List<RadioDtmfContact> DtmfContacts = new List<RadioDtmfContact>();
        public string BootLine1 = "", BootLine2 = "";
        public byte[] GeneralSettings, ExtendedSettings;
        /// <summary>Things that looked wrong while decoding.</summary>
        public List<string> Warnings = new List<string>();

        public RadioChannel ChannelAt(int index)
        {
            if (index == Dmr6x2Pro.MaxChannels) return VfoA;
            if (index == Dmr6x2Pro.MaxChannels + 1) return VfoB;
            return Channels.FirstOrDefault(c => c.Index == index);
        }

        public RadioContact ContactAt(int index) => Contacts.FirstOrDefault(c => c.Index == index);

        public static RadioCodeplug Decode(MemoryImage img)
        {
            var cp = new RadioCodeplug { Model = img.Model, Version = img.Version };
            var w = cp.Warnings;

            foreach (int i in Dmr6x2Pro.Enabled(img, Dmr6x2Pro.ChannelBitmap, Dmr6x2Pro.MaxChannels))
            {
                uint a = Dmr6x2Pro.ChannelAddress(i);
                var ch = new RadioChannel(i, img.Get(a, Dmr6x2Pro.ChannelSize),
                    img.Has(a + Dmr6x2Pro.ChannelExtension, Dmr6x2Pro.ChannelSize) ? img.Get(a + Dmr6x2Pro.ChannelExtension, Dmr6x2Pro.ChannelSize) : null);
                if (!RadioCodec.IsBcd(ch.Raw, 0) || !RadioCodec.IsBcd(ch.Raw, 4))
                {
                    w.Add("Channel " + (i + 1) + " has an unreadable frequency (" + BitConverter.ToString(ch.Raw, 0, 8) + "); skipped.");
                    continue;
                }
                cp.Channels.Add(ch);
            }
            cp.VfoA = Vfo(img, 0, w);
            cp.VfoB = Vfo(img, 1, w);

            // Contacts, then put them in the radio's list order if that list is consistent.
            var slots = Dmr6x2Pro.ExistingContacts(img).ToList();
            var bySlot = new Dictionary<int, RadioContact>();
            foreach (int i in slots)
                bySlot[i] = new RadioContact(i, img.Get(Dmr6x2Pro.ContactAddress(i), Dmr6x2Pro.ContactSize));
            List<RadioContact> ordered = null;
            if (slots.Count > 0 && img.Has(Dmr6x2Pro.ContactIndexList, 4 * slots.Count))
            {
                byte[] order = img.Get(Dmr6x2Pro.ContactIndexList, 4 * slots.Count);
                var list = new List<RadioContact>();
                for (int p = 0; p < slots.Count; p++)
                {
                    int s = (int)BitConverter.ToUInt32(order, 4 * p);
                    if (!bySlot.TryGetValue(s, out var c) || list.Contains(c)) { list = null; break; }
                    list.Add(c);
                }
                ordered = list;
                if (ordered == null) w.Add("The contact order list didn't match the contacts in use; contacts are listed in slot order.");
            }
            cp.Contacts = ordered ?? slots.Select(s => bySlot[s]).ToList();

            foreach (int i in Dmr6x2Pro.Enabled(img, Dmr6x2Pro.RadioIdBitmap, Dmr6x2Pro.MaxRadioIds))
                cp.RadioIds.Add(new RadioIdEntry(i, img.Get(Dmr6x2Pro.RadioIds + (uint)i * Dmr6x2Pro.RadioIdStride, (int)Dmr6x2Pro.RadioIdStride)));

            foreach (int i in Dmr6x2Pro.Enabled(img, Dmr6x2Pro.GroupListBitmap, Dmr6x2Pro.MaxGroupLists))
                cp.GroupLists.Add(new RadioGroupList(i, img.Get(Dmr6x2Pro.GroupLists + (uint)i * Dmr6x2Pro.GroupListStride, Dmr6x2Pro.GroupListSize)));

            foreach (int i in Dmr6x2Pro.Enabled(img, Dmr6x2Pro.ScanListBitmap, Dmr6x2Pro.MaxScanLists))
                cp.ScanLists.Add(new RadioScanList(i, img.Get(Dmr6x2Pro.ScanListAddress(i), Dmr6x2Pro.ScanListSize)));

            foreach (int i in Dmr6x2Pro.ExistingDtmfContacts(img))
                cp.DtmfContacts.Add(new RadioDtmfContact(i, img.Get(Dmr6x2Pro.DtmfContacts + (uint)(i * Dmr6x2Pro.DtmfContactSize), Dmr6x2Pro.DtmfContactSize)));

            foreach (int i in Dmr6x2Pro.Enabled(img, Dmr6x2Pro.ZoneBitmap, Dmr6x2Pro.MaxZones))
            {
                byte[] members = img.Get(Dmr6x2Pro.ZoneChannels + (uint)i * Dmr6x2Pro.ZoneChannelsStride, Dmr6x2Pro.MaxZoneMembers * 2);
                var z = new RadioZone(i, members)
                {
                    Name = RadioCodec.AsciiZ(img.Get(Dmr6x2Pro.ZoneNames + (uint)i * Dmr6x2Pro.ZoneNameStride, (int)Dmr6x2Pro.ZoneNameStride), 0, 16),
                    Hidden = img.Bit(Dmr6x2Pro.HiddenZoneBitmap, i),
                };
                for (int m = 0; m < Dmr6x2Pro.MaxZoneMembers; m++)
                {
                    int c = members[2 * m] | members[2 * m + 1] << 8;
                    if (c == 0xFFFF) continue;
                    if (cp.ChannelAt(c) == null) w.Add("Zone \"" + z.Name + "\" lists channel slot " + (c + 1) + ", which isn't in use.");
                    else z.Members.Add(c);
                }
                byte[] a = img.Get(Dmr6x2Pro.ZoneA + (uint)(2 * i), 2), b = img.Get(Dmr6x2Pro.ZoneB + (uint)(2 * i), 2);
                z.A = a[0] | a[1] << 8;
                z.B = b[0] | b[1] << 8;
                if (z.Members.Count > 0 && (z.ChannelA < 0 || z.ChannelB < 0))
                    w.Add("Zone \"" + z.Name + "\" has an A/B channel position past the end of its " + z.Members.Count + " channels.");
                cp.Zones.Add(z);
            }

            byte[] boot = img.Get(Dmr6x2Pro.BootSettings, 0x30);
            cp.BootLine1 = RadioCodec.AsciiZ(boot, 0x00, 16);
            cp.BootLine2 = RadioCodec.AsciiZ(boot, 0x10, 16);
            cp.GeneralSettings = img.Get(Dmr6x2Pro.GeneralSettings, Dmr6x2Pro.GeneralSettingsSize);
            cp.ExtendedSettings = img.Get(Dmr6x2Pro.ExtendedSettings, 0x30);

            foreach (var ch in cp.Channels)
            {
                if (ch.Mode != 0 && cp.ContactAt(ch.ContactIndex) == null)
                    w.Add("Channel " + ch.Number + " \"" + ch.Name + "\" points at contact slot " + (ch.ContactIndex + 1) + ", which isn't in use.");
                if (ch.GroupListIndex != 0xFF && cp.GroupLists.All(g => g.Index != ch.GroupListIndex))
                    w.Add("Channel " + ch.Number + " \"" + ch.Name + "\" points at RX group list " + (ch.GroupListIndex + 1) + ", which isn't in use.");
            }
            return cp;
        }

        static RadioChannel Vfo(MemoryImage img, int n, List<string> warnings)
        {
            uint a = n == 0 ? Dmr6x2Pro.VfoA : Dmr6x2Pro.VfoB;
            var ch = new RadioChannel(Dmr6x2Pro.MaxChannels + n, img.Get(a, Dmr6x2Pro.ChannelSize), img.Get(Dmr6x2Pro.VfoExtension + (uint)(n * Dmr6x2Pro.ChannelSize), Dmr6x2Pro.ChannelSize));
            if (!RadioCodec.IsBcd(ch.Raw, 0) || !RadioCodec.IsBcd(ch.Raw, 4))
            {
                warnings.Add("VFO " + (n == 0 ? "A" : "B") + " has an unreadable frequency.");
                return null;
            }
            return ch;
        }
    }
}
