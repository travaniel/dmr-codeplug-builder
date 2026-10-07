using System;
using Avalonia;

namespace CodeplugBuilder.Mac
{
    static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // --diagnose: print the diagnostics report to the terminal and quit (no window), e.g. from CI or Terminal.
            if (Array.IndexOf(args, "--diagnose") >= 0)
            {
                Console.WriteLine(Diagnostics.Report(Array.IndexOf(args, "--offline") < 0));
                return 0;
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<MacApp>().UsePlatformDetect().LogToTrace();
        }
    }
}
