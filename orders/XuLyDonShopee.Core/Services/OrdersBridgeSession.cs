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

/// <summary>GĐ4: kết quả một VÒNG qua MỌI shop (<see cref="OrdersBridgeSession.RunAllShopsAsync"/>).</summary>
/// <param name="ShopCount">Số shop đọc được từ picker.</param>
/// <param name="ShopsDone">Số shop đã xử trọn trong vòng này.</param>
/// <param name="TotalOrders">Tổng số đơn đọc được (cộng qua các shop).</param>
/// <param name="TotalSlips">Tổng số phiếu PDF đã lưu.</param>
/// <param name="Captcha">True nếu dừng vòng vì captcha/verify.</param>
/// <param name="Error">Thông báo lỗi (null nếu vòng chạy trọn không lỗi).</param>
/// <param name="PickupAddressFailed">True nếu có ≥1 shop bị BỎ QUA vì KHÔNG đặt được địa chỉ lấy hàng
/// (chưa in phiếu cho shop đó; các shop khác trong vòng vẫn chạy) — caller cảnh báo ra kênh ngoài.
/// KHÁC <paramref name="Captcha"/>: nguyên nhân khác, xử khác.</param>
/// <param name="PickupFailedShop">Nhãn shop (hoặc nhiều shop nối bằng ", ") không đặt được địa chỉ
/// (null nếu không dính lỗi này).</param>
public sealed record OrdersBridgeRunResult(
    int ShopCount, int ShopsDone, int TotalOrders, int TotalSlips, bool Captcha, string? Error,
    bool PickupAddressFailed = false, string? PickupFailedShop = null);

/// <summary>
/// Vòng đời MỘT phiên cầu nối: mở cổng loopback (<see cref="OrdersBridgeChannel"/>) → mở trình duyệt SẠCH
/// (không CDP, không remote-debugging-port) qua <see cref="OrdersBridgeLauncher"/> với <c>startUrl</c> có hash
/// <c>#_od_ws=&lt;port&gt;</c> để extension đọc cổng → chờ extension báo <c>ready</c> → đăng nhập → lặp shop.
/// <list type="bullet">
/// <item><see cref="RunLoginThenSliceAsync"/> (GĐ2 pivot): đăng nhập bằng trình duyệt điều khiển Playwright
/// (tái dùng luồng production) → đóng → mở lại bằng trình duyệt sạch + extension → lát cắt Seller Centre.</item>
/// <item><see cref="RunAllShopsAsync"/> (GĐ4): y hệt phần đầu rồi LẶP QUA MỌI shop.</item>
/// </list>
/// Parse dữ liệu qua các hàm THUẦN sẵn có (<see cref="ShopeeLoginService.ParseShopListJson"/>,
/// <see cref="ShopeeDashboard.ParseToShipCount"/>); mọi việc BÊN TRONG một shop nằm ở <see cref="ShopFlowRunner"/>.
/// <para>Một phiên/lần chạy (chưa đa-lane) — cổng cầu nối là hằng <see cref="OrdersBridgeChannel.BridgePort"/>.</para>
/// </summary>
public sealed class OrdersBridgeSession : IDisposable
{
    private readonly string _userDataDir;
    private readonly BrowserChoice _browserChoice;
    private readonly Action<string>? _log;
    private readonly string _province;
    // Tab "Kết quả": callback do App rót (Core không ref App/DB) — CHỈ THÊM lời gọi, không đổi luồng. Null-safe.
    //  · _onShopListRead: gọi ngay sau khi parse xong danh sách shop → App lưu account_shops (mọi shop, kể cả 0 đơn).
    //  · _onShopCheckStarted/_onShopCheckFinished: cột tiến độ — bắt đầu/xong MỘT shop (nhãn shop = khóa prepare_daily).
    private readonly Action<IReadOnlyList<ShopListItem>>? _onShopListRead;
    private readonly Action<string>? _onShopCheckStarted;
    private readonly Action<string>? _onShopCheckFinished;
    /// <summary>TEST: gọi ở shop đầu — trả true = ép bỏ qua vì lỗi địa chỉ (một lần). null = tắt.</summary>
    private readonly Func<bool>? _tryConsumeForceFirstShopPickupFail;
    // GĐ4: khoảng nghỉ ngẫu nhiên giữa các shop (ms) — kiểu người, tránh dồn dập.
    private readonly Random _rng = new();

