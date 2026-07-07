using System;
using Avalonia;
using Velopack;

namespace Shopee.Suite;

internal static class Program
{
    // Avalonia yêu cầu [STAThread] cho desktop trên Windows.
    [STAThread]
    public static void Main(string[] args)
    {
        // PHẢI là dòng ĐẦU TIÊN, trước Avalonia. Velopack chặn các lần chạy "hook" (cài/gỡ/first-run/
        // khởi động-lại-sau-update) rồi tự thoát — bỏ dòng này thì cập nhật "im lặng không chạy".
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Dùng bởi Avalonia designer + Main.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
