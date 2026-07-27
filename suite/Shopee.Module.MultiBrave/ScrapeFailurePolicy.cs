using Shopee.Core.Proxy;

namespace Shopee.Modules.MultiBrave;

/// <summary>Cách xử lý một khối scrape vừa kết thúc (quyết định của <see cref="ScrapeRunner"/>).</summary>
public enum ScrapeFailureAction
{
    /// <summary>Không lỗi → trả tk về kho.</summary>
    None,
    /// <summary>Captcha → LOẠI tk khỏi khung (đổi tk khác, phần dở vá).</summary>
    Quarantine,
    /// <summary>Lỗi HẠ TẦNG TOÀN CỤC (key proxy chết) → DỪNG cả job: đổi tk vô ích, vá/bỏ dòng chỉ tổ thủng dữ liệu.</summary>
    StopJob,
    /// <summary>Lỗi tạm của một tk (proxy lẻ/đứt) → cho tk nghỉ, vá phần dở bằng tk khác.</summary>
    Cooldown,
}

/// <summary>Luật xử lý khối scrape lỗi — TÁCH khỏi <see cref="ScrapeRunner"/> (vốn cần Brave/CDP thật) để
/// test được bằng hàm thuần.</summary>
public static class ScrapeFailurePolicy
{
    /// <summary>Quyết định theo kết quả 1 khối: captcha ưu tiên trước (giữ nguyên hành vi cũ), rồi tới lỗi
    /// hạ tầng toàn cục, còn lại là lỗi tạm → cooldown.</summary>
    public static ScrapeFailureAction Decide(bool errored, bool isCaptcha, string? reason)
    {
        if (!errored) return ScrapeFailureAction.None;
        if (isCaptcha) return ScrapeFailureAction.Quarantine;
        if (ProxyFailure.IsFleetWideProxyFailure(reason)) return ScrapeFailureAction.StopJob;
        return ScrapeFailureAction.Cooldown;
    }

    /// <summary>Lý do NGẮN, người-đọc-hiểu-ngay để báo 'failed' lên hub (hiện thẳng trên ô ở trang Giao việc).</summary>
    public static string FleetWideReason(string? reason)
    {
        if (reason is not null && reason.Contains("KEY_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            return "key proxy không tồn tại (KEY_NOT_FOUND) — sửa key rồi chạy lại";
        if (reason is not null && reason.Contains("KEY_EXPIRED", StringComparison.OrdinalIgnoreCase))
            return "key proxy hết hạn (KEY_EXPIRED) — gia hạn key rồi chạy lại";
        return "key proxy KiotProxy không dùng được — kiểm tra key rồi chạy lại";
    }
}
