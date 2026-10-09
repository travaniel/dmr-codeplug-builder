using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>Roadmap item 7: zones as views (favourites, talkgroup zones, a channel in several zones).</summary>
    static class ZoneViewTests
    {
        static List<string> ZoneMembers(GeneratedCodeplug g, string zone)
        {
            var row = g.Zones.Rows.FirstOrDefault(r => g.Zones.Get(r, "Zone Name") == zone);
            Assert.True(row != null, "zone '" + zone + "' written");
            return g.Zones.Get(row, "Zone Channel Member").Split('|').ToList();
        }

        static ChannelRef Ch(Project p, string repeater, int talkgroupId = 0)
        {
            var r = p.AllRepeaters().First(x => x.Name == repeater);
            return talkgroupId == 0 ? new ChannelRef(r, null) : new ChannelRef(r, r.Talkgroups.First(e => e.TalkgroupId == talkgroupId));
        }

        static void CheckMembersExist(GeneratedCodeplug g)
        {
            var names = new HashSet<string>(g.Channels.Rows.Select(r => g.Channels.Get(r, "Channel Name")));
            foreach (var zr in g.Zones.Rows)
            {
                var members = g.Zones.Get(zr, "Zone Channel Member").Split('|');
                foreach (var m in members) Assert.True(names.Contains(m), "zone member '" + m + "' exists");
                Assert.Equal(members.Length, members.Distinct().Count(), "no channel twice in zone " + g.Zones.Get(zr, "Zone Name"));
            }
        }

        [Test]
        static void FavoritesZoneHoldsChannelsFromOtherZones()
        {
            var p = Fixtures.Sample();
            p.Options.FavoritesScanPriority = false; // item 9 gives favourites a second scan list; here only the zones matter
            var before = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.True(p.AddToZone("Favorites", Ch(p, "K2XYZ", 93)), "added");
            Assert.True(p.AddToZone("Favorites", Ch(p, "W1ABC VHF")), "analog added");
            Assert.True(p.AddToZone("Favorites", new ChannelRef(p.Hotspot, p.Hotspot.Talkgroups[0])), "hotspot channel added");
            Assert.True(!p.AddToZone("Favorites", Ch(p, "W1ABC VHF")), "already there");
            p.SyncZones();
            Assert.Equal(ZoneKinds.Favorites, p.FindZone("Favorites").Kind, "new zone is a Favorites zone");
            Assert.Equal("Metro|Analog|Weather|Hotspot|Favorites", string.Join("|", p.Zones.Select(z => z.Name)), "the zone stays with no repeater of its own");

            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            CheckMembersExist(g);
            Assert.Equal(before.Channels.ToCsv(), g.Channels.ToCsv(), "channels unchanged: a zone only refers to them");
            Assert.Equal("K2XYZ N America|W1ABC VHF|HS Worldwide", string.Join("|", ZoneMembers(g, "Favorites")), "favourites in the order added");
            Assert.True(ZoneMembers(g, "Metro").Contains("K2XYZ N America"), "still in its own zone");
            Assert.True(ZoneMembers(g, "Analog").Contains("W1ABC VHF"), "analog still in its own zone");

            Assert.True(p.MoveZoneMember("Favorites", Ch(p, "W1ABC VHF"), -1), "moved up");
            Assert.True(p.RemoveFromZone("Favorites", Ch(p, "K2XYZ", 93)), "removed");
            Assert.True(!p.RemoveFromZone("Metro", Ch(p, "K2XYZ", 93)), "a channel stays in its repeater's own zone");
            g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal("W1ABC VHF|HS Worldwide", string.Join("|", ZoneMembers(g, "Favorites")), "after move and remove");
            Assert.Equal("Favorites", string.Join("|", p.OtherZonesOf(Ch(p, "W1ABC VHF"))), "other zones of a channel");
        }

        [Test]
        static void TalkgroupZoneCollectsItsTalkgroupFromEveryRepeater()
        {
            var p = Fixtures.Sample();
            p.Zones.Add(new ZoneInfo("Local 9") { RuleTalkgroups = new List<int> { 9 } });
            p.Zones.Add(new ZoneInfo("Wide") { RuleTalkgroups = new List<int> { 91, 3100 }, RuleZones = new List<string> { "Hotspot" } });
            p.SyncZones();
            Assert.Equal(ZoneKinds.Talkgroup, p.ZoneKindOf(p.FindZone("Local 9")), "kind from the rule");
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            CheckMembersExist(g);
            Assert.Equal("W1ABC Local|K2XYZ Local", string.Join("|", ZoneMembers(g, "Local 9")), "TG 9 on both repeaters");
            Assert.Equal("HS Worldwide|HS USA Natl", string.Join("|", ZoneMembers(g, "Wide")), "limited to the hotspot's zone");

            p.RenameZone("Hotspot", "Home");
            Assert.Equal("Home", p.FindZone("Wide").RuleZones.Single(), "rule follows a zone rename");
            p.ChangeTalkgroupId(9, 8);
            Assert.Equal(8, p.FindZone("Local 9").RuleTalkgroups.Single(), "rule follows a talkgroup ID change");
            p.DeleteTalkgroups(p.Talkgroups.Where(t => t.Id == 91 || t.Id == 3100).ToList());
            Assert.True(p.FindZone("Wide").RuleTalkgroups == null, "deleted talkgroups leave the rule");
        }

        [Test]
        static void MembersFollowEditsAndSurviveSaving()
        {
            var p = Fixtures.Sample();
            p.AddToZone("Favorites", Ch(p, "W1ABC Metro", 91));
            p.AddToZone("Favorites", Ch(p, "K2XYZ", 9));
            var back = ProjectStore.FromJson(ProjectStore.ToJson(p));
            Assert.Equal(CodeplugGenerator.Generate(p, CpsFormat.BuiltIn()).Zones.ToCsv(), CodeplugGenerator.Generate(back, CpsFormat.BuiltIn()).Zones.ToCsv(), "same zones after save/load");

            p.ChangeTalkgroupId(91, 92);
            Assert.Equal(2, p.ZoneChannels("Favorites").Count, "member follows a talkgroup ID change");
            p.Repeaters.Remove(p.Repeaters.First(r => r.Name == "K2XYZ"));
            p.SyncZones();
            Assert.Equal(1, p.FindZone("Favorites").Members.Count, "a deleted repeater's channel leaves the zone");
            p.Repeaters[0].Enabled = false;
            Assert.Equal(0, p.ZoneChannels("Favorites").Count, "a switched-off repeater's channel isn't written");
            Assert.Equal(1, p.FindZone("Favorites").Members.Count, "but stays listed");
        }

        [Test]
        static void OlderProjectsDontChange()
        {
            var p = Fixtures.Sample();
            string json = ProjectStore.ToJson(p);
            foreach (var word in new[] { "\"Members\"", "\"Kind\"", "\"RuleTalkgroups\"" })
                Assert.True(!json.Contains(word), word + " not written for a project that doesn't use it");
            Assert.True(ProjectStore.FromJson(json).AllRepeaters().All(r => r.Id == null), "repeaters get no id until a zone refers to them");
            Assert.Equal(ZoneKinds.Area, p.ZoneKindOf(p.FindZone("Metro")), "area zone");
            var q = new Project();
            Presets.AddNoaaWeather(q, "Weather");
            Assert.Equal(ZoneKinds.Utility, q.ZoneKindOf(q.FindZone("Weather")), "a zone of presets counts as utility");
        }

        /// <summary>
        /// The user's codeplug with a Favorites zone, a channel shared into another area zone and a zone in its own order:
        /// import and generate give back the same Channel.CSV and Zone.CSV.
        /// </summary>
        [Test]
        static void RoundTripKeepsChannelsInSeveralZones()
        {
            string src = Fixtures.RequireExport();
            string dir = Path.Combine(Path.GetTempPath(), "cpb-zones-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                foreach (var f in Directory.GetFiles(src, "*.CSV")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                var z = CsvTable.Load(Path.Combine(dir, "Zone.CSV"));
                string Members(string zone) { return z.Get(z.Rows.First(r => z.Get(r, "Zone Name") == zone), "Zone Channel Member"); }
                void SetMembers(string zone, string members) { z.Set(z.Rows.First(r => z.Get(r, "Zone Name") == zone), members, "Zone Channel Member"); }
                var home = Members("Home").Split('|').ToList();
                home.Reverse();                                        // not the order the generator writes the hotspot's channels
                SetMembers("Home", string.Join("|", home));
                SetMembers("Hwy 183", Members("Hwy 183") + "|KC5EZZ"); // a channel of San Angelo DMR shared into another area zone
                var fav = z.NewRow(z.Rows[0]);
                z.Set(fav, (z.Rows.Count + 1).ToString(), "No.");
                z.Set(fav, "Favorites", "Zone Name");
                z.Set(fav, "Texas|KC5EZZ|K5BWD VHF|NOAA CH3", "Zone Channel Member");
                z.Set(fav, "KC5EZZ", "A Channel");
                z.Set(fav, "Texas", "B Channel");
                z.Rows.Add(fav);
                z.Save(Path.Combine(dir, "Zone.CSV"));

                var r = CpsImporter.Import(dir);
                var p = r.Project;
                Assert.Equal("San Angelo DMR", p.Repeaters.Single(x => x.Name == "KC5EZZ").Zone, "a shared channel keeps its first zone");
                Assert.Equal(ZoneKinds.Favorites, p.ZoneKindOf(p.FindZone("Favorites")), "a zone of only shared channels is Favorites");
                Assert.True(r.Notes.Any(n => n.Contains("more than one zone")), "the import says so");

                p.Options.RxGroupListPerRepeater = false;
                var format = CpsFormat.FromFolder(src);
                var g = CodeplugGenerator.Generate(p, format);
                Assert.Equal(CsvTable.Load(Path.Combine(src, "Channel.CSV")).ToCsv(), g.Channels.ToCsv(), "Channel.CSV unchanged");
                Assert.Equal(z.ToCsv(), g.Zones.ToCsv(), "Zone.CSV with shared channels identical");
                Assert.Equal(CsvTable.Load(Path.Combine(src, "TalkGroups.CSV")).ToCsv(), g.TalkGroups.ToCsv(), "TalkGroups.CSV identical");

                var p2 = ProjectStore.FromJson(ProjectStore.ToJson(p));
                Assert.Equal(g.Zones.ToCsv(), CodeplugGenerator.Generate(p2, format).Zones.ToCsv(), "same after save/load");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
