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

        // Bảo vệ tài nguyên cho bầy Brave (chống "đơ máy" khi chạy dài qua đêm):
        //  1) Đặt trần CỨNG số tiến trình cho Job Object — PHẢI trước lần phóng Brave đầu (OS tự ép, kể
        //     cả khi app treo/chết). 2) Dọn Brave mồ côi sót lại từ lần chạy trước bị treo/crash.
        //  3) Bật vòng dọn nền (GC + trả working set + quét mồ côi) chạy trên luồng threadpool, không phụ
        //     thuộc UI nên UI có treo thì việc dọn vẫn chạy.
        try
        {
            // Nạp ngân sách CPU/RAM do người dùng đặt (Cài đặt → Hiệu năng) → tính trần cửa sổ rồi nạp vào
            // RAM. 0/0 = mặc định (nửa số nhân + toàn bộ RAM). Phải set TRƯỚC ConfigureJobLimits vì trần
            // tiến trình Job tính theo trần cửa sổ.
            var perf = Shopee.Core.Infrastructure.PerformanceSettingsStore.Shared.Current;
            Shopee.Core.Browser.BraveFleet.MaxConcurrentWindows =
                Shopee.Core.Browser.BraveFleet.WindowsForBudget(perf.UsableCpuCores, perf.UsableRamGb);
            Shopee.Core.Browser.BraveFleet.ConfigureJobLimits();
            var swept = Shopee.Core.Browser.BraveFleet.StartupSweep();
            if (swept > 0) TryLog("BraveFleet.StartupSweep", new Exception($"Đã dọn {swept} Brave mồ côi lúc khởi động."));
            Shopee.Core.Browser.BraveFleet.StartMaintenance();
        }
        catch (Exception ex) { TryLog("BraveFleet.Init", ex); }

        // Đa máy: nếu máy này được đặt làm Hub → tự bật mini-server + Cloudflare Tunnel (nền, không chặn UI).
        try
        {
            var hubCfg = Shopee.Core.Coordination.HubServerConfigStore.Shared.Current;
            if (hubCfg.Enabled)
                _ = Shopee.Hub.HubRuntime.Shared.StartAsync(hubCfg);
        }
        catch (Exception ex) { TryLog("Hub.Start", ex); }

        // Khởi tạo điều phối phía client (khoá việc, account-lease, ledger). Chưa cấu hình → để NoOp.
        try { Shopee.Core.Coordination.CoordinationRuntime.InitFromConfig(); }
        catch (Exception ex) { TryLog("Coordination.Init", ex); }
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
        try { Shopee.Hub.HubRuntime.Shared.StopBlocking(); } catch { }
        try { MultiBraveRuntime.Cleanup(); } catch { }
        base.OnExit(e);
    }
}