    // Kênh lệnh (WebSocket + các chặng chờ) và flow bên trong một shop — dựng một lần, dùng lại qua các vòng.
    private readonly OrdersBridgeChannel _channel;
    private readonly ShopFlowRunner _flow;

    /// <summary>Tiến trình trình duyệt sạch đã mở (để tầng UI theo dõi/kill). Set ngay sau khi launch.</summary>
    public System.Diagnostics.Process? Process { get; private set; }

    /// <param name="invoiceDir">GĐ3 Phần B: thư mục lưu phiếu giao PDF; null/rỗng → chỉ đọc đơn, không tải phiếu.</param>
    /// <param name="province">GĐ3 Phần B: tỉnh của địa chỉ lấy hàng cần đặt (mặc định "Thanh Hóa").</param>
    /// <param name="syncCallback">GĐ4: gọi SAU khi đọc xong đơn mỗi shop — App lưu DB/GSheet/hub, kèm tên shop. null → chỉ log.</param>
    /// <param name="finalDoneSns">GĐ4: tập <c>order_sn</c> ĐÃ có "Số tiền cuối cùng" trong DB (App rót) — đơn nằm trong
    /// tập này KHÔNG mở lại chi tiết. null → không lọc (mở chi tiết cho MỌI đơn pending chưa có final).</param>
    /// <param name="onShopListRead">Tab "Kết quả": gọi ngay sau khi parse xong danh sách shop → App lưu account_shops. null → bỏ qua.</param>
    /// <param name="onOrderPrepared">Tab "Kết quả": gọi mỗi khi chuẩn bị xong 1 đơn (tham số = nhãn shop, MÃ ĐƠN
    /// <c>order_sn</c>) → App +1 đếm ngày + đánh dấu đơn đó đã chuẩn bị hàng (để đẩy lên hub). null → bỏ qua.</param>
    /// <param name="onShopCheckStarted">Tab "Kết quả" (cột tiến độ): gọi NGAY khi bắt đầu xử một shop (tham số = nhãn
    /// shop, ĐÚNG khóa <c>prepare_daily</c>) → App chuyển chấm sang shop đó + bật vòng quay. null → bỏ qua.</param>
    /// <param name="onShopCheckFinished">Tab "Kết quả" (cột tiến độ): gọi khi XONG shop đó — kể cả shop lỗi/captcha/bỏ
    /// qua (gọi trong <c>finally</c>) → App tắt vòng quay nhưng GIỮ chấm ở shop này tới khi shop kế bắt đầu. null → bỏ qua.</param>
    /// <param name="returnCountLast">Check đơn trả hàng: mốc "số yêu cầu" lần check TRƯỚC của shop (tham số = nhãn shop);
    /// null trả về = shop chưa từng check → lượt này CHỈ ghi nhớ số. Callback null → BỎ HẲN bước check trả hàng.</param>
    /// <param name="saveReturnCount">Check đơn trả hàng: ghi mốc mới cho shop (nhãn shop, số vừa đọc). null → bỏ hẳn bước.</param>
    /// <param name="saveReturnCodes">Check đơn trả hàng: lưu các cặp (mã đơn, mã yêu cầu) vào đơn tương ứng; trả chuỗi
    /// tóm tắt để phiên log. null → bỏ hẳn bước.</param>
    /// <param name="tryConsumeForceFirstShopPickupFail">TEST: ở shop đầu, trả <c>true</c> một lần → bỏ qua shop
    /// đó như lỗi địa chỉ (không in phiếu), vẫn chạy shop kế. null → tắt.</param>
    public OrdersBridgeSession(string userDataDir, BrowserChoice browserChoice, Action<string>? log = null,
        string? invoiceDir = null, string? province = null,
        Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>? syncCallback = null,
        Func<IReadOnlySet<string>>? finalDoneSns = null,
        Action<IReadOnlyList<ShopListItem>>? onShopListRead = null,
        Action<string, string>? onOrderPrepared = null,
        Action<string>? onShopCheckStarted = null,
        Action<string>? onShopCheckFinished = null,
        Func<string, int?>? returnCountLast = null,
        Action<string, int>? saveReturnCount = null,
        Func<IReadOnlyList<YeuCauTraHang>, string>? saveReturnCodes = null,
        Func<bool>? tryConsumeForceFirstShopPickupFail = null)
    {
        _userDataDir = userDataDir;
        _browserChoice = browserChoice;
        _log = log;
        _province = string.IsNullOrWhiteSpace(province) ? "Thanh Hóa" : province;
        _onShopListRead = onShopListRead;
        _onShopCheckStarted = onShopCheckStarted;
        _onShopCheckFinished = onShopCheckFinished;
        _tryConsumeForceFirstShopPickupFail = tryConsumeForceFirstShopPickupFail;

        _channel = new OrdersBridgeChannel(log);
        _flow = new ShopFlowRunner(_channel, log, invoiceDir, _province, syncCallback, finalDoneSns,
            onOrderPrepared, returnCountLast, saveReturnCount, saveReturnCodes);
    }

