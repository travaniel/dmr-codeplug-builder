using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    // The WinForms part of Online (kept out of Online.cs so the Mac version can share that file).
    static partial class Online
    {
        /// <summary>
        /// Checks the picked BrandMeister-only repeaters and gives them BrandMeister's static talkgroups (<see cref="PrepareForAdding"/>),
        /// with a wait dialog while anything has to be asked. On any failure, or Cancel, they keep RadioID.net's lists.
        /// </summary>
        public static void PrepareForAdding(IWin32Window owner, List<OnlineRepeater> picked)
        {
            if (!NeedsPreparing(picked)) return;
            using (var cancel = new System.Threading.CancellationTokenSource())
            {
                try
                {
                    RadioProgressDialog.Run(owner, "BrandMeister", pr => PrepareForAdding(picked, pr, cancel.Token),
                        "Asking BrandMeister about the picked repeaters: still on the air, and which talkgroups they carry...", cancel);
                }
                catch { }
            }
        }

        /// <summary>
        /// Asks for a callsign, looks it up on RadioID.net and fills in the project's DMR ID and Radio ID name.
        /// Returns true when the project changed.
        /// </summary>
        public static async Task<bool> LookUpRadioIdAsync(IWin32Window owner, Session session)
        {
            var p = session.Project;
            string guess = "";
            var words = (p.RadioIdName ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0) guess = words[words.Length - 1];
            string call = Prompt.Show(owner, "Look up your DMR ID", "Your callsign (looked up on RadioID.net):", guess, 10);
            if (string.IsNullOrWhiteSpace(call)) return false;
            call = call.Trim().ToUpperInvariant();

            List<RadioIdUser> users;
            Cursor.Current = Cursors.WaitCursor;
            try { users = await LookupUserAsync(call); }
            catch (Exception ex) { Ui.Error(owner, "Couldn't look up " + call + ":\n\n" + ex.Message); return false; }
            finally { Cursor.Current = Cursors.Default; }

            if (users.Count == 0)
            {
                Ui.Error(owner, "RadioID.net has no DMR ID for " + call + ".\n\nIf you don't have one yet, register at radioid.net (it's free), then try again.");
                return false;
            }
            var u = users[0];
            string name = u.SuggestedName(p.Options.MaxNameLength);
            string others = users.Count > 1
                ? "\n\nYou have " + users.Count + " IDs: " + string.Join(", ", users.ConvertAll(x => x.Id.ToString())) + ". Using the first; change it on the Settings tab if needed."
                : "";
            if (!Ui.Confirm(owner, "Found " + u.Callsign + ": " + (u.FirstName + " " + u.LastName).Trim() + ", " + u.City + ", " + u.State +
                                   "\n\nDMR ID: " + u.Id + "\nRadio ID name: " + name + others +
                                   "\n\nUse these? Every channel will use this Radio ID name, and RadioIDList.CSV will contain it."))
                return false;
            p.RadioId = u.Id;
            p.RadioIdName = name;
            if (p.Home == null) // the town RadioID lists becomes home, when the map knows it
            {
                try { p.Home = HomeLocation.Find(GeoAtlas.BuiltIn(), u.City, u.State, u.Country); }
                catch (Exception) { }
            }
            session.MarkDirty();
            return true;
        }
    }
}
