using System.IO;
using System.Windows;
using System.Windows.Threading;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;
using Shopee.Suite.ViewModels;

namespace Shopee.Suite;

public partial class App : Application
{
    private static readonly string CrashLog =
        Path.Combine(Path.GetTempPath(), "shopeesuite-crash.log");

    /// <summary>Chế độ hiện tại có nhóm Workspace không (quyết định có cleanup MultiBrave lúc đóng).</summary>
    private bool _showWorkspace;

    /// <summary>Hook đóng app chạy ĐÚNG MỘT LẦN (Closing của cửa sổ chính, Exit và SessionEnding đều gọi).</summary>
    private int _shutdownHooksRan;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HookBindingTrace();

        // Lưới đỡ lỗi UI: DispatcherUnhandledException (WPF) + callback UiThread + AppDomain + Task.
        UiThread.OnError = HandleUiCallbackException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => TryLog("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { TryLog("UnobservedTask", args.Exception); args.SetObserved(); };

        // Chế độ ứng dụng: chỉ init engine WORKSPACE (Brave/scrape/update) khi chế độ có Workspace. Đọc 1 lần
        // (đổi chế độ = restart nên không đổi giữa vòng đời). Auto-update + điều phối vẫn chạy MỌI chế độ (dưới).
        var appMode = Shopee.Core.Infrastructure.AppModeStore.Shared.Current;
        bool showWorkspace = Shopee.Core.Infrastructure.AppModeStore.ShowsWorkspace(appMode);
        _showWorkspace = showWorkspace;

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

            // Cho TryPublish (đẩy ledger nền) HẾT CÂM: lỗi POST ledger giờ báo lên tab Log Hub (throttle 1 dòng/60s
            // ở HttpCoordinationHub) → nếu Thống kê thiếu dòng vì mạng/hub, ta THẤY thay vì đoán mò. Gán 1 lần lúc
            // boot. Chỉ phục vụ coordination + đẩy BigSeller (workspace) nên gate cùng engine workspace.
            try { Shopee.Core.Coordination.HttpCoordinationHub.DiagLog = Shopee.Core.Coordination.HubLog.Warn; }
            catch (Exception ex) { TryLog("Coordination.DiagLog", ex); }
        }

        // Điều phối phía CLIENT theo SUẤT LÀM VIỆC (xem MachineSlots): chế độ có Workspace → suất workspace (khoá
        // việc, account-lease, nhận việc Hub giao) mang ĐÚNG machine_id cũ; chế độ có Shopee → thêm suất
        // "<id-máy>:orders" chỉ heartbeat (đăng ký máy + nhận lệnh update app), KHÔNG claim việc/lease BigSeller
        // nên hai bản chạy song song trên một PC không tranh nhau. Chưa cấu hình Hub → NoOp (app chạy như cũ).
        // ĐẶT SAU khối trên: heartbeat mang MaxBrave nên phải đợi BraveFleet cấu hình xong.
        try { Shopee.Core.Coordination.CoordinationRuntime.InitForCurrentMode(); }
        catch (Exception ex) { TryLog("Coordination.Init", ex); }

        try
        {
            Shopee.Core.Infrastructure.StartupJanitor.Notice = msg => TryLog("StartupJanitor", new Exception(msg));
            Shopee.Core.Infrastructure.StartupJanitor.RunInBackground();
        }
        catch (Exception ex) { TryLog("StartupJanitor.Init", ex); }

        // Tự cập nhật (Velopack): kiểm tra + TẢI nền bản mới. KHÔNG tự khởi động lại — chỉ báo sẵn để
        // người dùng bấm cập nhật khi rảnh (khỏi cắt job). No-op nếu chạy từ dev/bin (chưa cài qua Velopack).
        try { _ = Services.UpdateService.Shared.CheckAsync(); }
        catch (Exception ex) { TryLog("UpdateService.Check", ex); }

        var window = new MainWindow { DataContext = new ShellViewModel() };
        MainWindow = window;
        // Module đơn hàng dùng cửa sổ shell làm owner cho hộp thoại/chọn thư mục (ConfirmDialog…).
        XuLyDonShopee.App.Services.DialogService.MainWindow = window;

        // Hook đóng app: WPF không có một event "sắp thoát" duy nhất → gắn cả 3 đường (đóng cửa sổ chính,
        // Application.Shutdown() từ AppRestart/Velopack, và Windows log-off/shutdown). RunShutdownHooks tự
        // chống chạy lặp. Closing (không phải Closed) để còn kịp block trước khi cửa sổ biến mất.
        window.Closing += (_, _) => RunShutdownHooks();
        Exit += (_, _) => RunShutdownHooks();
        SessionEnding += (_, _) => RunShutdownHooks();

        window.Show();
    }

    /// <summary>
    /// Dừng êm: flush ghi đĩa hoãn + dừng module đơn hàng + cleanup engine Brave. Chạy đúng một lần dù
    /// được gọi từ nhiều đường (Closing / Exit / SessionEnding).
    /// </summary>
    private void RunShutdownHooks()
    {
        if (Interlocked.Exchange(ref _shutdownHooksRan, 1) != 0) return;

        // Flush ghi đĩa hoãn (PersistDebounce) để không mất sửa BigSeller cuối nếu đóng nhanh.
        try { Shopee.Core.BigSeller.BigSellerStore.Shared.Save(); } catch { }
        // Module đơn hàng: dừng vòng "Chạy tự động" + kill hết phiên Brave (block như app gốc, tránh Brave mồ côi).
        try { OrdersModuleHost.StopAsync().GetAwaiter().GetResult(); } catch { }
        // Chỉ cleanup MultiBrave khi đã init (chế độ có Workspace) — chế độ Shopee không đụng engine này.
        if (_showWorkspace) { try { MultiBraveRuntime.Cleanup(); } catch { } }
    }

    /// <summary>
    /// Ghi LỖI BINDING ("System.Windows.Data Error…") ra file khi biến môi trường
    /// <c>SHOPEESUITE_BINDING_LOG</c> trỏ tới đường dẫn file. Mặc định TẮT (không có biến → không tốn gì).
    /// Cần vì WPF chỉ đẩy các lỗi này vào cửa sổ Output của debugger — chạy thường thì không thấy gì, mà
    /// binding sai lại là kiểu lỗi IM LẶNG hay gặp nhất khi dựng lại view (mỗi đợt port đều phải soát).
    /// </summary>
    private static void HookBindingTrace()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("SHOPEESUITE_BINDING_LOG");
            if (string.IsNullOrWhiteSpace(path)) return;

            System.Diagnostics.PresentationTraceSources.Refresh();
            var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(new TextWriterTraceListener(path) { TraceOutputOptions = TraceOptions.None });
            source.Switch.Level = SourceLevels.Warning;
            Trace.AutoFlush = true;
        }
        catch (Exception ex) { TryLog("BindingTrace.Hook", ex); }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleUiCallbackException(e.Exception);
        e.Handled = true;
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
