# Handoff: DMR Codeplug Builder 1.2

Written at the end of the session that built version 1.0 (October 2026), for whoever picks this up next.
`DEVELOPMENT.md` is the short version; this file has the reasoning, the full CPS
format notes, what is and isn't proven, and the roadmap.

## 1. What the user asked for

A native Windows program that generates a CSV codeplug for the BTECH DMR-6X2 PRO, to import into the
original CPS, with:

- a simple GUI for adding talkgroups to a repeater,
- an option for a local MMDVM repeater/hotspot,
- repeaters added to named zones automatically.

They supplied their own codeplug as the format sample (exported from CPS 1.22e with Tool > Export >
Export All), so the generator copies that exact layout.

## 2. What version 1.0 does

- **Repeaters tab:** list of DMR repeaters and analog repeaters/simplex channels (tick to include,
  untick to keep but leave out). Editor: name, zone (editable combo; a new name makes a new zone),
  channel prefix, type, power, RX/TX MHz with an offset helper (US band plan suggestion when only RX is
  typed: 2 m below 147 → -0.6, above → +0.6; 70 cm below 445 → +5, above → -5), color code, tones and
  tone squelch (analog), bandwidth, RX-only, notes. Talkgroup picker: search list on the left, *Add on
  slot 1/2*, grid on the right with slot and channel name (gray = automatic name, typed = custom).
- **Hotspot tab:** the same editor bound to `Project.Hotspot`, plus an on/off switch. Generated last.
- **Talkgroups tab:** master list (becomes TalkGroups.CSV). ID edits rewrite references; deletes remove
  the channels that used the talkgroup (after a confirmation). CSV import, sort, a small menu of common
  BrandMeister talkgroups (9, 91, 93, 3100, 9990 private, 4000).
- **Zones tab:** zone order, rename (or merge), A/B channel per zone, live list of members.
- **Settings tab:** Radio ID name + DMR ID, RX group list per repeater (on), scan list per zone (on for new
  codeplugs, off for ones imported from the CPS; template row from a real CPS scan list), write RadioIDList.CSV
  (on), CPS format source, radio limits.
- **File menu:** new/open/save (`.cpb` JSON), *Import from CPS export* (builds a project from an Export
  All folder), *Generate CSV files* (validates, shows warnings, asks where to save the `.LST`, writes the
  CSVs next to it, explains how to import).
- **Status bar:** live channel/zone counts and a problems link (re-validated 400 ms after each edit).
- **CLI:** `--generate` and `--import` for scripting and testing.

**Added in 1.2:**
- **Start page** when there's no project to reopen (LastProject missing and not exactly one `.cpb` next to
  the exe): set up a new codeplug, import from the CPS, open a project, or start empty.
- **New codeplug wizard** (File > New codeplug, Ctrl+N), full window:
  1. Region on a world map (countries or states/provinces). Next starts the RadioID download in the background:
     US states one by one, elsewhere the whole country. BrandMeister names come at the same time.
  2. Your radio: callsign lookup inline, DMR ID, Radio ID name, power, NOAA (US only), optional hotspot
     (frequency, CC).
  3. Which repeaters: the map (pick counties, states or countries; dots are repeaters, red = picked; badges
     show counts) and a List tab to tick or untick single repeaters, including ones the map couldn't place.
  4. Zones: one per county (US) / city / state / country / band / single, with preview, rename-merge and
     reorder.
  5. Talkgroups per zone: the zone talkgroup editor (below). Finish puts the project in the workspace, unsaved.
- **Zone talkgroup sets:** a zone's ticked talkgroups go on every DMR repeater in it, including repeaters added
  later or moved into it. Slot is the repeater's listed one if it already has the talkgroup, else
  `Project.DefaultSlot` (TS1 wide-area 91/93/3100; TS2 for 2/8/9, US statewide 31xx and IDs of 5+ digits). Simplex
  hotspots get TS2. *Starter set* = your state, USA 3100, Local 9, Parrot. Also on the Zones tab (Talkgroups |
  Channels). In the repeater editor, zone channels are blue.
- **Repeaters > Add from map:** the same area picker for an open project; new repeaters go into zones by a
  chosen scheme and pick up those zones' sets.
- A second repeater with the same callsign gets prefix `CALL2` (channels "KC5EZZ2 Texas") and both names get
  the frequency. Before, the generator renamed collisions to "KC5EZZ Texas 2".

The shipped project `My 6X2 Codeplug.cpb` is the user's codeplug imported: 9 talkgroups, 15 analog
channels (including NOAA CH1-7 as RX-only), repeater KC5EZZ (one private-call channel "KC5EZZ" to
Parrot), the 433.550 simplex hotspot with 8 talkgroups in zone "Home", zones Local, Weather,
San Angelo DMR, Hwy 183, Home.

## 3. Design decisions and why

| Decision | Why |
| --- | --- |
| C# WinForms on .NET Framework 4.8 | Ships with Windows 10/11: one small exe, nothing to install. Native look. |
| Built on Linux against Mono's reference assemblies | The build sandbox had no Windows and no NuGet access. The exe references only standard framework assemblies (mscorlib, System, System.Core, System.Drawing, System.Windows.Forms, System.Runtime.Serialization, System.Xml), so it runs on Microsoft's .NET Framework. |
| Template-driven CSV writing | The CPS's columns change between versions and models. Copying a real exported row and changing only managed fields keeps every unknown column valid, and a new CPS version only needs a new export (Settings > Load from CPS export). |
| Built-in templates from the user's export, scrubbed | Matches their exact CPS (1.22e). Personal values in template rows were replaced with placeholders; the generator overwrites those fields anyway. |
| Repeater-centric model | "Add talkgroups to a repeater" maps to one channel per (repeater, talkgroup, slot). Analog entries are one channel each. The hotspot is just a `Repeater`, so it reuses the editor and generator. |
| Zones derived from `Repeater.Zone` | Typing a zone name is the "add to named zone automatically" feature. `Project.Zones` only stores order and A/B choices; `SyncZones()` adds new names and drops unused ones. |
| Channel names `prefix + talkgroup`, shortened to 16, unique | The CPS links zones and RX lists by name only, and its duplicate-name check is on (`Setting.ini` `SameName=0`). Since 1.3 names are shortened, not cut (`Naming.Fit`: drop "(notes)", abbreviations such as Talkgroup→TG, Texas→TX, North→N, then cut, never cutting trailing numbers or IDs). A talkgroup on both slots of one repeater gets "TS1"/"TS2". On a clash the generator tries keeping the last word, then (same repeater) the slot, then " 2", " 3" with a note. Projects saved before 1.3 (`FileVersion` 1) keep their old cut names (`Project.KeepOldChannelNames`), so channels already in the radio aren't renamed. Custom per-channel names are allowed. |
| Talkgroups BrandMeister doesn't name | `TalkgroupNames`: names from owners' repeater notes ("Bell Co (314891)"), another listed repeater's DMR ID ("W5LOS Luling"), then RadioID.net lookups by ID in the App (7 digits = a user: "N0FTW TG"; shorter = a repeater), cached 30 days in `talkgroup-lookups.txt`. Texas, 2026-10-06: 55 unnamed → 20 named from the listings, 25 of the rest by lookup; unregistered 5-digit BM IDs stay "TG 1234". A project talkgroup still called "TG 1234" takes the real name when one turns up. Same-named counties or cities in different states get the state added to the zone name (`ZonePlanner.SeparateStates`). |
| RX group list per DMR repeater, on by default | Hear every talkgroup the repeater carries on that slot. The user's original codeplug had none, so this is a behavior change; it's a Settings toggle. Only group-call talkgroups go in. |
| Importer keeps names | Imported channels keep their exact names through `ChannelName` overrides (only stored when they differ from the automatic name), which is what makes the byte-for-byte round trip possible. |
| One `.LST` listing only generated files | Matches the CPS's own file-list format; untouched sections stay as they are in the CPS. Not yet confirmed that the CPS accepts a partial list (see section 6). |
| Validation separate from generation | Errors block Generate; warnings and generator notes (renames, splits) are shown before saving. |

