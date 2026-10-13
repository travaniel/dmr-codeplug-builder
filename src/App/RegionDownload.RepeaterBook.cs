using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    // RepeaterBook in the map step (Windows only, like its client in Online.RepeaterBook.cs): listings fetched with the
    // user's own token join this download in memory only; they are never put in the run's cache.
    sealed partial class RegionDownload
    {
        /// <summary>The US states of some map areas (a county's state, a state), for RepeaterBook; a whole country gives none.</summary>
        public static List<string> UsStates(IEnumerable<GeoArea> areas)
        {
            return areas.Where(a => a.CountryCode == "US")
                        .Select(a => a.Level == AreaLevel.County ? a.Parent : a.Level == AreaLevel.State ? a : null)
                        .Where(a => a != null).Select(a => a.Name).Distinct()
                        .Where(s => BrandMeister.UsStates.Any(u => u.Key == s)).ToList();
        }

        partial void DownloadDone() { FetchRegionRepeaterBook(); }

        /// <summary>What happened with RepeaterBook last (progress, result or problem); null before anything was asked.</summary>
        public string RepeaterBookNote { get; private set; }
        public bool RepeaterBookBusy { get; private set; }

        /// <summary>
        /// RepeaterBook for the states picked on the first map, with the user's own token, as part of the download the user
        /// started: up to <see cref="RepeaterBookApi.MaxStatesPerAction"/> states (more, or a whole country, and the user clicks
        /// the ones they want on the next map instead). Call on the UI thread; does nothing without a token or when done.
        /// </summary>
        public async void FetchRegionRepeaterBook()
        {
            if (!Done || RepeaterBookBusy || !RepeaterBookToken.IsSet) return;
            var states = UsStates(Areas);
            if (states.Count == 0 || states.All(HasRepeaterBook)) return;
            if (states.Count > RepeaterBookApi.MaxStatesPerAction)
            {
                RepeaterBookNote = "RepeaterBook: you picked " + states.Count + " states; click the states or counties you want on the map to bring in theirs (up to " +
                                   RepeaterBookApi.MaxStatesPerAction + " at a time).";
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            await FetchRepeaterBookAsync(states);
        }

        /// <summary>
        /// Fetches RepeaterBook's DMR and analog listings for <paramref name="states"/> (not already fetched; at most
        /// <see cref="RepeaterBookApi.MaxStatesPerAction"/>) and adds them (<see cref="AddRepeaterBook"/>). One batch at a time;
        /// returns false when busy or there was nothing to do. A failure is not retried by itself. Call on the UI thread.
        /// </summary>
        public async Task<bool> FetchRepeaterBookAsync(IEnumerable<string> states)
        {
            var ask = states.Where(s => !HasRepeaterBook(s)).Take(RepeaterBookApi.MaxStatesPerAction).ToList();
            if (RepeaterBookBusy || ask.Count == 0 || !RepeaterBookToken.IsSet) return false;
            string blocked = Online.RepeaterBookBlocked();
            if (blocked != null) { RepeaterBookNote = "RepeaterBook: " + blocked; Changed?.Invoke(this, EventArgs.Empty); return false; }

            MarkRepeaterBook(ask, true);
            RepeaterBookBusy = true;
            RepeaterBookNote = "RepeaterBook: downloading " + string.Join(", ", ask) + "...";
            Changed?.Invoke(this, EventArgs.Empty);
            RepeaterBookDownload got;
            try
            {
                got = await Online.RepeaterBookStatesAsync(ask, new[] { RepeaterBookApi.DmrMode, RepeaterBookApi.AnalogMode },
                                                          new Progress<string>(s => { RepeaterBookNote = "RepeaterBook: " + s; Changed?.Invoke(this, EventArgs.Empty); }), cts.Token);
            }
            catch (Exception ex) { got = new RepeaterBookDownload { Failure = new RepeaterBookFailure { Message = ex.Message } }; }
            RepeaterBookBusy = false;
            var added = AddRepeaterBook(got.Listings);
            int fm = added.Count(r => r.IsAnalog);
            var loaded = new HashSet<string>(got.Listings.Select(r => r.State), StringComparer.OrdinalIgnoreCase);
            if (got.Failure != null)
            {
                MarkRepeaterBook(ask.Where(s => !loaded.Contains(s)), false); // the next click asks again (after any wait RepeaterBook asked for)
                RepeaterBookNote = "RepeaterBook: " + got.Failure.Message;
            }
            else
            {
                lock (repeaterBookStates) loaded.UnionWith(repeaterBookStates);
                int total, totalFm;
                lock (repeaters) { var rb = repeaters.Where(r => r.Source == RepeaterBookImport.Site).ToList(); total = rb.Count; totalFm = rb.Count(r => r.IsAnalog); }
                RepeaterBookNote = "RepeaterBook: " + string.Join(", ", loaded.OrderBy(s => s)) + ": " + totalFm + " analog and " + (total - totalFm) +
                                   " DMR repeaters RadioID.net doesn't list: in the map's counts and on the List tab. Clicking an area takes them too.";
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return got.Failure == null;
        }

        readonly HashSet<string> repeaterBookStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>US states whose RepeaterBook listings were added (or are being fetched).</summary>
        public bool HasRepeaterBook(string state) { lock (repeaterBookStates) return repeaterBookStates.Contains(state); }
        public void MarkRepeaterBook(IEnumerable<string> states, bool on)
        {
            lock (repeaterBookStates) foreach (var s in states) { if (on) repeaterBookStates.Add(s); else repeaterBookStates.Remove(s); }
        }

        /// <summary>
        /// Adds RepeaterBook listings (fetched with the user's token after a click on the map) to this download only: they live
        /// in memory with it, never in the run's cache. A DMR listing RadioID.net or BrandMeister already has (same callsign and
        /// output) is left out, since theirs carries talkgroups; same for an analog one listed twice. Returns the ones added.
        /// </summary>
        public List<OnlineRepeater> AddRepeaterBook(IEnumerable<OnlineRepeater> listings)
        {
            var added = new List<OnlineRepeater>();
            lock (repeaters)
                foreach (var r in listings)
                {
                    if (repeaters.Any(x => x.IsAnalog == r.IsAnalog && x.RxMHz == r.RxMHz && string.Equals(x.Callsign, r.Callsign, StringComparison.OrdinalIgnoreCase))) continue;
                    repeaters.Add(r);
                    added.Add(r);
                }
            return added;
        }
    }
}
