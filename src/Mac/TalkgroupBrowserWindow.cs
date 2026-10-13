using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>Talkgroups > Browse BrandMeister on the Mac (the Windows TalkgroupBrowserDialog): search BrandMeister's list, tick, add.</summary>
    sealed class TalkgroupBrowserWindow : Window
    {
        readonly Project project;
        readonly TextBox txtSearch;
        readonly DataGrid list;
        readonly GridSorter sorter;
        readonly TextBlock lblStatus;
        readonly Button btnAdd;
        readonly HashSet<int> picked = new HashSet<int>();
        Dictionary<int, string> names = new Dictionary<int, string>();

        /// <summary>Talkgroups to add (names already shortened to 16 characters).</summary>
        public List<Talkgroup> Selected { get; } = new List<Talkgroup>();

        sealed class Row : RowBase
        {
            readonly TalkgroupBrowserWindow owner;
            public int Id { get; }
            public string BmName { get; }
            public string Yours { get; }
            public string Note { get; }
            public bool InList { get; }
            public Row(TalkgroupBrowserWindow owner, int id, string bmName, Talkgroup existing)
            {
                this.owner = owner; Id = id; BmName = bmName; InList = existing != null;
                Yours = existing != null ? existing.Name : ShortName(id, bmName);
                Note = existing != null ? "in your list" : "";
            }
            public bool Pick
            {
                get { return owner.picked.Contains(Id); }
                set { if (InList) { Refresh(); return; } if (value) owner.picked.Add(Id); else owner.picked.Remove(Id); owner.UpdateButton(); }
            }
        }

        public TalkgroupBrowserWindow(Project project)
        {
            this.project = project;
            Title = "BrandMeister talkgroups";
            Width = 760; Height = 640; MinWidth = 520; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(UiKit.Hint("Every talkgroup on the BrandMeister network. Search by name or number, tick the ones you want, and they're added to your " +
                                        "talkgroup list (names shortened to the radio's 16 characters)."));
            txtSearch = new TextBox { Width = 260, Watermark = "e.g. Texas, 3148, SOTA" };
            lblStatus = new TextBlock { Text = "Loading...", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            top.Children.Add(UiKit.Row(UiKit.Label("Search"), txtSearch, lblStatus));

            list = UiKit.Grid(false);
            list.Columns.Add(new DataGridCheckBoxColumn { Header = "Add", Binding = new Avalonia.Data.Binding("Pick"), Width = new DataGridLength(66), CanUserSort = false });
            list.Columns.Add(UiKit.Col("ID", "Id", true, 90));
            list.Columns.Add(UiKit.Col("BrandMeister name", "BmName", true, 300));
            list.Columns.Add(UiKit.Col("Name in your codeplug", "Yours", true, 180));
            list.Columns.Add(UiKit.Col("", "Note", true, 100));
            sorter = new GridSorter(list);

            btnAdd = new Button { Content = "Add", IsEnabled = false, IsDefault = true, FontWeight = FontWeight.SemiBold };
            btnAdd.Click += (s, e) => Finish();
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Click += (s, e) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { btnAdd, cancel } };

            var dock = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(buttons); dock.Children.Add(list);
            Content = dock;

            UiKit.OnText(txtSearch, Refill);
            Opened += async (s, e) =>
            {
                names = await Online.BrandMeisterNamesAsync();
                if (names.Count == 0) { lblStatus.Text = "Couldn't download BrandMeister's list. Check your internet connection."; return; }
                Refill();
                txtSearch.Focus();
            };
        }

        /// <summary>Dev check (--online-check): search, tick the first <paramref name="n"/> new ones; the rows shown.</summary>
        internal async Task<int> DevPick(string search, int n)
        {
            var t = DateTime.Now;
            while (names.Count == 0 && (DateTime.Now - t).TotalSeconds < 60) await Task.Delay(300);
            txtSearch.Text = search;
            await Task.Delay(300);
            foreach (var kv in names.Where(kv => kv.Value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 && project.FindTalkgroup(kv.Key) == null).OrderBy(kv => kv.Key).Take(n))
                picked.Add(kv.Key);
            Refill();
            return names.Count(kv => kv.Value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal void DevFinish() { Finish(); }

        void Refill()
        {
            var terms = (txtSearch.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = names
                .Where(kv => terms.All(t => kv.Value.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            kv.Key.ToString(CultureInfo.InvariantCulture).StartsWith(t, StringComparison.Ordinal)))
                .OrderBy(kv => kv.Key)
                .Take(1000)
                .Select(kv => new Row(this, kv.Key, kv.Value, project.FindTalkgroup(kv.Key)))
                .ToList();
            sorter.Show(rows);
            lblStatus.Text = rows.Count >= 1000 ? "Showing the first 1000 of " + names.Count + "; type to narrow it down." : rows.Count + " of " + names.Count + " talkgroups";
            UpdateButton();
        }

        static string ShortName(int id, string name)
        {
            string n = BrandMeister.ShortName(name);
            return n.Length > 0 ? n : "TG " + id.ToString(CultureInfo.InvariantCulture);
        }

        void UpdateButton()
        {
            int n = picked.Count(id => project.FindTalkgroup(id) == null);
            btnAdd.IsEnabled = n > 0;
            btnAdd.Content = n > 0 ? "Add " + n + " talkgroup" + (n == 1 ? "" : "s") : "Add";
        }

        void Finish()
        {
            foreach (int id in picked.OrderBy(x => x))
            {
                if (project.FindTalkgroup(id) != null || !names.TryGetValue(id, out string name)) continue;
                string n = ShortName(id, name);
                Selected.Add(new Talkgroup(n, id, BrandMeister.IsPrivateCall(id, n) ? CallTypes.Private : CallTypes.Group));
            }
            Close(true);
        }

        /// <summary>Shows the window; the talkgroups to add (empty when cancelled).</summary>
        public static async Task<List<Talkgroup>> Run(Window owner, Project project)
        {
            var w = new TalkgroupBrowserWindow(project);
            return await w.ShowDialog<bool>(owner) ? w.Selected : new List<Talkgroup>();
        }
    }
}
