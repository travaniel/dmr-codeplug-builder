using System;
using System.Diagnostics;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Paste, replace or forget the user's own RepeaterBook token (RepeaterBook > API Apps). Opened from Settings and from
    /// Repeaters > From RepeaterBook. The token is never shown back: the box starts empty.
    /// </summary>
    static class RepeaterBookTokenDialog
    {
        /// <summary>Shows the dialog; true when the saved token changed (saved or forgotten).</summary>
        public static bool Show(IWin32Window owner)
        {
            using (var f = new Form
            {
                Text = "Your RepeaterBook token", Font = Ui.BaseFont, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
                MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(Ui.S(10)),
            })
            {
                var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                t.Controls.Add(Ui.Hint("Each user makes their own token for this program: sign in on RepeaterBook, open API Apps, and generate a token " +
                                       "for W6OZZ CPS. Paste it here. It is yours alone: don't share it or post it anywhere. It is kept on this PC, " +
                                       "encrypted for your Windows account, and never put in projects, exports or logs." +
                                       (RepeaterBookToken.IsSet ? "\n\nA token is saved. Paste a new one to replace it." : ""), Ui.S(520)));
                var link = new LinkLabel { Text = "Open RepeaterBook > API Apps", AutoSize = true, Margin = new Padding(3, Ui.S(4), 3, Ui.S(6)) };
                link.LinkClicked += (s, e) => Open(owner, RepeaterBookApi.TokenPageUrl);
                t.Controls.Add(link);
                var box = new TextBox { Width = Ui.S(520), UseSystemPasswordChar = true };
                t.Controls.Add(box);
                var show = new CheckBox { Text = "Show", AutoSize = true };
                show.CheckedChanged += (s, e) => box.UseSystemPasswordChar = !show.Checked;
                t.Controls.Add(show);
                var save = new Button { Text = "Save", AutoSize = true, UseVisualStyleBackColor = true };
                var forget = new Button { Text = "Forget my token", AutoSize = true, UseVisualStyleBackColor = true, Enabled = RepeaterBookToken.IsSet };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, UseVisualStyleBackColor = true };
                save.Click += (s, e) =>
                {
                    string token = box.Text.Trim();
                    if (!RepeaterBookApi.LooksLikeToken(token)) { Ui.Error(f, "That doesn't look like a token. Copy the whole token from RepeaterBook > API Apps."); return; }
                    if (!token.StartsWith(RepeaterBookApi.UserTokenPrefix, StringComparison.Ordinal) &&
                        MessageBox.Show(f, "Tokens you make for this program start with \"" + RepeaterBookApi.UserTokenPrefix + "\". Save this one anyway?",
                                        f.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    RepeaterBookToken.Set(token);
                    f.DialogResult = DialogResult.OK;
                };
                forget.Click += (s, e) => { RepeaterBookToken.Set(null); f.DialogResult = DialogResult.OK; };
                t.Controls.Add(Ui.Row(save, forget, cancel));
                f.Controls.Add(t);
                f.AcceptButton = save;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        /// <summary>"saved on this PC" / "none yet", for the token rows.</summary>
        public static string Status => RepeaterBookToken.IsSet ? "saved on this PC" : "none yet";

        public static void Open(IWin32Window owner, string url)
        {
            try { Process.Start(url); }
            catch (Exception ex) { Ui.Error(owner, "Couldn't open the browser:\n\n" + ex.Message + "\n\nGo to " + url + " yourself."); }
        }
    }
}
