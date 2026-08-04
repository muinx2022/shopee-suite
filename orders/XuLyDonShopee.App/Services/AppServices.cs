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
/// SỐ HÀNG CÒN TỒN trong vòng chờ đẩy, tách theo 5 loại đích: <paramref name="Orders"/> đơn lên Hub ·
/// <paramref name="Slips"/> file phiếu lên Hub · <paramref name="SheetRows"/> dòng Google Sheet ·
/// <paramref name="SoldCounts"/> lượt +1 "Đã bán" · <paramref name="ReturnCodes"/> mã trả hàng chờ lên Google
/// Sheet (kho <c>return_codes</c> — sống ĐỘC LẬP với bảng <c>orders</c> nên phải đếm riêng, tài khoản đã dọn sạch
/// đơn vẫn có thể còn cả đống mã). <see cref="HubOutboxWorker"/> tính lại sau mỗi lượt; thanh trạng thái hiện
/// <see cref="Tong"/> khi &gt; 0. Loại nào KHÔNG có đích (hook hub chưa rót / URL sheet trống) được đếm 0 để badge
/// không kẹt số vĩnh viễn.
/// </summary>
public readonly record struct OutboxPending(
    int Orders = 0, int Slips = 0, int SheetRows = 0, int SoldCounts = 0, int ReturnCodes = 0)
{
    /// <summary>Tổng hàng còn chờ đẩy (cả 5 loại).</summary>
    public int Tong => Orders + Slips + SheetRows + SoldCounts + ReturnCodes;

    /// <summary>Cộng dồn số tồn của một tài khoản nữa vào tổng của cả lượt.</summary>
    public OutboxPending Cong(OutboxPending khac)
        => new(Orders + khac.Orders, Slips + khac.Slips, SheetRows + khac.SheetRows,
            SoldCounts + khac.SoldCounts, ReturnCodes + khac.ReturnCodes);
}

/// <summary>
/// Kết quả xin KHÓA CHẠY một tài khoản (chống hai máy cùng chạy một subaccount). <paramref name="Ok"/> = false ⇒
/// máy KHÁC đang chạy tài khoản này (phiên bỏ qua lượt, KHÔNG xếp hàng chờ); <paramref name="HolderMachine"/> =
/// tên máy đang giữ (null khi hub không nói được là máy nào — vẫn là "máy khác").
/// </summary>
public sealed record OrdersLeaseResult(bool Ok, string? HolderMachine);

/// <summary>Một sub-acc Đơn hàng trong DANH BẠ GỘP toàn Hub (kéo về máy mới): <paramref name="Login"/> = email
/// đăng nhập, <paramref name="Shops"/> = shop con (login + tên). KHÔNG mang mật khẩu/cookie — Hub không giữ.
/// Kiểu RIÊNG của module (module Đơn hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên không lộ DTO hub vào đây).</summary>
public sealed record OrdersDirectoryItem(string Login, IReadOnlyList<(string Login, string Name)> Shops);

/// <summary>Dòng thống kê dùng chung từ Hub cho tab "Thống kê".</summary>
public sealed record SharedStatBreakdown(string Label, int OrderCount, long Value, double Percentage);

/// <summary>Dòng shop thống kê dùng chung từ Hub.</summary>
public sealed record SharedShopStatRow(string Shop, int OrderCount, int ItemCount, long Revenue, long Average, double TrackingRate);

/// <summary>
/// Kết quả thống kê dùng chung từ Hub — CHỈ SỐ THÔ. Hub chạy giờ UTC và không biết múi giờ/định dạng của từng
/// máy nên KHÔNG dựng chuỗi hiển thị: màn Thống kê tự định dạng tiền/ngày/tỉ lệ.
/// <paramref name="ActiveOrders"/> = số đơn không hủy (mẫu số "TB / đơn hiệu lực");
/// <paramref name="LastSyncedUtc"/> = lần đẩy đơn lên hub gần nhất trong khoảng (null = khoảng đó không có đơn).
/// </summary>
public sealed record SharedOrderStatistics(
    int TotalOrders,
    int TotalItems,
    int NeedsAction,
    int Delivered,
    int Cancelled,
    long Revenue,
    long AverageOrder,
    int ActiveOrders,
    int WithTracking,
    int WithFinalAmount,
    DateTimeOffset? LastSyncedUtc,
    IReadOnlyList<SharedStatBreakdown> StatusRows,
    IReadOnlyList<SharedShopStatRow> ShopRows,
    IReadOnlyList<SharedStatBreakdown> CarrierRows,
    IReadOnlyList<SharedStatBreakdown> PaymentRows);

