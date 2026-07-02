using System.IO;

namespace Shopee.Core.Infrastructure;

/// <summary>
/// Van an toàn DUNG LƯỢNG ĐĨA cho máy client (thường ít ổ hơn máy dev). Lý do tồn tại: profile Brave của app
/// là BỀN (giữ cookie login) + video tải về KHÔNG tự xoá → đĩa phình dần theo thời gian; nếu để ghi tới khi
/// ổ đầy 0 byte thì SQLite/DB của profile hỏng → mất phiên → Shopee bắn captcha hàng loạt (đúng thứ cần tránh).
/// Thà HOÃN mở cửa sổ / bỏ tải video và CẢNH BÁO còn hơn để hỏng dữ liệu.
///
/// Chỉ ĐỌC dung lượng, không xoá gì (việc dọn là của <see cref="StartupJanitor"/>). Best-effort: đọc lỗi →
/// coi như đủ chỗ (fail-open) để không kẹt oan luồng chạy vì một đường dẫn lạ.
/// </summary>
public static class DiskSpaceGuard
{
    /// <summary>Ngưỡng "sắp đầy" mặc định cho ổ chứa profile Brave (dưới mức này → hoãn mở cửa sổ mới).</summary>
    public const long DefaultMinFreeBytes = 5L * 1024 * 1024 * 1024;   // 5 GB

    /// <summary>Ngưỡng tối thiểu để cho phép tải 1 video về (nhỏ hơn vì mỗi video &lt; 60s, vài chục MB).</summary>
    public const long VideoMinFreeBytes = 2L * 1024 * 1024 * 1024;     // 2 GB

    /// <summary>Số byte trống trên ổ chứa <paramref name="path"/>. Trả -1 nếu không đọc được.</summary>
    public static long FreeBytesFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return -1;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : -1;
        }
        catch { return -1; }
    }

    /// <summary>true nếu ổ chứa <paramref name="path"/> còn trống ≥ <paramref name="minFreeBytes"/>.
    /// Đọc lỗi (free &lt; 0) → true (fail-open, không chặn oan).</summary>
    public static bool HasFreeSpace(string? path, long minFreeBytes)
    {
        var free = FreeBytesFor(path);
        return free < 0 || free >= minFreeBytes;
    }

    /// <summary>Đổi số byte ra "x.y GB" để log cho người đọc.</summary>
    public static string ToGb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
}
