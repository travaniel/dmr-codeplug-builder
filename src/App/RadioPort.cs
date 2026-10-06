using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// The radio's USB serial port. The DMR-6X2 PRO (like the AnyTone D868/878) shows up as a USB CDC
    /// serial port with vendor 28E9, product 018A (GD32 chip); Windows gives it a COM number.
    /// </summary>
    static class RadioPort
    {
        const string UsbKey = @"SYSTEM\CurrentControlSet\Enum\USB\VID_28E9&PID_018A";

        /// <summary>Documents\DMR Codeplug Builder\Radio reads: every read and write is kept there as a backup.</summary>
        public static string ReadsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DMR Codeplug Builder", "Radio reads");

        /// <summary>A new, dated folder under <see cref="ReadsFolder"/> (not created yet).</summary>
        public static string NewReadFolder(string suffix = "") =>
            Path.Combine(ReadsFolder, DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + suffix);

        /// <summary>COM ports that belong to a connected radio (by USB ID), present right now.</summary>
        public static List<string> FindRadioPorts()
        {
            var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            var found = new List<string>();
            try
            {
                using (var usb = Registry.LocalMachine.OpenSubKey(UsbKey))
                {
                    if (usb == null) return found;
                    foreach (string instance in usb.GetSubKeyNames())
                    {
                        using (var p = usb.OpenSubKey(instance + @"\Device Parameters"))
                        {
                            if (p?.GetValue("PortName") is string port && present.Contains(port) && !found.Contains(port))
                                found.Add(port);
                        }
                    }
                }
            }
            catch (Exception) { }
            return found;
        }

        /// <summary>Picks the port to use: the given one, else the only radio port found.</summary>
        public static string Choose(string requested, List<string> log)
        {
            // After leaving programming mode the radio drops off USB for a few seconds; wait for it.
            var radios = FindRadioPorts();
            for (int i = 0; i < 20 && radios.Count == 0 && string.IsNullOrEmpty(requested); i++)
            {
                Thread.Sleep(500);
                radios = FindRadioPorts();
            }
            log?.Add("Serial ports: " + string.Join(", ", SerialPort.GetPortNames()) + "; radio (USB 28E9:018A): " + (radios.Count > 0 ? string.Join(", ", radios) : "none"));
            if (!string.IsNullOrEmpty(requested)) return requested;
            if (radios.Count == 1) return radios[0];
            if (radios.Count == 0)
                throw new InvalidOperationException("No DMR-6X2 PRO found on USB. Connect the programming cable, switch the radio on, and close the BTECH CPS if it's open.");
            throw new InvalidOperationException("More than one radio is connected (" + string.Join(", ", radios) + "). Name the port to use.");
        }

        public static SerialPort Open(string portName)
        {
            var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1500,
                WriteTimeout = 1500,
                DtrEnable = true,
                RtsEnable = true,
            };
            port.Open();
            port.DiscardInBuffer();
            return port;
        }

        /// <summary>Writes what differs between <paramref name="original"/> (read from this radio) and <paramref name="edited"/>.</summary>
        public static WriteResult Write(string portName, MemoryImage original, MemoryImage edited, IProgress<ReadProgress> progress = null)
        {
            WriteResult result;
            using (var port = Open(portName))
            {
                var link = new AnytoneLink(port.BaseStream, port.DiscardInBuffer);
                result = RadioWriter.Write(link, original, edited, progress);
            }
            if (result.Blocks.Count == 0) return result;

            // The radio restarts its USB connection after leaving programming mode: wait, then check in a new session.
            Thread.Sleep(2000);
            Exception last = null;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    if (!SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase)) { Thread.Sleep(500); continue; }
                    using (var port = Open(portName))
                    {
                        var bad = RadioWriter.Verify(new AnytoneLink(port.BaseStream, port.DiscardInBuffer), edited, result.Blocks);
                        if (bad.Count > 0)
                            throw new RadioProtocolException("The radio accepted the write, but after reconnecting " + bad.Count + " of " + result.Blocks.Count
                                + " block(s) don't hold the new data (" + string.Join(", ", bad.Select(x => "0x" + x.ToString("X7"))) + ").");
                        result.Log.Add("Verified after reconnecting: all " + result.Blocks.Count + " block(s) hold the new data.");
                        return result;
                    }
                }
                catch (RadioProtocolException) { throw; }
                catch (Exception ex) { last = ex; Thread.Sleep(500); }
            }
            throw new InvalidOperationException("Wrote the radio but couldn't reconnect to check it" + (last != null ? ": " + last.Message : ".") + " Read the radio to see what it holds.");
        }

        /// <summary>Reads the whole codeplug from the radio on <paramref name="portName"/>.</summary>
        public static MemoryImage Read(string portName, IProgress<ReadProgress> progress = null, CancellationToken cancel = default(CancellationToken), bool allowOtherModel = false)
        {
            using (var port = Open(portName))
            {
                var link = new AnytoneLink(port.BaseStream, port.DiscardInBuffer);
                return RadioReader.Read(link, progress, cancel, allowOtherModel);
            }
        }
    }
}
