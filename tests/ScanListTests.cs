using System;
using System.Collections.Generic;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>Roadmap item 9: the Favorites scan list with a priority channel, the Local FM list, Scan List 2-8.</summary>
    static class ScanListTests
    {
        static List<string> Row(CsvTable t, string keyColumn, string key)
        {
            var row = t.Rows.FirstOrDefault(r => t.Get(r, keyColumn) == key);
            Assert.True(row != null, keyColumn + " '" + key + "' written");
            return row;
        }

        static Project WithFavorites()
        {
            var p = Fixtures.Sample();
            p.Home = new HomeLocation { Place = "Home", Latitude = 41.0, Longitude = -73.0, Country = "US" };
            var w1 = p.Repeaters.First(r => r.Name == "W1ABC Metro");
            var vhf = p.Repeaters.First(r => r.Name == "W1ABC VHF");
            w1.Latitude = 41.1; w1.Longitude = -73.1;   // ~9 mi
            vhf.Latitude = 41.3; vhf.Longitude = -73.3; // ~26 mi
            p.AddToZone("Favorites", new ChannelRef(w1, w1.Talkgroups[0]));
            p.AddToZone("Favorites", new ChannelRef(vhf, null));
            p.SyncZones();
            return p;
        }

        [Test]
        static void FavoritesListWatchesTheHomeChannel()
        {
            var p = WithFavorites();
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            var fav = Row(g.ScanLists, "Scan List Name", "Favorites");
            Assert.Equal(ScanPriorityValues.Select1, g.ScanLists.Get(fav, "Priority Channel Select"), "priority on");
            Assert.Equal("W1ABC Local", g.ScanLists.Get(fav, "Priority Channel 1"), "no hotspot in the list: the nearest member");
            var ch = Row(g.Channels, "Channel Name", "W1ABC Local");
            Assert.Equal("Metro", g.Channels.Get(ch, "Scan List 1"), "own zone's list first");
            Assert.Equal("Favorites", g.Channels.Get(ch, "Scan List 2"), "favorites second");
            Assert.True(g.Channels.Get(Row(g.Channels, "Channel Name", "K2XYZ Local"), "Scan List 2") == "", "non-favourites get no second list");
            var metro = Row(g.ScanLists, "Scan List Name", "Metro");
            Assert.Equal("Off", g.ScanLists.Get(metro, "Priority Channel Select"), "area lists keep no priority");

            p.AddToZone("Favorites", new ChannelRef(p.Hotspot, p.Hotspot.Talkgroups[0]));
            g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal("HS Worldwide", g.ScanLists.Get(Row(g.ScanLists, "Scan List Name", "Favorites"), "Priority Channel 1"), "the hotspot wins");

            p.Options.FavoritesScanPriority = false;
            g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal("Off", g.ScanLists.Get(Row(g.ScanLists, "Scan List Name", "Favorites"), "Priority Channel Select"), "option off: no priority");
            Assert.True(g.Channels.Get(Row(g.Channels, "Channel Name", "W1ABC Local"), "Scan List 2") == "", "option off: one list per channel, as before");
        }

        [Test]
        static void LocalFmListHasTheNearestAnalogRepeaters()
        {
            var p = WithFavorites();
            var far = Repeater.NewAnalog("W1FAR VHF");
            far.Zone = "Analog"; far.RxMHz = 147.000m; far.TxMHz = 147.600m; far.Latitude = 43.0; far.Longitude = -73.0; // ~138 mi
            p.Repeaters.Add(far);
            p.SyncZones();
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            var local = Row(g.ScanLists, "Scan List Name", "Local FM");
            Assert.Equal("W1ABC VHF", g.ScanLists.Get(local, "Scan Channel Member"), "within 50 miles, weather (RX only) left out");
            var ch = Row(g.Channels, "Channel Name", "W1ABC VHF");
            Assert.Equal("Analog|Favorites|Local FM", string.Join("|", new[] { 1, 2, 3 }.Select(k => g.Channels.Get(ch, "Scan List " + k))), "its lists in order");

            p.Options.LocalAnalogMiles = 200;
            g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal("W1ABC VHF|W1FAR VHF", g.ScanLists.Get(Row(g.ScanLists, "Scan List Name", "Local FM"), "Scan Channel Member"), "nearest first");

            p.Home = null;
            g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.True(!g.ScanLists.Rows.Any(r => g.ScanLists.Get(r, "Scan List Name") == "Local FM"), "no home, no Local FM list");
        }

        [Test]
        static void OlderProjectsKeepOneScanListPerChannel()
        {
            var json = ProjectStore.ToJson(WithFavorites());
            Assert.True(json.Contains("\"FavoritesScanPriority\""), "the option is saved");
            var old = ProjectStore.FromJson("{\"Options\":{\"ScanListPerZone\":true,\"MaxNameLength\":16}}"); // a 1.4 file
            Assert.True(!old.Options.FavoritesScanPriority && !old.Options.LocalAnalogScanList, "a file without the options has them off");
            Assert.True(new Project().Options.FavoritesScanPriority && new Project().Options.LocalAnalogScanList, "new projects have them on");
            string dir = Fixtures.RequireExport();
            var imported = CpsImporter.Import(dir).Project;
            Assert.True(!imported.Options.FavoritesScanPriority && !imported.Options.LocalAnalogScanList, "CPS imports have them off");
        }
    }
}
