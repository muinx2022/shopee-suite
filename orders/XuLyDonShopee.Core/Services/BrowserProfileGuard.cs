namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Dọn đường cho một hồ sơ trình duyệt (<c>--user-data-dir</c>) TRƯỚC khi phóng cửa sổ mới: giết mọi tiến trình
/// trình duyệt đang giữ hồ sơ đó (poll tới khi chết hẳn) rồi xóa session-restore + khóa Singleton.
/// <para>
/// Vì sao BẮT BUỘC: Chromium (Brave/Chrome/Edge) chạy cơ chế <i>process singleton</i> theo hồ sơ — phóng tiến
/// trình thứ hai vào ĐÚNG <c>--user-data-dir</c> đang có chủ thì dòng lệnh được chuyển cho tiến trình cũ (mở
/// thêm cửa sổ ở đó) còn tiến trình mới <b>thoát ngay lập tức</b>. Với đường CDP thì đó là chết vòng: không ai
/// ghi <c>DevToolsActivePort</c> nên không nối vào được ("Trình duyệt thoát ngay khi khởi động").
/// </para>
/// <para>
/// Tách khỏi <see cref="OrdersBridgeLauncher"/> (2026-08-06) vì đường đăng nhập Playwright
/// (<see cref="LoginBrowserBootstrap"/>) cần đúng bước dọn này — trước đó chỉ đường "trình duyệt sạch" có, nên
/// hễ còn sót cửa sổ của hồ sơ là vòng chết ngay ở bước đăng nhập.
/// </para>
/// <para><b>Best-effort NHƯNG có dấu vết:</b> mọi lỗi đều bị nuốt (không chặn bước phóng) — bù lại bước dọn
/// báo qua <c>log</c> khi có giết cửa sổ, khi CÒN SÓT sau khi dọn, hoặc khi chính nó hỏng. Bỏ log là để lần
/// chẩn đoán sau mù đúng như lần sinh ra lớp này.</para>
/// </summary>
internal static class BrowserProfileGuard
{
    /// <summary>Chuỗi nhận diện trình duyệt của cầu nối qua dòng lệnh (thư mục bản chép extension luôn chứa tên này).</summary>
    private const string BridgeExtensionMarker = "shopee-orders";

    /// <summary>Trần chờ tiến trình PowerShell dọn hồ sơ (bản thân script tự dừng sau ~3,2s).</summary>
    private const int TranChoDonMs = 10000;

    /// <summary>
    /// Giải phóng hồ sơ <paramref name="userDataDir"/>: kill mọi trình duyệt đang giữ nó (POLL tới khi chết hẳn)
    /// rồi xóa session-restore + khóa Singleton. Best-effort — nuốt mọi lỗi, không chặn bước phóng.
    /// </summary>
    /// <param name="alsoMatchBridgeExtension">true → giết THÊM mọi trình duyệt đang nạp extension
    /// <c>shopee-orders</c> dù khác hồ sơ (đường cầu nối cần: chúng cùng tranh cổng WS cố định 47821 và có thể
    /// còn <c>--remote-debugging-port</c> → bản sạch bị nhồi vào đó ⇒ Chi tiết dính captcha).
    /// false → CHỈ đụng đúng hồ sơ này (đường đăng nhập Playwright + dọn cuối vòng: không cướp cửa sổ của phiên khác).</param>
    /// <param name="log">Kênh nhật ký của phiên. null → im lặng (chỉ nên dùng ở nơi thật sự không có kênh log).</param>
    internal static void FreeProfile(string userDataDir, bool alsoMatchBridgeExtension, Action<string>? log = null)
    {
        KillBrowsersOnProfile(userDataDir, alsoMatchBridgeExtension, log);
        ClearProfileSessionAndLocks(userDataDir);
        XoaModelAiDaTai(userDataDir, log);
    }

    /// <summary>Xoá model AI on-device Chrome đã trót tải về hồ sơ (~4 GB/hồ sơ — xem
    /// <see cref="ProfileJanitor.XoaModelAiOnDevice"/>). Gọi SAU khi mọi trình duyệt của hồ sơ đã chết, nếu không
    /// thư mục còn bị khoá. Chỉ báo nhật ký khi thật sự giải phóng được (im lặng ở các vòng sau).</summary>
    private static void XoaModelAiDaTai(string userDataDir, Action<string>? log)
    {
        var freed = ProfileJanitor.XoaModelAiOnDevice(userDataDir, log);
        if (freed > 0)
        {
            // Format 2 chữ số thập phân: chia nguyên cho MB làm phần dưới 1 MB thành "~0 MB" (khó hiểu khi đọc log).
            var mb = freed / (1024d * 1024d);
            log?.Invoke($"Dọn hồ sơ trình duyệt: đã xoá model AI on-device Chrome tự tải (~{mb:0.##} MB).");
        }
    }

