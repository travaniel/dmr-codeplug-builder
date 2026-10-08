using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.Mac
{
    // Radio > Read / Write / Restore. The same steps as the Windows app's MainForm (reading keeps radio.img and the CSVs as a
    // backup, a write goes through RadioEncoder and RadioWriter and is verified after the radio reconnects), with Avalonia
    // dialogs. The serial port is found by RadioPort (/dev/cu.usbmodem* on a Mac: not verified on real hardware yet).
    sealed partial class MainWindow
    {
        /// <summary>Radio > Read codeplug from radio: reads, keeps the image and CSVs as a backup, opens them as a project.</summary>
        async Task ImportFromRadio()
        {
            if (!await ConfirmDiscard()) return;
            string folder = RadioPort.NewReadFolder();
            MemoryImage img;
            try { img = await Dialogs.Progress(this, "Reading the radio", p => RadioPort.Read(RadioPort.Choose(null, null), p)); }
            catch (Exception ex) { await Dialogs.Error(this, "Reading the radio failed:\n\n" + ex.Message); return; }
            try
            {
                Directory.CreateDirectory(folder);
                img.Save(Path.Combine(folder, "radio.img"));
                RadioCsv.WriteTo(RadioCsv.ToTables(RadioCodeplug.Decode(img), CpsFormat.BuiltIn()), folder);
            }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't decode what was read:\n\n" + ex.Message); return; }
            await ImportFolder(folder, "read from the radio", true);
        }

        /// <summary>
        /// Radio > Write codeplug to radio: reads the radio (kept as a backup), generates this project, writes its channels,
        /// zones, talkgroups, RX and scan lists and radio ID into that read, shows what changes, then sends it and checks it.
        /// </summary>
        async Task WriteProjectToRadio()
        {
            if (!InWorkspace) return;
            var p = session.Project;
            p.SyncZones();
            var issues = Validator.Validate(p, session.Format);
            var errors = issues.Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
            if (errors.Count > 0)
            {
                await Dialogs.List(this, "Fix these before writing to the radio:", errors, issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message), false);
                return;
            }
            GeneratedCodeplug g = null;
            await WriteCodeplugToRadio("this project", (original, before) =>
            {
                g = CodeplugGenerator.Generate(p, session.Format, p.Options.KeepCpsChannels ? CpsExport.Load(before) : null);
                if (g.ChannelList.Count + g.KeptChannels.Count > p.Options.MaxChannels)
                    throw new InvalidOperationException("This codeplug has " + (g.ChannelList.Count + g.KeptChannels.Count) + " channels; the radio holds " + p.Options.MaxChannels + ".");
                var notes = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message).Concat(g.Notes).ToList();
                return new KeyValuePair<Dictionary<string, CsvTable>, List<string>>(g.Files().ToDictionary(f => f.Key, f => f.Value), notes);
            }, () =>
            {
                int numbered = CodeplugGenerator.KeepChannelNumbers(g);
                bool remembered = CodeplugGenerator.RememberOutput(p, g);
                if (numbered > 0 || remembered) session.MarkDirty();
                return numbered > 0 ? "\n\n" + Plural(numbered, "channel") + " got a channel number that will now stay the same; save the project (File > Save) to keep it." : "";
            }, true);
        }

        /// <summary>Radio > Restore codeplug from a backup: writes the codeplug of a saved read (radio.img / before.img) back.</summary>
        async Task RestoreRadioBackup()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick a saved read of the radio (radio.img, or before.img from a write)",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Radio memory image") { Patterns = new[] { "*.img" } } },
            });
            if (files.Count == 0) return;
            MemoryImage backup;
            try { backup = MemoryImage.Load(files[0].Path.LocalPath); }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't open that image:\n\n" + ex.Message); return; }
            string when = backup.ReadAtUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
            await WriteCodeplugToRadio("the backup read " + when, (original, before) =>
            {
                var cp = RadioCodeplug.Decode(backup);
                var notes = cp.Warnings.ToList();
                notes.Insert(0, "Settings stay as they are on the radio now; only the channels, zones, talkgroups, RX and scan lists and radio IDs come from the backup.");
                return new KeyValuePair<Dictionary<string, CsvTable>, List<string>>(RadioCsv.ToTables(cp, CpsFormat.BuiltIn()), notes);
            }, () => "");
        }

        async Task WriteCodeplugToRadio(string what, Func<MemoryImage, string, KeyValuePair<Dictionary<string, CsvTable>, List<string>>> build, Func<string> after,
                                        bool offerSettings = false)
        {
            // 1. What's on the radio now: the base to write into, and the backup.
            string folder = RadioPort.NewReadFolder(" write");
            MemoryImage original;
            try { original = await Dialogs.Progress(this, "Reading the radio", pr => RadioPort.Read(RadioPort.Choose(null, null), pr)); }
            catch (Exception ex) { await Dialogs.Error(this, "Reading the radio failed:\n\n" + ex.Message); return; }
            string before = Path.Combine(folder, "before");
            try
            {
                Directory.CreateDirectory(before);
                original.Save(Path.Combine(folder, "before.img"));
                RadioCsv.WriteTo(RadioCsv.ToTables(RadioCodeplug.Decode(original), CpsFormat.BuiltIn()), before);
            }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't save or decode what was read:\n\n" + ex.Message); return; }

            // 2. The codeplug, written into that read.
            Dictionary<string, CsvTable> tables;
            List<string> notes;
            EncodeResult enc;
            int changed;
            try
            {
                var built = build(original, before);
                tables = built.Key;
                notes = built.Value;
                enc = RadioEncoder.Encode(original, tables, session.Format);
                if (enc.Errors.Count == 0) RadioWriter.BlocksToWrite(original, enc.Image); // refuses an image that doesn't hold everything a write sends
                changed = enc.Errors.Count == 0 ? RadioWriter.ChangedBlocks(original, enc.Image).Count : 0;
            }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't prepare the write:\n\n" + ex.Message + "\n\nNothing was written. What was read is in " + folder + "."); return; }
            if (enc.Errors.Count > 0)
            {
                await Dialogs.List(this, "This can't be written to the radio. Nothing was written.", enc.Errors, notes.Concat(enc.Notes), false);
                return;
            }

            // Recommended settings go into the same write; No is remembered, closing the question stops the write.
            var applied = new List<Recommendation>();
            var recs = offerSettings ? RecommendedSettings.Check(enc.Image, session.Project.RadioIdName, AppSettings.Get(RecommendedSettings.DeclinedKey)) : applied;
            if (recs.Count > 0)
            {
                string q = "Also change " + (recs.Count == 1 ? "this radio setting" : "these radio settings") + " in this write?\n\n" +
                           string.Join("\n\n", recs.Select(r => r.Describe())) + "\n\n" +
                           "Leave as is: the radio keeps its settings, and you won't be asked again.";
                bool? answer = await Dialogs.YesNo(this, q, "Change", "Leave as is", "Write to radio");
                if (answer == null) return;
                if (answer == true)
                {
                    RecommendedSettings.Apply(enc.Image, recs);
                    applied = recs;
                    changed = RadioWriter.ChangedBlocks(original, enc.Image).Count;
                }
                else AppSettings.Set(RecommendedSettings.DeclinedKey, RecommendedSettings.Decline(AppSettings.Get(RecommendedSettings.DeclinedKey), recs));
            }
            if (changed == 0)
            {
                await Dialogs.Info(this, "The radio already holds " + what + ". Nothing to write.");
                return;
            }

            // 3. Show what changes, then ask.
            var now = RadioCodeplug.Decode(original);
            var next = RadioCodeplug.Decode(enc.Image);
            var oldNames = new HashSet<string>(now.Channels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var newNames = new HashSet<string>(next.Channels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            var added = next.Channels.Where(c => !oldNames.Contains(c.Name)).Select(c => c.Name).ToList();
            var removed = now.Channels.Where(c => !newNames.Contains(c.Name)).Select(c => c.Name).ToList();
            if (added.Count > 0) lines.Add("New channels (" + added.Count + "): " + Shorten(added));
            if (removed.Count > 0) lines.Add("Channels taken off the radio (" + removed.Count + "): " + Shorten(removed));
            lines.AddRange(applied.Select(r => "Setting: " + r.Def.Label + " " + new SettingValue { Def = r.Def, Raw = r.Value }.Display + "."));
            lines.AddRange(notes.Concat(enc.Notes).Distinct());
            string head = "Write " + what + " to the radio?\n\n" +
                          "On the radio now:  " + Counts(now) + "\n" +
                          "After writing:       " + Counts(next) + "\n\n" +
                          "This replaces the channels, zones, talkgroups, RX group lists, scan lists and radio IDs on the radio. Its settings stay as they are" +
                          (applied.Count > 0 ? ", except " + string.Join(", ", applied.Select(r => r.Def.Label.ToLowerInvariant())) + "." : ".");
            if (!await Dialogs.List(this, head, new string[0], lines, true, "Write to radio")) return;
            string text = "Ready to write.\n\n" +
                          "  - Close the BTECH CPS if it's open.\n" +
                          "  - Don't touch the radio or unplug the cable until this is done (about half a minute; the radio restarts).\n\n" +
                          "What's on the radio now was saved in:\n" + folder + "\n" +
                          "Radio > Restore codeplug from a backup puts it back (pick before.img there). A CPS codeplug file (.rdt) is a good second backup.";
            if (!await Dialogs.Ask(this, text, "Write", "Cancel", "Write to radio")) return;

            // 4. Write and check.
            try
            {
                enc.Image.Save(Path.Combine(folder, "written.img"));
                RadioCsv.WriteTo(tables.ToDictionary(kv => kv.Key, kv => kv.Value), Path.Combine(folder, "written"));
            }
            catch (Exception ex) { await Dialogs.Error(this, "Couldn't save the backup of what's being written:\n\n" + ex.Message + "\n\nNothing was written."); return; }
            WriteResult result;
            RadioWriter.Enabled = true;
            try
            {
                result = await Dialogs.Progress(this, "Writing to the radio", pr => RadioPort.Write(RadioPort.Choose(null, null), original, enc.Image, pr),
                    "Writing to the radio. Don't touch the radio or unplug the cable until this window closes.");
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); } catch { }
                bool wrote = ex.Message.Contains("after reconnecting") || ex.Message.Contains("couldn't reconnect");
                await Dialogs.Error(this, (wrote
                    ? "The radio was written, but checking it afterwards failed:\n\n" + ex.Message + "\n\nUse Radio > Read codeplug from radio to see what it holds now."
                    : "Writing failed:\n\n" + ex.Message) +
                    "\n\nTo put the old codeplug back: Radio > Restore codeplug from a backup, and pick before.img in " + folder + ". If the radio misbehaves, write a saved codeplug with the BTECH CPS.");
                return;
            }
            finally
            {
                RadioWriter.Enabled = false;
            }
            try { File.WriteAllLines(Path.Combine(folder, "write.log"), result.Log.Concat(lines.Select(l => "Note: " + l))); } catch { }
            string extra = after();
            await Dialogs.Info(this, "Done: " + Counts(next) + " written and checked on the radio." + extra +
                                     "\n\nWhat was on the radio before, and what was written, are saved in:\n" + folder, "Write to radio");
        }

        static string Counts(RadioCodeplug cp) =>
            Plural(cp.Channels.Count, "channel") + ", " + Plural(cp.Zones.Count, "zone") + ", " + Plural(cp.Contacts.Count, "talkgroup") + ", " +
            Plural(cp.GroupLists.Count, "RX list") + ", " + Plural(cp.ScanLists.Count, "scan list");

        static string Shorten(List<string> names) =>
            string.Join(", ", names.Take(12)) + (names.Count > 12 ? " and " + (names.Count - 12) + " more" : "");
    }
}

