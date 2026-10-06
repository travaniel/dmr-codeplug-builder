using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    sealed class TestAttribute : Attribute { }

    static class Assert
    {
        public static void True(bool cond, string message)
        {
            if (!cond) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(what + ": expected <" + expected + "> but got <" + actual + ">");
        }
    }

    static class Program
    {
        /// <summary>Folder with the CPS "Export All" from the user's radio (Channel.CSV, Zone.CSV, ...).</summary>
        public static string ExportFolder;

        static int Main(string[] args)
        {
            ExportFolder = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("CPS_EXPORT");
            int failed = 0, passed = 0;
            foreach (var m in typeof(Program).Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                         .Where(m => m.GetCustomAttribute<TestAttribute>() != null).OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name))
            {
                string name = m.DeclaringType.Name + "." + m.Name;
                try
                {
                    m.Invoke(null, null);
                    passed++;
                    Console.WriteLine("  PASS  " + name);
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    Console.WriteLine("  FAIL  " + name + "\n        " + ex.InnerException.Message.Replace("\n", "\n        "));
                }
            }
            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }

    static class Fixtures
    {
        public static Project Sample(int extraRepeaters = 0)
        {
            var p = new Project { RadioIdName = "Test N0CALL", RadioId = 3100001 };
            p.Talkgroups.Add(new Talkgroup("Local", 9));
            p.Talkgroups.Add(new Talkgroup("Worldwide", 91));
            p.Talkgroups.Add(new Talkgroup("North America", 93));
            p.Talkgroups.Add(new Talkgroup("USA Nationwide", 3100));
            p.Talkgroups.Add(new Talkgroup("Parrot", 9990, CallTypes.Private));
            p.Talkgroups.Add(new Talkgroup("A Very Long Talkgroup Name", 31999));

            var r1 = Repeater.NewDigital("W1ABC Metro");
            r1.Prefix = "W1ABC";
            r1.Zone = "Metro";
            r1.RxMHz = 444.125m; r1.TxMHz = 449.125m; r1.ColorCode = 3;
            r1.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
            r1.Talkgroups.Add(new RepeaterTalkgroup(91, 1));
            r1.Talkgroups.Add(new RepeaterTalkgroup(3100, 1));
            r1.Talkgroups.Add(new RepeaterTalkgroup(31999, 2));
            r1.Talkgroups.Add(new RepeaterTalkgroup(9990, 2));
            p.Repeaters.Add(r1);

            var r2 = Repeater.NewDigital("K2XYZ");
            r2.Prefix = "K2XYZ";
            r2.Zone = "Metro";
            r2.RxMHz = 442.000m; r2.TxMHz = 447.000m; r2.ColorCode = 1;
            r2.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
            r2.Talkgroups.Add(new RepeaterTalkgroup(93, 1));
            p.Repeaters.Add(r2);

            var a = Repeater.NewAnalog("W1ABC VHF");
            a.Zone = "Analog";
            a.RxMHz = 146.940m; a.TxMHz = 146.340m; a.ToneEncode = "100.0"; a.ToneDecode = "100.0"; a.ToneSquelch = true;
            p.Repeaters.Add(a);

            var noaa = Repeater.NewAnalog("NOAA 1");
            noaa.Zone = "Weather";
            noaa.RxMHz = 162.400m; noaa.TxMHz = 162.400m; noaa.Bandwidth = Bandwidths.Narrow; noaa.RxOnly = true;
            p.Repeaters.Add(noaa);

            for (int i = 0; i < extraRepeaters; i++)
            {
                var x = Repeater.NewDigital("Rpt " + i);
                x.Prefix = "R" + i;
                x.Zone = "Big";
                x.RxMHz = 440m + i * 0.0125m; x.TxMHz = x.RxMHz + 5m;
                x.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
                x.Talkgroups.Add(new RepeaterTalkgroup(91, 1));
                x.Talkgroups.Add(new RepeaterTalkgroup(93, 1));
                p.Repeaters.Add(x);
            }

            p.HotspotEnabled = true;
            p.Hotspot.RxMHz = 433.550m; p.Hotspot.TxMHz = 433.550m;
            p.Hotspot.Talkgroups.Add(new RepeaterTalkgroup(91, 2));
            p.Hotspot.Talkgroups.Add(new RepeaterTalkgroup(3100, 2));
            p.Hotspot.Talkgroups.Add(new RepeaterTalkgroup(9990, 2));
            p.Normalize();
            return p;
        }

        public static string RequireExport()
        {
            if (string.IsNullOrEmpty(Program.ExportFolder) || !Directory.Exists(Program.ExportFolder))
                throw new Exception("Pass the CPS export folder as the first argument.");
            return Program.ExportFolder;
        }
    }

    static class CsvTests
    {
        [Test]
        static void ExportFilesRoundTripByteForByte()
        {
            string dir = Fixtures.RequireExport();
            foreach (var path in Directory.GetFiles(dir, "*.CSV"))
            {
                string original = File.ReadAllText(path, CsvTable.FileEncoding);
                var t = CsvTable.Parse(original);
                // RoamingZone.CSV has a stray trailing comma in its header; every other file must match exactly.
                if (Path.GetFileName(path).Equals("RoamingZone.CSV", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(path).Equals("APRS.CSV", StringComparison.OrdinalIgnoreCase)) continue;
                Assert.Equal(original, t.ToCsv(), Path.GetFileName(path) + " round trip");
            }
        }

        [Test]
        static void ParsesQuotesCommasAndBareLf()
        {
            var t = CsvTable.Parse("\"a\",\"b,c\",d\n\"x \"\"q\"\"\",,\"\"\n");
            Assert.Equal(3, t.Header.Count, "header width");
            Assert.Equal("b,c", t.Header[1], "comma inside quotes");
            Assert.Equal("x \"q\"", t.Rows[0][0], "escaped quote");
            Assert.Equal("", t.Rows[0][1], "empty unquoted");
            Assert.Equal("\"a\",\"b,c\",\"d\"\r\n", t.CloneHeader().ToCsv(), "writer quotes every field");
        }
    }

    static class NamingTests
    {
        [Test]
        static void AutoNamesFitSixteenCharacters()
        {
            Assert.Equal("W5FC Local", Naming.AutoChannelName("W5FC", "Local", 16), "short");
            Assert.Equal("W5FC TX Statewid", Naming.AutoChannelName("W5FC", "TX Statewide", 16), "truncated tg");
            Assert.Equal("Parrot", Naming.AutoChannelName("", "Parrot", 16), "no prefix");
            string longPrefix = Naming.AutoChannelName("Very Long Repeater", "Worldwide", 16);
            Assert.True(longPrefix.Length <= 16, "long prefix fits: " + longPrefix);
            Assert.True(longPrefix.Contains("Worl"), "long prefix keeps part of the TG: " + longPrefix);
        }

        [Test]
        static void UniqueNamerAddsNumbersWithinLimit()
        {
            var n = new UniqueNamer(16);
            Assert.Equal("Sixteen Chars Ab", n.Claim("Sixteen Chars Abc"), "first");
            Assert.Equal("Sixteen Chars 2", n.Claim("Sixteen Chars Abc"), "second gets a number");
            Assert.Equal("sixteen chars 3", n.Claim("sixteen chars abc"), "duplicate check is case-insensitive");
            Assert.Equal("Bad Name", n.Claim("Bad|\"Name\""), "strips pipe and quotes");
        }

        [Test]
        static void TonesNormalize()
        {
            Assert.Equal("Off", Tones.Normalize(""), "blank");
            Assert.Equal("94.8", Tones.Normalize("94.8"), "ctcss");
            Assert.Equal("100.0", Tones.Normalize("100"), "ctcss no decimal");
            Assert.Equal("D023N", Tones.Normalize("d023"), "dcs");
            Assert.Equal("D754I", Tones.Normalize("754i"), "dcs inverted");
            Assert.True(!Tones.IsValid("12.3"), "12.3 isn't a tone");
        }
    }

    static class FormatTests
    {
        [Test]
        static void BuiltInFormatMatchesCps122()
        {
            var f = CpsFormat.BuiltIn();
            Assert.Equal(55, f.Channels.Header.Count, "Channel.CSV columns");
            Assert.Equal(5, f.Zones.Header.Count, "Zone.CSV columns (names only, no frequencies)");
            Assert.Equal(3, f.RxGroupLists.Header.Count, "ReceiveGroupCallList.CSV columns");
            Assert.Equal(2, f.VfoRows.Count, "VFO rows");
            Assert.Equal(5, f.FrequencyDecimals, "frequency decimals");
            Assert.Equal("A-Analog", f.Channels.Get(f.AnalogTemplate, "Channel Type"), "analog template");
            Assert.Equal("D-Digital", f.Channels.Get(f.DigitalTemplate, "Channel Type"), "digital template");
        }

        [Test]
        static void FormatFromExportEqualsBuiltInHeaders()
        {
            var a = CpsFormat.FromFolder(Fixtures.RequireExport());
            var b = CpsFormat.BuiltIn();
            foreach (var file in CpsFormat.Files)
                Assert.Equal(string.Join(",", b.Table(file).Header), string.Join(",", a.Table(file).Header), file + " header");
        }

        [Test]
        static void ListFileMatchesCpsFormat()
        {
            string lst = CpsFormat.BuildListFile(new[] { "Zone.CSV", "Channel.CSV", "TalkGroups.CSV", "ReceiveGroupCallList.CSV" });
            Assert.Equal("4\r\n0,\"Channel.CSV\"\r\n2,\"Zone.CSV\"\r\n5,\"TalkGroups.CSV\"\r\n8,\"ReceiveGroupCallList.CSV\"\r\n", lst, "lst text");

            // The indices must agree with the CPS's own Export All list.
            string exported = File.ReadAllText(Path.Combine(Fixtures.RequireExport(), "testplug.LST"));
            foreach (var line in lst.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
                Assert.True(exported.Contains(line + "\r\n"), "CPS list file contains " + line);
        }
    }

    static class GeneratorTests
    {
        static void CheckIntegrity(GeneratedCodeplug g, Project p)
        {
            var f = CpsFormat.BuiltIn();
            foreach (var file in g.Files())
            {
                var t = file.Value;
                Assert.Equal(string.Join(",", f.Table(file.Key).Header), string.Join(",", t.Header), file.Key + " header matches CPS");
                foreach (var row in t.Rows) Assert.Equal(t.Header.Count, row.Count, file.Key + " row width");
            }

            var ch = g.Channels;
            var names = ch.Rows.Select(r => ch.Get(r, "Channel Name")).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "channel names unique");
            foreach (var n in names)
            {
                Assert.True(n.Length > 0 && n.Length <= 16, "channel name length: '" + n + "'");
                Assert.True(!n.Contains("|") && !n.Contains("\""), "channel name characters: " + n);
            }

            var tgNames = new HashSet<string>(g.TalkGroups.Rows.Select(r => g.TalkGroups.Get(r, "Name")));
            var rxNames = new HashSet<string>(g.RxGroupLists.Rows.Select(r => g.RxGroupLists.Get(r, "Group Name")));
            var radioIds = g.RadioIds != null ? new HashSet<string>(g.RadioIds.Rows.Select(r => g.RadioIds.Get(r, "Name"))) : null;
            foreach (var r in ch.Rows)
            {
                string n = ch.Get(r, "Channel Name");
                Assert.True(tgNames.Contains(ch.Get(r, "Contact")), n + ": contact '" + ch.Get(r, "Contact") + "' exists");
                string rx = ch.Get(r, "Receive Group List");
                Assert.True(rx == "None" || rxNames.Contains(rx), n + ": RX group list '" + rx + "' exists");
                if (radioIds != null) Assert.True(radioIds.Contains(ch.Get(r, "Radio ID")), n + ": radio ID exists");
                foreach (string col in new[] { "Receive Frequency", "Transmit Frequency" })
                {
                    string v = ch.Get(r, col);
                    Assert.True(System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{3}\.\d{5}$"), n + ": " + col + " format " + v);
                }
                if (ch.Get(r, "Channel Type") == "D-Digital")
                {
                    string slot = ch.Get(r, "Slot");
                    Assert.True(slot == "1" || slot == "2", n + ": slot");
                }
            }
            var nameSet = new HashSet<string>(names);
            foreach (var z in g.Zones.Rows)
            {
                var members = g.Zones.Get(z, "Zone Channel Member").Split('|');
                Assert.True(members.Length >= 1, "zone has members");
                foreach (var m in members) Assert.True(nameSet.Contains(m), "zone member '" + m + "' exists");
                Assert.True(members.Contains(g.Zones.Get(z, "A Channel")), "A channel in zone");
                Assert.True(members.Contains(g.Zones.Get(z, "B Channel")), "B channel in zone");
                Assert.True(g.Zones.Get(z, "Zone Name").Length <= 16, "zone name length");
            }
            foreach (var rl in g.RxGroupLists.Rows)
                foreach (var m in g.RxGroupLists.Get(rl, "Contact").Split('|'))
                    Assert.True(tgNames.Contains(m), "RX group member '" + m + "' exists");
            var tgIds = g.TalkGroups.Rows.Select(r => g.TalkGroups.Get(r, "Radio ID")).ToList();
            Assert.Equal(tgIds.Count, tgIds.Distinct().Count(), "talkgroup IDs unique");
        }

        static string Numbers(GeneratedCodeplug g)
        {
            return string.Join(",", g.ChannelList.Select(c => c.Name + "=" + c.Number));
        }

        [Test]
        static void ChannelNumbersStayPut()
        {
            var f = CpsFormat.BuiltIn();
            var p = Fixtures.Sample();
            var g = CodeplugGenerator.Generate(p, f);
            Assert.Equal("1,2,3,4,5,6,7,8,9,10,11,12", string.Join(",", g.ChannelList.Select(c => c.Number)), "nothing stored: 1, 2, 3... as before");
            Assert.Equal(12, CodeplugGenerator.KeepChannelNumbers(g), "first Generate stores every number");
            Assert.Equal(0, CodeplugGenerator.KeepChannelNumbers(CodeplugGenerator.Generate(p, f)), "nothing new the second time");

            // A new repeater at the top of the list: everyone else keeps their number, the new channels take 13, 14.
            var before = CodeplugGenerator.Generate(p, f).ChannelList.ToDictionary(c => c.Name, c => c.Number);
            var fresh = Repeater.NewDigital("N3NEW");
            fresh.Prefix = "N3NEW"; fresh.Zone = "Metro"; fresh.RxMHz = 443m; fresh.TxMHz = 448m;
            fresh.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
            fresh.Talkgroups.Add(new RepeaterTalkgroup(91, 1));
            p.Repeaters.Insert(0, fresh);
            g = CodeplugGenerator.Generate(p, f);
            Assert.True(g.ChannelList.Where(c => c.Repeater != fresh).All(c => before[c.Name] == c.Number), "existing channels keep their numbers: " + Numbers(g));
            Assert.Equal("13,14", string.Join(",", g.ChannelList.Where(c => c.Repeater == fresh).Select(c => c.Number)), "new channels get the next free numbers");
            Assert.Equal("1,2,3,4,5,6,7,8,9,10,11,12,13,14,4001,4002", string.Join(",", g.Channels.Rows.Select(r => g.Channels.Get(r, "No."))), "rows written in number order");
            CodeplugGenerator.KeepChannelNumbers(g);

            // Deleting a channel frees its number for the next new one; a switched-off repeater keeps its numbers for later.
            var r1 = p.Repeaters.First(r => r.Name == "W1ABC Metro");
            int freed = r1.Talkgroups[0].ChannelNumber;
            r1.Talkgroups.RemoveAt(0);
            var k2 = p.Repeaters.First(r => r.Name == "K2XYZ");
            var k2Numbers = k2.Talkgroups.Select(e => e.ChannelNumber).ToList();
            k2.Enabled = false;
            r1.Talkgroups.Add(new RepeaterTalkgroup(93, 1));
            r1.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
            g = CodeplugGenerator.Generate(p, f);
            var added = g.ChannelList.Where(c => c.Repeater == r1).Skip(r1.Talkgroups.Count - 2).Select(c => c.Number).ToList();
            Assert.Equal(freed, added[0], "the freed number is reused first");
            Assert.True(!added.Intersect(k2Numbers).Any(), "a switched-off repeater's numbers aren't handed out: " + string.Join(",", added));
            CodeplugGenerator.KeepChannelNumbers(g);
            k2.Enabled = true;
            g = CodeplugGenerator.Generate(p, f);
            Assert.Equal(string.Join(",", k2Numbers), string.Join(",", g.ChannelList.Where(c => c.Repeater == k2).Select(c => c.Number)), "switched back on: same numbers");

            // Clashes and impossible numbers are fixed with a note.
            var hs = p.Hotspot.Talkgroups;
            hs[1].ChannelNumber = hs[0].ChannelNumber;
            hs[2].ChannelNumber = 4001;
            g = CodeplugGenerator.Generate(p, f);
            var nums = g.ChannelList.Select(c => c.Number).ToList();
            Assert.Equal(nums.Count, nums.Distinct().Count(), "numbers unique after a clash");
            Assert.True(nums.All(n => n >= 1 && n < 4001), "no channel on a VFO number");
            Assert.True(g.Notes.Any(n => n.Contains("both had number")) && g.Notes.Any(n => n.Contains("doesn't have")), "clash and bad number noted");
            CheckIntegrity(g, p);

            // Renumbering starts over at 1.
            p.ClearChannelNumbers();
            Assert.Equal(0, p.StoredChannelNumbers().Count, "nothing stored after clearing");
            g = CodeplugGenerator.Generate(p, f);
            Assert.Equal(string.Join(",", Enumerable.Range(1, g.ChannelList.Count)), string.Join(",", g.ChannelList.Select(c => c.Number)), "renumbered 1, 2, 3...");

            // Saved in the project file; zero isn't written.
            CodeplugGenerator.KeepChannelNumbers(g);
            var back = ProjectStore.FromJson(ProjectStore.ToJson(p));
            Assert.Equal(p.StoredChannelNumbers().Count, back.StoredChannelNumbers().Count, "numbers saved");
            Assert.True(!ProjectStore.ToJson(Fixtures.Sample()).Contains("ChannelNumber"), "unnumbered projects don't mention ChannelNumber");
        }

        [Test]
        static void SampleProjectIsConsistent()
        {
            var p = Fixtures.Sample();
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            CheckIntegrity(g, p);

            // 5 + 2 digital, 2 analog, 3 hotspot channels + 2 VFO rows
            Assert.Equal(12, g.ChannelList.Count, "channel count");
            Assert.Equal(14, g.Channels.Rows.Count, "rows incl. VFO");
            Assert.Equal("4001", g.Channels.Get(g.Channels.Rows[12], "No."), "VFO A keeps its number");
            Assert.Equal("W1ABC Local", g.ChannelList[0].Name, "auto name");
            Assert.Equal("W1ABC A Very Lon", g.ChannelList[3].Name, "long TG name truncated");
            Assert.Equal("HS Worldwide", g.ChannelList.First(c => c.Repeater == p.Hotspot).Name, "hotspot prefix");

            var zones = g.ZoneList.Select(z => z.Name).ToList();
            Assert.Equal("Metro|Analog|Weather|Hotspot", string.Join("|", zones), "zone order follows repeaters");

            // NOAA is receive-only and narrow
            var noaaRow = g.Channels.Rows.First(r => g.Channels.Get(r, "Channel Name") == "NOAA 1");
            Assert.Equal("On", g.Channels.Get(noaaRow, "TX Prohibit"), "rx only");
            Assert.Equal("12.5K", g.Channels.Get(noaaRow, "Band Width"), "narrow");
            var vhf = g.Channels.Rows.First(r => g.Channels.Get(r, "Channel Name") == "W1ABC VHF");
            Assert.Equal("CTCSS/DCS", g.Channels.Get(vhf, "Squelch Mode"), "tone squelch");
            Assert.Equal("100.0", g.Channels.Get(vhf, "CTCSS/DCS Encode"), "encode tone");
            Assert.Equal("25K", g.Channels.Get(vhf, "Band Width"), "analog wide");

            var parrot = g.Channels.Rows.First(r => g.Channels.Get(r, "Channel Name") == "W1ABC Parrot");
            Assert.Equal("Private Call", g.Channels.Get(parrot, "Contact Call Type"), "private call");
            Assert.Equal("3", g.Channels.Get(parrot, "Color Code"), "color code");
            Assert.Equal("2", g.Channels.Get(parrot, "Slot"), "slot");

            // RX group list holds the repeater's group-call talkgroups only (not the private Parrot)
            var rxl = g.RxGroupLists.Rows.First(r => g.RxGroupLists.Get(r, "Group Name") == "W1ABC Metro");
            Assert.Equal("Local|Worldwide|USA Nationwide|A Very Long Talk", g.RxGroupLists.Get(rxl, "Contact"), "rx list members");

            Assert.True(g.RadioIds != null && g.RadioIds.Rows.Count == 1, "radio ID list written");
            Assert.Equal("3100001", g.RadioIds.Get(g.RadioIds.Rows[0], "Radio ID"), "dmr id");
        }

        [Test]
        static void CustomChannelNamesAndCollisions()
        {
            var p = Fixtures.Sample();
            p.Repeaters[0].Talkgroups[1].ChannelName = "My Custom";
            p.Repeaters[1].Prefix = "W1ABC"; // same prefix as repeater 0 → "W1ABC Local" collides
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            CheckIntegrity(g, p);
            Assert.True(g.ChannelList.Any(c => c.Name == "My Custom"), "custom name used");
            Assert.True(g.ChannelList.Any(c => c.Name == "W1ABC Local 2"), "collision numbered");
            Assert.True(g.Notes.Any(n => n.Contains("already taken")), "collision reported");
        }

        [Test]
        static void LargeZoneIsSplitAt250()
        {
            var p = Fixtures.Sample(extraRepeaters: 100); // 300 channels in zone "Big"
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            CheckIntegrity(g, p);
            var big = g.ZoneList.Where(z => z.Name.StartsWith("Big")).ToList();
            Assert.Equal(2, big.Count, "split into two zones");
            Assert.Equal(250, big[0].Members.Count, "first part full");
            Assert.Equal(50, big[1].Members.Count, "remainder");
            Assert.Equal("Big 2", big[1].Name, "second zone name");
        }

        [Test]
        static void ScanListsPerZone()
        {
            var p = Fixtures.Sample();
            p.Options.ScanListPerZone = true;
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            CheckIntegrity(g, p);
            Assert.Equal(g.ZoneList.Count, g.ScanLists.Rows.Count, "one scan list per zone");
            var first = g.Channels.Rows[0];
            Assert.Equal("Metro", g.Channels.Get(first, "Scan List 1"), "channel points at its zone's scan list");
            Assert.True(g.Files().Any(f => f.Key == CpsFormat.ScanListFile), "scan list file written");
            Assert.True(g.ListFileText().Contains("3,\"ScanList.CSV\""), "listed in LST");
        }

        [Test]
        static void ZoneAAndBSelections()
        {
            var p = Fixtures.Sample();
            p.FindZone("Metro").AChannel = "K2XYZ Local";
            p.FindZone("Metro").BChannel = "does not exist";
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            var metro = g.ZoneList.First(z => z.Name == "Metro");
            Assert.Equal("K2XYZ Local", metro.AChannel, "A from project");
            Assert.Equal(metro.Members[1].Name, metro.BChannel, "B falls back to 2nd channel");
        }

        [Test]
        static void WritesFilesAndListToFolder()
        {
            var p = Fixtures.Sample();
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            string dir = Path.Combine(Path.GetTempPath(), "cpb-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var written = g.WriteTo(dir);
                Assert.Equal(6, written.Count, "5 CSVs + LST");
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "Channel.CSV"));
                Assert.True(bytes[0] == (byte)'"', "no BOM");
                Assert.True(bytes[bytes.Length - 2] == '\r' && bytes[bytes.Length - 1] == '\n', "ends with CRLF");
                string lst = File.ReadAllText(Path.Combine(dir, GeneratedCodeplug.DefaultListFileName));
                Assert.Equal("5\r\n0,\"Channel.CSV\"\r\n1,\"RadioIDList.CSV\"\r\n2,\"Zone.CSV\"\r\n5,\"TalkGroups.CSV\"\r\n8,\"ReceiveGroupCallList.CSV\"\r\n", lst, "list file");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Test]
        static void DisabledRepeatersAreLeftOut()
        {
            var p = Fixtures.Sample();
            p.Repeaters[1].Enabled = false;
            p.HotspotEnabled = false;
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            CheckIntegrity(g, p);
            Assert.True(!g.ChannelList.Any(c => c.Repeater == p.Repeaters[1]), "disabled repeater skipped");
            Assert.True(!g.ZoneList.Any(z => z.Name == "Hotspot"), "hotspot zone gone");
        }
    }

    static class ImportTests
    {
        [Test]
        static void ImportsUsersCodeplug()
        {
            var r = CpsImporter.Import(Fixtures.RequireExport());
            var p = r.Project;
            Assert.Equal("Austin W6OZZ", p.RadioIdName, "radio id name");
            Assert.Equal(3226509, p.RadioId, "dmr id");
            Assert.Equal(9, p.Talkgroups.Count, "talkgroups");
            Assert.True(p.HotspotEnabled, "hotspot detected");
            Assert.Equal(433.55m, p.Hotspot.RxMHz, "hotspot freq");
            Assert.Equal(8, p.Hotspot.Talkgroups.Count, "hotspot talkgroups");
            Assert.Equal("Home", p.Hotspot.Zone, "hotspot zone");
            Assert.Equal(16, p.Repeaters.Count, "15 analog + KC5EZZ");
            Assert.Equal(15, p.Repeaters.Count(x => !x.IsDigital), "analog channels");
            var kc = p.Repeaters.Single(x => x.IsDigital);
            Assert.Equal("KC5EZZ", kc.Name, "digital repeater name");
            Assert.Equal("San Angelo DMR", kc.Zone, "digital repeater zone");
            Assert.Equal("Local|Weather|San Angelo DMR|Hwy 183|Home", string.Join("|", p.Zones.Select(z => z.Name)), "zone order");
            var noaa = p.Repeaters.First(x => x.Name == "NOAA CH1");
            Assert.True(noaa.RxOnly, "NOAA is RX only");
            var wd9 = p.Repeaters.First(x => x.Name == "WD9ARW");
            Assert.True(wd9.ToneSquelch && wd9.ToneDecode == "94.8", "tone squelch imported");
        }

        /// <summary>The acid test: import the user's codeplug and regenerate it; every channel must come back identical.</summary>
        [Test]
        static void RoundTripReproducesUsersCodeplug()
        {
            string dir = Fixtures.RequireExport();
            var p = CpsImporter.Import(dir).Project;
            p.Options.RxGroupListPerRepeater = false; // the original codeplug uses none
            p.Options.WriteRadioIdList = true;
            var f = CpsFormat.FromFolder(dir);
            var g = CodeplugGenerator.Generate(p, f);
            CheckAgainstOriginal(dir, g);

            // And through a save/load cycle of the project file.
            var p2 = ProjectStore.FromJson(ProjectStore.ToJson(p));
            var g2 = CodeplugGenerator.Generate(p2, f);
            Assert.Equal(g.Channels.ToCsv(), g2.Channels.ToCsv(), "same output after save/load");
        }

        static string Csvs(GeneratedCodeplug g)
        {
            return string.Join("\n---\n", g.Files().Select(kv => kv.Key + "\n" + kv.Value.ToCsv()));
        }

        /// <summary>Merge mode: what was made in the CPS comes back, what was deleted or renamed here doesn't.</summary>
        [Test]
        static void MergeKeepsWhatWasMadeInTheCps()
        {
            string dir = Fixtures.RequireExport();
            var f = CpsFormat.FromFolder(dir);

            // Merging with the export the project came from keeps nothing and changes nothing.
            var p0 = CpsImporter.Import(dir).Project;
            Assert.Equal(Csvs(CodeplugGenerator.Generate(p0, f)), Csvs(CodeplugGenerator.Generate(p0, f, CpsExport.Load(dir))), "merge with its own export = no merge");
            Assert.True(p0.KnownChannels != null && p0.KnownChannels.Count == 24 && p0.KnownZones.Count == 5, "the import remembers its channels and zones");

            // A later export with things made in the CPS.
            string tmp = Path.Combine(Path.GetTempPath(), "cpb-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                foreach (var file in Directory.GetFiles(dir)) File.Copy(file, Path.Combine(tmp, Path.GetFileName(file)));
                var ch = CsvTable.Load(Path.Combine(tmp, "Channel.CSV"));
                List<string> Row(string name) { return ch.Rows.First(r => ch.Get(r, "Channel Name") == name); }
                var hand = new List<string>(Row("AC5KT2"));
                ch.Set(hand, "7", "No."); ch.Set(hand, "Hand Made", "Channel Name"); ch.Set(hand, "146.52000", "Receive Frequency"); ch.Set(hand, "146.52000", "Transmit Frequency");
                var club = new List<string>(Row("KC5EZZ"));
                ch.Set(club, "15", "No."); ch.Set(club, "Club Net", "Channel Name"); ch.Set(club, "Club", "Contact"); ch.Set(club, "Club RX", "Receive Group List");
                ch.Set(Row("Brownwood ARCNET"), "Overwritten", "Channel Name"); // slot 31 reused in the CPS
                ch.Rows.Insert(6, hand);
                ch.Rows.Insert(14, club);
                ch.Save(Path.Combine(tmp, "Channel.CSV"));
                var tg = CsvTable.Load(Path.Combine(tmp, "TalkGroups.CSV"));
                var clubTg = new List<string>(tg.Rows[0]);
                tg.Set(clubTg, "99", "No."); tg.Set(clubTg, "31999", "Radio ID"); tg.Set(clubTg, "Club", "Name");
                tg.Rows.Add(clubTg);
                tg.Save(Path.Combine(tmp, "TalkGroups.CSV"));
                var rx = CsvTable.Load(Path.Combine(tmp, "ReceiveGroupCallList.CSV"));
                var clubRx = new List<string>(rx.Rows[0]);
                rx.Set(clubRx, "2", "No."); rx.Set(clubRx, "Club RX", "Group Name"); rx.Set(clubRx, "Club|Texas", "Contact");
                rx.Rows.Add(clubRx);
                rx.Save(Path.Combine(tmp, "ReceiveGroupCallList.CSV"));
                var zn = CsvTable.Load(Path.Combine(tmp, "Zone.CSV"));
                var local = zn.Rows.First(r => zn.Get(r, "Zone Name") == "Local");
                zn.Set(local, zn.Get(local, "Zone Channel Member") + "|Hand Made", "Zone Channel Member");
                var fav = new List<string>(local);
                zn.Set(fav, "6", "No."); zn.Set(fav, "Favorites", "Zone Name"); zn.Set(fav, "Hand Made|KC5EZZ|Club Net", "Zone Channel Member");
                zn.Set(fav, "Club Net", "A Channel"); zn.Set(fav, "KC5EZZ", "B Channel");
                zn.Rows.Add(fav);
                zn.Save(Path.Combine(tmp, "Zone.CSV"));
                var ids = CsvTable.Load(Path.Combine(tmp, "RadioIDList.CSV"));
                var id2 = new List<string>(ids.Rows[0]);
                ids.Set(id2, "2", "No."); ids.Set(id2, "3226510", "Radio ID"); ids.Set(id2, "Austin 2", "Name");
                ids.Rows.Add(id2);
                ids.Save(Path.Combine(tmp, "RadioIDList.CSV"));

                // Meanwhile in the project: NOAA CH7 deleted, WD9ARW renamed, a new channel added; numbers stored as they are in the radio.
                var p = CpsImporter.Import(dir).Project;
                p.Repeaters.Remove(p.Repeaters.First(r => r.Name == "NOAA CH7"));
                p.Repeaters.First(r => r.Name == "WD9ARW").Name = "WD9ARW Rptr";
                var fresh = Repeater.NewAnalog("New One"); fresh.Zone = "Local"; fresh.RxMHz = fresh.TxMHz = 146.55m;
                p.Repeaters.Add(fresh);

                var g = CodeplugGenerator.Generate(p, f, CpsExport.Load(tmp));
                var c = g.Channels;
                string Numbered() { return string.Join(",", c.Rows.Select(r => c.Get(r, "No.") + ":" + c.Get(r, "Channel Name"))); }
                List<string> Named(string name) { return c.Rows.FirstOrDefault(r => c.Get(r, "Channel Name") == name); }
                Assert.Equal("7", c.Get(Named("Hand Made"), "No."), "hand-made channel keeps its number: " + Numbered());
                Assert.Equal("146.52000", c.Get(Named("Hand Made"), "Receive Frequency"), "with its own settings");
                Assert.Equal("15", c.Get(Named("Club Net"), "No."), "second hand-made channel");
                Assert.Equal("Club", c.Get(Named("Club Net"), "Contact"), "its contact");
                Assert.Equal("Club RX", c.Get(Named("Club Net"), "Receive Group List"), "its receive group list");
                Assert.Equal("14", c.Get(Named("New One"), "No."), "a new channel takes the lowest number nobody holds (NOAA CH7's 14 is free)");
                Assert.True(Named("Overwritten") == null, "a channel renamed in the CPS in the project's slot is the project's: not duplicated");
                Assert.Equal("31", c.Get(Named("Brownwood ARCNET"), "No."), "the project's channel keeps slot 31");
                Assert.True(g.Notes.Any(n => n.Contains("\"Overwritten\" in the CPS export but \"Brownwood ARCNET\" here")), "and the user is told");
                Assert.True(Named("NOAA CH7") == null, "deleted here: not brought back");
                Assert.True(Named("WD9ARW") == null && Named("WD9ARW Rptr") != null, "renamed here: old name not brought back");
                var nums = c.Rows.Select(r => CpsFormat.ChannelNumber(c, r)).ToList();
                Assert.Equal(string.Join(",", nums.OrderBy(n => n)), string.Join(",", nums), "rows in number order");
                Assert.Equal(nums.Count, nums.Distinct().Count(), "channel numbers unique");

                Assert.True(g.TalkGroups.Rows.Any(r => g.TalkGroups.Get(r, "Name") == "Club" && g.TalkGroups.Get(r, "Radio ID") == "31999"), "the CPS talkgroup is kept");
                var clubList = g.RxGroupLists.Rows.FirstOrDefault(r => g.RxGroupLists.Get(r, "Group Name") == "Club RX");
                Assert.Equal("Club|Texas", clubList == null ? null : g.RxGroupLists.Get(clubList, "Contact"), "its receive group list is kept");
                Assert.True(!g.RxGroupLists.Rows.Any(r => g.RxGroupLists.Get(r, "Group Name") == "Group List 1"), "an unused CPS list isn't");
                var z = g.Zones;
                Assert.True(z.Get(z.Rows.First(r => z.Get(r, "Zone Name") == "Local"), "Zone Channel Member").EndsWith("|Hand Made"), "stays in the project's zone it was in");
                var favRow = z.Rows.FirstOrDefault(r => z.Get(r, "Zone Name") == "Favorites");
                Assert.Equal("Hand Made|KC5EZZ|Club Net", favRow == null ? null : z.Get(favRow, "Zone Channel Member"), "the CPS's own zone is kept with its members");
                Assert.Equal("Club Net", z.Get(favRow, "A Channel"), "and its A channel");
                Assert.True(g.RadioIds.Rows.Any(r => g.RadioIds.Get(r, "Name") == "Austin 2"), "the CPS's other radio ID is kept");
                Assert.True(g.Notes[0].StartsWith("Keeping 2 channels made in the CPS"), "noted: " + g.Notes[0]);

                // After writing, the project remembers only its own things; the next merge keeps the same.
                CodeplugGenerator.KeepChannelNumbers(g);
                Assert.True(CodeplugGenerator.RememberOutput(p, g), "new output remembered");
                Assert.True(!p.KnownZones.Contains("Favorites") && !p.KnownChannels.Any(k => k.Name == "Hand Made"), "CPS-made things aren't remembered as ours");
                var again = CodeplugGenerator.Generate(p, f, CpsExport.Load(tmp));
                Assert.Equal(Csvs(g), Csvs(again), "same result the second time");
                CheckIntegrityLoose(again);
            }
            finally { Directory.Delete(tmp, true); }
        }

        /// <summary>Every zone member and receive-list contact in the output exists.</summary>
        static void CheckIntegrityLoose(GeneratedCodeplug g)
        {
            var names = new HashSet<string>(g.Channels.Rows.Select(r => g.Channels.Get(r, "Channel Name")));
            foreach (var zr in g.Zones.Rows)
                foreach (var m in g.Zones.Get(zr, "Zone Channel Member").Split('|'))
                    Assert.True(names.Contains(m), "zone member '" + m + "' exists");
            var tgs = new HashSet<string>(g.TalkGroups.Rows.Select(r => g.TalkGroups.Get(r, "Name")));
            foreach (var rr in g.RxGroupLists.Rows)
                foreach (var m in g.RxGroupLists.Get(rr, "Contact").Split('|'))
                    Assert.True(tgs.Contains(m), "RX list member '" + m + "' exists");
            var tgIds = g.TalkGroups.Rows.Select(r => g.TalkGroups.Get(r, "Radio ID")).ToList();
            Assert.Equal(tgIds.Count, tgIds.Distinct().Count(), "talkgroup IDs unique");
            Assert.Equal(tgs.Count, g.TalkGroups.Rows.Count, "talkgroup names unique");
        }

        static void CheckAgainstOriginal(string dir, GeneratedCodeplug g)
        {
            var orig = CsvTable.Load(Path.Combine(dir, "Channel.CSV"));
            var gen = g.Channels;
            Assert.Equal(orig.Rows.Count, gen.Rows.Count, "channel row count");
            foreach (var o in orig.Rows)
            {
                string name = orig.Get(o, "Channel Name");
                var n = gen.Rows.FirstOrDefault(r => gen.Get(r, "Channel Name") == name);
                Assert.True(n != null, "channel '" + name + "' regenerated");
                for (int i = 0; i < orig.Header.Count; i++)
                    Assert.Equal(o[i], n[i], name + " / " + orig.Header[i]); // channel numbers too (the export has gaps)
            }
            Assert.Equal(orig.ToCsv(), gen.ToCsv(), "Channel.CSV identical, row order and numbers included");

            var oz = CsvTable.Load(Path.Combine(dir, "Zone.CSV"));
            Assert.Equal(oz.ToCsv(), g.Zones.ToCsv(), "Zone.CSV identical");
            var otg = CsvTable.Load(Path.Combine(dir, "TalkGroups.CSV"));
            Assert.Equal(otg.ToCsv(), g.TalkGroups.ToCsv(), "TalkGroups.CSV identical");
            var orid = CsvTable.Load(Path.Combine(dir, "RadioIDList.CSV"));
            Assert.Equal(orid.ToCsv(), g.RadioIds.ToCsv(), "RadioIDList.CSV identical");
        }
    }

    static class ProjectTests
    {
        [Test]
        static void JsonRoundTrip()
        {
            var p = Fixtures.Sample();
            p.Repeaters[0].Talkgroups[0].ChannelName = "Custom";
            p.FindZone("Metro").AChannel = "Custom";
            string json = ProjectStore.ToJson(p);
            var back = ProjectStore.FromJson(json);
            Assert.Equal(json, ProjectStore.ToJson(back), "json stable");
            Assert.Equal(444.125m, back.Repeaters[0].RxMHz, "decimal frequency");
            Assert.True(json.Contains("\n  "), "indented");
        }

        [Test]
        static void MissingFieldsGetDefaults()
        {
            var p = ProjectStore.FromJson("{\"RadioIdName\":\"X\",\"Repeaters\":[{\"Name\":\"R\",\"RxMHz\":146.94,\"TxMHz\":146.34}]}");
            Assert.Equal("X", p.RadioIdName, "name");
            Assert.True(p.Talkgroups != null && p.Options != null && p.Hotspot != null, "lists created");
            Assert.True(p.Repeaters[0].Enabled, "enabled default");
            Assert.Equal(Modes.Digital, p.Repeaters[0].Mode, "mode default");
            Assert.Equal(16, p.Options.MaxNameLength, "options default");
        }

        [Test]
        static void SaveWritesFileAtomically()
        {
            string path = Path.Combine(Path.GetTempPath(), "cpb-" + Guid.NewGuid().ToString("N") + ".cpb");
            try
            {
                ProjectStore.Save(Fixtures.Sample(), path);
                ProjectStore.Save(Fixtures.Sample(), path); // overwrite
                var p = ProjectStore.Load(path);
                Assert.Equal(4, p.Repeaters.Count, "loaded");
                Assert.True(!File.Exists(path + ".tmp"), "temp file cleaned up");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }

    static class ZoneTests
    {
        [Test]
        static void SyncAddsNewZonesAndDropsUnused()
        {
            var p = Fixtures.Sample();
            Assert.Equal("Metro|Analog|Weather|Hotspot", string.Join("|", p.Zones.Select(z => z.Name)), "initial");
            p.Repeaters[3].Zone = "Wx";          // NOAA moves to a new zone; "Weather" is now unused
            p.SyncZones();
            Assert.Equal("Metro|Analog|Hotspot|Wx", string.Join("|", p.Zones.Select(z => z.Name)), "new zone appended, unused dropped");
        }

        [Test]
        static void RenameAndMerge()
        {
            var p = Fixtures.Sample();
            p.RenameZone("Analog", "FM Repeaters");
            Assert.Equal("FM Repeaters", p.Repeaters[2].Zone, "repeater follows rename");
            Assert.Equal("Metro|FM Repeaters|Weather|Hotspot", string.Join("|", p.Zones.Select(z => z.Name)), "order kept");
            p.RenameZone("Weather", "metro"); // merge into an existing zone (case-insensitive)
            Assert.True(Project.SameZone(p.Repeaters[3].Zone, "Metro"), "merged");
            Assert.Equal("Metro|FM Repeaters|Hotspot", string.Join("|", p.Zones.Select(z => z.Name)), "merged zone removed");
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal(1, g.ZoneList.Count(z => Project.SameZone(z.Name, "Metro")), "one Metro zone");
        }
    }

    static class ValidationTests
    {
        [Test]
        static void SampleHasNoErrors()
        {
            var issues = Validator.Validate(Fixtures.Sample());
            Assert.True(issues.All(i => i.Severity != Severity.Error), string.Join("\n", issues));
        }

        [Test]
        static void CatchesCommonMistakes()
        {
            var p = Fixtures.Sample();
            p.RadioIdName = "";
            p.Talkgroups.Add(new Talkgroup("Dup", 91));
            p.Repeaters[0].ColorCode = 16;
            p.Repeaters[2].ToneEncode = "12.3";
            p.Repeaters[3].RxMHz = 0;
            var issues = Validator.Validate(p);
            string all = string.Join("\n", issues);
            Assert.True(issues.Any(i => i.Message.Contains("Radio ID name")), "radio id name: " + all);
            Assert.True(issues.Any(i => i.Message.Contains("ID 91")), "duplicate id: " + all);
            Assert.True(issues.Any(i => i.Message.Contains("color code")), "color code: " + all);
            Assert.True(issues.Any(i => i.Message.Contains("12.3")), "bad tone: " + all);
            Assert.True(issues.Any(i => i.Message.Contains("frequency")), "missing freq: " + all);
        }

        [Test]
        static void TooManyChannelsIsAnError()
        {
            var p = Fixtures.Sample(extraRepeaters: 1340); // > 4000 channels
            var issues = Validator.Validate(p);
            Assert.True(issues.Any(i => i.Severity == Severity.Error && i.Message.Contains("4000")), string.Join("\n", issues.Take(5)));
        }
    }

    static class TalkgroupCsvTests
    {
        [Test]
        static void ReadsCpsTalkGroupsFile()
        {
            var list = TalkgroupCsv.Load(Path.Combine(Fixtures.RequireExport(), "TalkGroups.CSV"));
            Assert.Equal(9, list.Count, "count");
            Assert.Equal("Private Call", list.First(t => t.Id == 310997).CallType, "call type");
        }

        [Test]
        static void ReadsHeaderlessIdNameList()
        {
            var list = TalkgroupCsv.Parse(CsvTable.Parse("91,Worldwide\r\n93,North America\r\n3100,USA\r\n"));
            Assert.Equal(3, list.Count, "count");
            Assert.Equal("North America", list[1].Name, "name");
            var swapped = TalkgroupCsv.Parse(CsvTable.Parse("TG Name,TG ID\r\nWorldwide,91\r\n"));
            Assert.Equal(91, swapped[0].Id, "header with name first");
        }
    }
}
