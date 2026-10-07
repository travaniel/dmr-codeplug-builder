using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using CodeplugBuilder.Core.Radio;

namespace CodeplugBuilder.Mac
{
    /// <summary>Message boxes, a list-and-confirm box and the radio progress box (Avalonia has none built in).</summary>
    static class Dialogs
    {
        public const string AppName = "W6OZZ CPS";

        static Window Make(string title, double width)
        {
            return new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
        }

        static TextBlock Text(string text, bool bold = false)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        }

        static Button Button(string text, Action click, bool isDefault = false)
        {
            var b = new Button { Content = text, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = isDefault };
            b.Click += (s, e) => click();
            return b;
        }

        static StackPanel Buttons(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var b in buttons) row.Children.Add(b);
            return row;
        }

        public static Task Info(Window owner, string text, string title = AppName) { return Show(owner, title, text, "OK", null); }

        public static Task Error(Window owner, string text) { return Show(owner, AppName, text, "OK", null); }

        /// <summary>True when the user picked <paramref name="ok"/>.</summary>
        public static Task<bool> Ask(Window owner, string text, string ok = "OK", string cancel = "Cancel", string title = AppName)
        {
            return Show(owner, title, text, ok, cancel);
        }

        static async Task<bool> Show(Window owner, string title, string text, string ok, string cancel)
        {
            var w = Make(title, 460);
            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(Text(text));
            var buttons = new List<Button>();
            if (cancel != null) buttons.Add(Button(cancel, () => w.Close(false)));
            buttons.Add(Button(ok, () => w.Close(true), true));
            body.Children.Add(Buttons(buttons.ToArray()));
            w.Content = body;
            return await w.ShowDialog<bool>(owner);
        }

        /// <summary>Pick one line from a list; returns its index, or -1 when cancelled.</summary>
        public static async Task<int> Choose(Window owner, string title, string text, IList<string> items, int selected)
        {
            var w = Make(title, 560);
            var list = new ListBox { ItemsSource = items, SelectedIndex = selected, MaxHeight = 260, Margin = new Thickness(0, 8, 0, 0) };
            int result = -1;
            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(Text(text));
            body.Children.Add(list);
            body.Children.Add(Buttons(Button("Cancel", () => w.Close()), Button("OK", () => { result = list.SelectedIndex; w.Close(); }, true)));
            w.Content = body;
            await w.ShowDialog(owner);
            return result;
        }

        /// <summary>A resizable window with selectable monospaced text and Copy / Save buttons (the diagnostics report).</summary>
        public static async Task Report(Window owner, string title, string text)
        {
            var w = new Window { Title = title, Width = 900, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Menlo, Consolas, Courier New, monospace"), FontSize = 12 };
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            var copy = new Button { Content = "Copy" };
            copy.Click += async (s, e) =>
            {
                try { await w.Clipboard.SetTextAsync(text); status.Text = "Copied to the clipboard."; }
                catch (Exception ex) { status.Text = "Couldn't copy: " + ex.Message; }
            };
            var close = new Button { Content = "Close", IsDefault = true };
            close.Click += (s, e) => w.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { copy, close, status } };
            var dock = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(new ScrollViewer { Content = box, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            w.Content = dock;
            await w.ShowDialog(owner);
        }

        /// <summary>A one-line text prompt; null when cancelled.</summary>
        public static async Task<string> Prompt(Window owner, string title, string label, string value, int maxLength = 0)
        {
            var w = Make(title, 420);
            var box = new TextBox { Text = value ?? "", MaxLength = maxLength > 0 ? maxLength : 0, Margin = new Thickness(0, 8, 0, 0) };
            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(Text(label));
            body.Children.Add(box);
            string result = null;
            body.Children.Add(Buttons(Button("Cancel", () => w.Close()), Button("OK", () => { result = box.Text; w.Close(); }, true)));
            w.Content = body;
            w.Opened += (s, e) => { box.Focus(); box.SelectAll(); };
            await w.ShowDialog(owner);
            return result;
        }

        /// <summary>A heading, then problems (red) and notes in a scrolling list; with <paramref name="ask"/> there is a Cancel and the answer matters.</summary>
        public static async Task<bool> List(Window owner, string head, IEnumerable<string> problems, IEnumerable<string> notes, bool ask, string ok = "OK")
        {
            var w = Make(AppName, 600);
            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(Text(head));
            var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 8, 0) };
            foreach (var p in problems ?? new string[0])
                list.Children.Add(new TextBlock { Text = "• " + p, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick });
            foreach (var n in notes ?? new string[0])
                list.Children.Add(new TextBlock { Text = "• " + n, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            if (list.Children.Count > 0)
                body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            var buttons = new List<Button>();
            if (ask) buttons.Add(Button("Cancel", () => w.Close(false)));
            buttons.Add(Button(ask ? ok : "OK", () => w.Close(true), true));
            body.Children.Add(Buttons(buttons.ToArray()));
            w.Content = body;
            return await w.ShowDialog<bool>(owner);
        }

        /// <summary>
        /// Runs <paramref name="work"/> off the UI thread behind a progress box that can't be closed halfway (a radio
        /// write must never be interrupted). Rethrows the work's exception.
        /// </summary>
        public static async Task<T> Progress<T>(Window owner, string title, Func<IProgress<ReadProgress>, T> work,
            string message = "Talking to the radio. Don't touch the radio or unplug the cable.")
        {
            var w = Make(title, 440);
            var label = Text(message);
            var bar = new ProgressBar { IsIndeterminate = true, Height = 18, Margin = new Thickness(0, 12, 0, 0) };
            w.Content = new StackPanel { Margin = new Thickness(18), Children = { label, bar } };
            bool done = false;
            w.Closing += (s, e) => { if (!done) e.Cancel = true; };
            var progress = new Progress<ReadProgress>(p =>
            {
                if (p.Total <= 0) return;
                bar.IsIndeterminate = false;
                bar.Maximum = p.Total;
                bar.Value = Math.Min(p.Done, p.Total);
                if (!string.IsNullOrEmpty(p.What)) label.Text = char.ToUpperInvariant(p.What[0]) + p.What.Substring(1) + " (" + p.Done + " of " + p.Total + ")";
            });
            T result = default(T);
            Exception error = null;
            w.Opened += async (s, e) =>
            {
                try { result = await Task.Run(() => work(progress)); }
                catch (Exception ex) { error = ex; }
                done = true;
                w.Close();
            };
            await w.ShowDialog(owner);
            if (error != null) throw error;
            return result;
        }
    }
}
