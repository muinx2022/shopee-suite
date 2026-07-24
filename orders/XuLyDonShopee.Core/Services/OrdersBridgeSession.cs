using System.Text.Json;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Kết quả một "lát cắt kiểm chứng" của cầu nối extension↔C#: đọc danh sách shop → mở "Chi tiết"
/// shop đầu (trusted click, kỳ vọng KHÔNG captcha) → đọc số "Chờ Lấy Hàng".
/// </summary>
/// <param name="Shops">Danh sách shop parse từ bảng <c>/portal/shop</c> (rỗng nếu chưa/không đọc được).</param>
/// <param name="FirstShopId">Mã shop đầu đã thử mở "Chi tiết" (null nếu không có shop nào).</param>
/// <param name="ToShipCount">Số "Chờ Lấy Hàng" đọc được ở shop đầu (null nếu không đọc được).</param>
/// <param name="Captcha">True nếu extension báo rơi vào trang verify/captcha.</param>
/// <param name="Error">Thông báo lỗi (null nếu chạy trọn lát cắt không lỗi).</param>
/// <param name="OrdersCount">GĐ3: số đơn đọc được từ tab "Tất cả" (0 nếu không đọc).</param>
/// <param name="SlipsSaved">GĐ3: số phiếu giao PDF đã lưu (Phần B; 0 nếu không xử đơn).</param>
public sealed record OrdersBridgeSliceResult(
    IReadOnlyList<ShopListItem> Shops,
    string? FirstShopId,
    int? ToShipCount,
    bool Captcha,
    string? Error,
    int OrdersCount = 0,
    int SlipsSaved = 0);

/// <summary>Tham số đăng nhập cho <see cref="OrdersBridgeSession.RunLoginThenSliceAsync"/> (GĐ2).</summary>
/// <param name="User">Tên đăng nhập subaccount (= <c>acc.Email</c> ở luồng production).</param>
/// <param name="Pass">Mật khẩu subaccount (= <c>acc.Password</c>).</param>
/// <param name="VerifyEmail">Hotmail/Outlook để đọc mã xác thực (có thể rỗng → không mở hộp thư).</param>
/// <param name="VerifyEmailPassword">Mật khẩu hộp thư.</param>
public sealed record OrdersLoginParams(
    string User, string Pass, string? VerifyEmail, string? VerifyEmailPassword);

/// <summary>
/// Vòng đời MỘT phiên cầu nối: cấp cổng loopback trống → chạy <see cref="OrdersWebSocketServer"/> →
/// mở trình duyệt SẠCH (không CDP, không remote-debugging-port) qua <see cref="PocCleanLauncher"/> với
/// <c>startUrl</c> có hash <c>#_od_ws=&lt;port&gt;</c> để extension đọc cổng → chờ extension báo <c>ready</c>.
/// <list type="bullet">
/// <item><see cref="RunSliceAsync"/> (GĐ1): mở thẳng <c>/portal/shop</c> (user đã đăng nhập tay) → chạy lát cắt.</item>
/// <item><see cref="RunLoginThenSliceAsync"/> (GĐ2 pivot): đăng nhập bằng trình duyệt điều khiển Playwright
/// (tái dùng luồng production) → đóng → mở lại bằng trình duyệt sạch + extension → lát cắt Seller Centre.</item>
/// </list>
/// Parse dữ liệu qua các hàm THUẦN sẵn có (<see cref="ShopeeLoginService.ParseShopListJson"/>,
/// <see cref="ShopeeDashboard.ParseToShipCount"/>).
/// <para>
/// Message đến (WebSocket) xử lý ĐỒNG BỘ trong handler — rút mọi giá trị cần thiết ra (dạng chuỗi) rồi mới đẩy
/// vào <see cref="TaskCompletionSource"/>, KHÔNG giữ tham chiếu <see cref="JsonDocument"/> qua ranh giới async.
/// </para>
/// <para>Một phiên/lần test (chưa đa-lane). Cấp cổng đơn giản bằng <see cref="TcpListener"/> port 0.</para>
/// </summary>
public sealed class OrdersBridgeSession : IDisposable
{
    private readonly string _userDataDir;
    private readonly BrowserChoice _browserChoice;
    private readonly Action<string>? _log;
    private readonly string? _invoiceDir;
    private readonly string _province;

