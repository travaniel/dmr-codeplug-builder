using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CodeplugBuilder.Core.Radio
{
    /// <summary>A stretch of radio memory to read, with what it holds (for progress and logs).</summary>
    public sealed class MemoryRange
    {
        public uint Address;
        public int Length;
        public string What;

        public MemoryRange(uint address, int length, string what)
        {
            // Widen to whole 16-byte blocks.
            uint start = MemoryImage.AlignDown(address);
            uint end = (address + (uint)length + MemoryImage.BlockSize - 1) & ~(uint)(MemoryImage.BlockSize - 1);
            Address = start;
            Length = (int)(end - start);
            What = what;
        }

        public override string ToString() => "0x" + Address.ToString("X7") + " +" + Length + " " + What;
    }

    /// <summary>
    /// Memory map of the BTECH DMR-6X2 PRO (model "D6X2UV2", firmware 1.21), from the codeplug description
    /// for firmware 1.21b at dmr-tools.github.io/codeplugs. The 6X2 PRO keeps the AnyTone D868UV layout
    /// with extensions. Addresses are absolute; strides are between consecutive elements.
    /// </summary>
    public static class Dmr6x2Pro
    {
        public const string ModelName = "D6X2UV2";

        // Channels: 32 banks of 128, 0x40 bytes each; each bank has an extension bank 0x2000 after it.
        public const uint ChannelBanks = 0x00800000, ChannelBankStride = 0x00040000, ChannelExtension = 0x2000;
        public const int ChannelsPerBank = 128, MaxChannels = 4000, ChannelSize = 0x40;
        public const uint ChannelBitmap = 0x024C1500;   // 4000 bits, set = channel exists
        public const uint VfoA = 0x00FC0800, VfoB = 0x00FC0840, VfoExtension = 0x00FC2800;

        // Zones: member list (250 × uint16 channel index, FFFF unused), name, A/B channel.
        public const uint ZoneChannels = 0x01000000, ZoneChannelsStride = 0x200;
        public const uint ZoneNames = 0x02540000, ZoneNameStride = 0x20;
        // A and B channel per zone: two separate lists of 250 × uint16, each the channel's position within
        // the zone's member list (checked against the CPS on the user's radio, 2026-10-06).
        public const uint ZoneA = 0x02500100, ZoneB = 0x02500300;
        public const uint ZoneBitmap = 0x024C1300, HiddenZoneBitmap = 0x024C1360;
        public const int MaxZones = 250, MaxZoneMembers = 250;

        // Scan lists: 16 banks of 16, 0x90 bytes each, 0x200 apart.
        public const uint ScanListBanks = 0x01080000, ScanListBankStride = 0x00040000, ScanListStride = 0x200;
        public const int ScanListsPerBank = 16, MaxScanLists = 250, ScanListSize = 0x90;
        public const uint ScanListBitmap = 0x024C1340;

        // Radio IDs (own DMR IDs).
        public const uint RadioIds = 0x02580000, RadioIdStride = 0x20;
        public const uint RadioIdBitmap = 0x024C1320;
        public const int MaxRadioIds = 250;

        // Digital contacts (talkgroups and private calls): banks of 1000, 0x64 bytes each.
        public const uint ContactBanks = 0x02680000, ContactBankStride = 0x00040000;
        public const int ContactsPerBank = 1000, MaxContacts = 10000, ContactSize = 0x64;
        public const uint ContactBitmap = 0x02640000;    // 10000 bits, CLEAR = contact exists
        public const int ContactBitmapSize = 0x4E2;
        public const uint ContactIndexList = 0x02600000; // uint32 slot per list position

        // RX group lists: 64 × uint32 contact index (FFFFFFFF unused) + name at 0x100.
        public const uint GroupLists = 0x02980000, GroupListStride = 0x200;
        public const int GroupListSize = 0x120, MaxGroupLists = 250, MaxGroupListMembers = 64;
        public const uint GroupListBitmap = 0x025C0B10;

        // Analog (DTMF) contacts: byte map, 0 = exists.
        public const uint DtmfContactIndex = 0x02900000, DtmfContactBytemap = 0x02900100;
        public const uint DtmfContacts = 0x02940000;
        public const int DtmfContactSize = 0x18, MaxDtmfContacts = 128;

        // Roaming.
        public const uint RoamingChannels = 0x01040000, RoamingChannelBitmap = 0x01042000;
        public const uint RoamingZoneBitmap = 0x01042080, RoamingZones = 0x01043000;
        public const int RoamingChannelSize = 0x20, MaxRoamingChannels = 250, RoamingZoneSize = 0x80, MaxRoamingZones = 64;

        // DTMF / 5-tone functions (bitmaps at 0x024C2610 / 0x024C2630).
        public const uint DtmfFunctions = 0x024C3000, DtmfFunctionBitmap = 0x024C2610;
        public const uint FiveToneFunctions = 0x024C5000, FiveToneFunctionBitmap = 0x024C2630;
        public const int FunctionSize = 0x20, MaxFunctions = 250;

        // Settings.
        public const uint GeneralSettings = 0x02500000;  // 0xE0 bytes
        public const int GeneralSettingsSize = 0xE0;
        public const uint BootSettings = 0x02500600;     // intro line 1 (16), line 2 (16), password (8)
        public const uint ExtendedSettings = 0x02501400; // 0x30 bytes
        public const uint AprsSettings = 0x02501000;

        /// <summary>Address of channel <paramref name="index"/> (0-based; CPS channel number - 1).</summary>
        public static uint ChannelAddress(int index) =>
            ChannelBanks + (uint)(index / ChannelsPerBank) * ChannelBankStride + (uint)(index % ChannelsPerBank) * ChannelSize;

        public static uint ContactAddress(int index) =>
            ContactBanks + (uint)(index / ContactsPerBank) * ContactBankStride + (uint)(index % ContactsPerBank) * ContactSize;

        public static uint ScanListAddress(int index) =>
            ScanListBanks + (uint)(index / ScanListsPerBank) * ScanListBankStride + (uint)(index % ScanListsPerBank) * ScanListStride;

        /// <summary>
        /// Everything at a fixed place: the bitmaps that say which channels, zones, contacts... exist,
        /// and the settings blocks. Read first; <see cref="ElementRanges"/> follows from it.
        /// </summary>
        public static List<MemoryRange> FixedRanges()
        {
            return new List<MemoryRange>
            {
                new MemoryRange(ChannelBitmap, 0x200, "channel bitmap"),
                new MemoryRange(ZoneBitmap, 0x80, "zone, radio ID, scan list and hidden zone bitmaps"),
                new MemoryRange(0x025C0B00, 0x30, "status message and RX group list bitmaps"),
                new MemoryRange(ContactBitmap, ContactBitmapSize, "contact bitmap"),
                new MemoryRange(DtmfContactIndex, 0x180, "analog contact index and byte map"),
                new MemoryRange(RoamingChannelBitmap, 0x90, "roaming channel and zone bitmaps"),
                new MemoryRange(VfoA, 0x80, "VFO A/B"),
                new MemoryRange(VfoExtension, 0x80, "VFO A/B extensions"),
                new MemoryRange(GeneralSettings, 0x630, "general settings, zone A/B channels, DTMF IDs, boot text"),
                new MemoryRange(AprsSettings, 0x100, "APRS settings"),
                new MemoryRange(0x02501200, 0xB0, "APRS messages"),
                new MemoryRange(ExtendedSettings, 0x30, "extended settings"),
                new MemoryRange(0x02501800, 0x100, "APRS receive filters"),
                new MemoryRange(0x02504000, 0x400, "GPS roaming"),
                new MemoryRange(0x024C0000, 0x12A0, "5-tone, DTMF and 2-tone settings"),
                new MemoryRange(0x024C1400, 0x70, "alarm settings"),
                new MemoryRange(0x024C2000, 0x650, "repeater offsets, 2-tone functions, function bitmaps"),
                new MemoryRange(0x025C0000, 0x860, "FM quick call, status messages, hot keys"),
                new MemoryRange(0x02480000, 0x230, "FM broadcast"),
                // Everything below is also written by the CPS on every write (seen in a USB capture, 2026-10-06),
                // so it has to be read: the radio erases what it is written over and keeps only what one session sends.
                new MemoryRange(0x024C1700, 0x600, "DMR and enhanced encryption keys"),
                new MemoryRange(0x025C1000, 0x4FF0, "AES and ARC4 keys"),
                new MemoryRange(ContactIndexList, 4 * MaxContacts, "contact order list"),
                new MemoryRange(MessageIndex, 0x890, "SMS index and byte map"),
            };
        }

        /// <summary>
        /// Fixed areas the CPS 1.22e writes on every "Write to radio" (from a USB capture of a full write, 2026-10-06).
        /// Together with <see cref="ElementRanges"/> and any other block holding data, this is the CPS's write set.
        /// </summary>
        public static List<MemoryRange> CpsFixedWriteRanges()
        {
            (uint a, int n)[] r =
            {
                (0x00FC0800, 0x80), (0x00FC2800, 0x80), (0x01042000, 0x20), (0x01042080, 0x10), (0x01640800, 0x90),
                (0x02480200, 0x30), (0x024C0C80, 0x10), (0x024C0D00, 0x200), (0x024C1000, 0xD0), (0x024C1280, 0x20),
                (0x024C1300, 0x80), (0x024C1400, 0x20), (0x024C1440, 0x30), (0x024C1500, 0x240), (0x024C1800, 0x500),
                (0x024C2000, 0x3F0), (0x024C2600, 0x50), (0x02500000, 0xE0), (0x02500100, 0x530), (0x02501000, 0x100),
                (0x02501200, 0x40), (0x02501280, 0x30), (0x02501400, 0x30), (0x02501800, 0x100), (0x02504000, 0x400),
                (0x025C0000, 0x860), (0x025C0B00, 0x30), (0x025C1000, 0x4FF0), (ContactIndexList, 4 * MaxContacts),
                (ContactBitmap, 0x4F0), (0x02900000, 0x80), (0x02900100, 0x80),
            };
            return r.Select(x => new MemoryRange(x.a, x.n, "CPS write area")).ToList();
        }

        /// <summary>
        /// Blocks a write must send, in address order: the CPS's fixed areas, every element that exists, and any other
        /// read block that isn't erased (all FF). The radio erases what a session writes over and keeps only what that
        /// session sends, so a write always sends all of this, never just the changed blocks.
        /// </summary>
        public static List<uint> WriteSet(MemoryImage img)
        {
            var set = new SortedSet<uint>();
            foreach (var r in CpsFixedWriteRanges().Concat(ElementRanges(img)))
                for (uint a = r.Address; a < r.Address + (uint)r.Length; a += MemoryImage.BlockSize)
                    set.Add(a);
            foreach (var run in img.Runs())
                for (int i = 0; i < run.Value.Length; i += MemoryImage.BlockSize)
                {
                    bool erased = true;
                    for (int j = 0; j < MemoryImage.BlockSize; j++) if (run.Value[i + j] != 0xFF) { erased = false; break; }
                    if (!erased) set.Add(run.Key + (uint)i);
                }
            return set.ToList();
        }

        // Prepared SMS messages: 12 banks of 8, 0xD0 bytes each, 0x100 apart; byte map at 0x1640800 (FF = free).
        public const uint MessageIndex = 0x01640000, MessageBytemap = 0x01640800, MessageBanks = 0x02140000;
        public const int MaxMessages = 100, MessageSize = 0xD0;

        public static uint MessageAddress(int index) => MessageBanks + (uint)(index / 8) * 0x40000 + (uint)(index % 8) * 0x100;

        /// <summary>Contact ID map (sorted ID → slot), 8 bytes per contact plus one, written by the CPS.</summary>
        public const uint ContactMap = 0x04800000;

        /// <summary>The channels, zones, contacts... that the bitmaps in <paramref name="img"/> say exist.</summary>
        public static List<MemoryRange> ElementRanges(MemoryImage img)
        {
            var r = new List<MemoryRange>();
            foreach (int i in Enabled(img, ChannelBitmap, MaxChannels))
            {
                uint a = ChannelAddress(i);
                r.Add(new MemoryRange(a, ChannelSize, "channel " + (i + 1)));
                r.Add(new MemoryRange(a + ChannelExtension, ChannelSize, "channel " + (i + 1) + " extension"));
            }
            foreach (int i in Enabled(img, ZoneBitmap, MaxZones))
            {
                r.Add(new MemoryRange(ZoneChannels + (uint)i * ZoneChannelsStride, MaxZoneMembers * 2, "zone " + (i + 1) + " members"));
                r.Add(new MemoryRange(ZoneNames + (uint)i * ZoneNameStride, (int)ZoneNameStride, "zone " + (i + 1) + " name"));
            }
            // The CPS writes 0xC0 per scan list and 0x130 per RX group list (more than the documented elements).
            foreach (int i in Enabled(img, ScanListBitmap, MaxScanLists))
                r.Add(new MemoryRange(ScanListAddress(i), 0xC0, "scan list " + (i + 1)));
            foreach (int i in Enabled(img, RadioIdBitmap, MaxRadioIds))
                r.Add(new MemoryRange(RadioIds + (uint)i * RadioIdStride, (int)RadioIdStride, "radio ID " + (i + 1)));
            foreach (int i in Enabled(img, GroupListBitmap, MaxGroupLists))
                r.Add(new MemoryRange(GroupLists + (uint)i * GroupListStride, 0x130, "RX group list " + (i + 1)));

            var contacts = ExistingContacts(img).ToList();
            foreach (int i in contacts)
                r.Add(new MemoryRange(ContactAddress(i), ContactSize, "contact " + (i + 1)));
            r.Add(new MemoryRange(ContactMap, 8 * (contacts.Count + 1), "contact ID map"));

            if (img.Has(MessageBytemap, MaxMessages))
                for (int i = 0; i < MaxMessages; i++)
                    if (img.U8(MessageBytemap + (uint)i) != 0xFF)
                        r.Add(new MemoryRange(MessageAddress(i), MessageSize, "SMS " + (i + 1)));

            foreach (int i in ExistingDtmfContacts(img))
                r.Add(new MemoryRange(DtmfContacts + (uint)(i * DtmfContactSize), DtmfContactSize, "analog contact " + (i + 1)));
            foreach (int i in Enabled(img, RoamingChannelBitmap, MaxRoamingChannels))
                r.Add(new MemoryRange(RoamingChannels + (uint)(i * RoamingChannelSize), RoamingChannelSize, "roaming channel " + (i + 1)));
            foreach (int i in Enabled(img, RoamingZoneBitmap, MaxRoamingZones))
                r.Add(new MemoryRange(RoamingZones + (uint)(i * RoamingZoneSize), RoamingZoneSize, "roaming zone " + (i + 1)));
            foreach (int i in Enabled(img, DtmfFunctionBitmap, MaxFunctions))
                r.Add(new MemoryRange(DtmfFunctions + (uint)(i * FunctionSize), FunctionSize, "DTMF function " + (i + 1)));
            foreach (int i in Enabled(img, FiveToneFunctionBitmap, MaxFunctions))
                r.Add(new MemoryRange(FiveToneFunctions + (uint)(i * FunctionSize), FunctionSize, "5-tone function " + (i + 1)));
            return r;
        }

        /// <summary>Indexes whose bit is set in a bitmap (bit set = in use).</summary>
        public static IEnumerable<int> Enabled(MemoryImage img, uint bitmap, int count)
        {
            for (int i = 0; i < count; i++)
                if (img.Bit(bitmap, i)) yield return i;
        }

        /// <summary>Contact slots in use: the contact bitmap is inverted (a clear bit means the slot is used).</summary>
        public static IEnumerable<int> ExistingContacts(MemoryImage img)
        {
            for (int i = 0; i < MaxContacts; i++)
                if (!img.Bit(ContactBitmap, i)) yield return i;
        }

        /// <summary>Analog contacts in use: one byte each, 0 = used, FF = free.</summary>
        public static IEnumerable<int> ExistingDtmfContacts(MemoryImage img)
        {
            for (int i = 0; i < MaxDtmfContacts; i++)
                if (img.U8(DtmfContactBytemap + (uint)i) == 0) yield return i;
        }
    }

    public sealed class ReadProgress
    {
        public int Done, Total;
        public string What;
        public override string ToString() => Done + "/" + Total + " " + What;
    }

    /// <summary>Reads the codeplug from a DMR-6X2 PRO into a <see cref="MemoryImage"/>.</summary>
    public static class RadioReader
    {
        /// <param name="allowOtherModel">Read even if the radio isn't a DMR-6X2 PRO (the layout may not fit).</param>
        public static MemoryImage Read(AnytoneLink link, IProgress<ReadProgress> progress = null, CancellationToken cancel = default(CancellationToken), bool allowOtherModel = false)
        {
            try
            {
                var id = link.Open();
                if (id.Model != Dmr6x2Pro.ModelName && !allowOtherModel)
                    throw new RadioProtocolException("This radio reports itself as \"" + id.Model + "\", not a BTECH DMR-6X2 PRO (\"" + Dmr6x2Pro.ModelName + "\"). Nothing was read.");
                var img = new MemoryImage { Model = id.Model, Version = id.Version, Bands = id.Bands, ReadAtUtc = DateTime.UtcNow };

                var fixedRanges = Dmr6x2Pro.FixedRanges();
                int fixedBlocks = fixedRanges.Sum(r => r.Length / MemoryImage.BlockSize);
                var state = new ReadProgress { Total = fixedBlocks };
                ReadRanges(link, img, fixedRanges, state, progress, cancel);

                var elementRanges = Dmr6x2Pro.ElementRanges(img);
                state.Total = state.Done + CountMissing(img, elementRanges);
                ReadRanges(link, img, elementRanges, state, progress, cancel);
                return img;
            }
            finally
            {
                link.Close();
            }
        }

        static int CountMissing(MemoryImage img, IEnumerable<MemoryRange> ranges)
        {
            var blocks = new HashSet<uint>();
            foreach (var r in ranges)
                for (uint a = r.Address; a < r.Address + (uint)r.Length; a += MemoryImage.BlockSize)
                    if (!img.HasBlock(a)) blocks.Add(a);
            return blocks.Count;
        }

        static void ReadRanges(AnytoneLink link, MemoryImage img, IEnumerable<MemoryRange> ranges, ReadProgress state, IProgress<ReadProgress> progress, CancellationToken cancel)
        {
            foreach (var r in ranges)
            {
                for (uint a = r.Address; a < r.Address + (uint)r.Length; a += MemoryImage.BlockSize)
                {
                    if (img.HasBlock(a)) continue;
                    cancel.ThrowIfCancellationRequested();
                    img.PutBlock(a, link.ReadBlock(a));
                    state.Done++;
                    if (progress != null && (state.Done % 16 == 0 || state.Done == state.Total))
                        progress.Report(new ReadProgress { Done = state.Done, Total = state.Total, What = r.What });
                }
            }
        }
    }
}
