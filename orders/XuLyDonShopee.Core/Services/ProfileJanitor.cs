using System;
using System.IO;
using System.Threading;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Dọn (xóa + tạo lại) một thư mục hồ sơ trình duyệt. Hàm thuần BCL (không phụ thuộc gì ngoài
/// System.IO) nên test được dễ dàng với thư mục tạm.
/// </summary>
public static class ProfileJanitor
{
    /// <summary>
    /// Xóa sạch <paramref name="dir"/> (nếu tồn tại) rồi tạo lại RỖNG. Vì thư mục hồ sơ có thể bị
    /// Brave mồ côi giữ khóa, retry tối đa <paramref name="attempts"/> lần (nghỉ
    /// <paramref name="delayMs"/> ms giữa các lần) khi gặp <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/>. KHÔNG ném ra ngoài — trả <c>false</c> khi cuối cùng
    /// vẫn thất bại để caller degrade êm (chạy tiếp với hồ sơ cũ, KHÔNG chặn sync).
    /// <para>
    /// SANITY CHECK phòng thủ (đây là thao tác PHÁ HỦY): CHỈ xóa khi <paramref name="dir"/> có một
    /// segment tên đúng <c>profiles</c>. Không thỏa (hoặc rỗng) → trả <c>false</c> NGAY, tuyệt đối
    /// KHÔNG đụng đĩa — tránh lỡ xóa thư mục cha/anh em.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> nếu thư mục đã được tạo lại sạch; <c>false</c> nếu bị chặn bởi sanity
    /// check hoặc không xóa được sau khi retry.</returns>
    public static bool TryResetDirectory(string dir, Action<string>? log = null, int attempts = 3, int delayMs = 300)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        // Phòng thủ: đường dẫn PHẢI chứa một segment tên đúng "profiles" (segment thật, không phải
        // substring). Không thỏa → KHÔNG xóa gì (bảo vệ chống lỡ tay xóa cha/anh em).
        if (!HasProfilesSegment(dir))
        {
            log?.Invoke($"Bỏ qua xóa hồ sơ: đường dẫn không nằm trong 'profiles' ({dir}).");
            return false;
        }

        var maxAttempts = Math.Max(1, attempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                Directory.CreateDirectory(dir);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                {
                    log?.Invoke($"Không xóa được hồ sơ sau {attempt} lần thử: {ex.Message}");
                    return false;
                }

                Thread.Sleep(Math.Max(0, delayMs));
            }
        }

        return false;
    }

    /// <summary>True nếu <paramref name="dir"/> có một segment tên đúng "profiles" (không phân biệt hoa/thường).</summary>
    private static bool HasProfilesSegment(string dir)
    {
        var segments = dir.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var seg in segments)
        {
            if (string.Equals(seg, "profiles", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
