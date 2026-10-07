using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>Developer aids: <c>--map</c> opens the map on its own; <c>--map-snapshot folder</c> draws a few map views to PNG files and quits.</summary>
    sealed class MapPreviewWindow : Window
    {
        readonly RegionPickerView picker = new RegionPickerView();

        public MapPreviewWindow(string snapshotFolder)
        {
            Title = "Map preview - " + Dialogs.AppName;
            Width = 1100;
            Height = 760;
            Content = new Border { Padding = new Thickness(10), Child = picker };
            picker.Map.SelectionChanged += (s, e) => picker.Status = picker.Map.Selected.Count + " picked: " + string.Join(", ", picker.Map.Selected.Select(a => a.Name));
            Opened += async (s, e) =>
            {
                await picker.LoadAtlasAsync();
                var atlas = picker.Map.Atlas;
                var tx = atlas.Find("US-TX");
                if (snapshotFolder == null)
                {
                    picker.Map.Dots = atlas.Places.Where(p => p.Country == "US" && p.Admin == "TX" && p.Weight == 2).Take(400)
                        .Select(p => new MapDot { Lon = p.Lon, Lat = p.Lat }).ToList();
                    picker.Map.ZoomToAreas(new[] { tx });
                    return;
                }
                Directory.CreateDirectory(snapshotFolder);
                async Task Shot(string name, Action setup)
                {
                    setup();
                    await Task.Delay(300);
                    var size = new PixelSize((int)Width, (int)Height);
                    using (var bmp = new RenderTargetBitmap(size)) { bmp.Render(this); bmp.Save(Path.Combine(snapshotFolder, name + ".png")); }
                }
                await Shot("1-world-states", () => { picker.Level = AreaLevel.State; picker.Map.ZoomWorld(); });
                await Shot("2-north-america-states", () =>
                {
                    picker.Map.SetSelected(new[] { tx, atlas.Find("US-OK"), atlas.Find("CA-ON") });
                    picker.Map.ZoomToAreas(new[] { atlas.Find("US-WA"), atlas.Find("US-FL"), atlas.Find("US-ME"), atlas.Find("US-CA") });
                });
                await Shot("3-texas-counties", () =>
                {
                    picker.Level = AreaLevel.County;
                    picker.Map.CanPick = a => a.Parent == tx;
                    var tomGreen = atlas.Counties.First(c => c.Name == "Tom Green County");
                    picker.Map.SetSelected(new[] { tomGreen, atlas.Counties.First(c => c.Name == "Harris County" && c.Parent == tx) });
                    picker.Map.Dots = atlas.Places.Where(p => p.Admin == "TX" && p.Weight == 2).Take(300).Select(p => new MapDot { Lon = p.Lon, Lat = p.Lat, Highlight = tomGreen.Contains(p.Lon, p.Lat) }).ToList();
                    picker.Map.Badge = a => a == tomGreen ? "3 repeaters" : null;
                    picker.Map.ZoomToAreas(new[] { tx });
                });
                await Shot("4-central-texas-highways", () => { picker.Map.CanPick = null; picker.Map.Dots = new System.Collections.Generic.List<MapDot>(); picker.Map.Badge = null; picker.Map.SetSelected(new GeoArea[0]); picker.Map.ZoomTo(-101.5, 29.0, -96.0, 33.0); });
                Close();
            };
        }
    }
}

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// <c>--addmap-check out.png</c>: opens Add from map on an empty project (region from the settings, set it with
    /// CODEPLUGBUILDER_SETTINGS), waits for the real RadioID/BrandMeister download, picks Tom Green County, saves a picture, logs.
    /// </summary>
    static class AddMapCheck
    {
        public static void Run(Avalonia.Controls.Window host, string png)
        {
            host.Opened += async (s, e) =>
            {
                var log = new System.Collections.Generic.List<string>();
                try
                {
                    var session = new CodeplugBuilder.App.Session();
                    var w = new AddFromMapWindow(session);
                    w.Show();
                    var started = DateTime.Now;
                    while ((w.Download == null || !w.Download.Done) && (DateTime.Now - started).TotalSeconds < 180) await Task.Delay(500);
                    log.Add("download done after " + (int)(DateTime.Now - started).TotalSeconds + " s: " + w.Download?.Status + " errors: " + string.Join("; ", w.Download?.Errors ?? new System.Collections.Generic.List<string>()));
                    var all = w.Download?.Repeaters ?? new System.Collections.Generic.List<OnlineRepeater>();
                    log.Add("repeaters: " + all.Count + ", from BrandMeister only: " + all.Count(r => r.Details.StartsWith("BrandMeister ID")));
                    var atlas = GeoAtlas.BuiltIn();
                    var county = atlas.Counties.First(c => c.Name == "Tom Green County");
                    w.Chooser.Picker.Map.ClickArea(county);
                    await Task.Delay(500);
                    log.Add("picked: " + w.Chooser.Picked().Count);
                    var size = new PixelSize((int)w.Width, (int)w.Height);
                    using (var bmp = new RenderTargetBitmap(size)) { bmp.Render(w); bmp.Save(png); }
                    w.Close();
                }
                catch (Exception ex) { log.Add("ERROR " + ex); }
                File.WriteAllLines(Path.ChangeExtension(png, ".log"), log);
                host.Close();
            };
        }
    }
}
