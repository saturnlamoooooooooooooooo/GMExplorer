using System;
using Avalonia;

namespace GMExplorer
{
    internal class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--dump") { Gm.Dump.Run(args); return; }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }
}