using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// The radio's optional settings (Radio > Radio settings, or --radio-settings-ui [radio.img]), edited on a memory
    /// image read from the radio. Every control comes from <see cref="RadioSettings.All"/>. Write to radio sends the
    /// whole codeplug the way the BTECH CPS does (RadioWriter), with every read and write kept in RadioPort.ReadsFolder.
    /// </summary>
    sealed class RadioSettingsForm : Form
    {
        MemoryImage original, image;
        string imagePath;
        bool loading;

        readonly ListBox groups = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly Panel page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 3, 3) };
        readonly ProgressBar progress = new ProgressBar { Width = 160, Visible = false, Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 3, 3) };
        readonly Button readButton, openButton, saveButton, undoButton, writeButton;
        readonly Dictionary<string, Control> editors = new Dictionary<string, Control>();

        public RadioSettingsForm(string path)
        {
            Text = "Radio settings - " + Ui.AppName;
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Ui.S(900), Ui.S(680));
            MinimumSize = new Size(Ui.S(640), Ui.S(420));

            readButton = Ui.Button("Read from radio", (s, e) => ReadFromRadio());
            openButton = Ui.Button("Open image...", (s, e) => OpenImage());
            saveButton = Ui.Button("Save image as...", (s, e) => SaveImage());
            undoButton = Ui.Button("Undo all changes", (s, e) => UndoAll());
            writeButton = Ui.Button("Write to radio", (s, e) => WriteToRadio());
            writeButton.Enabled = false;

            var toolbar = Ui.Row(readButton, openButton, saveButton, undoButton, writeButton, progress, status);
            toolbar.Padding = new Padding(Ui.S(6), Ui.S(4), Ui.S(6), Ui.S(4));
            toolbar.Dock = DockStyle.Top;

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(groups);
            split.Panel2.Controls.Add(page);
            Ui.InitSplitter(split, Ui.S(190), Ui.S(140), Ui.S(300));

            var legend = Ui.Hint("Gray labels haven't been checked against the BTECH CPS on this radio yet. Bold = changed. Changes go to the radio only when you click Write to radio.");
            legend.Dock = DockStyle.Bottom;
            legend.Padding = new Padding(Ui.S(6), 0, 0, Ui.S(4));

            Controls.Add(split);
            Controls.Add(legend);
            Controls.Add(toolbar);

            groups.Items.AddRange(RadioSettings.All.Select(d => d.Group).Distinct().Cast<object>().ToArray());
            groups.SelectedIndexChanged += (s, e) => ShowGroup();

            if (path != null && File.Exists(path)) SetImage(MemoryImage.Load(path), path);
            else SetImage(null, null);
        }

        /// <summary>--radio-settings-snapshot radio.img outFolder: draws every group off screen to PNGs.</summary>
        public static int Snapshot(string imagePath, string folder)
        {
            Directory.CreateDirectory(folder);
            using (var f = new RadioSettingsForm(Path.GetFullPath(imagePath)) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                f.Show();
                Application.DoEvents();
                for (int i = 0; i < f.groups.Items.Count; i++)
                {
                    f.groups.SelectedIndex = i;
                    Application.DoEvents();
                    // Tall enough for the longest group, so nothing is cut off.
                    var grid = f.page.Controls.Count > 0 ? f.page.Controls[0] : null;
                    int needed = grid != null ? grid.PreferredSize.Height + Ui.S(140) : f.Height;
                    f.Height = Math.Max(Ui.S(680), needed);
                    Application.DoEvents();
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(Path.Combine(folder, (i + 1).ToString("00", CultureInfo.InvariantCulture) + " " + ((string)f.groups.Items[i]).Replace(" ", "_") + ".png"));
                    }
                }
                f.Close();
            }
            return 0;
        }

        void SetImage(MemoryImage img, string path)
        {
            original = img;
            image = img == null ? null : MemoryImage.Load(Copy(img));
            imagePath = path;
            saveButton.Enabled = undoButton.Enabled = image != null;
            groups.Enabled = image != null;
            if (image != null && groups.SelectedIndex < 0 && groups.Items.Count > 0) groups.SelectedIndex = 0;
            else ShowGroup();
            UpdateStatus();
        }

        static MemoryStream Copy(MemoryImage img)
        {
            var ms = new MemoryStream();
            img.Save(ms);
            ms.Position = 0;
            return ms;
        }

        void UpdateStatus()
        {
            if (image == null)
            {
                writeButton.Enabled = false;
                status.Text = "Read the radio or open a saved radio.img.";
                return;
            }
            int changed = ChangedKeys().Count;
            writeButton.Enabled = changed > 0;
            status.Text = image.Model + " " + image.Version + ", read " + image.ReadAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                          + (changed > 0 ? " - " + changed + " changed" : "");
        }

        HashSet<string> ChangedKeys()
        {
            var keys = new HashSet<string>();
            if (image == null) return keys;
            foreach (var d in RadioSettings.All)
            {
                if (!image.Has(d.Address, d.Length)) continue;
                var a = RadioSettings.Read(original, d);
                var b = RadioSettings.Read(image, d);
                if (a.Raw != b.Raw || a.Text != b.Text) keys.Add(d.Key);
            }
            return keys;
        }

        void ShowGroup()
        {
            page.SuspendLayout();
            page.Controls.Clear();
            editors.Clear();
            if (image == null || groups.SelectedItem == null) { page.ResumeLayout(); return; }

            string group = (string)groups.SelectedItem;
            var grid = Ui.Grid(2);
            grid.Padding = new Padding(Ui.S(10), Ui.S(8), Ui.S(10), Ui.S(8));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(260)));
            grid.Controls.Add(Ui.Heading(group), 0, 0);
            grid.SetColumnSpan(grid.GetControlFromPosition(0, 0), 2);

            var changed = ChangedKeys();
            loading = true;
            int row = 1;
            foreach (var d in RadioSettings.All.Where(x => x.Group == group && image.Has(x.Address, x.Length)))
            {
                var label = Ui.Label(d.Label, changed.Contains(d.Key));
                if (!d.Verified) label.ForeColor = Ui.HintColor;
                var editor = MakeEditor(d);
                if (!(editor is CheckBox))
                {
                    editor.Anchor = AnchorStyles.Left;
                    editor.Width = Ui.S(d.Kind == SettingKind.Text && d.Length > 8 ? 220 : 180);
                }
                grid.Controls.Add(label, 0, row);
                grid.Controls.Add(editor, 1, row);
                editors[d.Key] = label;
                row++;
            }
            loading = false;
            page.Controls.Add(grid);
            page.ResumeLayout();
        }

        Control MakeEditor(SettingDef d)
        {
            var v = RadioSettings.Read(image, d);
            switch (d.Kind)
            {
                case SettingKind.Flag:
                {
                    var c = new CheckBox { Checked = v.Raw != 0, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
                    c.CheckedChanged += (s, e) => Apply(d, () => RadioSettings.Write(image, d, c.Checked ? 1 : 0));
                    return c;
                }
                case SettingKind.Choice:
                {
                    var c = Ui.Combo(false, d.Options.Select(o => o.Value).ToArray());
                    int i = d.Options.FindIndex(o => o.Key == v.Raw);
                    if (i < 0)
                    {
                        // A stored value outside the documented list: show it, don't change it unless asked.
                        c.Items.Add(v.Display);
                        i = c.Items.Count - 1;
                    }
                    c.SelectedIndex = i;
                    c.SelectedIndexChanged += (s, e) =>
                    {
                        if (c.SelectedIndex >= 0 && c.SelectedIndex < d.Options.Count)
                            Apply(d, () => RadioSettings.Write(image, d, d.Options[c.SelectedIndex].Key));
                    };
                    return c;
                }
                case SettingKind.Text:
                {
                    var t = Ui.Text();
                    t.MaxLength = d.Length;
                    t.Text = v.Text;
                    t.TextChanged += (s, e) => Apply(d, () => RadioSettings.WriteText(image, d, t.Text));
                    return t;
                }
                default:
                {
                    var t = Ui.Text("MHz");
                    t.Text = (v.Raw / 100000m).ToString("0.00000", CultureInfo.InvariantCulture);
                    t.Validating += (s, e) =>
                    {
                        decimal? mhz = Ui.ParseMHz(t.Text);
                        if (mhz == null) { t.BackColor = Ui.ErrorBack; return; }
                        t.BackColor = SystemColors.Window;
                        Apply(d, () => RadioSettings.Write(image, d, (long)Math.Round(mhz.Value * 100000m)));
                    };
                    return t;
                }
            }
        }

        void Apply(SettingDef d, Action write)
        {
            if (loading) return;
            try
            {
                write();
            }
            catch (ArgumentException ex)
            {
                Ui.Error(this, ex.Message);
                return;
            }
            if (editors.TryGetValue(d.Key, out var label))
            {
                var a = RadioSettings.Read(original, d);
                var b = RadioSettings.Read(image, d);
                label.Font = a.Raw != b.Raw || a.Text != b.Text ? Ui.BoldFont : Ui.BaseFont;
            }
            UpdateStatus();
        }

        void UndoAll()
        {
            if (image == null || ChangedKeys().Count == 0) return;
            if (!Ui.Confirm(this, "Undo all " + ChangedKeys().Count + " changes?")) return;
            SetImage(original, imagePath);
        }

        void OpenImage()
        {
            using (var dlg = new OpenFileDialog { Filter = "Radio image (*.img)|*.img|All files|*.*", Title = "Open a radio image" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { SetImage(MemoryImage.Load(dlg.FileName), dlg.FileName); }
                catch (Exception ex) { Ui.Error(this, "Couldn't open " + dlg.FileName + ":\n\n" + ex.Message); }
            }
        }

        void SaveImage()
        {
            if (image == null) return;
            using (var dlg = new SaveFileDialog { Filter = "Radio image (*.img)|*.img", Title = "Save the edited image", FileName = "radio-edited.img" })
            {
                if (imagePath != null) dlg.InitialDirectory = Path.GetDirectoryName(imagePath);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (imagePath != null && string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase))
                {
                    Ui.Error(this, "Keep the image read from the radio as it is (it's your backup). Save the edited one under another name.");
                    return;
                }
                image.Save(dlg.FileName);
                Ui.Info(this, "Saved " + dlg.FileName + ".");
            }
        }

        void ReadFromRadio()
        {
            if (image != null && ChangedKeys().Count > 0 && !Ui.Confirm(this, "Reading the radio replaces your unsaved changes. Continue?")) return;
            MemoryImage img;
            try { img = RadioProgressDialog.Run(this, "Reading the radio", p => RadioPort.Read(RadioPort.Choose(null, null), p)); }
            catch (Exception ex)
            {
                Ui.Error(this, "Reading the radio failed:\n\n" + ex.Message);
                return;
            }
            string folder = RadioPort.NewReadFolder();
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "radio.img");
            img.Save(path);
            SetImage(img, path);
        }

        void WriteToRadio()
        {
            if (image == null) return;
            var changed = RadioSettings.All.Where(d => ChangedKeys().Contains(d.Key)).ToList();
            if (changed.Count == 0) { Ui.Info(this, "Nothing has changed yet."); return; }
            try { RadioWriter.BlocksToWrite(original, image); }
            catch (InvalidOperationException)
            {
                Ui.Error(this, "This image comes from an older read that doesn't hold the whole codeplug. Read the radio again (Read from radio), make the changes, then write.");
                return;
            }

            var lines = changed.Select(d => "  " + d.Label + ": " + RadioSettings.Read(original, d).Display + " -> " + RadioSettings.Read(image, d).Display + (d.Verified ? "" : "   (not yet checked against the CPS)"));
            string text = "Write these changes to the radio?\n\n" + string.Join("\n", lines) +
                          "\n\nThe whole codeplug is sent the way the BTECH CPS sends it, and checked afterwards. It takes about half a minute." +
                          "\n\nBefore you go on:\n  - close the BTECH CPS\n  - don't touch the radio or the cable until it's done\n  - keep a CPS codeplug file (.rdt) at hand, in case the radio has to be restored";
            if (MessageBox.Show(this, text, "Write to radio", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            // Keep what was on the radio and what is being written.
            string folder = RadioPort.NewReadFolder(" write");
            Directory.CreateDirectory(folder);
            original.Save(Path.Combine(folder, "before.img"));
            image.Save(Path.Combine(folder, "written.img"));

            WriteResult result;
            RadioWriter.Enabled = true;
            try
            {
                result = RadioProgressDialog.Run(this, "Writing to the radio", p => RadioPort.Write(RadioPort.Choose(null, null), original, image, p),
                    "Writing to the radio. Don't touch the radio or unplug the cable until this window closes.");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString());
                bool wrote = ex.Message.Contains("after reconnecting") || ex.Message.Contains("couldn't reconnect");
                Ui.Error(this, (wrote
                    ? "The radio was written, but checking it afterwards failed:\n\n" + ex.Message + "\n\nUse Read from radio to see what it holds now. If the radio misbehaves, write your saved codeplug with the BTECH CPS."
                    : "Writing failed:\n\n" + ex.Message + "\n\nIf the radio misbehaves, write your saved codeplug with the BTECH CPS.") + "\n\nThe images from before and after are in " + folder + ".");
                return;
            }
            finally
            {
                RadioWriter.Enabled = false;
            }
            File.WriteAllLines(Path.Combine(folder, "write.log"), result.Log);
            var written = MemoryImage.Load(Copy(image));
            written.ReadAtUtc = DateTime.UtcNow;
            SetImage(written, Path.Combine(folder, "written.img"));
            Ui.Info(this, "Done: " + changed.Count + (changed.Count == 1 ? " setting was" : " settings were") + " written and checked on the radio.\n\nBackups are in " + folder + ".");
        }
    }
}