/// <summary>
/// Gom Database + các repository, khởi tạo một lần và truyền vào ViewModel.
/// (Bước đầu không dùng DI container.)
/// </summary>
public class AppServices
{
    public Database Database { get; }
    public AccountRepository Accounts { get; }
    public SettingsRepository Settings { get; }

    /// <summary>Kho đơn hàng đã sync (bảng <c>orders</c>) — phiên ghi qua đây khi Sync Đơn hàng.</summary>
    public OrdersRepository Orders { get; }

    /// <summary>Kho "Kết quả chuẩn bị hàng" theo shop/ngày (bảng <c>account_shops</c> + <c>prepare_daily</c>) —
    /// phiên cầu nối ghi qua callback (lưu shop + tăng đếm mỗi đơn arrange); tab "Kết quả" đọc để hiển thị.</summary>
    public ResultsRepository Results { get; }

    /// <summary>Kho banner cảnh báo lỗi địa chỉ lấy hàng (bảng <c>pickup_address_alerts</c>) — bền tới khi bấm X;
    /// đồng bộ Hub theo tài khoản.</summary>
    public PickupAddressAlertsRepository PickupAlerts { get; }

    /// <summary>Kho MÃ YÊU CẦU TRẢ HÀNG (bảng <c>return_codes</c>) — sống ĐỘC LẬP với vòng đời đơn, nên mã của đơn
    /// đã bị dọn khỏi app vẫn đẩy được lên Google Sheet.</summary>
    public ReturnCodesRepository ReturnCodes { get; }

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
    /// HOOK báo sự kiện lỗi app lên Hub (Hub quyết định gửi webhook). Tham số: kind (vd.
    /// <c>khong_dat_duoc_dia_chi</c>), account, shop, detail, machine, ct. Trả true = Hub nhận OK.
    /// Null = chưa nối Hub → caller gửi Slack local (fallback độc lập).
    /// </summary>
    public Func<string, string?, string?, string?, string?, CancellationToken, Task<bool>>? ReportAppAlertToHub { get; set; }

    /// <summary>
    /// HOOK upsert banner lỗi địa chỉ lên Hub (đồng bộ đa máy). Tham số: accountLogin, shopLogin, province,
    /// occurredAt (mốc sự kiện local), ct. Trả true = Hub nhận OK. Null = chưa nối Hub.
    /// </summary>
    public Func<string, string, string?, DateTimeOffset?, CancellationToken, Task<bool>>? UpsertPickupAlertToHub { get; set; }

    /// <summary>
    /// HOOK dismiss banner lỗi địa chỉ trên Hub. Tham số: accountLogin, shopLogin, occurredAt, ct. Null = chưa nối Hub.
    /// </summary>
    public Func<string, string, DateTimeOffset?, CancellationToken, Task<bool>>? DismissPickupAlertToHub { get; set; }

    /// <summary>
    /// HOOK kéo danh sách banner lỗi địa chỉ từ Hub cho một tài khoản (kể cả đã dismiss — để merge).
    /// Trả null = không hỏi được Hub.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<PickupAlertHubDong>?>>? FetchPickupAlertsFromHub { get; set; }

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
    /// HOOK đẩy cấu hình ĐỒNG BỘ GOOGLE SHEET (URL Web App + tab override + link file phụ) của máy này lên HUB, do
    /// shell suite RÓT (module Đơn hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Tham số:
    /// <c>url</c>, <c>tab</c> ("" = tự động theo tháng), <c>sheet2</c> ("" = không ghi file phụ), <c>ct</c>. Trả
    /// <c>true</c> = hub nhận OK → màn Cài đặt báo "đã đồng bộ lên Hub".
    /// Mặc định <c>null</c> = TẮT (app Đơn hàng chạy độc lập / hub chưa cấu hình → chỉ lưu local như cũ).
    /// Màn Cài đặt gọi sau khi LƯU local; hub CHỈ nhận khối GSheet khi URL khác trống nên máy chưa cấu hình không
    /// xoá được cấu hình của cả fleet.
    /// </summary>
    public Func<string, string, string, CancellationToken, Task<bool>>? PushGsheetConfigToHub { get; set; }

