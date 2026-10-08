# DMR Codeplug Builder: developer guide

A Windows desktop app (C# WinForms on .NET Framework 4.8) that builds CSV codeplugs for the
**BTECH DMR-6X2 PRO** CPS (customer programming software). The user edits talkgroups, repeaters, an
MMDVM hotspot and zones; the app writes the CPS's CSV files plus a `.LST` file list that the CPS loads
with **Tool > Import > Import From File List**.

`HANDOFF.md` (same folder) has the full story: design decisions, the complete CPS CSV format reference, what has
and hasn't been verified, and the prioritized roadmap. **Read it before changing `src/Core` or anything
about the CSV output.**

## Setup the author uses

- Radio: BTECH DMR-6X2 PRO (firmware 1.21a), with the BTECH CPS 1.22e on Windows.
- A reference "Export All" of a real codeplug (26 CSVs + a `.LST`) in a folder of your own. The round-trip tests need it; without
  it they are skipped, and everything else still runs. Point the test runner at that folder (see below).
- The author keeps the built exe and a personal project (`.cpb`) in a folder next to this source; neither is in the repository.

## Build and test

Windows (normal case):
```
dotnet build src\App\CodeplugBuilder.csproj -c Release      # or open CodeplugBuilder.sln in VS 2022
dotnet run --project tests -- "<folder with a CPS Export All>"
```
The exe lands in `src\App\bin\Release\net48\`. Copy it (and `CodeplugBuilder.exe.config`) to the app folder.
The tests need the .NET 8 SDK; the app needs the .NET Framework 4.8 targeting pack (VS ".NET desktop
development" workload) or internet access so the SDK can fetch reference assemblies.

Linux (how version 1.0 was built): `bash build.sh <export-folder>` compiles against Mono's 4.8 reference
assemblies with NuGet switched off (`tools/nuget.offline.config`). See HANDOFF for the Mono + Xvfb +
xdotool recipe used to screenshot and click through the UI without Windows.

Command line (no console on Windows; a log is written next to the output):
```
CodeplugBuilder.exe --generate "My 6X2 Codeplug.cpb" OutFolder [--format CpsExportFolder] [--merge CpsExportFolder]
CodeplugBuilder.exe --import CpsExportFolder "My 6X2 Codeplug.cpb"
```

## Rules that keep the output importable

1. **Never address CSV columns by index.** Use `CsvTable.Set(row, value, "Column Name", "Alias")` /
   `Get(...)`. A column the template lacks is skipped; a column we don't manage keeps the CPS value.
2. **Every generated row starts as a copy of a row the CPS itself exported** (`CpsFormat.AnalogTemplate`,
   `DigitalTemplate`, `VfoRows`, `ScanTemplate`). Don't build rows from scratch.
3. CSV dialect: every field double-quoted, comma separated, CRLF, trailing CRLF, no BOM, ASCII
   (read/written as Latin-1 by `CsvTable`).
4. **Names are the foreign keys.** This CPS's Zone.CSV and ReceiveGroupCallList.CSV refer to channels and
   talkgroups by name only (no frequencies, no IDs). Names must be unique (case-insensitive), at most 16
   characters, with no `|` or `"`. Always go through `UniqueNamer` / `Naming.Clean`.
5. Keep the VFO rows (No. 4001 and 4002) in Channel.CSV, pointed at a contact and radio ID that exist.
   APRS.CSV refers to channel 4001 by number.
6. Analog channels still need a valid Contact (the generator uses the first talkgroup) and Radio ID.
7. After any change in `src/Core`, run the tests with the export folder.
   `ImportTests.RoundTripReproducesUsersCodeplug` must stay green: importing the user's export and
   regenerating it has to reproduce Channel (channel numbers included), Zone, TalkGroups and RadioIDList byte-for-byte.
8. New facts about the CPS format go into the format section of `docs/HANDOFF.md`.

## Code constraints

- The app targets **.NET Framework 4.8** so it runs on stock Windows 10/11 with nothing to install.
  C# `latest` syntax is fine, but no APIs newer than 4.8: no `TextBox.PlaceholderText`,
  `Dictionary.GetValueOrDefault`, `string.Contains(char)`, `Math.Clamp`, ranges (`^1`, `..`),
  records or `init`. The test runner (net8.0) compiles the same Core files, so Core must build on both.
- **Zero NuGet dependencies** in the app and tests. JSON uses `DataContractJsonSerializer`.
- The csproj uses plain `<Reference Include="System.Windows.Forms" />` instead of `UseWindowsForms`,
  because the Linux SDK has no WindowsDesktop SDK. Keep it that way so both platforms build.
- `src/Core` has no UI and nothing Windows-only. It is compiled into both projects through
  `<Compile Include="..\Core\**\*.cs" />`. Templates in `src/Core/Templates` are embedded resources
  named `CodeplugBuilder.Templates.<File>.CSV`.
- `DataContractJsonSerializer` doesn't run constructors: each model sets its defaults in an
  `[OnDeserializing]` `Init()`. Put defaults for new fields there, and keep old `.cpb` files loading
  (`Project.Normalize()` repairs nulls).
- The UI is built in code (no designer files): TableLayoutPanel / FlowLayoutPanel with Dock/Anchor, and
  `Ui.S(px)` around every fixed pixel size so high-DPI screens scale. `Ui.InitSplitter` sets
  SplitContainer splitters once they have their real size.

## Architecture

`src/Core` (engine, no UI)

| File | What it does |
| --- | --- |
| `Csv.cs` | `CsvTable`: header + rows, name-based `Get`/`Set` (punctuation-insensitive matching, aliases), `NewRow(template)`, parse/write in the CPS dialect |
| `Models.cs` | `Project` (radio ID, talkgroups, repeaters, hotspot, zone order + A/B, options), `Repeater` (digital or analog; digital carries `RepeaterTalkgroup {TalkgroupId, Slot, ChannelName override}`), `Talkgroup`, `ZoneInfo`, `GenerationOptions`. `SyncZones()` adds new zone names and drops unused ones; `RenameZone()` |
| `CpsFormat.cs` | One CPS version's layout: the six template tables (built-in resources or an Export All folder), template rows, frequency decimals, `.LST` writer with the CPS's section numbers |
| `Generator.cs` | `CodeplugGenerator.Generate(project, format)` → `GeneratedCodeplug` (tables, channel/zone lists, notes). Channel numbers: stored ones kept, new ones lowest free, rows in number order; `KeepChannelNumbers` stores them after a Generate. Order: talkgroups → channel names → RX group lists → zones (split at 250) → scan lists → channel rows → VFO rows → radio ID. `WriteTo(folder, lstName)` |
| `CpsImporter.cs` | Export All folder → `Project`: groups digital channels by frequency pair + color code + zone into repeaters, picks the hotspot, keeps channel names |
| `Validation.cs` | `Validator.Validate(project, format)` → errors (block Generate) and warnings, including the safety warnings (`Validator.SafetyWarnings`) |
| `Aprs.cs` | APRS suggestion from the project (callsign, SSID, region frequency, gateway) and the factory-BG6LKK note on radio reads; nothing written yet |
| `CallerDatabase.cs` | RadioID user.csv → callers by scope (world, countries, US states), ID order, ASCII; `ToTable` fills a CPS template row (no template yet, so nothing is generated) |
| `Bands.cs` | `AmateurBands`: 2 m / 70 cm transmit limits per country (from the repeaters' `AreaCode`; unknown = 144-148 / 420-450), the satellite sub-bands, frequencies a hotspot or DMR simplex keeps off (APRS, calling, ISS), US Part 97.201(b) hotspot segments |
| `Naming.cs`, `Tones.cs` | 16-character names, uniqueness, auto channel names; CTCSS/DCS normalization |
| `ProjectStore.cs`, `TalkgroupCsv.cs` | `.cpb` JSON save/load (atomic); talkgroup CSV import |
| `Json.cs` | Minimal JSON reader (objects → `Dictionary<string, object>`, numbers → `decimal`) for the online APIs |
| `OnlineData.cs` | RadioID.net repeater/user parsing (by state or country), BrandMeister talkgroup names + US state TGs (31 + FIPS), `Networks.Normalize`, `BandPlan` (offsets), `OnlineImporter.AddRepeaters` (repeaters + talkgroups + location into a project; same-callsign repeaters get prefix `CALL2`; applies zone talkgroup sets), `Presets.AddNoaaWeather`, `Presets.AddSimplex` (`IsPreset`: preset channels keep their zone). Pure: no HTTP |
| `Geo.cs` | `GeoAtlas`: the embedded map (`Geo/atlas.gz`: countries, states/provinces, US counties, ~63k places). `Locate(city, state, country)` → `GeoLocation` (point + areas), `AreaAt`, `FindCountry/State/Place`, `Key()` name folding |
| `RepeaterBook.cs` | RepeaterBook without the API: `ChirpCsv` reads a CHIRP export (Name = callsign, Comment = city, CHIRP tone modes), `RepeaterBookImport` (dedupe against analog repeaters, "CALL City" or "CALL VHF/UHF" names, zone suggestion from the county, `Add`). `StateFromName` (state from the file/folder name), `ToListings` (CHIRP lines as `OnlineRepeater`s with `Analog` set, placed at their towns, so the map picker and `OnlineImporter.AddRepeaters` take them like DMR listings) |
| `Merge.cs` | Merge mode: `CpsExport` (an Export All folder), `Merge` (what was made in the CPS is kept: channels, zones, talkgroups, RX/scan lists, radio IDs), `KnownChannel`. Called from `Generate(p, f, mergeBase)` |
| `Updates.cs` | `UpdateCheck`: Check for updates (item 12): `Compare` (project vs current RadioID.net listings, BrandMeister last-seen and static talkgroups, new repeaters in the project's counties) → `UpdateItem`s with suggested ticks; `Apply` |
| `Home.cs` | `HomeLocation` (Project.Home; `Parse` a typed town on the atlas), `Distances` (km, bearing, "148 mi NE"), `ZoneOrder.Sort` (hotspot, favorites, areas nearest first, talkgroup zones, utilities), `TalkgroupOrder` (local first) |
| `Zoning.cs` | `ZonePlanner`: automatic zone names per county/city/state/country/band/single, `Apply`, one spelling per zone (`Canonical`) |
| `Radio/*.cs` | Direct radio access (HANDOFF 4d): `AnytoneLink` (serial protocol over a Stream), `MemoryImage` (16-byte blocks, `radio.img`), `Dmr6x2Pro` (memory map, read plan, `RadioReader`, CPS write set), `RadioCodeplug` (decoder), `RadioCsv` (→ CPS CSVs, `Compare`), `RadioEncoder` (CPS tables → image, the reverse), `RadioWriter` (full CPS-style write, guards), `RadioSettings` (table of optional settings: read/write/report; App `RadioSettingsForm` is the dev editor), `RecommendedSettings` (settings Write codeplug to radio offers to change: send talker alias) |
| (`Models.cs`, 1.5) | Zones as views: `ZoneInfo.Kind` (`ZoneKinds`), `Members` (`ZoneMember` → `Repeater.Id`), `RuleTalkgroups/RuleZones`; `Project.ZoneChannels` (what the generator writes per zone), `AddToZone / RemoveFromZone / MoveZoneMember`, `ZoneKindOf`, `ChannelRef`. UI: `ZoneMembersDialog` (Windows), `ZoneMembersWindow` (Mac) |
| (`Models.cs`) | Zone talkgroup sets: `ZoneInfo.Talkgroups`, `RepeaterTalkgroup.FromZone`, `Project.AddZoneTalkgroup / RemoveZoneTalkgroup / SetZoneTalkgroupSlot / ApplyZoneTalkgroups`, `DefaultSlot`. Repeater location fields (`City`, `State`, `Country`, `County`, `AreaCode`, `Latitude/Longitude`, `SourceId`). All optional, never read by the generator |

`src/App` (WinForms)

| File | What it does |
| --- | --- |
| `Program.cs` | Entry point, DPI scale, CLI (`--generate`, `--import`), dev switches (`--map`, `--map-snapshot`, `--ui-walkthrough`, see below), crash handler |
| `Session.cs` | `Session` (project, path, dirty flag, CPS format; events `Changed`, `Replaced`, `TalkgroupsChanged`) and `AppSettings` (`%APPDATA%\DMR Codeplug Builder\settings.txt`, or `CODEPLUGBUILDER_SETTINGS`; custom CPS format folder; keys `LastProject`, `Region`, `Callsign`...) |
| `MainForm.cs` | Menu, start page / wizard / workspace (tabs + status bar) switching, Generate / Import / Open / Save flows |
| `StartPage.cs` | First screen when there's no project to reopen: new codeplug (wizard), import, open, empty |
| `Wizard.cs` | `NewCodeplugWizard` (File > New codeplug): `RegionStep` → `RadioStep` → `AreasStep` → `ZonesStep` → `ZoneTalkgroupsStep`; `WizardState.Build()` makes the project |
| `RegionMap.cs`, `RegionPicker.cs` | GDI+ Mercator map (pan/zoom, pick countries/states/counties, dots, badges) and its toolbar (levels, Find, zoom) |
| `RegionDownload.cs` | Background RadioID download for map areas (US states by state, elsewhere by country), places each repeater, cached for the run |
| `AreaChooser.cs` | Map + List tabs for picking downloaded repeaters by area or one by one (wizard step 3 and Add from map) |
| `ZoneTalkgroupsEditor.cs` | One zone's talkgroups: ticked = zone set on every repeater; search project + BrandMeister; starter set; copy to all zones (wizard step 5 and the Zones tab) |
| `AddFromMapDialog.cs` | *Repeaters > Add from map* (and `RegionDialog`) |
| `DevTools.cs` | `--ui-walkthrough`: drives the start page and whole wizard off screen with real downloads, saves PNGs, generates the CSVs |
| `RepeatersPage.cs` + `RepeaterEditor.cs` | Repeater list and the editor (fields, offset helper, talkgroup assignment grid). The editor is shared with `HotspotPage.cs` |
| `TalkgroupsPage.cs` | Master talkgroup grid; changing an ID rewrites references on every repeater |
| `ZonesPage.cs`, `SettingsPage.cs`, `IssuesDialog.cs`, `Ui.cs` | Zone order/rename/A-B, settings, problem list dialog, layout helpers (`Ui.SetUpGrid` for every DataGridView) |
| `Online.cs` | HTTP (`HttpWebRequest`, 30 s timeout), all-pages RadioID download, BrandMeister names cached a week in `%APPDATA%`, callsign → DMR ID lookup flow |
| `OnlineRepeaterDialog.cs`, `TalkgroupBrowserDialog.cs` | *Find repeaters online* (state → tick → add, default talkgroups grid) and *Browse BrandMeister* |
| `RepeaterBookDialog.cs` | *Repeaters > From RepeaterBook*: opens repeaterbook.com, picks the CHIRP export up from Downloads (or Choose file), list with ticks, one zone / per county / per city. Dev check: `--repeaterbook-snapshot project.cpb export.csv folder` |

**ListView checkboxes:** on Windows a `ListView` raises `ItemChecked` for every item while it creates its
handle. Every checkbox ListView here ignores those (`creatingHandle` flag set in `HandleCreated`, cleared
by `BeginInvoke`); copy that pattern for new ones.

**Zone talkgroup sets** are the "pick talkgroups per zone" feature. The generator never reads them: they only
add `RepeaterTalkgroup` entries (marked `FromZone`) to the repeaters in the zone, so the CSV output still comes
from `Repeater.Talkgroups` alone and the round trip is unaffected. Unticking takes back only `FromZone`
channels; *Remove from zone* removes the talkgroup from every repeater in the zone. A repeater that changes zone
(`RepeaterEditor.ApplyZone`) or is added online gets `ApplyZoneTalkgroups`.

**Mac version (started 2026-10-06):** `src/Mac` is an Avalonia UI on .NET 8 sharing `src/Core` and the UI-free App files
(`Session`, `Online`, `RegionDownload`, `RadioPort`, `WizardState`, linked, not copied). Those five files must stay free of WinForms; the
WinForms bit of `Online` lives in `OnlineUi.cs`. It may use NuGet (Avalonia, System.IO.Ports); the Windows app may not.
Package with `tools/package-mac.ps1`. Status, first-launch steps and the port order are in `docs/MAC.md`.

**Highways** are in `src/Core/Geo/roads.gz` (~0.5 MB, embedded, written by the same `tools/GeoBuild` run from `ne_10m_roads.zip`);
`GeoAtlas.BuiltIn().Roads`, drawn by `RegionMap.DrawRoads`, switch in `RegionPicker`. The map works without the file.

**The map atlas** (`src/Core/Geo/atlas.gz`, ~3.7 MB, embedded) is built by `tools/GeoBuild` from the zips in
`tools/geodata` (not in the repo, read without extracting; sources and URLs in HANDOFF 4c): `dotnet run --project tools\GeoBuild -c Release -- tools\geodata src\Core\Geo\atlas.gz`.
Don't name embedded resources `*.bin.gz`: MSBuild takes "bin" for a culture and makes a satellite assembly.

**Checking the UI without clicking:** `CodeplugBuilder.exe --ui-walkthrough <folder> [callsign]` (set
`CODEPLUGBUILDER_SETTINGS` to a scratch folder first so the user's settings and LastProject stay untouched) and
`--map-snapshot <folder>`. Both draw real forms off screen with `DrawToBitmap`; they can't test real mouse clicks.

**Data flow:** pages mutate `Session.Project` directly and call `session.MarkDirty()`. That updates the
title and restarts a 400 ms timer; `MainForm.RefreshStatus()` then runs the generator and validator for
the status bar. Generate runs `Validator`, then `CodeplugGenerator`, then `WriteTo`. Pages rebind on
`Session.Replaced` (new/open/import). `RepeaterEditor` guards with its `loading` flag while binding, so
programmatic changes don't fire edits.

## Status

- **1.5 (in progress, from 2026-10-08):** roadmap HANDOFF 7, items 7-12 in the order 7, 8, 9, 12, 10, 11. Done: **zones as views**
  (item 7): zone kinds (Area, Favorites, Talkgroup, Utility), a channel in several zones (`ZoneInfo.Members`, `Project.ZoneChannels`),
  talkgroup zones (`RuleTalkgroups` / `RuleZones`), the importer keeps every zone a CPS channel is in, Zones tab and *Add to zone...* on
  Windows and Mac. Older projects' output unchanged. Still to check in the CPS: a multi-zone Zone.CSV import (HANDOFF 7, "To check").
  **Order that matches use** (item 8): `Project.Home` (Settings > Home town, callsign lookup, wizard), distance and direction on the
  Repeaters list, Zones > *Sort by distance* (`ZoneOrder`), *Local first* talkgroup order (`TalkgroupOrder`), talkgroup zones nearest first
  with an optional radius. Nothing reorders an existing project unless the user clicks.
  **Check for updates** (item 12, done before 9 because 9 needs the CPS): File > Check repeaters for updates compares repeaters added
  from RadioID.net with RadioID.net and BrandMeister now (frequency, color code, talkgroups, off air, delisted, new repeaters in their
  counties), ticks, applies the ticked ones (`UpdateCheck`, `Online.CheckForUpdates`; dev `--check-updates`). Windows and Mac. 108 tests.
- **1.4 (in progress, from 2026-10-08):** roadmap HANDOFF 7. Done: **safety checks** (item 5): warnings for transmit outside the
  amateur bands unless receive only (the user's "Tall Oaks Ranch" 154.570 MURS channel is the one warning their codeplug gets),
  and for the hotspot or DMR simplex in the satellite sub-bands, on or next to APRS/calling/ISS frequencies, or (US) in the
  other Part 97.201(b) segments. Output unchanged. **Polite transmit** (item 3): `GenerationOptions.PoliteTransmit` (Settings,
  Windows and Mac) gives DMR repeater channels TX permit Same Color Code, hotspot/DMR simplex Always, analog Off; on for new
  projects, off for older files and for CPS imports that weren't polite already (round trip unchanged). *Write codeplug to
  radio* offers *Send talker alias* once (`RecommendedSettings`; a No is remembered). Still to check in the CPS and on the
  radio: HANDOFF 7, "To check". **Simplex preset** (item 6): `Presets.AddSimplex` (FM calling 146.520/446.000, five DMR simplex
  frequencies on TG 99, zone "Simplex"), *Add simplex* on the Repeaters tab and a wizard checkbox (US/Canada), Windows and Mac;
  simplex-only zones take no zone talkgroups. **Caller names and APRS** (items 1-2): Settings > Caller names (world / countries / US
  states from RadioID.net) and Settings > APRS add DigitalContactList.CSV and APRS.CSV on Export; both checked in the CPS by import and
  re-export (HANDOFF 4). **Repeater health** (item 4): BrandMeister-only listings not heard for over a year
  are grey and skipped by area clicks (checked in the background after the download, rate-limited), and picked BrandMeister
  repeaters get BrandMeister's static talkgroups when added. The `--ui-walkthrough` footer can lag (the harness has no WinForms
  context after the region step; the real app does). 90 tests.
- **1.3.1 (2026-10-07), review pass:** wizard step 4 re-zones CHIRP (analog) repeaters too, not only DMR ones (weather channels keep
  their zone; `Presets.IsNoaaWeather`), and *By band* names analog zones "2m FM" / "70cm FM". DMR and analog repeaters added
  with one fixed zone name get the same (shortened, not cut) zone. Settings caps talkgroups per RX list at the radio's 64, and
  `GenerationOptions.KeepWithinRadioLimits` repairs older projects on load. The analog name preview matches the generated name.
  Saving replaces the project file in one step (`File.Replace`). Help, tooltips and docs updated for 1.3's Export menu and
  shortened names. Also in 1.3.1, from the first real-Mac tests: past the radio's 250 RX group lists, repeaters with the same
  talkgroups share a list (others get none); the Mac wizard regroups zones with the option that was clicked. 79 tests.
- **1.3 (2026-10-06):** renamed W6OZZ CPS (display name only; exe, settings and backup folders unchanged). Bottom bar has
  *Read from radio* / *Write to radio*; CSV export moved to the new **Export** menu (Ctrl+G). Names are shortened, not cut
  (`Naming.Fit`), both-slot talkgroups get TS1/TS2, clashes try smarter names before " 2"; unnamed talkgroups are named from
  repeater notes, other repeaters' IDs and RadioID.net lookups (`TalkgroupNames`, `Online.NameTalkgroups`); same-named
  counties in different states get separate zones. Old projects keep their channel names (FileVersion 2 migration). NOAA
  preset in WX1-WX7 order (was by frequency, so the radio showed WX2, WX4, WX5, WX3...); `Presets.SortNoaaWeather` repairs
  saved projects on load. Repeaters are placed by their DMR-MARC map coordinates (RadioID `/api/rptr/map/`), and the wizard
  warns before going on with no repeaters picked. *Repeaters > From RepeaterBook* imports a
  RepeaterBook CHIRP export (analog; no API token), checked on the user's Brown County export. The map step (wizard and
  Add from map) also takes CHIRP files, one per state (*Add analog repeaters from CHIRP files...*): green dots, picked
  with the DMR ones; also reads CHIRP's own RepeaterBook-query CSV (county and state from the comment). Dev:
  `CODEPLUGBUILDER_WALK_CHIRP=<Texas csv>` mixes a file into `--ui-walkthrough`. 75 tests.
- **Radio read/write (in progress, 2026-10-06):** step 1 (read + decode) built as backend + dev switches
  `--radio-read/--radio-decode/--radio-compare` in `RadioCli.cs`; 53 tests. Own code only (qdmr/dmrconfig are GPL).
  A real read decodes byte-identical to the CPS export; writes copy the CPS write exactly (USB capture) and a settings
  write verified on the radio (HANDOFF 4d). In the app since 2026-10-06 (user asked): **Radio** menu with *Read codeplug
  from radio* (Ctrl+R: read, keep image + CSVs in DocumentsDMR Codeplug BuilderRadio reads, import as a project) and
  *Radio settings* (RadioSettingsForm: read, edit, Write to radio with confirmation, before/after images kept).
  **Whole-codeplug writes (2026-10-06):** `RadioEncoder` writes the six CPS tables into a full read; its image of a test
  codeplug equals the CPS's own write of it in all 5,990 blocks, and the first app write (the user's codeplug) read back
  byte for byte. Menu: *Radio > Write codeplug to radio* and *Restore codeplug from a backup* (MainForm). Dev switch
  `--radio-encode`. 65 tests. Rules learned from the CPS are in HANDOFF 4d ("Writing a whole codeplug").

- **1.2 (2026-10-06):** start page, new-codeplug wizard on a built-in world map (region → radio → areas →
  zones → zone talkgroups), *Repeaters > Add from map*, zone talkgroup sets on the Zones tab, auto zones by
  county/city/state/country/band, stable channel numbers (kept from the CPS import and from the first Generate;
  Settings > Renumber), merge mode (Settings > Keep channels made in the CPS). 47 tests. `--ui-walkthrough` ran the whole wizard on Texas with real
  downloads (353 repeaters, 18 picked, 95 channels, no validation errors). **Not yet clicked through on
  screen** (computer-use access to the app was declined): mouse interaction, hover, wheel zoom, drag,
  scaling and the "*" on open still need a hand check. Outside the US about half the repeaters are placed
  by city (GeoNames cities15000 only has towns of 15,000+); the rest get their state/province or country.
- **1.1 (2026-10-06):** online data. *File > New from online data* and *Repeaters > Find online* (RadioID.net
  DMR repeaters with their published talkgroups), *Talkgroups > Browse BrandMeister*, *Settings > Look up by
  callsign*, NOAA preset. 34 tests (7 online ones use trimmed real API responses); the importer was also run
  over all 353 Texas repeaters (1898 channels, no validation errors). Driven by hand in the real Windows app.
  Analog repeaters online are **not** done: RepeaterBook needs an approved per-user token (see HANDOFF).
- **1.0 verified:** 27 engine tests pass, including the byte-for-byte round trip of the user's codeplug. The
  real net48 exe was run under Mono + Xvfb and driven with xdotool: add repeater, new zone, talkgroups,
  hotspot, talkgroup ID edit, import, save, generate.
- **Verified on Windows (2026-10-06):** builds with the .NET 8 SDK; the generated codeplug imports into
  CPS 1.22e with the 5-entry `.LST`, no conflicts, and each file replaces its whole list (no leftover
  channels). Two Windows-only UI bugs fixed (dirty-on-open, highlighted combo text); see HANDOFF.
- **Verified in the CPS (2026-10-06, later):** a wizard codeplug with new talkgroups imports and re-exports byte for
  byte. The scan-list template row is now a real CPS row (same values as the old defaults, output unchanged).
  Merge mode is verified in the real CPS (hand-made channel, number, zone and the CPS's scan list all survived),
  and so is a scan-lists-on import (all six files re-export byte for byte). Scan lists are now on by default for new
  codeplugs; `CpsImporter` sets them off so CPS-made scan lists survive and the round trip holds.
- **Not verified yet:** high-DPI scaling other than the user's own.

## Recommended next steps (details and more in HANDOFF)

The plan is HANDOFF section 7 (rewritten 2026-10-08): **milestone 1.4** (caller names, APRS from the callsign, polite
transmit, repeater health from BrandMeister, safety checks, a simplex preset), then **1.5** (zones as views, order by
distance, scan lists 2-8, GPS zone switching, route builder, check for updates). Also still open:

1. Click through the wizard and Add from map on screen (hover, wheel zoom, drag, county picking, list ticks).
2. Analog repeaters online: RepeaterBook with a user-supplied token (RadioID.net and BrandMeister are done).
