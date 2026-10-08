DMR Codeplug Builder
====================

Builds a CSV codeplug for the BTECH DMR-6X2 PRO programming software (CPS 1.22e). You manage
talkgroups, repeaters, your hotspot and zones; it writes the CSV files plus a .LST file list that the
CPS imports in one step.

Requirements: Windows 10 or 11. It uses the .NET Framework 4.8 that Windows already includes, so
there is nothing to install.

Getting started
---------------
1. Extract the whole folder somewhere (for example Documents). If Windows blocks the program, right-
   click the downloaded zip > Properties > tick "Unblock" > OK, then extract again. The program is not
   code-signed, so SmartScreen may also ask: "More info" > "Run anyway".
2. Run CodeplugBuilder.exe.
   - New codeplug: a wizard on a map downloads the DMR repeaters for your area (RadioID.net), looks up
     your DMR ID from your callsign, and builds zones and talkgroups.
   - Or start from your radio: in the CPS read the radio, then Tool > Export > Export All, and in this
     program File > Import from CPS export... and pick the exported .LST.
3. Export > Export CSV files for the CPS (Ctrl+G) and save the .LST. The CSV files are saved next to it.
4. In the CPS: open your codeplug, Tool > Import > Import From File List, pick the .LST, check a few
   channels, then write to the radio.
   Or, with the programming cable connected and the CPS closed, click "Write to radio..." (bottom
   right). It saves what was on the radio first; Radio > Restore codeplug from a backup puts it back.

What the import replaces
------------------------
The Channel, Zone, Talk Groups, Receive Group Call List and Radio ID lists, plus the Scan List when
scan lists are on (one per zone; on for new codeplugs, off for one imported from the CPS; see
Settings). Everything else in the codeplug stays as it was.

Channels you made by hand in the CPS are removed by the import unless you turn on Settings > "Keep
channels, zones and talkgroups made in the CPS" and point it at a fresh Export All of your codeplug.

Your projects are saved as .cpb files. Always keep a CPS backup of your codeplug before importing.
