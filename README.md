# DMR Codeplug Builder

A small Windows program that builds a CSV codeplug for the **BTECH DMR-6X2 PRO** customer programming
software (CPS). You manage talkgroups, repeaters and your hotspot in a simple GUI; it writes the CSV
files plus a `.LST` file list that the CPS imports in one step.

It runs on Windows 10 and 11 with the .NET Framework 4.8 that Windows already includes, so there is
nothing to install.

A Mac version (an Avalonia app over the same engine) is in progress in `src/Mac`; see `docs/MAC.md`.

> **Unofficial.** This is an independent hobby project. It is not made by, endorsed by or affiliated with BTECH,
> RepeaterBook, RadioID.net or BrandMeister. Check what it writes before you load it into your radio, and keep
> a backup of your codeplug (a CPS `.rdt` file); you use it at your own risk.


## Using it

**From scratch:** *File > New codeplug* (or *Set up a new codeplug* on the start page) is a five-step
wizard on a built-in world map: pick the states or countries you use (their DMR repeaters download from
RadioID.net in the background), look up your DMR ID from your callsign, click counties/states/countries
to take their repeaters (or tick single repeaters on a list), choose how zones are made (per county,
city, state, country, band, or one zone), then pick talkgroups per zone. Repeaters bring the
talkgroups their owners list on RadioID.net (named from BrandMeister's list); talkgroups ticked for a
zone go on every repeater in it, including ones added later. *Repeaters > Add from map...* does the same
for an existing project (*Find online...* is the older list by state), the Zones tab has the same
per-zone talkgroup editor, and *Talkgroups > Browse BrandMeister...* searches every BrandMeister talkgroup.

1. Run `CodeplugBuilder.exe`. It reopens the project you used last (or the one `.cpb` file next to the
   program); otherwise the start page offers the wizard, an import from the CPS, or opening a project.
2. **Repeaters tab.** *Add DMR repeater* or *Add analog*. Type the receive frequency (the repeater
   output); the transmit frequency follows the usual band offset, which you can change.
   - **Zone:** pick an existing zone or type a new name. Every repeater with the same zone name lands
     in that zone automatically, in list order.
   - **Talkgroups:** pick talkgroups on the left and click *Add on slot 1* or *Add on slot 2*. Each one
     becomes a channel named *prefix + talkgroup* (for example `W5FC Texas`), shortened to 16 characters
     when needed (common abbreviations first, such as `TX` for Texas, and numbers at the end are never cut).
     Type in the *Channel name* column to use your own name; clear it to go back to automatic.
   - Untick a repeater in the list to keep it in the project but leave it out of the codeplug.
   - *Add NOAA weather* adds the seven weather channels (receive only). *Add simplex* adds the 146.520 and 446.000 FM
     calling frequencies and five DMR simplex frequencies (441.000, 446.500, 446.075, 145.790, 145.510; color code 1,
     slot 1, talkgroup 99) in a "Simplex" zone. That DMR list is widely shared but not official: change it to what
     your area uses.
3. **Hotspot tab.** Tick *Include my hotspot*, set its frequency (Offset *Simplex* for a simplex
   MMDVM hotspot), color code, and the talkgroups you use on it.
4. **Talkgroups tab.** The master list (the CPS Talk Groups list). Add, rename, change IDs (repeaters
   follow), import from a CSV, or add common BrandMeister talkgroups.
5. **Zones tab.** Zone order on the radio, renaming, which channel each zone opens on, and the
   talkgroups every repeater in a zone carries.
6. **Settings tab.** Your Radio ID name and DMR ID, receive group lists, scan lists, polite transmit, and limits.
   *Polite transmit* (on for new codeplugs) sets DMR repeater channels to TX permit "Same Color Code", so the radio
   won't key over a call already on that repeater's slot; the hotspot and DMR simplex stay on "Always".
   The problems list also warns about channels that transmit outside the amateur bands (a MURS or GMRS
   channel without *Receive only*), and about a hotspot or DMR simplex channel in a satellite sub-band
   (145.8-146, 435-438 MHz) or on an APRS or calling frequency.
   *Caller names* (off by default) adds DigitalContactList.CSV with DMR users from RadioID.net (the whole world, countries
   or US states), so the radio shows who is calling. *APRS* (off by default) adds APRS.CSV with your callsign, SSID and
   frequency. Importing either replaces that part of the CPS's codeplug; after an APRS import, check the CPS's APRS screen.
7. Click **Export > Export CSV files for the CPS** (Ctrl+G) and save the `.LST`. The CSVs are saved next to it.
8. In the CPS: open your codeplug, **Tool > Import > Import From File List**, pick the `.LST`, click
   **Import**, check a few channels, then write to the radio.

The import replaces the CPS's **Channel, Zone, Talk Groups, Receive Group Call List and Radio ID
List**, plus the **Scan List** when scan lists are on (the default for new codeplugs; off for one imported
from the CPS, so the scan lists you made there stay). Everything else in the codeplug, such as optional
settings, APRS and the digital contact database, stays as it was.

If the file list won't import, use **Tool > Import** and load each CSV yourself, in this order:
TalkGroups, ReceiveGroupCallList, RadioIDList, Channel, ScanList, Zone.

### Writing straight to the radio (no CPS)

With the programming cable connected and the BTECH CPS closed, **Radio > Write codeplug to radio...** reads the
radio, writes this project's channels, zones, talkgroups, receive group lists, scan lists and radio ID into it, shows
what will change, and after you confirm sends it the way the CPS does and checks it. The radio's settings stay as
they are, except that it offers once to turn on *Send talker alias* (so other radios and network dashboards show your
name), if it's off. What was on the radio is saved first (Documents\DMR Codeplug Builder\Radio reads), and **Radio > Restore
codeplug from a backup...** writes any saved read back. Keep a CPS codeplug file (.rdt) as well, just in case.
**Radio > Read codeplug from radio** opens what's on the radio as a project. The **Read from radio** and **Write to radio** buttons at the bottom right of the window do the same.

### Starting from what's in the radio

In the CPS, read the radio, then **Tool > Export > Export All (Default CSV FileName)**. In this program,
**File > Import from CPS export...** and pick the exported `.LST`. Digital channels on the same
frequency and color code become one repeater; the biggest simplex group of digital channels becomes the
hotspot; analog channels, zones and channel names are kept.

## How it works

- **The CPS's own rows are the template.** Generated rows are copies of rows the CPS exported, with only
  the fields this program manages changed (name, frequencies, power, bandwidth, tones, contact, color
  code, slot, receive group list, TX prohibit, and TX permit with polite transmit on). Columns it doesn't manage keep the CPS defaults. The
  built-in layout comes from a CPS 1.22 export; if a CPS update changes the columns, export again and
  use **Settings > Load from CPS export...**.
- **Names link everything.** This CPS's Zone and receive group list files refer to channels and
  talkgroups by name only, so every channel name is made unique (a number is added if two would clash)
  and kept to 16 plain ASCII characters, without `|` or `"`.
- The VFO A/B rows (channel numbers 4001 and 4002) are always written, pointed at contacts that exist.
- One receive group list per DMR repeater (its group-call talkgroups), so its channels play every
  talkgroup the repeater carries on that slot. Turn this off in Settings to hear only the channel's
  own talkgroup.
- Zones larger than 250 channels are split into numbered zones.

### Confirmed and not yet confirmed in the CPS

- Confirmed (CPS 1.22e): the 5-file `.LST` imports in one step, and each imported list replaces the
  CPS's list (no leftover channels). Channels made by hand in the CPS are therefore removed on import,
  unless you turn on **Settings > Keep channels, zones and talkgroups made in the CPS** and point it at a fresh
  **Export All** of your codeplug: then they're written back as they were (same channel numbers), with the zones,
  talkgroups and lists they use. Channels keep their numbers between generations either way.
- Confirmed: a codeplug that brings new talkgroups imports cleanly and exports back unchanged.
- Confirmed: scan lists (one per zone) import cleanly and export back unchanged. They copy
  the settings of a scan list made in the CPS. With scan lists off, scan lists you made in the CPS stay as
  long as their channels keep their names.

## Building from source

- **Windows:** open `CodeplugBuilder.sln` in Visual Studio 2022 (".NET desktop development" workload) and
  build *Release*, or run `dotnet build src/App/CodeplugBuilder.csproj -c Release`.
- **Linux:** `bash build.sh [path-to-CPS-export]` (needs the .NET 8 SDK and Mono's reference assemblies;
  see the script). The optional export folder turns on the round-trip tests.

Output: `src/App/bin/Release/net48/CodeplugBuilder.exe`.

Tests: `dotnet run --project tests -- [path-to-CPS-export]`. Without an export folder the round-trip tests are
skipped. GitHub Actions builds and tests every push (`.github/workflows/build.yml`); pushing a tag like `v1.3.1`
makes a release "W6OZZ CPS 1.3.1" with `W6OZZ-CPS-windows-1.3.1.zip` (the exe, its config and `docs/README.txt`;
`release.yml`). Set the same version in both `.csproj` files first.

### Command line

```
CodeplugBuilder.exe --generate "My 6X2 Codeplug.cpb" OutputFolder [--format CpsExportFolder] [--merge CpsExportFolder]
CodeplugBuilder.exe --import CpsExportFolder "My 6X2 Codeplug.cpb"
```

A log file is written next to the output (a Windows GUI program has no console).

## Working on it

`docs/DEVELOPMENT.md` has the build commands, how the code is wired and the rules that keep the CSVs importable;
`docs/HANDOFF.md` has the design decisions, the full CPS CSV format notes, what has been verified, and a prioritized
list of improvements. `docs/MAC.md` covers the Mac version.

## Layout

| Folder | What's there |
| --- | --- |
| `src/Core` | The engine, no UI: models, CSV reader/writer, CPS format, generator, importer, validation, online data parsing, the map atlas and automatic zones, direct radio access (`Radio/`), built-in CPS templates and `Geo/atlas.gz` + `Geo/roads.gz` |
| `src/App` | The Windows Forms GUI (start page, wizard, map, tabs, radio read/write) |
| `src/Mac` | The Mac version: an Avalonia GUI over the same engine (also runs on Windows and Linux) |
| `tests` | Engine tests (a console runner). The key one imports a real CPS export and regenerates it, checking Channel, Zone, TalkGroups and RadioIDList come back identical |
| `tools/GeoBuild` | Builds `src/Core/Geo/atlas.gz` and `roads.gz` from the public boundary, road and place files (zips in `tools/geodata`, not in the repo; see HANDOFF 4c) |
| `tools/RepeaterList` | Writes one CSV of DMR repeaters per US state from RadioID.net's map and BrandMeister's device list (a data check, not part of the app) |
| `tools/package-mac.ps1` | Packages the Mac version as `.app` bundles from Windows |
| `docs` | `DEVELOPMENT.md` (build, rules, architecture), `HANDOFF.md` (background, format reference, verification status, roadmap), `MAC.md` and `MAC-TESTING.md` (the Mac version), `README.txt` (goes in the release zip) |

Projects are saved as readable JSON (`.cpb`). The built-in map uses US Census and Natural Earth data
(public domain) and GeoNames place names (CC BY 4.0).

## Data sources and credits

- **DMR repeaters and users:** [RadioID.net](https://radioid.net) (repeater lists, DMR ID lookup).
- **Talkgroup names and repeaters on the network:** [BrandMeister](https://brandmeister.network).
- **Analog repeaters:** RepeaterBook's own CHIRP export, which you download yourself from
  [repeaterbook.com](https://www.repeaterbook.com) and import into the program. *Data courtesy of RepeaterBook.com.*
  This program does not call RepeaterBook's API, ships no RepeaterBook data, and has no bulk-download feature.
  If an API client is added later, each user will use their own RepeaterBook token (never a shared one), and
  requests will be limited to the areas the user picks.
- **Map:** boundaries from the US Census Bureau and Natural Earth (public domain); highways from Natural Earth;
  place names from [GeoNames](https://www.geonames.org), licensed under CC BY 4.0.

## License

MIT, see [LICENSE](LICENSE).
