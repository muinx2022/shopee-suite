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

    /// <summary>
    /// Xoá thư mục model AI on-device mà Chrome tự tải về GỐC hồ sơ (<c>OptGuideOnDeviceModel</c>) — đo
    /// 07/08/2026: <b>3,98 GB mỗi hồ sơ</b>, hai hồ sơ đã ăn ~8 GB. Việc TẢI đã bị chặn bằng cờ dòng lệnh
    /// (<see cref="Shopee.Toolkit.Browser.BraveArgs.OnDeviceAiModelFeatures"/>); hàm này dọn phần đã trót tải
    /// ở các hồ sơ có từ trước.
    /// <para>Chỉ đụng ĐÚNG thư mục con đó — KHÔNG xoá hồ sơ, KHÔNG đụng cookie/đăng nhập. Best-effort: mọi lỗi
    /// bị nuốt (thư mục đang bị khoá thì lần dọn sau xoá tiếp), không ném.</para>
    /// <para>SANITY CHECK phòng thủ giống <see cref="TryResetDirectory"/> (cùng là thao tác PHÁ HỦY): chỉ dọn khi
    /// <paramref name="userDataDir"/> có một segment tên đúng <c>profiles</c> — mọi hồ sơ thật đều nằm ở
    /// <c>&lt;baseDir&gt;/profiles/&lt;id&gt;-&lt;kind&gt;</c> (xem <see cref="BrowserProfilePaths.ForAccount"/>), nên luật này
    /// không cắt mất trường hợp nào mà chỉ chặn đường dẫn rỗng/tương đối/trỏ nhầm.</para>
    /// </summary>
    /// <returns>Số byte ước tính đã giải phóng (0 nếu không có gì để xoá / xoá không được).</returns>
    public static long XoaModelAiOnDevice(string userDataDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(userDataDir) || !HasProfilesSegment(userDataDir))
        {
            return 0;
        }

        try
        {
            var dir = Path.Combine(userDataDir, Shopee.Toolkit.Browser.BraveArgs.OnDeviceAiModelDirName);
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            var size = DoKichThuoc(dir);
            Directory.Delete(dir, recursive: true);
            return size;
        }
        catch (Exception ex)
        {
            log?.Invoke("Không xoá được model AI on-device của hồ sơ (bỏ qua, dọn lại lần sau): " + ex.Message);
            return 0;
        }
    }

    /// <summary>Tổng kích thước file trong một thư mục (best-effort — file lỗi/khoá thì bỏ qua).</summary>
    private static long DoKichThuoc(string dir)
    {
        long size = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { /* bỏ qua file lẻ */ }
            }
        }
        catch { /* bỏ qua */ }
        return size;
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
