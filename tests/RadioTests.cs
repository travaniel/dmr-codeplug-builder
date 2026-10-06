using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CodeplugBuilder.Core;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.Tests
{
    /// <summary>
    /// A simulated DMR-6X2 PRO on the other end of the USB serial port: answers PROGRAM, identify,
    /// 16-byte reads and END from a <see cref="MemoryImage"/> (unread memory reads as zeros).
    /// </summary>
    sealed class FakeRadio : Stream
    {
        readonly MemoryImage memory;
        readonly Queue<byte> toHost = new Queue<byte>();
        readonly List<byte> fromHost = new List<byte>();
        bool program;

        public string Model = "D6X2UV2";
        /// <summary>Corrupt the checksum of this many read replies (to test retries).</summary>
        public int CorruptReplies;
        public int Reads, Ends;
        public readonly List<uint> Writes = new List<uint>();
        public MemoryImage Memory => memory;

        public FakeRadio(MemoryImage memory) { this.memory = memory; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) fromHost.Add(buffer[offset + i]);
            Process();
        }

        void Process()
        {
            while (fromHost.Count > 0)
            {
                if (Starts("PROGRAM")) { Consume(7); program = true; Reply(Encoding.ASCII.GetBytes("QX\x06")); continue; }
                if (Starts("END")) { Consume(3); program = false; Ends++; Reply(new byte[] { 0x06 }); continue; }
                if (program && fromHost[0] == 0x02)
                {
                    Consume(1);
                    var id = new byte[16];
                    id[0] = (byte)'I';
                    Encoding.ASCII.GetBytes(Model).CopyTo(id, 1);
                    id[8] = 0x00;
                    Encoding.ASCII.GetBytes("V100").CopyTo(id, 9);
                    id[15] = 0x06;
                    Reply(id);
                    continue;
                }
                if (program && fromHost[0] == (byte)'R')
                {
                    if (fromHost.Count < 6) return;
                    uint addr = AnytoneLink.GetBigEndian(fromHost.ToArray(), 1);
                    Consume(6);
                    Reads++;
                    var resp = new byte[24];
                    resp[0] = (byte)'W';
                    AnytoneLink.PutBigEndian(resp, 1, addr);
                    resp[5] = 0x10;
                    byte[] data = memory.HasBlock(addr) ? memory.Get(addr, 16) : new byte[16];
                    Buffer.BlockCopy(data, 0, resp, 6, 16);
                    resp[22] = AnytoneLink.Checksum(resp, 1, 21);
                    resp[23] = 0x06;
                    if (CorruptReplies > 0) { CorruptReplies--; resp[22]++; }
                    Reply(resp);
                    continue;
                }
                if (program && fromHost[0] == (byte)'W')
                {
                    if (fromHost.Count < 24) return;
                    byte[] req = fromHost.GetRange(0, 24).ToArray();
                    Consume(24);
                    uint addr = AnytoneLink.GetBigEndian(req, 1);
                    bool ok = req[5] == 0x10 && req[22] == AnytoneLink.Checksum(req, 1, 21) && req[23] == 0x06;
                    if (ok)
                    {
                        var data = new byte[16];
                        Buffer.BlockCopy(req, 6, data, 0, 16);
                        memory.PutBlock(addr, data);
                        Writes.Add(addr);
                    }
                    Reply(new byte[] { ok ? (byte)0x06 : (byte)0xFF });
                    continue;
                }
                if ("PROGRAM".StartsWith(Encoding.ASCII.GetString(fromHost.ToArray())) || "END".StartsWith(Encoding.ASCII.GetString(fromHost.ToArray())))
                    return; // wait for the rest of the command
                fromHost.RemoveAt(0); // unknown byte: ignore, like a real radio would
            }
        }

        bool Starts(string s)
        {
            if (fromHost.Count < s.Length) return false;
            for (int i = 0; i < s.Length; i++) if (fromHost[i] != (byte)s[i]) return false;
            return true;
        }

        void Consume(int n) => fromHost.RemoveRange(0, n);
        void Reply(byte[] b) { foreach (byte x in b) toHost.Enqueue(x); }
        public void DiscardInput() => toHost.Clear();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count && toHost.Count > 0) buffer[offset + n++] = toHost.Dequeue();
            return n; // 0 = nothing pending, which the link treats as a timeout
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Builds radio memory from a CPS export, following the same documented layout the decoder reads.
    /// It checks the plumbing (protocol, read plan, decoder, CSV writer) end to end; whether the layout
    /// matches the real radio can only be checked against a real read (--radio-compare).
    /// </summary>
    static class FakeCodeplug
    {
        public static MemoryImage FromExport(string folder)
        {
            var img = new MemoryImage { Model = "D6X2UV2", Version = "V100" };
            img.Set(Dmr6x2Pro.ContactBitmap, Enumerable.Repeat((byte)0xFF, Dmr6x2Pro.ContactBitmapSize).ToArray());
            img.Set(Dmr6x2Pro.DtmfContactBytemap, Enumerable.Repeat((byte)0xFF, Dmr6x2Pro.MaxDtmfContacts).ToArray());
            img.Set(Dmr6x2Pro.MessageBytemap, Enumerable.Repeat((byte)0xFF, Dmr6x2Pro.MaxMessages).ToArray());
            img.Set(Dmr6x2Pro.BootSettings, Encoding.ASCII.GetBytes("WELCOME\0\0\0\0\0\0\0\0\0DMR-6X2\0"));

            // Talkgroups: slot = list position, order list = slots in order.
            var tg = CsvTable.Load(CpsFormat.FindFile(folder, CpsFormat.TalkGroupsFile));
            var contactSlot = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < tg.Rows.Count; i++)
            {
                var row = tg.Rows[i];
                var raw = new byte[Dmr6x2Pro.ContactSize];
                raw[0] = (byte)Array.IndexOf(RadioCsv.CallTypeNames, tg.Get(row, "Call Type"));
                Ascii(raw, 1, tg.Get(row, "Name"), 16);
                Bcd(raw, 0x23, uint.Parse(tg.Get(row, "Radio ID"), CultureInfo.InvariantCulture));
                raw[0x27] = (byte)Math.Max(0, Array.IndexOf(RadioCsv.Alerts, tg.Get(row, "Call Alert")));
                img.Set(Dmr6x2Pro.ContactAddress(i), raw);
                ClearBit(img, Dmr6x2Pro.ContactBitmap, i);
                img.Set(Dmr6x2Pro.ContactIndexList + (uint)(4 * i), BitConverter.GetBytes((uint)i));
                contactSlot[tg.Get(row, "Name")] = i;
            }

            var rid = CsvTable.Load(CpsFormat.FindFile(folder, CpsFormat.RadioIdFile));
            var radioIdIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in rid.Rows)
            {
                int i = int.Parse(rid.Get(row, "No."), CultureInfo.InvariantCulture) - 1;
                var raw = new byte[Dmr6x2Pro.RadioIdStride];
                Bcd(raw, 0, uint.Parse(rid.Get(row, "Radio ID"), CultureInfo.InvariantCulture));
                Ascii(raw, 5, rid.Get(row, "Name"), 16);
                img.Set(Dmr6x2Pro.RadioIds + (uint)i * Dmr6x2Pro.RadioIdStride, raw);
                SetBit(img, Dmr6x2Pro.RadioIdBitmap, i);
                radioIdIndex[rid.Get(row, "Name")] = i;
            }

            var rg = CsvTable.Load(CpsFormat.FindFile(folder, CpsFormat.RxGroupFile));
            var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in rg.Rows)
            {
                int i = int.Parse(rg.Get(row, "No."), CultureInfo.InvariantCulture) - 1;
                var raw = Enumerable.Repeat((byte)0xFF, Dmr6x2Pro.GroupListSize).ToArray();
                Array.Clear(raw, 0x100, 0x20);
                var members = rg.Get(row, "Contact").Split('|').Where(m => m.Length > 0).ToList();
                for (int m = 0; m < members.Count; m++) BitConverter.GetBytes((uint)contactSlot[members[m]]).CopyTo(raw, 4 * m);
                Ascii(raw, 0x100, rg.Get(row, "Group Name"), 16);
                img.Set(Dmr6x2Pro.GroupLists + (uint)i * Dmr6x2Pro.GroupListStride, raw);
                SetBit(img, Dmr6x2Pro.GroupListBitmap, i);
                groupIndex[rg.Get(row, "Group Name")] = i;
            }

            var ch = CsvTable.Load(CpsFormat.FindFile(folder, CpsFormat.ChannelFile));
            var channelIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in ch.Rows)
            {
                int no = CpsFormat.ChannelNumber(ch, row);
                int i = no - 1;
                var raw = EncodeChannel(ch, row, contactSlot, radioIdIndex, groupIndex);
                var ext = new byte[Dmr6x2Pro.ChannelSize];
                ext[0] = byte.Parse(ch.Get(row, "ARC4"), CultureInfo.InvariantCulture);
                if (no >= CpsFormat.FirstVfoNumber)
                {
                    int v = no - CpsFormat.FirstVfoNumber;
                    Array.Clear(raw, 0x23, 16); // the CPS shows fixed names for the VFOs
                    img.Set((v == 0 ? Dmr6x2Pro.VfoA : Dmr6x2Pro.VfoB), raw);
                    img.Set(Dmr6x2Pro.VfoExtension + (uint)(v * Dmr6x2Pro.ChannelSize), ext);
                    channelIndex[ch.Get(row, "Channel Name")] = Dmr6x2Pro.MaxChannels + v;
                    continue;
                }
                img.Set(Dmr6x2Pro.ChannelAddress(i), raw);
                img.Set(Dmr6x2Pro.ChannelAddress(i) + Dmr6x2Pro.ChannelExtension, ext);
                SetBit(img, Dmr6x2Pro.ChannelBitmap, i);
                channelIndex[ch.Get(row, "Channel Name")] = i;
            }

            var zn = CsvTable.Load(CpsFormat.FindFile(folder, CpsFormat.ZoneFile));
            foreach (var row in zn.Rows)
            {
                int i = int.Parse(zn.Get(row, "No."), CultureInfo.InvariantCulture) - 1;
                var members = Enumerable.Repeat((byte)0xFF, Dmr6x2Pro.MaxZoneMembers * 2).ToArray();
                var names = zn.Get(row, "Zone Channel Member").Split('|').Where(m => m.Length > 0).ToList();
                for (int m = 0; m < names.Count; m++) BitConverter.GetBytes((ushort)channelIndex[names[m]]).CopyTo(members, 2 * m);
                img.Set(Dmr6x2Pro.ZoneChannels + (uint)i * Dmr6x2Pro.ZoneChannelsStride, members);
                var name = new byte[Dmr6x2Pro.ZoneNameStride];
                Ascii(name, 0, zn.Get(row, "Zone Name"), 16);
                img.Set(Dmr6x2Pro.ZoneNames + (uint)i * Dmr6x2Pro.ZoneNameStride, name);
                // A/B are positions within the zone, in two separate lists.
                img.Set(Dmr6x2Pro.ZoneA + (uint)(2 * i), BitConverter.GetBytes((ushort)names.IndexOf(zn.Get(row, "A Channel"))));
                img.Set(Dmr6x2Pro.ZoneB + (uint)(2 * i), BitConverter.GetBytes((ushort)names.IndexOf(zn.Get(row, "B Channel"))));
                SetBit(img, Dmr6x2Pro.ZoneBitmap, i);
            }
            return img;
        }

        static byte[] EncodeChannel(CsvTable t, List<string> row, Dictionary<string, int> contacts, Dictionary<string, int> radioIds, Dictionary<string, int> groups)
        {
            var raw = new byte[Dmr6x2Pro.ChannelSize];
            long rx = Hz(t.Get(row, "Receive Frequency")), tx = Hz(t.Get(row, "Transmit Frequency"));
            Bcd(raw, 0, (uint)(rx / 10));
            Bcd(raw, 4, (uint)(Math.Abs(tx - rx) / 10));
            int repeater = tx > rx ? 1 : tx < rx ? 2 : 0;
            int mode = Array.IndexOf(RadioCsv.ChannelTypes, t.Get(row, "Channel Type"));
            int power = Array.IndexOf(Powers.Values, t.Get(row, "Transmit Power"));
            bool wide = t.Get(row, "Band Width") == Bandwidths.Wide;
            raw[0x08] = (byte)(repeater << 6 | (wide ? 1 : 0) << 4 | power << 2 | mode);

            int custom = (int)Math.Round(decimal.Parse(t.Get(row, "Custom CTCSS"), CultureInfo.InvariantCulture) * 10);
            int rxSig = Tone(t.Get(row, "CTCSS/DCS Decode"), raw, 0x0B, 0x0E, ref custom);
            int txSig = Tone(t.Get(row, "CTCSS/DCS Encode"), raw, 0x0A, 0x0C, ref custom);
            raw[0x09] = (byte)(Bit(t, row, "Talk Around") << 7 | Bit(t, row, "Call Confirmation") << 6 | Bit(t, row, "TX Prohibit") << 5
                               | Bit(t, row, "Reverse") << 4 | txSig << 2 | rxSig);
            BitConverter.GetBytes((ushort)custom).CopyTo(raw, 0x10);
            raw[0x13] = byte.Parse(t.Get(row, "AES Digital Encryption"), CultureInfo.InvariantCulture);
            BitConverter.GetBytes((uint)contacts[t.Get(row, "Contact")]).CopyTo(raw, 0x14);
            raw[0x18] = (byte)radioIds[t.Get(row, "Radio ID")];
            raw[0x19] = (byte)(Array.IndexOf(RadioCsv.SquelchModes, t.Get(row, "Squelch Mode")) << 4 | Array.IndexOf(RadioCsv.PttIds, t.Get(row, "PTT ID")));
            bool digital = mode == 1 || mode == 3;
            int permit = Array.IndexOf(digital ? RadioCsv.DigitalPermit : RadioCsv.AnalogPermit, t.Get(row, "Busy channel Lock-Out/TX Permit"));
            raw[0x1A] = (byte)(Array.IndexOf(RadioCsv.OptionalSignals, t.Get(row, "Optional Signal")) << 4 | permit);
            raw[0x1B] = (byte)(Num(t, row, "Random Key") << 4 | Num(t, row, "Multiple Key") << 3 | Bit(t, row, "Exclude Channel From Roaming") << 2 | Bit(t, row, "Ranging"));
            string rgl = t.Get(row, "Receive Group List");
            raw[0x1C] = rgl == "None" ? (byte)0xFF : (byte)groups[rgl];
            raw[0x20] = byte.Parse(t.Get(row, "Color Code"), CultureInfo.InvariantCulture);
            raw[0x21] = (byte)(Bit(t, row, "Work Alone") << 7 | (t.Get(row, "Extend Encryption Type") == "Enhanced Encryption" ? 1 : 0) << 6
                               | Bit(t, row, "TDMA Adaptive") << 4 | Bit(t, row, "Simplex TDMA") << 2 | (t.Get(row, "Slot") == "2" ? 1 : 0));
            string enc = t.Get(row, "Digital Encryption");
            raw[0x22] = enc == "Off" ? (byte)0 : byte.Parse(enc, CultureInfo.InvariantCulture);
            Ascii(raw, 0x23, t.Get(row, "Channel Name"), 16);
            raw[0x34] = (byte)(Bit(t, row, "Through Mode") << 1);
            for (int i = 0; i < 8; i++) raw[0x36 + i] = 0xFF; // the export has no scan lists
            raw[0x3E] = (byte)(int.Parse(t.Get(row, "APRS Report Channel"), CultureInfo.InvariantCulture) - 1);
            return raw;
        }

        /// <summary>Encodes a tone into the CTCSS/DCS fields; returns the signaling mode (0 off, 1 CTCSS, 2 DCS).</summary>
        static int Tone(string s, byte[] raw, int ctcssOff, int dcsOff, ref int custom)
        {
            if (s == "Off") return 0;
            if (s.StartsWith("D"))
            {
                int code = Convert.ToInt32(s.Substring(1, 3), 8) + (s.EndsWith("I") ? 512 : 0);
                BitConverter.GetBytes((ushort)code).CopyTo(raw, dcsOff);
                return 2;
            }
            int idx = s == "62.5" ? 0 : Array.IndexOf(Tones.Ctcss, s) + 1;
            if (idx == 0 && s != "62.5")
            {
                idx = 51;
                custom = (int)Math.Round(decimal.Parse(s, CultureInfo.InvariantCulture) * 10);
            }
            raw[ctcssOff] = (byte)idx;
            return 1;
        }

        static int Bit(CsvTable t, List<string> row, string col) => t.Get(row, col) == "On" ? 1 : 0;
        static int Num(CsvTable t, List<string> row, string col) => t.Get(row, col) == "1" ? 1 : 0;
        static long Hz(string mhz) => (long)Math.Round(decimal.Parse(mhz, CultureInfo.InvariantCulture) * 1000000m);

        public static void Bcd(byte[] raw, int off, uint value)
        {
            for (int i = 3; i >= 0; i--)
            {
                uint two = value % 100;
                value /= 100;
                raw[off + i] = (byte)((two / 10) << 4 | two % 10);
            }
        }

        static void Ascii(byte[] raw, int off, string s, int max)
        {
            var b = Encoding.ASCII.GetBytes(s);
            Buffer.BlockCopy(b, 0, raw, off, Math.Min(b.Length, max));
        }

        static void SetBit(MemoryImage img, uint bitmap, int i)
        {
            uint a = bitmap + (uint)(i / 8);
            byte v = img.Has(a, 1) ? img.U8(a) : (byte)0;
            img.Set(a, new[] { (byte)(v | 1 << (i % 8)) });
        }

        static void ClearBit(MemoryImage img, uint bitmap, int i)
        {
            uint a = bitmap + (uint)(i / 8);
            img.Set(a, new[] { (byte)(img.U8(a) & ~(1 << (i % 8))) });
        }
    }

    static class RadioSettingsTests
    {
        static MemoryImage SettingsImage()
        {
            var img = new MemoryImage();
            var rnd = new Random(42);
            foreach (var r in Dmr6x2Pro.FixedRanges())
            {
                var data = new byte[r.Length];
                rnd.NextBytes(data);
                img.Set(r.Address, data);
            }
            return img;
        }

        [Test]
        static void TableIsConsistent()
        {
            var all = RadioSettings.All;
            Assert.True(all.Count > 100, "settings listed: " + all.Count);
            Assert.Equal(all.Count, all.Select(d => d.Key).Distinct().Count(), "unique keys");
            foreach (var g in all.GroupBy(d => d.Group))
                Assert.Equal(g.Count(), g.Select(d => d.Label).Distinct().Count(), "unique labels in " + g.Key);
            // No two settings claim the same bits.
            var used = new Dictionary<string, string>();
            foreach (var d in all)
            {
                int bits = d.Kind == SettingKind.Choice || d.Kind == SettingKind.Flag ? d.Bits : d.Length * 8;
                for (int i = 0; i < bits; i++)
                {
                    int bit = d.Kind == SettingKind.Choice || d.Kind == SettingKind.Flag ? d.Lsb + i : i;
                    uint byteAddr = d.Address + (uint)(bit / 8);
                    string k = byteAddr.ToString("X7") + "." + bit % 8;
                    Assert.True(!used.ContainsKey(k), d.Key + " overlaps " + (used.ContainsKey(k) ? used[k] : ""));
                    used[k] = d.Key;
                }
                if (d.Kind == SettingKind.Choice)
                {
                    Assert.True(d.Options.Count > 0, d.Key + " has options");
                    Assert.Equal(d.Options.Count, d.Options.Select(o => o.Key).Distinct().Count(), d.Key + " option values unique");
                    Assert.True(d.Options.All(o => o.Key >= 0 && o.Key < 1 << d.Bits), d.Key + " options fit in " + d.Bits + " bits");
                }
                Assert.True(Dmr6x2Pro.FixedRanges().Any(r => d.Address >= r.Address && d.Address + d.Length <= r.Address + r.Length), d.Key + " is inside an area the reader reads");
            }
        }

        [Test]
        static void WriteChangesOnlyThatSetting()
        {
            var img = SettingsImage();
            var before = SettingsImage();
            foreach (var d in RadioSettings.All)
            {
                var copy = MemoryImage.Load(Save(img));
                if (d.Kind == SettingKind.Text)
                {
                    string s = "AB12".Substring(0, Math.Min(4, d.Length));
                    RadioSettings.WriteText(copy, d, s);
                    Assert.Equal(s, RadioSettings.Read(copy, d).Text, d.Key + " text");
                }
                else
                {
                    long v = d.Kind == SettingKind.Choice ? d.Options[d.Options.Count - 1].Key : d.Kind == SettingKind.Flag ? 1 - RadioSettings.Read(img, d).Raw : 14652000;
                    RadioSettings.Write(copy, d, v);
                    Assert.Equal(v, RadioSettings.Read(copy, d).Raw, d.Key + " value");
                }
                // Every other setting reads the same as before.
                foreach (var o in RadioSettings.All.Where(o => o != d))
                {
                    var a = RadioSettings.Read(img, o);
                    var b = RadioSettings.Read(copy, o);
                    Assert.True(a.Raw == b.Raw && a.Text == b.Text, "writing " + d.Key + " changed " + o.Key);
                }
            }
            Assert.True(Blocks(before) == Blocks(img), "original image untouched");
        }

        [Test]
        static void RejectsBadValues()
        {
            var img = SettingsImage();
            var tot = RadioSettings.Find("Tot");
            try { RadioSettings.Write(img, tot, 200); throw new Exception("accepted TOT 200"); }
            catch (ArgumentOutOfRangeException) { }
            try { RadioSettings.WriteText(img, RadioSettings.Find("BootLine1"), "Ünïcode"); throw new Exception("accepted non-ASCII"); }
            catch (ArgumentException) { }
            Assert.Equal("90 s", RadioSettings.Find("Tot").OptionLabel(3), "TOT label");
            Assert.Equal("Off", RadioSettings.Find("Tot").OptionLabel(0), "TOT off");
            Assert.Equal("VFO", RadioSettings.Find("DefaultChannelA").OptionLabel(0xFF), "start channel VFO");
            var tz = RadioSettings.Find("TimeZone");
            Assert.Equal("GMT-5", tz.OptionLabel(14), "time zone 14 (the CPS showed GMT-5 for it)");
            Assert.Equal("GMT-12", tz.OptionLabel(0), "time zone 0");
            Assert.Equal("GMT", tz.OptionLabel(24), "time zone 24");
            Assert.Equal("GMT+5:30", tz.OptionLabel(35), "time zone 35");
        }

        static string Blocks(MemoryImage img) =>
            string.Join(";", img.Runs().Select(r => r.Key.ToString("X7") + ":" + Convert.ToBase64String(r.Value)));

        static MemoryStream Save(MemoryImage img)
        {
            var ms = new MemoryStream();
            img.Save(ms);
            ms.Position = 0;
            return ms;
        }
    }

    static class RadioWriteTests
    {
        static MemoryImage Clone(MemoryImage img)
        {
            var ms = new MemoryStream();
            img.Save(ms);
            ms.Position = 0;
            return MemoryImage.Load(ms);
        }

        /// <summary>A radio with the user's codeplug, and an image read from it.</summary>
        static (FakeRadio radio, MemoryImage read) Setup()
        {
            RadioWriter.Enabled = true; // the simulated radio has no sectors
            var radio = new FakeRadio(FakeCodeplug.FromExport(Fixtures.RequireExport()));
            var read = RadioReader.Read(new AnytoneLink(radio, radio.DiscardInput));
            return (radio, read);
        }

        [Test]
        static void WritesOnlyTheChangedBlockAndVerifies()
        {
            var (radio, read) = Setup();
            var edited = Clone(read);
            var color = RadioSettings.Find("ZoneAColor");
            RadioSettings.Write(edited, color, 2); // yellow
            Assert.Equal(1, RadioWriter.ChangedBlocks(read, edited).Count, "one block changed");

            var result = RadioWriter.Write(new AnytoneLink(radio, radio.DiscardInput), read, edited);
            // A write always sends the full CPS-style set, in address order, never just the changed block.
            var expected = Dmr6x2Pro.WriteSet(edited);
            Assert.Equal(string.Join(",", expected), string.Join(",", radio.Writes), "full write set, in order");
            Assert.True(radio.Writes.Contains(MemoryImage.AlignDown(color.Address)), "includes the changed block");
            Assert.True(radio.Writes.Contains(Dmr6x2Pro.GeneralSettings), "includes the general settings");
            Assert.Equal(0, RadioWriter.Verify(new AnytoneLink(radio, radio.DiscardInput), edited, result.Blocks).Count, "verified in a new session");
            Assert.Equal(2L, RadioSettings.Read(radio.Memory, color).Raw, "radio now holds yellow");
            Assert.Equal(3, radio.Ends, "left programming mode after read, write and verify");

            // Reading again gives the edited image.
            var again = RadioReader.Read(new AnytoneLink(radio, radio.DiscardInput));
            Assert.Equal(0, RadioWriter.ChangedBlocks(again, edited).Count, "radio matches the edited image");
        }

        [Test]
        static void RefusesWhenTheRadioChangedSinceTheRead()
        {
            var (radio, read) = Setup();
            var edited = Clone(read);
            RadioSettings.Write(edited, RadioSettings.Find("ZoneAColor"), 2);
            // Someone changes the same block on the radio after the read.
            radio.Memory.Set(RadioSettings.Find("ChannelAColor").Address, new byte[] { 1 });
            try
            {
                RadioWriter.Write(new AnytoneLink(radio, radio.DiscardInput), read, edited);
                throw new Exception("should refuse");
            }
            catch (RadioProtocolException ex)
            {
                Assert.True(ex.Message.Contains("changed since it was read"), ex.Message);
            }
            Assert.Equal(0, radio.Writes.Count, "nothing written");
        }

        [Test]
        static void NothingToWriteAndUnreadDataAreHandled()
        {
            var (radio, read) = Setup();
            var result = RadioWriter.Write(new AnytoneLink(radio, radio.DiscardInput), read, Clone(read));
            Assert.Equal(0, result.Blocks.Count, "no blocks");
            Assert.Equal(0, radio.Writes.Count, "nothing written");

            var edited = Clone(read);
            edited.Set(0x05500000, new byte[16]); // the call-sign DB area, never read
            try
            {
                RadioWriter.ChangedBlocks(read, edited);
                throw new Exception("should refuse unread data");
            }
            catch (InvalidOperationException) { }
        }
    }

    static class RadioTests
    {
        [Test]
        static void CodecBasics()
        {
            var b = new byte[4];
            FakeCodeplug.Bcd(b, 0, 44412500);
            Assert.Equal("44-41-25-00", BitConverter.ToString(b), "BCD of 444.125 MHz in 10 Hz");
            Assert.Equal(44412500u, RadioCodec.BcdBigEndian(b, 0), "BCD back");
            Assert.True(!RadioCodec.IsBcd(new byte[] { 0xFF, 0, 0, 0 }, 0), "FF isn't BCD");
            Assert.Equal("D023N", RadioCodec.Dcs(19), "DCS 023 (octal) = 19");
            Assert.Equal("D754I", RadioCodec.Dcs(Convert.ToInt32("754", 8) + 512), "inverted DCS");
            Assert.Equal("62.5", RadioCodec.Ctcss(0), "CTCSS index 0");
            Assert.Equal("67.0", RadioCodec.Ctcss(1), "CTCSS index 1");
            Assert.Equal("254.1", RadioCodec.Ctcss(50), "CTCSS index 50");
            Assert.True(RadioCodec.Ctcss(51) == null, "51 is custom");
        }

        [Test]
        static void ReadResponseChecks()
        {
            var resp = new byte[24];
            resp[0] = (byte)'W';
            AnytoneLink.PutBigEndian(resp, 1, 0x02500000);
            resp[5] = 0x10;
            for (int i = 0; i < 16; i++) resp[6 + i] = (byte)(i * 7);
            resp[22] = AnytoneLink.Checksum(resp, 1, 21);
            resp[23] = 0x06;
            Assert.True(AnytoneLink.CheckReadResponse(resp, 0x02500000) == null, "good reply");
            Assert.True(AnytoneLink.CheckReadResponse(resp, 0x02500010) != null, "wrong address caught");
            resp[10] ^= 1;
            Assert.True(AnytoneLink.CheckReadResponse(resp, 0x02500000) != null, "bad checksum caught");
        }

        [Test]
        static void MemoryImageSaveLoad()
        {
            var img = new MemoryImage { Model = "D6X2UV2", Version = "V100", Bands = 3 };
            img.Set(0x00800000, Enumerable.Range(0, 40).Select(i => (byte)i).ToArray());
            img.Set(0x02500000, new byte[] { 1, 2, 3 });
            var ms = new MemoryStream();
            img.Save(ms);
            ms.Position = 0;
            var back = MemoryImage.Load(ms);
            Assert.Equal("D6X2UV2", back.Model, "model");
            Assert.Equal((byte)3, back.Bands, "bands");
            Assert.Equal(img.BlockCount, back.BlockCount, "blocks");
            Assert.Equal(BitConverter.ToString(img.Get(0x00800000, 48)), BitConverter.ToString(back.Get(0x00800000, 48)), "data");
            Assert.True(!back.Has(0x00800030, 1), "unread block stays unread");
        }

        [Test]
        static void RefusesOtherModels()
        {
            var radio = new FakeRadio(new MemoryImage()) { Model = "D878UV" };
            try
            {
                RadioReader.Read(new AnytoneLink(radio, radio.DiscardInput));
                throw new Exception("should have refused a D878UV");
            }
            catch (RadioProtocolException ex)
            {
                Assert.True(ex.Message.Contains("D878UV"), "message names the model");
            }
            Assert.Equal(0, radio.Reads, "nothing read");
            Assert.Equal(1, radio.Ends, "left programming mode");
        }

        [Test]
        static void SilentRadioTimesOut()
        {
            var silent = new MemoryStream();
            try
            {
                new AnytoneLink(silent).Open();
                throw new Exception("should time out");
            }
            catch (TimeoutException ex)
            {
                Assert.True(ex.Message.Contains("PROGRAM"), "says what it waited for: " + ex.Message);
            }
        }

        /// <summary>
        /// Simulated radio holding the user's codeplug: read it over the protocol (with a few garbled replies),
        /// decode, write CSVs, and get the user's export back in every decoded column.
        /// </summary>
        [Test]
        static void ReadDecodeReproducesExport()
        {
            string export = Fixtures.RequireExport();
            var radio = new FakeRadio(FakeCodeplug.FromExport(export)) { CorruptReplies = 2 }; // the first block needs a third try
            var img = RadioReader.Read(new AnytoneLink(radio, radio.DiscardInput));
            Assert.Equal(1, radio.Ends, "left programming mode");
            Assert.True(radio.Reads < 7000, "read plan stays bounded for a small codeplug (" + radio.Reads + " reads)");

            // Save and reload, as --radio-decode would.
            var ms = new MemoryStream();
            img.Save(ms);
            ms.Position = 0;
            var cp = RadioCodeplug.Decode(MemoryImage.Load(ms));
            Assert.Equal(0, cp.Warnings.Count, "decode warnings: " + string.Join("; ", cp.Warnings));
            Assert.Equal("WELCOME", cp.BootLine1, "boot line 1");

            var format = CpsFormat.BuiltIn();
            var tables = RadioCsv.ToTables(cp, format);
            foreach (string file in new[] { CpsFormat.ChannelFile, CpsFormat.ZoneFile, CpsFormat.TalkGroupsFile, CpsFormat.RadioIdFile, CpsFormat.RxGroupFile })
            {
                var expected = CsvTable.Load(CpsFormat.FindFile(export, file));
                var diffs = RadioCsv.CompareTables(file, expected, tables[file]);
                Assert.True(diffs.Count == 0, file + " differs:\n" + string.Join("\n", diffs.Take(10)));
                Assert.Equal(expected.ToCsv(), tables[file].ToCsv(), file + " byte for byte");
            }

            // The folder imports like a CPS export.
            string dir = Path.Combine(Path.GetTempPath(), "cpb-radio-" + Guid.NewGuid().ToString("N"));
            try
            {
                RadioCsv.WriteTo(tables, dir);
                var fromRadio = CpsImporter.Import(dir).Project;
                var fromExport = CpsImporter.Import(export).Project;
                Assert.Equal(ProjectStore.ToJson(fromExport), ProjectStore.ToJson(fromRadio), "same project from the radio as from the export");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