## 4. CPS CSV format reference (CPS 1.22e, from the user's Export All)

General: every field double-quoted, comma separated, CRLF line endings including after the last line,
no BOM, ASCII. Multi-value fields use `|`. Frequencies have 5 decimals (`146.94000`).

**Menus:** Tool > Export > *Export All (Default CSV FileName)* writes 26 CSVs plus a `.LST`. Tool > Import
> *Import From File List* loads a `.LST`; Tool > Import also takes single files. Tool > Mode toggles the
duplicate channel/contact name check. The CPS has "Import Conflict Information" dialogs ("Ignore this
problem and continue importing?"), so it does validate references on import.

**Import behavior (verified 2026-10-06 in CPS 1.22e):** *Import From File List* accepts a partial `.LST`
(our 5 entries: Channel, RadioIDList, Zone, TalkGroups, ReceiveGroupCallList) with no conflict dialogs,
even though Channel (0) comes before TalkGroups (5) in the list. Each imported file **replaces** that
whole list rather than overwriting by number: channels 25-31 of the old codeplug were gone after
importing 24 channels, and the old "Group List 1" RX list disappeared when ours was imported. The channel
editor also shows a *DMR MODE* field (`DMO/simplex` on the hotspot channels), which comes from the
template row.

**New talkgroups (verified 2026-10-06):** a wizard-made codeplug (`CPSnow\cpswizardtest`: 223 channels, 31
talkgroups, most new to the radio, 10 zones, 16 RX lists) imported into the CPS holding the radio's own codeplug,
and its Export All (`CPSnow\afterwizard`) reproduced all five generated files **byte for byte**. So Channel before
TalkGroups is fine even when the contacts are new. The same import emptied the radio's scan list ("Scan List 1",
whose members were all replaced) although the `.LST` had no ScanList.CSV.

**Merge mode and scan lists (verified 2026-10-06):** a channel made by hand in the CPS (`Handmade 1`, No. 33, in a
new zone "Test", Scan List 1 = "Scan List 1"), Export All (`CPSnow\handmade`), `--generate --merge` (`CPSnow\mergegen`),
import, Export All (`CPSnow\aftermerge`): no dialogs, all five files byte for byte as generated, so the kept channel,
its number and its zone survive. The CPS's "Scan List 1" survived too (members all still existed by name), with
ScanList.CSV not in the `.LST`. So importing Channel.CSV doesn't clear scan lists; it drops the members (and a list
left empty) whose channels no longer exist. With scan lists off, scan lists made in the CPS survive as long as
their channels keep their names.

**.LST:** first line is the entry count, then `index,"File.CSV"`. Section numbers from the export:
0 Channel, 1 RadioIDList, 2 Zone, 3 ScanList, 4 AnalogAddressBook, 5 TalkGroups, 6 PrefabricatedSMS,
7 FM, 8 ReceiveGroupCallList, 9 5ToneEncode, 10 2ToneEncode, 11 DTMFEncode, 12 HotKey_QuickCall,
13 HotKey_State, 14 HotKey_HotKey, 15 DigitalContactList, 16 5ToneDecode, 17 2ToneDecode,
18 DTMFDecode, 19 APRS, 20 RoamingZone, 21 RoamingChannel, 22 GpsRoaming, 23 AESEncryptionCode,
24 ARC4EncryptionCode, 25 OptionalSetting.

**Channel.CSV** (55 columns, in order): No., Channel Name, Receive Frequency, Transmit Frequency,
Channel Type, Transmit Power, Band Width, CTCSS/DCS Decode, CTCSS/DCS Encode, Contact, Contact Call
Type, Radio ID, Busy channel Lock-Out/TX Permit, Squelch Mode, Optional Signal, DTMF Signal Code,
2Tone Signal Code, 5Tone Signal Code, PTT ID, Color Code, Slot, Receive Group List, TX Prohibit,
Reverse, Simplex TDMA, TDMA Adaptive, Extend Encryption Type, Digital Encryption, Call Confirmation,
Talk Around, Work Alone, Custom CTCSS, 2Tone Own ID, DTMF Own ID, 5TONE Own ID, Scan List 1 … Scan List 8,
Ranging, Through Mode, Exclude Channel From Roaming, APRS Report Channel, AES Digital Encryption,
Multiple Key, Random Key, ex_emg_kind, ARC4, AprsUpDateKind, AnaAprsUpDate, DigiAprsUpDate1.

