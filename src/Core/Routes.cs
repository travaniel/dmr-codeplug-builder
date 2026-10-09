using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>A driving route: a line along the highways from one town to another.</summary>
    public sealed class RoutePlan
    {
        /// <summary>The line, as latitude/longitude pairs from start to end.</summary>
        public List<double[]> Points = new List<double[]>();
        public double LengthKm;
        /// <summary>Highway labels used, in driving order ("US 183, I-20").</summary>
        public List<string> Via = new List<string>();
        /// <summary>False when no road joined the towns and the route is a straight line.</summary>
        public bool OnRoads;
        /// <summary>The highway asked for doesn't join the towns in the built-in data (often unlabeled there), so any highway was used.</summary>
        public bool HighwayMissing;
    }

    /// <summary>A listing near a route: how far along the route, and how far off it.</summary>
    public sealed class RouteStop
    {
        public OnlineRepeater Listing;
        public Repeater Repeater;
        public double AlongKm, OffKm;
    }

    /// <summary>
    /// Route builder (roadmap item 11): finds a route on the built-in highways (<see cref="GeoAtlas.Roads"/>) between two points,
    /// optionally on one highway only, and the repeaters within a corridor of it in driving order. Pure; the App shows it on the map.
    /// </summary>
    public static class RoutePlanner
    {
        /// <summary>Roads farther than this from a town aren't used to start or end the route.</summary>
        public const double MaxReachKm = 40;
        /// <summary>Line ends this close to another line are joined (the simplified roads have small gaps at junctions).</summary>
        public const double JoinKm = 2.5;

        /// <summary>"US-183", "US 183", "us183" → "US183"; "183" → "183".</summary>
        public static string RoadKey(string label)
        {
            return new string((label ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        /// <summary>True when a road's label is the highway the user typed ("183" matches "US 183"; "Hwy 183" too).</summary>
        public static bool MatchesHighway(string label, string typed)
        {
            string a = RoadKey(label), b = RoadKey(typed);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            string digitsA = new string(a.Where(char.IsDigit).ToArray()), digitsB = new string(b.Where(char.IsDigit).ToArray());
            if (digitsB.Length == 0 || digitsA != digitsB) return false;
            string lettersB = new string(b.Where(char.IsLetter).ToArray());
            return lettersB.Length == 0 || lettersB == "HWY" || lettersB == "HIGHWAY" || lettersB == "SH" || lettersB == "ROUTE" || a.StartsWith(lettersB, StringComparison.Ordinal);
        }

        sealed class Node
        {
            public double Lat, Lon;
            public List<KeyValuePair<Node, double>> Edges = new List<KeyValuePair<Node, double>>();
            public List<string> Labels = new List<string>(1);
            public double Dist = double.MaxValue;
            public Node Prev;
            public int Id;
        }

        /// <summary>
        /// The shortest route on the highways (only <paramref name="highway"/> when given) from A to B, or a straight line when no
        /// road joins them. Roads within the box around A and B (plus a margin) are used.
        /// </summary>
        public static RoutePlan Find(GeoAtlas atlas, double latA, double lonA, double latB, double lonB, string highway = null)
        {
            var plan = FindOn(atlas, latA, lonA, latB, lonB, highway);
            if (plan.OnRoads || string.IsNullOrWhiteSpace(highway)) return plan;
            // Natural Earth leaves many US and state routes unlabeled: fall back to every highway.
            plan = FindOn(atlas, latA, lonA, latB, lonB, null);
            plan.HighwayMissing = true;
            return plan;
        }

        static RoutePlan FindOn(GeoAtlas atlas, double latA, double lonA, double latB, double lonB, string highway)
        {
            double margin = Math.Max(0.6, 0.25 * Math.Max(Math.Abs(latA - latB), Math.Abs(lonA - lonB)));
            double minLat = Math.Min(latA, latB) - margin, maxLat = Math.Max(latA, latB) + margin;
            double minLon = Math.Min(lonA, lonB) - margin * 1.3, maxLon = Math.Max(lonA, lonB) + margin * 1.3;
            var nodes = new Dictionary<long, Node>();
            var ends = new List<Node>();
            Node NodeAt(double lat, double lon)
            {
                long key = (long)Math.Round(lat * 10000) * 4000000L + (long)Math.Round(lon * 10000);
                if (!nodes.TryGetValue(key, out var n)) nodes[key] = n = new Node { Lat = lat, Lon = lon, Id = nodes.Count };
                return n;
            }
            void Link(Node a, Node b, double km) { a.Edges.Add(new KeyValuePair<Node, double>(b, km)); b.Edges.Add(new KeyValuePair<Node, double>(a, km)); }

            foreach (var road in atlas?.Roads ?? new List<GeoRoad>())
            {
                if (road.MaxLon < minLon || road.MinLon > maxLon || road.MaxLat < minLat || road.MinLat > maxLat) continue;
                if (!string.IsNullOrWhiteSpace(highway) && !MatchesHighway(road.Label, highway)) continue;
                foreach (var line in road.Lines)
                {
                    Node last = null;
                    for (int i = 0; i + 1 < line.Length; i += 2)
                    {
                        double lon = line[i], lat = line[i + 1];
                        if (lat < minLat || lat > maxLat || lon < minLon || lon > maxLon) { if (last != null) ends.Add(last); last = null; continue; }
                        var n = NodeAt(lat, lon);
                        if (road.Label.Length > 0 && !n.Labels.Contains(road.Label)) n.Labels.Add(road.Label);
                        if (last == null) ends.Add(n);
                        else if (last != n) Link(last, n, Distances.Km(last.Lat, last.Lon, n.Lat, n.Lon));
                        last = n;
                    }
                    if (last != null) ends.Add(last);
                }
            }
            var all = nodes.Values.ToList();
            var grid = Grid(all, 0.05);
            foreach (var e in ends.Distinct())
                foreach (var o in Near(grid, e.Lat, e.Lon, 0.05))
                    if (o != e && !e.Edges.Any(x => x.Key == o))
                    {
                        double km = Distances.Km(e.Lat, e.Lon, o.Lat, o.Lon);
                        if (km <= JoinKm) Link(e, o, km);
                    }

            // The towns join the graph through the nearest few road points (driving to the highway).
            var start = new Node { Lat = latA, Lon = lonA, Id = -1 };
            var goal = new Node { Lat = latB, Lon = lonB, Id = -2 };
            foreach (var t in new[] { start, goal })
                foreach (var n in all.Select(n => new { n, d = Distances.Km(t.Lat, t.Lon, n.Lat, n.Lon) }).Where(x => x.d <= MaxReachKm).OrderBy(x => x.d).Take(6))
                    Link(t, n.n, n.d * 1.3); // off-highway miles cost a little more
            Dijkstra(start);
            var plan = new RoutePlan();
            if (goal.Prev == null)
            {
                plan.Points.Add(new[] { latA, lonA });
                plan.Points.Add(new[] { latB, lonB });
                plan.LengthKm = Distances.Km(latA, lonA, latB, lonB);
                return plan;
            }
            var path = new List<Node>();
            for (var n = goal; n != null; n = n.Prev) path.Add(n);
            path.Reverse();
            plan.OnRoads = true;
            plan.Points = path.Select(n => new[] { n.Lat, n.Lon }).ToList();
            for (int i = 1; i < plan.Points.Count; i++) plan.LengthKm += Distances.Km(plan.Points[i - 1][0], plan.Points[i - 1][1], plan.Points[i][0], plan.Points[i][1]);
            foreach (var n in path)
                foreach (var l in n.Labels)
                    if (!plan.Via.Contains(l) && path.Count(x => x.Labels.Contains(l)) >= 3) plan.Via.Add(l);
            return plan;
        }

        static void Dijkstra(Node start)
        {
            start.Dist = 0;
            var queue = new SortedSet<Tuple<double, int, Node>>(Comparer<Tuple<double, int, Node>>.Create((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1);
                return c != 0 ? c : a.Item2.CompareTo(b.Item2);
            }));
            queue.Add(Tuple.Create(0.0, start.Id, start));
            while (queue.Count > 0)
            {
                var top = queue.Min;
                queue.Remove(top);
                var n = top.Item3;
                if (top.Item1 > n.Dist) continue;
                foreach (var e in n.Edges)
                {
                    double d = n.Dist + e.Value;
                    if (d >= e.Key.Dist) continue;
                    if (e.Key.Dist != double.MaxValue) queue.Remove(Tuple.Create(e.Key.Dist, e.Key.Id, e.Key));
                    e.Key.Dist = d;
                    e.Key.Prev = n;
                    queue.Add(Tuple.Create(d, e.Key.Id, e.Key));
                }
            }
        }

        static Dictionary<long, List<Node>> Grid(IEnumerable<Node> nodes, double cell)
        {
            var g = new Dictionary<long, List<Node>>();
            foreach (var n in nodes)
            {
                long k = Cell(n.Lat, n.Lon, cell);
                if (!g.TryGetValue(k, out var l)) g[k] = l = new List<Node>();
                l.Add(n);
            }
            return g;
        }

        static long Cell(double lat, double lon, double cell) { return (long)Math.Floor(lat / cell) * 100000L + (long)Math.Floor(lon / cell); }

        static IEnumerable<Node> Near(Dictionary<long, List<Node>> grid, double lat, double lon, double cell)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (grid.TryGetValue(Cell(lat + dy * cell, lon + dx * cell, cell), out var l))
                        foreach (var n in l) yield return n;
        }

        /// <summary>Distance from a point to the route (km) and how far along the route its nearest point is (km).</summary>
        public static double DistanceTo(IList<double[]> points, double lat, double lon, out double alongKm)
        {
            alongKm = 0;
            if (points == null || points.Count == 0) return double.MaxValue;
            if (points.Count == 1) return Distances.Km(lat, lon, points[0][0], points[0][1]);
            double best = double.MaxValue, run = 0;
            double kx = 111.32 * Math.Cos(lat * Math.PI / 180), ky = 110.57; // local flat projection, km per degree
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                double ax = (a[1] - lon) * kx, ay = (a[0] - lat) * ky, bx = (b[1] - lon) * kx, by = (b[0] - lat) * ky;
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                double t = len2 > 0 ? Math.Max(0, Math.Min(1, -(ax * dx + ay * dy) / len2)) : 0;
                double px = ax + t * dx, py = ay + t * dy, d = Math.Sqrt(px * px + py * py);
                double seg = Math.Sqrt(len2);
                if (d < best) { best = d; alongKm = run + t * seg; }
                run += seg;
            }
            return best;
        }

        /// <summary>Listings within <paramref name="corridorKm"/> of the route, in driving order.</summary>
        public static List<RouteStop> Along(IList<double[]> points, IEnumerable<OnlineRepeater> listings, double corridorKm)
        {
            var stops = new List<RouteStop>();
            foreach (var l in listings)
            {
                if (l.Location?.Lat == null || l.Location.Lon == null) continue;
                double off = DistanceTo(points, l.Location.Lat.Value, l.Location.Lon.Value, out double along);
                if (off <= corridorKm) stops.Add(new RouteStop { Listing = l, AlongKm = along, OffKm = off });
            }
            return stops.OrderBy(s => s.AlongKm).ToList();
        }

        /// <summary>The project's own repeaters within the corridor, in driving order.</summary>
        public static List<RouteStop> AlongProject(IList<double[]> points, Project p, double corridorKm)
        {
            var stops = new List<RouteStop>();
            foreach (var r in p.Repeaters.Where(r => r.Latitude.HasValue && r.Longitude.HasValue && !Presets.IsPreset(r)))
            {
                double off = DistanceTo(points, r.Latitude.Value, r.Longitude.Value, out double along);
                if (off <= corridorKm) stops.Add(new RouteStop { Repeater = r, AlongKm = along, OffKm = off });
            }
            return stops.OrderBy(s => s.AlongKm).ToList();
        }

        /// <summary>States (or provinces) the route passes through, in driving order: what to download.</summary>
        public static List<GeoArea> StatesCrossed(GeoAtlas atlas, IList<double[]> points)
        {
            var list = new List<GeoArea>();
            double run = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) run += Distances.Km(points[i - 1][0], points[i - 1][1], points[i][0], points[i][1]);
                if (i > 0 && i < points.Count - 1 && run < 10) continue; // sample about every 10 km
                run = 0;
                var country = atlas.AreaAt(points[i][1], points[i][0], AreaLevel.Country);
                var state = country != null ? atlas.AreaAt(points[i][1], points[i][0], AreaLevel.State, country) : null;
                var a = state ?? country;
                if (a != null && !list.Contains(a)) list.Add(a);
            }
            return list;
        }

        /// <summary>Douglas-Peucker in degrees (a route of a few hundred points instead of thousands, for the project file).</summary>
        public static List<double[]> Simplify(IList<double[]> points, double tolerance = 0.005)
        {
            if (points.Count <= 2) return points.ToList();
            var keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            var stack = new Stack<Tuple<int, int>>();
            stack.Push(Tuple.Create(0, points.Count - 1));
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                double max = 0; int at = -1;
                for (int i = s.Item1 + 1; i < s.Item2; i++)
                {
                    double d = SegmentDistance(points[i], points[s.Item1], points[s.Item2]);
                    if (d > max) { max = d; at = i; }
                }
                if (at > 0 && max > tolerance) { keep[at] = true; stack.Push(Tuple.Create(s.Item1, at)); stack.Push(Tuple.Create(at, s.Item2)); }
            }
            return points.Where((p, i) => keep[i]).Select(p => new[] { Math.Round(p[0], 4), Math.Round(p[1], 4) }).ToList();
        }

        static double SegmentDistance(double[] p, double[] a, double[] b)
        {
            double dx = b[1] - a[1], dy = b[0] - a[0], len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((p[1] - a[1]) * dx + (p[0] - a[0]) * dy) / len2)) : 0;
            double x = a[1] + t * dx - p[1], y = a[0] + t * dy - p[0];
            return Math.Sqrt(x * x + y * y);
        }

        /// <summary>"Brownwood to Abilene on US 183, 145 mi".</summary>
        public static string Describe(RoutePlan plan, string from, string to, bool miles)
        {
            string len = miles ? Math.Round(plan.LengthKm / Distances.KmPerMile).ToString(CultureInfo.InvariantCulture) + " mi"
                               : Math.Round(plan.LengthKm).ToString(CultureInfo.InvariantCulture) + " km";
            return from + " to " + to + (plan.OnRoads ? (plan.Via.Count > 0 ? " on " + string.Join(", ", plan.Via.Take(4)) : " on the highways") : " (straight line: no highway joins them)") + ", " + len +
                   (plan.HighwayMissing ? " (that highway isn't named in the built-in map here, so the route follows any highway)" : "");
        }
    }
}

namespace CodeplugBuilder.Core
{
    /// <summary>Turns a route into a zone: the repeaters along it, in driving order (roadmap item 11).</summary>
    public static class RouteBuilder
    {
        /// <summary>
        /// Makes (or replaces) the route zone <paramref name="zoneName"/>: a Favorites zone whose members are every channel of
        /// <paramref name="repeaters"/> (in driving order), with the route kept for GPS zone switching. Returns the zone.
        /// </summary>
        public static ZoneInfo Apply(Project p, string zoneName, IList<double[]> route, double corridorKm, IEnumerable<Repeater> repeaters)
        {
            zoneName = Naming.Fit(zoneName, 16);
            var info = p.FindZone(zoneName);
            if (info == null) p.Zones.Add(info = new ZoneInfo(zoneName));
            info.Kind = ZoneKinds.Favorites;
            info.Members = repeaters.Distinct().SelectMany(Project.ChannelsOf).Select(p.MemberFor).ToList();
            if (info.Members.Count == 0) info.Members = null;
            info.RoutePoints = RoutePlanner.Simplify(route).SelectMany(x => x).ToList();
            info.RouteCorridorKm = corridorKm;
            p.SyncZones();
            return info;
        }
    }
}
