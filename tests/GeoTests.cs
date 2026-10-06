using System;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>The built-in map atlas, placing RadioID listings on it, zone talkgroup sets and automatic zones.</summary>
    static class GeoTests
    {
        static GeoAtlas Atlas => GeoAtlas.BuiltIn();

        [Test]
        static void AtlasLoadsWithParentsAndOutlines()
        {
            var a = Atlas;
            Assert.True(a.Countries.Count > 200, "countries: " + a.Countries.Count);
            Assert.True(a.States.Count > 3000, "states/provinces: " + a.States.Count);
            Assert.True(a.Counties.Count > 3000, "US counties: " + a.Counties.Count);
            Assert.True(a.Places.Count > 50000, "places: " + a.Places.Count);
            var tx = a.Find("US-TX");
            Assert.Equal("Texas", tx?.Name, "Texas");
            Assert.Equal("US", tx.Parent?.Code, "Texas is in the US");
            var tomGreen = a.Counties.FirstOrDefault(c => c.Name == "Tom Green County");
            Assert.True(tomGreen != null && tomGreen.Parent == tx, "Tom Green County is in Texas");
            Assert.Equal("US", tomGreen.CountryCode, "county's country code");
            Assert.True(tomGreen.Contains(-100.437, 31.464), "San Angelo is inside Tom Green County");
            Assert.True(!tomGreen.Contains(-97.743, 30.267), "Austin is not");
            Assert.True(a.Countries.All(c => c.Rings.Length > 0), "every country has an outline");
            Assert.Equal(null, a.Find("AQ"), "no Antarctica");
        }

        [Test]
        static void FindsCountriesByTheNamesRadioIdUses()
        {
            foreach (var kv in new[] { ("United States", "US"), ("USA", "US"), ("Canada", "CA"), ("Germany", "DE"), ("Deutschland", "DE"),
                                       ("United Kingdom", "GB"), ("UK", "GB"), ("England", "GB"), ("Korea, Republic of", "KR"), ("France", "FR"),
                                       ("Norway", "NO"), ("Netherlands", "NL"), ("Australia", "AU"), ("Brazil", "BR"), ("Puerto Rico", "PR") })
                Assert.Equal(kv.Item2, Atlas.FindCountry(kv.Item1)?.Code, kv.Item1);
            Assert.Equal(null, Atlas.FindCountry("Atlantis"), "unknown country");
        }

        [Test]
        static void LocatesUsRepeatersToTheirCounty()
        {
            var loc = Atlas.Locate("San Angelo", "Texas", "United States");
            Assert.Equal(LocationPrecision.City, loc.Precision, "precision");
            Assert.Equal("US-TX", loc.State?.Code, "state");
            Assert.Equal("Tom Green County", loc.County?.Name, "county");
            Assert.True(Math.Abs(loc.Lat.Value - 31.46) < 0.1 && Math.Abs(loc.Lon.Value + 100.45) < 0.1, "point");

            Assert.Equal("St. Louis city", Atlas.Locate("Saint Louis", "Missouri", "United States").County?.Name, "Saint = St.");
            Assert.Equal("US-IL", Atlas.Locate("Springfield", "Illinois", "United States").State?.Code, "same name in many states: stays in the listed one");
            Assert.Equal("Dallas County", Atlas.Locate("Dallas/Fort Worth", "Texas", "United States").County?.Name, "A/B tries each part");
            Assert.Equal("Harris County", Atlas.Locate("Houston", "TX", "USA").County?.Name, "state abbreviation and USA");
            Assert.Equal("Tom Green County", Atlas.Locate("San Angelo", "Texas", "").County?.Name, "blank country with a US state");

            var unknownCity = Atlas.Locate("Nowhereville", "Texas", "United States");
            Assert.Equal(LocationPrecision.State, unknownCity.Precision, "unknown city falls back to the state");
            Assert.Equal(null, unknownCity.County, "no county without a city");
            Assert.Equal(LocationPrecision.None, Atlas.Locate("X", "Y", "Atlantis").Precision, "unknown country");
        }

        [Test]
        static void LocatesRepeatersElsewhere()
        {
            var toronto = Atlas.Locate("Toronto", "Ontario", "Canada");
            Assert.Equal(LocationPrecision.City, toronto.Precision, "Toronto found");
            Assert.True(toronto.State != null && toronto.State.Name == "Ontario", "Toronto is in Ontario: " + toronto.State?.Name);

            var munich = Atlas.Locate("Munchen", "Bayern", "Germany");
            Assert.Equal(LocationPrecision.City, munich.Precision, "Munchen found through GeoNames' alternate names");
            Assert.True(munich.State != null && munich.State.Code == "DE-BY", "Munich is in Bavaria: " + munich.State?.Code);

            var messy = Atlas.Locate("Pettstaedt", "Sachsen-Anhalt / Mecklenburg-Vorpommen", "Germany");
            Assert.Equal("DE", messy.Country?.Code, "country");
            Assert.True(messy.State != null && messy.State.Code == "DE-ST", "'A / B' state picks the first that matches: " + messy.State?.Code);
            Assert.Equal(null, messy.County, "no counties outside the US");
        }

        [Test]
        static void NameKeysFoldAccentsAndAbbreviations()
        {
            Assert.Equal(GeoAtlas.Key("St. Louis"), GeoAtlas.Key("Saint Louis"), "saint");
            Assert.Equal(GeoAtlas.Key("Mt Pleasant"), GeoAtlas.Key("Mount Pleasant"), "mount");
            Assert.Equal(GeoAtlas.Key("Quebec"), GeoAtlas.Key("Québec"), "accent");
            Assert.Equal(GeoAtlas.Key("Tangermünde"), GeoAtlas.Key("Tangermuende"), "umlaut typed as ue");
            Assert.Equal("Baden-Wurttemberg", Naming.Fold("Baden-Württemberg"), "fold umlaut");
            Assert.Equal("Strasse", Naming.Fold("Straße"), "fold sharp s");
            Assert.Equal("plain", Naming.Fold("plain"), "ASCII unchanged");
        }

        static Project ZoneProject()
        {
            var p = new Project();
            p.Talkgroups.Add(new Talkgroup("Worldwide", 91));
            p.Talkgroups.Add(new Talkgroup("Texas", 3148));
            p.Talkgroups.Add(new Talkgroup("Local", 9));
            var a = Repeater.NewDigital("A"); a.Zone = "West"; a.Talkgroups.Add(new RepeaterTalkgroup(3148, 1)); // listed by its owner on TS1
            var b = Repeater.NewDigital("B"); b.Zone = "West";
            var c = Repeater.NewDigital("C"); c.Zone = "East";
            var fm = Repeater.NewAnalog("FM"); fm.Zone = "West";
            p.Repeaters.AddRange(new[] { a, b, c, fm });
            p.SyncZones();
            return p;
        }

        [Test]
        static void ZoneTalkgroupsReachEveryRepeaterInTheZone()
        {
            var p = ZoneProject();
            Repeater a = p.Repeaters[0], b = p.Repeaters[1], c = p.Repeaters[2], fm = p.Repeaters[3];
            Assert.Equal(2, p.AddZoneTalkgroup("West", 91, 1), "91 added to A and B");
            Assert.Equal(1, p.AddZoneTalkgroup("West", 3148, 2), "3148 added to B (A already lists it)");
            Assert.Equal(1, a.Talkgroups.Count(t => t.TalkgroupId == 3148 && t.Slot == 1 && !t.FromZone), "A keeps its own TS1 entry");
            Assert.True(b.Talkgroups.Any(t => t.TalkgroupId == 3148 && t.Slot == 2 && t.FromZone), "B gets 3148 on the zone's slot");
            Assert.Equal(0, c.Talkgroups.Count, "other zones untouched");
            Assert.Equal(0, fm.Talkgroups.Count, "analog untouched");

            p.SetZoneTalkgroupSlot("West", 3148, 1);
            Assert.True(b.Talkgroups.Single(t => t.TalkgroupId == 3148).Slot == 1, "slot change moves zone channels");

            // Moving a repeater: the old zone's channels go, the new zone's come.
            p.AddZoneTalkgroup("East", 9, 2);
            b.Zone = "East";
            p.ApplyZoneTalkgroups(b);
            Assert.Equal("9", string.Join(",", b.Talkgroups.Select(t => t.TalkgroupId)), "B now carries East's set only");
            b.Zone = "West";
            p.ApplyZoneTalkgroups(b);
            Assert.Equal("91,3148", string.Join(",", b.Talkgroups.Select(t => t.TalkgroupId)), "and back");

            Assert.Equal(1, p.RemoveZoneTalkgroup("West", 3148, alsoListed: false), "unticking takes only B's zone channel");
            Assert.True(a.Talkgroups.Any(t => t.TalkgroupId == 3148) && p.FindZone("West").FindTalkgroup(3148) == null, "A keeps its listed one; the set forgets it");
            p.AddZoneTalkgroup("West", 3148, 1);
            Assert.Equal(2, p.RemoveZoneTalkgroup("West", 3148), "removal takes A's listed channel and B's zone channel");
            Assert.Equal(2, p.RemoveZoneTalkgroup("West", 91), "and 91 from both");
            Assert.Equal(0, a.Talkgroups.Count + b.Talkgroups.Count, "West is empty again");
            Assert.True(!p.FindZone("West").HasTalkgroups && p.FindZone("West").Talkgroups == null, "empty set stored as null");
        }

        [Test]
        static void ZoneSetsFollowRenamesMergesAndTalkgroupEdits()
        {
            var p = ZoneProject();
            p.AddZoneTalkgroup("West", 91, 1);
            p.AddZoneTalkgroup("East", 9, 2);
            p.RenameZone("West", "Far West");
            Assert.True(p.FindZone("Far West").FindTalkgroup(91) != null, "rename keeps the set");
            p.RenameZone("East", "Far West");
            var merged = p.FindZone("Far West");
            Assert.Equal("91,9", string.Join(",", merged.Talkgroups.Select(t => t.TalkgroupId)), "merge unions the sets");
            Assert.True(p.Repeaters.Where(r => r.IsDigital).All(r => r.Talkgroups.Any(t => t.TalkgroupId == 9) && r.Talkgroups.Any(t => t.TalkgroupId == 91)), "merged zone applied to all");

            p.ChangeTalkgroupId(91, 92);
            Assert.True(merged.FindTalkgroup(92) != null && p.Repeaters[1].Talkgroups.Any(t => t.TalkgroupId == 92), "ID change followed");
            p.DeleteTalkgroups(p.Talkgroups.Where(t => t.Id == 9).ToList());
            Assert.True(merged.FindTalkgroup(9) == null && p.CountTalkgroupUse(9) == 0 && p.FindTalkgroup(9) == null, "delete removes everywhere");
        }

        [Test]
        static void NewFieldsSaveAndOldFilesStayTheSame()
        {
            var plain = Fixtures.Sample();
            string json = ProjectStore.ToJson(plain);
            foreach (var key in new[] { "FromZone", "AreaCode", "Latitude", "SourceId", "\"County\"" })
                Assert.True(!json.Contains(key), "a project without the new data doesn't mention " + key);

            var p = ZoneProject();
            p.AddZoneTalkgroup("West", 91, 1);
            var r = p.Repeaters[0];
            r.City = "San Angelo"; r.County = "Tom Green County"; r.AreaCode = "US-48451"; r.Latitude = 31.46; r.Longitude = -100.44; r.SourceId = 311562;
            var back = ProjectStore.FromJson(ProjectStore.ToJson(p));
            var br = back.Repeaters[0];
            Assert.Equal("Tom Green County", br.County, "county saved");
            Assert.Equal(31.46, br.Latitude.Value, "latitude saved");
            Assert.Equal(311562, br.SourceId, "source ID saved");
            Assert.True(back.FindZone("West").FindTalkgroup(91)?.Slot == 1, "zone set saved");
            Assert.True(back.Repeaters[1].Talkgroups.Single(t => t.TalkgroupId == 91).FromZone, "FromZone saved");
        }

        [Test]
        static void DefaultSlotsFollowBrandMeisterHabits()
        {
            Assert.Equal(1, Project.DefaultSlot(91), "Worldwide");
            Assert.Equal(1, Project.DefaultSlot(93), "North America");
            Assert.Equal(1, Project.DefaultSlot(3100), "USA");
            Assert.Equal(2, Project.DefaultSlot(3148), "Texas statewide");
            Assert.Equal(2, Project.DefaultSlot(9), "Local");
            Assert.Equal(2, Project.DefaultSlot(31486), "local talkgroup");
        }

        [Test]
        static void AutomaticZoneNames()
        {
            var r = Repeater.NewDigital("KC5EZZ San Angelo");
            r.City = "San Angelo"; r.State = "Texas"; r.Country = "United States"; r.County = "Tom Green County"; r.RxMHz = 441.75m;
            Assert.Equal("Tom Green Co", ZonePlanner.ZoneName(r, ZoneScheme.County), "county");
            Assert.Equal("San Angelo", ZonePlanner.ZoneName(r, ZoneScheme.City), "city");
            Assert.Equal("Texas", ZonePlanner.ZoneName(r, ZoneScheme.State), "state");
            Assert.Equal("United States", ZonePlanner.ZoneName(r, ZoneScheme.Country), "country");
            Assert.Equal("70cm DMR", ZonePlanner.ZoneName(r, ZoneScheme.Band), "band");
            Assert.Equal("Home", ZonePlanner.ZoneName(r, ZoneScheme.Single, "Home"), "single");
            Assert.Equal("San Bernardino", ZonePlanner.CountyZone("San Bernardino County"), "long county drops the suffix");
            Assert.Equal("Richmond", ZonePlanner.CountyZone("Richmond city"), "independent city");
            r.County = null;
            Assert.Equal("Texas", ZonePlanner.ZoneName(r, ZoneScheme.County), "no county falls back to the state");
            var bw = Repeater.NewDigital("x"); bw.State = "Baden-Württemberg";
            Assert.Equal("Baden-Wurttember", ZonePlanner.ZoneName(bw, ZoneScheme.State), "ASCII, 16 characters");

            var p = new Project();
            var a = Repeater.NewDigital("a"); a.City = "Abilene";
            var b = Repeater.NewDigital("b"); b.City = "abilene";
            var c = Repeater.NewDigital("c"); c.City = "Odessa";
            p.Repeaters.AddRange(new[] { a, b, c });
            ZonePlanner.Apply(p, p.Repeaters, ZoneScheme.City);
            Assert.Equal("Abilene,Odessa,Hotspot", string.Join(",", p.Zones.Select(z => z.Name)), "zones made, 'abilene' joins 'Abilene' (the hotspot's zone always exists)");
            Assert.Equal("Abilene", b.Zone, "case-insensitive city grouping keeps the first spelling's zone");
        }

        [Test]
        static void OnlineImportStoresLocationAndUsesZoneSets()
        {
            var p = new Project();
            var page = RadioId.ParseRepeaters(@"{""count"":1,""page"":1,""pages"":1,""results"":[
 {""callsign"":""KC5EZZ"",""city"":""San Angelo"",""color_code"":1,""country"":""United States"",""frequency"":""441.75000"",""ipsc_network"":""Brandmeister"",
  ""locator"":311562,""offset"":""+5.000"",""state"":""Texas"",""talkgroups"":[{""description"":"""",""talkgroup"":3148,""timeslot"":2}],""trustee"":[]}]}");
            var r = page.Repeaters[0];
            r.Location = Atlas.Locate(r.City, r.State, r.Country);
            p.Talkgroups.Add(new Talkgroup("Worldwide", 91));
            var o = new OnlineImportOptions { ZoneFor = rep => ZonePlanner.ZoneName(rep, ZoneScheme.County) };
            // A zone set made before the repeater arrives applies to it on import.
            var placeholder = Repeater.NewDigital("placeholder"); placeholder.Zone = "Tom Green Co";
            p.Repeaters.Add(placeholder);
            p.SyncZones();
            p.AddZoneTalkgroup("Tom Green Co", 91, 1);
            var result = OnlineImporter.AddRepeaters(p, page.Repeaters, o, null);
            var added = result.Added.Single();
            Assert.Equal("Tom Green Co", added.Zone, "zone from county");
            Assert.Equal("US-48451", added.AreaCode, "area code is the county");
            Assert.Equal("Tom Green County", added.County, "county name");
            Assert.Equal(311562, added.SourceId, "source ID");
            Assert.Equal("3148:2,91:1", string.Join(",", added.Talkgroups.Select(t => t.TalkgroupId + ":" + t.Slot)), "published talkgroups plus the zone set");
            Assert.Equal("KC5EZZ San Angelo", added.Name, "a callsign's only repeater keeps the plain name");

            // A second repeater with the same callsign on another frequency.
            var second = RadioId.ParseRepeaters(@"{""count"":1,""page"":1,""pages"":1,""results"":[
 {""callsign"":""KC5EZZ"",""city"":""San Angelo"",""color_code"":1,""country"":""United States"",""frequency"":""444.12500"",""ipsc_network"":""Brandmeister"",
  ""locator"":314877,""offset"":""+5.000"",""state"":""Texas"",""talkgroups"":[],""trustee"":[]}]}");
            var two = OnlineImporter.AddRepeaters(p, second.Repeaters, o, null).Added.Single();
            Assert.Equal("KC5EZZ2", two.Prefix, "second repeater gets a numbered prefix");
            Assert.Equal("KC5EZZ San Angelo 444.125", two.Name, "and its frequency in the name");
            Assert.Equal("KC5EZZ San Angelo 441.750", added.Name, "the first one's name gets its frequency too");
            Assert.Equal("KC5EZZ", added.Prefix, "the first keeps its prefix");
        }
    }
}
