using System.IO;
using System.Windows;
using System.Windows.Threading;
using Shopee.Modules.MultiBrave;

namespace Shopee.Suite;

public partial class App : Application
{
    private static readonly string CrashLog =
        Path.Combine(Path.GetTempPath(), "shopeesuite-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            TryLog("AppDomain", args.ExceptionObject as Exception);

        // Engine scrape v31 + update-product cần session/port block khởi tạo trước khi mở Brave.
        try { MultiBraveRuntime.Initialize(); } catch (Exception ex) { TryLog("MultiBraveRuntime.Init", ex); }
        try { Shopee.Modules.UpdateProduct.UpdateProductRuntime.Initialize(); } catch (Exception ex) { TryLog("UpdateProductRuntime.Init", ex); }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog("Dispatcher", e.Exception);
        // Lỗi teardown LÀNH TÍNH khi dừng/hủy (tác vụ async sót lại dùng token/CTS đã hủy) → chỉ log,
        // KHÔNG popup (tránh "The CancellationTokenSource has been disposed" làm phiền sau khi bấm Dừng).
        if (IsBenignTeardown(e.Exception)) { e.Handled = true; return; }
        // Không cho app tắt vì 1 lỗi UI — báo lỗi rồi tiếp tục.
        Dialogs.Show(e.Exception.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static bool IsBenignTeardown(Exception? ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is ObjectDisposedException or OperationCanceledException)
                return true;
            if ((cur.Message ?? "").Contains("CancellationTokenSource has been disposed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void TryLog(string source, Exception? ex)
    {
        try { File.AppendAllText(CrashLog, $"=== {source} @ {DateTimeOffset.Now:o} ===\n{ex}\n\n"); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { MultiBraveRuntime.Cleanup(); } catch { }
        base.OnExit(e);
    }
}
