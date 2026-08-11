using ToolkitLocator = Shopee.Toolkit.Browser.BrowserLocator;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Tìm file thực thi Brave cho module Đơn hàng.
/// <para>
/// <b>Module này CHỈ chạy được trên Brave</b> (chốt 11/08/2026). Trước đó có enum <c>BrowserChoice</c>
/// (Auto · Chrome · Edge · Brave · Chromium đóng gói) cho người dùng chọn ở màn Cài đặt, đã bỏ hẳn. Hai lý do:
/// </para>
/// <list type="number">
/// <item>Cầu nối bắt buộc nạp extension qua <c>--load-extension</c>. Chromium 137+ đã bỏ cờ cho phép cờ đó, nên
/// trên Chrome/Edge đời mới extension <b>không nạp mà cũng không báo lỗi</b> — cầu nối treo hết hạn 45 giây rồi
/// chết, không có dấu vết nào chỉ ra nguyên nhân.</item>
/// <item>Cả luồng chạy (cờ khởi động, dọn hồ sơ mồ côi, chống vứt tab, giới hạn crash GPU) đều đã bám Brave.</item>
/// </list>
/// <para>Việc DÒ đường dẫn nằm ở <see cref="ToolkitLocator"/> (shared/Shopee.Toolkit) — dùng chung với suite để
/// hai phía luôn mở CÙNG một file thực thi.</para>
/// </summary>
public static class BrowserLocator
{
    /// <summary>
    /// Hậu tố thư mục hồ sơ persistent theo trình duyệt — xem <see cref="BrowserProfilePaths.ForAccount"/>
    /// (<c>profiles/&lt;id&gt;-brave</c>).
    /// <para><b>ĐỪNG ĐỔI CHUỖI NÀY.</b> Nó là một phần tên thư mục hồ sơ đang tồn tại trên máy người dùng, nơi
    /// giữ cookie đăng nhập subaccount. Đổi một ký tự = mọi tài khoản trỏ sang thư mục rỗng, phải đăng nhập lại
    /// từ đầu và ăn captcha. Trước 11/08/2026 chuỗi này do <c>ResolveBrowserKind</c> tính theo lựa chọn của
    /// người dùng; nay app chỉ chạy Brave nên nó là hằng.</para>
    /// </summary>
    public const string LoaiHoSo = "brave";

    /// <summary>
    /// Tìm đường dẫn tới file thực thi Brave theo HĐH hiện tại. <c>null</c> = máy chưa cài Brave.
    /// <para>Không có đường lùi sang trình duyệt khác: app chặn ngay lúc khởi động (xem cổng Brave ở
    /// <c>App.OnStartup</c>), nên mọi chỗ gọi hàm này đều có quyền coi <c>null</c> là lỗi thật.</para>
    /// </summary>
    public static string? FindBraveExecutable() => ToolkitLocator.FindBraveExecutable();

    /// <summary>
    /// Đường dẫn Brave để mở phiên; ném <see cref="InvalidOperationException"/> kèm thông báo tiếng Việt nếu máy
    /// không có Brave (ca này chỉ xảy ra khi Brave bị gỡ TRONG LÚC app đang chạy — lúc khởi động đã chặn rồi).
    /// </summary>
    public static string RequireBraveExecutable()
        => FindBraveExecutable() ?? throw new InvalidOperationException(ThieuBraveMessage);

    /// <summary>Thông báo dùng chung cho mọi chỗ phát hiện thiếu Brave (cổng khởi động lẫn lúc phóng trình
    /// duyệt) — một câu chữ duy nhất để người dùng không thấy hai cách diễn đạt khác nhau cho cùng một bệnh.
    /// Nguồn chuẩn ở <see cref="Shopee.Toolkit.Browser.BraveRequirement.ThieuBraveMessage"/>.</summary>
    public const string ThieuBraveMessage = Shopee.Toolkit.Browser.BraveRequirement.ThieuBraveMessage;
}
