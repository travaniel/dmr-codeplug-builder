using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>File > Check repeaters for updates: the differences with ticks (same groups and texts as the Windows UpdatesDialog).</summary>
    static class UpdatesWindow
    {
        static readonly KeyValuePair<UpdateKind, string>[] Groups =
        {
            new KeyValuePair<UpdateKind, string>(UpdateKind.Frequency, "Frequency changed"),
            new KeyValuePair<UpdateKind, string>(UpdateKind.ColorCode, "Color code changed"),
            new KeyValuePair<UpdateKind, string>(UpdateKind.TalkgroupsAdded, "Talkgroups added"),
            new KeyValuePair<UpdateKind, string>(UpdateKind.TalkgroupsDropped, "Talkgroups no longer listed (unticked: you may have added them yourself)"),
            new KeyValuePair<UpdateKind, string>(UpdateKind.OffAir, "Off the air"),
            new KeyValuePair<UpdateKind, string>(UpdateKind.BackOnAir, "Back on the air"),
            new KeyValuePair<UpdateKind, string>(UpdateKind.Delisted, "No longer listed on RadioID.net (tick to switch off)"),
            new KeyValuePair<UpdateKind, string>(UpdateKind.NewRepeater, "New repeaters in your counties (unticked: tick the ones you want)"),
        };

        /// <summary>The ticked items and whether to refresh the caller list; null when closed without applying.</summary>
        public static async Task<KeyValuePair<List<UpdateItem>, bool>?> Run(Window owner, Online.UpdateReport report, Project p, string lastChecked)
        {
            var w = new Window { Title = "Check repeaters for updates", Width = 880, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            string head = report.Items.Count == 0
                ? "Nothing changed for the " + report.Tracked + " repeater(s) added from RadioID.net, and no new repeaters in their counties."
                : report.Items.Count + " update(s) for the " + report.Tracked + " repeater(s) added from RadioID.net. Ticked ones are applied; untick what you want to keep as it is.";
            if (report.Errors.Count > 0) head += "\n\nCouldn't check everything: " + string.Join("; ", report.Errors.Take(5)) + (report.Errors.Count > 5 ? "..." : "");
            if (lastChecked != null) head += "\nLast checked " + lastChecked + ".";

            var boxes = new List<KeyValuePair<CheckBox, UpdateItem>>();
            var stack = new StackPanel { Spacing = 2 };
            foreach (var g in Groups)
            {
                var items = report.Items.Where(i => i.Kind == g.Key).ToList();
                if (items.Count == 0) continue;
                stack.Children.Add(new TextBlock { Text = g.Value + " (" + items.Count + ")", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
                foreach (var i in items)
                {
                    var box = new CheckBox { Content = new TextBlock { Text = i.Text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = i.Ticked };
                    boxes.Add(new KeyValuePair<CheckBox, UpdateItem>(box, i));
                    stack.Children.Add(box);
                }
            }
            var callers = new CheckBox { Content = "Also download RadioID.net's caller list again (for Export's caller names; otherwise kept a week)", IsChecked = true,
                                         IsVisible = !string.IsNullOrEmpty(p.Options.CallerScope) };
            var hint = UiKit.Hint("Repeaters typed in by hand, imported from the CPS or from RepeaterBook have no RadioID.net listing to compare with. " +
                                  "Channel numbers stay the same; check the changes before writing to the radio.");
            KeyValuePair<List<UpdateItem>, bool>? result = null;
            var ok = new Button { Content = "Apply ticked", MinWidth = 110, IsDefault = true, IsEnabled = report.Items.Count > 0 || callers.IsVisible };
            var close = new Button { Content = "Close", MinWidth = 90 };
            ok.Click += (s, e) =>
            {
                result = new KeyValuePair<List<UpdateItem>, bool>(boxes.Where(b => b.Key.IsChecked == true).Select(b => b.Value).ToList(), callers.IsVisible && callers.IsChecked == true);
                w.Close();
            };
            close.Click += (s, e) => w.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { close, ok } };
            var top = UiKit.Hint(head);
            var bottom = new StackPanel { Spacing = 6, Children = { callers, hint, buttons } };
            var dock = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(bottom);
            dock.Children.Add(new ScrollViewer { Content = stack, Margin = new Thickness(0, 6, 0, 0) });
            w.Content = dock;
            await w.ShowDialog(owner);
            return result;
        }
    }
}
