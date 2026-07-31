using System;
using Velopack;

namespace Shopee.Suite;

internal static class Program
{
    // WPF yêu cầu [STAThread] cho luồng UI.
    [STAThread]
    public static void Main()
    {
        // PHẢI là dòng ĐẦU TIÊN, trước khi dựng app WPF. Velopack chặn các lần chạy "hook" (cài/gỡ/first-run/
        // khởi động-lại-sau-update) rồi tự thoát — bỏ dòng này thì cập nhật "im lặng không chạy".
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();   // nạp App.xaml (theme + icon + DataTemplate)
        app.Run();                   // MainWindow dựng trong App.OnStartup (không dùng StartupUri)
    }
}
