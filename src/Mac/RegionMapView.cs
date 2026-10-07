using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>A repeater (or anything) to mark on the map.</summary>
    sealed class MapDot
    {
        public double Lon, Lat;
        public bool Highlight;
        /// <summary>An analog (FM) repeater: drawn green until picked.</summary>
        public bool Analog;
        public object Tag;
    }

    /// <summary>
    /// The built-in map (see Core/Geo.cs), drawn in a Mercator projection with Avalonia's DrawingContext: the same view, zoom
    /// levels, picking and highways as the Windows RegionMap. Wheel to zoom, drag to pan, click to pick countries, states or
    /// US counties (<see cref="PickLevel"/>). Nothing is downloaded: the outlines are embedded in the program.
    /// </summary>
    sealed class RegionMapView : Control
    {
        GeoAtlas atlas;
        AreaLevel pickLevel = AreaLevel.State;
        readonly HashSet<GeoArea> selected = new HashSet<GeoArea>();
        readonly Dictionary<GeoArea, float[][]> projected = new Dictionary<GeoArea, float[][]>();
        readonly Dictionary<GeoArea, Rect> projectedBox = new Dictionary<GeoArea, Rect>();
        readonly Dictionary<GeoRoad, float[][]> projectedRoads = new Dictionary<GeoRoad, float[][]>();
        GeoArea hover;
        List<MapDot> dots = new List<MapDot>();
        bool showRoads = true;

        // View: the Mercator point at the middle of the control and pixels per degree.
        double centerX, centerY, scale;
        bool viewSet;
        // A ZoomTo that came before the control had its real size.
        Rect? pendingZoom;

        Point downAt;
        bool dragging, mouseDown;
        double downCenterX, downCenterY;

        static readonly IBrush Water = new SolidColorBrush(Color.FromRgb(214, 230, 242));
        static readonly IBrush Land = new SolidColorBrush(Color.FromRgb(246, 244, 238));
        static readonly IBrush DimLand = new SolidColorBrush(Color.FromRgb(229, 229, 226));
        static readonly IPen CountryPen = new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 1);
        static readonly IPen StatePen = new Pen(new SolidColorBrush(Color.FromRgb(186, 186, 186)), 1);
        static readonly IPen CountyPen = new Pen(new SolidColorBrush(Color.FromRgb(212, 212, 212)), 1);
        static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(150, 66, 133, 214));
        static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(40, 90, 170)), 2, lineJoin: PenLineJoin.Round);
        static readonly IBrush HoverFill = new SolidColorBrush(Color.FromArgb(110, 255, 196, 64));
        static readonly IBrush DotColor = new SolidColorBrush(Color.FromArgb(200, 70, 70, 70));
        static readonly IBrush DotHighlight = new SolidColorBrush(Color.FromArgb(220, 200, 40, 40));
        static readonly IBrush DotAnalog = new SolidColorBrush(Color.FromArgb(210, 20, 140, 70));
        static readonly IPen DotRing = new Pen(Brushes.White, 1);
        static readonly IPen MajorRoadPen = new Pen(new SolidColorBrush(Color.FromArgb(225, 224, 140, 40)), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        static readonly IPen MinorRoadPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 232, 184, 120)), 1, lineJoin: PenLineJoin.Round);
        // Zoom (pixels per degree) from which each kind of road shows: about a country at 900 px is 15, a state 70, a county 400.
        const double MajorRoadScale = 12, MinorRoadScale = 40, RoadLabelScale = 130;
        static readonly Typeface Face = new Typeface(FontFamily.Default);
        static readonly Typeface BoldFace = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        const double FontSize = 13;

        /// <summary>The user clicked an area (it's already added to or removed from <see cref="Selected"/>).</summary>
        public event EventHandler SelectionChanged;
        /// <summary>The area under the mouse changed (<see cref="HoverArea"/>, may be null).</summary>
        public event EventHandler HoverChanged;

        /// <summary>Which areas a click may pick; others are grayed out. Null: everything at <see cref="PickLevel"/>.</summary>
        public Func<GeoArea, bool> CanPick { get; set; }
        /// <summary>Optional short text drawn under an area's name (e.g. "12 repeaters").</summary>
        public Func<GeoArea, string> Badge { get; set; }

        public RegionMapView()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            // A Mac trackpad pinch zooms (Avalonia raises Pinch for the trackpad's magnify gesture). Not tried on a real Mac yet.
            AddHandler(Gestures.PinchEvent, OnPinch);
            AddHandler(Gestures.PinchEndedEvent, (s, e) => lastPinch = 1);
        }

        double lastPinch = 1;

        void OnPinch(object sender, PinchEventArgs e)
        {
            if (!viewSet) return;
            double factor = lastPinch > 0 ? e.Scale / lastPinch : 1;
            lastPinch = e.Scale;
            if (factor > 0 && Math.Abs(factor - 1) > 0.001) ZoomAt(e.ScaleOrigin, factor);
            e.Handled = true;
        }

        public GeoAtlas Atlas
        {
            get { return atlas; }
            set { atlas = value; projected.Clear(); projectedBox.Clear(); projectedRoads.Clear(); InvalidateVisual(); }
        }

        public AreaLevel PickLevel
        {
            get { return pickLevel; }
            set { pickLevel = value; SetHover(null); InvalidateVisual(); }
        }

        public GeoArea HoverArea => hover;
        public IReadOnlyCollection<GeoArea> Selected => selected;

        public void SetSelected(IEnumerable<GeoArea> areas)
        {
            selected.Clear();
            foreach (var a in areas) if (a != null) selected.Add(a);
            InvalidateVisual();
        }

        public List<MapDot> Dots
        {
            get { return dots; }
            set { dots = value ?? new List<MapDot>(); InvalidateVisual(); }
        }

        /// <summary>Draw interstates and other major highways (and, zoomed in, the secondary ones) over the areas.</summary>
        public bool ShowRoads
        {
            get { return showRoads; }
            set { if (showRoads == value) return; showRoads = value; InvalidateVisual(); }
        }

        /// <summary>True when the map has road data (some builds of the atlas have none).</summary>
        public bool HasRoads => atlas != null && atlas.Roads.Count > 0;

        public double Scale => scale;

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
            projectedBox[a] = new Rect(a.MinLon, minY, a.MaxLon - a.MinLon, maxY - minY);
            return p;
        }

        Rect Box(GeoArea a)
        {
            if (!projectedBox.ContainsKey(a)) Projected(a);
            return projectedBox[a];
        }

        static bool Touches(Rect a, Rect b)
        {
            return a.X <= b.Right && a.Right >= b.X && a.Y <= b.Bottom && a.Bottom >= b.Y;
        }

        bool SizeKnown => IsVisible && Bounds.Width > 50 && Bounds.Height > 50;

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

        Rect ViewRect()
        {
            double w = Bounds.Width / scale, h = Bounds.Height / scale;
            return new Rect(centerX - w / 2, centerY - h / 2, w, h);
        }

        Point ToScreen(double x, double mercY)
        {
            return new Point((x - centerX) * scale + Bounds.Width / 2.0, Bounds.Height / 2.0 - (mercY - centerY) * scale);
        }

        void ToMap(Point p, out double lon, out double lat)
        {
            lon = centerX + (p.X - Bounds.Width / 2.0) / scale;
            lat = Lat(centerY + (Bounds.Height / 2.0 - p.Y) / scale);
        }

        double ScreenWidth(GeoArea a) { return Box(a).Width * scale; }

        // ======================================================================
        // Zoom
        // ======================================================================

        /// <summary>Fits the given longitude/latitude box in view (with a margin).</summary>
        public void ZoomTo(double minLon, double minLat, double maxLon, double maxLat)
        {
            if (!SizeKnown)
            {
                pendingZoom = new Rect(minLon, minLat, maxLon - minLon, maxLat - minLat); // fit it once the control has its real size
                return;
            }
            pendingZoom = null;
            double y0 = MercY(minLat), y1 = MercY(maxLat);
            double w = Math.Max(0.05, maxLon - minLon), h = Math.Max(0.05, y1 - y0);
            scale = Math.Min(Bounds.Width / (w * 1.08), Bounds.Height / (h * 1.08));
            scale = Math.Max(MinScale, Math.Min(MaxScale, scale));
            centerX = (minLon + maxLon) / 2;
            centerY = (y0 + y1) / 2;
            viewSet = true;
            ClampView();
            InvalidateVisual();
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

        /// <summary>The rings near the area's biggest one: France without French Guiana, Alaska without the Aleutians past the date line.</summary>
        static IEnumerable<float[]> MainRings(GeoArea a)
        {
            if (a.Rings.Length <= 1) return a.Rings;
            Rect Bounds(float[] r)
            {
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                for (int i = 0; i < r.Length; i += 2)
                {
                    x0 = Math.Min(x0, r[i]); x1 = Math.Max(x1, r[i]);
                    y0 = Math.Min(y0, r[i + 1]); y1 = Math.Max(y1, r[i + 1]);
                }
                return new Rect(x0, y0, x1 - x0, y1 - y0);
            }
            var boxes = a.Rings.Select(Bounds).ToArray();
            int big = 0;
            for (int i = 1; i < boxes.Length; i++)
                if (boxes[i].Width * boxes[i].Height > boxes[big].Width * boxes[big].Height) big = i;
            var main = boxes[big];
            double reach = Math.Max(8, Math.Max(main.Width, main.Height) * 0.75);
            var near = main.Inflate(reach);
            return a.Rings.Where((r, i) => Touches(near, boxes[i]));
        }

        public void ZoomWorld() { ZoomTo(-170, -56, 180, 75); }

        public void ZoomBy(double factor) { ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor); }

        double MinScale => Math.Max(0.5, Bounds.Width / 400.0);
        const double MaxScale = 6000;

        void ZoomAt(Point p, double factor)
        {
            EnsureView();
            ToMap(p, out double lon, out double lat);
            double my = MercY(lat);
            double newScale = Math.Max(MinScale, Math.Min(MaxScale, scale * factor));
            // Keep the point under the cursor where it is.
            centerX = lon - (p.X - Bounds.Width / 2.0) / newScale;
            centerY = my - (Bounds.Height / 2.0 - p.Y) / newScale;
            scale = newScale;
            ClampView();
            InvalidateVisual();
        }

        void ClampView()
        {
            double halfH = Bounds.Height / scale / 2;
            double top = MercY(84), bottom = MercY(-60);
            centerX = Math.Max(-200, Math.Min(200, centerX));
            if (top - bottom > 2 * halfH) centerY = Math.Max(bottom + halfH, Math.Min(top - halfH, centerY));
            else centerY = (top + bottom) / 2;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (!viewSet || pendingZoom.HasValue) EnsureView();
            else if (SizeKnown) { ClampView(); InvalidateVisual(); }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            EnsureView();
        }

        // ======================================================================
        // Painting
        // ======================================================================

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);
            ctx.FillRectangle(Water, new Rect(Bounds.Size));
            if (atlas == null)
            {
                DrawText(ctx, "Loading the map...", new Point(Bounds.Width / 2, Bounds.Height / 2), Brushes.Gray, false, true);
                return;
            }
            EnsureView();
            if (!viewSet) return;
            var view = ViewRect();

            var visibleCountries = atlas.Countries.Where(c => Touches(Box(c), view)).ToList();
            // Show state lines inside countries big enough on screen; county lines inside US states big enough.
            var detailedCountries = visibleCountries.Where(c => pickLevel >= AreaLevel.State || ScreenWidth(c) > 500).Where(c => ScreenWidth(c) > 60).ToList();
            var visibleStates = detailedCountries.SelectMany(c => c.Children).Where(s => Touches(Box(s), view)).ToList();
            var visibleCounties = visibleStates.Where(s => s.Children.Count > 0 && (pickLevel == AreaLevel.County || ScreenWidth(s) > 400) && ScreenWidth(s) > 80)
                                               .SelectMany(s => s.Children).Where(c => Touches(Box(c), view)).ToList();

            // Land, grayed where nothing can be picked.
            foreach (var c in visibleCountries)
            {
                var g = GeometryOf(c, view);
                if (g != null) ctx.DrawGeometry(Pickable(c) || HasPickableInside(c) ? Land : DimLand, null, g);
            }
            if (CanPick != null)
                foreach (var s in visibleStates.Concat(visibleCounties).Where(a => AtPickLevel(a) && !Pickable(a)))
                {
                    var g = GeometryOf(s, view);
                    if (g != null) ctx.DrawGeometry(DimLand, null, g);
                }

            // Selection and hover fills.
            foreach (var a in selected.Where(a => Touches(Box(a), view)))
            {
                var g = GeometryOf(a, view);
                if (g != null) ctx.DrawGeometry(SelectedFill, null, g);
            }
            if (hover != null)
            {
                var g = GeometryOf(hover, view);
                if (g != null) ctx.DrawGeometry(HoverFill, null, g);
            }

            // Borders, finest first so the bolder lines sit on top.
            foreach (var c in visibleCounties) Outline(ctx, CountyPen, c, view);
            foreach (var s in visibleStates) Outline(ctx, StatePen, s, view);
            foreach (var c in visibleCountries) Outline(ctx, CountryPen, c, view);
            foreach (var a in selected.Where(a => Touches(Box(a), view))) Outline(ctx, SelectedPen, a, view);

            var roadLabels = DrawRoads(ctx, view);
            DrawDots(ctx, view);
            DrawLabels(ctx, visibleCountries, visibleStates, visibleCounties);
            DrawRoadLabels(ctx, roadLabels);
            DrawHoverBox(ctx);
        }

        void Outline(DrawingContext ctx, IPen pen, GeoArea a, Rect view)
        {
            var g = GeometryOf(a, view);
            if (g != null) ctx.DrawGeometry(null, pen, g);
        }

        /// <summary>A click at the current level would pick this area. Picking "US counties", states without counties (everywhere but the US) count.</summary>
        bool AtPickLevel(GeoArea a)
        {
            return a.Level == pickLevel || (pickLevel == AreaLevel.County && a.Level == AreaLevel.State && a.Children.Count == 0);
        }

        bool Pickable(GeoArea a) { return AtPickLevel(a) && (CanPick == null || CanPick(a)); }

        bool HasPickableInside(GeoArea country)
        {
            if (CanPick == null) return true;
            if (pickLevel == AreaLevel.Country) return CanPick(country);
            if (pickLevel == AreaLevel.State) return country.Children.Any(CanPick);
            return country.Children.Any(s => s.Children.Any(CanPick)) || (country.Children.All(s => s.Children.Count == 0) && country.Children.Any(CanPick));
        }

        /// <summary>The area as a screen geometry (points closer than half a pixel merged), or null when it's off screen.</summary>
        StreamGeometry GeometryOf(GeoArea a, Rect view)
        {
            var rings = Projected(a);
            var geo = new StreamGeometry();
            bool any = false;
            using (var c = geo.Open())
            {
                c.SetFillRule(FillRule.EvenOdd); // holes (lakes, enclaves) are rings too
                var pts = new List<Point>(256);
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
                    Point last = new Point(double.NaN, double.NaN);
                    for (int i = 0; i < r.Length; i += 2)
                    {
                        var p = ToScreen(r[i], r[i + 1]);
                        if (Math.Abs(p.X - last.X) < 0.6 && Math.Abs(p.Y - last.Y) < 0.6) continue;
                        pts.Add(p);
                        last = p;
                    }
                    if (pts.Count < 3) continue;
                    c.BeginFigure(pts[0], true);
                    for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i]);
                    c.EndFigure(true);
                    any = true;
                }
            }
            return any ? geo : null;
        }

        // ---------------- Highways ----------------

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
        List<KeyValuePair<string, Point>> DrawRoads(DrawingContext ctx, Rect view)
        {
            var labels = new List<KeyValuePair<string, Point>>();
            if (!showRoads || atlas == null || atlas.Roads.Count == 0 || scale < MajorRoadScale) return labels;
            bool minor = scale >= MinorRoadScale, label = scale >= RoadLabelScale;
            foreach (var pass in new[] { false, true }) // secondary first, so the majors sit on top
            {
                if (!pass && !minor) continue;
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    var pts = new List<Point>(128);
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
                            Point last = new Point(double.NaN, double.NaN);
                            for (int i = 0; i < line.Length; i += 2)
                            {
                                var p = ToScreen(line[i], line[i + 1]);
                                if (Math.Abs(p.X - last.X) < 0.8 && Math.Abs(p.Y - last.Y) < 0.8) continue;
                                pts.Add(p);
                                last = p;
                            }
                            if (pts.Count < 2) continue;
                            c.BeginFigure(pts[0], false);
                            for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i]);
                            c.EndFigure(false);
                            if (best == null || line.Length > best.Length) best = line;
                        }
                        if (label && pass && best != null && road.Label.Length > 0)
                        {
                            int mid = (best.Length / 4) * 2;
                            labels.Add(new KeyValuePair<string, Point>(road.Label, ToScreen(best[mid], best[mid + 1])));
                        }
                    }
                }
                ctx.DrawGeometry(null, pass ? MajorRoadPen : MinorRoadPen, geo);
            }
            return labels;
        }

        /// <summary>Route numbers ("I-35", "US 83") on small tags, skipping any that would cover another label or the edge.</summary>
        void DrawRoadLabels(DrawingContext ctx, List<KeyValuePair<string, Point>> labels)
        {
            if (labels.Count == 0) return;
            var taken = new List<Rect>();
            var fill = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(200, 140, 90, 20)), 1);
            var text = new SolidColorBrush(Color.FromRgb(90, 55, 10));
            var all = new Rect(Bounds.Size);
            foreach (var l in labels)
            {
                var ft = Format(l.Key, text, false);
                var rect = new Rect(l.Value.X - ft.Width / 2 - 3, l.Value.Y - ft.Height / 2 - 1, ft.Width + 6, ft.Height + 2);
                if (!all.Contains(rect.TopLeft) || !all.Contains(rect.BottomRight) || taken.Any(t => Touches(t, rect))) continue;
                taken.Add(rect.Inflate(new Thickness(40, 20)));
                ctx.DrawRectangle(fill, edge, rect);
                ctx.DrawText(ft, new Point(rect.X + 3, rect.Y + 1));
            }
        }

        // ---------------- Dots and labels ----------------

        void DrawDots(DrawingContext ctx, Rect view)
        {
            if (dots.Count == 0) return;
            double r = Math.Max(2.5, Math.Min(5, scale / 40));
            foreach (var d in dots.OrderBy(x => x.Highlight))
            {
                double my = MercY(d.Lat);
                if (d.Lon < view.Left || d.Lon > view.Right || my < view.Top || my > view.Bottom) continue;
                var p = ToScreen(d.Lon, my);
                ctx.DrawEllipse(d.Highlight ? DotHighlight : d.Analog ? DotAnalog : DotColor, DotRing, p, r, r);
            }
        }

        FormattedText Format(string text, IBrush brush, bool bold)
        {
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, FontSize, brush);
        }

        /// <summary>Text with a light halo so it reads over lines. <paramref name="center"/>: the point is the middle of the text.</summary>
        void DrawText(DrawingContext ctx, string text, Point at, IBrush color, bool bold, bool center)
        {
            var ft = Format(text, color, bold);
            var origin = center ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : at;
            var halo = Format(text, new SolidColorBrush(Color.FromArgb(250, 250, 250, 250)), bold);
            foreach (var off in new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) })
                ctx.DrawText(halo, new Point(origin.X + off.X, origin.Y + off.Y));
            ctx.DrawText(ft, origin);
        }

        void DrawLabels(DrawingContext ctx, List<GeoArea> countries, List<GeoArea> states, List<GeoArea> counties)
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
            var taken = new List<Rect>();
            var all = new Rect(Bounds.Size);
            // Areas with a badge (e.g. repeaters in them) and selected ones get first claim on space.
            foreach (var a in candidates.OrderByDescending(a => selected.Contains(a)).ThenByDescending(a => Badge?.Invoke(a) != null).ThenByDescending(a => Box(a).Width * Box(a).Height))
            {
                string badge = Badge?.Invoke(a);
                string name = ShortName(a);
                var nameText = Format(name, null, false);
                var badgeText = badge != null ? Format(badge, null, true) : null;
                double w = Math.Max(nameText.Width, badgeText?.Width ?? 0), h = nameText.Height + (badgeText?.Height ?? 0);
                var box = Box(a);
                if (box.Width * scale < w * 0.8 && badge == null) continue; // doesn't fit and nothing important to say
                if (box.Width * scale < 24) continue;
                if (badge == null && !selected.Contains(a) && !a.Contains(a.LabelLon, a.LabelLat)) continue; // scattered islands: the middle is open sea
                var c = ToScreen(a.LabelLon, MercY(a.LabelLat));
                var rect = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
                if (!all.Contains(rect.TopLeft) || !all.Contains(rect.BottomRight) || taken.Any(t => Touches(t, rect))) continue;
                taken.Add(rect.Inflate(new Thickness(4, 2)));
                var color = new SolidColorBrush(selected.Contains(a) ? Color.FromRgb(20, 40, 90) : Color.FromRgb(70, 70, 70));
                DrawText(ctx, name, new Point(c.X, rect.Y + nameText.Height / 2), color, false, true);
                if (badge != null)
                    DrawText(ctx, badge, new Point(c.X, rect.Y + nameText.Height + badgeText.Height / 2), new SolidColorBrush(Color.FromRgb(150, 30, 30)), true, true);
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

        void DrawHoverBox(DrawingContext ctx)
        {
            if (hover == null) return;
            string text = hover.FullName;
            string badge = Badge?.Invoke(hover);
            if (badge != null) text += "  -  " + badge;
            if (!Pickable(hover)) text += "  (not available)";
            var ft = Format(text, Brushes.Black, false);
            var rect = new Rect(8, 8, ft.Width + 12, ft.Height + 8);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromRgb(160, 160, 160)), 1), rect);
            ctx.DrawText(ft, new Point(rect.X + 6, rect.Y + 4));
        }

        // ======================================================================
        // Hit testing and mouse
        // ======================================================================

        /// <summary>The area at the pick level under a point. Outside the US "county" falls back to the state.</summary>
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
            InvalidateVisual();
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed || !viewSet) return;
            mouseDown = true;
            dragging = false;
            downAt = pt.Position;
            downCenterX = centerX;
            downCenterY = centerY;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);
            if (mouseDown && (dragging || Math.Abs(pos.X - downAt.X) + Math.Abs(pos.Y - downAt.Y) > 5))
            {
                dragging = true;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                centerX = downCenterX - (pos.X - downAt.X) / scale;
                centerY = downCenterY + (pos.Y - downAt.Y) / scale;
                ClampView();
                InvalidateVisual();
                return;
            }
            if (viewSet) SetHover(AreaAt(pos));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!mouseDown) return;
            mouseDown = false;
            e.Pointer.Capture(null);
            Cursor = new Cursor(StandardCursorType.Hand);
            if (dragging) { dragging = false; return; }
            ClickArea(AreaAt(e.GetPosition(this)));
        }

        /// <summary>Same as clicking the area.</summary>
        public void ClickArea(GeoArea a)
        {
            if (a == null || !Pickable(a)) return;
            if (!selected.Remove(a)) selected.Add(a);
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (!mouseDown) SetHover(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (!viewSet) return;
            var pos = e.GetPosition(this);
            ZoomAt(pos, Math.Pow(1.3, e.Delta.Y));
            SetHover(AreaAt(pos));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!viewSet) return;
            double step = 80 / scale;
            switch (e.Key)
            {
                case Key.Add: case Key.OemPlus: ZoomBy(1.5); break;
                case Key.Subtract: case Key.OemMinus: ZoomBy(1 / 1.5); break;
                case Key.Left: centerX -= step; break;
                case Key.Right: centerX += step; break;
                case Key.Up: centerY += step; break;
                case Key.Down: centerY -= step; break;
                default: return;
            }
            ClampView();
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
