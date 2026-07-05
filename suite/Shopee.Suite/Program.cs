using System;
using Avalonia;

namespace Shopee.Suite;

internal static class Program
{
    // Avalonia yêu cầu [STAThread] cho desktop trên Windows.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Dùng bởi Avalonia designer + Main.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