    /// <summary>
    /// HOOK đọc thống kê ĐƠN HÀNG dùng chung từ Hub theo khoảng + shop. Tham số: <c>fromUtc</c>,
    /// <c>toUtcExclusive</c> (hai MỐC UTC màn hình tính SẴN từ ngày địa phương người dùng — hub không suy diễn múi
    /// giờ; biên <c>[from, to)</c> y hệt bộ lọc <c>created_at</c> của kho đơn local), shop, <c>ct</c>. Trả
    /// <c>null</c> = hub không phản hồi / route chưa có, để màn GIỮ số local; trả object = số liệu CHUNG toàn hệ
    /// thống, mọi client cùng nhìn một ảnh chụp. Mặc định <c>null</c> = TẮT (app chạy độc lập / chưa nối Hub).
    /// </summary>
    public Func<DateTime, DateTime, string?, CancellationToken, Task<SharedOrderStatistics?>>? QueryOrderStatistics { get; set; }

    /// <summary>
    /// HOOK đọc SỐ ĐƠN "chuẩn bị hàng" CHUNG TOÀN HỆ THỐNG theo shop trong MỘT ngày (tab "Kết quả"), do shell
    /// suite RÓT (module Đơn hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Tham số: <c>day</c>
    /// (<c>yyyy-MM-dd</c> giờ địa phương), <c>ct</c>; trả map <c>shop_login → số đơn</c>. Hub đếm THẲNG từ bảng đơn
    /// nên nhiều máy cùng chạy vẫn ra con số thật (không cộng trùng bộ đếm rời của từng máy). Khóa map so khớp
    /// KHÔNG phân biệt hoa/thường — bên NHẬN tự chuẩn hóa, không phụ thuộc comparer bên rót hook.
    /// <para><b>null KHÁC map rỗng:</b> <c>null</c> = KHÔNG hỏi được hub (chưa cấu hình / offline / hub cũ chưa có
    /// route) → màn GIỮ số cục bộ + ghi chú; map RỖNG = hub bảo ngày đó chưa shop nào có đơn (số 0 là số THẬT).
    /// Mặc định <c>null</c> = TẮT (app Đơn hàng chạy độc lập) → hành vi y như trước, số của máy.</para>
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>?>>? QueryPrepareStats { get; set; }

    /// <summary>
    /// HOOK XIN KHÓA CHẠY một tài khoản trên HUB — chống hai máy cùng chạy MỘT subaccount (tranh đơn "chuẩn bị
    /// hàng", đăng nhập song song một tài khoản Shopee → đá phiên/ăn captcha), do shell suite RÓT (module Đơn hàng
    /// KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Tham số: <b>login subaccount THÔ</b> (chưa thêm
    /// tiền tố, chưa hạ chữ — quy ước khóa do phía suite lo, module không phải biết), <c>ct</c>.
    /// <para><see cref="OrdersLeaseResult.Ok"/> = false ⇒ máy khác đang giữ → phiên KHÔNG mở trình duyệt, kết thúc
    /// êm (bỏ qua lượt, KHÔNG xếp hàng chờ). Hub chưa cấu hình / offline / lỗi ⇒ trả <c>Ok = true</c> (degrade như
    /// MỘT máy: mất hub thì cũng không phối hợp được với ai, chặn sẽ làm app vô dụng khi mất mạng).</para>
    /// Mặc định <c>null</c> = TẮT (app Đơn hàng chạy độc lập / hub chưa cấu hình) → phiên chạy y như trước.
    /// </summary>
    public Func<string, CancellationToken, Task<OrdersLeaseResult>>? AcquireAccountLease { get; set; }

    /// <summary>
    /// HOOK kéo DANH BẠ sub-acc Đơn hàng GỘP TỪ MỌI MÁY trên Hub (login + shop; KHÔNG mật khẩu/cookie), do shell
    /// suite RÓT (module Đơn hàng KHÔNG tham chiếu <c>Shopee.Core</c> nên không tự biết hub). Máy MỚI dùng để tạo
    /// sẵn bản ghi tài khoản rỗng-mật-khẩu; người dùng tự nhập mật khẩu rồi đăng nhập.
    /// <para><c>null</c> trả về = KHÔNG hỏi được Hub (chưa cấu hình / offline / hub cũ chưa có route) — khác list
    /// rỗng ("Hub chưa có tài khoản nào"). Mặc định property <c>null</c> = TẮT (app chạy độc lập / hub chưa cấu hình).</para>
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<OrdersDirectoryItem>?>>? QueryOrdersDirectory { get; set; }

    /// <summary>
    /// HOOK NHẢ khóa chạy tài khoản — cặp với <see cref="AcquireAccountLease"/>, tham số cũng là <b>login
    /// subaccount THÔ</b>. Phiên gọi ở MỌI lối ra (xong / lỗi / hủy) và ĐÚNG MỘT lần cho mỗi lần giành được.
    /// Nhả sót là lỗi nặng: tài khoản bị coi là "đang chạy ở máy X" tới khi lease hết hạn (~5') → máy khác không
    /// chạy được. Mặc định <c>null</c> = TẮT (app chạy độc lập / hub chưa cấu hình).
    /// </summary>
    public Func<string, Task>? ReleaseAccountLease { get; set; }

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

