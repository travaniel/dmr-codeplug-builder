# Mac version

A second front end for the same engine: `src/Mac` (project `CodeplugBuilder.Mac.csproj`) is an **Avalonia** UI on
**.NET 8**. It compiles `src/Core` and a few UI-free files from `src/App` (`Session.cs`, `Online.cs`,
`RegionDownload.cs`, `RadioPort.cs`), so the engine, the CPS CSV rules, the online data and the radio protocol are
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

Not ported yet, in the order I would do them:

1. (done) editors and zones.
2. (done) the map.
3. The new-codeplug wizard (`Wizard.cs`: region, radio, areas, zones, zone talkgroups), Find repeaters online, Browse BrandMeister, From RepeaterBook.
4. Settings tab extras (CPS format folder, merge mode, renumber), radio settings editor, About with the credits.
5. App icon (`.icns`), signing and notarization if the app is shared outside this Mac.

Known duplication to remove later: the Windows `MainForm` and `MainWindow` each hold the open/save/import/generate/radio
flows. If the Windows app keeps being developed, move those flows into a UI-free class in `src/Core` that both call.
