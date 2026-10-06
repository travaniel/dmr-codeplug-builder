using System.Windows.Forms;

namespace CodeplugBuilder.App
{
    /// <summary>Your own MMDVM hotspot or local repeater: one switch plus the same editor the repeaters use.</summary>
    sealed class HotspotPage : UserControl
    {
        readonly Session session;
        readonly CheckBox chkEnabled;
        readonly RepeaterEditor editor;
        bool loading;

        public HotspotPage(Session session)
        {
            this.session = session;
            Font = Ui.BaseFont;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(Ui.S(10), Ui.S(8), Ui.S(10), 0) };
            top.Controls.Add(Ui.Heading("MMDVM hotspot / local repeater"));
            chkEnabled = new CheckBox { Text = "Include my hotspot in the codeplug", AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 3, 3, 3) };
            top.Controls.Add(chkEnabled);
            top.Controls.Add(Ui.Hint(
                "Simplex hotspot (Pi-Star, WPSD): set Offset to Simplex; these normally carry everything on slot 2. " +
                "Duplex hotspot or MMDVM repeater: use its offset and both slots. Each talkgroup you add becomes a channel in the hotspot's zone.", Ui.S(980)));

            editor = new RepeaterEditor(true) { Dock = DockStyle.Fill };
            Controls.Add(editor);
            Controls.Add(top);
            editor.BringToFront();

            chkEnabled.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                session.Project.HotspotEnabled = chkEnabled.Checked;
                if (chkEnabled.Checked)
                {
                    // Switched on: it now belongs to its zone, so it takes on the zone's talkgroups.
                    session.Project.ApplyZoneTalkgroups(session.Project.Hotspot);
                    editor.Bind(session, session.Project.Hotspot);
                }
                editor.Enabled = chkEnabled.Checked;
                session.MarkDirty();
            };
            session.Replaced += (s, e) => Reload();
            session.TalkgroupsChanged += (s, e) => editor.RefreshTalkgroups();
            Reload();
        }

        public void Reload()
        {
            loading = true;
            chkEnabled.Checked = session.Project.HotspotEnabled;
            editor.Bind(session, session.Project.Hotspot);
            editor.Enabled = session.Project.HotspotEnabled;
            loading = false;
        }
    }
}