    /// <summary>
    /// Mệnh đề lọc tiến trình cho <c>Where-Object</c> (hàm THUẦN — test được không cần chạy PowerShell):
    /// tên tiến trình thuộc brave/chrome/msedge VÀ (dòng lệnh chứa hồ sơ [HOẶC chứa <c>shopee-orders</c> nếu bật cờ]).
    /// Đường dẫn được escape qua <see cref="EscapeLikePattern"/> (ký tự đại diện của <c>-like</c> + nháy đơn).
    /// </summary>
    internal static string BuildProcessFilter(string userDataDir, bool alsoMatchBridgeExtension)
    {
        var safe = EscapeLikePattern(userDataDir ?? string.Empty);
        var cmdLine = "$_.CommandLine -like '*" + safe + "*'";
        if (alsoMatchBridgeExtension)
        {
            cmdLine += " -or $_.CommandLine -like '*" + BridgeExtensionMarker + "*'";
        }
        return "$_.Name -in 'brave.exe','chrome.exe','msedge.exe' -and (" + cmdLine + ")";
    }

    /// <summary>
    /// Escape một đường dẫn để nhét vào mẫu <c>-like</c> nằm trong chuỗi nháy ĐƠN của PowerShell. Hai tầng, đúng
    /// thứ tự này:
    /// <list type="number">
    /// <item>ký tự đại diện của <c>-like</c>: <c>`</c> → <c>``</c> rồi <c>[</c> → <c>`[</c> (tên thư mục Windows
    /// được phép chứa cả hai; lọt vào là <c>[abc]</c> thành character class ⇒ mẫu lọc hỏng ⇒ KHÔNG giết được gì
    /// mà cũng chẳng ai báo lỗi). <c>*</c>/<c>?</c> không hợp lệ trong tên file Windows nên bỏ qua.</item>
    /// <item>nháy đơn của chuỗi PowerShell: <c>'</c> → <c>''</c> (không escape thì đứt chuỗi, lệnh hỏng).</item>
    /// </list>
    /// </summary>
    internal static string EscapeLikePattern(string value)
        => (value ?? string.Empty)
            .Replace("`", "``")
            .Replace("[", "`[")
            .Replace("'", "''");

