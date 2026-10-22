using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodeplugBuilder.Mac
{
    /// <summary>Small helpers for building the Avalonia screens in code (the same job Ui.cs does for WinForms).</summary>
    static class UiKit
    {
        public static decimal? ParseMHz(string text)
        {
            string s = (text ?? "").Trim().Replace(',', '.');
            if (s.Length == 0) return null;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) && v > 0 && v < 10000)
                return Math.Round(v, 5);
            return null;
        }

        public static string FormatMHz(decimal v) { return v <= 0 ? "" : v.ToString("0.000##", CultureInfo.InvariantCulture); }

        public static TextBlock Label(string text, bool bold = false)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(0, 0, 6, 0) };
        }

        public static TextBlock Hint(string text)
        {
            return new TextBlock { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        }

        /// <summary>
        /// Two panes side by side with a divider the user can drag. <paramref name="left"/> starts <paramref name="leftWidth"/> wide
        /// (it must not set its own Width); <paramref name="right"/> takes the rest.
        /// </summary>
        public static Avalonia.Controls.Grid SplitView(Control left, Control right, double leftWidth, double leftMin = 260, double rightMin = 340)
        {
            var g = new Avalonia.Controls.Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(leftWidth)) { MinWidth = leftMin });
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = rightMin });
            var splitter = new GridSplitter
            {
                Width = 6, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Background = Avalonia.Media.Brushes.Gray, Opacity = 0.25, Margin = new Thickness(0, 8),
            };
            Avalonia.Controls.Grid.SetColumn(left, 0); Avalonia.Controls.Grid.SetColumn(splitter, 1); Avalonia.Controls.Grid.SetColumn(right, 2);
            g.Children.Add(left); g.Children.Add(splitter); g.Children.Add(right);
            return g;
        }

        public static TextBlock Heading(string text)
        {
            return new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        }

        public static Button Button(string text, Action click, double minWidth = 0)
        {
            var b = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (minWidth > 0) b.MinWidth = minWidth;
            b.Click += (s, e) => click();
            return b;
        }

        public static StackPanel Row(params Control[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var i in items) row.Children.Add(i);
            return row;
        }

        /// <summary>Calls <paramref name="changed"/> whenever the text property of the control changes (TextBox, AutoCompleteBox).</summary>
        public static void OnText(AvaloniaObject box, AvaloniaProperty<string> property, Action changed)
        {
            box.PropertyChanged += (s, e) => { if (e.Property == property) changed(); };
        }

        public static void OnText(TextBox box, Action changed) { OnText(box, TextBox.TextProperty, changed); }

        public static void OnText(AutoCompleteBox box, Action changed) { OnText(box, AutoCompleteBox.TextProperty, changed); }

        public static AutoCompleteBox Suggest(IEnumerable<string> items, double width = 0)
        {
            var box = new AutoCompleteBox { ItemsSource = items.ToList(), MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.Contains, IsTextCompletionEnabled = false };
            if (width > 0) box.Width = width;
            // Show the whole list when the box gets focus or is clicked, like a combo box.
            box.GotFocus += (s, e) => { if (string.IsNullOrEmpty(box.Text)) box.IsDropDownOpen = true; };
            return box;
        }

        public static DataGridTextColumn Col(string header, string path, bool readOnly = false, double width = 0)
        {
            var c = new DataGridTextColumn { Header = header, Binding = new Avalonia.Data.Binding(path), IsReadOnly = readOnly };
            // Proportional, so the columns share a wider window; never narrower than the width asked for (the grid then scrolls).
            if (width > 0) { c.Width = new DataGridLength(width, DataGridLengthUnitType.Star); c.MinWidth = width; }
            return c;
        }

        public static DataGrid Grid(bool multi = false)
        {
            return new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserSortColumns = false,
                CanUserResizeColumns = true,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                SelectionMode = multi ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single,
            };
        }
    }

    /// <summary>A row in a grid that can say "my values changed" without rebuilding the grid.</summary>
    abstract class RowBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
    }
}
