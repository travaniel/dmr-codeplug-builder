using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Repeaters > Add from map: the wizard's area picker for an open project. New repeaters go into zones by
    /// county, city, state... or into one zone, and pick up that zone's talkgroup set automatically.
    /// </summary>
    sealed class AddFromMapDialog : Form
    {
        readonly Session session;
        readonly AreaChooser chooser;
        readonly Label lblRegion, lblDownload;
        readonly ComboBox cboScheme, cboZone, cboPower;
        readonly Button btnAdd;
        RegionDownload download;

        public OnlineImportResult Result { get; private set; }

        public AddFromMapDialog(Session session)
        {
            this.session = session;
            Text = "Add repeaters from the map";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Ui.S(1180), area.Width * 95 / 100), Math.Min(Ui.S(820), area.Height * 95 / 100));
            MinimumSize = new Size(Ui.S(860), Ui.S(600));
            Padding = new Padding(Ui.S(10));

            lblRegion = Ui.Label("", true);
            lblDownload = new Label { AutoSize = true, ForeColor = Ui.HintColor, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(12), Ui.S(7), 3, 3) };
            var top = Ui.Row(Ui.Label("Region"), lblRegion, Ui.Button("Change region...", (s, e) => ChangeRegion()), lblDownload);
            top.Dock = DockStyle.Top;
            top.WrapContents = false;

            chooser = new AreaChooser { Dock = DockStyle.Fill, Margin = new Padding(0, Ui.S(4), 0, Ui.S(4)) };

            cboScheme = Ui.Combo(false, ZonePlanner.Choices.Select(c => c.Value).ToArray());
            cboScheme.Width = Ui.S(230);
            cboScheme.Anchor = AnchorStyles.Left;
            cboZone = Ui.Combo(true);
            cboZone.Width = Ui.S(170);
            cboZone.Anchor = AnchorStyles.Left;
            cboZone.MaxLength = 16;
            cboPower = Ui.Combo(false, Powers.Values);
            cboPower.Width = Ui.S(90);
            cboPower.Anchor = AnchorStyles.Left;
            cboPower.SelectedItem = "High";
            var opts = Ui.Row(Ui.Label("Put new repeaters in"), cboScheme, cboZone, Ui.Label("Power"), cboPower);
            opts.WrapContents = false;

            btnAdd = new Button { Text = "Add repeaters", AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(Ui.S(10), Ui.S(3), Ui.S(10), Ui.S(3)), UseVisualStyleBackColor = true, Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Ui.S(6), Ui.S(3), Ui.S(6), Ui.S(3)), UseVisualStyleBackColor = true };
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), Anchor = AnchorStyles.Right };
            buttons.Controls.Add(btnAdd);
            buttons.Controls.Add(cancel);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(opts, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);
            bottom.Controls.Add(Ui.Hint("Repeaters land in the zone you choose and get that zone's ticked talkgroups (Zones tab) on top of the ones they list themselves.", Ui.S(900)), 0, 1);

            Controls.Add(chooser);
            Controls.Add(top);
            Controls.Add(bottom);
            chooser.BringToFront();
            CancelButton = cancel;

            foreach (var z in session.Project.Zones) cboZone.Items.Add(z.Name);
            bool hasCounty = session.Project.Repeaters.Any(r => !string.IsNullOrEmpty(r.County));
            cboScheme.SelectedIndex = Array.FindIndex(ZonePlanner.Choices, c => c.Key == (hasCounty ? ZoneScheme.County : ZoneScheme.City));
            cboZone.Text = session.Project.Zones.FirstOrDefault()?.Name ?? "DMR";
            cboScheme.SelectedIndexChanged += (s, e) => cboZone.Enabled = Scheme == ZoneScheme.Single;
            cboZone.Enabled = Scheme == ZoneScheme.Single;
            chooser.PickedChanged += (s, e) => UpdateAdd();
            btnAdd.Click += (s, e) => AddPicked();
            Shown += (s, e) => Begin();
            FormClosed += (s, e) => { if (download != null) { download.Changed -= OnDownload; download.Cancel(); } };
        }

        ZoneScheme Scheme => cboScheme.SelectedIndex >= 0 ? ZonePlanner.Choices[cboScheme.SelectedIndex].Key : ZoneScheme.City;

        async void Begin()
        {
            await chooser.Picker.LoadAtlasAsync();
            if (IsDisposed) return;
            var atlas = chooser.Picker.Map.Atlas;
            var region = (AppSettings.Get("Region") ?? "").Split(',').Select(atlas.Find).Where(a => a != null).ToList();
            if (region.Count == 0)
            {
                // Guess from the project's repeaters (their states), else ask.
                region = session.Project.Repeaters.Select(r => atlas.Find(r.AreaCode)).Where(a => a != null)
                                .Select(a => a.Level == AreaLevel.County ? a.Parent : a).Distinct().ToList();
            }
            if (region.Count == 0 && !AskRegion(out region)) { DialogResult = DialogResult.Cancel; return; }
            StartDownload(region);
        }

        void ChangeRegion()
        {
            if (AskRegion(out var region)) StartDownload(region);
        }

        bool AskRegion(out List<GeoArea> region)
        {
            region = null;
            using (var d = new RegionDialog(download?.Areas))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Picked.Count == 0) return false;
                region = d.Picked;
                AppSettings.Set("Region", string.Join(",", region.Select(a => a.Code)));
                return true;
            }
        }

        void StartDownload(List<GeoArea> region)
        {
            if (download != null) { download.Changed -= OnDownload; download.Cancel(); }
            download = new RegionDownload(region);
            download.Changed += OnDownload;
            lblRegion.Text = RegionDownload.Describe(region);
            chooser.Bind(download, session.Project);
            download.Start();
            UpdateAdd();
        }

        void OnDownload(object sender, EventArgs e)
        {
            if (IsDisposed || sender != download) return;
            lblDownload.Text = download.Done && download.Errors.Count > 0 ? download.Status + " " + string.Join("; ", download.Errors) : download.Status;
            lblDownload.ForeColor = download.Done && download.Errors.Count > 0 ? Color.Firebrick : Ui.HintColor;
            chooser.Reload();
            UpdateAdd();
        }

        void UpdateAdd()
        {
            int n = download != null && download.Done ? chooser.Picked().Count : 0;
            btnAdd.Enabled = n > 0;
            btnAdd.Text = n == 0 ? "Add repeaters" : "Add " + n + " repeater" + (n == 1 ? "" : "s");
        }

        void AddPicked()
        {
            string single = Naming.Clean(cboZone.Text, 16);
            if (Scheme == ZoneScheme.Single && single.Length == 0) { Ui.Error(this, "Type a zone name."); return; }
            var scheme = Scheme;
            var o = new OnlineImportOptions
            {
                Power = (string)cboPower.SelectedItem ?? "High",
                ZoneFor = r => ZonePlanner.ZoneName(r, scheme, single.Length > 0 ? single : "DMR"),
                Scheme = scheme,
                MoreNames = download.TalkgroupNames,
            };
            Result = OnlineImporter.AddRepeaters(session.Project, chooser.Picked(), o, download.BrandMeisterNames);
            DialogResult = DialogResult.OK;
        }

        /// <summary>Shows the dialog and reports what was added. Returns the first added repeater (or null).</summary>
        public static Repeater Run(IWin32Window owner, Session session)
        {
            using (var d = new AddFromMapDialog(session))
            {
                if (d.ShowDialog(owner) != DialogResult.OK || d.Result == null) return null;
                var r = d.Result;
                session.NotifyTalkgroupsChanged();
                var lines = new List<string>();
                if (r.NewTalkgroups.Count > 0)
                    lines.Add("New talkgroups: " + string.Join(", ", r.NewTalkgroups.Select(t => t.Name + " (" + t.Id + ")")) + ".");
                lines.AddRange(r.Notes);
                int bare = r.Added.Count(x => x.Talkgroups.Count == 0);
                if (bare > 0) lines.Add(bare + " of them have no talkgroups yet. Tick some for their zones on the Zones tab.");
                string head = "Added " + r.Added.Count + " repeater" + (r.Added.Count == 1 ? "" : "s") + " (" + r.Channels + " channels).";
                using (var dlg = new IssuesDialog(head, new string[0], lines, false)) dlg.ShowDialog(owner);
                return r.Added.FirstOrDefault();
            }
        }
    }

    /// <summary>Pick the states or countries to download (used by Add from map).</summary>
    sealed class RegionDialog : Form
    {
        readonly RegionPicker picker;
        readonly IEnumerable<GeoArea> initial;

        public List<GeoArea> Picked => picker.Map.Selected.OrderBy(a => a.Name).ToList();

        public RegionDialog(IEnumerable<GeoArea> current)
        {
            initial = current;
            Text = "Pick the region to download";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(Ui.S(1000), Ui.S(700));
            MinimumSize = new Size(Ui.S(700), Ui.S(480));
            Padding = new Padding(Ui.S(10));
            picker = new RegionPicker { Dock = DockStyle.Fill };
            picker.AllowLevels(AreaLevel.Country, AreaLevel.State);
            var ok = new Button { Text = "Download", AutoSize = true, Font = Ui.BoldFont, DialogResult = DialogResult.OK, UseVisualStyleBackColor = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, UseVisualStyleBackColor = true };
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, Ui.S(6), 0, 0) };
            row.Controls.Add(cancel);
            row.Controls.Add(ok);
            Controls.Add(picker);
            Controls.Add(row);
            picker.BringToFront();
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += async (s, e) =>
            {
                await picker.LoadAtlasAsync();
                if (IsDisposed) return;
                var start = (initial ?? Enumerable.Empty<GeoArea>()).ToList();
                if (start.Count > 0)
                {
                    picker.Level = start.Any(a => a.Level == AreaLevel.Country) ? AreaLevel.Country : AreaLevel.State;
                    picker.Map.SetSelected(start);
                    picker.Map.ZoomToAreas(start);
                }
                else picker.Map.ZoomTo(-128, 22, -64, 52);
            };
        }
    }
}