    private OrdersWebSocketServer? _ws;

    /// <summary>GĐ3: kết quả extension "chuẩn bị hàng" 1 đơn (mã đơn + URL tab phiếu). null qua TCS = hết đơn.</summary>
    private sealed record PrepareResult(string OrderCode, string SlipTabUrl, string SlipBase64);

    // Cờ hoàn tất từng chặng — tạo mới mỗi lần chạy; RunContinuationsAsynchronously để continuation KHÔNG chạy
    // trên thread nhận WebSocket (tránh nghẽn vòng nhận / deadlock).
    private TaskCompletionSource<bool> _readyTcs = NewTcs<bool>();
    private TaskCompletionSource<bool> _atSellerTcs = NewTcs<bool>();          // bản sạch: SSO về trang chọn shop
    private TaskCompletionSource<string?> _shopListTcs = NewTcs<string?>();
    private TaskCompletionSource<string> _detailTcs = NewTcs<string>();        // "ok" | "captcha"
    private TaskCompletionSource<string?> _toShipTcs = NewTcs<string?>();
    private TaskCompletionSource<string?> _ordersTcs = NewTcs<string?>();      // GĐ3: JSON mảng đơn
    private TaskCompletionSource<bool> _pickupTcs = NewTcs<bool>();            // GĐ3: đặt địa chỉ lấy hàng xong
    private TaskCompletionSource<bool> _pickupOtherTcs = NewTcs<bool>();       // GĐ3: set địa chỉ VỀ địa chỉ khác xong
    private TaskCompletionSource<PrepareResult?> _prepareTcs = NewTcs<PrepareResult?>(); // GĐ3: 1 đơn (null=hết)

    private bool _captchaSeen;

    /// <summary>Tiến trình trình duyệt sạch đã mở (để tầng UI theo dõi/kill). Set ngay sau khi launch.</summary>
    public System.Diagnostics.Process? Process { get; private set; }

    /// <param name="invoiceDir">GĐ3 Phần B: thư mục lưu phiếu giao PDF; null/rỗng → chỉ đọc đơn, không tải phiếu.</param>
    /// <param name="province">GĐ3 Phần B: tỉnh của địa chỉ lấy hàng cần đặt (mặc định "Thanh Hóa").</param>
    public OrdersBridgeSession(string userDataDir, BrowserChoice browserChoice, Action<string>? log = null,
        string? invoiceDir = null, string? province = null)
    {
        _userDataDir = userDataDir;
        _browserChoice = browserChoice;
        _log = log;
        _invoiceDir = invoiceDir;
        _province = string.IsNullOrWhiteSpace(province) ? "Thanh Hóa" : province;
    }

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void L(string m) => _log?.Invoke(m);

    private void ResetTcs()
    {
        _readyTcs = NewTcs<bool>();
        _atSellerTcs = NewTcs<bool>();
        _shopListTcs = NewTcs<string?>();
        _detailTcs = NewTcs<string>();
        _toShipTcs = NewTcs<string?>();
        _ordersTcs = NewTcs<string?>();
        _pickupTcs = NewTcs<bool>();
        _pickupOtherTcs = NewTcs<bool>();
        _prepareTcs = NewTcs<PrepareResult?>();
        _captchaSeen = false;
    }

    /// <summary>Cổng cầu nối CỐ ĐỊNH — extension dùng cổng này khi hash <c>#_od_ws</c> bị rụng lúc Shopee redirect
    /// trang đăng nhập (khớp <c>DEFAULT_PORT</c> trong extension). Một phiên/lần test nên cố định là đủ.</summary>
    private const int BridgePort = 47821;

