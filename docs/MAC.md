# Mac version

A second front end for the same engine: `src/Mac` (project `CodeplugBuilder.Mac.csproj`) is an **Avalonia** UI on
**.NET 8**. It compiles `src/Core` and a few UI-free files from `src/App` (`Session.cs`, `Online.cs`,
`RegionDownload.cs`, `RadioPort.cs`, `WizardState.cs`), so the engine, the CPS CSV rules, the online data and the radio protocol are
shared, not copied. The Windows app (WinForms, .NET Framework 4.8, zero NuGet) is unchanged. The Mac project uses
NuGet (Avalonia 11.3, its DataGrid, System.IO.Ports 8). It runs on Windows and Linux too, which is how it is built
and checked without a Mac.

## Build, run, package

```
dotnet build src\Mac\CodeplugBuilder.Mac.csproj -c Release
src\Mac\bin\Release\net8.0\CodeplugBuilderMac.exe [project.cpb]          # runs on Windows for checking
powershell -File tools\package-mac.ps1                                    # dist\W6OZZ-CPS-mac-arm64.zip and -x64.zip
```

`--snapshot out.png [--tab n]` draws the window to a PNG and quits (like the Windows app's dev switches). Use
`CODEPLUGBUILDER_SETTINGS=<scratch folder>` so the real settings and LastProject stay untouched.

`package-mac.ps1` cross-publishes self-contained for `osx-arm64` and `osx-x64` (about 44 MB each; the first run
downloads the macOS runtime packs), wraps the output in `W6OZZ CPS.app` with an `Info.plist`, and zips it with Unix
permissions (a normal Windows zip loses the executable bit).

### First launch on a Mac

The app is **not signed or notarized**. After unzipping and moving it to Applications:

1. Right-click `W6OZZ CPS.app` > Open > Open (once). If macOS says it is damaged, run
   `xattr -dr com.apple.quarantine "/Applications/W6OZZ CPS.app"` first.
2. If it closes at once on an Apple Silicon Mac (a missing signature), run
   `codesign --force --deep --sign - "/Applications/W6OZZ CPS.app"` and open it again.

Project files (`.cpb`) are the same on both systems. Settings live in `~/.config/DMR Codeplug Builder`, documents
and radio backups in `~/Documents/DMR Codeplug Builder` (.NET's folder mapping on macOS).

## The radio on macOS

The radio's USB serial port (CDC, vendor 28E9, product 018A) appears as `/dev/cu.usbmodem*`. `RadioPort.FindRadioPorts`
takes those (and `/dev/ttyACM*` on Linux) when it is not on Windows, because there is no registry to ask for the USB ID.
The protocol code (`AnytoneLink`) works on any `Stream`, and `SerialPort` from System.IO.Ports opens the port at
115200 8N1 with DTR/RTS like on Windows. **Nothing about the radio has been run on a Mac yet**: the first real test is
Radio > Read codeplug from radio. If it finds no radio, check `ls /dev/cu.*` with the radio on and the cable in, and
close the BTECH CPS (it can't run on a Mac anyway; it is Windows-only).

## Status (milestone 1, 2026-10-06)

Done and checked by running it (on Windows): window, start page, menus with shortcuts (Cmd on Mac, Ctrl elsewhere), open /
save / save as, import from a CPS export folder, export the CSV files + `.LST` (same validation and generator as the
Windows app), the Talkgroups tab (edit names, IDs with references following, call type, add, delete), read-only lists of
the Settings tab (radio ID name and number), problem check and status line, and the radio flows
(read, write with the review dialog and backups, restore), all through `Dialogs.cs`.

**Milestone 2 (same day):** the Repeaters tab (list with on/off ticks, zone filter, add DMR / analog, duplicate, delete, move,
NOAA weather, and the full editor: frequencies with offset helper and band checks, tones, color code, zone with its talkgroup
set applied, the talkgroup assignment grid with slots, names, move, switch slot), the Hotspot tab (same editor), and the Zones
tab (order, rename / merge, A/B channels, channel list, and the zone talkgroup editor with search over the project and
BrandMeister, ticks, slots, starter set, copy to all zones). Files: `RepeaterEditorView.cs`, `RepeatersTab.cs`
(also `HotspotTab`), `ZonesTab.cs` (also `ZoneTalkgroupsView`), `UiKit.cs`. The Windows files they follow are
`RepeaterEditor.cs`, `RepeatersPage.cs`, `HotspotPage.cs`, `ZonesPage.cs`, `ZoneTalkgroupsEditor.cs`: keep the two in step
when one changes.

**Milestone 3 (same day):** the map and Add from map. `RegionMapView.cs` is the Windows `RegionMap` in Avalonia (same Mercator view,
zoom levels, county/state/country picking, highways with route tags, dots, badges, labels; drawn with `StreamGeometry` and
`DrawingContext`), `RegionPickerView.cs` its toolbar, `AreaChooserView.cs` the Map + List picker (with CHIRP files), and
`AddFromMapWindow.cs` (also `RegionWindow`) *Repeaters > Add from map*. Checked with real downloads: `--addmap-check out.png`
opened Texas (356 repeaters, 3 of them BrandMeister-only), picked Tom Green County (8 repeaters) and drew it. Dev switches
`--map` and `--map-snapshot folder` show the map alone.

**Milestone 4 (same day):** the new-codeplug wizard (`WizardView.cs`: region, radio, areas, zones, zone talkgroups; start page choice and
File > New codeplug). The UI-free `WizardState` moved out of the Windows `Wizard.cs` into `WizardState.cs`, which both apps compile.
Checked with `--wizard-walkthrough folder [callsign]`, which drives all five steps with real downloads and the real callsign
lookup (Texas, Tom Green and Travis counties, 356 repeaters downloaded, W6OZZ found), takes a picture per step, then validates
and generates the result: 13 repeaters, 41 channels, 0 errors, CSVs written.

Not ported yet, in the order I would do them:

1. (done) editors and zones, the map, Add from map, the wizard.
2. Find repeaters online, Browse BrandMeister, From RepeaterBook.
4. Settings tab extras (CPS format folder, merge mode, renumber), radio settings editor, About with the credits.
5. App icon (`.icns`), signing and notarization if the app is shared outside this Mac.

Known duplication to remove later: the Windows `MainForm` and `MainWindow` each hold the open/save/import/generate/radio
flows. If the Windows app keeps being developed, move those flows into a UI-free class in `src/Core` that both call.

## Getting ready to test on a Mac (2026-10-07)

- `docs/MAC-TESTING.md`: the step-by-step first run on a real Mac, with what to expect and what to report.
- **Help > Diagnostics** (and `CodeplugBuilderMac --diagnose` in Terminal): a copyable report of the system, writable folders, map data,
  serial ports, the radio's USB entry (`system_profiler`, vendor 0x28e9) and whether RadioID.net and BrandMeister answer.
- **Radio > Radio port...**: pick the serial port by hand if automatic detection (`/dev/cu.usbmodem*`) misses the radio; it is
  remembered (`RadioPort` in the settings file). After a write the verify step now follows the radio if it comes back under another name.
- Map: trackpad pinch zoom (untried), alongside two-finger scroll.
- App icon (`src/Mac/app.icns`, made from the Windows icon) and `src/Mac/Info.plist.in`.
- `.github/workflows/mac.yml`: a macOS GitHub runner builds the app, runs the engine tests, prints the diagnostics report, draws the
  start page, the map and every wizard step to PNGs, and packages ad-hoc signed `.app` zips (artifacts of the run).

## First real-Mac test (2026-10-07)

Run on an Apple Silicon MacBook Air (macOS 25.6 / Darwin 25.6.0, arm64, .NET 8.0.425 SDK, runtime 8.0.31), built from source with
`dotnet build src/Mac/CodeplugBuilder.Mac.csproj -c Release`. Radio: BTECH DMR-6X2 PRO, operator W6OZZ.

**Verified on a real Mac**

- Build: 0 warnings, 0 errors. Engine tests: 66 passed, 0 failed, 12 skipped (no CPS export folder).
- `--diagnose`: system, folders (all writable), map data (251 countries, 4,594 states, 3,222 US counties, 63,003 places, 18,777
  highways), RadioID.net and BrandMeister lookups all OK.
- The app starts and shows the start page and map; the new-codeplug wizard runs.
- Wizard zones step: "One zone per country" etc. now regroup correctly (see the fix below).
- **Radio detection:** with the cable in and the radio on, the radio appears as `/dev/cu.usbmodem0000000100001` and
  `RadioPort.FindRadioPorts` picks it ("Found as the radio"). `ioreg` shows it as "GD32 Virtual ComPort in FS Mode",
  idVendor 0x28E9, idProduct 0x018A.
- **Radio > Read codeplug from radio works.** It wrote `Radio reads/<date time>/radio.img` (header `CPBIMG01`, model `D6X2UV2`) and the
  six CSVs. The result matched what is on the radio: 17 channels (7 NOAA weather, 8 hotspot), zones Hotspot and Weather, 8 talkgroups,
  the Hotspot receive group list, two scan lists and radio ID 3226509 "Austin W6OZZ". Read only; nothing was written to the radio.
- **Radio > Write codeplug to radio works.** A small project (6 zones: Aptos, Bonny Doon, Capitola, Oakland, Santa Cruz, Santa Maria; 49 new
  channels plus the 2 VFO rows; it replaced the hotspot and weather channels) was written after a CPS backup (`.rdt`) was made on Windows.
  The app checked 5,490 blocks against the earlier read first, wrote them (722 changed), and verified them after the radio reconnected
  (`write.log`, `before.img`, `written.img` kept in `Radio reads/<date time> write/`). A separate read afterwards gave six CSVs
  byte-identical to what was written, and an image that differs from `written.img` only in the 8 timestamp bytes in its header. The radio
  restarted and works with the new codeplug.
- Diagnostics USB entry: with the radio connected the report shows "GD32 Virtual ComPort in FS Mode", idVendor 10473 (0x28E9),
  idProduct 394 (0x018A).

**Fixed**

- Diagnostics USB line (`Diagnostics.cs`): it said "no device with vendor 0x28e9" with the radio connected, because
  `system_profiler SPUSBDataType` prints nothing on recent macOS. It now reads the I/O registry (`ioreg -r -c IOUSBHostDevice -l`).
- Wizard zones step (`WizardView.cs`): clicking a grouping option regrouped with the first ticked option instead of the one clicked,
  because the new button's checked event fires before the old one is unchecked. "One zone per country" gave state zones. `Rezone` now
  takes the clicked scheme.
- Writing a codeplug with more than 250 digital repeaters failed with "RX group list ... outside 1-250" (the generator made one RX group
  list per repeater and the radio holds 250). Past 250, repeaters now share an earlier list with the same talkgroups, or get none, and
  a note says so. The refusal happened before anything was written to the radio.

**Not verified on a real Mac yet**

- Radio > Restore codeplug from a backup (not tried on a Mac), and the radio settings editor. Keep a CPS backup (`.rdt`) made on Windows
  before any write.
- Map interaction (drag, wheel zoom, trackpad pinch, county picking), the rest of the wizard (callsign lookup, county picking,
  talkgroups, Finish), the Repeaters / Hotspot / Zones tabs, Save / Open and Export CSV on a real Mac.
- The signed `.app` bundle from the GitHub workflow.

**Known issues**

- A zone for "One zone per country" is named "United States of" (the country name is cut to 16 characters).
