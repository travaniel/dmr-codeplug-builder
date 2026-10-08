# Testing the Mac version

The Mac version has been built and run on Windows only. These steps are for the first run on a real Mac. Each one says what
you should see; anything else is a finding worth reporting (with the Diagnostics report, see the end).

## 0. Get the app

- Download the zip for your Mac from the Releases page (Apple menu > About This Mac: "Apple M1/M2/M3/M4" means
  `arm64`, "Intel" means `x64`), or from the latest run of the *Mac build and checks* workflow (Actions tab >
  artifact `mac-app-zips`; that one is signed by the build machine).
- Unzip, drag **W6OZZ CPS.app** to Applications, right-click > Open > Open.
- If macOS says it is damaged: `xattr -dr com.apple.quarantine "/Applications/W6OZZ CPS.app"`.
- If it closes at once on Apple Silicon: `codesign --force --deep --sign - "/Applications/W6OZZ CPS.app"`.
- It starts but you want to see errors: run `"/Applications/W6OZZ CPS.app/Contents/MacOS/CodeplugBuilderMac"` in Terminal.

## 1. Look at it (no radio needed)

| # | Do | Expect |
| --- | --- | --- |
| 1 | Open the app | Start page with four choices; menu bar inside the window (File, Export, Radio, Help) |
| 2 | Help > Diagnostics | A report: system, folders (all `[writable]`), map data loaded, online sources `OK` |
| 3 | File > New codeplug (map wizard) | Step 1 map of the world |
| 4 | Two-finger scroll or pinch over the map | Both zoom toward the pointer (pinch is new and untried: tell me if it does nothing) |
| 5 | Drag the map | It pans; the cursor changes; releasing does not pick an area |
| 6 | Click Texas | It turns blue and the bottom line names the download |
| 7 | Next, enter your callsign, *Look up on RadioID.net* | Your DMR ID and name fill in (green line) |
| 8 | Next | Download finishes; pick counties on the map (US counties level); dots turn red, the counter at the bottom grows; *List* tab shows the repeaters |
| 9 | Next, Next | Zones list, then talkgroups per zone; *Starter set* ticks talkgroups; Finish gives a summary |
| 10 | Repeaters, Hotspot, Zones tabs | Edit a frequency (the TX follows the offset), add a talkgroup to a repeater, rename a zone |
| 11 | File > Save as, quit, reopen | The project comes back (File > Open) |
| 12 | Export > Export CSV files for the CPS | Six CSVs and a `.LST` in the folder you pick |

Keyboard: Cmd+O, Cmd+S, Cmd+Shift+S, Cmd+G, Cmd+R, Cmd+N.

## 2. The radio (read first, write later)

Use the programming cable, radio on, **the BTECH CPS closed** (it does not run on a Mac anyway).

| # | Do | Expect |
| --- | --- | --- |
| 1 | Help > Diagnostics with the radio connected | "Ports the system lists" shows `/dev/cu.usbmodem...`; "Found as the radio" shows the same; the USB (I/O registry) entry shows vendor 0x28e9 ("GD32 Virtual ComPort in FS Mode") |
| 2 | Radio > Read codeplug from radio | A progress bar, then a summary of what was imported. Reads are read-only and safe |
| 3 | Check the backup | `~/Documents/DMR Codeplug Builder/Radio reads/<date>/radio.img` and the CSVs exist |
| 4 | Compare | The imported channels/zones/talkgroups match what the radio and the CPS show |
| 5 | *Only after 1-4 look right:* make a small change, Radio > Write codeplug to radio | Review dialog, backup of the old codeplug (`before.img`), progress, "written and checked", the radio restarts |

Before the first write, also keep a CPS backup (`.rdt`) made on Windows. The write is the same protocol the BTECH CPS and the
Windows version use (verified there), but its Mac serial port handling has not been tried.

If the radio is not found: Radio > Radio port... lists every serial port and lets you pick one by hand (it is remembered).
If nothing like `/dev/cu.usbmodem` exists, the Mac does not see the radio: try another cable or USB port, and look at the
USB (I/O registry) entry in the diagnostics.

## 3. What to report

Press **Help > Diagnostics > Copy** and paste it into an issue with:
what you did (step number from above), what you expected, what happened, and a screenshot if it is visual.
For a crash, run the app from Terminal as in step 0 and include the output.

## Without a Mac

The *Mac build and checks* workflow (`.github/workflows/mac.yml`) runs on a GitHub macOS machine: engine tests, the
diagnostics report, pictures of the start page, the map and every wizard step, and signed `.app` bundles. It cannot test mouse
input or the radio, but it shows that the app starts, draws and the real downloads work on macOS.