    // ── Khởi động cầu + mở trình duyệt sạch tại startUrl (kèm hash cổng WS) ─────────────────────────────
    private void StartBridgeAndLaunch(string baseUrl)
    {
        // Bind cổng cố định; phiên trước vừa đóng có thể chưa nhả hẳn → retry vài nhịp.
        OrdersWebSocketServer? ws = null;
        for (var attempt = 0; attempt < 5 && ws is null; attempt++)
        {
            try { var s = new OrdersWebSocketServer(BridgePort); s.Start(); ws = s; }
            catch when (attempt < 4) { System.Threading.Thread.Sleep(400); }
        }
        _ws = ws ?? throw new InvalidOperationException(
            $"Không mở được cổng cầu nối {BridgePort} (đang bận? đóng phiên cũ rồi thử lại).");
        _ws.MessageReceived += OnMessage;
        L($"Cầu nối: WebSocket lắng nghe ws://localhost:{BridgePort} — mở trình duyệt sạch...");

        // Vẫn nhúng hash (extension đọc nếu còn) nhưng KHÔNG phụ thuộc: mất hash → extension dùng cổng cố định.
        var startUrl = $"{baseUrl}#_od_ws={BridgePort}";
        var srcExt = BraveLaunchArgs.ResolveOrdersBridgeExtension()
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension 'shopee-orders' (cạnh app hoặc trong repo). " +
                "Cầu nối cần extension này để nối WebSocket + bắn trusted input.");

        // CHÉP extension ra thư mục MỚI (GUID) mỗi lần chạy rồi nạp bản chép. Vì sao: Brave/Chrome CACHE service
        // worker (MV3) theo extension ID (= hash đường dẫn) trong hồ sơ persistent — nạp lại CÙNG đường dẫn vẫn có
        // thể chạy SW CŨ dù file đã đổi (đã kiểm chứng: reload ext tay mới ăn code mới). Đường dẫn MỚI ⇒ ID mới ⇒ SW
        // mới tinh, luôn đúng code. Tên thư mục vẫn chứa 'shopee-orders' để KillBrowsersOnProfile nhận diện.
        var extPath = PrepareFreshExtensionCopy(srcExt);

        // Kill MỌI trình duyệt của cầu nối (theo hồ sơ HOẶC đang nạp 'shopee-orders') + POLL tới khi chết hẳn TRƯỚC
        // khi mở bản sạch: chống "single-instance handoff" vào tiến trình Playwright login còn CDP (→ Chi tiết captcha)
        // + orphan cùng nối cổng cố định 47821 cướp lệnh.
        KillBrowsersOnProfile(_userDataDir);

        // Sau khi mọi trình duyệt đã chết: xóa session-restore (đóng tab cũ) + khóa Singleton (chống handoff). Giữ Cookies.
        ClearProfileSessionAndLocks(_userDataDir);

