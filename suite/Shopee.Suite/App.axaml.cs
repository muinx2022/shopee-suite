using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;
using Shopee.Suite.ViewModels;

namespace Shopee.Suite;

public partial class App : Application
{
    private static readonly string CrashLog =
        Path.Combine(Path.GetTempPath(), "shopeesuite-crash.log");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Lưới đỡ lỗi UI (Avalonia không có DispatcherUnhandledException kiểu WPF): callback UiThread + AppDomain + Task.
        UiThread.OnError = HandleUiCallbackException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => TryLog("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { TryLog("UnobservedTask", args.Exception); args.SetObserved(); };

        // Chế độ ứng dụng: chỉ init engine WORKSPACE (Brave/scrape/update) khi chế độ có Workspace. Đọc 1 lần
        // (đổi chế độ = restart nên không đổi giữa vòng đời). Auto-update + điều phối vẫn chạy MỌI chế độ (dưới).
        var appMode = Shopee.Core.Infrastructure.AppModeStore.Shared.Current;
        bool showWorkspace = Shopee.Core.Infrastructure.AppModeStore.ShowsWorkspace(appMode);

        // ── Khởi tạo engine workspace (CHỈ chế độ có Workspace; mỗi hook đã try/catch nên boot được cả khi 1 phần lỗi) ──
        if (showWorkspace)
        {
            try { MultiBraveRuntime.Initialize(); } catch (Exception ex) { TryLog("MultiBraveRuntime.Init", ex); }
            try { Shopee.Modules.UpdateProduct.UpdateProductRuntime.Initialize(); } catch (Exception ex) { TryLog("UpdateProductRuntime.Init", ex); }

            try
            {
                var perf = Shopee.Core.Infrastructure.PerformanceSettingsStore.Shared.Current;
                Shopee.Core.Browser.BraveFleet.MaxConcurrentWindows =
                    Shopee.Core.Browser.BraveFleet.WindowsForBudget(perf.UsableCpuCores, perf.UsableRamGb);
                Shopee.Core.Browser.BraveFleet.ConfigureJobLimits();
                var swept = Shopee.Core.Browser.BraveFleet.StartupSweep();
                if (swept > 0) TryLog("BraveFleet.StartupSweep", new Exception($"Đã dọn {swept} Brave mồ côi lúc khởi động."));
                Shopee.Core.Browser.BraveFleet.StartMaintenance();
            }
            catch (Exception ex) { TryLog("BraveFleet.Init", ex); }
        }

        try
        {
            Shopee.Core.Infrastructure.StartupJanitor.Notice = msg => TryLog("StartupJanitor", new Exception(msg));
            Shopee.Core.Infrastructure.StartupJanitor.RunInBackground();
        }
        catch (Exception ex) { TryLog("StartupJanitor.Init", ex); }

        // Điều phối phía CLIENT (khoá việc, account-lease, nhận việc Hub giao). Chưa cấu hình → NoOp.
        try { Shopee.Core.Coordination.CoordinationRuntime.InitFromConfig(); }
        catch (Exception ex) { TryLog("Coordination.Init", ex); }

        // Cho TryPublish (đẩy ledger nền) HẾT CÂM: lỗi POST ledger giờ báo lên tab Log Hub (throttle 1 dòng/60s
        // ở HttpCoordinationHub) → nếu Thống kê thiếu dòng vì mạng/hub, ta THẤY thay vì đoán mò. Gán 1 lần lúc boot.
        try { Shopee.Core.Coordination.HttpCoordinationHub.DiagLog = Shopee.Core.Coordination.HubLog.Warn; }
        catch (Exception ex) { TryLog("Coordination.DiagLog", ex); }

        // Tự cập nhật (Velopack): kiểm tra + TẢI nền bản mới. KHÔNG tự khởi động lại — chỉ báo sẵn để
        // người dùng bấm cập nhật khi rảnh (khỏi cắt job). No-op nếu chạy từ dev/bin (chưa cài qua Velopack).
        try { _ = Services.UpdateService.Shared.CheckAsync(); }
        catch (Exception ex) { TryLog("UpdateService.Check", ex); }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel() };
            // Module đơn hàng dùng cửa sổ shell làm owner cho hộp thoại/StoragePicker (ConfirmDialog, chọn thư mục…).
            XuLyDonShopee.App.Services.DialogService.MainWindow = desktop.MainWindow;
            desktop.ShutdownRequested += (_, _) =>
            {
                // Flush ghi đĩa hoãn (PersistDebounce) để không mất sửa BigSeller cuối nếu đóng nhanh.
                try { Shopee.Core.BigSeller.BigSellerStore.Shared.Save(); } catch { }
                // Module đơn hàng: dừng vòng "Chạy tự động" + kill hết phiên Brave (block như app gốc, tránh Brave mồ côi).
                try { OrdersModuleHost.StopAsync().GetAwaiter().GetResult(); } catch { }
                // Chỉ cleanup MultiBrave khi đã init (chế độ có Workspace) — chế độ Shopee không đụng engine này.
                if (showWorkspace) { try { MultiBraveRuntime.Cleanup(); } catch { } }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void HandleUiCallbackException(Exception ex)
    {
        TryLog("UiThread", ex);
        // Lỗi teardown LÀNH TÍNH khi dừng/hủy (task async sót dùng token/CTS đã hủy) → chỉ log, KHÔNG popup.
        if (IsBenignTeardown(ex)) return;
        Dialogs.Notify(ex.Message, "Lỗi", DialogIcon.Error);
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
}
