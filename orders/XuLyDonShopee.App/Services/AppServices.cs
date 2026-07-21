using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Gom Database + các repository, khởi tạo một lần và truyền vào ViewModel.
/// (Bước đầu không dùng DI container.)
/// </summary>
public class AppServices
{
    public Database Database { get; }
    public AccountRepository Accounts { get; }
    public ProxyRepository Proxies { get; }
    public SettingsRepository Settings { get; }

    /// <summary>Kho đơn hàng đã sync (bảng <c>orders</c>) — phiên ghi qua đây khi Sync Đơn hàng.</summary>
    public OrdersRepository Orders { get; }

    /// <summary>Đẩy đơn (kèm file phiếu) lên Google Sheet qua Apps Script Web App — phiên gọi sau mỗi lượt Sync.</summary>
    public GoogleSheetSyncService GsheetSync { get; }

    /// <summary>Helper CHUNG báo "đơn mới" tới Slack / Discord / Telegram — phiên gọi sau mỗi lượt Sync có đơn mới.</summary>
    public OrderNotifyService Notify { get; }

    /// <summary>
    /// HOOK đẩy một LÔ đơn đã sync của một tài khoản lên HUB đơn hàng, do shell suite RÓT (module Đơn hàng
    /// KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Tham số: <c>accountId</c>, lô đơn, <c>ct</c>;
    /// trả <c>true</c> = hub nhận OK → phiên đánh dấu <c>hub_synced_at</c> để không đẩy lại. Mặc định
    /// <c>null</c> = TẮT (app Đơn hàng chạy độc lập / hub chưa cấu hình → không đẩy, hành vi cũ y nguyên).
    /// Phiên gọi CHẠY NỀN sau mỗi lượt Sync (<see cref="AccountSession"/>).
    /// </summary>
    public Func<long, IReadOnlyList<SyncedOrder>, CancellationToken, Task<bool>>? PushOrdersToHub { get; set; }

    /// <summary>Nhật ký hoạt động của app (panel UI + ghi file cạnh database). Các phiên nạp log qua đây.</summary>
    public ActivityLog Log { get; }

    /// <summary>Quản lý các phiên mở trang bán hàng song song (mỗi tài khoản một phiên độc lập).
    /// App shutdown gọi <see cref="AccountSessionManager.StopAllAsync"/> để kill hết Brave.</summary>
    public AccountSessionManager Sessions { get; }

    /// <summary>Bộ "Chạy tự động theo lô" (vòng chạy nền lặp liên tục). App shutdown gọi
    /// <see cref="AutoRunService.StopAsync"/> để dừng vòng trước khi kill các phiên.</summary>
    public AutoRunService AutoRun { get; }

    /// <summary>
    /// Phát khi kho đơn (bảng <c>orders</c>) vừa được ghi thêm/cập nhật — phiên sync gọi
    /// <see cref="RaiseOrdersChanged"/> ngay sau <c>OrdersRepository.UpsertMany</c> để màn "Đơn hàng" đang mở
    /// tự nạp lại. CỐ Ý có thể bắn từ THREAD NỀN của phiên sync → người nghe (OrdersViewModel) PHẢI marshal về
    /// UI thread trước khi đụng ObservableCollection.
    /// </summary>
    public event Action? OrdersChanged;

    /// <summary>Phiên gọi sau khi UpsertMany đơn về DB THÀNH CÔNG để phát <see cref="OrdersChanged"/>.</summary>
    public void RaiseOrdersChanged() => OrdersChanged?.Invoke();

    public AppServices(string? dbPath = null)
    {
        Database = new Database(dbPath);
        Accounts = new AccountRepository(Database);
        Proxies = new ProxyRepository(Database);
        Settings = new SettingsRepository(Database);
        Orders = new OrdersRepository(Database);

        // Migration MỘT LẦN (idempotent qua cờ settings): gộp ProxyKey cố định của tài khoản (cơ chế cũ) vào
        // pool KiotProxy CHUNG để không mất key sẵn có khi chuyển sang cấp phát key theo pool lúc chạy.
        // Chạy sau khi có Accounts + Settings; các lần mở app sau bỏ qua nhờ cờ.
        ProxyKeyPoolMigration.EnsureMigrated(Accounts, Settings);
        GsheetSync = new GoogleSheetSyncService();
        Notify = new OrderNotifyService();
        // Log đặt TRƯỚC Sessions vì các phiên sẽ nạp log qua Log khi chạy. Thư mục logs cạnh file database.
        var logDir = Path.Combine(Path.GetDirectoryName(Database.Path) ?? ".", "logs");
        Directory.CreateDirectory(logDir);
        Log = new ActivityLog(logDir);
        // Tạo sau các repository vì factory phiên đọc Accounts/Proxies/Settings khi chạy.
        Sessions = new AccountSessionManager(this);
        // Scheduler đọc Accounts/Settings/Sessions/Log khi chạy → tạo sau các dịch vụ trên.
        AutoRun = new AutoRunService(this);
    }
}
