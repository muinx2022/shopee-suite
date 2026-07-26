namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Bộ icon vector ĐƠN SẮC (Material filled, viewbox 24×24) dùng chung TOÀN APP — thay emoji màu và ký tự
/// Unicode để look đồng nhất, chuyên nghiệp. Tô bằng Foreground (qua PathIcon) nên tự đổi màu theo class
/// của nút / trạng thái tab mà không cần đổi icon.
/// <para>
/// ══════════ BẢNG ÁNH XẠ CHUẨN — HÀNH ĐỘNG → ICON ══════════<br/>
/// <b>MỖI HÀNH ĐỘNG ĐÚNG MỘT ICON.</b> Thêm icon mới thì PHẢI cập nhật bảng này; muốn dùng icon cho một
/// hành động đã có trong bảng thì dùng lại đúng tên ở đây, KHÔNG tự chế glyph/emoji mới.
/// </para>
/// <code>
/// | Hành động                | Icon           | Thay cho glyph cũ            |
/// |--------------------------|----------------|------------------------------|
/// | Chạy / Bắt đầu           | Play           | ▶ · "Chạy"                   |
/// | Dừng                     | Stop           | ■ · ✕ (nghĩa dừng) · "Dừng"  |
/// | Tạm dừng                 | Pause          | ⏸ ⏯                          |
/// | Tiếp tục                 | Resume         | ⏯ (cùng hình mũi tên Play)   |
/// | Tải lại / Làm mới        | Refresh        | ↻ ⟳ ⟲ 🔄 · "Làm mới"         |
/// | Lưu                      | Save           | 💾 ✔ ✓ · "Lưu"               |
/// | Xóa                      | Delete         | 🗑 − ✖ · "Xóa"               |
/// | Thêm                     | Add            | + ➕                          |
/// | Nhập (import)            | Import         | ⬇ · "Import"                 |
/// | Xuất (export)            | Export         | ⬆ 📦 · "Xuất CSV"            |
/// | Mở thư mục / duyệt       | Folder         | 📂 📁 …                       |
/// | Mở liên kết ngoài        | OpenExternal   | ↗                            |
/// | Lọc                      | Filter         | 🔍 ▽ · "Lọc"                 |
/// | Sửa                      | Edit           | ✎ ✏                          |
/// | Đồng ý / Xác nhận        | Check          | ✓ ✔ · "Đồng ý" · "OK"        |
/// | Chọn tất cả              | CheckAll       | ✓ ✔                          |
/// | Bỏ chọn                  | Uncheck        | ✖ ✕ (nghĩa bỏ chọn)          |
/// | Cập nhật app             | Upgrade        | ⬆ 🔄                          |
/// | Thống kê                 | Chart          | 📊 ▤                          |
/// | Đăng nhập / khoá         | Login          | 🔐                            |
/// | Sinh mới / AI            | Sparkle        | 🆕 🤖                         |
/// | Đồng bộ                  | Sync           | ⇅ ⟲                          |
/// | Hủy / đóng               | Close          | ✕ ↶ · "Hủy" · "Đóng"         |
/// </code>
/// <para>
/// Màu icon KHÔNG đặt tại chỗ dùng — đặt qua class nút trong <c>Themes/Theme.axaml</c>
/// (<c>.primary</c> → cam · <c>.destructive</c>/<c>.danger</c> → đỏ · <c>.success</c> → xanh · mặc định → xám).
/// </para>
/// </summary>
public static class AppIcons
{
    // ══════════ ĐIỀU HƯỚNG (ribbon + màn Welcome) — bộ có từ trước, KHÔNG đổi ══════════

    /// <summary>Ô bảng điều khiển — Workspace (Scrape · Import · Update).</summary>
    public const string Dashboard =
        "M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z";

    /// <summary>Trụ cơ sở dữ liệu — Cấu hình (kho tài khoản · workbook · cookie · shop · proxy).</summary>
    public const string Database =
        "M12 3C7.58 3 4 4.79 4 7v10c0 2.21 3.59 4 8 4s8-1.79 8-4V7c0-2.21-3.58-4-8-4zm6 14c0 .5-2.13 " +
        "2-6 2s-6-1.5-6-2v-2.23c1.61.78 3.72 1.23 6 1.23s4.39-.45 6-1.23V17zm0-4.55c-1.3.95-3.58 1.55-6 " +
        "1.55s-4.7-.6-6-1.55V9.64c1.47.83 3.61 1.36 6 1.36s4.53-.53 6-1.36v2.81zM12 9C8.13 9 6 7.5 6 " +
        "7s2.13-2 6-2 6 1.5 6 2-2.13 2-6 2z";

