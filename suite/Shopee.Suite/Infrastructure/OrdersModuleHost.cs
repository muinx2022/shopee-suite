using System;
using System.Diagnostics;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Glue tĩnh cắm module "Xử lý đơn Shopee" (app đơn hàng đã module hóa — phase 1b) vào shell suite.
/// <see cref="TryCreate"/> mở SQLite + migration của app đơn hàng và dựng <see cref="MainViewModel"/>;
/// <see cref="StopAsync"/> dừng vòng chạy tự động rồi kill hết phiên Brave khi thoát app.
/// Giữ tối thiểu để nếu init hỏng thì suite vẫn chạy (chỉ thiếu module đơn hàng).
/// </summary>
public static class OrdersModuleHost
{
    /// <summary>Bộ dịch vụ của app đơn hàng (DB + repository + phiên + scheduler). null nếu chưa/không khởi tạo được.</summary>
    public static AppServices? Services { get; private set; }

    // Chống dừng đúp: ShutdownRequested và UpdateService.PrepareShutdownAsync đều gọi StopAsync. Hai lệnh
    // dừng bên dưới vốn idempotent (AutoRun.StopAsync no-op khi chưa chạy; StopAllAsync thao tác list rỗng),
    // cờ này chỉ để khỏi lặp công vô ích.
    private static bool _stopped;

    /// <summary>
    /// Khởi tạo bộ dịch vụ đơn hàng (ctor <see cref="AppServices"/> mở SQLite <c>%APPDATA%\XuLyDonShopee\app.db</c>
    /// + chạy migration) và dựng ViewModel gốc của module. Lỗi (đĩa/khóa DB…) → ghi log, trả null để suite vẫn boot.
    /// </summary>
    public static MainViewModel? TryCreate()
    {
        try
        {
            Services = new AppServices();
            return new MainViewModel(Services);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] Không khởi tạo được module đơn hàng: " + ex);
            Services = null;
            return null;
        }
    }

    /// <summary>
    /// Thoát app: dừng vòng "Chạy tự động" TRƯỚC (không mở thêm phiên mới) rồi dừng TẤT CẢ phiên (kill hết Brave,
    /// tránh tiến trình mồ côi giữ khóa hồ sơ) — đúng thứ tự như app gốc. No-op khi module không khởi tạo được.
    /// </summary>
    public static async Task StopAsync()
    {
        var svc = Services;
        if (svc is null || _stopped) return;
        _stopped = true;
        try { await svc.AutoRun.StopAsync(); } catch { /* bỏ qua khi thoát */ }
        try { await svc.Sessions.StopAllAsync(); } catch { /* bỏ qua khi thoát */ }
    }
}
