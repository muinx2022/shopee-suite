using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// <b>Mở TRÌNH DUYỆT SẠCH của phiên cầu nối</b> (không CDP, không remote-debugging-port) kèm bản chép mới của
/// extension <c>shopee-orders</c>. Tách khỏi <see cref="OrdersBridgeSession"/> (đợt dọn 2026-07-30): toàn bộ phần
/// này là thao tác HỆ ĐIỀU HÀNH (chép thư mục, kill tiến trình, xóa khóa hồ sơ) — không dính vòng đời chặng/WS.
/// </summary>
internal static class OrdersBridgeLauncher
{
    /// <summary>
    /// Dọn đường rồi mở trình duyệt sạch tại <paramref name="startUrl"/>. Thứ tự BẮT BUỘC: chép extension ra thư
    /// mục mới → kill mọi trình duyệt của hồ sơ (POLL tới khi chết hẳn) → xóa session-restore + khóa Singleton →
    /// mở. Đảo thứ tự là dính "single-instance handoff" vào tiến trình Playwright còn CDP ⇒ Chi tiết ăn captcha.
    /// </summary>
    public static System.Diagnostics.Process? Launch(string userDataDir, BrowserChoice browserChoice, string startUrl,
        Action<string>? log = null)
    {
        var srcExt = BraveLaunchArgs.ResolveOrdersBridgeExtension()
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension 'shopee-orders' (cạnh app hoặc trong repo). " +
                "Cầu nối cần extension này để nối WebSocket + bắn trusted input.");

        // CHÉP extension ra thư mục MỚI (GUID) mỗi lần chạy rồi nạp bản chép. Vì sao: Brave/Chrome CACHE service
        // worker (MV3) theo extension ID (= hash đường dẫn) trong hồ sơ persistent — nạp lại CÙNG đường dẫn vẫn có
        // thể chạy SW CŨ dù file đã đổi (đã kiểm chứng: reload ext tay mới ăn code mới). Đường dẫn MỚI ⇒ ID mới ⇒ SW
        // mới tinh, luôn đúng code. Tên thư mục vẫn chứa 'shopee-orders' để KillBrowsersOnProfile nhận diện.
        var extPath = PrepareFreshExtensionCopy(srcExt);

        // Kill MỌI trình duyệt của cầu nối (theo hồ sơ HOẶC đang nạp 'shopee-orders') + POLL tới khi chết hẳn, rồi
        // xóa session-restore + khóa Singleton TRƯỚC khi mở bản sạch: chống "single-instance handoff" vào tiến trình
        // Playwright login còn CDP (→ Chi tiết captcha) + orphan cùng nối cổng cố định 47821 cướp lệnh.
        BrowserProfileGuard.FreeProfile(userDataDir, alsoMatchBridgeExtension: true, log);

        return PocCleanLauncher.Open(userDataDir, browserChoice, startUrl, extPath);
    }

    /// <summary>Chép thư mục extension <paramref name="srcDir"/> ra một thư mục MỚI (GUID) dưới temp và trả về
    /// đường dẫn bản chép. Mục đích: đường dẫn mới ⇒ Brave cấp extension ID mới ⇒ service worker MV3 mới tinh (không
    /// dính SW cache của hồ sơ persistent). Dọn các bản chép cũ trước (best-effort) để không tích tụ.
    /// Chép ĐỆ QUY cả thư mục con: <c>background.js</c> là ES module <c>import</c> từ <c>./shared/*.js</c> — thiếu
    /// <c>shared/</c> thì service worker chết ngay lúc nạp ⇒ extension KHÔNG BAO GIỜ gửi <c>ready</c> ⇒ cầu nối
    /// treo hết 45s. Đừng rút về chỉ-chép-file-top-level.</summary>
    private static string PrepareFreshExtensionCopy(string srcDir)
    {
        var baseDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shopee-orders-bridge");
        try
        {
            if (System.IO.Directory.Exists(baseDir))
            {
                foreach (var d in System.IO.Directory.GetDirectories(baseDir))
                {
                    try { System.IO.Directory.Delete(d, true); } catch { /* bản đang bị 1 Brave khác giữ — bỏ qua */ }
                }
            }
        }
        catch { /* bỏ qua */ }

        var dest = System.IO.Path.Combine(baseDir, "shopee-orders-" + System.Guid.NewGuid().ToString("N"));
        CopyDirectory(srcDir, dest);
        return dest;
    }

    /// <summary>Chép TOÀN BỘ cây thư mục <paramref name="srcDir"/> sang <paramref name="destDir"/> (tạo thư mục con
    /// theo đúng cấu trúc). Dùng cho bản chép extension — phải giữ nguyên <c>shared/</c> để ES module import được.</summary>
    internal static void CopyDirectory(string srcDir, string destDir)
    {
        System.IO.Directory.CreateDirectory(destDir);
        foreach (var f in System.IO.Directory.GetFiles(srcDir))
        {
            System.IO.File.Copy(f, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(f)), true);
        }
        foreach (var d in System.IO.Directory.GetDirectories(srcDir))
        {
            CopyDirectory(d, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(d)));
        }
    }

}
