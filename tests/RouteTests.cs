using System;
using System.Collections.Generic;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>Roadmap item 11: the route builder.</summary>
    static class RouteTests
    {
        static RoutePlan Route(string from, string to, string highway = null)
        {
            var atlas = GeoAtlas.BuiltIn();
            var a = HomeLocation.Parse(atlas, from, "United States");
            var b = HomeLocation.Parse(atlas, to, "United States");
            Assert.True(a != null && b != null, from + " and " + to + " found");
            return RoutePlanner.Find(atlas, a.Latitude, a.Longitude, b.Latitude, b.Longitude, highway);
        }

        [Test]
        static void FindsRoutesOnTheHighways()
        {
            var started = DateTime.Now;
            var i35 = Route("Austin, TX", "Dallas, TX");
            Console.WriteLine("        Austin-Dallas: " + RoutePlanner.Describe(i35, "Austin", "Dallas", true) + ", " + i35.Points.Count + " points, " + (DateTime.Now - started).TotalMilliseconds.ToString("0") + " ms");
            Assert.True(i35.OnRoads, "on the roads");
            Assert.True(i35.LengthKm > 290 && i35.LengthKm < 360, "about 195 mi by road: " + i35.LengthKm);
            Assert.True(i35.Via.Contains("I-35"), "via I-35: " + string.Join(", ", i35.Via));

            var i20 = Route("Abilene, TX", "Cisco, TX", "I-20");
            Console.WriteLine("        Abilene-Cisco on I-20: " + RoutePlanner.Describe(i20, "Abilene", "Cisco", true));
            Assert.True(i20.OnRoads && !i20.HighwayMissing && i20.Via.SequenceEqual(new[] { "I-20" }), "only I-20: " + string.Join(", ", i20.Via));

            // US 183 at Brownwood is in Natural Earth's roads but unlabeled: the route follows any highway and says so.
            var us183 = Route("Brownwood, TX", "Cisco, TX", "183");
            Console.WriteLine("        Brownwood-Cisco, 183: " + RoutePlanner.Describe(us183, "Brownwood", "Cisco", true));
            Assert.True(us183.OnRoads && us183.HighwayMissing, "on the roads, highway missing");
            Assert.True(us183.LengthKm > 65 && us183.LengthKm < 110, "about 50 mi: " + us183.LengthKm);
        }

        [Test]
        static void HighwayNamesMatchHowPeopleTypeThem()
        {
            Assert.True(RoutePlanner.MatchesHighway("US 183", "183"), "183");
            Assert.True(RoutePlanner.MatchesHighway("US 183", "Hwy 183"), "Hwy 183");
            Assert.True(RoutePlanner.MatchesHighway("US 183", "US-183"), "US-183");
            Assert.True(RoutePlanner.MatchesHighway("I-35", "I35"), "I35");
            Assert.True(!RoutePlanner.MatchesHighway("I-35", "US 35"), "not another road with that number");
            Assert.True(!RoutePlanner.MatchesHighway("US 183", "83"), "not another number");
        }

        [Test]
        static void RepeatersAlongTheRouteInDrivingOrder()
        {
            var line = new List<double[]> { new[] { 31.0, -99.0 }, new[] { 32.0, -99.0 } }; // 111 km north
            var atlas = GeoAtlas.BuiltIn();
            OnlineRepeater At(string call, double lat, double lon) { return new OnlineRepeater { Callsign = call, Location = new GeoLocation { Lat = lat, Lon = lon } }; }
            var stops = RoutePlanner.Along(line, new[] { At("NORTH", 31.9, -99.05), At("SOUTH", 31.1, -98.95), At("FAR", 31.5, -98.0) }, 15);
            Assert.Equal("SOUTH,NORTH", string.Join(",", stops.Select(s => s.Listing.Callsign)), "in driving order, the far one left out");
            Assert.True(Math.Abs(stops[0].AlongKm - 11.1) < 0.5 && stops[0].OffKm < 5, "along and off: " + stops[0].AlongKm + " / " + stops[0].OffKm);
            Assert.Equal("Texas", string.Join(",", RoutePlanner.StatesCrossed(atlas, line).Select(s => s.Name)), "states crossed");
            Assert.True(RoutePlanner.Simplify(new List<double[]> { new[] { 0.0, 0.0 }, new[] { 0.5, 0.0001 }, new[] { 1.0, 0.0 } }).Count == 2, "simplified");
        }

        [Test]
        static void RouteZoneAndGpsInDrivingOrder()
        {
            var p = Fixtures.Sample();
            p.Options.GpsZoneSwitching = true;
            p.Home = new HomeLocation { Latitude = 40.0, Longitude = -70.0, Country = "US" }; // far from both, east
            var w1 = p.Repeaters.First(r => r.Name == "W1ABC Metro");
            var vhf = p.Repeaters.First(r => r.Name == "W1ABC VHF");
            w1.Latitude = 32.0; w1.Longitude = -99.0;   // Metro: the north end
            vhf.Latitude = 31.0; vhf.Longitude = -99.0; // Analog: the south end
            var route = new List<double[]> { new[] { 30.9, -99.0 }, new[] { 32.1, -99.0 } }; // driving north
            var zone = RouteBuilder.Apply(p, "Hwy 999", route, 15, new[] { vhf, w1 });
            Assert.True(zone.IsRoute && zone.Kind == ZoneKinds.Favorites, "a route zone");
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            var row = g.Zones.Rows.First(r => g.Zones.Get(r, "Zone Name") == "Hwy 999");
            Assert.Equal("W1ABC VHF|W1ABC Local|W1ABC Worldwide|W1ABC USA Natl|W1ABC A Very Lon|W1ABC Parrot", g.Zones.Get(row, "Zone Channel Member"), "driving order");
            Assert.Equal("Analog,Metro", string.Join(",", g.GpsEntries.Select(e => e.Zone)), "GPS: along the route first, in driving order");
            var back = ProjectStore.FromJson(ProjectStore.ToJson(p));
            Assert.True(back.FindZone("Hwy 999").IsRoute, "the route is saved");
        }
    }
}
