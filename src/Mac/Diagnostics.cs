using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// A plain-text report of what this computer looks like to the program: system, folders, the map data, the serial ports
    /// and the radio's USB entry, and whether the online sources answer. Help > Diagnostics shows it (with a Copy button), and
    /// <c>--diagnose</c> prints it to the terminal. It is what to send when something doesn't work on a Mac.
    /// </summary>
    static class Diagnostics
    {
        public static string Report(bool network)
        {
            var sb = new StringBuilder();
            void L(string s = "") { sb.AppendLine(s); }

            L(Dialogs.AppName + " diagnostics (Mac version)");
            L("Made " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            L();
            L("== System");
            L("Program version : " + (typeof(Diagnostics).Assembly.GetName().Version?.ToString() ?? "?"));
            L("Operating system: " + RuntimeInformation.OSDescription);
            L("OS architecture : " + RuntimeInformation.OSArchitecture + ", program runs as " + RuntimeInformation.ProcessArchitecture);
            L(".NET            : " + RuntimeInformation.FrameworkDescription);
            L("Culture         : " + System.Globalization.CultureInfo.CurrentCulture.Name);
            L();

            L("== Folders");
            Folder(L, "Settings", AppSettings.Folder);
            Folder(L, "Documents", AppSettings.DocumentsFolder);
            Folder(L, "Radio reads (backups)", RadioPort.ReadsFolder);
            foreach (var key in new[] { "Region", "Callsign", RadioPort.SavedPortKey })
                L("Setting " + key.PadRight(9) + ": " + (AppSettings.Get(key) ?? "(not set)"));
            L("Last project    : " + (AppSettings.Get("LastProject") == null ? "(none)" : "set"));
            L();

            L("== Map data");
            try
            {
                var sw = Stopwatch.StartNew();
                var atlas = GeoAtlas.BuiltIn();
                L("Loaded in " + sw.ElapsedMilliseconds + " ms: " + atlas.Countries.Count + " countries, " + atlas.States.Count + " states/provinces, " +
                  atlas.Counties.Count + " US counties, " + atlas.Places.Count + " places, " + atlas.Roads.Count + " highways");
            }
            catch (Exception ex) { L("FAILED: " + ex.Message); }
            L();

            L("== Serial ports and the radio");
            try
            {
                var ports = RadioPort.AllPorts();
                L("Ports the system lists (" + ports.Count + "): " + (ports.Count == 0 ? "none" : string.Join(", ", ports)));
            }
            catch (Exception ex) { L("Listing ports failed: " + ex.Message); }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var dev = Directory.GetFiles("/dev", "cu.*").Concat(Directory.GetFiles("/dev", "tty.usb*")).Concat(Directory.GetFiles("/dev", "ttyACM*")).OrderBy(x => x).ToList();
                    L("/dev entries (cu.*, tty.usb*, ttyACM*): " + (dev.Count == 0 ? "none" : string.Join(", ", dev)));
                }
                catch (Exception ex) { L("Reading /dev failed: " + ex.Message); }
            }
            try
            {
                var radios = RadioPort.FindRadioPorts();
                L("Found as the radio: " + (radios.Count == 0 ? "none" : string.Join(", ", radios)));
            }
            catch (Exception ex) { L("Radio detection failed: " + ex.Message); }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) UsbLines(L);
            L("(The radio is USB vendor 28E9, product 018A. If it is not listed above with the cable in and the radio on, the Mac does not see it.)");
            L();

            if (network)
            {
                L("== Online sources");
                Probe(L, "RadioID.net (user lookup)", () => RadioId.ParseUsers(Online.Get(RadioId.UserUrl("W6OZZ"), 15000)).Count + " result(s)");
                Probe(L, "BrandMeister (talkgroup names)", () => BrandMeister.ParseTalkgroups(Online.Get(BrandMeister.TalkgroupUrl, 15000)).Count + " talkgroups");
                L();
            }
            L("This report has no tokens and no project data. Folder paths contain your user name; edit it out if you prefer.");
            return sb.ToString();
        }

        static void Folder(Action<string> L, string what, string path)
        {
            string note;
            try
            {
                Directory.CreateDirectory(path);
                string probe = Path.Combine(path, ".diag-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                note = "writable";
            }
            catch (Exception ex) { note = "NOT writable (" + ex.Message + ")"; }
            L(what.PadRight(15) + ": " + path + "  [" + note + "]");
        }

        static void Probe(Action<string> L, string what, Func<string> test)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                string result = test();
                L(what.PadRight(32) + ": OK, " + result + " in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex) { L(what.PadRight(32) + ": FAILED, " + ex.Message.Replace('\n', ' ')); }
        }

        /// <summary>
        /// macOS only: the radio's USB entry (vendor 0x28e9 = 10473), from the I/O registry. <c>system_profiler SPUSBDataType</c> prints
        /// nothing on recent macOS versions, so <c>ioreg</c> is asked instead; it prints the IDs in decimal.
        /// </summary>
        static void UsbLines(Action<string> L)
        {
            try
            {
                var psi = new ProcessStartInfo("ioreg", "-r -c IOUSBHostDevice -l") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string all = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                    var found = false;
                    var keys = new[] { "\"USB Product Name\"", "\"USB Vendor Name\"", "\"idVendor\"", "\"idProduct\"", "\"USB Serial Number\"" };
                    var entry = new List<string>();
                    bool isRadio = false;
                    var shown = new HashSet<string>();
                    Action flush = () =>
                    {
                        // ioreg repeats a device's properties on its child nodes; show each distinct, complete entry once.
                        if (isRadio && entry.Any(l => l.StartsWith("\"USB Product Name\"")) && shown.Add(string.Join("\n", entry)))
                        {
                            found = true;
                            L("USB (I/O registry) entry for vendor 0x28e9:");
                            foreach (var l in entry) L("    " + l);
                        }
                        entry.Clear(); isRadio = false;
                    };
                    foreach (string raw in all.Split('\n'))
                    {
                        string line = raw.Trim().TrimStart('|', ' ');
                        if (line.StartsWith("+-o ")) { flush(); continue; }
                        if (keys.Any(k => line.StartsWith(k)) && !entry.Contains(line)) entry.Add(line);
                        if (line.StartsWith("\"idVendor\"") && line.EndsWith("= " + 0x28E9.ToString(System.Globalization.CultureInfo.InvariantCulture))) isRadio = true;
                    }
                    flush();
                    if (!found) L("USB (I/O registry): no device with vendor 0x28e9 (the radio is not on USB, or is not recognized)");
                }
            }
            catch (Exception ex) { L("USB (I/O registry) could not be read: " + ex.Message); }
        }
    }
}