    /// <summary>Kill mọi tiến trình trình duyệt khớp <see cref="BuildProcessFilter"/> VÀ POLL tới khi hết (tối đa
    /// ~3,2s), rồi đếm lại xem còn sót không. Vì sao POLL: kill là bất đồng bộ — phóng cửa sổ mới trong lúc tiến
    /// trình cũ chưa chết hẳn thì vẫn dính handoff (tiến trình mới thoát ngay). Windows-only (CIM), best-effort:
    /// nuốt lỗi nhưng BÁO qua <paramref name="log"/>.</summary>
    private static void KillBrowsersOnProfile(string userDataDir, bool alsoMatchBridgeExtension, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(userDataDir))
        {
            return;
        }
        try
        {
            var filter = BuildProcessFilter(userDataDir, alsoMatchBridgeExtension);
            // Vòng: liệt kê → nếu hết thì thoát; còn thì kill + chờ 400ms. Chạy tới 8 lần (~3.2s) để chắc chết hẳn.
            // Đếm PID KHÁC NHAU đã giết (một PID sống dai qua nhiều vòng vẫn chỉ tính 1) rồi đếm lại số còn sót —
            // "conlai>0" chính là tín hiệu bước dọn thất bại, phải hiện ra nhật ký.
            var cmd =
                "$ids = @{}; " +
                "for ($i=0; $i -lt 8; $i++) { " +
                "$ps = Get-CimInstance Win32_Process | Where-Object { " + filter + " }; " +
                "if (-not $ps) { break }; " +
                "$ps | ForEach-Object { $ids[$_.ProcessId] = 1; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; " +
                "Start-Sleep -Milliseconds 400 }; " +
                "$con = @(Get-CimInstance Win32_Process | Where-Object { " + filter + " }).Count; " +
                // Chỉ dùng nháy ĐƠN trong script: cả lệnh đi vào -Command "..." nên nháy kép ở đây là mời lỗi tách tham số.
                "Write-Output ('killed=' + $ids.Count + ';conlai=' + $con)";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"" + cmd + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                log?.Invoke("⚠ Dọn hồ sơ trình duyệt: không khởi chạy được PowerShell — bỏ bước dọn.");
                return;
            }

            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(TranChoDonMs))
            {
                // Hết giờ mà PowerShell còn sống → giết luôn, đừng để rò tiến trình mỗi vòng.
                try { p.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
                log?.Invoke($"⚠ Dọn hồ sơ trình duyệt: quá {TranChoDonMs / 1000}s chưa xong — đã hủy bước dọn, "
                          + "lần mở tới có thể lỗi \"thoát ngay khi khởi động\".");
                return;
            }

            BaoCaoKetQuaDon(stdout, log);
        }
        catch (Exception ex)
        {
            // best-effort — không chặn launch nếu dọn lỗi, nhưng PHẢI để lại dấu vết (ex.ToString(): đủ stack).
            log?.Invoke("⚠ Dọn hồ sơ trình duyệt lỗi (bỏ qua, vẫn mở tiếp): " + ex.ToString());
        }
    }

    /// <summary>
    /// Đọc dòng <c>killed=&lt;n&gt;;conlai=&lt;m&gt;</c> do script dọn in ra rồi báo nhật ký. Im lặng khi hồ sơ vốn
    /// đã rảnh (n=0, m=0) để không rác log mỗi vòng; nói khi có giết, và KÊU khi còn sót / không đọc được kết quả.
    /// Hàm thuần — test được không cần chạy PowerShell.
    /// </summary>
    internal static void BaoCaoKetQuaDon(string? stdout, Action<string>? log)
    {
        var (daGiet, conLai) = DocKetQuaDon(stdout);
        if (daGiet is null || conLai is null)
        {
            log?.Invoke("⚠ Dọn hồ sơ trình duyệt: không đọc được kết quả bước dọn (" + (stdout ?? string.Empty).Trim() + ").");
            return;
        }

        if (conLai > 0)
        {
            log?.Invoke($"⚠ Dọn hồ sơ trình duyệt: còn {conLai} tiến trình đang giữ hồ sơ sau khi dọn "
                      + $"(đã đóng {daGiet}) — lần mở tới có thể lỗi \"thoát ngay khi khởi động\".");
            return;
        }

        if (daGiet > 0)
        {
            log?.Invoke($"Dọn hồ sơ trình duyệt: đã đóng {daGiet} cửa sổ còn giữ hồ sơ trước khi mở phiên mới.");
        }
    }

    /// <summary>Tách cặp số từ chuỗi <c>killed=&lt;n&gt;;conlai=&lt;m&gt;</c>; sai định dạng → <c>(null, null)</c>.</summary>
    internal static (int? DaGiet, int? ConLai) DocKetQuaDon(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return (null, null);
        }

        foreach (var dong in stdout.Split('\n'))
        {
            var s = dong.Trim();
            if (!s.StartsWith("killed=", StringComparison.Ordinal))
            {
                continue;
            }
            var phan = s.Split(';');
            if (phan.Length == 2
                && int.TryParse(phan[0]["killed=".Length..], out var giet)
                && phan[1].StartsWith("conlai=", StringComparison.Ordinal)
                && int.TryParse(phan[1]["conlai=".Length..], out var con))
            {
                return (giet, con);
            }
        }
        return (null, null);
    }

    /// <summary>Xóa session-restore của hồ sơ (Current/Last Session|Tabs + thư mục Sessions) và các khóa Singleton —
    /// GỌI SAU khi mọi trình duyệt của hồ sơ đã chết. Tác dụng: (1) cửa sổ mới mở CHỈ start URL, KHÔNG khôi phục tab
    /// cũ (tránh tab shop cũ / tab CDP còn sót); (2) dọn trạng thái Singleton cũ.
    /// <para>⚠ ĐỪNG tưởng bước xóa <c>Singleton*</c> là thứ chống handoff trên Windows: process singleton của
    /// Chromium ở Windows dựa vào <b>cửa sổ ẩn + mutex khóa theo đường dẫn hồ sơ</b>, ba file kia là cơ chế POSIX.
    /// Thứ THẬT SỰ chống handoff ở Windows là bước KILL phía trên — bỏ kill mà chỉ xóa file là vô dụng.</para>
    /// KHÔNG xóa <c>Cookies</c> nên GIỮ đăng nhập. Best-effort.</summary>
    internal static void ClearProfileSessionAndLocks(string userDataDir)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
        {
            return;
        }
        try
        {
            var def = System.IO.Path.Combine(userDataDir, "Default");
            foreach (var f in new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs" })
            {
                try { System.IO.File.Delete(System.IO.Path.Combine(def, f)); } catch { /* bỏ qua */ }
            }
            try { System.IO.Directory.Delete(System.IO.Path.Combine(def, "Sessions"), true); } catch { /* bỏ qua */ }
            foreach (var s in new[] { "SingletonLock", "SingletonCookie", "SingletonSocket" })
            {
                try { System.IO.File.Delete(System.IO.Path.Combine(userDataDir, s)); } catch { /* bỏ qua */ }
            }
        }
        catch { /* bỏ qua */ }
    }
}
