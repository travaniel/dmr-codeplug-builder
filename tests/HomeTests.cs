using System;
using System.Collections.Generic;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>Roadmap item 8: home, distances, zone order by distance, talkgroup order within a repeater.</summary>
    static class HomeTests
    {
        static void Place(Repeater r, double lat, double lon) { r.Latitude = lat; r.Longitude = lon; }

        [Test]
        static void DistanceAndBearing()
        {
            double km = Distances.Km(30.267, -97.743, 32.777, -96.797); // Austin → Dallas
            Assert.True(km > 289 && km < 297, "Austin-Dallas about 293 km, got " + km);
            Assert.Equal("NE", Distances.Compass(Distances.Bearing(30.267, -97.743, 32.777, -96.797) + 20), "compass rounding");
            Assert.Equal("N", Distances.Compass(Distances.Bearing(30.267, -97.743, 32.777, -96.797)), "Dallas is north of Austin");
            Assert.Equal("182 mi N", Distances.Describe(km, 10, true), "miles");
            Assert.Equal("4.2 km W", Distances.Describe(4.2, 270, false), "km, one decimal under 10");
            Assert.Equal("here", Distances.Describe(0.3, 0, true), "under half a mile");
            Assert.True(Distances.UsesMiles("US") && Distances.UsesMiles("GB") && !Distances.UsesMiles("DE"), "units by country");
        }

        [Test]
        static void FindsHomeTownOnTheMap()
        {
            var atlas = GeoAtlas.BuiltIn();
            foreach (var text in new[] { "Brownwood, TX", "Brownwood, Texas", "Brownwood Texas", "Brownwood, Texas, United States" })
            {
                var h = HomeLocation.Parse(atlas, text, "United States");
                Assert.True(h != null, "found '" + text + "'");
                Assert.True(Math.Abs(h.Latitude - 31.71) < 0.1 && Math.Abs(h.Longitude + 98.98) < 0.1, text + " is Brownwood: " + h.Latitude + ", " + h.Longitude);
                Assert.Equal("US", h.Country, "country code");
            }
            Assert.True(HomeLocation.Parse(atlas, "Nowhereville Qqq", "United States") == null, "unknown town");
            Assert.Equal("Texas", HomeLocation.Find(atlas, "Brownwood", "TX", "United States").Place.Split(',').Last().Trim(), "place names the state");
        }

        static Project Located()
        {
            var p = Fixtures.Sample();
            p.Home = new HomeLocation { Place = "Home", Latitude = 31.71, Longitude = -98.99, Country = "US" }; // Brownwood
            var w1 = p.Repeaters.First(r => r.Name == "W1ABC Metro");
            var k2 = p.Repeaters.First(r => r.Name == "K2XYZ");
            var vhf = p.Repeaters.First(r => r.Name == "W1ABC VHF");
            k2.Zone = "Near";
            Place(w1, 32.78, -96.80);   // Dallas, ~145 mi
            Place(k2, 31.46, -100.44);  // San Angelo, ~87 mi
            Place(vhf, 30.27, -97.74);  // Austin, ~124 mi
            p.SyncZones();
            return p;
        }

        [Test]
        static void ZonesSortByUseAndDistance()
        {
            var p = Located();
            p.Zones.Add(new ZoneInfo("TG 9") { RuleTalkgroups = new List<int> { 9 } });
            p.AddToZone("Favorites", new ChannelRef(p.Repeaters[0], p.Repeaters[0].Talkgroups[0]));
            p.SyncZones();
            Assert.True(ZoneOrder.Sort(p), "order changed");
            Assert.Equal("Hotspot|Favorites|Near|Analog|Metro|Weather|TG 9", string.Join("|", p.Zones.Select(z => z.Name)),
                         "hotspot, favorites, areas nearest first (unplaced Weather after them), talkgroup zones");
            Assert.True(!ZoneOrder.Sort(p), "sorting again changes nothing");
            Assert.Equal("148 mi NE", p.DistanceText(p.Repeaters[0]), "Brownwood to Dallas");

            var q = Located();
            q.Home = null;
            ZoneOrder.Sort(q);
            Assert.Equal("Hotspot|Metro|Analog|Weather|Near", string.Join("|", q.Zones.Select(z => z.Name)), "no home: areas keep their order");
        }

        [Test]
        static void TalkgroupZoneListsNearestFirstAndHonoursTheRadius()
        {
            var p = Located();
            p.Zones.Add(new ZoneInfo("TG 9") { RuleTalkgroups = new List<int> { 9 } });
            p.SyncZones();
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            var row = g.Zones.Rows.First(r => g.Zones.Get(r, "Zone Name") == "TG 9");
            Assert.Equal("K2XYZ Local|W1ABC Local", g.Zones.Get(row, "Zone Channel Member"), "San Angelo before Dallas");
            p.FindZone("TG 9").RuleMiles = 100;
            Assert.Equal(1, p.ZoneChannels("TG 9").Count, "only within 100 miles");
            p.Home = null;
            Assert.Equal(2, p.ZoneChannels("TG 9").Count, "no home: no radius, list order");
            Assert.True(p.ZoneChannels("TG 9")[0].Repeater.Name == "W1ABC Metro", "list order without a home");
        }

        [Test]
        static void TalkgroupsSortLocalFirst()
        {
            var p = new Project();
            foreach (var t in new[] { new Talkgroup("Worldwide", 91), new Talkgroup("Texas", 3148), new Talkgroup("Local", 9), new Talkgroup("Parrot", 310997, CallTypes.Private),
                                      new Talkgroup("TX North", 31480), new Talkgroup("Club", 1234567), new Talkgroup("USA", 3100), new Talkgroup("Own", 311178) })
                p.Talkgroups.Add(t);
            var r = Repeater.NewDigital("W5X");
            r.SourceId = 311178;
            foreach (int id in new[] { 91, 3148, 9, 310997, 31480, 1234567, 3100, 311178 }) r.Talkgroups.Add(new RepeaterTalkgroup(id, 1));
            p.Repeaters.Add(r);
            Assert.True(TalkgroupOrder.Sort(p, r), "sorted");
            Assert.Equal("9,311178,3148,31480,1234567,91,3100,310997", string.Join(",", r.Talkgroups.Select(e => e.TalkgroupId)),
                         "local, own ID, state, state region, regional, wide area, private call");
            Assert.True(!TalkgroupOrder.Sort(p, r), "stable");
        }

        [Test]
        static void HomeIsOptional()
        {
            var p = Fixtures.Sample();
            string json = ProjectStore.ToJson(p);
            Assert.True(!json.Contains("\"Home\""), "no home written when not set");
            p.Home = new HomeLocation { Place = "Brownwood, Texas", Latitude = 31.7, Longitude = -98.99, Country = "US" };
            var back = ProjectStore.FromJson(ProjectStore.ToJson(p));
            Assert.Equal("Brownwood, Texas", back.Home.Place, "home survives saving");
            Assert.Equal(CodeplugGenerator.Generate(Fixtures.Sample(), CpsFormat.BuiltIn()).Zones.ToCsv(), CodeplugGenerator.Generate(back, CpsFormat.BuiltIn()).Zones.ToCsv(),
                         "a home alone changes no output");
        }
    }
}
