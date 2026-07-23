using System.Net;
using System.Net.Sockets;
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
public sealed record OrdersBridgeSliceResult(
    IReadOnlyList<ShopListItem> Shops,
    string? FirstShopId,
    int? ToShipCount,
    bool Captcha,
    string? Error);

/// <summary>Tham số đăng nhập cho <see cref="OrdersBridgeSession.RunLoginThenSliceAsync"/> (GĐ2).</summary>
/// <param name="User">Tên đăng nhập subaccount (= <c>acc.Email</c> ở luồng production).</param>
/// <param name="Pass">Mật khẩu subaccount (= <c>acc.Password</c>).</param>
/// <param name="VerifyEmail">Hotmail/Outlook để đọc mã xác thực (có thể rỗng → không mở hộp thư).</param>
/// <param name="VerifyEmailPassword">Mật khẩu hộp thư.</param>
/// <param name="MailUserDataDir">Hồ sơ persistent RIÊNG cho trình duyệt hộp thư (tách khỏi hồ sơ Shopee).</param>
public sealed record OrdersLoginParams(
    string User, string Pass, string? VerifyEmail, string? VerifyEmailPassword, string MailUserDataDir);

/// <summary>
/// Vòng đời MỘT phiên cầu nối: cấp cổng loopback trống → chạy <see cref="OrdersWebSocketServer"/> →
/// mở trình duyệt SẠCH (không CDP, không remote-debugging-port) qua <see cref="PocCleanLauncher"/> với
/// <c>startUrl</c> có hash <c>#_od_ws=&lt;port&gt;</c> để extension đọc cổng → chờ extension báo <c>ready</c>.
/// <list type="bullet">
/// <item><see cref="RunSliceAsync"/> (GĐ1): mở thẳng <c>/portal/shop</c> (user đã đăng nhập tay) → chạy lát cắt.</item>
/// <item><see cref="RunLoginThenSliceAsync"/> (GĐ2): mở <c>subaccount.shopee.com</c> → extension tự điền form
/// đăng nhập → chờ user nhập mã (mở hộp thư Playwright riêng cho user đọc) → SSO sang Seller Centre → lát cắt.</item>
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

    private OrdersWebSocketServer? _ws;

    // Cờ hoàn tất từng chặng — tạo mới mỗi lần chạy; RunContinuationsAsynchronously để continuation KHÔNG chạy
    // trên thread nhận WebSocket (tránh nghẽn vòng nhận / deadlock).
    private TaskCompletionSource<bool> _readyTcs = NewTcs<bool>();
    private TaskCompletionSource<string> _loginStatusTcs = NewTcs<string>();   // GĐ2: loggedIn/needCode/pending
    private TaskCompletionSource<bool> _atSellerTcs = NewTcs<bool>();          // GĐ2: đã vào Seller Centre
    private TaskCompletionSource<string?> _shopListTcs = NewTcs<string?>();
    private TaskCompletionSource<string> _detailTcs = NewTcs<string>();        // "ok" | "captcha"
    private TaskCompletionSource<string?> _toShipTcs = NewTcs<string?>();

    private bool _captchaSeen;

    /// <summary>Tiến trình trình duyệt sạch đã mở (để tầng UI theo dõi/kill). Set ngay sau khi launch.</summary>
    public System.Diagnostics.Process? Process { get; private set; }

    public OrdersBridgeSession(string userDataDir, BrowserChoice browserChoice, Action<string>? log = null)
    {
        _userDataDir = userDataDir;
        _browserChoice = browserChoice;
        _log = log;
    }

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void L(string m) => _log?.Invoke(m);

    private void ResetTcs()
    {
        _readyTcs = NewTcs<bool>();
        _loginStatusTcs = NewTcs<string>();
        _atSellerTcs = NewTcs<bool>();
        _shopListTcs = NewTcs<string?>();
        _detailTcs = NewTcs<string>();
        _toShipTcs = NewTcs<string?>();
        _captchaSeen = false;
    }

    // ── Khởi động cầu + mở trình duyệt sạch tại startUrl (kèm hash cổng WS) ─────────────────────────────
    private void StartBridgeAndLaunch(string baseUrl)
    {
        var wsPort = AllocateFreePort();
        _ws = new OrdersWebSocketServer(wsPort);
        _ws.MessageReceived += OnMessage;
        _ws.Start();
        L($"Cầu nối: WebSocket lắng nghe ws://localhost:{wsPort} — mở trình duyệt sạch...");

        // Extension đọc cổng từ hash của URL đầu tiên (content.js đọc sớm + lưu chrome.storage.session).
        var startUrl = $"{baseUrl}#_od_ws={wsPort}";
        var extPath = BraveLaunchArgs.ResolveOrdersBridgeExtension()
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension 'shopee-orders' (cạnh app hoặc trong repo). " +
                "Cầu nối cần extension này để nối WebSocket + bắn trusted input.");

        Process = PocCleanLauncher.Open(_userDataDir, _browserChoice, startUrl, extPath);
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

    // ── GĐ2: đăng nhập subaccount qua extension → SSO Seller Centre → lát cắt ───────────────────────────
    /// <summary>
    /// GĐ2: mở <c>subaccount.shopee.com</c> → extension điền form đăng nhập → chờ user nhập mã (mở hộp thư
    /// Playwright riêng để user đọc) → SSO "Tài khoản của tôi" → "Kênh Người bán" → <c>/portal/shop</c> → lát cắt.
    /// KHÔNG tự nhập mã hộ (mã là thao tác tay). Ngoại lệ bọc thành Error; hủy ném ra ngoài.
    /// </summary>
    public async Task<OrdersBridgeSliceResult> RunLoginThenSliceAsync(OrdersLoginParams login, CancellationToken ct = default)
    {
        ResetTcs();
        StartBridgeAndLaunch(ShopeeLoginService.SubaccountUrl);

        OrdersMailboxSession? mail = null;
        Task<bool>? mailTask = null;
        try
        {
            L("Chờ extension nối cầu (ready) — tối đa 45s...");
            await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            L("Extension đã nối cầu — điền form đăng nhập Nền tảng tài khoản phụ...");

            await _ws!.SendAsync(new { action = "login", user = login.User, pass = login.Pass }).ConfigureAwait(false);

            // Poll trạng thái đăng nhập; mở hộp thư khi Shopee đòi mã (hoặc sau ~8s) để user sẵn sàng đọc code.
            var hasVerifyMail = !string.IsNullOrWhiteSpace(login.VerifyEmail)
                                && !string.IsNullOrWhiteSpace(login.VerifyEmailPassword);
            var loginStart = DateTime.UtcNow;
            var deadline = DateTime.UtcNow.AddMinutes(15); // chờ user gõ mã là phần lâu nhất
            var loggedIn = false;
            var mailOpened = false;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                _loginStatusTcs = NewTcs<string>();
                await _ws.SendAsync(new { action = "checkLogin" }).ConfigureAwait(false);
                string state;
                try { state = await _loginStatusTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
                catch (TimeoutException) { state = "pending"; }

                if (state == "loggedIn") { loggedIn = true; break; }

                var elapsed = DateTime.UtcNow - loginStart;
                if (!mailOpened && hasVerifyMail && (state == "needCode" || elapsed > TimeSpan.FromSeconds(8)))
                {
                    L(state == "needCode"
                        ? "Shopee đòi mã xác thực — mở hộp thư để bạn tự đọc mã..."
                        : "Mở sẵn hộp thư để bạn đọc mã nếu Shopee yêu cầu...");
                    mail = new OrdersMailboxSession(login.MailUserDataDir, _browserChoice, _log);
                    mailTask = mail.OpenForManualCodeAsync(login.VerifyEmail!, login.VerifyEmailPassword!, ct);
                    mailOpened = true;
                }

                await Task.Delay(3000, ct).ConfigureAwait(false);
            }

            if (!loggedIn)
            {
                return Fail("Chờ 15' chưa đăng nhập được Nền tảng tài khoản phụ (nhập mã chưa xong?).");
            }

            // SSO sang Seller Centre.
            L("Đã đăng nhập subaccount — chuyển sang Kênh Người bán (Seller Centre)...");
            _atSellerTcs = NewTcs<bool>();
            await _ws.SendAsync(new { action = "gotoSellerCentre" }).ConfigureAwait(false);
            var atSeller = await _atSellerTcs.Task.WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
            if (_captchaSeen)
            {
                L("PHÁT HIỆN captcha/verify khi vào Seller Centre — cần soi lại.");
                return new OrdersBridgeSliceResult(Array.Empty<ShopListItem>(), null, null, true,
                    "Rơi vào trang verify/captcha khi vào Seller Centre.");
            }
            if (!atSeller)
            {
                return Fail("Không mở được Kênh Người bán / /portal/shop sau đăng nhập.");
            }

            L("Đã vào Seller Centre — đọc danh sách shop...");
            return await RunSliceCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            L("Cầu nối: hết thời gian chờ phản hồi từ extension.");
            return Fail("Hết thời gian chờ phản hồi từ extension khi đăng nhập.");
        }
        catch (Exception ex)
        {
            L("Cầu nối lỗi: " + ex.Message);
            return Fail(ex.Message);
        }
        finally
        {
            // Đóng trình duyệt hộp thư (user đã đọc mã xong trước khi loggedIn). Best-effort.
            if (mail is not null)
            {
                try { if (mailTask is not null) await Task.WhenAny(mailTask, Task.Delay(1000)).ConfigureAwait(false); }
                catch { /* bỏ qua */ }
                await mail.DisposeAsync().ConfigureAwait(false);
            }
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

        return new OrdersBridgeSliceResult(shops, firstShopId, toShip, false, null);
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

                case "loginStatus":
                {
                    var state = root.TryGetProperty("state", out var s) ? (s.GetString() ?? "pending") : "pending";
                    _loginStatusTcs.TrySetResult(state);
                    break;
                }

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
                    break;
                }

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
                    // Có thể xảy ra khi mở Chi tiết HOẶC khi vào Seller Centre → hoàn tất cả hai chặng đang chờ.
                    _detailTcs.TrySetResult("captcha");
                    _atSellerTcs.TrySetResult(false);
                    break;
                }

                case "error":
                {
                    var m = root.TryGetProperty("message", out var mm) ? mm.GetString() : "lỗi extension";
                    L("extension LỖI: " + m);
                    var ex = new InvalidOperationException("Extension báo lỗi: " + m);
                    // Fault mọi chặng đang chờ để phiên thoát sớm thay vì đợi timeout.
                    _readyTcs.TrySetException(ex);
                    _loginStatusTcs.TrySetException(ex);
                    _atSellerTcs.TrySetException(ex);
                    _shopListTcs.TrySetException(ex);
                    _detailTcs.TrySetException(ex);
                    _toShipTcs.TrySetException(ex);
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

    /// <summary>Chọn một cổng loopback trống bằng cách bind port 0 rồi nhả (một phiên/lần test là đủ).</summary>
    private static int AllocateFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    public void Dispose()
    {
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }
}
