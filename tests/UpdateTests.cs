using System;
using System.Collections.Generic;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>Roadmap item 12: Check for updates (the pure comparison and apply).</summary>
    static class UpdateTests
    {
        static OnlineRepeater Listing(string call, string city, int id, decimal rx, decimal tx, int cc, string network, params int[] tgs)
        {
            var l = new OnlineRepeater { Callsign = call, City = city, State = "Texas", Country = "United States", DmrId = id, RxMHz = rx, TxMHz = tx, ColorCode = cc, Network = network };
            foreach (int t in tgs) l.Talkgroups.Add(new OnlineTalkgroup { Id = t, Slot = Project.DefaultSlot(t), Description = "" });
            return l;
        }

        static Project Tracked()
        {
            var p = new Project();
            p.Talkgroups.Add(new Talkgroup("Local", 9));
            p.Talkgroups.Add(new Talkgroup("Texas", 3148));
            p.Talkgroups.Add(new Talkgroup("Mine", 31480));
            var a = Repeater.NewDigital("W5AAA Austin");
            a.Prefix = "W5AAA"; a.Zone = "Travis Co"; a.RxMHz = 444.100m; a.TxMHz = 449.100m; a.ColorCode = 1; a.SourceId = 311001;
            a.State = "Texas"; a.Country = "United States"; a.AreaCode = "US-48453";
            a.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
            a.Talkgroups.Add(new RepeaterTalkgroup(31480, 1));
            var b = Repeater.NewDigital("W5BBB Austin");
            b.Prefix = "W5BBB"; b.Zone = "Travis Co"; b.RxMHz = 442.000m; b.TxMHz = 447.000m; b.SourceId = 311002; b.State = "Texas"; b.AreaCode = "US-48453";
            b.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
            var c = Repeater.NewDigital("W5CCC Austin");
            c.Prefix = "W5CCC"; c.Zone = "Travis Co"; c.RxMHz = 443.000m; c.TxMHz = 448.000m; c.SourceId = 311003; c.State = "Texas"; c.AreaCode = "US-48453"; c.OffAirSince = "2022-07-19";
            c.Talkgroups.Add(new RepeaterTalkgroup(9, 2));
            var hand = Repeater.NewDigital("Typed in");
            hand.RxMHz = 441.000m; hand.TxMHz = 446.000m;
            p.Repeaters.AddRange(new[] { a, b, c, hand });
            p.Normalize();
            return p;
        }

        [Test]
        static void FindsChangesAndAppliesTheTickedOnes()
        {
            var p = Tracked();
            var now = new DateTime(2026, 10, 8);
            var county = new GeoLocation { County = null };
            var listings = new Dictionary<int, OnlineRepeater>
            {
                [311001] = Listing("W5AAA", "Austin", 311001, 444.150m, 449.150m, 3, "BrandMeister", 9, 3148), // moved, new CC, 3148 added, 31480 dropped
                [311002] = null,                                                                               // delisted
                [311003] = Listing("W5CCC", "Austin", 311003, 443.000m, 448.000m, 1, "BrandMeister", 9),          // back on the air
            };
            var lastSeen = new Dictionary<int, DateTime?> { [311001] = new DateTime(2024, 1, 1), [311003] = now.AddDays(-2) };
            var items = UpdateCheck.Compare(p, listings, lastSeen, null, null, new Dictionary<int, string> { [3148] = "Texas" }, now);
            string Kinds() { return string.Join(",", items.Select(i => i.Kind)); }
            Assert.Equal("Frequency,ColorCode,OffAir,TalkgroupsAdded,TalkgroupsDropped,Delisted,BackOnAir", Kinds(), "differences found");
            Assert.True(!items.Single(i => i.Kind == UpdateKind.TalkgroupsDropped).Ticked, "dropped talkgroups are not ticked");
            Assert.True(!items.Single(i => i.Kind == UpdateKind.Delisted).Ticked, "delisted is not ticked");
            Assert.True(items.All(i => i.Repeater == null || i.Repeater.Name != "Typed in"), "hand-typed repeaters aren't checked");

            var notes = UpdateCheck.Apply(p, items.Where(i => i.Ticked), null);
            var a = p.Repeaters[0];
            Assert.Equal(444.150m, a.RxMHz, "new output");
            Assert.Equal(3, a.ColorCode, "new color code");
            Assert.Equal("9,31480,3148", string.Join(",", a.Talkgroups.Select(e => e.TalkgroupId)), "3148 added, 31480 kept (not ticked)");
            Assert.Equal("2024-01-01", a.OffAirSince, "marked off the air");
            Assert.True(p.Repeaters[1].Enabled, "delisted stays on (not ticked)");
            Assert.True(p.Repeaters[2].OffAirSince == null, "back on the air");
            Assert.True(notes.Count >= 5, "notes say what changed");

            // Nothing left after applying everything.
            UpdateCheck.Apply(p, items.Where(i => !i.Ticked), null);
            Assert.True(!p.Repeaters[1].Enabled, "delisted switched off when ticked");
            var again = UpdateCheck.Compare(p, listings, lastSeen, null, null, null, now);
            Assert.Equal("Delisted", string.Join(",", again.Select(i => i.Kind)), "only the delisted note is left");
        }

        [Test]
        static void BrandMeisterStaticTalkgroupsWinForBrandMeisterOnlyRepeaters()
        {
            var p = Tracked();
            var listings = new Dictionary<int, OnlineRepeater> { [311001] = Listing("W5AAA", "Austin", 311001, 444.100m, 449.100m, 1, "BrandMeister", 9) };
            var statics = new Dictionary<int, List<OnlineTalkgroup>> { [311001] = new List<OnlineTalkgroup> { new OnlineTalkgroup { Id = 31480, Slot = 1 }, new OnlineTalkgroup { Id = 3148, Slot = 1 } } };
            var items = UpdateCheck.Compare(p, listings, null, statics, null, null, DateTime.UtcNow);
            var added = items.Single(i => i.Kind == UpdateKind.TalkgroupsAdded);
            Assert.True(added.Text.Contains("BrandMeister lists") && added.Talkgroups.Single().Id == 3148, "3148 from BrandMeister: " + added.Text);
            Assert.True(items.Single(i => i.Kind == UpdateKind.TalkgroupsDropped).Dropped.Single().TalkgroupId == 9, "9 isn't static on BrandMeister");
        }

        [Test]
        static void NewRepeatersOnlyInTheProjectsCounties()
        {
            var p = Tracked();
            var atlas = GeoAtlas.BuiltIn();
            var travis = Listing("K5NEW", "Austin", 311999, 441.500m, 446.500m, 1, "BrandMeister", 9);
            travis.Location = atlas.Locate("Austin", "Texas", "United States");
            var elsewhere = Listing("K5FAR", "Amarillo", 311998, 441.500m, 446.500m, 1, "BrandMeister", 9);
            elsewhere.Location = atlas.Locate("Amarillo", "Texas", "United States");
            var known = Listing("W5BBB", "Austin", 311002, 442.000m, 447.000m, 1, "BrandMeister", 9);
            known.Location = travis.Location;
            Assert.Equal("US-48453", travis.Location.County?.Code, "Austin is in Travis County");
            var items = UpdateCheck.Compare(p, new Dictionary<int, OnlineRepeater>(), null, null, new[] { travis, elsewhere, known }, null, DateTime.UtcNow);
            var n = items.Single(i => i.Kind == UpdateKind.NewRepeater);
            Assert.True(n.Listing == travis && n.Zone == "Travis Co" && !n.Ticked, "the Austin one, into Travis Co, unticked");
            UpdateCheck.Apply(p, new[] { n }, null);
            Assert.True(p.Repeaters.Any(r => r.SourceId == 311999 && r.Zone == "Travis Co"), "added to the zone");
        }

        [Test]
        static void AreasAreTheTrackedRepeatersStates()
        {
            var p = Tracked();
            Assert.Equal("Texas", string.Join(",", UpdateCheck.Areas(p)), "states to download");
            Assert.Equal(3, UpdateCheck.Tracked(p).Count, "repeaters with a RadioID listing");
        }
    }
}
