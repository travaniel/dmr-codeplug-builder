using System;
using System.IO;
using System.Text;

namespace CodeplugBuilder.Core.Radio
{
    public sealed class RadioIdentity
    {
        /// <summary>Model name, "D6X2UV2" for the BTECH DMR-6X2 PRO.</summary>
        public string Model = "";
        /// <summary>Band code (which TX ranges the radio allows).</summary>
        public byte Bands;
        /// <summary>Hardware version, "V100".</summary>
        public string Version = "";

        public override string ToString() => Model + " " + Version + " (band code " + Bands + ")";
    }

    public sealed class RadioProtocolException : IOException
    {
        public RadioProtocolException(string message) : base(message) { }
    }

    /// <summary>
    /// The USB serial protocol of the AnyTone family (AT-D868/878, BTECH DMR-6X2 and 6X2 PRO), written from
    /// the public descriptions at dmr-tools.github.io and qdmr's documentation:
    ///   "PROGRAM" → "QX" 06              enter programming mode
    ///   02 → 'I' model[7] bands version[6] 06    identify
    ///   'R' addr(4, big-endian) 10 → 'W' addr 10 data[16] sum 06    read 16 bytes
    ///   'W' addr(4, big-endian) 10 data[16] sum 06 → 06    write 16 bytes
    ///   "END" → 06                       leave programming mode
    /// The checksum is the byte sum of address, length and data. Writes go through <see cref="RadioWriter"/>,
    /// which checks the radio first and reads every block back.
    /// Works on any <see cref="Stream"/> (a serial port in the app, a simulated radio in the tests);
    /// the stream's read timeout should be about a second.
    /// </summary>
    public sealed class AnytoneLink
    {
        readonly Stream stream;
        readonly Action discardInput;
        bool inProgramMode;

        /// <summary>Read attempts per block before giving up (a garbled reply is retried).</summary>
        public int Attempts = 3;

        /// <param name="discardInput">Throws away anything waiting in the receive buffer (SerialPort.DiscardInBuffer); used before a retry.</param>
        public AnytoneLink(Stream stream, Action discardInput = null)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.discardInput = discardInput;
        }

        public bool InProgramMode => inProgramMode;

        /// <summary>Puts the radio in programming mode and asks what it is.</summary>
        public RadioIdentity Open()
        {
            Send(Encoding.ASCII.GetBytes("PROGRAM"));
            byte[] ack = Receive(3, "the reply to PROGRAM");
            if (ack[0] != (byte)'Q' || ack[1] != (byte)'X' || ack[2] != 0x06)
                throw new RadioProtocolException("The radio didn't accept programming mode (got " + Hex(ack) + "). Is it switched on and is this the right COM port?");
            inProgramMode = true;

            Send(new byte[] { 0x02 });
            byte[] id = Receive(16, "the identification reply");
            if (id[0] != (byte)'I' || id[15] != 0x06)
                throw new RadioProtocolException("Unexpected identification reply " + Hex(id) + ".");
            return new RadioIdentity
            {
                Model = AsciiZ(id, 1, 7),
                Bands = id[8],
                Version = AsciiZ(id, 9, 6),
            };
        }

        /// <summary>Reads the 16 bytes at <paramref name="addr"/> (must be 16-byte aligned).</summary>
        public byte[] ReadBlock(uint addr)
        {
            if (!inProgramMode) throw new InvalidOperationException("Call Open() first.");
            if ((addr & 0xF) != 0) throw new ArgumentException("Address must be 16-byte aligned.");
            var req = new byte[6];
            req[0] = (byte)'R';
            PutBigEndian(req, 1, addr);
            req[5] = 0x10;

            Exception last = null;
            for (int attempt = 0; attempt < Math.Max(1, Attempts); attempt++)
            {
                try
                {
                    if (attempt > 0) discardInput?.Invoke();
                    Send(req);
                    byte[] resp = Receive(24, "the reply for address 0x" + addr.ToString("X7"));
                    string problem = CheckReadResponse(resp, addr);
                    if (problem != null) throw new RadioProtocolException(problem);
                    var data = new byte[16];
                    Buffer.BlockCopy(resp, 6, data, 0, 16);
                    return data;
                }
                catch (RadioProtocolException ex) { last = ex; }
                catch (TimeoutException ex) { last = ex; }
            }
            throw new RadioProtocolException("Reading address 0x" + addr.ToString("X7") + " failed: " + last.Message);
        }