namespace CodeplugBuilder.Mac
{
    sealed partial class MainWindow
    {
        /// <summary>Radio > Radio port...: leave it on automatic (the radio is found by its USB ID), or pick the serial port by hand.</summary>
        async Task ChooseRadioPort()
        {
            var ports = RadioPort.AllPorts();
            var radios = RadioPort.FindRadioPorts();
            string saved = AppSettings.Get(RadioPort.SavedPortKey);
            var items = new List<string> { "Automatic (find the radio by its USB ID)" };
            items.AddRange(ports.Select(p => p + (radios.Contains(p) ? "   <- looks like the radio" : "")));
            int selected = string.IsNullOrEmpty(saved) ? 0 : Math.Max(0, ports.IndexOf(saved) + 1);
            string text = ports.Count == 0
                ? "No serial ports are listed right now. Connect the programming cable and switch the radio on, then try again. (Help > Diagnostics shows what the Mac sees.)"
                : "Pick the port the radio is on. On a Mac it is called /dev/cu.usbmodem... Leave this on Automatic unless the radio isn't found.";
            int pick = await Dialogs.Choose(this, "Radio port", text, items, selected);
            if (pick < 0) return;
            AppSettings.Set(RadioPort.SavedPortKey, pick == 0 ? null : ports[pick - 1]);
            await Dialogs.Info(this, pick == 0 ? "The radio will be found automatically." : "Using " + ports[pick - 1] + " for the radio.");
        }

        /// <summary>Help > Diagnostics: what this computer looks like to the program (copy it into a bug report).</summary>
        async Task ShowDiagnostics()
        {
            string report = await Task.Run(() => Diagnostics.Report(true));
            await Dialogs.Report(this, "Diagnostics", report);
        }
    }
}
