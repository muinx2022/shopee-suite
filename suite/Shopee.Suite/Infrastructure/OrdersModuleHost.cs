using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shopee.Core.Browser;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Services;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Glue tĩnh cắm module "Xử lý đơn Shopee" (app đơn hàng đã module hóa — phase 1b) vào shell suite.
/// <see cref="TryCreate"/> mở SQLite + migration của app đơn hàng và dựng <see cref="MainViewModel"/>;
/// <see cref="StopAsync"/> kill hết phiên Brave khi thoát app.
/// Giữ tối thiểu để nếu init hỏng thì suite vẫn chạy (chỉ thiếu module đơn hàng).
/// <para>Chia làm 6 file cùng class (partial): file này = VÒNG ĐỜI (dựng dịch vụ, thứ tự rót hook, thoát app);
/// <c>.HubPush.cs</c> = đẩy đơn/phiếu/+1-đã-bán lên hub; <c>.HubRead.cs</c> = đọc thống kê/số chuẩn-bị-hàng/danh
/// bạ từ hub; <c>.AccountLease.cs</c> = khóa tài khoản xuyên máy; <c>.GsheetConfig.cs</c> = cấu hình GSheet dùng
/// chung; <c>.Mirror.cs</c> = gương danh bạ + lệnh hub giao.</para>
/// </summary>
public static partial class OrdersModuleHost
{
    /// <summary>Bộ dịch vụ của app đơn hàng (DB + repository + phiên). null nếu chưa/không khởi tạo được.</summary>
    public static AppServices? Services { get; private set; }

    // Chống dừng đúp: ShutdownRequested và UpdateService.PrepareShutdownAsync đều gọi StopAsync. Lệnh dừng
    // bên dưới vốn idempotent (StopAllAsync thao tác list rỗng), cờ này chỉ để khỏi lặp công vô ích.
    private static bool _stopped;

    /// <summary>VÒNG CHỜ ĐẨY chạy theo vòng đời APP (không theo phiên) — đẩy bù hàng tồn lên Hub/GSheet/đếm
    /// "Đã bán" mỗi ~2 phút. Giữ tham chiếu static để Dispose khi thoát (và để GC không gom).</summary>
    private static HubOutboxWorker? _outboxWorker;

    /// <summary>ViewModel gốc của module (shell giữ để hiển thị). Giữ thêm tham chiếu ở đây để dọn timer nền
    /// của các màn con khi thoát app (xem <see cref="StopAsync"/>).</summary>
    private static MainViewModel? _mainVm;

    /// <summary>
    /// Khởi tạo bộ dịch vụ đơn hàng (ctor <see cref="AppServices"/> mở SQLite <c>%APPDATA%\XuLyDonShopee\app.db</c>
    /// + chạy migration) và dựng ViewModel gốc của module. Lỗi (đĩa/khóa DB…) → ghi log, trả null để suite vẫn boot.
    /// </summary>
    public static MainViewModel? TryCreate()
    {
        try
        {
            Services = new AppServices();
            WireBrowserLifetime(Services);
            WireHubPush(Services);
            WireIncrementSoldBySku(Services);
            WireHubSlipPush(Services);
            WireGsheetConfig(Services);
            WireOrderStatisticsRead(Services);
            WirePrepareStatsRead(Services);
            WireAccountLease(Services);
            WireOrdersDirectory(Services);
            WireOrdersMirror(Services);
            var vm = new MainViewModel(Services);
            _mainVm = vm;
            // Vòng chờ đẩy: dựng SAU khi AppServices (DB + migration) và các hook hub đã sẵn sàng; tự hoãn ~15s
            // rồi chạy lượt đầu (bắt đúng ý "khi client chạy, còn vòng chờ thì đẩy") và lặp mỗi ~2 phút.
            _outboxWorker = new HubOutboxWorker(Services);
            _outboxWorker.Start();
            return vm;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] Không khởi tạo được module đơn hàng: " + ex);
            Services = null;
            return null;
        }
    }

    /// <summary>
    /// RÓT hook phóng trình duyệt + đăng ký thư mục profiles đơn hàng vào <see cref="BraveFleet"/>:
    /// (1) mọi Brave/Chrome/Edge do module mở vào Job Object → chết theo app khi force-kill;
    /// (2) StartupSweep dọn mồ côi sót từ lần chạy trước (kể cả chế độ chỉ Shopee, không có Workspace).
    /// Module Đơn hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên shell suite làm cầu nối.
    /// </summary>
    private static void WireBrowserLifetime(AppServices services)
    {
        BrowserProcessStarter.Start = (exe, args) =>
            BraveJobObject.Start(exe, BrowserProcessStarter.JoinArguments(args), startMinimized: false);

        try
        {
            var baseDir = Path.GetDirectoryName(services.Database.Path);
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                var profilesRoot = Path.Combine(baseDir, "profiles");
                // CHỈ QUÉT LÚC KHỞI ĐỘNG. Module Đơn hàng không gọi BraveFleet.RegisterActiveProfile (chỉ
                // MultiBrave/UpdateProduct gọi) ⇒ với nhịp dọn định kỳ, trình duyệt ĐANG chạy vòng shop của
                // Đơn hàng trông y hệt mồ côi và sẽ bị giết cả cây. Rác của lần chạy trước vẫn được dọn ở
                // StartupSweep ngay dưới; trong lúc chạy đã có lưới riêng của module (BrowserProfileGuard
                // trước khi phóng, kill theo --user-data-dir khi đóng phiên, Job Object khi app chết).
                BraveFleet.AddManagedRoot(profilesRoot, chiQuetLucKhoiDong: true);
            }

            var swept = BraveFleet.StartupSweep();
            if (swept > 0)
                Trace.WriteLine($"[OrdersModuleHost] StartupSweep: đã dọn {swept} trình duyệt mồ côi (profiles đơn hàng / persistent-data).");
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] WireBrowserLifetime/sweep lỗi (bỏ qua): " + ex.Message);
        }
    }

    /// <summary>
    /// Thoát app: dừng TẤT CẢ phiên (kill hết Brave, tránh tiến trình mồ côi giữ khóa hồ sơ). No-op khi module
    /// không khởi tạo được.
    /// </summary>
    public static async Task StopAsync()
    {
        var svc = Services;
        if (svc is null || _stopped) return;
        _stopped = true;
        try { _gsheetTimer?.Dispose(); } catch { /* bỏ qua khi thoát */ }   // dừng nhịp kéo cấu hình GSheet
        try { _mirrorTimer?.Dispose(); } catch { /* bỏ qua khi thoát */ }   // dừng worker đẩy gương danh bạ
        try { _outboxWorker?.Dispose(); } catch { /* bỏ qua khi thoát */ }  // dừng vòng chờ đẩy
        try { _mainVm?.AccountsVm.Dispose(); } catch { /* bỏ qua khi thoát */ } // dừng nhịp dò sang ngày mới
        try { _mainVm?.StatisticsVm.Dispose(); } catch { /* bỏ qua khi thoát */ } // nhịp sang ngày của màn Thống kê
        try { await svc.Sessions.StopAllAsync(); } catch { /* bỏ qua khi thoát */ }
        // Dispose SAU CÙNG: phiên đang dừng vẫn còn ghi log. ActivityLog gom dòng trong bộ đệm rồi mới xả ra file
        // theo nhịp — không Dispose là mất nốt phần _pending của phút cuối (đúng lúc cần soi nhất khi thoát bất thường).
        try { svc.Log.Dispose(); } catch { /* bỏ qua khi thoát */ }
    }
}
