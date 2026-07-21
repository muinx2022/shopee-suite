namespace XuLyDonShopee.Core.Models;

/// <summary>
/// Lựa chọn trình duyệt để mở phiên Shopee (màn "Cài đặt"). Mỗi tài khoản vẫn mở bằng hồ sơ
/// persistent riêng (<c>--user-data-dir</c>) — lựa chọn này chỉ quyết định <b>file thực thi</b>
/// trình duyệt nào được dùng, KHÔNG đụng hồ sơ trình duyệt cá nhân của người dùng.
/// <list type="bullet">
/// <item><see cref="Auto"/> — tự dò: ưu tiên Chrome → Edge → Brave; không có gì → Chromium đóng gói.</item>
/// <item><see cref="Chrome"/> / <see cref="Edge"/> / <see cref="Brave"/> — ép trình duyệt cụ thể; không
/// tìm thấy trên máy → fallback Chromium đóng gói.</item>
/// <item><see cref="BundledChromium"/> — luôn dùng Chromium đóng gói của Playwright.</item>
/// </list>
/// </summary>
public enum BrowserChoice
{
    /// <summary>Tự động: ưu tiên Chrome → Edge → Brave; không có → Chromium đóng gói.</summary>
    Auto,

    /// <summary>Ép dùng Google Chrome (không có → fallback Chromium đóng gói).</summary>
    Chrome,

    /// <summary>Ép dùng Microsoft Edge (không có → fallback Chromium đóng gói).</summary>
    Edge,

    /// <summary>Ép dùng Brave (không có → fallback Chromium đóng gói).</summary>
    Brave,

    /// <summary>Luôn dùng Chromium đóng gói của Playwright.</summary>
    BundledChromium
}

/// <summary>
/// Helper thuần (test được, không đụng hệ thống) cho <see cref="BrowserChoice"/>: parse/serialize
/// chuỗi lưu bảng <c>settings</c>, nhãn tiếng Việt, và danh sách theo thứ tự hiển thị.
/// </summary>
public static class BrowserChoices
{
    /// <summary>Chuỗi lưu DB cho <see cref="BrowserChoice.Auto"/>.</summary>
    public const string Auto = "auto";

    /// <summary>Chuỗi lưu DB cho <see cref="BrowserChoice.Chrome"/>.</summary>
    public const string Chrome = "chrome";

    /// <summary>Chuỗi lưu DB cho <see cref="BrowserChoice.Edge"/>.</summary>
    public const string Edge = "edge";

    /// <summary>Chuỗi lưu DB cho <see cref="BrowserChoice.Brave"/>.</summary>
    public const string Brave = "brave";

    /// <summary>Chuỗi lưu DB cho <see cref="BrowserChoice.BundledChromium"/>.</summary>
    public const string BundledChromium = "chromium";

    /// <summary>Danh sách các lựa chọn theo THỨ TỰ HIỂN THỊ ở màn Cài đặt.</summary>
    public static readonly BrowserChoice[] All =
    {
        BrowserChoice.Auto,
        BrowserChoice.Chrome,
        BrowserChoice.Edge,
        BrowserChoice.Brave,
        BrowserChoice.BundledChromium
    };

    /// <summary>
    /// Đọc lựa chọn từ chuỗi lưu DB: khớp không phân biệt hoa/thường; null/trống/lạ → <see cref="BrowserChoice.Auto"/>.
    /// </summary>
    public static BrowserChoice Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BrowserChoice.Auto;
        }

        var v = value.Trim();
        if (string.Equals(v, Chrome, StringComparison.OrdinalIgnoreCase)) return BrowserChoice.Chrome;
        if (string.Equals(v, Edge, StringComparison.OrdinalIgnoreCase)) return BrowserChoice.Edge;
        if (string.Equals(v, Brave, StringComparison.OrdinalIgnoreCase)) return BrowserChoice.Brave;
        if (string.Equals(v, BundledChromium, StringComparison.OrdinalIgnoreCase)) return BrowserChoice.BundledChromium;
        return BrowserChoice.Auto; // Auto hoặc chuỗi lạ.
    }

    /// <summary>Chuỗi lưu DB cho một lựa chọn.</summary>
    public static string ToStorage(BrowserChoice choice) => choice switch
    {
        BrowserChoice.Chrome => Chrome,
        BrowserChoice.Edge => Edge,
        BrowserChoice.Brave => Brave,
        BrowserChoice.BundledChromium => BundledChromium,
        _ => Auto
    };

    /// <summary>Nhãn tiếng Việt hiển thị cho người dùng.</summary>
    public static string VnLabel(BrowserChoice choice) => choice switch
    {
        BrowserChoice.Chrome => "Chrome",
        BrowserChoice.Edge => "Edge",
        BrowserChoice.Brave => "Brave",
        BrowserChoice.BundledChromium => "Chromium đóng gói",
        _ => "Tự động (ưu tiên Chrome)"
    };
}
