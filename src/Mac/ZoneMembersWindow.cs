using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Puts channels into a zone besides their own (favourites): from the Zones tab ("Add channels...", zone fixed) or the
    /// Repeaters tab ("Add to zone...", one repeater's channels, zone chosen here). Same as the Windows ZoneMembersDialog.
    /// </summary>
    static class ZoneMembersWindow
    {
        sealed class ChRow : RowBase
        {
            public GeneratedChannel C;
            public HashSet<GeneratedChannel> Ticked;
            public bool Pick
            {
                get { return Ticked.Contains(C); }
                set { if (value) Ticked.Add(C); else Ticked.Remove(C); Changed?.Invoke(); }
            }
            public Action Changed;
            public string Channel => C.Name;
            public string Talkgroup => C.IsDigital ? C.Talkgroup.Name : "FM" + (C.Repeater.RxOnly ? " (RX only)" : "");
            public string Slot => C.IsDigital ? C.Entry.Slot.ToString(CultureInfo.InvariantCulture) : "";
            public string Rx => C.Repeater.RxMHz.ToString("0.000", CultureInfo.InvariantCulture);
            public string Zone => C.Zone ?? "";
        }

        /// <summary>Returns the zone and how many channels were added (0 when cancelled).</summary>
        public static async Task<KeyValuePair<string, int>> Run(Window owner, Session session, string zone, Repeater only)
        {
            var project = session.Project;
            List<GeneratedChannel> channels;
            try { channels = CodeplugGenerator.Generate(project, session.Format).ChannelList; }
            catch (Exception) { channels = new List<GeneratedChannel>(); }

            var w = new Window
            {
                Title = only != null ? "Add " + (string.IsNullOrWhiteSpace(only.Name) ? "channels" : only.Name) + " to a zone" : "Add channels to zone \"" + zone + "\"",
                Width = 720, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            };
            var names = project.Zones.Select(z => z.Name).ToList();
            if (!names.Any(n => Project.SameZone(n, "Favorites"))) names.Insert(0, "Favorites");
            var cboZone = UiKit.Suggest(names, 240);
            cboZone.Text = zone ?? "Favorites";
            cboZone.IsEnabled = zone == null;
            var find = new TextBox { Watermark = "Find a channel, repeater, talkgroup or zone", IsVisible = only == null };
            var grid = UiKit.Grid(false);
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Add", Binding = new Avalonia.Data.Binding("Pick"), Width = new DataGridLength(50) });
            grid.Columns.Add(UiKit.Col("Channel", "Channel", true, 160));
            grid.Columns.Add(UiKit.Col("Talkgroup", "Talkgroup", true, 140));
            grid.Columns.Add(UiKit.Col("Slot", "Slot", true, 45));
            grid.Columns.Add(UiKit.Col("RX MHz", "Rx", true, 75));
            grid.Columns.Add(UiKit.Col("Its zone", "Zone", true, 120));
            var count = UiKit.Label("");
            var ticked = new HashSet<GeneratedChannel>();
            int added = 0;
            string target = null;
            var ok = new Button { Content = "Add", MinWidth = 88, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 88 };

            void UpdateCount()
            {
                count.Text = ticked.Count == 1 ? "1 channel ticked" : ticked.Count + " channels ticked";
                ok.IsEnabled = ticked.Count > 0 && (cboZone.Text ?? "").Trim().Length > 0;
            }
            void Fill()
            {
                var inZone = new HashSet<object>(project.ZoneChannels((cboZone.Text ?? "").Trim()).Select(c => c.Key));
                string f = (find.Text ?? "").Trim();
                var rows = new List<ChRow>();
                foreach (var c in channels)
                {
                    if (only != null && c.Repeater != only) continue;
                    if (inZone.Contains((object)c.Entry ?? c.Repeater)) continue;
                    var row = new ChRow { C = c, Ticked = ticked, Changed = UpdateCount };
                    if (f.Length > 0 && !new[] { c.Name, c.Repeater.Name, row.Talkgroup, row.Zone }.Any(s => s.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (only != null) ticked.Add(c);
                    rows.Add(row);
                }
                grid.ItemsSource = rows;
                UpdateCount();
            }
            UiKit.OnText(find, Fill);
            UiKit.OnText(cboZone, Fill);
            ok.Click += (s, e) =>
            {
                target = Naming.Fit(cboZone.Text ?? "", 16);
                if (target.Length == 0) return;
                target = project.FindZone(target)?.Name ?? target;
                foreach (var c in channels.Where(ticked.Contains))
                    if (project.AddToZone(target, new ChannelRef(c.Repeater, c.Entry))) added++;
                w.Close();
            };
            cancel.Click += (s, e) => w.Close();

            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            var hint = UiKit.Hint("A channel can be in several zones: it stays in its own repeater's zone and also shows up here. Favorites zones are handy for the few channels you use most.");
            Grid.SetColumnSpan(hint, 2);
            var lz = UiKit.Label("Zone"); Grid.SetRow(lz, 1);
            Grid.SetRow(cboZone, 1); Grid.SetColumn(cboZone, 1);
            var lf = UiKit.Label("Find"); Grid.SetRow(lf, 2); lf.IsVisible = only == null;
            Grid.SetRow(find, 2); Grid.SetColumn(find, 1);
            top.Children.Add(hint); top.Children.Add(lz); top.Children.Add(cboZone); top.Children.Add(lf); top.Children.Add(find);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(count); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(0, 0, 12, 0);
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            var dock = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(buttons); dock.Children.Add(grid);
            w.Content = dock;
            Fill();
            await w.ShowDialog(owner);
            return new KeyValuePair<string, int>(target, added);
        }
    }
}
