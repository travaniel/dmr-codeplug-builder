using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>Roadmap item 10: GPS zone switching (GpsRoaming.CSV).</summary>
    static class GpsRoamingTests
    {
        [Test]
        static void WritesTheRowTheCpsWrites()
        {
            GpsRoaming.Coordinate(31.46383, out int d, out int m, out int h);
            Assert.Equal("31 27 83", d + " " + m + " " + h, "31.46383 = 31° 27.83'");
            GpsRoaming.Coordinate(-100.437, out d, out m, out h);
            Assert.Equal("100 26 22", d + " " + m + " " + h, "100.437 W = 100° 26.22'");
            GpsRoaming.Coordinate(30.99999, out d, out m, out h);
            Assert.Equal("31 0 0", d + " " + m + " " + h, "rounding carries into the degree");

            var t = GpsRoaming.ToTable(new[] { new GpsZoneEntry { ZoneIndex = 2, Latitude = 31.46383, Longitude = -100.437, RadiusMeters = 25000 } });
            Assert.Equal(32, t.Rows.Count, "always 32 rows");
            var lines = t.ToCsv().Split(new[] { "\r\n" }, StringSplitOptions.None);
            // The row the CPS exported after the entry was made in its editor (CPSnow\gpsroam, HANDOFF 4).
            Assert.Equal("\"1\",\"2\",\"31\",\"0\",\"100\",\"1\",\"27.00\",\"83.00\",\"26.00\",\"22.00\",\"25000\"", lines[1], "the CPS's own row");
            Assert.Equal("\"0\",\"0\",\"0\",\"0\",\"0\",\"0\",\"00.00\",\"00.00\",\"00.00\",\"00.00\",\"0\"", lines[2], "the rest off");
            Assert.Equal(GpsRoaming.BuiltInTemplate().ToCsv().Split('\n')[0], t.ToCsv().Split('\n')[0], "the CPS header");
        }

        [Test]
        static void OneCirclePerPlacedAreaZone()
        {
            var p = Fixtures.Sample();
            Assert.True(!p.Options.GpsZoneSwitching, "off by default");
            Assert.True(CodeplugGenerator.Generate(p, CpsFormat.BuiltIn()).GpsRoaming == null, "not written when off");
            p.Options.GpsZoneSwitching = true;
            var w1 = p.Repeaters.First(r => r.Name == "W1ABC Metro");
            var k2 = p.Repeaters.First(r => r.Name == "K2XYZ");
            var vhf = p.Repeaters.First(r => r.Name == "W1ABC VHF");
            w1.Latitude = 41.0; w1.Longitude = -73.0;
            k2.Latitude = 41.2; k2.Longitude = -73.0;   // 22 km north, same zone
            vhf.Latitude = 42.0; vhf.Longitude = -72.0;
            p.Zones.Add(new ZoneInfo("TG 9") { RuleTalkgroups = new List<int> { 9 } });
            p.SyncZones();
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal("Metro,Analog", string.Join(",", g.GpsEntries.Select(e => e.Zone)), "area zones with placed repeaters only (not the talkgroup zone, not unplaced Weather)");
            var metro = g.GpsEntries[0];
            Assert.Equal(g.ZoneList.FindIndex(z => z.Name == "Metro"), metro.ZoneIndex, "zone index = position in Zone.CSV");
            Assert.True(Math.Abs(metro.Latitude - 41.1) < 0.001, "centre between the two repeaters");
            Assert.Equal(26100, metro.RadiusMeters, "11.1 km to the farthest repeater + 15 km");
            Assert.True(g.ListFileText().Contains("22,\"GpsRoaming.CSV\""), ".LST section 22");

            p.Home = new HomeLocation { Latitude = 42.0, Longitude = -72.0, Country = "US" };
            g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal("Analog,Metro", string.Join(",", g.GpsEntries.Select(e => e.Zone)), "nearest home first");
        }

        [Test]
        static void KeepsTheNearest32()
        {
            var p = Fixtures.Sample(40);
            p.Options.GpsZoneSwitching = true;
            p.Home = new HomeLocation { Latitude = 30.0, Longitude = -97.0, Country = "US" };
            int i = 0;
            foreach (var r in p.Repeaters.Where(r => r.Name.StartsWith("Rpt ")))
            {
                r.Zone = "Z" + i;
                r.Latitude = 30.0 + i * 0.1; r.Longitude = -97.0;
                i++;
            }
            p.SyncZones();
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal(32, g.GpsEntries.Count, "the radio holds 32");
            Assert.Equal("Z0", g.GpsEntries[0].Zone, "nearest first");
            Assert.True(g.GpsEntries.All(e => int.Parse(e.Zone.Substring(1)) < 32), "the farthest ones are left out");
            Assert.True(g.Notes.Any(n => n.Contains("32 entries")), "and the notes say so");
        }
    }
}
