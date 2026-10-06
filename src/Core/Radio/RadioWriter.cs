using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CodeplugBuilder.Core.Radio
{
    public sealed class WriteResult
    {
        /// <summary>Blocks written (and read back correctly).</summary>
        public List<uint> Blocks = new List<uint>();
        public List<string> Log = new List<string>();
    }

    /// <summary>
    /// Writes an edited image back to the radio, safely:
    /// 1. sends what the CPS sends on a full write (Dmr6x2Pro.WriteSet), from a full read plus edits, in address
    ///    order, in one session: the radio erases what a session writes over and keeps only what it sends;
    /// 2. first re-reads every one of those blocks and stops if the radio no longer holds what was read;
    /// 3. writes, and ends the session; <see cref="Verify"/> then reads the blocks back in a new session.
    /// </summary>
    public static class RadioWriter
    {
        /// <summary>Blocks where <paramref name="edited"/> differs from <paramref name="original"/>, in address order.</summary>
        public static List<uint> ChangedBlocks(MemoryImage original, MemoryImage edited)
        {
            var result = new List<uint>();
            HashSet<uint> elements = null;
            foreach (var run in edited.Runs())
            {
                for (int i = 0; i < run.Value.Length; i += MemoryImage.BlockSize)
                {
                    uint addr = run.Key + (uint)i;
                    if (!original.HasBlock(addr))
                    {
                        // A new channel, zone, contact... (RadioEncoder) lands where nothing was read. Allowed only inside
                        // the records the edited image's own bitmaps say exist; anything else is refused.
                        elements = elements ?? ElementBlocks(edited);
                        if (!elements.Contains(addr))
                            throw new InvalidOperationException("The edited image has data at 0x" + addr.ToString("X7") + " that was never read from the radio; refusing to write it.");
                        result.Add(addr);
                        continue;
                    }
                    byte[] a = original.Get(addr, MemoryImage.BlockSize);
                    for (int j = 0; j < MemoryImage.BlockSize; j++)
                        if (a[j] != run.Value[i + j]) { result.Add(addr); break; }
                }
            }
            return result;
        }

        /// <summary>
        /// The blocks to send: every changed block widened to the whole area it belongs to (a settings block,
        /// a channel...), from its start. The radio ignored a lone block written in the middle of the extended
        /// settings (2026-10-06), so writes follow the CPS's pattern of whole areas.
        /// </summary>
        public static List<uint> BlocksToWrite(MemoryImage original, MemoryImage edited)
        {
            ChangedBlocks(original, edited); // refuses data that was never read
            var blocks = Dmr6x2Pro.WriteSet(edited);
            var elements = ElementBlocks(edited);
            var missing = blocks.Where(b => !edited.HasBlock(b) || (!original.HasBlock(b) && !elements.Contains(b))).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("The image doesn't hold everything a write must send (" + missing.Count + " blocks, first 0x" + missing[0].ToString("X7")
                    + "). Read the radio again with this version of the program.");
            return blocks;
        }

        static HashSet<uint> ElementBlocks(MemoryImage img)
        {
            var set = new HashSet<uint>();
            foreach (var r in Dmr6x2Pro.ElementRanges(img))
                for (uint a = r.Address; a < r.Address + (uint)r.Length; a += MemoryImage.BlockSize)
                    set.Add(a);
            return set;
        }

        /// <summary>Reads <paramref name="blocks"/> in a new session; returns the ones that don't hold <paramref name="edited"/>'s data.</summary>
        public static List<uint> Verify(AnytoneLink link, MemoryImage edited, IEnumerable<uint> blocks)
        {
            var bad = new List<uint>();
            try
            {
                link.Open();
                foreach (uint b in blocks)
                    if (!Same(link.ReadBlock(b), edited.Get(b, MemoryImage.BlockSize))) bad.Add(b);
            }
            finally
            {
                link.Close();
            }
            return bad;
        }

        /// <summary>
        /// Off until writes cover whole flash sectors. On 2026-10-06 a 48-byte write at 0x2501400 made the radio erase
        /// its whole sector (general settings, boot password, APRS...) and keep only those 48 bytes: the radio came up
        /// asking for a power-on password in Chinese, and was restored with a full CPS write. Tests turn this on.
        /// </summary>
        public static bool Enabled;

        public static WriteResult Write(AnytoneLink link, MemoryImage original, MemoryImage edited, IProgress<ReadProgress> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            if (!Enabled)
                throw new InvalidOperationException("Writing to the radio is switched off: partial writes erase whole flash sectors on the DMR-6X2 PRO. Use the BTECH CPS to write.");
            var result = new WriteResult();
            if (ChangedBlocks(original, edited).Count == 0)
            {
                result.Log.Add("Nothing to write: the edited image is the same as the one read from the radio.");
                return result;
            }
            var blocks = BlocksToWrite(original, edited);
            try
            {
                var id = link.Open();
                if (id.Model != original.Model)
                    throw new RadioProtocolException("This radio is a \"" + id.Model + "\" but the image was read from a \"" + original.Model + "\". Nothing was written.");
                result.Log.Add("Radio: " + id);

                int total = blocks.Count * 2, done = 0;
                void Step(string what)
                {
                    done++;
                    progress?.Report(new ReadProgress { Done = done, Total = total, What = what });
                }

                // 1. The radio must still hold exactly what was read (blocks of new records weren't read: nothing to compare).
                foreach (uint b in blocks)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (original.HasBlock(b) && !Same(link.ReadBlock(b), original.Get(b, MemoryImage.BlockSize)))
                        throw new RadioProtocolException("The radio's memory at 0x" + b.ToString("X7") + " changed since it was read (programmed with the CPS or changed on the radio?). Nothing was written. Read the radio again and redo the changes.");
                    Step("checking");
                }
                result.Log.Add("Checked " + blocks.Count + " block(s): the radio still matches the read.");

                // 2. Write. No cancelling halfway.
                foreach (uint b in blocks)
                {
                    link.WriteBlock(b, edited.Get(b, MemoryImage.BlockSize));
                    Step("writing");
                }

                // 3. No reading back in this session: on the 6X2 PRO a read after a write in the same session
                //    loses the write (twice on 2026-10-06; qdmr never does it). The caller verifies in a new session
                //    after END, once the radio is back on USB (see Verify).
                result.Blocks.AddRange(blocks);
                result.Log.Add("Wrote " + blocks.Count + " blocks (0x" + blocks[0].ToString("X7") + " to 0x" + blocks[blocks.Count - 1].ToString("X7") + "), " + ChangedBlocks(original, edited).Count + " of them changed.");
                return result;
            }
            finally
            {
                link.Close();
            }
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
