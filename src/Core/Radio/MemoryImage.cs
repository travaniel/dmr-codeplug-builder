using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeplugBuilder.Core.Radio
{
    /// <summary>
    /// The parts of the radio's memory that were read, kept as 16-byte blocks (the unit the radio
    /// reads and writes). Addresses are the radio's own. Saved as a small binary file so a read can
    /// be decoded again later and serves as a backup before any write.
    /// </summary>
    public sealed class MemoryImage
    {
        public const int BlockSize = 16;
        const string Magic = "CPBIMG01";

        readonly Dictionary<uint, byte[]> blocks = new Dictionary<uint, byte[]>();

        /// <summary>Model name the radio reported ("D6X2UV2" for the DMR-6X2 PRO).</summary>
        public string Model = "";
        /// <summary>Version string the radio reported ("V100").</summary>
        public string Version = "";
        /// <summary>Frequency band code the radio reported.</summary>
        public byte Bands;
        public DateTime ReadAtUtc = DateTime.UtcNow;

        public int BlockCount => blocks.Count;

        public static uint AlignDown(uint addr) => addr & ~(uint)(BlockSize - 1);

        public bool HasBlock(uint blockAddr) => blocks.ContainsKey(blockAddr);

        public void PutBlock(uint blockAddr, byte[] data)
        {
            if ((blockAddr & (BlockSize - 1)) != 0) throw new ArgumentException("Block address must be 16-byte aligned.");
            if (data == null || data.Length != BlockSize) throw new ArgumentException("A block is 16 bytes.");
            blocks[blockAddr] = (byte[])data.Clone();
        }

        /// <summary>True if every byte of [addr, addr+length) was read.</summary>
        public bool Has(uint addr, int length)
        {
            if (length <= 0) return true;
            for (uint b = AlignDown(addr); b < addr + (uint)length; b += BlockSize)
                if (!blocks.ContainsKey(b)) return false;
            return true;
        }

        public byte[] Get(uint addr, int length)
        {
            var result = new byte[length];
            for (int i = 0; i < length;)
            {
                uint a = addr + (uint)i;
                uint b = AlignDown(a);
                if (!blocks.TryGetValue(b, out byte[] block))
                    throw new InvalidDataException("Address 0x" + a.ToString("X7") + " wasn't read from the radio.");
                int off = (int)(a - b);
                int n = Math.Min(BlockSize - off, length - i);
                Buffer.BlockCopy(block, off, result, i, n);
                i += n;
            }
            return result;
        }

        /// <summary>Writes bytes into blocks that were already read (used by tests and, later, edits).</summary>
        public void Set(uint addr, byte[] data)
        {
            for (int i = 0; i < data.Length;)
            {
                uint a = addr + (uint)i;
                uint b = AlignDown(a);
                if (!blocks.TryGetValue(b, out byte[] block))
                {
                    block = new byte[BlockSize];
                    blocks[b] = block;
                }
                int off = (int)(a - b);
                int n = Math.Min(BlockSize - off, data.Length - i);
                Buffer.BlockCopy(data, i, block, off, n);
                i += n;
            }
        }

        public MemoryImage Clone()
        {
            var c = new MemoryImage { Model = Model, Version = Version, Bands = Bands, ReadAtUtc = ReadAtUtc };
            foreach (var kv in blocks) c.blocks[kv.Key] = (byte[])kv.Value.Clone();
            return c;
        }

        public byte U8(uint addr) => Get(addr, 1)[0];

        /// <summary>Bit <paramref name="index"/> of a bitmap at <paramref name="addr"/>, least significant bit first.</summary>
        public bool Bit(uint addr, int index) => (U8(addr + (uint)(index / 8)) >> (index % 8) & 1) != 0;

        /// <summary>Sorted runs of consecutive blocks.</summary>
        public IEnumerable<KeyValuePair<uint, byte[]>> Runs()
        {
            uint start = 0;
            var run = new List<byte>();
            uint next = 0;
            foreach (uint b in blocks.Keys.OrderBy(k => k))
            {
                if (run.Count > 0 && b != next)
                {
                    yield return new KeyValuePair<uint, byte[]>(start, run.ToArray());
                    run.Clear();
                }
                if (run.Count == 0) start = b;
                run.AddRange(blocks[b]);
                next = b + BlockSize;
            }
            if (run.Count > 0) yield return new KeyValuePair<uint, byte[]>(start, run.ToArray());
        }

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
                Save(fs);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public void Save(Stream stream)
        {
            var runs = Runs().ToList();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes(Magic));
                w.Write(Model ?? "");
                w.Write(Version ?? "");
                w.Write(Bands);
                w.Write(ReadAtUtc.ToString("o", CultureInfo.InvariantCulture));
                w.Write(runs.Count);
                foreach (var r in runs)
                {
                    w.Write(r.Key);
                    w.Write(r.Value.Length);
                    w.Write(r.Value);
                }
            }
        }

        public static MemoryImage Load(string path)
        {
            using (var fs = File.OpenRead(path))
                return Load(fs);
        }

        public static MemoryImage Load(Stream stream)
        {
            using (var r = new BinaryReader(stream, Encoding.UTF8, true))
            {
                string magic = Encoding.ASCII.GetString(r.ReadBytes(Magic.Length));
                if (magic != Magic) throw new InvalidDataException("Not a radio memory image saved by this program.");
                var img = new MemoryImage
                {
                    Model = r.ReadString(),
                    Version = r.ReadString(),
                    Bands = r.ReadByte(),
                };
                DateTime.TryParse(r.ReadString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out img.ReadAtUtc);
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    uint addr = r.ReadUInt32();
                    int len = r.ReadInt32();
                    if ((addr & (BlockSize - 1)) != 0 || len < 0 || len % BlockSize != 0)
                        throw new InvalidDataException("Damaged memory image (run " + i + ").");
                    byte[] data = r.ReadBytes(len);
                    if (data.Length != len) throw new InvalidDataException("Memory image is cut short.");
                    for (int j = 0; j < len; j += BlockSize)
                    {
                        var block = new byte[BlockSize];
                        Buffer.BlockCopy(data, j, block, 0, BlockSize);
                        img.blocks[addr + (uint)j] = block;
                    }
                }
                return img;
            }
        }
    }
}
