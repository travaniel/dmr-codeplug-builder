using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CodeplugBuilder.App;

namespace CodeplugBuilder.Mac
{
    /// <summary>The Avalonia application: theme, then the main window (a project file on the command line is opened).</summary>
    sealed class MacApp : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Default; // follow the system's light/dark setting
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://CodeplugBuilderMac/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                string path = null, snapshot = null;
                int tab = 0;
                var args = desktop.Args ?? new string[0];
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--snapshot" && i + 1 < args.Length) snapshot = Path.GetFullPath(args[++i]);
                    else if (args[i] == "--tab" && i + 1 < args.Length) int.TryParse(args[++i], out tab);
                    else if (File.Exists(args[i])) path = Path.GetFullPath(args[i]);
                }
                var window = new MainWindow(path);
                desktop.MainWindow = window;
                if (snapshot != null) window.SnapshotThenExit(snapshot, tab); // dev check: draw the window to a PNG and quit
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
