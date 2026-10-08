using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// File > Check repeaters for updates (roadmap item 12): what changed on RadioID.net and BrandMeister since the repeaters were
    /// added, with ticks; Apply changes the ticked ones. Also offers to refresh the caller list.
    /// </summary>
    sealed class UpdatesDialog : Form
    {
        readonly ListView list;
        readonly CheckBox chkCallers;

        public List<UpdateItem> Ticked => list.Items.Cast<ListViewItem>().Where(i => i.Checked && i.Tag is UpdateItem).Select(i => (UpdateItem)i.Tag).ToList();
        public bool RefreshCallers => chkCallers.Visible && chkCallers.Checked;

        static readonly Dictionary<UpdateKind, string> Groups = new Dictionary<UpdateKind, string>
        {
            { UpdateKind.Frequency, "Frequency changed" },
            { UpdateKind.ColorCode, "Color code changed" },
            { UpdateKind.TalkgroupsAdded, "Talkgroups added" },
            { UpdateKind.TalkgroupsDropped, "Talkgroups no longer listed (unticked: you may have added them yourself)" },
            { UpdateKind.OffAir, "Off the air" },
            { UpdateKind.BackOnAir, "Back on the air" },
            { UpdateKind.Delisted, "No longer listed on RadioID.net (tick to switch off)" },
            { UpdateKind.NewRepeater, "New repeaters in your counties (unticked: tick the ones you want)" },
        };

        public UpdatesDialog(Online.UpdateReport report, Project p)
        {
            Text = "Check repeaters for updates";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(Ui.S(900), Ui.S(600));
            MinimumSize = new Size(Ui.S(560), Ui.S(360));
            Padding = new Padding(Ui.S(10));

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            string head = report.Items.Count == 0
                ? "Nothing changed for the " + report.Tracked + " repeater(s) added from RadioID.net, and no new repeaters in their counties."
                : report.Items.Count + " update(s) for the " + report.Tracked + " repeater(s) added from RadioID.net. Ticked ones are applied; untick what you want to keep as it is.";
            if (report.Errors.Count > 0) head += "\n\nCouldn't check everything: " + string.Join("; ", report.Errors.Take(5)) + (report.Errors.Count > 5 ? "..." : "");
            t.Controls.Add(Ui.Hint(head + (p.LastUpdateCheck != null ? "\nLast checked " + p.LastUpdateCheck + "." : ""), Ui.S(840)));

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, ShowItemToolTips = true };
            Ui.DoubleBuffer(list);
            list.Columns.Add("", Ui.S(820));
            foreach (var g in Groups)
            {
                var items = report.Items.Where(i => i.Kind == g.Key).ToList();
                if (items.Count == 0) continue;
                var group = new ListViewGroup(g.Value + " (" + items.Count + ")");
                list.Groups.Add(group);
                foreach (var i in items) list.Items.Add(new ListViewItem(i.Text, group) { Tag = i, Checked = i.Ticked, ToolTipText = i.Text });
            }
            t.Controls.Add(list);

            chkCallers = new CheckBox { Text = "Also download RadioID.net's caller list again (for Export's caller names; otherwise kept a week)", AutoSize = true,
                                        Visible = !string.IsNullOrEmpty(p.Options.CallerScope), Checked = true };
            t.Controls.Add(chkCallers);
            t.Controls.Add(Ui.Hint("Repeaters typed in by hand, imported from the CPS or from RepeaterBook have no RadioID.net listing to compare with. " +
                                   "Channel numbers stay the same; check the changes before writing to the radio.", Ui.S(840)));

            var ok = new Button { Text = "Apply ticked", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(Ui.S(110), 0) };
            var cancel = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Ui.S(90), 0) };
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Margin = new Padding(0, Ui.S(8), 0, 0) };
            row.Controls.Add(cancel);
            row.Controls.Add(ok);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(t);
            Controls.Add(row);
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Enabled = report.Items.Count > 0 || chkCallers.Visible;
        }
    }
}