    /// <summary>Kính lúp — Search.</summary>
    public const string Search =
        "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 " +
        "9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 " +
        "9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z";

    /// <summary>Nhóm người — Tài khoản &amp; Proxy.</summary>
    public const string People =
        "M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5c-1.66 0-3 1.34-3 3s1.34 3 3 3zm-8 0c1.66 0 2.99-1.34 " +
        "2.99-3S9.66 5 8 5C6.34 5 5 6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5c0-2.33-4.67-3.5-7-3.5zm8 " +
        "0c-.29 0-.62.02-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z";

    /// <summary>Dàn máy chủ — Trạng thái &amp; Giao việc (đa máy).</summary>
    public const string Servers =
        "M20 13H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1v-6c0-.55-.45-1-1-1zM7 " +
        "19c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM20 3H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 " +
        "1h16c.55 0 1-.45 1-1V4c0-.55-.45-1-1-1zM7 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z";

    /// <summary>Thùng hàng — Dữ liệu sản phẩm (kho Hub).</summary>
    public const string Inventory =
        "M20 2H4c-1 0-2 .9-2 2v3.01c0 .72.43 1.34 1 1.69V20c0 1.1 1.1 2 2 2h14c.9 0 2-.9 2-2V8.7c.57-.35 " +
        "1-.97 1-1.69V4c0-1.1-1-2-2-2zm-5 12H9v-2h6v2zm5-7H4V4h16v3z";

    /// <summary>Hóa đơn dài (receipt_long) — Xử lý đơn Shopee (theo dõi &amp; xử lý đơn · in phiếu).</summary>
    public const string Receipt =
        "M19.5 3.5L18 2l-1.5 1.5L15 2l-1.5 1.5L12 2l-1.5 1.5L9 2 7.5 3.5 6 2v14H3v3c0 1.66 1.34 3 3 " +
        "3h12c1.66 0 3-1.34 3-3V2l-1.5 1.5zM19 19c0 .55-.45 1-1 1s-1-.45-1-1v-3H8V5h11v14zM9 7h6v2H9V7zm7 " +
        "0h2v2h-2V7zm-7 3h6v2H9v-2zm7 0h2v2h-2v-2z";

    /// <summary>Nút play trong vòng tròn — màn "Chạy tự động" (module đơn hàng).</summary>
    public const string PlayCircle =
        "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 14.5v-9l6 4.5-6 4.5z";

    /// <summary>Hai mũi tên đổi chiều — màn "Proxy" (module đơn hàng).</summary>
    public const string SwapHoriz =
        "M6.99 11L3 15l3.99 4v-3H14v-2H6.99v-3zM21 9l-3.99-4v3H10v2h7.01v3L21 9z";

    /// <summary>Bánh răng — Cài đặt.</summary>
    public const string Settings =
        "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 " +
        "0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 " +
        "8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 " +
        "1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 " +
        "2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 " +
        "0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 " +
        "3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z";

    // ══════════ HÀNH ĐỘNG — theo bảng ánh xạ ở đầu file ══════════

    /// <summary>Mũi tên play — Chạy / Bắt đầu.</summary>
    public const string Play = "M8 5v14l11-7z";

    /// <summary>Ô vuông đặc — Dừng.</summary>
    public const string Stop = "M6 6h12v12H6z";

    /// <summary>Hai vạch dọc — Tạm dừng.</summary>
    public const string Pause = "M6 19h4V5H6v14zm8-14v14h4V5h-4z";

    /// <summary>Tiếp tục sau khi tạm dừng — cùng hình mũi tên với <see cref="Play"/> (đúng quy ước Material).</summary>
    public const string Resume = Play;

    /// <summary>Mũi tên vòng tròn — Tải lại / Làm mới.</summary>
    public const string Refresh =
        "M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-8 8s3.58 8 8 8c3.73 0 6.84-2.55 " +
        "7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 " +
        "1.78L13 11h7V4l-2.35 2.35z";

    /// <summary>Đĩa mềm — Lưu.</summary>
    public const string Save =
        "M17 3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 " +
        "3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z";

    /// <summary>Thùng rác — Xóa.</summary>
    public const string Delete =
        "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z";

