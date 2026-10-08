using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>A repeater (or anything) to mark on the map.</summary>
    sealed class MapDot
    {
        public double Lon, Lat;
        public bool Highlight;
        /// <summary>An analog (FM) repeater: drawn green until picked.</summary>
        public bool Analog;
        /// <summary>Not heard on BrandMeister for a year: drawn light grey, under the others, until picked.</summary>
        public bool OffAir;
        public object Tag;
    }

    /// <summary>
    /// The built-in map (see Core/Geo.cs), drawn with GDI+ in a Mercator projection. Wheel to zoom, drag to pan,
    /// click to pick countries, states/provinces or US counties (<see cref="PickLevel"/>). Nothing is downloaded:
    /// the outlines are embedded in the program.
    /// </summary>
    sealed class RegionMap : Control
    {
        GeoAtlas atlas;
        AreaLevel pickLevel = AreaLevel.State;
        readonly HashSet<GeoArea> selected = new HashSet<GeoArea>();
        readonly Dictionary<GeoArea, float[][]> projected = new Dictionary<GeoArea, float[][]>();
        readonly Dictionary<GeoArea, RectangleF> projectedBox = new Dictionary<GeoArea, RectangleF>();
        GeoArea hover;
        List<MapDot> dots = new List<MapDot>();

        // View: the Mercator point at the middle of the control and pixels per degree.
        double centerX, centerY, scale;
        bool viewSet;
        // A ZoomTo that came before the control had its real size (hidden wizard page, form not shown yet).
        RectangleF? pendingZoom;

        Point downAt;
        bool dragging, mouseDown;
        double downCenterX, downCenterY;

        static readonly Color Water = Color.FromArgb(214, 230, 242);
        static readonly Color Land = Color.FromArgb(246, 244, 238);
        static readonly Color DimLand = Color.FromArgb(229, 229, 226);
        static readonly Color CountryBorder = Color.FromArgb(150, 150, 150);
        static readonly Color StateBorder = Color.FromArgb(186, 186, 186);
        static readonly Color CountyBorder = Color.FromArgb(212, 212, 212);
        static readonly Color SelectedFill = Color.FromArgb(150, 66, 133, 214);
        static readonly Color SelectedBorder = Color.FromArgb(40, 90, 170);
        static readonly Color HoverFill = Color.FromArgb(110, 255, 196, 64);
        static readonly Color DotColor = Color.FromArgb(200, 70, 70, 70);
        static readonly Color DotHighlight = Color.FromArgb(220, 200, 40, 40);
        static readonly Color DotAnalog = Color.FromArgb(210, 20, 140, 70);
        static readonly Color DotOffAir = Color.FromArgb(170, 175, 175, 175);

        /// <summary>The user clicked an area (it's already added to or removed from <see cref="Selected"/>).</summary>
        public event EventHandler SelectionChanged;
        /// <summary>The area under the mouse changed (<see cref="HoverArea"/>, may be null).</summary>
        public event EventHandler HoverChanged;

        /// <summary>Which areas a click may pick; others are grayed out. Null: everything at <see cref="PickLevel"/>.</summary>
        public Func<GeoArea, bool> CanPick { get; set; }
        /// <summary>Optional short text drawn under an area's name (e.g. "12 repeaters").</summary>
        public Func<GeoArea, string> Badge { get; set; }

        public RegionMap()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Ui.BaseFont;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public GeoAtlas Atlas
        {
            get { return atlas; }
            set { atlas = value; projected.Clear(); projectedBox.Clear(); projectedRoads.Clear(); Invalidate(); }
        }

        public AreaLevel PickLevel
        {
            get { return pickLevel; }
            set { pickLevel = value; SetHover(null); Invalidate(); }
        }

        public GeoArea HoverArea => hover;

        public IReadOnlyCollection<GeoArea> Selected => selected;

        public void SetSelected(IEnumerable<GeoArea> areas)
        {
            selected.Clear();
            foreach (var a in areas) if (a != null) selected.Add(a);
            Invalidate();
        }

        public List<MapDot> Dots
        {
            get { return dots; }
            set { dots = value ?? new List<MapDot>(); Invalidate(); }
        }

        // ======================================================================
        // Projection
        // ======================================================================

        static double MercY(double lat)
        {
            lat = Math.Max(-85, Math.Min(85, lat));
            return Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360)) * 180 / Math.PI;
        }

        static double Lat(double mercY)
        {
            return (2 * Math.Atan(Math.Exp(mercY * Math.PI / 180)) - Math.PI / 2) * 180 / Math.PI;
        }

        float[][] Projected(GeoArea a)
        {
            if (projected.TryGetValue(a, out var p)) return p;
            p = new float[a.Rings.Length][];
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int k = 0; k < a.Rings.Length; k++)
            {
                var r = a.Rings[k];
                var q = new float[r.Length];
                for (int i = 0; i < r.Length; i += 2)
                {
                    q[i] = r[i];
                    q[i + 1] = (float)MercY(r[i + 1]);
                    if (q[i + 1] < minY) minY = q[i + 1];
                    if (q[i + 1] > maxY) maxY = q[i + 1];
                }
                p[k] = q;
            }
            projected[a] = p;
            projectedBox[a] = new RectangleF(a.MinLon, minY, a.MaxLon - a.MinLon, maxY - minY);
            return p;
        }

        RectangleF Box(GeoArea a)
        {
            if (!projectedBox.ContainsKey(a)) Projected(a);
            return projectedBox[a];
        }

        bool SizeKnown => Visible && Width > 50 && Height > 50;

        void EnsureView()
        {
            if (!SizeKnown) return;
            if (pendingZoom.HasValue)
            {
                var z = pendingZoom.Value;
                pendingZoom = null;
                ZoomTo(z.Left, z.Top, z.Right, z.Bottom);
            }
            else if (!viewSet) ZoomTo(-170, -56, 180, 75);
        }

        RectangleF ViewRect()
        {
            double w = Width / scale, h = Height / scale;
            return new RectangleF((float)(centerX - w / 2), (float)(centerY - h / 2), (float)w, (float)h);
        }

        PointF ToScreen(double x, double mercY)
        {
            return new PointF((float)((x - centerX) * scale + Width / 2.0), (float)(Height / 2.0 - (mercY - centerY) * scale));
        }

        void ToMap(Point p, out double lon, out double lat)
        {
            lon = centerX + (p.X - Width / 2.0) / scale;
            lat = Lat(centerY + (Height / 2.0 - p.Y) / scale);
        }

        /// <summary>Screen size of an area's box, in pixels.</summary>
        float ScreenWidth(GeoArea a) { return (float)(Box(a).Width * scale); }

        // ======================================================================
        // Zoom
        // ======================================================================

        /// <summary>Fits the given longitude/latitude box in view (with a margin).</summary>
        public void ZoomTo(double minLon, double minLat, double maxLon, double maxLat)
        {
            if (!SizeKnown)
            {
                // Fit it once the control is shown at its real size (see EnsureView).
                pendingZoom = RectangleF.FromLTRB((float)minLon, (float)minLat, (float)maxLon, (float)maxLat);
                return;
            }
            pendingZoom = null;
            double y0 = MercY(minLat), y1 = MercY(maxLat);
            double w = Math.Max(0.05, maxLon - minLon), h = Math.Max(0.05, y1 - y0);
            scale = Math.Min(Width / (w * 1.08), Height / (h * 1.08));
            scale = Math.Max(MinScale, Math.Min(MaxScale, scale));
            centerX = (minLon + maxLon) / 2;
            centerY = (y0 + y1) / 2;
            viewSet = true;
            ClampView();
            Invalidate();
        }

        /// <summary>Fits areas in view. Areas that straddle the date line (Alaska, Russia, Fiji) use their main part.</summary>
        public void ZoomToAreas(IEnumerable<GeoArea> areas)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var a in areas)
                foreach (var r in MainRings(a))
                    for (int i = 0; i < r.Length; i += 2)
                    {
                        minX = Math.Min(minX, r[i]); maxX = Math.Max(maxX, r[i]);
                        minY = Math.Min(minY, r[i + 1]); maxY = Math.Max(maxY, r[i + 1]);
                    }
            if (minX <= maxX) ZoomTo(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// The rings near the area's biggest one: France without French Guiana and Reunion, Alaska without the
        /// Aleutians past the date line, but the Florida Keys still with Florida.
        /// </summary>
        static IEnumerable<float[]> MainRings(GeoArea a)
        {
            if (a.Rings.Length <= 1) return a.Rings;
            RectangleF Bounds(float[] r)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int i = 0; i < r.Length; i += 2)
                {
                    x0 = Math.Min(x0, r[i]); x1 = Math.Max(x1, r[i]);
                    y0 = Math.Min(y0, r[i + 1]); y1 = Math.Max(y1, r[i + 1]);
                }
                return RectangleF.FromLTRB(x0, y0, x1, y1);
            }
            var boxes = a.Rings.Select(Bounds).ToArray();
            int big = 0;
            for (int i = 1; i < boxes.Length; i++)
                if (boxes[i].Width * boxes[i].Height > boxes[big].Width * boxes[big].Height) big = i;
            var main = boxes[big];
            float reach = Math.Max(8f, Math.Max(main.Width, main.Height) * 0.75f);
            var near = RectangleF.Inflate(main, reach, reach);
            return a.Rings.Where((r, i) => near.IntersectsWith(boxes[i]));
        }

        public void ZoomWorld() { ZoomTo(-170, -56, 180, 75); }

        public void ZoomBy(double factor) { ZoomAt(new Point(Width / 2, Height / 2), factor); }

        double MinScale => Math.Max(0.5, Width / 400.0);
        const double MaxScale = 6000;

        void ZoomAt(Point p, double factor)
        {
            EnsureView();
            ToMap(p, out double lon, out double lat);
            double my = MercY(lat);
            double newScale = Math.Max(MinScale, Math.Min(MaxScale, scale * factor));
            // Keep the point under the cursor where it is.
            centerX = lon - (p.X - Width / 2.0) / newScale;
            centerY = my - (Height / 2.0 - p.Y) / newScale;
            scale = newScale;
            ClampView();
            Invalidate();
        }

        void ClampView()
        {
            double halfH = Height / scale / 2;
            double top = MercY(84), bottom = MercY(-60);
            centerX = Math.Max(-200, Math.Min(200, centerX));
            if (top - bottom > 2 * halfH) centerY = Math.Max(bottom + halfH, Math.Min(top - halfH, centerY));
            else centerY = (top + bottom) / 2;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!viewSet || pendingZoom.HasValue) EnsureView();
            else if (SizeKnown) { ClampView(); Invalidate(); }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) EnsureView();
        }

        // ======================================================================
        // Painting
        // ======================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Water);
            if (atlas == null)
            {
                TextRenderer.DrawText(g, "Loading the map...", Font, ClientRectangle, Ui.HintColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            EnsureView();
            if (!viewSet) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var view = ViewRect();

            var visibleCountries = atlas.Countries.Where(c => Box(c).IntersectsWith(view)).ToList();
            // Show state lines inside countries big enough on screen; county lines inside US states big enough.
            var detailedCountries = visibleCountries.Where(c => pickLevel >= AreaLevel.State || ScreenWidth(c) > Ui.S(500)).Where(c => ScreenWidth(c) > Ui.S(60)).ToList();
            var visibleStates = detailedCountries.SelectMany(c => c.Children).Where(s => Box(s).IntersectsWith(view)).ToList();
            var visibleCounties = visibleStates.Where(s => s.Children.Count > 0 && (pickLevel == AreaLevel.County || ScreenWidth(s) > Ui.S(400)) && ScreenWidth(s) > Ui.S(80))
                                               .SelectMany(s => s.Children).Where(c => Box(c).IntersectsWith(view)).ToList();

            using (var land = new SolidBrush(Land))
            using (var dim = new SolidBrush(DimLand))
            using (var countryPen = new Pen(CountryBorder, Math.Max(1f, Ui.S(1))))
            using (var statePen = new Pen(StateBorder, 1f))
            using (var countyPen = new Pen(CountyBorder, 1f))
            using (var selFill = new SolidBrush(SelectedFill))
            using (var selPen = new Pen(SelectedBorder, Math.Max(1.5f, Ui.S(2))) { LineJoin = LineJoin.Round })
            using (var hoverFill = new SolidBrush(HoverFill))
            {
                // Land, grayed where nothing can be picked.
                foreach (var c in visibleCountries)
                    using (var path = PathOf(c)) if (path != null) g.FillPath(Pickable(c) || HasPickableInside(c) ? land : dim, path);
                if (CanPick != null)
                    foreach (var s in visibleStates.Concat(visibleCounties).Where(a => AtPickLevel(a) && !Pickable(a)))
                        using (var path = PathOf(s)) if (path != null) g.FillPath(dim, path);

                // Selection and hover fills.
                foreach (var a in selected.Where(a => Box(a).IntersectsWith(view)))
                    using (var path = PathOf(a)) if (path != null) g.FillPath(selFill, path);
                if (hover != null)
                    using (var path = PathOf(hover)) if (path != null) g.FillPath(hoverFill, path);

                // Borders, finest first so the bolder lines sit on top.
                foreach (var c in visibleCounties)
                    using (var path = PathOf(c)) if (path != null) g.DrawPath(countyPen, path);
                foreach (var s in visibleStates)
                    using (var path = PathOf(s)) if (path != null) g.DrawPath(statePen, path);
                foreach (var c in visibleCountries)
                    using (var path = PathOf(c)) if (path != null) g.DrawPath(countryPen, path);
                foreach (var a in selected.Where(a => Box(a).IntersectsWith(view)))
                    using (var path = PathOf(a)) if (path != null) g.DrawPath(selPen, path);
            }

            var roadLabels = DrawRoads(g, view);
            DrawDots(g, view);
            DrawLabels(g, visibleCountries, visibleStates, visibleCounties);
            DrawRoadLabels(g, roadLabels);
            DrawHoverBox(g);
        }

        /// <summary>A click at the current level would pick this area. Picking "US counties", states without counties (everywhere but the US) count.</summary>
        bool AtPickLevel(GeoArea a)
        {
            return a.Level == pickLevel || (pickLevel == AreaLevel.County && a.Level == AreaLevel.State && a.Children.Count == 0);
        }

        bool Pickable(GeoArea a)
        {
            return AtPickLevel(a) && (CanPick == null || CanPick(a));
        }

        bool HasPickableInside(GeoArea country)
        {
            if (CanPick == null) return true;
            if (pickLevel == AreaLevel.Country) return CanPick(country);
            if (pickLevel == AreaLevel.State) return country.Children.Any(CanPick);
            return country.Children.Any(s => s.Children.Any(CanPick)) || (country.Children.All(s => s.Children.Count == 0) && country.Children.Any(CanPick));
        }

        /// <summary>The area as a screen path (points closer than half a pixel merged), or null when it's off screen.</summary>
        GraphicsPath PathOf(GeoArea a)
        {
            var rings = Projected(a);
            var path = new GraphicsPath(FillMode.Alternate);
            var view = ViewRect();
            bool any = false;
            var pts = new List<PointF>(256);
            foreach (var r in rings)
            {
                // Skip rings outside the view or smaller than a pixel.
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                for (int i = 0; i < r.Length; i += 2)
                {
                    if (r[i] < minX) minX = r[i];
                    if (r[i] > maxX) maxX = r[i];
                    if (r[i + 1] < minY) minY = r[i + 1];
                    if (r[i + 1] > maxY) maxY = r[i + 1];
                }
                if (maxX < view.Left || minX > view.Right || maxY < view.Top || minY > view.Bottom) continue;
                if ((maxX - minX) * scale < 1 && (maxY - minY) * scale < 1) continue;
                pts.Clear();
                PointF last = new PointF(float.NaN, float.NaN);
                for (int i = 0; i < r.Length; i += 2)
                {
                    var p = ToScreen(r[i], r[i + 1]);
                    if (Math.Abs(p.X - last.X) < 0.6f && Math.Abs(p.Y - last.Y) < 0.6f) continue;
                    pts.Add(p);
                    last = p;
                }
                if (pts.Count < 3) continue;
                path.AddPolygon(pts.ToArray());
                any = true;
            }
            if (any) return path;
            path.Dispose();
            return null;
        }

        // ======================================================================
        // Highways
        // ======================================================================

        static readonly Color MajorRoad = Color.FromArgb(225, 224, 140, 40);
        static readonly Color MinorRoad = Color.FromArgb(200, 232, 184, 120);
        // Zoom (pixels per degree) from which each kind shows: about a country at 900 px is 15, a state 70, a county 400.
        const double MajorRoadScale = 12, MinorRoadScale = 40, RoadLabelScale = 130;

        readonly Dictionary<GeoRoad, float[][]> projectedRoads = new Dictionary<GeoRoad, float[][]>();
        bool showRoads = true;

        /// <summary>Draw interstates and other major highways (and, zoomed in, the secondary ones) over the areas.</summary>
        public bool ShowRoads
        {
            get { return showRoads; }
            set { if (showRoads == value) return; showRoads = value; Invalidate(); }
        }

        /// <summary>True when the map has road data (some builds of the atlas have none).</summary>
        public bool HasRoads => atlas != null && atlas.Roads.Count > 0;

        float[][] ProjectedRoad(GeoRoad road)
        {
            if (projectedRoads.TryGetValue(road, out var p)) return p;
            p = new float[road.Lines.Length][];
            for (int k = 0; k < p.Length; k++)
            {
                var src = road.Lines[k];
                var q = new float[src.Length];
                for (int i = 0; i < src.Length; i += 2) { q[i] = src[i]; q[i + 1] = (float)MercY(src[i + 1]); }
                p[k] = q;
            }
            return projectedRoads[road] = p;
        }

        /// <summary>Draws the roads in view; returns the labeled ones with a spot for the label (the middle of their longest line).</summary>
        List<KeyValuePair<string, PointF>> DrawRoads(Graphics g, RectangleF view)
        {
            var labels = new List<KeyValuePair<string, PointF>>();
            if (!showRoads || atlas == null || atlas.Roads.Count == 0 || scale < MajorRoadScale) return labels;
            bool minor = scale >= MinorRoadScale, label = scale >= RoadLabelScale;
            using (var major = new Pen(MajorRoad, Math.Max(1.4f, Ui.S(2))) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var second = new Pen(MinorRoad, Math.Max(1f, Ui.S(1))) { LineJoin = LineJoin.Round })
            {
                var pts = new List<PointF>(128);
                foreach (var pass in new[] { false, true }) // secondary first, so the majors sit on top
                {
                    if (!pass && !minor) continue;
                    foreach (var road in atlas.Roads)
                    {
                        if (road.Major != pass) continue;
                        if (road.MaxLon < view.Left || road.MinLon > view.Right) continue;
                        var lines = ProjectedRoad(road);
                        float[] best = null;
                        foreach (var line in lines)
                        {
                            float minY = float.MaxValue, maxY = float.MinValue;
                            for (int i = 1; i < line.Length; i += 2) { if (line[i] < minY) minY = line[i]; if (line[i] > maxY) maxY = line[i]; }
                            if (maxY < view.Top || minY > view.Bottom) continue;
                            pts.Clear();
                            PointF last = new PointF(float.NaN, float.NaN);
                            for (int i = 0; i < line.Length; i += 2)
                            {
                                var p = ToScreen(line[i], line[i + 1]);
                                if (Math.Abs(p.X - last.X) < 0.8f && Math.Abs(p.Y - last.Y) < 0.8f) continue;
                                pts.Add(p);
                                last = p;
                            }
                            if (pts.Count < 2) continue;
                            g.DrawLines(road.Major ? major : second, pts.ToArray());
                            if (best == null || line.Length > best.Length) best = line;
                        }
                        if (label && best != null && road.Label.Length > 0)
                        {
                            int mid = (best.Length / 4) * 2;
                            labels.Add(new KeyValuePair<string, PointF>(road.Label, ToScreen(best[mid], best[mid + 1])));
                        }
                    }
                }
            }
            return labels;
        }

        /// <summary>Route numbers ("I-35", "US 83") on small white tags, skipping any that would cover another label or the edge.</summary>
        void DrawRoadLabels(Graphics g, List<KeyValuePair<string, PointF>> labels)
        {
            if (labels.Count == 0) return;
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            var taken = new List<Rectangle>();
            using (var fill = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            using (var edge = new Pen(Color.FromArgb(200, 140, 90, 20)))
            {
                foreach (var l in labels)
                {
                    var sz = TextRenderer.MeasureText(l.Key, Font, Size.Empty, flags);
                    var rect = new Rectangle((int)(l.Value.X - sz.Width / 2f) - 3, (int)(l.Value.Y - sz.Height / 2f) - 1, sz.Width + 6, sz.Height + 2);
                    if (!ClientRectangle.Contains(rect) || taken.Any(t => t.IntersectsWith(rect))) continue;
                    taken.Add(Rectangle.Inflate(rect, Ui.S(40), Ui.S(20))); // keep same-numbered tags well apart
                    g.FillRectangle(fill, rect);
                    g.DrawRectangle(edge, rect);
                    TextRenderer.DrawText(g, l.Key, Font, rect, Color.FromArgb(90, 55, 10), flags);
                }
            }
        }

        void DrawDots(Graphics g, RectangleF view)
        {
            if (dots.Count == 0) return;
            float r = Math.Max(2.5f, Math.Min(Ui.S(5), (float)(scale / 40)));
            using (var b = new SolidBrush(DotColor))
            using (var h = new SolidBrush(DotHighlight))
            using (var fm = new SolidBrush(DotAnalog))
            using (var off = new SolidBrush(DotOffAir))
            using (var ring = new Pen(Color.White, 1f))
            {
                foreach (var d in dots.OrderBy(x => x.Highlight ? 2 : x.OffAir ? 0 : 1))
                {
                    double my = MercY(d.Lat);
                    if (d.Lon < view.Left || d.Lon > view.Right || my < view.Top || my > view.Bottom) continue;
                    var p = ToScreen(d.Lon, my);
                    g.FillEllipse(d.Highlight ? h : d.OffAir ? off : d.Analog ? fm : b, p.X - r, p.Y - r, 2 * r, 2 * r);
                    g.DrawEllipse(ring, p.X - r, p.Y - r, 2 * r, 2 * r);
                }
            }
        }

        void DrawLabels(Graphics g, List<GeoArea> countries, List<GeoArea> states, List<GeoArea> counties)
        {
            // Label the pick level once it's readable; zoomed further out, label the level above instead.
            IEnumerable<GeoArea> candidates;
            if (pickLevel == AreaLevel.County && scale >= 30)
                candidates = counties.Concat(states.Where(s => s.Children.Count == 0)); // outside the US, states stand in for counties
            else if (pickLevel >= AreaLevel.State && scale >= 7)
                candidates = states;
            else
                candidates = countries;
            // Grayed-out areas stay quiet unless they're picked or have something to say.
            candidates = candidates.Where(a => selected.Contains(a) || Badge?.Invoke(a) != null || CanPick == null || !AtPickLevel(a) || CanPick(a));
            var taken = new List<Rectangle>();
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            // Areas with a badge (e.g. repeaters in them) and selected ones get first claim on space.
            foreach (var a in candidates.OrderByDescending(a => selected.Contains(a)).ThenByDescending(a => Badge?.Invoke(a) != null).ThenByDescending(a => Box(a).Width * Box(a).Height))
            {
                string badge = Badge?.Invoke(a);
                string name = ShortName(a);
                var sz = TextRenderer.MeasureText(name, Font, Size.Empty, flags);
                var bsz = badge != null ? TextRenderer.MeasureText(badge, Ui.BoldFont, Size.Empty, flags) : Size.Empty;
                int w = Math.Max(sz.Width, bsz.Width), h = sz.Height + bsz.Height;
                var box = Box(a);
                if (box.Width * scale < w * 0.8 && badge == null) continue; // doesn't fit and nothing important to say
                if (box.Width * scale < Ui.S(24)) continue;
                if (badge == null && !selected.Contains(a) && !a.Contains(a.LabelLon, a.LabelLat)) continue; // scattered islands: the middle is open sea
                var c = ToScreen(a.LabelLon, MercY(a.LabelLat));
                var rect = new Rectangle((int)(c.X - w / 2f), (int)(c.Y - h / 2f), w, h);
                if (!ClientRectangle.Contains(rect) || taken.Any(t => t.IntersectsWith(rect))) continue;
                taken.Add(Rectangle.Inflate(rect, Ui.S(4), Ui.S(2)));
                var color = selected.Contains(a) ? Color.FromArgb(20, 40, 90) : Color.FromArgb(70, 70, 70);
                DrawHalo(g, name, Font, new Rectangle(rect.X, rect.Y, w, sz.Height), color, flags);
                if (badge != null)
                    DrawHalo(g, badge, Ui.BoldFont, new Rectangle(rect.X, rect.Y + sz.Height, w, bsz.Height), Color.FromArgb(150, 30, 30), flags);
            }
        }

        /// <summary>Map label: "Tom Green" for "Tom Green County" (the hover box shows the full name).</summary>
        static string ShortName(GeoArea a)
        {
            if (a.Level != AreaLevel.County) return a.Name;
            foreach (var suf in new[] { " County", " Parish", " Borough", " Census Area", " Municipality" })
                if (a.Name.EndsWith(suf, StringComparison.Ordinal)) return a.Name.Substring(0, a.Name.Length - suf.Length);
            return a.Name;
        }

        static void DrawHalo(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            foreach (var off in new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) })
                TextRenderer.DrawText(g, text, font, new Rectangle(r.X + off.X, r.Y + off.Y, r.Width, r.Height), Color.FromArgb(250, 250, 250), flags);
            TextRenderer.DrawText(g, text, font, r, color, flags);
        }

        void DrawHoverBox(Graphics g)
        {
            if (hover == null) return;
            string text = hover.FullName;
            string badge = Badge?.Invoke(hover);
            if (badge != null) text += "  -  " + badge;
            if (!Pickable(hover)) text += "  (not available)";
            var sz = TextRenderer.MeasureText(text, Ui.BaseFont);
            var rect = new Rectangle(Ui.S(8), Ui.S(8), sz.Width + Ui.S(12), sz.Height + Ui.S(8));
            using (var b = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            using (var p = new Pen(Color.FromArgb(160, 160, 160)))
            {
                g.FillRectangle(b, rect);
                g.DrawRectangle(p, rect);
            }
            TextRenderer.DrawText(g, text, Ui.BaseFont, rect, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ======================================================================
        // Hit testing and mouse
        // ======================================================================

        /// <summary>The area at the pick level under a screen point. Outside the US "county" falls back to the state.</summary>
        public GeoArea AreaAt(Point p)
        {
            if (atlas == null) return null;
            ToMap(p, out double lon, out double lat);
            var country = atlas.AreaAt(lon, lat, AreaLevel.Country);
            if (country == null || pickLevel == AreaLevel.Country) return country;
            var state = atlas.AreaAt(lon, lat, AreaLevel.State, country);
            if (state == null || pickLevel == AreaLevel.State || state.Children.Count == 0) return state;
            return atlas.AreaAt(lon, lat, AreaLevel.County, state) ?? state;
        }

        void SetHover(GeoArea a)
        {
            if (a == hover) return;
            hover = a;
            Invalidate();
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left || !viewSet) return;
            mouseDown = true;
            dragging = false;
            downAt = e.Location;
            downCenterX = centerX;
            downCenterY = centerY;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mouseDown && (e.Button & MouseButtons.Left) == 0)
            {
                // The button came up somewhere we didn't hear about (another window, a dialog): stop dragging.
                mouseDown = dragging = false;
                Cursor = Cursors.Hand;
            }
            if (mouseDown && (dragging || Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > Ui.S(5)))
            {
                dragging = true;
                Cursor = Cursors.SizeAll;
                centerX = downCenterX - (e.X - downAt.X) / scale;
                centerY = downCenterY + (e.Y - downAt.Y) / scale;
                ClampView();
                Invalidate();
                return;
            }
            if (viewSet) SetHover(AreaAt(e.Location));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false;
            Cursor = Cursors.Hand;
            if (dragging) { dragging = false; return; }
            var a = AreaAt(e.Location);
            if (a == null || !Pickable(a)) return;
            if (!selected.Remove(a)) selected.Add(a);
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Same as clicking the area (for the --ui-walkthrough self-test).</summary>
        internal void ClickArea(GeoArea a)
        {
            if (a == null || !Pickable(a)) return;
            if (!selected.Remove(a)) selected.Add(a);
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(null);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!viewSet) return;
            ZoomAt(e.Location, Math.Pow(1.3, e.Delta / 120.0));
            SetHover(AreaAt(e.Location));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!viewSet) return;
            double step = Ui.S(80) / scale;
            switch (e.KeyCode)
            {
                case Keys.Add: case Keys.Oemplus: ZoomBy(1.5); break;
                case Keys.Subtract: case Keys.OemMinus: ZoomBy(1 / 1.5); break;
                case Keys.Left: centerX -= step; break;
                case Keys.Right: centerX += step; break;
                case Keys.Up: centerY += step; break;
                case Keys.Down: centerY -= step; break;
                default: return;
            }
            ClampView();
            Invalidate();
            e.Handled = true;
        }
    }
}
