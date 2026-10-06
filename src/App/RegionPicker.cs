using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// The map with its toolbar: what a click picks (countries, states/provinces, US counties), a search box to
    /// jump to a place, and zoom buttons. Used by the new-codeplug wizard and by Repeaters > Add from map.
    /// </summary>
    sealed class RegionPicker : UserControl
    {
        public readonly RegionMap Map;
        readonly RadioButton optCountry, optState, optCounty;
        readonly TextBox txtFind;
        readonly Label lblStatus;
        Dictionary<string, GeoArea> byName;
        bool loading;

        /// <summary>The pick level changed (by the user or in code).</summary>
        public event EventHandler LevelChanged;

        public RegionPicker()
        {
            Font = Ui.BaseFont;

            optCountry = Radio("Countries", AreaLevel.Country);
            optState = Radio("States / provinces", AreaLevel.State);
            optCounty = Radio("US counties", AreaLevel.County);
            loading = true;
            optState.Checked = true; // the map's default level; Map doesn't exist yet
            loading = false;
            var levelRow = Ui.Row(Ui.Label("Pick"), optCountry, optState, optCounty);
            levelRow.Dock = DockStyle.None;
            levelRow.WrapContents = false;
            levelRow.Anchor = AnchorStyles.Left;

            txtFind = new TextBox { Width = Ui.S(220), Anchor = AnchorStyles.Left, Margin = new Padding(3, Ui.S(4), 3, 3), AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.CustomSource };
            Ui.SetCue(txtFind, "Go to a place, e.g. Texas");
            txtFind.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GoTo(txtFind.Text); } };
            var findRow = Ui.Row(Ui.Label("Find"), txtFind,
                Ui.Button("Go", (s, e) => GoTo(txtFind.Text)),
                Ui.Button("+", (s, e) => Map.ZoomBy(1.6), Ui.S(32)),
                Ui.Button("-", (s, e) => Map.ZoomBy(1 / 1.6), Ui.S(32)),
                Ui.Button("Whole world", (s, e) => Map.ZoomWorld()),
                Ui.Button("Show picked", (s, e) => { if (Map.Selected.Count > 0) Map.ZoomToAreas(Map.Selected); }));
            findRow.Dock = DockStyle.None;
            findRow.WrapContents = false;
            findRow.Anchor = AnchorStyles.Right;

            var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.Controls.Add(levelRow, 0, 0);
            bar.Controls.Add(findRow, 1, 0);

            Map = new RegionMap { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var frame = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(0) };
            frame.Controls.Add(Map);

            lblStatus = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = Ui.S(24), ForeColor = Ui.HintColor, TextAlign = ContentAlignment.MiddleLeft,
                                    Text = "Wheel or + / - to zoom, drag to move, click to pick." };

            Controls.Add(frame);
            Controls.Add(bar);
            Controls.Add(lblStatus);
            frame.BringToFront();
        }

        RadioButton Radio(string text, AreaLevel level)
        {
            var r = new RadioButton { Text = text, AutoSize = true, Margin = new Padding(3, Ui.S(6), Ui.S(8), 3), Tag = level };
            r.CheckedChanged += (s, e) =>
            {
                if (!r.Checked || loading) return;
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
            if (IsDisposed) return;
            UseAtlas(atlas);
        }

        /// <summary>Shows an atlas that's already loaded (and fills the Find box's suggestions).</summary>
        public void UseAtlas(GeoAtlas atlas)
        {
            if (Map.Atlas == atlas) return;
            Map.Atlas = atlas;
            byName = new Dictionary<string, GeoArea>(StringComparer.OrdinalIgnoreCase);
            var names = new AutoCompleteStringCollection();
            foreach (var a in atlas.Countries.Concat(atlas.States).Concat(atlas.Counties))
            {
                foreach (var n in new[] { a.FullName, a.Name })
                    if (!byName.ContainsKey(n)) { byName[n] = a; names.Add(n); }
            }
            txtFind.AutoCompleteCustomSource = names;
        }

        public AreaLevel Level
        {
            get { return Map.PickLevel; }
            set
            {
                loading = true;
                optCountry.Checked = value == AreaLevel.Country;
                optState.Checked = value == AreaLevel.State;
                optCounty.Checked = value == AreaLevel.County;
                loading = false;
                Map.PickLevel = value;
                LevelChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Which pick levels the user may choose between.</summary>
        public void AllowLevels(params AreaLevel[] levels)
        {
            optCountry.Visible = levels.Contains(AreaLevel.Country);
            optState.Visible = levels.Contains(AreaLevel.State);
            optCounty.Visible = levels.Contains(AreaLevel.County);
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
