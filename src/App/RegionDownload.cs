using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Downloads every RadioID.net repeater in a set of map areas in the background and places each one on the
    /// map. US states are fetched one by one (RadioID's state names match the map's); anywhere else the whole
    /// country is fetched, because state names there are free text. Results are kept for the rest of the run,
    /// so Repeaters > Add from map doesn't download a region twice.
    /// </summary>
    sealed class RegionDownload
    {
        static readonly Dictionary<string, List<OnlineRepeater>> cache = new Dictionary<string, List<OnlineRepeater>>(StringComparer.OrdinalIgnoreCase);
        static readonly object cacheLock = new object();

        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly SynchronizationContext ui;
        readonly List<OnlineRepeater> repeaters = new List<OnlineRepeater>();

        public IReadOnlyList<GeoArea> Areas { get; }
        public GeoAtlas Atlas { get; private set; }
        public Dictionary<int, string> BrandMeisterNames { get; private set; } = new Dictionary<int, string>();
        /// <summary>Names for talkgroups BrandMeister doesn't list (<see cref="Online.NameTalkgroups"/>).</summary>
        public Dictionary<int, string> TalkgroupNames { get; private set; } = new Dictionary<int, string>();
        public List<string> Errors { get; } = new List<string>();
        public bool Done { get; private set; }
        public string Status { get; private set; } = "Starting the download...";

        /// <summary>Raised on the UI thread whenever <see cref="Status"/> changes and when the download finishes.</summary>
        public event EventHandler Changed;

        public RegionDownload(IEnumerable<GeoArea> areas)
        {
            Areas = areas.ToList();
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        /// <summary>Everything downloaded so far (all of it once <see cref="Done"/>).</summary>
        public List<OnlineRepeater> Repeaters
        {
            get { lock (repeaters) return repeaters.ToList(); }
        }

        /// <summary>What a list of areas means as RadioID.net queries: ("state", "Texas") or ("country", country area).</summary>
        public static List<KeyValuePair<string, GeoArea>> Queries(IEnumerable<GeoArea> areas)
        {
            var list = new List<KeyValuePair<string, GeoArea>>();
            foreach (var a in areas)
            {
                GeoArea target;
                string kind;
                if (a.Level == AreaLevel.County) { target = a.Parent; kind = "state"; }
                else if (a.Level == AreaLevel.State && a.CountryCode == "US") { target = a; kind = "state"; }
                else { target = a.Level == AreaLevel.Country ? a : a.Parent; kind = "country"; }
                if (target == null) continue;
                if (kind == "state" && list.Any(q => q.Key == "country" && q.Value.Code == "US")) continue;
                if (kind == "country" && target.Code == "US") list.RemoveAll(q => q.Key == "state");
                if (!list.Any(q => q.Key == kind && q.Value == target)) list.Add(new KeyValuePair<string, GeoArea>(kind, target));
            }
            return list;
        }

        /// <summary>A short description for the UI: "Texas and Oklahoma", "Germany", "3 places".</summary>
        public static string Describe(IEnumerable<GeoArea> areas)
        {
            var names = areas.Select(a => a.Name).ToList();
            if (names.Count == 0) return "nothing";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return names[0] + " and " + names[1];
            if (names.Count <= 4) return string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
            return names.Count + " places";
        }

        public void Start()
        {
            Task.Run(() => Run(cts.Token));
        }

        public void Cancel() { cts.Cancel(); }

        void Report(string status)
        {
            Status = status;
            ui.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
        }

        void Run(CancellationToken token)
        {
            try
            {
                Atlas = GeoAtlas.BuiltIn();
                var bm = Online.BrandMeisterNamesAsync(); // in parallel with the repeaters
                var positions = Online.RepeaterPositionsAsync(); // the DMR-MARC map: exact places
                var queries = Queries(Areas);
                int n = 0;
                foreach (var q in queries)
                {
                    if (token.IsCancellationRequested) return;
                    n++;
                    string label = q.Value.Name + (queries.Count > 1 ? " (" + n + " of " + queries.Count + ")" : "");
                    try
                    {
                        var found = q.Key == "state" ? State(q.Value, label, token) : Country(q.Value, label, token);
                        foreach (var r in found)
                            if (r.Location == null) r.Location = Atlas.Locate(r.City, r.State, r.Country);
                        lock (repeaters)
                            foreach (var r in found)
                                if (!repeaters.Any(x => Same(x, r))) repeaters.Add(r);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        lock (Errors) Errors.Add(q.Value.Name + ": " + ex.Message);
                    }
                }
                Report("Placing repeaters on the map (DMR-MARC positions)...");
                try
                {
                    List<OnlineRepeater> found;
                    lock (repeaters) found = repeaters.ToList();
                    RadioId.ApplyMapPositions(found, positions.Result, Atlas);
                }
                catch { }
                Report("Getting talkgroup names from BrandMeister...");
                try { BrandMeisterNames = bm.Result ?? new Dictionary<int, string>(); } catch { }
                List<OnlineRepeater> all;
                lock (repeaters) all = repeaters.ToList();
                try { TalkgroupNames = Online.NameTalkgroups(all, BrandMeisterNames, new StatusProgress(Report), token); } catch { }
            }
            catch (Exception ex)
            {
                lock (Errors) Errors.Add(ex.Message);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    Done = true;
                    int count;
                    lock (repeaters) count = repeaters.Count;
                    Report(Errors.Count > 0 && count == 0 ? "The download didn't work." : "Downloaded " + count + " repeaters.");
                }
            }
        }

        /// <summary>Reports straight away, in order (Progress&lt;T&gt; on a worker thread can deliver after the final "Downloaded" status).</summary>
        sealed class StatusProgress : IProgress<string>
        {
            readonly Action<string> report;
            public StatusProgress(Action<string> report) { this.report = report; }
            public void Report(string value) { report(value); }
        }

        static bool Same(OnlineRepeater a, OnlineRepeater b)
        {
            return a.Callsign == b.Callsign && a.RxMHz == b.RxMHz && a.ColorCode == b.ColorCode && a.DmrId == b.DmrId;
        }

        List<OnlineRepeater> State(GeoArea state, string label, CancellationToken token)
        {
            return Cached("state=" + state.Name, label, page => RadioId.RepeaterUrl(state.Name, page), token);
        }

        /// <summary>RadioID's country names don't always match the map's ("United States" vs "United States of America"), so try a few.</summary>
        List<OnlineRepeater> Country(GeoArea country, string label, CancellationToken token)
        {
            var names = new List<string>();
            if (country.Code == "US") names.Add("United States");
            if (country.Code == "GB") names.Add("United Kingdom");
            names.Add(country.Name);
            names.AddRange(country.Aliases.Where(a => a.Length > 3 && !a.Contains(",")));
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
            {
                var found = Cached("country=" + name, label, page => RadioId.CountryRepeaterUrl(name, page), token);
                if (found.Count > 0) return found;
            }
            return new List<OnlineRepeater>();
        }

        List<OnlineRepeater> Cached(string key, string label, Func<int, string> url, CancellationToken token)
        {
            lock (cacheLock)
                if (cache.TryGetValue(key, out var hit)) return hit;
            var all = new List<OnlineRepeater>();
            int pages = 1;
            for (int page = 1; page <= pages && page <= 60; page++)
            {
                token.ThrowIfCancellationRequested();
                Report("Downloading " + label + " from RadioID.net" + (pages > 1 ? ", page " + page + " of " + pages : "") + "...");
                var p = RadioId.ParseRepeaters(Online.Get(url(page)));
                pages = Math.Max(1, p.Pages);
                all.AddRange(p.Repeaters);
            }
            lock (cacheLock) cache[key] = all;
            return all;
        }
    }
}