        Process = PocCleanLauncher.Open(_userDataDir, _browserChoice, startUrl, extPath);
    }

    /// <summary>Chép thư mục extension <paramref name="srcDir"/> ra một thư mục MỚI (GUID) dưới temp và trả về
    /// đường dẫn bản chép. Mục đích: đường dẫn mới ⇒ Brave cấp extension ID mới ⇒ service worker MV3 mới tinh (không
    /// dính SW cache của hồ sơ persistent). Dọn các bản chép cũ trước (best-effort) để không tích tụ. Chỉ chép file
    /// top-level (manifest.json/background.js/content.js — extension không có thư mục con).</summary>
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
        System.IO.Directory.CreateDirectory(dest);
        foreach (var f in System.IO.Directory.GetFiles(srcDir))
        {
            System.IO.File.Copy(f, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(f)), true);
        }
        return dest;
    }

    /// <summary>Kill mọi tiến trình trình duyệt (brave/chrome/msedge) có <paramref name="userDataDir"/> HOẶC đang nạp
    /// 'shopee-orders' trong dòng lệnh, VÀ POLL tới khi hết (tối đa ~5s). Vì sao POLL: trình duyệt Playwright login có
    /// <c>--remote-debugging-port</c> — nếu còn sống lúc mở bản sạch, Brave single-instance sẽ NHỒI bản sạch vào tiến
    /// trình còn CDP đó ⇒ Chi tiết DÍNH CAPTCHA. Phải chắc chết hẳn mới mở. Windows-only (CIM), best-effort.</summary>
    private static void KillBrowsersOnProfile(string userDataDir)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(userDataDir))
        {
            return;
        }
        try
        {
            var safe = userDataDir.Replace("'", "''");
            var filter =
                "$_.Name -in 'brave.exe','chrome.exe','msedge.exe' -and " +
                "($_.CommandLine -like '*" + safe + "*' -or $_.CommandLine -like '*shopee-orders*')";
            // Vòng: liệt kê → nếu hết thì thoát; còn thì kill + chờ 400ms. Chạy tới 8 lần (~3.2s) để chắc chết hẳn.
            var cmd =
                "for ($i=0; $i -lt 8; $i++) { " +
                "$ps = Get-CimInstance Win32_Process | Where-Object { " + filter + " }; " +
                "if (-not $ps) { break }; " +
                "$ps | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; " +
                "Start-Sleep -Milliseconds 400 }";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"" + cmd + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch { /* best-effort — không chặn launch nếu dọn lỗi */ }
    }

    /// <summary>Xóa session-restore của hồ sơ (Current/Last Session|Tabs + thư mục Sessions) và các khóa Singleton —
    /// GỌI SAU khi mọi trình duyệt của hồ sơ đã chết. Tác dụng: (1) bản sạch mở CHỈ start URL, KHÔNG khôi phục tab cũ
    /// (tránh tab shop cũ / tab CDP còn sót); (2) xóa SingletonLock/Cookie/Socket chống "handoff" vào tiến trình cũ.
    /// KHÔNG xóa Cookies nên GIỮ đăng nhập. Best-effort.</summary>
    private static void ClearProfileSessionAndLocks(string userDataDir)
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

    // ── GĐ1: user đã đăng nhập tay tới /portal/shop → chạy lát cắt ──────────────────────────────────────
    /// <summary>
    /// Chạy lát cắt kiểm chứng GĐ1 (mở thẳng <c>/portal/shop</c>). Ngoại lệ không mong đợi được bọc thành
    /// <see cref="OrdersBridgeSliceResult.Error"/>; riêng <see cref="OperationCanceledException"/> ném ra ngoài.
    /// </summary>
    public async Task<OrdersBridgeSliceResult> RunSliceAsync(CancellationToken ct = default)
    {
        ResetTcs();
        StartBridgeAndLaunch(ShopeeLoginService.ShopListUrl);
        try
        {
            L("Chờ extension nối cầu (ready) — tối đa 45s...");
            await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            L("Extension đã nối cầu.");
            return await RunSliceCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            L("Cầu nối: hết thời gian chờ phản hồi từ extension.");
            return Fail("Hết thời gian chờ phản hồi từ extension (extension chưa nối / trang chưa sẵn sàng).");
        }
        catch (Exception ex)
        {
            L("Cầu nối lỗi: " + ex.Message);
            return Fail(ex.Message);
        }
    }

    // ── GĐ2 (pivot): đăng nhập bằng Playwright (an toàn — subaccount + /portal/shop KHÔNG bị captcha) → đóng
    //    → mở lại bằng trình duyệt SẠCH + extension để đọc Seller Centre (chỉ "Chi tiết" mới dính captcha). ──────
    /// <summary>
    /// GĐ2: đăng nhập Nền tảng tài khoản phụ bằng <b>trình duyệt điều khiển Playwright/CDP CŨ</b> (tái dùng NGUYÊN
    /// <see cref="ShopeeLoginService.OpenAsync"/> + <c>TryLoginSubaccountAsync</c> — tự điền form, mở hộp thư cho user
    /// đọc mã, chờ mã, SSO tới Seller Centre). Đăng nhập xong thì ĐÓNG trình duyệt điều khiển (nhả khoá hồ sơ), rồi
    /// mở lại bằng <b>trình duyệt SẠCH + extension</b> qua <see cref="RunSliceAsync"/> (hồ sơ đã đăng nhập nên vào
    /// thẳng <c>/portal/shop</c>) → đọc shop → "Chi tiết" (trusted click, né captcha) → "Chờ Lấy Hàng".
    /// KHÔNG tự nhập mã hộ (mã là thao tác tay). Hủy giữa chừng → đóng cả trình duyệt điều khiển (finally) lẫn sạch.
    /// </summary>
    public async Task<OrdersBridgeSliceResult> RunLoginThenSliceAsync(OrdersLoginParams login, CancellationToken ct = default)
    {
        // 1) Đăng nhập bằng trình duyệt điều khiển (Playwright). Bọc try/finally để user Dừng giữa chừng (ct hủy →
        //    TryLoginSubaccountAsync ném OperationCanceledException) thì trình duyệt điều khiển VẪN được đóng (không mồ côi).
        var entered = false;
        ILoginSession? session = null;
        try
        {
            L("Đăng nhập Nền tảng tài khoản phụ bằng trình duyệt điều khiển (Playwright)...");
            var svc = new ShopeeLoginService();
            session = await svc.OpenAsync(_userDataDir, null /* proxy */, _browserChoice, ct).ConfigureAwait(false);
            entered = await session.TryLoginSubaccountAsync(
                login.User, login.Pass, login.VerifyEmail, login.VerifyEmailPassword, _log, ct).ConfigureAwait(false);
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            }
        }

        if (!entered)
        {
            return Fail("Đăng nhập subaccount chưa xong (nhập mã?). Bấm lại để thử tiếp.");
        }

        // 2) Settle ngắn cho chắc nhả khoá file hồ sơ (Brave vừa kill) trước khi mở lại bằng trình duyệt sạch.
        await Task.Delay(800, ct).ConfigureAwait(false);

        // 3) Mở lại bằng trình duyệt SẠCH + extension tại trang TÀI KHOẢN subaccount (/account, đã đăng nhập nhờ
        //    cookie hồ sơ) → extension SSO "Kênh Người bán" → về trang CHỌN SHOP (/portal/shop, picker). KHÔNG mở
        //    thẳng /portal/shop vì Shopee sticky-redirect vào shop mở lần trước (server-side) → không tới picker.
        L("Đăng nhập xong — mở lại bằng trình duyệt sạch + extension (subaccount /account → SSO → trang chọn shop)...");
        ResetTcs();
        StartBridgeAndLaunch(ShopeeLoginService.SubaccountAccountUrl);
        try
        {
            L("Chờ extension nối cầu (ready) — tối đa 45s...");
            await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            L("Extension đã nối cầu — SSO 'Kênh Người bán' để về trang chọn shop...");

            _atSellerTcs = NewTcs<bool>();
            await _ws!.SendAsync(new { action = "gotoSellerCentre" }).ConfigureAwait(false);
            var atSeller = await _atSellerTcs.Task.WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
            if (_captchaSeen)
            {
                L("PHÁT HIỆN captcha/verify khi vào Seller Centre.");
                return new OrdersBridgeSliceResult(Array.Empty<ShopListItem>(), null, null, true,
                    "Rơi vào trang verify/captcha khi vào Seller Centre.");
            }
            if (!atSeller)
            {
                return Fail("Không về được trang chọn shop (/portal/shop) sau SSO — có thể sticky shop cũ / cookie hết hạn.");
            }

            L("Đã về trang chọn shop — đọc shop...");
            return await RunSliceCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            L("Cầu nối: hết thời gian chờ phản hồi từ extension (SSO Seller Centre).");
            return Fail("Hết thời gian chờ phản hồi từ extension khi SSO Seller Centre.");
        }
        catch (Exception ex)
        {
            L("Cầu nối lỗi: " + ex.Message);
            return Fail(ex.Message);
        }
    }

    // Lát cắt dùng chung (GĐ1 + đuôi GĐ2): readShopList → openShopDetail(shop đầu) → readToShip.
    private async Task<OrdersBridgeSliceResult> RunSliceCoreAsync(CancellationToken ct)
    {
        // 1) Đọc danh sách shop.
        await _ws!.SendAsync(new { action = "readShopList" }).ConfigureAwait(false);
        var shopListJson = await _shopListTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var shops = ShopeeLoginService.ParseShopListJson(shopListJson);
        L($"Đọc được {shops.Count} shop từ /portal/shop.");
        if (shops.Count == 0)
        {
            return new OrdersBridgeSliceResult(shops, null, null, false,
                "Không đọc được shop nào (đã tới /portal/shop chưa?).");
        }

        // 2) Mở "Chi tiết" shop đầu bằng trusted click (kỳ vọng KHÔNG captcha).
        var firstShopId = shops[0].ShopId;
        L($"Mở 'Chi tiết' shop đầu (id={firstShopId}) bằng trusted click...");
        await _ws.SendAsync(new { action = "openShopDetail", shopId = firstShopId }).ConfigureAwait(false);
        var detail = await _detailTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        if (detail == "captcha" || _captchaSeen)
        {
            L("PHÁT HIỆN captcha/verify khi mở Chi tiết — cần soi lại.");
            return new OrdersBridgeSliceResult(shops, firstShopId, null, true,
                "Rơi vào trang verify/captcha khi mở Chi tiết.");
        }
        L("Đã mở tab shop (không captcha).");

        // 3) Đọc số "Chờ Lấy Hàng".
        await _ws.SendAsync(new { action = "readToShip" }).ConfigureAwait(false);
        var raw = await _toShipTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var toShip = ShopeeDashboard.ParseToShipCount(raw);
        L($"Số 'Chờ Lấy Hàng' đọc được: {(toShip?.ToString() ?? "null")} (raw='{raw}').");

        // 4) GĐ3: đọc đơn (Phần A) + nếu ToShip>0 thì xử đơn (Phần B).
        var (ordersCount, slipsSaved) = await RunShopOrdersAsync(toShip ?? 0, ct).ConfigureAwait(false);
        if (_captchaSeen)
        {
            return new OrdersBridgeSliceResult(shops, firstShopId, toShip, true,
                "Rơi vào trang verify/captcha khi đọc/xử đơn.", ordersCount, slipsSaved);
        }

        return new OrdersBridgeSliceResult(shops, firstShopId, toShip, false, null, ordersCount, slipsSaved);
    }

    // ── GĐ3: đọc đơn (Phần A) + xử đơn (Phần B) trên tab shop đang mở ───────────────────────────────────
    private async Task<(int Orders, int Slips)> RunShopOrdersAsync(int toShip, CancellationToken ct)
    {
        // Phần A — đọc đơn tab "Tất cả" (test được ngay, kể cả shop 0 đơn chờ).
        _ordersTcs = NewTcs<string?>();
        await _ws!.SendAsync(new { action = "syncOrders" }).ConfigureAwait(false);
        var ordersJson = await _ordersTcs.Task.WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (_captchaSeen)
        {
            L("PHÁT HIỆN captcha khi đọc đơn.");
            return (0, 0);
        }
        var orders = ShopeeLoginService.ParseOrdersJson(ordersJson);
        L($"Đọc được {orders.Count} đơn (Tất cả).");

        // Phần B — chỉ khi có đơn Chờ Lấy Hàng VÀ có thư mục lưu phiếu.
        var slips = 0;
        if (toShip > 0 && !string.IsNullOrWhiteSpace(_invoiceDir))
        {
            L($"Có {toShip} đơn Chờ Lấy Hàng — đặt địa chỉ lấy hàng ({_province}) rồi xử từng đơn...");
            _pickupTcs = NewTcs<bool>();
            await _ws.SendAsync(new { action = "setPickupAddress", province = _province }).ConfigureAwait(false);
            var pickupOk = await _pickupTcs.Task.WaitAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            if (_captchaSeen)
            {
                L("PHÁT HIỆN captcha khi đặt địa chỉ lấy hàng.");
                return (orders.Count, 0);
            }
            if (!pickupOk)
            {
                L("Không đặt được địa chỉ lấy hàng — vẫn thử xử đơn (phiếu có thể sai địa chỉ).");
            }

            // Lặp Chuẩn bị hàng tới khi hết đơn / chốt chặn 50 đơn / captcha.
            var guard = 0;
            while (guard++ < 50)
            {
                ct.ThrowIfCancellationRequested();
                _prepareTcs = NewTcs<PrepareResult?>();
                await _ws.SendAsync(new { action = "prepareNextOrder", invoiceDir = _invoiceDir }).ConfigureAwait(false);
                var prep = await _prepareTcs.Task.WaitAsync(TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
                if (_captchaSeen)
                {
                    L("PHÁT HIỆN captcha khi xử đơn — dừng.");
                    break;
                }
                if (prep is null)
                {
                    L("Hết đơn cần Chuẩn bị hàng.");
                    break;
                }

                var saved = TrySaveSlip(prep.SlipBase64, prep.OrderCode, _invoiceDir!);
                if (saved) slips++;
                L($"Đã chuẩn bị đơn {prep.OrderCode} — {(saved ? "lưu phiếu OK" : "CHƯA lưu được phiếu (kiểm tra tay)")}.");
            }
            L($"Xử đơn xong: {slips} phiếu đã lưu.");

            // Hết đơn → set địa chỉ lấy hàng VỀ ĐỊA CHỈ KHÁC (giữ tag "trả hàng" ở địa chỉ mặc định) — hoàn tất 1 flow shop.
            L("Set địa chỉ lấy hàng về địa chỉ khác (hoàn tất flow shop)...");
            _pickupOtherTcs = NewTcs<bool>();
            await _ws.SendAsync(new { action = "setPickupAddressToOther" }).ConfigureAwait(false);
            try { await _pickupOtherTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (TimeoutException) { L("Set địa chỉ khác: quá hạn — bỏ qua."); }
        }

        return (orders.Count, slips);
    }

    /// <summary>Ghi phiếu giao PDF từ <paramref name="slipBase64"/> (extension đã fetch NGAY TRONG tab awbprint —
    /// có cookie, same-origin blob — nên KHÔNG dùng HttpClient GET vô cookie như bản cũ). Kiểm magic <c>%PDF</c> rồi
    /// lưu <c>&lt;dir&gt;/&lt;SanitizeFileName(orderCode)&gt;.pdf</c>. Best-effort — mọi lỗi/không phải PDF → false.</summary>
    private static bool TrySaveSlip(string? slipBase64, string orderCode, string dir)
    {
        if (string.IsNullOrWhiteSpace(slipBase64))
        {
            return false;
        }
        try
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(slipBase64); } catch { return false; }
            // Magic %PDF (0x25 0x50 0x44 0x46) — tránh lưu HTML/rác thành .pdf.
            if (bytes.Length < 4 || bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46)
            {
                return false;
            }
            System.IO.Directory.CreateDirectory(dir);
            var name = ShopeeShippingNav.SanitizeFileName(orderCode) + ".pdf";
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), bytes);
            return true;
        }
        catch { return false; }
    }

    private OrdersBridgeSliceResult Fail(string message) =>
        new(Array.Empty<ShopListItem>(), null, null, _captchaSeen, message);

    // Xử lý ĐỒNG BỘ: rút mọi giá trị (chuỗi) ra khỏi doc NGAY, rồi mới hoàn tất TCS. Dispose doc ở cuối.
    private void OnMessage(JsonDocument doc)
    {
        try
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var actEl))
            {
                return;
            }
            var action = actEl.GetString();
            switch (action)
            {
                case "ready":
                    _readyTcs.TrySetResult(true);
                    break;

                case "atSellerCentre":
                    _atSellerTcs.TrySetResult(true);
                    break;

                case "shopOpened":
                    _detailTcs.TrySetResult("ok");
                    break;

                case "pageData":
                {
                    var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    var data = ReadDataAsString(root);
                    if (kind == "shopList")
                    {
                        _shopListTcs.TrySetResult(data);
                    }
                    else if (kind == "toShip")
                    {
                        _toShipTcs.TrySetResult(data);
                    }
                    else if (kind == "orders")
                    {
                        _ordersTcs.TrySetResult(data);
                    }
                    break;
                }

                case "pickupDone":
                {
                    var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    _pickupTcs.TrySetResult(ok);
                    break;
                }

                case "pickupOtherDone":
                {
                    var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    _pickupOtherTcs.TrySetResult(ok);
                    break;
                }

                case "orderPrepared":
                {
                    var code = root.TryGetProperty("orderCode", out var oc) ? (oc.GetString() ?? string.Empty) : string.Empty;
                    var slip = root.TryGetProperty("slipTabUrl", out var su) ? (su.GetString() ?? string.Empty) : string.Empty;
                    var b64 = root.TryGetProperty("slipBase64", out var sb) ? (sb.GetString() ?? string.Empty) : string.Empty;
                    _prepareTcs.TrySetResult(new PrepareResult(code, slip, b64));
                    break;
                }

                case "noOrder":
                    _prepareTcs.TrySetResult(null);
                    break;

                case "progress":
                {
                    var m = root.TryGetProperty("message", out var mm) ? mm.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(m))
                    {
                        L("extension: " + m);
                    }
                    break;
                }

                case "captcha":
                {
                    _captchaSeen = true;
                    // Hoàn tất mọi chặng ĐANG chờ (bất kể pha nào) để C# thoát nhanh + kiểm _captchaSeen.
                    _atSellerTcs.TrySetResult(false);
                    _detailTcs.TrySetResult("captcha");
                    _ordersTcs.TrySetResult(null);
                    _pickupTcs.TrySetResult(false);
                    _pickupOtherTcs.TrySetResult(false);
                    _prepareTcs.TrySetResult(null);
                    break;
                }

                case "error":
                {
                    var m = root.TryGetProperty("message", out var mm) ? mm.GetString() : "lỗi extension";
                    L("extension LỖI: " + m);
                    var ex = new InvalidOperationException("Extension báo lỗi: " + m);
                    // Fault mọi chặng đang chờ để phiên thoát sớm thay vì đợi timeout.
                    _readyTcs.TrySetException(ex);
                    _atSellerTcs.TrySetException(ex);
                    _shopListTcs.TrySetException(ex);
                    _detailTcs.TrySetException(ex);
                    _toShipTcs.TrySetException(ex);
                    _ordersTcs.TrySetException(ex);
                    _pickupTcs.TrySetException(ex);
                    _pickupOtherTcs.TrySetException(ex);
                    _prepareTcs.TrySetException(ex);
                    break;
                }
            }
        }
        catch { /* message lạ — bỏ qua, để timeout xử lý */ }
        finally
        {
            doc.Dispose();
        }
    }

    /// <summary>Đọc trường <c>data</c>: chuỗi → lấy nguyên; object/array → JSON thô (để hàm parse thuần xử lý). Không có → null.</summary>
    private static string? ReadDataAsString(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var d))
        {
            return null;
        }
        return d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText();
    }

    public void Dispose()
    {
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }
}
