using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Puts channels into a zone besides their own (favourites): from the Zones tab ("Add channels...", zone fixed) or the
    /// Repeaters tab ("Add to zone...", one repeater's channels, zone chosen here).
    /// </summary>
    sealed class ZoneMembersDialog : Form
    {
        readonly Project project;
        readonly ComboBox cboZone;
        readonly TextBox txtFind;
        readonly ListView list;
        readonly Label lblCount;
        readonly Repeater only;
        readonly List<GeneratedChannel> channels;

        /// <summary>The zone the channels went into, after OK.</summary>
        public string Zone { get; private set; }
        public int Added { get; private set; }

        public ZoneMembersDialog(Session session, string zone, Repeater only)
        {
            project = session.Project;
            this.only = only;
            Text = only != null ? "Add " + (string.IsNullOrWhiteSpace(only.Name) ? "channels" : only.Name) + " to a zone" : "Add channels to zone \"" + zone + "\"";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(Ui.S(720), Ui.S(560));
            MinimumSize = new Size(Ui.S(520), Ui.S(360));
            Padding = new Padding(Ui.S(10));

            try { channels = CodeplugGenerator.Generate(project, session.Format).ChannelList; }
            catch (Exception) { channels = new List<GeneratedChannel>(); }

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Controls.Add(Ui.Hint("A channel can be in several zones: it stays in its own repeater's zone and also shows up here. " +
                                   "Favorites zones are handy for the few channels you use most.", Ui.S(660)), 0, 0);
            t.SetColumnSpan(t.GetControlFromPosition(0, 0), 2);

            cboZone = Ui.Combo(true);
            cboZone.Dock = DockStyle.Fill;
            foreach (var z in project.Zones) cboZone.Items.Add(z.Name);
            if (!project.Zones.Any(z => Project.SameZone(z.Name, "Favorites"))) cboZone.Items.Insert(0, "Favorites");
            cboZone.Text = zone ?? "Favorites";
            cboZone.Enabled = zone == null;
            cboZone.MaxLength = 16;
            t.Controls.Add(Ui.Label("Zone"), 0, 1);
            t.Controls.Add(cboZone, 1, 1);

            txtFind = Ui.Text("Find a channel, repeater, talkgroup or zone");
            txtFind.Dock = DockStyle.Fill;
            txtFind.Visible = only == null;
            if (only == null)
            {
                t.Controls.Add(Ui.Label("Find"), 0, 2);
                t.Controls.Add(txtFind, 1, 2);
            }

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false };
            Ui.DoubleBuffer(list);
            list.Columns.Add("Channel", Ui.S(160));
            list.Columns.Add("Talkgroup", Ui.S(140));
            list.Columns.Add("Slot", Ui.S(45));
            list.Columns.Add("RX MHz", Ui.S(75), HorizontalAlignment.Right);
            list.Columns.Add("Its zone", Ui.S(120));
            t.Controls.Add(list, 0, 3);
            t.SetColumnSpan(list, 2);

            lblCount = Ui.Label("");
            var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(Ui.S(80), 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Ui.S(80), 0) };
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Ui.S(8), 0, 0) };
            row.Controls.Add(cancel);
            row.Controls.Add(ok);
            row.Controls.Add(lblCount);
            t.Controls.Add(row, 0, 4);
            t.SetColumnSpan(row, 2);
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;

            var ticked = new HashSet<GeneratedChannel>();
            bool filling = false;
            void Fill()
            {
                filling = true;
                string target = cboZone.Text.Trim();
                var inZone = new HashSet<object>(project.ZoneChannels(target).Select(c => c.Key));
                string find = txtFind.Text.Trim();
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var c in channels)
                {
                    if (only != null && c.Repeater != only) continue;
                    if (inZone.Contains((object)c.Entry ?? c.Repeater)) continue;
                    string tg = c.IsDigital ? c.Talkgroup.Name : "FM" + (c.Repeater.RxOnly ? " (RX only)" : "");
                    if (find.Length > 0 && !new[] { c.Name, c.Repeater.Name, tg, c.Zone ?? "" }.Any(s => s.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    var item = new ListViewItem(c.Name) { Tag = c, Checked = ticked.Contains(c) || only != null };
                    item.SubItems.Add(tg);
                    item.SubItems.Add(c.IsDigital ? c.Entry.Slot.ToString(CultureInfo.InvariantCulture) : "");
                    item.SubItems.Add(c.Repeater.RxMHz.ToString("0.000", CultureInfo.InvariantCulture));
                    item.SubItems.Add(c.Zone ?? "");
                    list.Items.Add(item);
                    if (item.Checked) ticked.Add(c);
                }
                if (list.Items.Count == 0)
                    list.Items.Add(new ListViewItem(only != null ? "(all its channels are in this zone already)" : "(no channels to add)"));
                list.EndUpdate();
                filling = false;
                UpdateCount();
            }
            void UpdateCount()
            {
                lblCount.Text = ticked.Count == 1 ? "1 channel ticked" : ticked.Count + " channels ticked";
                ok.Enabled = ticked.Count > 0 && cboZone.Text.Trim().Length > 0;
            }
            // Ticks survive filtering (the set, not the items, holds them). ItemChecked also fires while the handle is made.
            list.ItemChecked += (s, e) =>
            {
                if (filling || !(e.Item.Tag is GeneratedChannel c)) return;
                if (e.Item.Checked) ticked.Add(c); else ticked.Remove(c);
                UpdateCount();
            };
            txtFind.TextChanged += (s, e) => Fill();
            cboZone.TextChanged += (s, e) => Fill();
            Fill();

            FormClosing += (s, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                string target = Naming.Fit(cboZone.Text, 16);
                if (target.Length == 0) { e.Cancel = true; return; }
                var existing = project.FindZone(target);
                Zone = existing?.Name ?? target;
                foreach (var c in channels.Where(ticked.Contains))
                    if (project.AddToZone(Zone, new ChannelRef(c.Repeater, c.Entry))) Added++;
            };
        }
    }
}