        /// <summary>
        /// Writes 16 bytes at <paramref name="addr"/>: 'W' addr(4 BE) 10 data[16] sum 06, answered by 06.
        /// Not retried: the caller reads the block back to check it.
        /// </summary>
        public void WriteBlock(uint addr, byte[] data)
        {
            if (!inProgramMode) throw new InvalidOperationException("Call Open() first.");
            if ((addr & 0xF) != 0) throw new ArgumentException("Address must be 16-byte aligned.");
            if (data == null || data.Length != 16) throw new ArgumentException("A block is 16 bytes.");
            var req = new byte[24];
            req[0] = (byte)'W';
            PutBigEndian(req, 1, addr);
            req[5] = 0x10;
            Buffer.BlockCopy(data, 0, req, 6, 16);
            req[22] = Checksum(req, 1, 21);
            req[23] = 0x06;
            Send(req);
            byte[] ack = Receive(1, "the reply to a write at 0x" + addr.ToString("X7"));
            if (ack[0] != 0x06)
                throw new RadioProtocolException("The radio refused the write at 0x" + addr.ToString("X7") + " (answered 0x" + ack[0].ToString("X2") + ").");
        }

        /// <summary>Null if <paramref name="resp"/> is a good reply to a read of <paramref name="addr"/>, else what's wrong.</summary>
        public static string CheckReadResponse(byte[] resp, uint addr)
        {
            if (resp.Length != 24) return "reply has " + resp.Length + " bytes, expected 24";
            if (resp[0] != (byte)'W') return "reply starts with 0x" + resp[0].ToString("X2") + ", expected 'W'";
            uint got = GetBigEndian(resp, 1);
            if (got != addr) return "reply is for address 0x" + got.ToString("X7");
            if (resp[5] != 0x10) return "reply length byte is " + resp[5];
            if (Checksum(resp, 1, 21) != resp[22]) return "checksum mismatch";
            if (resp[23] != 0x06) return "reply doesn't end with ACK";
            return null;
        }

        /// <summary>Leaves programming mode; the radio goes back to normal operation. Never throws.</summary>
        public void Close()
        {
            if (!inProgramMode) return;
            inProgramMode = false;
            try
            {
                Send(Encoding.ASCII.GetBytes("END"));
                Receive(1, "the reply to END");
            }
            catch (Exception) { }
        }

        public static byte Checksum(byte[] data, int offset, int count)
        {
            int sum = 0;
            for (int i = offset; i < offset + count; i++) sum += data[i];
            return (byte)sum;
        }

        public static void PutBigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        public static uint GetBigEndian(byte[] buf, int offset)
        {
            return (uint)buf[offset] << 24 | (uint)buf[offset + 1] << 16 | (uint)buf[offset + 2] << 8 | buf[offset + 3];
        }

        void Send(byte[] data)
        {
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        byte[] Receive(int count, string what)
        {
            var buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n;
                try { n = stream.Read(buf, got, count - got); }
                catch (TimeoutException) { n = 0; }
                if (n <= 0)
                    throw new TimeoutException("No answer from the radio while waiting for " + what + " (" + got + " of " + count + " bytes).");
                got += n;
            }
            return buf;
        }

        static string AsciiZ(byte[] b, int offset, int max)
        {
            int n = 0;
            while (n < max && b[offset + n] != 0) n++;
            return Encoding.ASCII.GetString(b, offset, n).Trim();
        }

        static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", " ");
    }
}
