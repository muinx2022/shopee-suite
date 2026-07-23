using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace XuLyDonShopee.App.Services;

/// <summary>Trạng thái vòng đời của một phiên mở trang bán hàng.</summary>
public enum SessionState
{
    /// <summary>Chưa chạy / đã dừng / đã kết thúc bình thường (đóng cửa sổ).</summary>
    Stopped,

    /// <summary>Đang chuẩn bị: chọn proxy, tải/khởi chạy trình duyệt.</summary>
    Opening,

    /// <summary>Đã mở trình duyệt; đang tự đăng nhập / bắt cookie / theo dõi đơn.</summary>
    Running,

    /// <summary>Kết thúc do lỗi (xem <see cref="IAccountSession.LastError"/>).</summary>
    Error
}

/// <summary>
/// Một phiên độc lập phục vụ MỘT tài khoản: mở trình duyệt riêng (Brave/profile/CDP port/proxy riêng),
/// tự đăng nhập, bắt cookie, theo dõi đơn — chạy nền song song với các phiên khác.
/// <para>
/// Tách interface nhỏ để <see cref="AccountSessionManager"/> có thể được test bằng stub (không cần
/// Brave/Playwright thật).
/// </para>
/// </summary>
public interface IAccountSession
{
    /// <summary>Id tài khoản mà phiên này phục vụ.</summary>
    long AccountId { get; }

    /// <summary>Trạng thái vòng đời hiện tại.</summary>
    SessionState State { get; }

    /// <summary>Dòng trạng thái hiển thị theo tài khoản (null = ẩn).</summary>
    string? StatusText { get; }

    /// <summary>Số đơn "Chờ Lấy Hàng" đọc gần nhất (null = chưa đọc được / chưa đăng nhập).</summary>
    int? ToShipCount { get; }

    /// <summary>
    /// True khi phiên đã "SẴN SÀNG THAO TÁC" của lần mở HIỆN TẠI: đã tự-đăng-nhập xong và đọc được số
    /// "Chờ Lấy Hàng" lần đầu. Cờ TƯỜNG MINH này (không suy từ <see cref="ToShipCount"/> — số đó không reset
    /// khi relaunch nên dễ "sẵn sàng ảo") để VM CHỜ đúng thời điểm rồi mới Sync/Kiểm tra, tránh giẫm lên luồng
    /// tự-đăng-nhập. false ở đầu mỗi lần mở/relaunch và khi Stopped/Error.
    /// </summary>
    bool ReadyForActions { get; }

    /// <summary>Thông điệp lỗi gần nhất (có khi <see cref="State"/> == <see cref="SessionState.Error"/>).</summary>
    string? LastError { get; }

    /// <summary>Tiến trình Brave/Chromium của phiên (để Plan B đưa cửa sổ ra trước). Null nếu chưa/không có.</summary>
    Process? BraveProcess { get; }

    /// <summary>Phát khi State/StatusText/ToShipCount/LastError đổi — manager/VM nghe để cập nhật UI.</summary>
    event Action? Changed;

    /// <summary>Phát khi vừa lưu cookie vào DB cho tài khoản (id) — VM nghe để làm mới danh sách trên UI thread.</summary>
    event Action<long>? CookieSaved;

    /// <summary>Bắt đầu phiên. Nếu đang chuẩn bị/đang chạy thì bỏ qua (idempotent — không mở trùng).</summary>
    Task StartAsync();

    /// <summary>Dừng phiên: hủy vòng lặp, đóng &amp; kill cây tiến trình Brave, đưa về <see cref="SessionState.Stopped"/>.</summary>
    Task StopAsync();

    /// <summary>
    /// <b>Bước đầu xử lý đơn:</b> trong phiên ĐANG chạy, điều hướng <b>kiểu người</b> tới "Cài Đặt Vận
    /// Chuyển" rồi bấm tab "Địa Chỉ". Trả <c>false</c> nếu phiên chưa chạy / lỗi / không mở được (graceful,
    /// không ném). Trong lúc chạy sẽ chặn nhịp đọc đơn (reload) để không phá thao tác điều hướng giữa chừng.
    /// </summary>
    Task<bool> ProcessOrdersAsync();

    /// <summary>
    /// <b>Kiểm tra đơn NGAY (thủ công):</b> trong phiên ĐANG chạy, điều hướng cửa sổ về trang chủ Seller
    /// (có khoảng dừng "kiểu người" trước/sau) rồi đọc số "Chờ Lấy Hàng" tại chỗ, cập nhật
    /// <see cref="ToShipCount"/> — bản thủ công của nhịp theo dõi 30'. Trả <c>false</c> nếu phiên chưa
    /// chạy / đang bận điều hướng / không đọc được số (graceful, KHÔNG ném). Dùng cờ điều hướng bao trùm
    /// để nhịp 30' không reload chồng lên giữa chừng.
    /// </summary>
    Task<bool> CheckOrdersAsync();

    /// <summary>
    /// <b>Sync Đơn hàng:</b> trong phiên ĐANG chạy, vào Quản lý đơn hàng → tab "Tất cả", duyệt MỌI trang danh
    /// sách (chốt chặn an toàn) thu thập thông tin đơn rồi <b>upsert về DB</b> (bảng <c>orders</c>), ghi log
    /// tiến trình + tổng kết (thêm mới / cập nhật). Trả <c>false</c> nếu phiên chưa chạy / đang bận điều hướng
    /// (graceful, KHÔNG ném). Loại trừ lẫn nhau với Xử lý đơn / Kiểm tra qua cờ điều hướng bao trùm (không hai
    /// luồng chuột trên cùng trang).
    /// </summary>
    Task<bool> SyncOrdersAsync();

    /// <summary>
    /// <b>Tải LẠI phiếu MỘT đơn</b> (nút "Tải phiếu" màn Đơn hàng): trong phiên ĐANG chạy, về danh sách "Tất
    /// cả", định vị card theo mã, bấm "In phiếu giao" và lưu lại file PDF. Trả <c>false</c> nếu phiên chưa
    /// chạy / đang bận điều hướng / không tải được (graceful, KHÔNG ném). Loại trừ lẫn nhau với Sync / Xử lý
    /// đơn / Kiểm tra qua cờ điều hướng bao trùm.
    /// </summary>
    Task<bool> RedownloadSlipAsync(string orderSn);

    /// <summary>
    /// <b>Sync TRỌN GÓI</b> (nút Sync màn Tài khoản): trong phiên ĐANG chạy, chạy chuỗi <b>Kiểm tra đơn mới
    /// → (nếu có đơn Chờ Lấy Hàng) Xử lý đơn → Sync đơn hàng</b>. Ghép từ ba bước con
    /// (<see cref="CheckOrdersAsync"/>/<see cref="ProcessOrdersAsync"/>/<see cref="SyncOrdersAsync"/>); xử lý
    /// đơn lỗi giữa chừng KHÔNG chặn bước sync. Trả về kết quả bước Sync (graceful — KHÔNG ném; bước Kiểm tra
    /// báo bận/không đọc được → dừng chuỗi, trả <c>false</c>).
    /// </summary>
    Task<bool> SyncFullAsync();
}