    private void L(string m) => _log?.Invoke(m);

    /// <summary>Thay MỚI mọi chặng chờ + xóa hai cờ dừng (captcha, không-đặt-được-địa-chỉ) trước một lần chạy.</summary>
    private void ResetState()
    {
        _channel.ResetStages();
        _flow.PickupFailedShop = null;
    }

    // ── Khởi động cầu + mở trình duyệt sạch tại startUrl (kèm hash cổng WS) ─────────────────────────────
    private void StartBridgeAndLaunch(string baseUrl)
    {
        _channel.Start();
        L($"Cầu nối: WebSocket lắng nghe ws://localhost:{OrdersBridgeChannel.BridgePort} — mở trình duyệt sạch...");

        // Vẫn nhúng hash (extension đọc nếu còn) nhưng KHÔNG phụ thuộc: mất hash → extension dùng cổng cố định.
        var startUrl = $"{baseUrl}#_od_ws={OrdersBridgeChannel.BridgePort}";
        Process = OrdersBridgeLauncher.Launch(_userDataDir, _browserChoice, startUrl);
    }

    // ── GĐ2 (pivot): đăng nhập bằng Playwright (an toàn — subaccount + /portal/shop KHÔNG bị captcha) → đóng
    //    → mở lại bằng trình duyệt SẠCH + extension để đọc Seller Centre (chỉ "Chi tiết" mới dính captcha). ──────
    /// <summary>
    /// GĐ2: đăng nhập Nền tảng tài khoản phụ bằng <b>trình duyệt điều khiển Playwright/CDP CŨ</b> (tái dùng NGUYÊN
    /// <see cref="ShopeeLoginService.OpenAsync"/> + <c>TryLoginSubaccountAsync</c> — tự điền form, mở hộp thư cho user
    /// đọc mã, chờ mã, SSO tới Seller Centre). Đăng nhập xong thì ĐÓNG trình duyệt điều khiển (nhả khoá hồ sơ), rồi
    /// mở lại bằng <b>trình duyệt SẠCH + extension</b> qua <see cref="RunSliceCoreAsync"/> (hồ sơ đã đăng nhập nên vào
    /// thẳng <c>/portal/shop</c>) → đọc shop → "Chi tiết" (trusted click, né captcha) → "Chờ Lấy Hàng".
    /// KHÔNG tự nhập mã hộ (mã là thao tác tay). Hủy giữa chừng → đóng cả trình duyệt điều khiển (finally) lẫn sạch.
    /// </summary>
    public async Task<OrdersBridgeSliceResult> RunLoginThenSliceAsync(OrdersLoginParams login, CancellationToken ct = default)
    {
        try
        {
            var err = await LoginAndReachPickerAsync(login, ct).ConfigureAwait(false);
            if (_channel.CaptchaSeen)
            {
                return new OrdersBridgeSliceResult(Array.Empty<ShopListItem>(), null, null, true,
                    "Rơi vào trang verify/captcha khi vào Seller Centre.");
            }
            if (err is not null)
            {
                return Fail(err);
            }

            L("Đã về trang chọn shop — đọc shop...");
            return await RunSliceCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            L("Cầu nối: hết thời gian chờ phản hồi từ extension.");
            return Fail("Hết thời gian chờ phản hồi từ extension.");
        }
        catch (Exception ex)
        {
            L("Cầu nối lỗi: " + ex.ToString());
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Đăng nhập Playwright + SSO qua bản sạch để về TRANG CHỌN SHOP (picker /portal/shop). Trả <c>null</c> nếu
    /// đã về picker (kiểm <see cref="OrdersBridgeChannel.CaptchaSeen"/> để phân biệt captcha — cũng trả null nhưng
    /// cờ bật); trả CHUỖI LỖI nếu login/SSO thất bại. Dùng chung cho <see cref="RunLoginThenSliceAsync"/> (một shop)
    /// và <see cref="RunAllShopsAsync"/> (mọi shop). Trình duyệt điều khiển login LUÔN được đóng (finally).
    /// </summary>
    private async Task<string?> LoginAndReachPickerAsync(OrdersLoginParams login, CancellationToken ct)
    {
        // 1) Đăng nhập bằng trình duyệt điều khiển (Playwright). try/finally: user Dừng giữa chừng vẫn đóng trình duyệt.
        var entered = false;
        ILoginSession? session = null;
        try
        {
            L("Đăng nhập Nền tảng tài khoản phụ bằng trình duyệt điều khiển (Playwright)...");
            var svc = new ShopeeLoginService();
            session = await svc.OpenAsync(_userDataDir, _browserChoice, ct).ConfigureAwait(false);
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
            return "Đăng nhập subaccount chưa xong (nhập mã?). Bấm lại để thử tiếp.";
        }

        // 2) Settle ngắn cho chắc nhả khoá file hồ sơ (Brave vừa kill) trước khi mở lại bằng trình duyệt sạch.
        await Task.Delay(800, ct).ConfigureAwait(false);

        // 3) Mở lại bằng trình duyệt SẠCH + extension tại /account → extension SSO "Kênh Người bán" → picker.
        //    KHÔNG mở thẳng /portal/shop vì Shopee sticky-redirect vào shop mở lần trước (server-side).
        L("Đăng nhập xong — mở lại bằng trình duyệt sạch + extension (subaccount /account → SSO → trang chọn shop)...");
        ResetState();
        StartBridgeAndLaunch(ShopeeLoginService.SubaccountAccountUrl);
        L("Chờ extension nối cầu (ready) — tối đa 45s...");
        await _channel.AwaitAsync(_channel.Ready, OrdersBridgeChannel.ChoChang.Ready, ct).ConfigureAwait(false);
        L("Extension đã nối cầu — SSO 'Kênh Người bán' để về trang chọn shop...");

        var atSellerTcs = _channel.ArmAtSeller();
        await _channel.SendAsync(new { action = "gotoSellerCentre" }).ConfigureAwait(false);
        var atSeller = await _channel.AwaitAsync(atSellerTcs, OrdersBridgeChannel.ChoChang.AtSeller, ct).ConfigureAwait(false);
        if (_channel.CaptchaSeen)
        {
            L("PHÁT HIỆN captcha/verify khi vào Seller Centre.");
            return null; // caller kiểm CaptchaSeen
        }
        if (!atSeller)
        {
            return "Không về được trang chọn shop (/portal/shop) sau SSO — có thể sticky shop cũ / cookie hết hạn.";
        }
        return null; // đã về picker, không captcha
    }

    // ── GĐ4: MỘT VÒNG qua MỌI shop: login → SSO → readShopList → từng shop (detail → toShip → syncOrders +
    //    callback lưu DB → nếu ToShip>0 xử đơn + revert địa chỉ) → đóng tab shop → nghỉ → shop kế. ─────────────
    /// <summary>
    /// GĐ4: đăng nhập + SSO về picker rồi LẶP QUA MỌI shop trong danh sách. Mỗi shop: mở Chi tiết → đọc "Chờ Lấy
    /// Hàng" → đọc đơn (tab Tất cả) → GỌI <c>syncCallback</c> (App lưu DB/GSheet/hub) → nếu ToShip&gt;0 thì đặt địa
    /// chỉ lấy hàng + Chuẩn bị hàng từng đơn + in phiếu + revert địa chỉ → đóng tab shop (về picker) → nghỉ 3-5' →
    /// shop kế. Captcha giữa chừng → dừng vòng (Captcha=true). Dùng cho nút "▶ Chạy" (production, chạy liên tục).
    /// </summary>
    public async Task<OrdersBridgeRunResult> RunAllShopsAsync(OrdersLoginParams login, CancellationToken ct = default)
    {
        int shopCount = 0, shopsDone = 0, totalOrders = 0, totalSlips = 0;
        var pickupFailedShops = new List<string>();
        try
        {
            var err = await LoginAndReachPickerAsync(login, ct).ConfigureAwait(false);
            if (_channel.CaptchaSeen)
            {
                return new OrdersBridgeRunResult(0, 0, 0, 0, true, "Rơi vào trang verify/captcha khi vào Seller Centre.");
            }
            if (err is not null)
            {
                return new OrdersBridgeRunResult(0, 0, 0, 0, false, err);
            }

            // Đọc danh sách shop (picker).
            var shopListTcs = _channel.ArmShopList();
            await _channel.SendAsync(new { action = "readShopList" }).ConfigureAwait(false);
            var json = await _channel.AwaitAsync(shopListTcs, OrdersBridgeChannel.ChoChang.ShopList, ct).ConfigureAwait(false);
            var shops = ShopeeLoginService.ParseShopListJson(json);
            _onShopListRead?.Invoke(shops); // tab "Kết quả": App lưu danh sách shop (mọi shop, kể cả 0 đơn).
            shopCount = shops.Count;
            L($"Đọc được {shops.Count} shop — bắt đầu lặp qua từng shop.");
            if (shops.Count == 0)
            {
                return new OrdersBridgeRunResult(0, 0, 0, 0, false, "Không đọc được shop nào (đã tới /portal/shop chưa?).");
            }

            for (int i = 0; i < shops.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var shop = shops[i];
                var shopName = string.IsNullOrWhiteSpace(shop.LoginName) ? shop.ShopId : shop.LoginName;
                // Nhãn shop cho cột Tên Shop (GSheet) + khóa đếm prepare_daily: LoginName, fallback ShopName
                // (KHÁC shopName ở trên fallback ShopId). Tính SỚM để cột tiến độ báo được ngay lúc bắt đầu.
                var shopLogin = string.IsNullOrWhiteSpace(shop.LoginName) ? shop.ShopName : shop.LoginName;
                L($"[Shop {i + 1}/{shops.Count}] {shopName} — mở Chi tiết...");

                // Cột tiến độ tab "Kết quả": bắt đầu check shop này → chấm nhảy sang đây + bật vòng quay.
                _onShopCheckStarted?.Invoke(shopLogin);
                try
                {
                    // TEST: ép shop ĐẦU lỗi địa chỉ (một lần) — bỏ qua không in phiếu, vẫn chạy shop kế + banner.
                    if (i == 0 && _tryConsumeForceFirstShopPickupFail?.Invoke() == true)
                    {
                        pickupFailedShops.Add(shopLogin);
                        L($"⛔ [TEST] Ép lỗi địa chỉ shop đầu {shopLogin} — BỎ QUA shop này, KHÔNG in phiếu; sang shop kế.");
                    }
                    else
                    {
                    // Mở Chi tiết shop (trusted click).
                    var detailTcs = _channel.ArmDetail();
                    await _channel.SendAsync(new { action = "openShopDetail", shopId = shop.ShopId }).ConfigureAwait(false);
                    var d = await _channel.AwaitAsync(detailTcs, OrdersBridgeChannel.ChoChang.Detail, ct).ConfigureAwait(false);
                    if (_channel.CaptchaSeen || d == "captcha")
                    {
                        return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, true, "Rơi vào captcha khi mở Chi tiết.");
                    }

                    // Đọc "Chờ Lấy Hàng".
                    var toShipTcs = _channel.ArmToShip();
                    await _channel.SendAsync(new { action = "readToShip" }).ConfigureAwait(false);
                    var raw = await _channel.AwaitAsync(toShipTcs, OrdersBridgeChannel.ChoChang.ToShip, ct).ConfigureAwait(false);
                    var toShip = ShopeeDashboard.ParseToShipCount(raw);
                    L($"[Shop {i + 1}] Chờ Lấy Hàng: {(toShip?.ToString() ?? "?")}.");

                    // Đọc đơn (Phần A) + callback lưu DB + xử đơn (Phần B) + revert địa chỉ.
                    var (orders, slips) = await _flow.RunShopOrdersAsync(shop.ShopId, shopLogin, toShip ?? 0, ct).ConfigureAwait(false);
                    if (_channel.CaptchaSeen)
                    {
                        return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders + orders, totalSlips + slips, true, "Rơi vào captcha khi đọc/xử đơn.");
                    }
                    if (_flow.PickupFailedShop is not null)
                    {
                        // Không đặt được địa chỉ → BỎ QUA shop này (không in phiếu), vẫn chạy shop kế.
                        // Lỗi địa chỉ thường chỉ của một shop; giả thuyết cũ "modal hỏng = mọi shop hỏng" đã bỏ (2026-08-04).
                        var failed = _flow.PickupFailedShop;
                        pickupFailedShops.Add(failed);
                        L($"⛔ Không đặt được địa chỉ lấy hàng ở shop {failed} — BỎ QUA shop này, KHÔNG in phiếu; sang shop kế.");
                        totalOrders += orders; // Phần A có thể đã sync đơn; slips = 0
                        _flow.PickupFailedShop = null; // tránh shop sau dính cờ cũ
                    }
                    else
                    {
                        totalOrders += orders;
                        totalSlips += slips;
                        shopsDone++;
                    }

                    // Đóng tab shop → về picker /portal/shop (listTabId picker giữ nguyên; extension đóng shopTabId).
                    // ⚠ PHẢI đọc cờ ok: bản trước vứt giá trị trả về, nên hễ picker không sẵn sàng là shop KẾ chết
                    // với thông báo lạc đề "chờ 30s chưa thấy tab shop mở" (3/3 lần trong nhật ký production, luôn
                    // đi ngay sau một lượt trang trả hàng không render).
                    // Cũng bắt buộc sau nhánh bỏ-qua-địa-chỉ — quên thì shop kế chết.
                    if (!await _flow.DongTabShopAsync(ct).ConfigureAwait(false))
                    {
                        // Shop CUỐI thì picker hỏng không hại ai — vòng đằng nào cũng kết thúc và vòng sau mở cửa
                        // sổ mới. Chỉ dừng khi CÒN shop phía sau, kẻo báo động giả ở đúng lúc mọi việc đã xong.
                        if (i < shops.Count - 1)
                        {
                            L($"⛔ Dừng cả vòng của tài khoản (bỏ {shops.Count - i - 1} shop còn lại) — không đưa "
                              + $"được về trang chọn shop sau shop {shopName}.");
                            var closeErr = $"Không quay lại được trang chọn shop sau shop {shopName} — đã dừng vòng (shop kế "
                                + "sẽ không mở được). Sẽ thử lại ở vòng sau.";
                            // Giữ tín hiệu địa chỉ nếu đã có shop bị bỏ qua → AccountSession vẫn gửi Slack.
                            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, closeErr,
                                PickupAddressFailed: pickupFailedShops.Count > 0,
                                PickupFailedShop: pickupFailedShops.Count > 0 ? string.Join(", ", pickupFailedShops) : null);
                        }
                        L($"closeShopTab không về được picker sau shop CUỐI ({shopName}) — vòng đã xong, bỏ qua.");
                    }
                    } // else (không ép TEST)
                }
                finally
                {
                    // XONG shop này — kể cả lỗi/captcha/hủy giữa chừng (đừng để vòng quay quay mãi). Tắt vòng quay,
                    // chấm VẪN ở lại shop này cho tới khi shop kế gọi _onShopCheckStarted. Đặt TRƯỚC nhịp nghỉ 3-5'.
                    _onShopCheckFinished?.Invoke(shopLogin);
                }

                // Nghỉ kiểu người 3-5' giữa các shop (trừ shop cuối).
                if (i < shops.Count - 1)
                {
                    var restMs = _rng.Next(180_000, 300_001);
                    L($"Nghỉ ~{restMs / 60000}' trước shop kế...");
                    await Task.Delay(restMs, ct).ConfigureAwait(false);
                }
            }

            L($"Xong 1 vòng: {shopsDone}/{shopCount} shop, {totalOrders} đơn, {totalSlips} phiếu"
              + (pickupFailedShops.Count > 0 ? $", bỏ qua {pickupFailedShops.Count} shop lỗi địa chỉ." : "."));
            if (pickupFailedShops.Count > 0)
            {
                var tenLoi = string.Join(", ", pickupFailedShops);
                return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false,
                    $"Không đặt được địa chỉ lấy hàng ({_province}) ở shop {tenLoi} — đã bỏ qua shop đó, chưa in phiếu; các shop khác vẫn chạy.",
                    PickupAddressFailed: true, PickupFailedShop: tenLoi);
            }
            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            L("Cầu nối: hết thời gian chờ phản hồi từ extension.");
            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, "Hết thời gian chờ phản hồi từ extension.");
        }
        catch (Exception ex)
        {
            L("Cầu nối lỗi: " + ex.ToString());
            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, ex.Message);
        }
    }

    /// <summary>
    /// <b>Tải LẠI phiếu MỘT đơn qua cầu nối extension</b> (nút "Tải phiếu" màn Đơn hàng) — thân nằm ở
    /// <see cref="ShopFlowRunner.RedownloadSlipAsync"/> (thao tác trên tab shop đang mở). Ném
    /// <see cref="InvalidOperationException"/> khi cầu nối chưa khởi động / extension chưa kết nối (fail-fast, để
    /// caller báo đúng lý do thay vì ngồi chờ timeout).
    /// </summary>
    public Task<bool> RedownloadSlipAsync(string orderSn, CancellationToken ct = default)
        => _flow.RedownloadSlipAsync(orderSn, ct);

    // Lát cắt dùng chung (GĐ1 + đuôi GĐ2): readShopList → openShopDetail(shop đầu) → readToShip.
    private async Task<OrdersBridgeSliceResult> RunSliceCoreAsync(CancellationToken ct)
    {
        // 1) Đọc danh sách shop.
        await _channel.SendAsync(new { action = "readShopList" }).ConfigureAwait(false);
        var shopListJson = await _channel.AwaitAsync(_channel.ShopList, OrdersBridgeChannel.ChoChang.ShopList, ct).ConfigureAwait(false);
        var shops = ShopeeLoginService.ParseShopListJson(shopListJson);
        _onShopListRead?.Invoke(shops); // tab "Kết quả": App lưu danh sách shop (mọi shop, kể cả 0 đơn).
        L($"Đọc được {shops.Count} shop từ /portal/shop.");
        if (shops.Count == 0)
        {
            return new OrdersBridgeSliceResult(shops, null, null, false,
                "Không đọc được shop nào (đã tới /portal/shop chưa?).");
        }

        // 2) Mở "Chi tiết" shop đầu bằng trusted click (kỳ vọng KHÔNG captcha).
        var firstShopId = shops[0].ShopId;
        // Nhãn shop cho khớp chữ ký (callback null ở "Chạy thử" nên không dùng, nhưng phải truyền). shops[0] an toàn (đã guard rỗng ở trên).
        var firstShopLogin = string.IsNullOrWhiteSpace(shops[0].LoginName) ? shops[0].ShopName : shops[0].LoginName;
        L($"Mở 'Chi tiết' shop đầu (id={firstShopId}) bằng trusted click...");

        // Cột tiến độ tab "Kết quả": lát cắt chỉ chạy shop đầu — vẫn báo bắt đầu/xong y như vòng RunAllShopsAsync.
        _onShopCheckStarted?.Invoke(firstShopLogin);
        try
        {
            await _channel.SendAsync(new { action = "openShopDetail", shopId = firstShopId }).ConfigureAwait(false);
            var detail = await _channel.AwaitAsync(_channel.Detail, OrdersBridgeChannel.ChoChang.Detail, ct).ConfigureAwait(false);
            if (detail == "captcha" || _channel.CaptchaSeen)
            {
                L("PHÁT HIỆN captcha/verify khi mở Chi tiết — cần soi lại.");
                return new OrdersBridgeSliceResult(shops, firstShopId, null, true,
                    "Rơi vào trang verify/captcha khi mở Chi tiết.");
            }
            L("Đã mở tab shop (không captcha).");

            // 3) Đọc số "Chờ Lấy Hàng".
            await _channel.SendAsync(new { action = "readToShip" }).ConfigureAwait(false);
            var raw = await _channel.AwaitAsync(_channel.ToShip, OrdersBridgeChannel.ChoChang.ToShip, ct).ConfigureAwait(false);
            var toShip = ShopeeDashboard.ParseToShipCount(raw);
            L($"Số 'Chờ Lấy Hàng' đọc được: {(toShip?.ToString() ?? "null")} (raw='{raw}').");

            // 4) GĐ3: đọc đơn (Phần A) + nếu ToShip>0 thì xử đơn (Phần B).
            var (ordersCount, slipsSaved) = await _flow.RunShopOrdersAsync(firstShopId, firstShopLogin, toShip ?? 0, ct).ConfigureAwait(false);
            if (_channel.CaptchaSeen)
            {
                return new OrdersBridgeSliceResult(shops, firstShopId, toShip, true,
                    "Rơi vào trang verify/captcha khi đọc/xử đơn.", ordersCount, slipsSaved);
            }
            if (_flow.PickupFailedShop is not null)
            {
                // "Chạy thử" chỉ 1 shop — bỏ qua shop đó, đừng báo OK (người soi sẽ tưởng đã chạy trọn).
                return new OrdersBridgeSliceResult(shops, firstShopId, toShip, false,
                    $"Không đặt được địa chỉ lấy hàng ({_province}) — đã bỏ qua shop, KHÔNG in phiếu.", ordersCount, slipsSaved);
            }

            return new OrdersBridgeSliceResult(shops, firstShopId, toShip, false, null, ordersCount, slipsSaved);
        }
        finally
        {
            // XONG shop (kể cả lỗi/captcha) → tắt vòng quay, chấm ở lại.
            _onShopCheckFinished?.Invoke(firstShopLogin);
        }
    }

    private OrdersBridgeSliceResult Fail(string message) =>
        new(Array.Empty<ShopListItem>(), null, null, _channel.CaptchaSeen, message);

    public void Dispose() => _channel.Dispose();
}
