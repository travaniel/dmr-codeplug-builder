using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// The map with its toolbar: what a click picks (countries, states/provinces, US counties), a search box to jump to a
    /// place, and zoom buttons. Used by the new-codeplug wizard and by Repeaters > Add from map.
    /// </summary>
    sealed class RegionPickerView : UserControl
    {
        public readonly RegionMapView Map;
        readonly RadioButton optCountry, optState, optCounty;
        readonly CheckBox chkRoads;
        readonly AutoCompleteBox txtFind;
        readonly TextBlock lblStatus;
        Dictionary<string, GeoArea> byName;
        bool loading;

        /// <summary>The pick level changed (by the user or in code).</summary>
        public event EventHandler LevelChanged;

        public RegionPickerView()
        {
            optCountry = Radio("Countries", AreaLevel.Country);
            optState = Radio("States / provinces", AreaLevel.State);
            optCounty = Radio("US counties", AreaLevel.County);
            loading = true;
            optState.IsChecked = true; // the map's default level; Map doesn't exist yet
            loading = false;
            chkRoads = new CheckBox { Content = "Highways", IsChecked = true, Margin = new Thickness(12, 0, 0, 0) };
            chkRoads.IsCheckedChanged += (s, e) => Map.ShowRoads = chkRoads.IsChecked == true;
            var levelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            levelRow.Children.Add(UiKit.Label("Pick"));
            levelRow.Children.Add(optCountry); levelRow.Children.Add(optState); levelRow.Children.Add(optCounty); levelRow.Children.Add(chkRoads);

            txtFind = new AutoCompleteBox { Width = 220, MinimumPrefixLength = 1, FilterMode = AutoCompleteFilterMode.Contains, Watermark = "Go to a place, e.g. Texas" };
            txtFind.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; GoTo(txtFind.Text); } };
            var findRow = UiKit.Row(UiKit.Label("Find"), txtFind,
                UiKit.Button("Go", () => GoTo(txtFind.Text)),
                UiKit.Button("+", () => Map.ZoomBy(1.6), 36),
                UiKit.Button("-", () => Map.ZoomBy(1 / 1.6), 36),
                UiKit.Button("Whole world", () => Map.ZoomWorld()),
                UiKit.Button("Show picked", () => { if (Map.Selected.Count > 0) Map.ZoomToAreas(Map.Selected); }));
            findRow.HorizontalAlignment = HorizontalAlignment.Right;

            var bar = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
            findRow.HorizontalAlignment = HorizontalAlignment.Left;
            bar.Children.Add(levelRow);
            bar.Children.Add(findRow);

            Map = new RegionMapView();
            var frame = new Border { BorderThickness = new Thickness(1), BorderBrush = Avalonia.Media.Brushes.Gray, Child = Map };
            lblStatus = new TextBlock { Text = "Wheel or + / - to zoom, drag to move, click to pick.", Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) };

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(lblStatus, Dock.Bottom);
            dock.Children.Add(bar); dock.Children.Add(lblStatus); dock.Children.Add(frame);
            Content = dock;
        }

        RadioButton Radio(string text, AreaLevel level)
        {
            var r = new RadioButton { Content = text, GroupName = "picklevel" + GetHashCode(), Margin = new Thickness(4, 0), Tag = level };
            r.IsCheckedChanged += (s, e) =>
            {
                if (r.IsChecked != true || loading) return;
                Map.PickLevel = level;
                LevelChanged?.Invoke(this, EventArgs.Empty);
            };
            return r;
        }

        /// <summary>Loads the built-in atlas in the background (about a third of a second) and shows it.</summary>
        public async Task LoadAtlasAsync()
        {
            if (Map.Atlas != null) return;
            var atlas = await Task.Run(() => GeoAtlas.BuiltIn());
            UseAtlas(atlas);
        }

        /// <summary>Shows an atlas that's already loaded (and fills the Find box's suggestions).</summary>
        public void UseAtlas(GeoAtlas atlas)
        {
            if (Map.Atlas == atlas) return;
            Map.Atlas = atlas;
            chkRoads.IsVisible = Map.HasRoads;
            byName = new Dictionary<string, GeoArea>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var a in atlas.Countries.Concat(atlas.States).Concat(atlas.Counties))
                foreach (var n in new[] { a.FullName, a.Name })
                    if (!byName.ContainsKey(n)) { byName[n] = a; names.Add(n); }
            txtFind.ItemsSource = names;
        }

        public AreaLevel Level
        {
            get { return Map.PickLevel; }
            set
            {
                loading = true;
                optCountry.IsChecked = value == AreaLevel.Country;
                optState.IsChecked = value == AreaLevel.State;
                optCounty.IsChecked = value == AreaLevel.County;
                loading = false;
                Map.PickLevel = value;
                LevelChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Which pick levels the user may choose between.</summary>
        public void AllowLevels(params AreaLevel[] levels)
        {
            optCountry.IsVisible = levels.Contains(AreaLevel.Country);
            optState.IsVisible = levels.Contains(AreaLevel.State);
            optCounty.IsVisible = levels.Contains(AreaLevel.County);
        }

        public string Status
        {
            get { return lblStatus.Text; }
            set { lblStatus.Text = value; }
        }

        void GoTo(string text)
        {
            if (byName == null || string.IsNullOrWhiteSpace(text)) return;
            if (!byName.TryGetValue(text.Trim(), out var a))
            {
                string k = GeoAtlas.Key(text);
                a = byName.Values.FirstOrDefault(x => GeoAtlas.Key(x.Name) == k);
            }
            if (a == null) { lblStatus.Text = "No place called \"" + text.Trim() + "\" on the map."; return; }
            Map.ZoomToAreas(new[] { a });
            Map.Focus();
        }
    }
}
