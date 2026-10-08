using System;
using System.Collections.Generic;
using System.Linq;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>Everything the new-codeplug wizard collects; <see cref="Build"/> turns it into a project.</summary>
    sealed class WizardState
    {
        public List<GeoArea> Region = new List<GeoArea>();
        public RegionDownload Download;
        public string Callsign = "", RadioIdName = "", Power = "High";
        public int RadioId;
        public bool Hotspot, Noaa, Simplex;

        /// <summary>The wizard's simplex checkbox (North America only).</summary>
        public const string SimplexChoice = "Add simplex channels: 146.520 and 446.000 FM calling, and 5 DMR simplex frequencies on talkgroup 99 (zone \"Simplex\")";
        public decimal HotspotRx, HotspotTx;
        public int HotspotCC = 1;
        public List<OnlineRepeater> Picked = new List<OnlineRepeater>();
        public ZoneScheme Scheme = ZoneScheme.County;
        public string SingleZone = "DMR";
        public Project Project;
        public List<string> Notes = new List<string>();
        string builtFrom;

        /// <summary>Raised (on the UI thread) when a download starts, progresses or finishes.</summary>
        public event EventHandler DownloadChanged;

        public void StartDownload(List<GeoArea> region)
        {
            // Same region and not failed: keep it (going Back and Next again shouldn't download twice).
            if (Download != null && Download.Areas.SequenceEqual(region) && !(Download.Done && Download.Errors.Count > 0)) return;
            StopDownload();
            Region = region;
            Download = new RegionDownload(region);
            Download.Changed += OnDownload;
            Download.Start();
            OnDownload(Download, EventArgs.Empty);
        }

        /// <summary>Stops listening to (and cancels) the current download, if any.</summary>
        public void StopDownload()
        {
            if (Download == null) return;
            Download.Changed -= OnDownload;
            Download.Cancel();
        }

        void OnDownload(object sender, EventArgs e) { DownloadChanged?.Invoke(this, EventArgs.Empty); }

        string BuildKey(List<OnlineRepeater> picked)
        {
            return string.Join(",", picked.Select(r => r.Callsign + r.RxMHz + r.ColorCode + r.DmrId)) + "|" + RadioIdName + "|" + RadioId + "|" +
                   Power + "|" + Hotspot + HotspotRx + HotspotTx + HotspotCC + "|" + Noaa + "|" + Simplex;
        }

        /// <summary>True when <see cref="Build"/> with these picks would start the project over (losing zone and talkgroup edits).</summary>
        public bool WouldRebuild(List<OnlineRepeater> picked)
        {
            return Project != null && BuildKey(picked) != builtFrom;
        }

        /// <summary>True when the user has already made zone talkgroup choices a rebuild would throw away.</summary>
        public bool HasZoneWork => Project != null && Project.Zones.Any(z => z.HasTalkgroups);

        /// <summary>Makes the project from the picked repeaters (again, if anything that goes into it changed).</summary>
        public void Build()
        {
            string key = BuildKey(Picked);
            if (Project != null && key == builtFrom) return;
            builtFrom = key;

            bool usOnly = Picked.Count > 0 && Picked.All(r => r.Location?.Country?.Code == "US");
            Scheme = usOnly ? ZoneScheme.County : Picked.Count <= 12 ? ZoneScheme.Single : ZoneScheme.State;
            SingleZone = Naming.Fit(RegionDownload.Describe(Region), 16); // shortened, not cut: "Texas and Oklahoma" → "Texas and OK"
            if (SingleZone.Length == 0 || Region.Count > 2) SingleZone = "DMR";

            var p = new Project { RadioId = RadioId, RadioIdName = RadioIdName };
            p.HotspotEnabled = Hotspot;
            if (Hotspot)
            {
                p.Hotspot.RxMHz = HotspotRx;
                p.Hotspot.TxMHz = HotspotTx > 0 ? HotspotTx : HotspotRx;
                p.Hotspot.ColorCode = HotspotCC;
            }
            var o = new OnlineImportOptions { Power = Power, ZoneFor = r => ZonePlanner.ZoneName(r, Scheme, SingleZone), Scheme = Scheme, MoreNames = Download?.TalkgroupNames };
            var result = OnlineImporter.AddRepeaters(p, Picked, o, Download?.BrandMeisterNames);
            if (Simplex) Presets.AddSimplex(p);
            if (Noaa) Presets.AddNoaaWeather(p);
            SortZones(p);
            Notes = result.Notes;
            Project = p;
        }

        /// <summary>Hotspot first, then the rest by name, then simplex and weather.</summary>
        public static void SortZones(Project p)
        {
            p.SyncZones();
            var sorted = p.Zones.OrderBy(z => Project.SameZone(z.Name, p.Hotspot.Zone) ? 0 : Project.SameZone(z.Name, "Simplex") ? 2 : Project.SameZone(z.Name, "Weather") ? 3 : 1)
                                .ThenBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
            p.Zones.Clear();
            p.Zones.AddRange(sorted);
        }
    }
}
