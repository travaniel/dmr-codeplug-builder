using System;
using Avalonia;

namespace CodeplugBuilder.Mac
{
    static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<MacApp>().UsePlatformDetect().LogToTrace();
        }
    }
}