- `No.` is the memory slot (1-4000, gaps allowed). Rows 4001 "Channel VFO A" and 4002 "Channel VFO B"
  hold the VFO settings. APRS.CSV's `channel1`…`channel8` refer to channel numbers (4001 in the export).
  Since 1.2 the project keeps every channel's number (`RepeaterTalkgroup.ChannelNumber`, analog
  `Repeater.ChannelNumber`): the importer stores the export's numbers, Generate keeps stored ones, gives new
  channels the lowest free numbers (skipping numbers held by switched-off repeaters and 4001/4002), writes rows in
  number order and stores what it chose (`CodeplugGenerator.KeepChannelNumbers`; the CLI doesn't save). With nothing
  stored the result is 1, 2, 3... in output order, exactly as before. Settings > Renumber all channels clears them.

**Merge mode** (Settings > Keep channels made in the CPS; `Options.KeepCpsChannels` + `BaseExportFolder`; CLI
`--merge folder`; `src/Core/Merge.cs`). Because every imported file replaces its list, Generate reads a fresh
Export All of the codeplug and writes back what the program doesn't manage:
- A base channel is the program's when the project generates that name, when it's in `Project.KnownChannels`
  (every number+name the program imported or generated, all names a number ever had), or when its number is one the
  project stores (a channel renamed or edited in the CPS: the project's version wins, with a note). Anything else is
  made in the CPS and kept: its row copied as-is (all columns), same number; new project channels avoid those numbers.
- Talkgroups (by ID) the project lacks: kept if a kept channel or kept RX list uses them, or never known
  (`KnownTalkgroups`). Same ID under another name: references switch to the project's name. Clashing names get a number.
- RX lists, and (only when ScanList.CSV is written) scan lists, that kept channels use; radio IDs other than ours.
- Zones: kept channels stay in the project's zones they were in (appended); zones not in `KnownZones` come back
  with members that still exist (with no history yet, only zones holding a kept channel).
- `CodeplugGenerator.RememberOutput` records the project's own output after each Generate; the importer records
  everything it imports. Kept things are never recorded, so they stay "made in the CPS".
- Generate warns when the export is older than the last `.LST` in the output folder.
- Values seen: Channel Type `A-Analog` / `D-Digital`; Transmit Power `Low`, `High`, `Turbo` (and
  presumably `Mid`); Band Width `12.5K` / `25K`; tones `Off` or `94.8` (DCS assumed `D023N`); Contact
  Call Type `Group Call` / `Private Call`; Busy channel Lock-Out/TX Permit `Off` (analog), `Always`
  (digital); Squelch Mode `Carrier` / `CTCSS/DCS`; TX Prohibit `On` / `Off`; Receive Group List `None`
  when unused; Scan List N empty (not `None`) when unused, otherwise a scan list **name**; Custom CTCSS `251.1`; Extend Encryption Type
  `Normal Encryption`.
- Contact = a talkgroup **name**. There is no talkgroup ID column in this version. Analog channels still
  carry a contact (the CPS default contact was the user's own ID as a group call).
- Radio ID = a name from RadioIDList.CSV.
- Fields the generator manages: No., Channel Name, both frequencies, Transmit Power, TX Prohibit,
  Radio ID, Band Width, both tones, Squelch Mode (analog), Contact, Contact Call Type, Color Code and Slot
  (digital), Receive Group List, Scan List 1 (if scan lists are on), plus `Contact TG/DMR ID` if a future
  layout has it. Channel Type comes from the template row.

**Zone.CSV:** No., Zone Name, Zone Channel Member (`a|b|c`), A Channel, B Channel. Names only, no
frequencies (AnyTone 878 CPS versions add member frequency columns; the generator fills them if present).

**TalkGroups.CSV:** No., Radio ID (the talkgroup number), Name, Call Type, Call Alert (`None`). The 6X2
guide warns that duplicate entries make the write to the radio fail.

**ReceiveGroupCallList.CSV:** No., Group Name, Contact (`name|name`). No ID column.

**ScanList.CSV:** No., Scan List Name, Scan Channel Member, Scan Mode, Priority Channel Select, Priority
Channel 1, Priority Channel 2, Revert Channel, Look Back Time A[s], Look Back Time B[s], Dropout Delay
Time[s], Dwell Time[s]. No member frequency columns (the generator's `Scan Channel Member RX/TX Frequency`
sets are skipped). A real row (Export All 2026-10-06, `CPSnow\ScanList.CSV`): `"1","Scan List 1","a|b|c","Off",
"Off","Off","Off","Selected","2.0","3.0","3.1","3.1"`, members by channel name. Those are exactly the values
`DefaultScanRow` had guessed; the row is now the built-in template (`CpsFormat.ScanTemplate`, member names
neutralized), so output is unchanged. Membership lives here only: the 13 member channels had **empty**
`Scan List 1` in Channel.CSV, while a hand-made channel that is *not* a member had `Scan List 1 = "Scan List 1"`.
So Channel's Scan List 1-8 pick the list(s) the channel scans with, independent of membership. The generator sets
Scan List 1 of each zone member to its zone's list (both at once). **Verified 2026-10-06:** the user's project
generated with scan lists on (6-entry `.LST` with ScanList at 3; 5 lists, one per zone) imported with no dialogs and
Export All reproduced all six files byte for byte (the export landed in `CPSnow\scangen` over the generated files;
the generated copy was kept and compared).

**RadioIDList.CSV:** No., Radio ID, Name.

**Others (not generated):** DigitalContactList.CSV (No., Radio ID, Callsign, Name, City, State, Country,
Remarks, Call Type, Call Alert); RoamingChannel.CSV (No., Receive Frequency, Transmit Frequency, Color
Code, Slot, Name; `No Use` for CC/slot); RoamingZone.CSV (No., Name, Roaming Channel Member, with a stray
trailing comma in the header); APRS.CSV and OptionalSetting.CSV are single settings rows with no `No.`
column (OptionalSetting has 169 columns).

## 4b. Online data sources (checked 2026-10-06)

**RadioID.net** (free, no key, no documented rate limit):
- `GET https://radioid.net/api/dmr/repeater/?state=Texas[&page=N]` → `{count, page, pages, per_page:200, results:[...]}`.
  `per_page` can't be raised; Texas is 2 pages (354 entries). Each result: `callsign`, `city`, `state`,
  `country`, `frequency` ("441.75000", the repeater output), `offset` (free text: "+5.000", "5.000", "+5",
  "-0.600", even "5000" in kHz), `color_code`, `locator` (the repeater's own DMR ID), `ipsc_network` (free
  text: "BM", "brandmeister", "Brandmister", "Lone Star", "DMR-MARC/Brandmeister/DMR+/TGIF"...), `status`,
  `details` (HTML), `trustee` (array), `talkgroups: [{talkgroup, timeslot, description}]`. About a third of
  Texas repeaters list talkgroups. No coordinates, so there's no "within N miles" search.
- Duplicate listings exist (same callsign, frequency and CC twice), and different repeaters often share a
  frequency pair and CC across a state. `OnlineImporter.FindExisting` therefore also requires the callsign.
- `GET https://radioid.net/api/dmr/user/?callsign=W6OZZ` → `results: [{radio_id, callsign, fname, surname, city, state, country}]`.

**BrandMeister:** `GET https://api.brandmeister.network/v2/talkgroup` → `{"91":"World-wide", ...}` (1844
entries, ~45 KB; some names non-ASCII, 429 longer than 16 characters, e.g. "Texas - 10 Minute Limit").
US statewide talkgroups are 31 + the state's FIPS code (3148 Texas, 3106 California). The US Parrot is
310997 (private call). `/v2/device/{id}` gives lat/lng per repeater, but `/v2/device/{id}/talkgroup`
returned HTTP 500.

`GET https://radioid.net/api/dmr/repeater/?country=Canada[&page=N]` also works (checked 2026-10-06): United
States 5101 (26 pages), Germany 758, Canada 481, United Kingdom 418, Australia 198. Outside the US the `state`
field is free text ("Sachsen-Anhalt / Mecklenburg-Vorpommen", "England", blank), so the app downloads by country
and places repeaters itself (section 4c).

**DMR-MARC** (checked 2026-10-06): dmr-marc.net/repeaters.html redirects to RadioID.net's DMR-MARC map; the old
`datadump.cgi` is gone (404). The map loads `GET https://radioid.net/api/rptr/map/` → `{count, markers:[...]}`: every
on-air repeater worldwide in one request (10,487; ~5.5 MB, ~0.7 MB gzipped), same fields as the repeater API plus
`lat`/`lng` (strings), `map_info` (= `details`), `trustee` as a string, `status` "ACTIVE". It's a subset of the
per-state/country lists (Texas 343 of 354), so the app keeps those as the list and uses the map only for positions
(`RadioId.ParseMapPositions` / `ApplyMapPositions` → `GeoAtlas.LocateAt`, matched by `locator`; a point outside the
listed country is ignored). Cached a week as `radioid-positions.txt` (~300 KB). North America: repeaters the map
couldn't place exactly went from 1,073 to 77 of 5,525.

**RepeaterBook CHIRP export** (what the app uses, no token; checked on the user's Brown County, TX export
2026-10-06, file `rb_chirp_<yyMMddHHmm>.csv` in Downloads): header
`Location,Name,Frequency,Duplex,Offset,Tone,rToneFreq,cToneFreq,DtcsCode,DtcsPolarity,Mode,TStep,Comment`, CRLF,
an extra trailing comma on every row. Name = callsign, Frequency = output, Comment = city ("Brownwood, Bangs Hill").
No county, state, color code or digital modes. Tone semantics are CHIRP's and match the user's CPS channels: `TSQL`
→ cToneFreq both ways + tone squelch (K5BWD UHF), `Tone` → rToneFreq transmit only (W5CBT), blank → carrier.
The export's county search includes towns just over the line (Cross Plains is in Callahan County).

**CHIRP's own CSV after its RepeaterBook query** (the user's `TexasRepeaters.csv`, 2026-10-06, 1,365 lines: 1,025 FM,
173 DN, 124 DMR, 43 DV; 137 FM lines on 10 m / 6 m / 220 / 1.2 GHz): extra columns `RxDtcsCode,CrossMode,...,Skip,Power,
...,URCALL,RPT1CALL,RPT2CALL,DVCODE`. Name is often a site or club ("Westlake Hills"); the comment is
"CALL near Town, X County, Texas OPEN [notes]" (12 lines leave out the callsign). `ChirpChannel.ReadComment` takes the
callsign, town, county and state from it, so the map step needs no state from the file name and towns the map
doesn't know go to their county's middle. Frequency pairs repeat across the state: duplicate checks must include the
callsign (or town), never frequencies alone. Result: 888 usable FM, 854 added next to Texas's DMR repeaters (the
rest are listed twice or are the same machine RadioID lists as DMR), every one in its county.

**RepeaterBook** (analog): since 2026-03 the API (`/api/export.php?state_id=<FIPS>&...`) answers 401
without a token. Personal use needs an approved token: request at repeaterbook.com/api/token_request.php,
(tokens are app-bound: a user's token for QDMR or CHIRP is for that app only; this app needs approval as a "distributed app",
then each user generates their own token for it from the API Apps dashboard)
then send `X-RB-App-Token: rbuapp_...` and a User-Agent with an app name and contact email. The exact JSON
key names weren't captured (no token); get a real response before writing the parser.

## 4c. The built-in map (atlas)

`src/Core/Geo/atlas.gz` (~3.7 MB, embedded as `CodeplugBuilder.Geo.atlas.gz`) is made by `tools/GeoBuild`
from these downloads, kept as zips in `tools/geodata/` (git-ignored; the tool reads inside the zips, and
the output is byte-for-byte reproducible):

| File | Source | License | Used for |
| --- | --- | --- | --- |
| `cb_2023_us_state_20m.zip`, `cb_2023_us_county_20m.zip` | www2.census.gov/geo/tiger/GENZ2023/shp/ | public domain | US states (with DC, PR) and 3222 counties |
| `2023_Gaz_place_national.zip` | www2.census.gov/geo/docs/maps-data/data/gazetteer/2023_Gazetteer/ | public domain | 32k US places with coordinates (" city", " CDP"... stripped) |
| `ne_10m_admin_0_countries.zip`, `ne_10m_admin_1_states_provinces.zip` | naciscdn.org/naturalearth/10m/cultural/ | public domain | countries and states/provinces outside the US |
| `cities15000.zip` | download.geonames.org/export/dump/ | CC BY 4.0 (credited in Help > About) | 31k world cities of 15,000+ people, with up to 12 Latin-script spellings |
| `ne_10m_roads.zip` (9 MB) | naciscdn.org/naturalearth/10m/cultural/ | public domain | Highways, written to a second file `src/Core/Geo/roads.gz` (~0.5 MB, embedded as `CodeplugBuilder.Geo.roads.gz`): "Major Highway" (9.7k) and "Secondary Highway" (9k) lines, simplified at 0.004 deg. US rows only carry the number in `name` and the kind in `level`, so labels are made "I-35" / "US 83" (state routes unlabeled); elsewhere `prefix` + `label`. Drawn by `RegionMap.DrawRoads` (majors from 12 px/deg, secondaries from 40, route tags from 130; "Highways" checkbox in `RegionPicker`) |

Outlines are Douglas-Peucker simplified (countries 0.02 deg, states 0.008, counties 0.003; tiny areas like
Vatican redone finer) and quantized to 1e-4 deg, delta/varint encoded, gzipped. Loading takes ~0.4 s and ~50 MB.

`GeoAtlas.Locate(city, state, country)`: country by name/aliases (plus a list of RadioID spellings), state by
name/alias/code ("A / B" tries each part), place by folded name (accents, St./Saint, Mt./Mount, ue = u) in that
country and state, then point-in-polygon for state and US county (nearest box if a point falls just outside a
simplified outline). Real RadioID data placed by city: Texas 92% (all with county), Canada 58%, Germany 49%,
UK 50%, Australia 37%; the rest get state/province (Canada, Germany, Australia) or only the country (UK lists
"England"/"Scotland", which aren't admin-1 areas). A bigger GeoNames file (cities5000/cities1000) would place
more small towns.

## 4d. Talking to the radio directly (read, settings and whole-codeplug writes work; see "Writing a whole codeplug")

Goal (user, 2026-10-06): a full CPS that reads and writes the radio and exposes the advanced settings. Decided:
**our own code** (qdmr and dmrconfig are GPL; we read their docs to learn the format but copy no code), read
first, write only after the read is verified, never firmware. Plan: 1 read + decode → 2 settings editor →
3 write (patch the image read from that radio, write only changed blocks, read back, backup first).

**Step 1 is built (backend only, no menu items; the exe was not copied to the app folder).** Code in
`src/Core/Radio` (`AnytoneLink` protocol over any Stream, `MemoryImage`, `Dmr6x2Pro` layout + read plan +
`RadioReader`, `RadioCodeplug` decoder, `RadioCsv` → CPS CSVs + compare) and `src/App/RadioPort.cs`,
`RadioCli.cs`. Dev switches:
```
CodeplugBuilder.exe --radio-read outFolder [--port COM5] [--any-model]   # radio.img + CSVs + radio.log
CodeplugBuilder.exe --radio-decode radio.img outFolder
CodeplugBuilder.exe --radio-compare cpsExportFolder radioFolder           # → radioFolder\compare.log
```
The CSV folder imports like an Export All (`--import outFolder x.cpb`). Tests: `RadioTests` (simulated radio
built from the user's export; read with garbled replies → decode → CSVs equal the export byte for byte → same
project as importing the export).

**First real read (2026-10-06, COM3, `D6X2UV2 V100`, band code 0):** 1416 blocks in 1.6 s, no decode warnings,
DTR+RTS on worked first try. Against the 2026-08-24 Export All, every column of Channel.CSV (numbers with gaps,
VFO rows), TalkGroups, RadioIDList and ReceiveGroupCallList matched exactly: bitmap bit order (LSB first),
the inverted contact bitmap and the contact order list are confirmed.

**Zone A/B solved:** dmr-tools describes 0x2500100 as 250 (A, B) pairs; it is really two lists, **A at 0x2500100 and
B at 0x2500300, 250 × u16 each, holding the channel's position within the zone's member list** (qdmr's
`ZoneChannelListElement` has the split right). An export taken without a fresh read showed a stale A channel; after
a CPS *Read from radio* (2026-10-06 10:17) the CPS agreed with the radio. A second read of ours was block-for-block
identical to the first. **Result: all six CSVs decoded from the radio are byte-identical to the CPS's Export All
of the same radio.** Values this codeplug doesn't use (mixed channel types, Mid power, other TX permit/squelch/PTT
ID options, encryption, scan lists) are still unverified spellings.

**Next, with the radio connected:** close the CPS, `--radio-read`, then in the CPS *Read from radio* + Export All
of the same state, `--radio-compare`. The "By column" summary shows which mappings are wrong. Unverified
guesses: value spellings other than the first of each list in `RadioCsv` (mixed channel types, "Mid" power,
TX permit, squelch, PTT ID, encryption, scan list fields), bitmap bit order (LSB first), contact order list,
zone No. numbering, DTR/RTS on the port.

**Step 2 (settings) started 2026-10-06.** `src/Core/Radio/RadioSettings.cs` is one table of 163 optional settings
(general block 0x2500000, boot text 0x2500600, extended 0x2501400, some APRS 0x2501000): address, bits, labelled
values, the OptionalSetting.CSV column where known, and a `Verified` flag. `Read`/`Write`/`WriteText` (bits outside a
field are kept; values outside the list are refused), `Report`, `CompareWithCps`. Dev switches:
`--radio-settings radio.img [cpsExportFolder]` (→ settings.txt) and `--radio-settings-ui [radio.img]` (editor window
built from the table: read radio, open/save image, edit in memory; *Write to radio* disabled) plus
`--radio-settings-snapshot radio.img outFolder` (PNG per page). Facts:
- **CPS 1.22e's OptionalSetting.CSV export is broken for this radio:** the first ~107 columns (the whole general
  block) are always "0" and the boot text is blank. The tail (from `StartChUse`) is real and follows the bytes: general
  0xCD-0xD7, then the extended block in order (talker alias, roaming, colors, Bluetooth, FM mic, TOT warning, ATPC,
  GNSS, channel index, WX). All 46 mapped columns matched the user's radio; 22 settings are marked verified because
  they matched on a non-default value. The general block (keys, display, squelch, VOX, hang times...) needs a
  different check: compare with the CPS's Optional Setting screen, or change settings in the CPS, write, re-read, diff.