    /// <summary>Dấu cộng — Thêm.</summary>
    public const string Add = "M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z";

    /// <summary>Mũi tên xuống khay — Nhập (import).</summary>
    public const string Import = "M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z";

    /// <summary>Mũi tên lên khỏi khay — Xuất (export).</summary>
    public const string Export = "M9 16h6v-6h4l-7-7-7 7h4zm-4 2h14v2H5z";

    /// <summary>Thư mục — Mở thư mục / duyệt đường dẫn.</summary>
    public const string Folder =
        "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z";

    /// <summary>Ô vuông + mũi tên chéo — Mở liên kết ngoài.</summary>
    public const string OpenExternal =
        "M19 19H5V5h7V3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2v-7h-2v7zM14 3v2h3.59l-9.83 " +
        "9.83 1.41 1.41L19 6.41V10h2V3h-7z";

    /// <summary>Phễu lọc — Lọc.</summary>
    public const string Filter =
        "M4.25 5.61C6.27 8.2 10 13 10 13v6c0 .55.45 1 1 1h2c.55 0 1-.45 1-1v-6s3.72-4.8 " +
        "5.74-7.39c.51-.66.04-1.61-.79-1.61H5.04c-.83 0-1.3.95-.79 1.61z";

    /// <summary>Bút chì — Sửa.</summary>
    public const string Edit =
        "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04c.39-.39.39-1.02 0-1.41l-2.34-2.34c-.39-.39-1.02-.39-1.41 " +
        "0l-1.83 1.83 3.75 3.75 1.83-1.83z";

    /// <summary>Một dấu tích — Đồng ý / Xác nhận (nút OK của hộp thoại). KHÁC <see cref="CheckAll"/>
    /// (hai dấu tích = "chọn tất cả" trong danh sách) — đừng dùng lẫn.</summary>
    public const string Check = "M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z";

    /// <summary>Hai dấu tích chồng nhau — Chọn tất cả.</summary>
    public const string CheckAll =
        "M18 7l-1.41-1.41-6.34 6.34 1.41 1.41L18 7zm4.24-1.41L11.66 16.17 7.48 12l-1.41 1.41L11.66 " +
        "19L23.66 7l-1.42-1.41zM0.41 13.41L6 19l1.41-1.41L1.83 12L0.41 13.41z";

    /// <summary>Ô vuông rỗng — Bỏ chọn.</summary>
    public const string Uncheck =
        "M19 5v14H5V5h14m0-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2z";

    /// <summary>Mũi tên lên trên vạch — Cập nhật app.</summary>
    public const string Upgrade = "M16 18v2H8v-2h8zM11 7.99V16h2V7.99h3L12 4 8 7.99h3z";

    /// <summary>Biểu đồ cột — Thống kê.</summary>
    public const string Chart = "M5 9.2h3V19H5zM10.6 5h2.8v14h-2.8zm5.6 8H19v6h-2.8z";

    /// <summary>Ổ khóa — Đăng nhập / khoá.</summary>
    public const string Login =
        "M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 " +
        "2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm3.1-9H8.9V6c0-1.71 " +
        "1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2z";

    /// <summary>Chùm tia lấp lánh — Sinh mới / AI.</summary>
    public const string Sparkle =
        "M19 9l1.25-2.75L23 5l-2.75-1.25L19 1l-1.25 2.75L15 5l2.75 1.25L19 9zm-7.5.5L9 4 6.5 9.5 1 " +
        "12l5.5 2.5L9 20l2.5-5.5L17 12l-5.5-2.5zM19 15l-1.25 2.75L15 19l2.75 1.25L19 23l1.25-2.75L23 " +
        "19l-2.75-1.25L19 15z";

    /// <summary>Hai mũi tên vòng — Đồng bộ.</summary>
    public const string Sync =
        "M12 4V1L8 5l4 4V6c3.31 0 6 2.69 6 6 0 1.01-.25 1.97-.7 2.8l1.46 1.46C19.54 15.03 20 13.57 20 " +
        "12c0-4.42-3.58-8-8-8zm0 14c-3.31 0-6-2.69-6-6 0-1.01.25-1.97.7-2.8L5.24 7.74C4.46 8.97 4 " +
        "10.43 4 12c0 4.42 3.58 8 8 8v3l4-4-4-4v3z";

    /// <summary>Dấu nhân — Hủy / đóng.</summary>
    public const string Close =
        "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 " +
        "17.59 13.41 12z";
}
