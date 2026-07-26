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
/// SỐ HÀNG CÒN TỒN trong vòng chờ đẩy, tách theo 4 loại đích: <paramref name="Orders"/> đơn lên Hub ·
/// <paramref name="Slips"/> file phiếu lên Hub · <paramref name="SheetRows"/> dòng Google Sheet ·
/// <paramref name="SoldCounts"/> lượt +1 "Đã bán". <see cref="HubOutboxWorker"/> tính lại sau mỗi lượt; thanh
/// trạng thái hiện <see cref="Tong"/> khi &gt; 0. Loại nào KHÔNG có đích (hook hub chưa rót / URL sheet trống)
/// được đếm 0 để badge không kẹt số vĩnh viễn.
/// </summary>
public readonly record struct OutboxPending(int Orders = 0, int Slips = 0, int SheetRows = 0, int SoldCounts = 0)
{
    /// <summary>Tổng hàng còn chờ đẩy (cả 4 loại).</summary>
    public int Tong => Orders + Slips + SheetRows + SoldCounts;

    /// <summary>Cộng dồn số tồn của một tài khoản nữa vào tổng của cả lượt.</summary>
    public OutboxPending Cong(OutboxPending khac)
        => new(Orders + khac.Orders, Slips + khac.Slips, SheetRows + khac.SheetRows, SoldCounts + khac.SoldCounts);
}

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

    /// <summary>Kho "Kết quả chuẩn bị hàng" theo shop/ngày (bảng <c>account_shops</c> + <c>prepare_daily</c>) —
    /// phiên cầu nối ghi qua callback (lưu shop + tăng đếm mỗi đơn arrange); tab "Kết quả" đọc để hiển thị.</summary>
    public ResultsRepository Results { get; }

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

    /// <summary>
    /// HOOK +1 "Đã bán" theo SKU (khớp TUYỆT ĐỐI, MỌI shop) trên kho sản phẩm HUB (Postgres), do shell suite RÓT
    /// (module Đơn hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Tham số: danh sách SKU — mỗi đơn
    /// vừa CHUYỂN sang "đã giao" đóng góp 1 SKU đại diện (đơn trùng SKU → +N); <c>ct</c>. Trả <c>true</c> = hub +1 OK
    /// → phiên đánh cờ <c>sold_counted_at</c> cho các đơn tương ứng. Mặc định <c>null</c> = TẮT (app Đơn hàng chạy
    /// độc lập / hub chưa cấu hình → không +1, đơn giữ CHƯA đánh cờ để lượt sync sau thử lại). Phiên gọi CHẠY NỀN
    /// sau mỗi lượt Sync (<see cref="AccountSession"/>).
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? IncrementSoldBySku { get; set; }

    /// <summary>
    /// HOOK đẩy một LÔ file phiếu PDF (base64) của một tài khoản lên HUB đơn hàng, do shell suite RÓT (module Đơn
    /// hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Tham số: <c>accountId</c>, lô
    /// <c>(OrderSn, FileBase64)</c> (≤5), <c>ct</c>. Trả DANH SÁCH <c>order_sn</c> hub ĐÃ LƯU thành công (đơn
    /// <c>missing</c>/lỗi KHÔNG có mặt) → phiên đánh cờ <c>hub_slip_synced_at</c> đúng các đơn đó; trả <c>null</c> =
    /// hub lỗi CẢ LÔ (offline / route chưa có) → không mark, lượt sync sau thử lại. Mặc định <c>null</c> = TẮT (app
    /// Đơn hàng chạy độc lập / hub chưa cấu hình). Phiên gọi CHẠY NỀN sau mỗi lượt Sync (<see cref="AccountSession"/>).
    /// </summary>
    public Func<long, IReadOnlyList<(string OrderSn, string FileBase64)>, CancellationToken, Task<IReadOnlyList<string>?>>? PushOrderSlipsToHub { get; set; }

    /// <summary>
    /// HOOK đẩy cấu hình ĐỒNG BỘ GOOGLE SHEET (URL Web App + tab override) của máy này lên HUB, do shell suite RÓT
    /// (module Đơn hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Tham số: <c>url</c>, <c>tab</c>
    /// ("" = tự động theo tháng), <c>ct</c>. Trả <c>true</c> = hub nhận OK → màn Cài đặt báo "đã đồng bộ lên Hub".
    /// Mặc định <c>null</c> = TẮT (app Đơn hàng chạy độc lập / hub chưa cấu hình → chỉ lưu local như cũ).
    /// Màn Cài đặt gọi sau khi LƯU local; hub CHỈ nhận field khác trống nên máy chưa cấu hình không xoá được
    /// cấu hình của cả fleet.
    /// </summary>
    public Func<string, string, CancellationToken, Task<bool>>? PushGsheetConfigToHub { get; set; }

    /// <summary>Nhật ký hoạt động của app (panel UI + ghi file cạnh database). Các phiên nạp log qua đây.</summary>
    public ActivityLog Log { get; }

    /// <summary>Quản lý các phiên mở trang bán hàng song song (mỗi tài khoản một phiên độc lập).
    /// App shutdown gọi <see cref="AccountSessionManager.StopAllAsync"/> để kill hết Brave.</summary>
    public AccountSessionManager Sessions { get; }

    /// <summary>
    /// Phát khi kho đơn (bảng <c>orders</c>) vừa được ghi thêm/cập nhật — phiên sync gọi
    /// <see cref="RaiseOrdersChanged"/> ngay sau <c>OrdersRepository.UpsertMany</c> để màn "Đơn hàng" đang mở
    /// tự nạp lại. CỐ Ý có thể bắn từ THREAD NỀN của phiên sync → người nghe (OrdersViewModel) PHẢI marshal về
    /// UI thread trước khi đụng ObservableCollection.
    /// </summary>
    public event Action? OrdersChanged;

    /// <summary>Phiên gọi sau khi UpsertMany đơn về DB THÀNH CÔNG để phát <see cref="OrdersChanged"/>.</summary>
    public void RaiseOrdersChanged() => OrdersChanged?.Invoke();

    /// <summary>
    /// Phát khi TẬP tài khoản (bảng <c>accounts</c>) vừa được thêm/đổi từ NGOÀI màn "Tài khoản" — vd sync shop
    /// từ BigSeller Insert dòng tài khoản mới. Màn "Tài khoản" đang mở nghe để tự <c>Reload()</c> danh sách, thấy
    /// dòng mới NGAY mà không phải đổi màn. Y như <see cref="OrdersChanged"/>: CỐ Ý có thể bắn từ THREAD NỀN →
    /// người nghe (AccountsViewModel) PHẢI marshal về UI thread trước khi đụng ObservableCollection.
    /// </summary>
    public event Action? AccountsChanged;

    /// <summary>Bên ngoài (vd sync shop BigSeller) gọi sau khi Insert tài khoản THÀNH CÔNG để phát <see cref="AccountsChanged"/>.</summary>
    public void RaiseAccountsChanged() => AccountsChanged?.Invoke();

    /// <summary>
    /// SỐ HÀNG CÒN TỒN của vòng chờ đẩy (gộp MỌI tài khoản) — <see cref="HubOutboxWorker"/> ghi sau mỗi lượt,
    /// thanh trạng thái đọc để hiện "⏳ Chờ đẩy: N". Mặc định 0 (chưa có lượt nào chạy → không hiện gì).
    /// </summary>
    public OutboxPending PendingOutbox { get; private set; }

    /// <summary>
    /// Phát khi <see cref="PendingOutbox"/> ĐỔI. Y như <see cref="OrdersChanged"/>: CỐ Ý bắn từ THREAD NỀN của
    /// worker → người nghe PHẢI marshal về UI thread trước khi đụng property bind.
    /// </summary>
    public event Action? PendingOutboxChanged;

    /// <summary>Worker gọi sau mỗi lượt quét. Không đổi so với lần trước → KHÔNG phát sự kiện (khỏi làm mới UI thừa
    /// mỗi 2 phút).</summary>
    public void SetPendingOutbox(OutboxPending value)
    {
        if (value.Equals(PendingOutbox))
        {
            return;
        }
        PendingOutbox = value;
        PendingOutboxChanged?.Invoke();
    }

    public AppServices(string? dbPath = null)
    {
        Database = new Database(dbPath);
        Accounts = new AccountRepository(Database);
        Proxies = new ProxyRepository(Database);
        Settings = new SettingsRepository(Database);
        Orders = new OrdersRepository(Database);
        Results = new ResultsRepository(Database);

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
    }
}
