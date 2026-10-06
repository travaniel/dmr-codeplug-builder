using System;
using System.Collections.Generic;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Tests
{
    /// <summary>Parsing and importing online data. Samples are trimmed real API responses (October 2026).</summary>
    static class OnlineTests
    {
        [Test]
        static void TalkgroupNamesFromNotesAndListings()
        {
            var ids = new HashSet<int> { 314891, 3140312, 3148, 75088, 8288, 312331 };
            // Real RadioID.net notes (Texas, 2026-10-06).
            var a = TalkgroupNames.FromNotes("TS1 TX State Wide (3148) / TS2 Bell Co (314891)", ids);
            Assert.Equal("Bell Co", a[314891], "name (id)");
            Assert.Equal("TX State Wide", a[3148], "name (id) after a slot");
            var b = TalkgroupNames.FromNotes("MINI Repeater located in South West Temple, TX. TS2 Static has Bell County Wide TG 314891 and 3819 HF Net TG3140312.", ids);
            Assert.Equal("Bell County Wide", b[314891], "name TG id, after a stop word");
            Assert.Equal("HF Net", b[3140312], "name TGid");
            var c = TalkgroupNames.FromNotes("Timeslot 1 / Brazoria Cty 8288 / Garland TX 75088", ids);
            Assert.Equal("Brazoria Cty", c[8288], "table row");
            Assert.True(!c.ContainsKey(75088), "a ZIP code isn't a talkgroup");

            var w5los = new OnlineRepeater { Callsign = "W5LOS", City = "Luling", DmrId = 312331 };
            var other = new OnlineRepeater { Callsign = "K5TRA", City = "Austin", Details = "", RxMHz = 444.1m, TxMHz = 449.1m };
            other.Talkgroups.Add(new OnlineTalkgroup { Id = 312331, Slot = 2, Description = "" });
            var unnamed = TalkgroupNames.Unnamed(new[] { w5los, other }, null);
            Assert.True(unnamed.Contains(312331), "nothing names 312331 yet");
            Assert.Equal("W5LOS Luling", TalkgroupNames.FromListings(new[] { w5los, other }, unnamed)[312331], "another repeater's DMR ID");
            Assert.True(TalkgroupNames.IsPlaceholder("TG#312331", 312331) && !TalkgroupNames.IsPlaceholder("W5LOS Luling", 312331), "placeholder");

            // A project talkgroup only known as "TG 312331" takes the real name.
            var p = new Project { RadioIdName = "Test", RadioId = 1 };
            p.Talkgroups.Add(new Talkgroup("TG 312331", 312331));
            OnlineImporter.AddRepeaters(p, new[] { other }, new OnlineImportOptions { MoreNames = new Dictionary<int, string> { { 312331, "W5LOS Luling" } } }, null);
            Assert.Equal("W5LOS Luling", p.FindTalkgroup(312331).Name, "placeholder renamed");
        }

        [Test]
        static void WeatherChannelsInWxOrder()
        {
            var p = new Project { RadioIdName = "Test", RadioId = 1 };
            p.Talkgroups.Add(new Talkgroup("Local", 9));
            Presets.AddNoaaWeather(p);
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            Assert.Equal("NOAA WX1,NOAA WX2,NOAA WX3,NOAA WX4,NOAA WX5,NOAA WX6,NOAA WX7", string.Join(",", g.ChannelList.Select(c => c.Name)), "WX1 to WX7");

            // A project saved with the old frequency order (and numbers 8-14) is put back in WX order on load.
            var old = new Project { RadioIdName = "Test", RadioId = 1 };
            int n = 8;
            foreach (var r in p.Repeaters.OrderBy(r => r.RxMHz)) { var c = r.Clone(); c.ChannelNumber = n++; old.Repeaters.Add(c); }
            old.Normalize();
            Assert.Equal("NOAA WX1,NOAA WX2,NOAA WX3,NOAA WX4,NOAA WX5,NOAA WX6,NOAA WX7", string.Join(",", old.Repeaters.Select(r => r.Name)), "reordered");
            Assert.Equal("8,9,10,11,12,13,14", string.Join(",", old.Repeaters.Select(r => r.ChannelNumber)), "numbers follow WX order");
            Assert.True(!Presets.SortNoaaWeather(old), "already sorted");
        }

        [Test]
        static void SameCountyNameInTwoStatesGetsTwoZones()
        {
            var p = new Project();
            Repeater R(string call, string state)
            {
                var r = Repeater.NewDigital(call);
                r.County = "Washington County";
                r.State = state;
                p.Repeaters.Add(r);
                return r;
            }
            var tx = R("K5TX", "Texas");
            var ok = R("K5OK", "Oklahoma");
            var tx2 = R("K5TY", "Texas");
            ZonePlanner.Apply(p, p.Repeaters, ZoneScheme.County);
            Assert.Equal("Washington Co TX", tx.Zone, "Texas");
            Assert.Equal("Washington Co OK", ok.Zone, "Oklahoma");
            Assert.Equal(tx.Zone, tx2.Zone, "same state, same zone");
            Assert.True(p.FindZone("Washington Co") == null, "shared zone gone");
        }

        const string RepeatersJson = @"{""count"":3,""page"":1,""pages"":1,""per_page"":200,""results"":[
 {""callsign"":""KC5EZZ"",""city"":""San Angelo"",""color_code"":1,""country"":""United States"",""coverage"":""Peer"",""details"":"""",
  ""frequency"":""441.75000"",""identity_id"":6480,""ipsc_network"":""Brandmeister"",""last_master"":null,""locator"":311562,""manufacturer"":null,
  ""offset"":""+5.000"",""state"":""Texas"",""status"":""on-air"",
  ""talkgroups"":[{""description"":"""",""discovery"":0,""talkgroup"":31486,""timeslot"":1},
                  {""description"":"""",""discovery"":0,""talkgroup"":311562,""timeslot"":1},
                  {""description"":""Texas - 10 Minute Limit"",""discovery"":0,""talkgroup"":3148,""timeslot"":2}],""trustee"":[""KC5EZZ""]},
 {""callsign"":""kg5cng"",""city"":""San Angelo"",""color_code"":1,""country"":""United States"",""details"":""Slot 1<br>TG3148 TX Statewide &amp; more"",
  ""frequency"":""444.65000"",""ipsc_network"":""BM"",""locator"":114801,""offset"":""5.000"",""state"":""Texas"",""status"":""on-air"",""talkgroups"":[],""trustee"":[""W5ETJ""]},
 {""callsign"":""N5VGQ"",""city"":""Kilgore"",""color_code"":3,""country"":""United States"",""details"":"""",
  ""frequency"":""147.30000"",""ipsc_network"":""DMR-MARC/Brandmeister/DMR+/TGIF"",""locator"":310016,""offset"":""+0.600"",""state"":""Texas"",""status"":""on-air"",
  ""talkgroups"":[{""description"":""TX ARES EmComm"",""talkgroup"":31487,""timeslot"":1},{""description"":""Worldwide (PTT)"",""talkgroup"":1,""timeslot"":1},
                  {""description"":"""",""talkgroup"":3179762,""timeslot"":2}],""trustee"":[""N5VGQ""]},
 {""callsign"":""W1BAD"",""city"":""Nowhere"",""color_code"":1,""frequency"":""927.01250"",""offset"":""-25.000"",""state"":""Texas"",""talkgroups"":[]}
]}";

        const string UserJson = @"{""count"":1,""page"":1,""pages"":1,""per_page"":200,""results"":[{""account_unvalidated"":0,""callsign"":""W6OZZ"",""city"":""Los Angeles"",
""country"":""United States"",""fname"":""Austin"",""id"":3226509,""lastheard"":""Tue, 15 Sep 2026 04:54:04 GMT"",""name"":""Austin"",""radio_id"":3226509,""state"":""California"",""surname"":""""}]}";

        const string BmJson = @"{""1"":""Local"",""9"":""Local"",""91"":""World-wide"",""93"":""North America"",""3100"":""USA Bridge"",""3148"":""Texas - 10 Minute Limit"",
""31487"":""TX ARES EmComm"",""310997"":""Parrot"",""202"":""Διεθνές Ελλάδα"",""x"":""ignored""}";

        [Test]
        static void JsonParsesNestedValuesAndEscapes()
        {
            var v = Json.Parse(@" { ""a"": [1, -2.5, 3e2, true, false, null], ""s"": ""q\""\\\/é\n"", ""o"": {} } ");
            var a = Json.Arr(Json.Get(v, "a"));
            Assert.Equal(6, a.Count, "array length");
            Assert.Equal(1, Json.Int(a[0]), "int");
            Assert.Equal("-2.5", Json.Str(a[1]), "decimal");
            Assert.Equal(300, Json.Int(a[2]), "exponent");
            Assert.True(a[3] is bool b && b && a[5] == null, "true/null");
            Assert.Equal("q\"\\/é\n", Json.Str(Json.Get(v, "s")), "escapes");
            Assert.True(Json.Obj(Json.Get(v, "o")).Count == 0, "empty object");
            bool threw = false;
            try { Json.Parse("{\"a\":1,}"); } catch (FormatException) { threw = true; }
            Assert.True(threw, "trailing comma rejected");
        }

        [Test]
        static void ParsesRadioIdRepeaters()
        {
            var page = RadioId.ParseRepeaters(RepeatersJson);
            Assert.Equal(4, page.Repeaters.Count, "repeaters");
            var ezz = page.Repeaters[0];
            Assert.Equal("KC5EZZ", ezz.Callsign, "callsign");
            Assert.Equal(441.75m, ezz.RxMHz, "rx");
            Assert.Equal(446.75m, ezz.TxMHz, "tx");
            Assert.Equal(311562, ezz.DmrId, "repeater ID");
            Assert.Equal("BrandMeister", ezz.Network, "network");
            Assert.Equal(3, ezz.Talkgroups.Count, "talkgroups");
            Assert.Equal(2, ezz.Talkgroups[2].Slot, "slot");

            var cng = page.Repeaters[1];
            Assert.Equal("KG5CNG", cng.Callsign, "callsign upper-cased");
            Assert.Equal(449.65m, cng.TxMHz, "unsigned 5.000 at 444.65 is +5 by the band plan");
            Assert.Equal("BrandMeister", cng.Network, "BM");
            Assert.Equal("Slot 1 / TG3148 TX Statewide & more", cng.Details, "HTML details flattened");

            var vgq = page.Repeaters[2];
            Assert.Equal(147.9m, vgq.TxMHz, "2 m +0.6");
            Assert.Equal("BrandMeister/TGIF/DMR-MARC/DMR+", vgq.Network, "multi-network");
            Assert.True(!page.Repeaters[3].InRadioBand, "900 MHz is outside the radio's bands");
            Assert.True(ezz.InRadioBand && vgq.InRadioBand, "UHF and VHF are inside");
        }

        [Test]
        static void ParsesRadioIdUserAndBrandMeister()
        {
            var users = RadioId.ParseUsers(UserJson);
            Assert.Equal(1, users.Count, "users");
            Assert.Equal(3226509, users[0].Id, "id");
            Assert.Equal("Austin W6OZZ", users[0].SuggestedName(), "radio ID name");
            Assert.Equal("W6OZZ", new RadioIdUser { FirstName = "Bartholomew-Maximilian", Callsign = "W6OZZ" }.SuggestedName(), "long first name falls back to callsign");

            var bm = BrandMeister.ParseTalkgroups(BmJson);
            Assert.Equal(9, bm.Count, "numeric keys only");
            Assert.Equal("Texas", BrandMeister.ShortName(bm[3148]), "suffix dropped");
            Assert.Equal("World-wide", BrandMeister.ShortName(bm[91]), "short name kept");
            Assert.Equal("", BrandMeister.ShortName(bm[202]), "non-ASCII name cleans to empty");
            Assert.Equal("Worldwide (PTT)", BrandMeister.ShortName("Worldwide (PTT)"), "fits, kept");
            Assert.Equal("Southern Plains", BrandMeister.ShortName("Southern Plains (Static, 15 min)"), "long parenthetical dropped");
            Assert.Equal(3148, BrandMeister.StateTalkgroup("texas"), "Texas");
            Assert.Equal(3106, BrandMeister.StateTalkgroup("California"), "California");
            Assert.Equal(0, BrandMeister.StateTalkgroup("Ontario"), "not a US state");
            Assert.True(BrandMeister.IsPrivateCall(310997, "") && BrandMeister.IsPrivateCall(1, "Parrot") && !BrandMeister.IsPrivateCall(91, "World-wide"), "private calls");
        }

        [Test]
        static void OffsetsAndNetworks()
        {
            Assert.Equal(5m, BandPlan.ParseOffset("+5.000", 442m), "+5");
            Assert.Equal(-5m, BandPlan.ParseOffset("5", 447m), "unsigned at 447 → -5");
            Assert.Equal(-0.6m, BandPlan.ParseOffset("0.6 MHz", 146.94m), "unsigned 2 m below 147 → -0.6");
            Assert.Equal(0m, BandPlan.ParseOffset("0", 433.55m), "simplex");
            Assert.Equal(5m, BandPlan.ParseOffset("", 441m), "blank → band plan");
            Assert.Equal(1.6m, BandPlan.ParseOffset("1.6", 223.94m), "odd offset kept");
            Assert.Equal(5m, BandPlan.ParseOffset("5000", 442.225m), "typed in kHz");
            Assert.Equal(-0.6m, BandPlan.ParseOffset("-600", 146.94m), "kHz with sign");
            Assert.Equal("BrandMeister", Networks.Normalize("Brandmister"), "misspelling");
            Assert.Equal("Lonestar", Networks.Normalize("Lone Star"), "lonestar");
            Assert.Equal("XYZ Net", Networks.Normalize("  XYZ   Net "), "unknown kept");
            Assert.Equal("Mixed", Networks.Normalize("mixed"), "lower-case capitalized");
            Assert.Equal("", Networks.Normalize(null), "null");
        }

        [Test]
        static void AddsRepeatersWithTalkgroupsAndZones()
        {
            var p = Fixtures.Sample();
            p.Talkgroups.Add(new Talkgroup("Texas", 31999 + 1)); // a different ID already uses the name "Texas"
            int tgBefore = p.Talkgroups.Count, rptBefore = p.Repeaters.Count;
            var rpts = RadioId.ParseRepeaters(RepeatersJson).Repeaters;
            var bm = BrandMeister.ParseTalkgroups(BmJson);
            var opts = new OnlineImportOptions { Power = "Turbo", DefaultTalkgroups = OnlineImporter.SuggestedDefaults("Texas", bm) };

            var r = OnlineImporter.AddRepeaters(p, rpts, opts, bm);

            Assert.Equal(3, r.Added.Count, "added (900 MHz skipped)");
            Assert.True(r.Notes.Any(n => n.Contains("W1BAD")), "skip noted");
            Assert.True(r.Notes.Any(n => n.Contains("KG5CNG")), "defaults noted");

            var ezz = r.Added[0];
            Assert.Equal("KC5EZZ", ezz.Prefix, "prefix");
            Assert.Equal("San Angelo", ezz.Zone, "zone per city");
            Assert.Equal("Turbo", ezz.Power, "power");
            Assert.Equal(3, ezz.Talkgroups.Count, "published talkgroups");
            Assert.Equal("Texas 3148", p.FindTalkgroup(3148).Name, "BrandMeister name, made unique");
            Assert.Equal("KC5EZZ Local", p.FindTalkgroup(311562).Name, "repeater's own ID");
            Assert.Equal("TG 31486", p.FindTalkgroup(31486).Name, "no name anywhere");
            Assert.True(ezz.Notes.Contains("311562") && ezz.Notes.Contains("BrandMeister"), "notes");

            var cng = r.Added[1];
            Assert.Equal(opts.DefaultTalkgroups.Count, cng.Talkgroups.Count, "defaults used");
            Assert.Equal(CallTypes.Private, p.FindTalkgroup(BrandMeister.Parrot).CallType, "Parrot is a private call");
            Assert.Equal(2, cng.Talkgroups.First(t => t.TalkgroupId == 9).Slot, "Local on slot 2");
            Assert.Equal("Local", p.FindTalkgroup(9).Name, "existing talkgroup reused, not renamed");

            var vgq = r.Added[2];
            Assert.Equal("TX ARES EmComm", p.FindTalkgroup(31487).Name, "BrandMeister name on a BM-listed repeater");
            Assert.Equal("Worldwide (PTT)", p.FindTalkgroup(1).Name, "multi-network repeater: owner's description beats BrandMeister's TG 1 \"Local\"");
            Assert.Equal("Kilgore", vgq.Zone, "zone");

            Assert.Equal(rptBefore + 3, p.Repeaters.Count, "repeaters added to project");
            Assert.Equal(tgBefore + r.NewTalkgroups.Count, p.Talkgroups.Count, "talkgroups added");
            Assert.True(p.Zones.Any(z => z.Name == "San Angelo") && p.Zones.Any(z => z.Name == "Kilgore"), "zones synced");

            // Same repeaters again: all skipped as duplicates.
            var again = OnlineImporter.AddRepeaters(p, rpts.Take(3), opts, bm);
            Assert.Equal(0, again.Added.Count, "duplicates skipped");
            // A different repeater that shares KC5EZZ's frequency pair and color code is not a duplicate.
            var other = RadioId.ParseRepeaters(RepeatersJson).Repeaters[0];
            other.Callsign = "K5ZY";
            Assert.True(OnlineImporter.FindExisting(p, other) == null, "same frequency, different callsign");

            // The result generates cleanly.
            var issues = Validator.Validate(p, CpsFormat.BuiltIn());
            Assert.True(!issues.Any(i => i.Severity == Severity.Error), "no validation errors: " + string.Join("; ", issues.Select(i => i.Message)));
            var g = CodeplugGenerator.Generate(p, CpsFormat.BuiltIn());
            var names = g.ChannelList.Select(c => c.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique channel names");
            Assert.True(names.All(n => n.Length <= 16), "names fit");
            Assert.True(names.Contains("KC5EZZ TX 3148"), "channel name = prefix + talkgroup, shortened to 16 without cutting the ID: " + string.Join(", ", names));
            Assert.True(names.Contains("KC5EZZ Local"), "talkgroup already named after the repeater isn't prefixed twice");
        }

        [Test]
        static void OneZoneForAllAndNoDefaults()
        {
            var p = new Project { RadioIdName = "Test", RadioId = 1234567 };
            var rpts = RadioId.ParseRepeaters(RepeatersJson).Repeaters;
            var r = OnlineImporter.AddRepeaters(p, rpts.Take(2), new OnlineImportOptions { ZonePerCity = false, Zone = "Texas DMR repeaters list" }, null);
            Assert.True(r.Added.All(x => x.Zone == "Texas DMR repeat"), "fixed zone, cut to 16");
            Assert.Equal(0, r.Added[1].Talkgroups.Count, "no defaults → no talkgroups");
            Assert.True(r.Notes.Any(n => n.StartsWith("No talkgroups yet")), "noted");
            Assert.Equal("Texas", p.FindTalkgroup(3148).Name, "owner's description shortened (no BrandMeister list)");
            Assert.Equal("TG 31486", p.FindTalkgroup(31486).Name, "no name anywhere");
        }

        [Test]
        static void NoaaPresetSkipsExisting()
        {
            var p = Fixtures.Sample(); // already has 162.400
            var added = Presets.AddNoaaWeather(p);
            Assert.Equal(6, added.Count, "six new");
            Assert.True(added.All(r => r.RxOnly && !r.IsDigital && r.Zone == "Weather" && r.RxMHz == r.TxMHz), "rx-only analog");
            Assert.Equal("NOAA WX1", added[0].Name, "WX order, not frequency order (WX2, already there, is skipped)");
            Assert.Equal(0, Presets.AddNoaaWeather(p).Count, "second time adds nothing");
        }
    }
}