- Checked with the user: power-on display 2 = custom image (correct). **Time zone (0x30) is in half-hour steps from
  GMT-12**, not whole hours as dmr-tools says: the radio stores 14 and the CPS shows GMT-5. Only one data point, so the
  setting stays unverified until a second value is seen (step 3's first write test is a good place). Still odd: priority
  zone A/B 0 (doc: FF = none), APRS callsign BG6LKK (factory default).

**Step 3 (writing): first test failed, radio recovered, writing is switched OFF (`RadioWriter.Enabled`).**
What happened on 2026-10-06 (test: Zone A name color Green → Yellow, one byte at 0x2501412):
1. Wrote block 0x2501410 alone, read it back in the same session: old data, radio unchanged after END.
2. Wrote 0x2501400-0x250142F (the whole extended area), same-session read-back: again unchanged.
3. Wrote the same 3 blocks **without** a same-session read-back. The radio dropped off USB, then answered nothing
   ("A device attached to the system is not functioning"), and on screen asked for a power-on password **in
   Chinese**. Restored by the user with a full BTECH CPS write of `CodePlugs\Codeplug 8-24-26.rdt`; a read after
   that matched the CPS export exactly (backup and all reads in `Documents\DMR-6X2-PRO-121e\RadioWriteTest\`).

Conclusions: (a) a read in the same session discards pending writes (cache), which is why 1 and 2 did nothing;
(b) when a write is committed, the radio **erases the whole flash sector** (at least 0x2500000-0x2501FFF: general
settings went to FF, so password "on" and language unset) and keeps only what was written in that session. The CPS
and qdmr always write every element of the codeplug, so they never see this. **Before writing again:** find the
sector size (64 KB? 4 KB?) and always write every block of each touched sector, which means first reading the whole
sector, not just the documented elements (0x2500000-0x250FFFF has areas we never read, e.g. 0x2502000). Better yet,
capture the CPS's own write traffic once to see its order and extent. Test on a sector whose loss is cheap, with the
CPS `.rdt` ready. The user's radio after the restore: Side B mode is Channel (was VFO) and Scan List 1 exists (from
the August `.rdt`); that scan list decodes identically to the CPS export (scan list decoding verified).

**Step 3 fixed: first successful write (2026-10-06).** A USB capture of a CPS 1.22e "Write to radio" (Wireshark +
USBPcap, `Documents\DMR-6X2-PRO-121e\cps-write-read.pcapng`) showed: one session; PROGRAM, identify, one read of
0x2FA0020 (answers `FF FF FF FF 00 00 FF FF 00...`, purpose unknown), then **5,044 writes in strictly ascending
address order** covering every element of the codeplug (65 runs: channels and extensions, VFOs, zones, roaming,
scan/group lists at 0xC0/0x130 each, SMS index/byte map/messages, FM, 5-tone/2-tone/DTMF, all bitmaps, encryption
key tables 0x24C1700-0x24C1CFF and 0x25C1000-0x25C5FEF, settings, zone names, radio IDs, the full 40,000-byte contact
order list, contact bitmap, contacts, analog contact index/byte map, the contact ID map at 0x4800000), then END.
No reads after writes. Every gap it skips reads FF: the radio erases what a session writes over and keeps only what
that session sends. The CPS read session (radio re-enumerates as a new USB device after END) uses the same commands.
Now: the reader covers all of it (5,509 blocks, 3.8 s); `Dmr6x2Pro.WriteSet` = `CpsFixedWriteRanges` + `ElementRanges`
+ any other non-FF block. **Dry run on the user's radio: the plan equals the captured CPS write exactly (same 5,044
addresses, order and data).** `RadioWriter.Write` sends that set from a full read + edits, after re-reading every
block of it (aborts if anything changed), then END; `RadioPort.Write` waits for the radio to come back (~30 s) and
verifies all blocks in a new session. Test write Zone A name color Green → Yellow: verified, a fresh full read equals
the edited image, the CSVs still equal the CPS export. Guards: `RadioWriter.Enabled` is false by default; only
`--radio-write ... --confirm` turns it on (the settings window's button is still disabled). Dev switches:
`--radio-set radio.img out.img Key=Value...`, `--radio-write-plan radio.img edited.img plan.txt` (dry run),
`--radio-write radio.img edited.img --confirm`. Channel/zone/contact writes: done, see "Writing a whole codeplug" below
(checked against a CPS-written radio instead of a second USB capture).
**In the app (2026-10-06):** Radio > Read codeplug from radio (MainForm.ImportFromRadio: RadioProgressDialog,
backup folder RadioPort.NewReadFolder, then the CPS import path) and Radio > Radio settings (RadioSettingsForm with
Write to radio: lists changed settings, flags unverified ones, saves before.img/written.img/write.log per write,
turns RadioWriter.Enabled on only for that write). The exe in the app folder has it.

**Writing a whole codeplug (2026-10-06, verified on the user's radio).** `RadioEncoder.Encode(read, tables)` writes
the six CPS tables into a full read: each table given replaces its list, a table not given keeps the radio's (a kept
scan list loses members that are gone and goes when none are left, like the CPS); every record starts as a copy of one
the radio holds (same slot if same kind, else one of its kind, else a VFO), so undecoded bytes keep the radio's values;
old records are blanked to FF first; contacts get slot = list position, order list = slots in order. Errors (unknown
talkgroup, radio ID, bad numbers) block the write. `RadioWriter` now allows blocks the read never covered, but only
inside records the edited image's own bitmaps say exist (new channels past the old last one); anything else is still
refused. Verification, all against CPS 1.22e:
- A test codeplug (`CPSnow\radiotest`: wizard, 99 channels, 19 talkgroups, 18 RX lists, 3 zones, 3 scan lists) was
  imported and written **by the CPS**, then read: the encoder's image of the same CSVs on the earlier read equals it in
  **all 5,990 blocks of the write set** (same blocks, order and data). Getting there found these CPS rules (all in the
  encoder): tone fields not in use (CTCSS index, DCS) are 0; a simplex VFO holds its TX frequency at +4 (not an
  offset); unused zone slots have A = 0, B = 1 in the A/B lists; the block with the end of the last contact is padded
  with 00; scan list bytes 0x84-0xB5 = per member, the index of the zone the member is in (first zone holding it), FF
  after the last; and after an import the CPS zeroes 0x24C1406 and 0x24C140E (u16 each, most likely the analog and
  digital alarm revert channels) and general settings byte 0x1F (held zone 4), even though they were still valid.
- Contact ID map at 0x4800000 (written by the CPS, read by nobody here before): 8 bytes per contact, u32 key + u32
  slot, sorted by key; key = the ID's BCD digits as a number, shifted left 1, + 1 for a group call (93 → 0x127,
  private 310997 → 0x62132E); FF after.
- **First app write of a codeplug:** the user's own codeplug (11:46 read) encoded onto the CPS-written test codeplug
  and written with `--radio-write`: 5,044 blocks (same addresses and order as the recorded CPS write of it), verified
  after reconnecting, and a fresh read decodes to the user's six tables byte for byte (`RadioWriteTest\afterrestore`).
- `--radio-encode radio.img (project.cpb | csvFolder) outFolder [--merge-radio]` does all of this without the radio:
  edited.img, before/after CSVs, plan.txt, and a check that decoding gives back what went in.
**In the app:** *Radio > Write codeplug to radio* (MainForm.WriteProjectToRadio): validate, read the radio (saved as
before.img + CSVs in Documents\DMR Codeplug Builder\Radio reads\<date> write), generate (with *Keep channels made in the
CPS* on, the radio read is the merge base), encode, show old/new counts, new and removed channels and notes, confirm
twice, write, verify, keep channel numbers like Generate. *Radio > Restore codeplug from a backup* writes the codeplug
of any saved .img back the same way (settings stay as on the radio). Not covered: settings other than the three above
are never changed by a codeplug write; a member of a scan list in no zone gets FF (a guess); the encoder only knows the
CPS 1.22e / firmware 1.21 layout.

**Protocol** (USB CDC, VID 28E9 PID 018A, any baud): `PROGRAM` → `QX 06`; `02` → `'I' model[7] bands
version[6] 06` (the PRO says `D6X2UV2`, `V100`); read `'R' addr(4 BE) 10` → `'W' addr 10 data[16] sum 06`,
sum = byte sum of addr, length and data; write `'W' addr 10 data[16] sum 06` → `06` (not implemented); `END` → `06`.

**Memory map** (from dmr-tools.github.io/codeplugs/dmr6x2uv2_1.21b.html, the 6X2 PRO firmware 1.21b page; strides
from qdmr's D868UV layout, which the PRO keeps):

| What | Address | Layout |
| --- | --- | --- |
| Channels | 0x800000 + bank·0x40000 + n·0x40 | 128 per bank, extension bank at +0x2000; bitmap 0x24C1500 (4000 bits, set = used) |
| VFO A/B | 0xFC0800, 0xFC0840 | extensions at 0xFC2800 |
| Zones | members 0x1000000 + i·0x200 (250 × u16, FFFF = none); names 0x2540000 + i·0x20; A 0x2500100 + i·2, B 0x2500300 + i·2 (position in the zone) | bitmap 0x24C1300, hidden 0x24C1360 |
| Contacts | 0x2680000 + (i/1000)·0x40000 + (i%1000)·0x64 | bitmap 0x2640000 **inverted** (clear = used); order list 0x2600000 (u32 slots) |
| RX group lists | 0x2980000 + i·0x200, 0x120 bytes | 64 × u32 contact slot + name at 0x100; bitmap 0x25C0B10 |
| Scan lists | 0x1080000 + (i/16)·0x40000 + (i%16)·0x200, 0x90 bytes (CPS writes 0xC0) | 0 mode, 1 priority select, 2/4 priority ch (FFFF off, 0 current, else slot+1), 6/8/A/C times ×10, E revert, F name, 20 50 × u16 member slot, 84-B5 member's zone index; bitmap 0x24C1340 |
| Contact ID map | 0x4800000, 8 × (contacts + 1) | u32 key (BCD ID << 1, +1 group) + u32 slot, sorted by key |
| Radio IDs | 0x2580000 + i·0x20 | BCD ID + name at 5; bitmap 0x24C1320 |
| Settings | general 0x2500000 (0xE0), boot text 0x2500600, APRS 0x2501000, extended 0x2501400 | decoded in step 2 |

Channel (0x40): 0 RX BCD (10 Hz), 4 offset BCD, 8 [7:6 offset dir, 4 wide, 3:2 power, 1:0 mode], 9 [7 talkaround,
6 call confirm, 5 RX only, 4 reverse, 3:2 TX tone type, 1:0 RX tone type], A/B TX/RX CTCSS index (0 = 62.5, 51 =
custom), C/E TX/RX DCS u16 (octal value, +512 inverted), 10 custom CTCSS ×10, 14 contact slot u32, 18 radio ID
index, 19 [6:4 squelch, 1:0 PTT ID], 1A [5:4 optional signal, 1:0 TX permit], 1B flags, 1C RX group list (FF none),
20 color code, 21 [7 lone worker, 6 enhanced enc, 4 adaptive TDMA, 2 simplex TDMA, 0 slot 2], 22 DMR key, 23 name
(16), 34 [1 through mode], 36-3D scan lists (FF none), 3E APRS report channel. Contact (0x64): 0 call type (0
private, 1 group, 2 all), 1 name, 0x23 ID BCD, 0x27 alert.

## 5. Verification done

Engine tests (`tests/Tests.cs`, 27 tests, all passing, run with the export folder as argument):

- Every exported CSV parses and re-serializes byte-for-byte (except RoamingZone/APRS, which have stray
  trailing commas the writer doesn't reproduce; they are never written).
- Built-in headers equal the export's headers; `.LST` section numbers match the CPS's own list.
- Generator integrity on synthetic projects: every Contact, RX list, Radio ID and zone member resolves;
  row widths; names unique and ≤16; frequency format; VFO rows kept; private calls left out of RX lists;
  zone split at 250; scan lists; A/B selection; disabled repeaters skipped; files written without BOM
  and with CRLF.
- Import of the user's export: talkgroups, radio ID, hotspot detection, zones, RX-only, tone squelch.
- **Round trip:** import the export, regenerate with RX lists off → Channel.CSV (channel numbers and their
  gaps included, since 1.2), Zone.CSV, TalkGroups.CSV and RadioIDList.CSV byte-identical; same again after a JSON
  save/load cycle.
- Project JSON round trip, missing-field defaults, atomic save, validation rules, talkgroup CSV import,
  zone sync/rename/merge.

UI: the Release exe was run under Mono WinForms on Xvfb and driven with xdotool. Checked: every tab
renders; adding a DMR repeater with a new zone, prefix, frequency (offset auto +5) and two talkgroups;
the hotspot tab; editing a talkgroup ID (references followed); the common-talkgroups menu; File > Import;
Save; Generate through the save dialog; the unsaved-changes prompt. Output files were inspected.

### First real-CPS import and Windows run (2026-10-06)

Built with the .NET 8 SDK on the user's Windows 11 machine (clean, 27/27 tests). Generated the user's
project and imported it with *Import From File List*: accepted, no conflicts, no leftover channels (see
"Import behavior" in section 4). Checked in the CPS: channel list 1-24, hotspot channel 17 (CC 1, slot 1,
RX list "Hotspot"), zones with A/B channels, talkgroups, RX group list. The new talkgroup case (a
talkgroup that didn't exist before the import) has still not been tried.

Two Windows-only UI bugs that Mono hid were fixed:
- Opening a project marked it dirty: the ListView raises `ItemChecked` for every item while creating its
  handle. `RepeatersPage` now ignores checks until handle creation finishes.
- The Zone and tone combos showed their text highlighted when not focused (setting `Text` on an
  editable combo selects it). `RepeaterEditor` clears the selection after binding and on handle creation.

### 1.2 (2026-10-06)

45 tests (GeoTests: atlas, placing listings in the US and abroad, name folding, zone talkgroup sets through
add/untick/remove/slot/rezone/rename/merge/ID change/delete, JSON compatibility of the new fields, default slots,
automatic zone names, online import with location, zone sets and same-callsign prefixes). Round trip unchanged.

UI (computer-use access to the app was declined, so off screen): `--map-snapshot` (world, North America,
Texas counties, Europe) and `--ui-walkthrough` (start page, all five wizard steps with the live Texas download
and W6OZZ lookup, Zones and Repeaters tabs on the result, CSV generation: 95 channels, 3 zones, 0 errors).
Found and fixed that way: a crash in the map toolbar's constructor, the areas map never getting its atlas, the
start page's bold description, an empty Hotspot zone taking zone sets, and same-callsign channel renames.

**Review pass (same day).** Fixed: ticking a talkgroup in the zone grid rebuilt the grid inside its own
CellValueChanged (DataGridView reentrancy crash; now refreshed after the event); the areas map zoomed while its
wizard page was still hidden, so it opened as a tiny world (RegionMap now remembers a zoom until it has its real
size, and the wizard shows a page before setting it up); Finish re-sorted zones and lost the user's order; a failed
download couldn't be retried and showed a blank map; regrouping zones or changing picks silently dropped zone
talkgroups (now asks); typing the one-zone name rebuilt every zone per keystroke (now renames); clicking an area
forgot List-tab ticks everywhere (now only inside that area); "US counties" level couldn't pick states abroad; a
mouse-up outside the map left it panning; the title bar didn't follow the start page/wizard; a hand-added repeater
didn't get its zone's talkgroups; Add from map and the wizard kept downloading after closing; zone names now keep
one spelling ("abilene" joins "Abilene"). The walkthrough also ticks a zone talkgroup with a real Space key.

**IssuesDialog input:** with real WM_KEYDOWN messages posted to the modal dialog, Escape and Enter both close it
(Close-only and Generate-anyway variants, also after clicking into the text). Posted mouse clicks closed neither
it nor a bare test dialog, so off-screen clicks can't be tested this way. Nothing in the code explains the earlier
"clicks and Escape ignored"; a real click on screen is still to be checked.

## 6. Not verified yet, and how to check

0. **1.2 UI by hand:** wizard and Add from map with the mouse (hover box, wheel zoom, drag, county clicks, List
   ticks with the creatingHandle guard), at the user's scaling; open the user's project and check no "*".

1. ~~Partial `.LST`~~, ~~import order~~, ~~replace or overwrite~~, ~~new talkgroups~~: verified, see section 4.
2. (merged into 1)
3. Because imports replace whole lists, channels made by hand in the CPS are lost on the next import
   unless they're in the project or merge mode is on (1.2). ~~Real CPS round~~: verified 2026-10-06 (section 4,
   "Merge mode and scan lists").
4. ~~ScanList.CSV~~: values confirmed from a real scan list, and a scan-lists-on import re-exports byte for byte
   (section 4).
5. **Real Windows rendering:** checked at the user's scaling on Windows 11 (all tabs, list checkboxes,
   editor fields, hotspot grid). Still to check: 125%/150% on another monitor, cue banners,
   ErrorProvider icons, menu shortcuts.
6. **SmartScreen:** the exe is unsigned. A downloaded zip may need Properties > Unblock before extracting.
7. **Hotspot slot:** the user's simplex hotspot channels are on slot 1. Pi-Star/WPSD simplex hotspots
   normally use slot 2. Left as imported; worth asking the user if receive through the hotspot is flaky.

## 7. Roadmap (in priority order)

**P1: make the import bulletproof**
1. ~~Run section 6 items 1-3 in the real CPS~~ (done 2026-10-06, all fine, new talkgroups included).
2. ~~Real scan-list template~~ (done 2026-10-06, test `BuiltInScanTemplateIsTheRealCpsRow`). Scan-lists-on import
   verified; on by default for new codeplugs since 2026-10-06 (`CpsImporter` keeps imports off). Remaining: Scan
   List 2-8 support.
3. ~~Stable channel numbers~~: done in 1.2 (section 4, Channel.CSV `No.`; test `ChannelNumbersStayPut`).
4. ~~Merge mode~~: done in 1.2 (section 4, "Merge mode"; test `MergeKeepsWhatWasMadeInTheCps`), verified in the
   real CPS 2026-10-06.
5. Windows polish: test DPI; consider `PerMonitorV2` via app.config; fix anything Mono hid.

**From 1.2**
- Same talkgroup listed on both slots of one repeater still gives "WA5TBB2 Texas" and "WA5TBB2 Texas 2"; a slot
  suffix ("Texas 1"/"Texas 2") would read better.
- Map: draw is 75-200 ms per frame at 4K scaling; cache screen paths per zoom level if panning feels slow.
- Wizard step 3: offer "pick the whole region" in one click; remember the picked areas in the project.
- Bigger GeoNames file for small towns abroad (needs the user's OK to download).

**P2: data sources ("automatic" codeplugs)**
Done in 1.1: RadioID.net DMR repeaters + talkgroups, RadioID callsign → DMR ID, BrandMeister talkgroup
names and browser, NOAA preset (section 4b). Ideas left: per-repeater "re-sync from RadioID" for repeaters
added online (their notes hold the repeater ID), and a nearest-first sort using BrandMeister device lat/lng.
6. RepeaterBook: since 2026-03-03 its API only serves approved clients, and since 2026-03-31 it uses
   tokens (commercial users pay). Approved distributed apps let each user generate their own app-bound
   token. Options: apply for approval, let the user paste a token, or import RepeaterBook's own CSV/KML
   downloads instead of calling the API.
7. RadioID.net: free API with DMR repeaters (frequencies, color codes) and the user database, which could
   also generate DigitalContactList.CSV (filter by country/state; the radio holds 500,000 contacts).
8. BrandMeister: official talkgroup names for the picker; possibly a repeater's static talkgroups per
   slot to pre-fill its channels (check what the current API offers).
9. Talkgroup sets: named presets ("BM wide area", "Texas") applied to one or many repeaters; mark
   talkgroups static vs. dynamic; per-network tags (BrandMeister, TGIF, DMR-MARC, local).

**P3: usability**
10. Multi-select repeaters to set zone/power/talkgroup set at once; drag-and-drop ordering; search and
    sort (frequency, zone).
11. Undo/redo (JSON snapshots in `Session`), recent projects, automatic `.cpb` backups.
12. Printable channel list per zone (HTML).
13. Show generator notes beside the affected channel (e.g. renamed for uniqueness).

**P4: more radio features**
14. Roaming: generate RoamingChannel.CSV / RoamingZone.CSV for repeaters that share a talkgroup set.
15. Per-repeater TX permit (`Busy channel Lock-Out/TX Permit`; check allowed values in the CPS UI),
    talk-around, APRS report channel.

**P5: other radios**
16. AnyTone D878UV/D578UV and relatives use the same CPS lineage. Loading their Export All as a format may
    mostly work already (frequency columns in Zone.CSV and `Contact TG/DMR ID` are filled when present);
    needs real exports to test. Add a radio-model setting for limits.
17. qdmr YAML export, so the same project can program other brands through `dmrconf`.

**Code health**
- Move UI-side cascades into Core with tests: talkgroup ID change and delete (`TalkgroupsPage`), and the
  zone operations already in `Project`.
- The status bar regenerates the whole codeplug on each change; fine up to 4000 channels, cache if it
  ever feels slow.
- Consider structured issue codes instead of message strings.

## 8. Checking the UI without Windows (what this session did)

```
apt install mono-devel libmono-system-windows-forms4.0-cil xvfb imagemagick xdotool
Xvfb :93 -screen 0 1280x860x24 &
DISPLAY=:93 mono src/App/bin/Release/net48/CodeplugBuilder.exe "My 6X2 Codeplug.cpb" --tab Hotspot &
sleep 6; DISPLAY=:93 import -window root shot.png          # screenshot, then view it
DISPLAY=:93 xdotool mousemove 120 501 click 1              # click
DISPLAY=:93 xdotool type --delay 30 "444.500"              # type
```

`--tab <name>` opens a given tab. Mono's WinForms looks different from Windows and ignores cue banners,
but layout bugs show up. Kill the app by PID (`$!`), not with `pkill -f`, which can match your own shell.

## 9. Prior art worth a look

- CODEPLUGGER (github.com/jimdawdy-hub/codeplugger): automatic DM-32UV codeplugs from RadioID,
  BrandMeister and RepeaterBook KML data; CSV import into the stock CPS.
- dzcb (DMR Zone Channel Builder, PyPI): builds zones from RepeaterBook and K7ABD-style CSVs, exports to
  several CPS formats.
- qdmr / dmrconf (dm3mat.de/software/qdmr): device-independent YAML codeplugs; supports the BTECH
  DMR-6X2UV and DMR-6X2UV PRO directly.