    /// <summary>
    /// Phát khi vừa CHUẨN BỊ HÀNG xong một đơn (số đếm của tab "Kết quả" vừa tăng) — kèm id tài khoản để màn
    /// "Tài khoản" chỉ nạp lại khi ĐÚNG tài khoản đang mở. Trước đây tab "Kết quả" chỉ đọc lại lúc đổi tài khoản
    /// / đổi ngày, nên đang chạy thì số đứng im dù CSDL đã cộng ngay. Y như <see cref="OrdersChanged"/>: CỐ Ý bắn
    /// từ THREAD NỀN của phiên → người nghe PHẢI marshal về UI thread.
    /// </summary>
    public event Action<long>? PrepareCountChanged;

    /// <summary>Phiên gọi ngay sau <c>ResultsRepository.IncrementPrepared</c> để tab "Kết quả" tự cập nhật.</summary>
    public void RaisePrepareCountChanged(long accountId) => PrepareCountChanged?.Invoke(accountId);

    /// <summary>
    /// Phát khi vừa ĐỌC ĐƯỢC danh sách shop của một tài khoản (ghi <c>account_shops</c>) — tab "Kết quả" nghe để
    /// dựng lưới NGAY. Không có sự kiện này thì màn đang mở giữ nguyên kết quả nạp lần đầu: người dùng mở app
    /// rồi chọn tài khoản TRƯỚC khi phiên kịp đọc shop sẽ thấy lưới TRỐNG mãi (chỉ hết khi bấm sang tài khoản
    /// khác rồi bấm lại). Y như <see cref="OrdersChanged"/>: CỐ Ý bắn từ THREAD NỀN → người nghe PHẢI marshal
    /// về UI thread.
    /// </summary>
    public event Action<long>? ShopListChanged;

    /// <summary>Phiên gọi ngay sau <c>ResultsRepository.UpsertShops</c> để tab "Kết quả" dựng lưới shop.</summary>
    public void RaiseShopListChanged(long accountId) => ShopListChanged?.Invoke(accountId);

    /// <summary>
    /// Phát khi phiên BẮT ĐẦU / XONG việc check một shop — cột tiến độ của tab "Kết quả" dùng để chuyển vòng quay
    /// sang shop đang chạy tới và đóng dấu tick cho shop vừa xong. Tham số: id tài khoản + <b>nhãn shop</b> (ĐÚNG khóa
    /// <c>prepare_daily</c>: <c>LoginName</c>, rỗng thì <c>ShopName</c>) + đang-check hay không. Y như
    /// <see cref="OrdersChanged"/>: CỐ Ý bắn từ THREAD NỀN của phiên → người nghe PHẢI marshal về UI thread.
    /// </summary>
    public event Action<long, string, bool>? ShopCheckChanged;

    /// <summary>Phiên gọi khi vào/ra một shop để tab "Kết quả" vẽ vòng quay + tick đúng shop.</summary>
    public void RaiseShopCheckChanged(long accountId, string shopLabel, bool checking)
        => ShopCheckChanged?.Invoke(accountId, shopLabel, checking);

    /// <summary>
    /// Phát khi vừa ghi/đóng banner lỗi địa chỉ (upsert hoặc dismiss) — tab Kết quả nghe để nạp lại list.
    /// CỐ Ý có thể bắn từ THREAD NỀN → người nghe PHẢI marshal về UI thread.
    /// </summary>
    public event Action<long>? AddressAlertsChanged;

    /// <summary>Phiên / ViewModel gọi sau khi upsert hoặc dismiss alert địa chỉ.</summary>
    public void RaiseAddressAlertsChanged(long accountId) => AddressAlertsChanged?.Invoke(accountId);

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
        // KHÔNG còn ProxyRepository: proxy runtime đã gỡ khỏi module (bảng `proxies` trong app.db vẫn giữ
        // nguyên, chỉ không còn code nào đọc).
        Settings = new SettingsRepository(Database);
        Orders = new OrdersRepository(Database);
        Results = new ResultsRepository(Database);
        PickupAlerts = new PickupAddressAlertsRepository(Database);
        ReturnCodes = new ReturnCodesRepository(Database);

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
        // Tạo sau các repository vì factory phiên đọc Accounts/Settings khi chạy.
        Sessions = new AccountSessionManager(this);
    }
}
