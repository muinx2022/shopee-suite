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
/// <param name="PickupOkShops">Nhãn shop (hoặc nhiều shop nối bằng ", ") ĐẶT ĐƯỢC địa chỉ lấy hàng trong vòng
/// này — caller dùng để TỰ GỠ banner lỗi địa chỉ cũ của đúng những shop đó. null = chưa shop nào chạy được bước
/// đặt địa chỉ (shop 0 đơn chờ lấy hàng KHÔNG tính: không chạy bước đó thì chưa chứng minh được gì).
/// ĐỘC LẬP với <paramref name="PickupFailedShop"/>: một vòng có thể vừa có shop lành vừa có shop hỏng.</param>
public sealed record OrdersBridgeRunResult(
    int ShopCount, int ShopsDone, int TotalOrders, int TotalSlips, bool Captcha, string? Error,
    bool PickupAddressFailed = false, string? PickupFailedShop = null, string? PickupOkShops = null);

/// <summary>
/// Kết quả MỘT lượt KIỂM TRA LẠI ĐỊA CHỈ của đúng một shop theo lệnh người dùng (nút "Check" trên banner lỗi
/// địa chỉ) — <see cref="OrdersBridgeSession.KiemTraDiaChiMotShopAsync"/>.
/// </summary>
/// <param name="Ok">Shop ĐẶT ĐƯỢC địa chỉ ⇒ đủ căn cứ gỡ banner + báo Hub.</param>
/// <param name="Captcha">Rơi captcha ⇒ <b>KHÔNG kết luận</b> được: không gỡ mà cũng không coi là còn lỗi.</param>
/// <param name="Error">Lỗi hạ tầng (không đăng nhập được / không thấy shop / cầu nối chết). null = chạy trọn.</param>
/// <param name="PickupOkShop">Nhãn shop đặt được địa chỉ — đưa thẳng vào đường gỡ banner sẵn có
/// (<c>GoBannerLoiDiaChi</c>), KHÔNG viết đường gỡ thứ hai.</param>
public sealed record OrdersKiemTraDiaChiResult(
    bool Ok, bool Captcha, string? Error, string? PickupOkShop = null);

/// <summary>Kết quả MỘT lượt mở trình duyệt sạch + SSO về trang chọn shop
/// (<see cref="OrdersBridgeSession.SsoVePickerAsync"/>).</summary>
internal enum KetQuaSso
{
    /// <summary>Đã về trang chọn shop (/portal/shop) — chạy tiếp được.</summary>
    Ok,

    /// <summary>Rơi vào trang verify/captcha — KHÔNG được đăng nhập lại, phải nghỉ vòng.</summary>
    Captcha,

    /// <summary>Extension TRẢ LỜI rõ ràng là không vào được, vì lý do KHÁC captcha: /account ra form đăng nhập
    /// (cookie hết hạn), SSO trượt, không thấy "Kênh Người bán", picker không render, hoặc gửi lệnh bị từ chối vì
    /// socket đã rớt. Đây là nhóm ĐÁNG đăng nhập lại — ca chính là cookie hết hạn.</summary>
    Loi,

    /// <summary>KHÔNG có phản hồi trong hạn chặng (extension chưa nối cầu / trang treo). Khác <see cref="Loi"/> ở
    /// chỗ ta KHÔNG biết đang ở đâu, nên KHÔNG đăng nhập lại: cookie hết hạn thì extension báo NGAY (nó kiểm
    /// <c>pageIsLoginForm</c> trước khi làm gì), nên treo gần như không bao giờ có nghĩa "cần đăng nhập lại".
    /// <para><b>Ca thật phải chặn:</b> tổng hạn phía extension của <c>gotoSellerCentre</c> (10s dò entry + 90s chờ
    /// tab banhang + 15s load + 30s ensure picker ≈ 145s) DÀI HƠN hạn chặng <c>AtSeller</c> (120s) — nên khi Shopee
    /// bày trang verify, C# có thể hết giờ TRƯỚC lúc extension kịp gửi <c>captcha</c>. Gộp ca này vào
    /// <see cref="Loi"/> là đi mở trình duyệt đăng nhập ngay giữa lúc bị nghi ngờ; bản trước khi đảo thứ tự để
    /// <c>TimeoutException</c> xuyên lên caller (nghỉ vòng) — giữ đúng hành vi đó.</para>
    /// <para><b>BẤT BIẾN mà luật này đứng lên</b> (phá là kẹt tài khoản vĩnh viễn): <c>ready</c> tới C# KHÔNG phụ
    /// thuộc trang đang mở — <c>background.js</c> gọi <c>bridge.connect()</c> ở TOP-LEVEL service worker, chạy ngay
    /// khi bản chép extension được nạp lúc phóng trình duyệt, độc lập với content script. Nhờ vậy "hết giờ Ready"
    /// luôn nghĩa là hạ tầng hỏng, KHÔNG BAO GIỜ nghĩa là "trang không phải Shopee ⇒ có thể đã logout". Nếu ai bỏ
    /// lời gọi connect top-level đó (để <c>ready</c> chỉ đến từ content script), phải xét lại
    /// <see cref="OrdersBridgeSession.QuyetDinhSauThuBanSach"/>: lúc ấy Treo có thể mang nghĩa cần đăng nhập lại
    /// thật, và nghỉ-vòng-mãi sẽ không bao giờ tự thoát.</para></summary>
    Treo,
}

/// <summary>Kết quả một lượt SSO kèm lý do (chỉ có khi <see cref="KetQuaSso.Loi"/>) để log + trả lên caller.</summary>
internal sealed record KetQuaSsoChiTiet(KetQuaSso Ket, string? LyDo);

/// <summary>Việc phải làm sau một lượt thử bản sạch — xem <see cref="OrdersBridgeSession.QuyetDinhSauThuBanSach"/>.</summary>
internal enum HanhDongSauThuSach
{
    /// <summary>Đã ở trang chọn shop → chạy tiếp vòng shop.</summary>
    ChayTiep,

    /// <summary>Captcha → kết thúc êm, caller kiểm <c>CaptchaSeen</c> rồi nghỉ vòng.</summary>
    DungVongCaptcha,

    /// <summary>Đóng bản sạch, đăng nhập lại bằng Playwright rồi mở bản sạch lần nữa.</summary>
    DangNhapLai,

    /// <summary>Đã đăng nhập lại mà vẫn không vào được → trả lỗi, chờ vòng sau.</summary>
    BaoLoi,
}

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
    // Tab "Shops": callback do App rót (Core không ref App/DB) — CHỈ THÊM lời gọi, không đổi luồng. Null-safe.
    //  · _onShopListRead: gọi ngay sau khi parse xong danh sách shop → App lưu account_shops (mọi shop, kể cả 0 đơn).
    //  · _onShopCheckStarted/_onShopCheckFinished: cột tiến độ — bắt đầu/xong MỘT shop (nhãn shop = khóa prepare_daily).
    private readonly Action<IReadOnlyList<ShopListItem>>? _onShopListRead;
    private readonly Action<string>? _onShopCheckStarted;
    private readonly Action<string>? _onShopCheckFinished;
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
    /// <param name="onShopListRead">Tab "Shops": gọi ngay sau khi parse xong danh sách shop → App lưu account_shops. null → bỏ qua.</param>
    /// <param name="onOrderPrepared">Tab "Shops": gọi mỗi khi chuẩn bị xong 1 đơn (tham số = nhãn shop, MÃ ĐƠN
    /// <c>order_sn</c>) → App +1 đếm ngày + đánh dấu đơn đó đã chuẩn bị hàng (để đẩy lên hub). null → bỏ qua.</param>
    /// <param name="onShopCheckStarted">Tab "Shops" (cột tiến độ): gọi NGAY khi bắt đầu xử một shop (tham số = nhãn
    /// shop, ĐÚNG khóa <c>prepare_daily</c>) → App chuyển chấm sang shop đó + bật vòng quay. null → bỏ qua.</param>
    /// <param name="onShopCheckFinished">Tab "Shops" (cột tiến độ): gọi khi XONG shop đó — kể cả shop lỗi/captcha/bỏ
    /// qua (gọi trong <c>finally</c>) → App tắt vòng quay nhưng GIỮ chấm ở shop này tới khi shop kế bắt đầu. null → bỏ qua.</param>
    /// <param name="returnCountLast">Check đơn trả hàng: mốc "số yêu cầu" lần check TRƯỚC của shop (tham số = nhãn shop);
    /// null trả về = shop chưa từng check → lượt này CHỈ ghi nhớ số. Callback null → BỎ HẲN bước check trả hàng.</param>
    /// <param name="saveReturnCount">Check đơn trả hàng: ghi mốc mới cho shop (nhãn shop, số vừa đọc). null → bỏ hẳn bước.</param>
    /// <param name="saveReturnCodes">Check đơn trả hàng: lưu các cặp (mã đơn, mã yêu cầu) vào đơn tương ứng; trả chuỗi
    /// tóm tắt để phiên log. null → bỏ hẳn bước.</param>
    /// <param name="layDonThieuPhieu">Tự tải lại phiếu thiếu (bước BÙ cuối flow shop): tham số = nhãn shop; App trả
    /// danh sách <c>order_sn</c> của ĐÚNG shop đó đang có mã vận đơn nhưng thiếu file PDF hợp lệ, xếp MỚI NHẤT
    /// TRƯỚC. null → BỎ HẲN bước (đường "Chạy thử" chỉ đọc, không lưu, nên không rót).</param>
    /// <param name="dangCoCanhBaoDiaChi">Shop này (tham số = nhãn shop) có đang treo BANNER lỗi địa chỉ trên tab
    /// Shops không. Shop đang treo banner mà lượt này không có đơn Chờ Lấy Hàng thì vẫn chạy riêng bước đặt địa chỉ,
    /// để banner có đường TỰ hết (xem <c>ShopFlowRunner.QuyetDinhBuocDiaChi</c>). null → coi như không shop nào có
    /// banner ⇒ hành vi y hệt trước 10/08/2026.</param>
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
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? layDonThieuPhieu = null,
        Func<IReadOnlyList<YeuCauTraHang>, int>? demMaTraChuaBiet = null,
        Func<string, bool>? dangCoCanhBaoDiaChi = null)
    {
        _userDataDir = userDataDir;
        _browserChoice = browserChoice;
        _log = log;
        _province = string.IsNullOrWhiteSpace(province) ? "Thanh Hóa" : province;
        _onShopListRead = onShopListRead;
        _onShopCheckStarted = onShopCheckStarted;
        _onShopCheckFinished = onShopCheckFinished;

        _channel = new OrdersBridgeChannel(log);
        _flow = new ShopFlowRunner(_channel, log, invoiceDir, _province, syncCallback, finalDoneSns,
            onOrderPrepared, returnCountLast, saveReturnCount, saveReturnCodes, layDonThieuPhieu,
            demMaTraChuaBiet, dangCoCanhBaoDiaChi);
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
        // Cổng mở ĐÚNG MỘT LẦN cho cả phiên: một vòng có thể phóng trình duyệt sạch HAI lần (thử trước bằng
        // cookie hồ sơ, rồi mở lại sau khi đăng nhập Playwright). Gọi Start lần hai là dựng HttpListener MỚI
        // trên cổng ta ĐANG giữ đăng ký → ném, và retry trong Channel.Start không cứu được vì kẻ chiếm cổng
        // chính là mình. WebSocketServer tự thay socket khi extension của lượt sau nối vào ("kết nối mới nhất
        // là sống"), nên giữ nguyên server là đủ.
        if (!_channel.Started)
        {
            _channel.Start();
        }
        L($"Cầu nối: WebSocket lắng nghe ws://localhost:{OrdersBridgeChannel.BridgePort} — mở trình duyệt sạch...");

        // Vẫn nhúng hash (extension đọc nếu còn) nhưng KHÔNG phụ thuộc: mất hash → extension dùng cổng cố định.
        var startUrl = $"{baseUrl}#_od_ws={OrdersBridgeChannel.BridgePort}";
        Process = OrdersBridgeLauncher.Launch(_userDataDir, _browserChoice, startUrl, _log);
        TheoDoiTrinhDuyetThoat(Process);
    }

    /// <summary>
    /// Ghi nhật ký khi TIẾN TRÌNH trình duyệt sạch thoát, kèm MÃ THOÁT. Không có dòng này thì trình duyệt chết
    /// giữa vòng chỉ hiện ra dưới dạng "cầu nối đứt", và câu chờ-nối-lại còn đổ oan cho service worker MV3
    /// ("service worker ngủ trong lúc nghỉ?") — đúng ca 10/08/2026: vòng 11:00 thấy Brave biến mất sạch lúc
    /// 11:23:46 mà nhật ký chỉ nói cầu nối đứt. Mã thoát phân biệt THOÁT ÊM (0) với CHẾT (mã lỗi Windows), tức
    /// phân biệt "có ai đó đóng cửa sổ / tab cuối vừa đóng" với "trình duyệt sập".
    /// <para>Best-effort tuyệt đối: hỏng ở đây tuyệt đối không được ảnh hưởng vòng chạy.</para>
    /// </summary>
    private void TheoDoiTrinhDuyetThoat(System.Diagnostics.Process? p)
    {
        if (p is null) { return; }
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                int? ma = null;
                try { ma = p.ExitCode; } catch { /* không đọc được mã */ }
                L($"⚠ Trình duyệt sạch (PID {p.Id}) đã THOÁT lúc {DateTime.Now:HH:mm:ss}"
                  + (ma is null ? " (không đọc được mã thoát)." : $" — mã thoát {ma} (0x{ma:X})."));
            };
        }
        catch (Exception ex) { L("Không gắn được theo dõi tiến trình trình duyệt: " + ex.ToString()); }
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
    /// PURE — việc phải làm sau MỘT lượt thử bản sạch. Bảng quyết định của thứ tự "sạch trước, Playwright chỉ khi
    /// cần" (user chốt 2026-08-07): cookie hồ sơ còn hạn thì KHỎI mở trình duyệt điều khiển; hỏng vì bất kỳ lý do
    /// nào KHÁC captcha thì đăng nhập lại rồi thử NỐT một lượt nữa; đã đăng nhập lại mà vẫn hỏng thì báo lỗi, chờ
    /// vòng sau.
    /// <para><b>Vì sao fallback cho MỌI lỗi, không chỉ "gặp form đăng nhập":</b> extension báo lý do bằng một câu
    /// tiếng Việt trong <c>action:"error"</c> — so khớp câu chữ đó là mong manh, sửa một dấu phẩy bên extension là
    /// mất nhánh đăng nhập lại. Lỗi SSO nào cũng có thể là cookie hỏng, nên cứ đăng nhập lại một lần.</para>
    /// <para><b>Riêng captcha KHÔNG đăng nhập lại:</b> đẩy Playwright vào lúc Shopee đang nghi ngờ là tự khai bot.</para>
    /// <para><b>Treo (không phản hồi) cũng KHÔNG đăng nhập lại</b> — xem <see cref="KetQuaSso.Treo"/>: hết giờ
    /// chặng có thể là captcha mà extension chưa kịp báo.</para>
    /// </summary>
    /// <param name="ket">Kết quả lượt thử vừa rồi.</param>
    /// <param name="daFallback">Lượt này CÓ PHẢI đã đi qua bước đăng nhập lại chưa (true = lượt hai, hết đường lui).</param>
    internal static HanhDongSauThuSach QuyetDinhSauThuBanSach(KetQuaSso ket, bool daFallback) => ket switch
    {
        KetQuaSso.Ok => HanhDongSauThuSach.ChayTiep,
        KetQuaSso.Captcha => HanhDongSauThuSach.DungVongCaptcha,
        KetQuaSso.Treo => HanhDongSauThuSach.BaoLoi,
        _ => daFallback ? HanhDongSauThuSach.BaoLoi : HanhDongSauThuSach.DangNhapLai,
    };

    /// <summary>
    /// MỘT lượt: mở trình duyệt SẠCH + extension tại <c>/account</c> → chờ extension nối cầu → SSO "Kênh Người bán"
    /// → trang chọn shop. KHÔNG mở thẳng <c>/portal/shop</c> vì Shopee sticky-redirect vào shop mở lần trước
    /// (server-side).
    /// <para>Dùng cho CẢ hai lượt của một vòng: lượt đầu (ăn cookie hồ sơ sẵn có) và lượt sau khi đã đăng nhập lại
    /// bằng Playwright. Mọi đường lỗi gói về <see cref="KetQuaSso.Loi"/> kèm lý do — trừ HỦY (người dùng bấm Dừng)
    /// thì ném tiếp, kẻo nuốt thành "lỗi" rồi đi mở trình duyệt đăng nhập ngay sau khi vừa bấm Dừng.</para>
    /// </summary>
    private async Task<KetQuaSsoChiTiet> SsoVePickerAsync(CancellationToken ct)
    {
        try
        {
            ResetState();
            StartBridgeAndLaunch(ShopeeLoginService.SubaccountAccountUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Không mở được cổng cầu nối / không phóng được trình duyệt → cũng là một đường "Lỗi": lượt đầu sẽ
            // dẫn tới đăng nhập lại, lượt hai trả lỗi cho caller (không treo vòng).
            L("Mở trình duyệt sạch lỗi: " + ex.ToString());
            return new KetQuaSsoChiTiet(KetQuaSso.Loi, "Không mở được trình duyệt sạch: " + ex.Message);
        }

        return await SsoQuaCauNoiAsync(_channel, _log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Phần SSO CHỈ nói chuyện qua cầu nối (trình duyệt sạch đã mở sẵn ở ngoài): chờ extension báo <c>ready</c> →
    /// gửi <c>gotoSellerCentre</c> → chờ chặng <c>AtSeller</c> → phân loại Ok / Captcha / Lỗi.
    /// <para>Tách khỏi <see cref="SsoVePickerAsync"/> (phần phóng trình duyệt) để test được nguyên luật phân loại
    /// bằng một client WebSocket giả làm extension, KHÔNG cần mở trình duyệt — xem <c>OrdersBridgeSsoTests</c>.</para>
    /// <para><b>Không gọi <c>ResetStages</c> ở đây</b>: chặng <c>Ready</c> phải được thay mới TRƯỚC khi phóng trình
    /// duyệt (việc của caller), kẻo xoá mất tín hiệu <c>ready</c> mà extension đã kịp gửi.</para>
    /// </summary>
    /// <param name="channel">Kênh cầu nối của phiên (cổng đã mở, trình duyệt đã phóng).</param>
    /// <param name="log">Hàm ghi nhật ký của phiên.</param>
    /// <param name="ct">Hủy của phiên (người dùng bấm Dừng).</param>
    /// <param name="hanReady">Hạn chờ extension nối cầu — mặc định <see cref="OrdersBridgeChannel.ChoChang.Ready"/>.
    /// Chỉ test rót hạn ngắn để canh được đường TREO mà không phải chờ thật 45s.</param>
    /// <param name="hanAtSeller">Hạn chờ về trang chọn shop — mặc định
    /// <see cref="OrdersBridgeChannel.ChoChang.AtSeller"/>. Chỉ test rót hạn ngắn.</param>
    internal static async Task<KetQuaSsoChiTiet> SsoQuaCauNoiAsync(
        OrdersBridgeChannel channel, Action<string>? log, CancellationToken ct,
        TimeSpan? hanReady = null, TimeSpan? hanAtSeller = null)
    {
        void L(string m) => log?.Invoke(m);

        // Captcha PHẢI được nhận diện ở MỌI lối ra, kể cả khi chặng vỡ vì lỗi/hết giờ: extension bật cờ qua
        // message "captcha" độc lập với việc chặng nào đang chờ, nên có ca cờ đã bật mà chặng vẫn hỏng.
        KetQuaSsoChiTiet? CaptchaNeuCo()
        {
            if (!channel.CaptchaSeen)
            {
                return null;
            }
            L("PHÁT HIỆN captcha/verify khi vào Seller Centre.");
            return new KetQuaSsoChiTiet(KetQuaSso.Captcha, null);
        }

        // Hai hỏng hóc rất khác nhau cùng ném TimeoutException; cờ này để câu lỗi chỉ ĐÚNG thủ phạm. Không có nó
        // thì ca "bản chép extension hỏng nên service worker chết câm" bị báo là lỗi ở bước SSO — chỉ sai chỗ cho
        // người đọc nhật ký (đã mất một đợt truy vì kiểu này: xem plans/…-orders-bridge-extension-copy-recursive).
        var daNoiCau = false;
        var hanCho = hanReady ?? OrdersBridgeChannel.ChoChang.Ready;
        try
        {
            L($"Chờ extension nối cầu (ready) — tối đa {hanCho.TotalSeconds:0}s...");
            await channel.AwaitAsync(channel.Ready, hanCho, ct).ConfigureAwait(false);
            daNoiCau = true;
            L("Extension đã nối cầu — SSO 'Kênh Người bán' để về trang chọn shop...");

            var atSellerTcs = channel.ArmAtSeller();
            await channel.SendAsync(new { action = "gotoSellerCentre" }).ConfigureAwait(false);
            var atSeller = await channel.AwaitAsync(atSellerTcs, hanAtSeller ?? OrdersBridgeChannel.ChoChang.AtSeller, ct).ConfigureAwait(false);

            if (CaptchaNeuCo() is { } captcha)
            {
                return captcha;
            }
            if (!atSeller)
            {
                return new KetQuaSsoChiTiet(KetQuaSso.Loi,
                    "Không về được trang chọn shop (/portal/shop) sau SSO — có thể sticky shop cũ / cookie hết hạn.");
            }
            return new KetQuaSsoChiTiet(KetQuaSso.Ok, null);
        }
        catch (OperationCanceledException) { throw; } // người dùng Dừng — KHÔNG biến thành lỗi rồi đi đăng nhập lại
        catch (InvalidOperationException ex)
        {
            // Extension báo error (hay gặp nhất: "bản sạch gặp trang đăng nhập subaccount (cookie hết hạn)")
            // hoặc extension chưa/không còn kết nối (SendAsync fail-fast).
            return CaptchaNeuCo() ?? new KetQuaSsoChiTiet(KetQuaSso.Loi, ex.Message);
        }
        catch (TimeoutException)
        {
            // Hết giờ = KHÔNG biết đang ở đâu → Treo (nghỉ vòng), KHÔNG đăng nhập lại. Xem KetQuaSso.Treo.
            return CaptchaNeuCo() ?? new KetQuaSsoChiTiet(KetQuaSso.Treo, daNoiCau
                ? "Hết thời gian chờ phản hồi từ extension khi vào trang chọn shop."
                : $"Extension không nối cầu trong {hanCho.TotalSeconds:0}s (bản chép extension hỏng? trình duyệt chặn?).");
        }
    }

    /// <summary>
    /// Về TRANG CHỌN SHOP (picker /portal/shop) — <b>bản sạch TRƯỚC, Playwright chỉ khi cần</b> (user chốt
    /// 2026-08-07). Thứ tự: mở trình duyệt sạch + extension ăn cookie hồ sơ → về được picker thì XONG (bỏ hẳn lượt
    /// Playwright); hỏng vì lý do khác captcha → đóng bản sạch, đăng nhập bằng trình duyệt điều khiển Playwright
    /// (<see cref="ShopeeLoginService.OpenAsync"/> + <c>TryLoginSubaccountAsync</c>), rồi mở bản sạch LẦN NỮA.
    /// <para><b>Vì sao đảo:</b> bản cũ luôn chạy Playwright trước, nên hồ sơ đã đăng nhập vẫn phải chịu một lượt
    /// mở/đóng trình duyệt (~30–60s mỗi vòng, mỗi tài khoản) và một lần bàn giao hồ sơ — chính là nguồn của lỗi
    /// "Trình duyệt thoát ngay khi khởi động".</para>
    /// Trả <c>null</c> nếu đã về picker HOẶC gặp captcha (caller phân biệt bằng
    /// <see cref="OrdersBridgeChannel.CaptchaSeen"/>); trả CHUỖI LỖI nếu hết đường. Dùng chung cho
    /// <see cref="RunLoginThenSliceAsync"/> (một shop) và <see cref="RunAllShopsAsync"/> (mọi shop). Trình duyệt
    /// điều khiển login LUÔN được đóng (finally).
    /// </summary>
    private async Task<string?> LoginAndReachPickerAsync(OrdersLoginParams login, CancellationToken ct)
    {
        // 1) THỬ BẢN SẠCH TRƯỚC — hồ sơ còn cookie thì vào thẳng picker, khỏi đụng Playwright.
        L("Mở trình duyệt sạch + extension (dùng cookie hồ sơ nếu còn hạn) — /account → SSO → trang chọn shop...");
        var lanDau = await SsoVePickerAsync(ct).ConfigureAwait(false);
        var hanhDong = QuyetDinhSauThuBanSach(lanDau.Ket, daFallback: false);
        if (hanhDong == HanhDongSauThuSach.ChayTiep)
        {
            L("Cookie hồ sơ còn hạn — đã về trang chọn shop, BỎ QUA bước đăng nhập Playwright.");
            return null;
        }
        if (hanhDong == HanhDongSauThuSach.DungVongCaptcha)
        {
            return null; // caller kiểm CaptchaSeen
        }

        // 2) Bản sạch không vào được → ĐÓNG hẳn nó rồi mới đăng nhập bằng trình duyệt điều khiển. Phải dùng
        //    DongTrinhDuyetSach (kill handle RỒI quét theo hồ sơ): Brave fork tiến trình browser thật sang PID
        //    khác nên kill-theo-handle một mình trượt, để lại cửa sổ mồ côi GIỮ HỒ SƠ → Playwright chết ngay ở
        //    bước mở ("Trình duyệt thoát ngay khi khởi động").
        L($"Bản sạch chưa vào được trang chọn shop ({lanDau.LyDo ?? "không rõ lý do"}) — đóng bản sạch, đăng nhập lại bằng Playwright.");
        DongTrinhDuyetSach();
        // Quên dòng này thì suốt pha đăng nhập (có thể vài phút — chờ người nhập mã) Process vẫn trỏ tiến trình
        // ĐÃ CHẾT, tức "bản sạch đang sống" là tín hiệu SAI với mọi chỗ đọc nó (nút Dừng, WindowFocus).
        // KHÔNG Dispose: UI thread có thể đang đọc cùng lúc, để GC lo còn hơn ném ObjectDisposedException chỗ khác.
        Process = null;
        await Task.Delay(800, ct).ConfigureAwait(false); // settle cho chắc nhả khoá file hồ sơ

        // 3) Đăng nhập bằng trình duyệt điều khiển (Playwright). try/finally: user Dừng giữa chừng vẫn đóng trình duyệt.
        var entered = false;
        ILoginSession? session = null;
        try
        {
            L("Đăng nhập Nền tảng tài khoản phụ bằng trình duyệt điều khiển (Playwright)...");
            var svc = new ShopeeLoginService();
            session = await svc.OpenAsync(_userDataDir, _browserChoice, ct, _log).ConfigureAwait(false);
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

        // 4) Settle ngắn cho chắc nhả khoá file hồ sơ (Brave vừa kill) trước khi mở lại bằng trình duyệt sạch.
        await Task.Delay(800, ct).ConfigureAwait(false);

        L("Đăng nhập xong — mở lại bằng trình duyệt sạch + extension (subaccount /account → SSO → trang chọn shop)...");
        var lanHai = await SsoVePickerAsync(ct).ConfigureAwait(false);
        // Liệt kê TƯỜNG MINH từng nhánh: gom BaoLoi với DangNhapLai vào `_` thì nếu ai sửa bảng quyết định cho
        // phép đăng nhập lại lần nữa, lượt đó sẽ IM LẶNG biến thành lỗi thay vì gãy ra để người sửa thấy.
        return QuyetDinhSauThuBanSach(lanHai.Ket, daFallback: true) switch
        {
            HanhDongSauThuSach.ChayTiep => null,          // đã về picker
            HanhDongSauThuSach.DungVongCaptcha => null,   // caller kiểm CaptchaSeen
            HanhDongSauThuSach.BaoLoi => lanHai.LyDo ?? "Không về được trang chọn shop sau khi đăng nhập lại.",
            var khac => throw new InvalidOperationException(
                $"Bảng quyết định trả {khac} ở lượt ĐÃ đăng nhập lại — không có đường lui nào nữa, phải sửa QuyetDinhSauThuBanSach."),
        };
    }

    // ── GĐ4: MỘT VÒNG qua MỌI shop: login → SSO → readShopList → từng shop (detail → toShip → syncOrders +
    //    callback lưu DB → nếu ToShip>0 xử đơn + revert địa chỉ) → đóng tab shop → nghỉ → shop kế. ─────────────
    /// <summary>
    /// GĐ4: đăng nhập + SSO về picker rồi LẶP QUA MỌI shop trong danh sách. Mỗi shop: mở Chi tiết → đọc "Chờ Lấy
    /// Hàng" → đọc đơn (tab Tất cả) → GỌI <c>syncCallback</c> (App lưu DB/GSheet/hub) → nếu ToShip&gt;0 thì đặt địa
    /// chỉ lấy hàng + Chuẩn bị hàng từng đơn + in phiếu + revert địa chỉ → đóng tab shop (về picker) → nghỉ 3-5' →
    /// shop kế. Captcha giữa chừng → dừng vòng (Captcha=true). Dùng cho nút "▶ Chạy" (production, chạy liên tục).
    /// </summary>
    /// <summary>
    /// KIỂM TRA LẠI ĐỊA CHỈ của ĐÚNG MỘT shop theo lệnh người dùng (nút "Check" trên banner lỗi địa chỉ):
    /// đăng nhập → về picker → đọc danh sách shop → mở Chi tiết ĐÚNG shop đó → chạy MỖI bước đặt địa chỉ.
    /// <para>
    /// <b>KHÔNG đọc đơn, KHÔNG chuẩn bị hàng, KHÔNG in phiếu, KHÔNG check trả hàng</b> — đây là lượt kiểm tra,
    /// không phải một vòng làm việc thu nhỏ. Người dùng bấm nút này lúc đang nhìn banner đỏ, thứ họ cần là câu
    /// trả lời "shop này còn lỗi không", không phải một lượt in phiếu ngoài ý muốn.
    /// </para>
    /// <para>
    /// Cầu nối chỉ có MỘT lane (<c>OrdersBridgeChannel.BridgePort</c> cố định) nên caller PHẢI tự chặn khi đang
    /// có phiên chạy — ở đây không tự xếp hàng.
    /// </para>
    /// <para>
    /// Tra shop theo <c>LoginName</c> rồi mới tới <c>ShopName</c>, so sánh KHÔNG phân biệt hoa/thường — đúng
    /// khuôn nhãn mà vòng shop thường ghi vào banner (<c>shopLogin</c>), kẻo bấm Check xong báo "không thấy
    /// shop" cho chính cái tên vừa hiện trên banner.
    /// </para>
    /// </summary>
    public async Task<OrdersKiemTraDiaChiResult> KiemTraDiaChiMotShopAsync(
        OrdersLoginParams login, string shopLogin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(shopLogin))
        {
            return new OrdersKiemTraDiaChiResult(false, false, "Chưa biết shop nào cần kiểm tra.");
        }

        try
        {
            var err = await LoginAndReachPickerAsync(login, ct).ConfigureAwait(false);
            if (_channel.CaptchaSeen)
            {
                return new OrdersKiemTraDiaChiResult(false, true, "Rơi vào trang verify/captcha khi vào Seller Centre.");
            }
            if (err is not null)
            {
                return new OrdersKiemTraDiaChiResult(false, false, err);
            }

            var shopListTcs = _channel.ArmShopList();
            await _channel.SendAsync(new { action = "readShopList" }).ConfigureAwait(false);
            var json = await _channel.AwaitAsync(shopListTcs, OrdersBridgeChannel.ChoChang.ShopList, ct).ConfigureAwait(false);
            var shops = ShopeeLoginService.ParseShopListJson(json);
            _onShopListRead?.Invoke(shops);

            var shop = shops.FirstOrDefault(s =>
                string.Equals(s.LoginName, shopLogin, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.ShopName, shopLogin, StringComparison.OrdinalIgnoreCase));
            if (shop is null)
            {
                return new OrdersKiemTraDiaChiResult(false, false,
                    $"Không thấy shop \"{shopLogin}\" trong danh sách {shops.Count} shop của tài khoản này.");
            }

            L($"Kiểm tra địa chỉ: mở Chi tiết shop {shopLogin}...");
            var detailTcs = _channel.ArmDetail();
            await _channel.SendAsync(new { action = "openShopDetail", shopId = shop.ShopId }).ConfigureAwait(false);
            var d = await _channel.AwaitAsync(detailTcs, OrdersBridgeChannel.ChoChang.Detail, ct).ConfigureAwait(false);
            if (_channel.CaptchaSeen || d == "captcha")
            {
                return new OrdersKiemTraDiaChiResult(false, true, "Rơi vào captcha khi mở Chi tiết shop.");
            }

            var ok = await _flow.KiemTraLaiDiaChiAsync(shopLogin, ct).ConfigureAwait(false);
            if (_channel.CaptchaSeen)
            {
                return new OrdersKiemTraDiaChiResult(false, true, "Rơi vào captcha khi đặt địa chỉ.");
            }
            return new OrdersKiemTraDiaChiResult(ok, false, null, _flow.PickupOkShop);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OrdersKiemTraDiaChiResult(false, false, ex.Message);
        }
    }

    public async Task<OrdersBridgeRunResult> RunAllShopsAsync(OrdersLoginParams login, CancellationToken ct = default)
    {
        int shopCount = 0, shopsDone = 0, totalOrders = 0, totalSlips = 0;
        var pickupFailedShops = new List<string>();
        var pickupOkShops = new List<string>();

        // Nhãn các shop ĐẶT ĐƯỢC địa chỉ trong vòng này (null khi chưa shop nào) — phải điền vào MỌI lối ra
        // NẰM TRONG/SAU vòng shop, kẻo vòng thoát giữa chừng là mất tín hiệu gỡ banner của shop đã chạy xong.
        string? NhanShopDatDuoc() => pickupOkShops.Count > 0 ? string.Join(", ", pickupOkShops) : null;

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
            _onShopListRead?.Invoke(shops); // tab "Shops": App lưu danh sách shop (mọi shop, kể cả 0 đơn).
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

                // Cột tiến độ tab "Shops": bắt đầu check shop này → chấm nhảy sang đây + bật vòng quay.
                _onShopCheckStarted?.Invoke(shopLogin);
                try
                {
                    // Mở Chi tiết shop (trusted click).
                    var detailTcs = _channel.ArmDetail();
                    await _channel.SendAsync(new { action = "openShopDetail", shopId = shop.ShopId }).ConfigureAwait(false);
                    var d = await _channel.AwaitAsync(detailTcs, OrdersBridgeChannel.ChoChang.Detail, ct).ConfigureAwait(false);
                    if (_channel.CaptchaSeen || d == "captcha")
                    {
                        return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, true, "Rơi vào captcha khi mở Chi tiết.",
                            PickupOkShops: NhanShopDatDuoc());
                    }

                    // Đọc "Chờ Lấy Hàng".
                    var toShipTcs = _channel.ArmToShip();
                    await _channel.SendAsync(new { action = "readToShip" }).ConfigureAwait(false);
                    var raw = await _channel.AwaitAsync(toShipTcs, OrdersBridgeChannel.ChoChang.ToShip, ct).ConfigureAwait(false);
                    var toShip = ShopeeDashboard.ParseToShipCount(raw);
                    L($"[Shop {i + 1}] Chờ Lấy Hàng: {(toShip?.ToString() ?? "?")}.");

                    // Đọc đơn (Phần A) + callback lưu DB + xử đơn (Phần B) + revert địa chỉ.
                    // Reset cờ "đặt được địa chỉ" NGAY TRƯỚC lời gọi: quên là shop sau thừa hưởng cờ của shop
                    // trước ⇒ GỠ NHẦM banner của shop chưa hề chạy bước địa chỉ (vd shop 0 đơn chờ lấy hàng).
                    _flow.PickupOkShop = null;
                    var (orders, slips) = await _flow.RunShopOrdersAsync(shop.ShopId, shopLogin, toShip ?? 0, ct).ConfigureAwait(false);
                    if (_channel.CaptchaSeen)
                    {
                        // Captcha giữa chừng vẫn phải mang theo tín hiệu của shop đã đặt được địa chỉ trước đó.
                        if (_flow.PickupOkShop is not null) { pickupOkShops.Add(_flow.PickupOkShop); }
                        return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders + orders, totalSlips + slips, true, "Rơi vào captcha khi đọc/xử đơn.",
                            PickupOkShops: NhanShopDatDuoc());
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
                        // Shop này ĐẶT ĐƯỢC địa chỉ (bước đó thực sự chạy) → vòng ngoài tự gỡ banner cũ của nó.
                        if (_flow.PickupOkShop is not null) { pickupOkShops.Add(_flow.PickupOkShop); }
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
                                PickupFailedShop: pickupFailedShops.Count > 0 ? string.Join(", ", pickupFailedShops) : null,
                                PickupOkShops: NhanShopDatDuoc());
                        }
                        L($"closeShopTab không về được picker sau shop CUỐI ({shopName}) — vòng đã xong, bỏ qua.");
                    }
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
                    PickupAddressFailed: true, PickupFailedShop: tenLoi, PickupOkShops: NhanShopDatDuoc());
            }
            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, null,
                PickupOkShops: NhanShopDatDuoc());
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            // Vẫn mang theo tín hiệu của những shop ĐÃ đặt được địa chỉ trước lúc gãy — banner của chúng phải
            // được gỡ dù vòng kết thúc bằng lỗi (hai đường đụng những shop KHÁC NHAU, không giẫm nhau).
            L("Cầu nối: hết thời gian chờ phản hồi từ extension.");
            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, "Hết thời gian chờ phản hồi từ extension.",
                PickupOkShops: NhanShopDatDuoc());
        }
        catch (Exception ex)
        {
            L("Cầu nối lỗi: " + ex.ToString());
            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, ex.Message,
                PickupOkShops: NhanShopDatDuoc());
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
        _onShopListRead?.Invoke(shops); // tab "Shops": App lưu danh sách shop (mọi shop, kể cả 0 đơn).
        L($"Đọc được {shops.Count} shop từ /portal/shop.");
        if (shops.Count == 0)
        {
            return new OrdersBridgeSliceResult(shops, null, null, false,
                "Không đọc được shop nào (đã tới /portal/shop chưa?).");
        }

        // 2) Mở "Chi tiết" shop đầu bằng trusted click (kỳ vọng KHÔNG captcha).
        var firstShopId = shops[0].ShopId;
        // Nhãn shop cho khớp chữ ký (callback null ở "Chạy thử" nên không dùng, nhưng phải truyền). shops[0] an toàn (đã guard rỗng ở trên).
        // "Chạy thử" dựng phiên KHÔNG rót callback nào (xem AccountsViewModel.Phien) ⇒ bước BÙ "tự tải lại phiếu
        // thiếu" trong RunShopOrdersAsync tự tắt: lát cắt này chỉ ĐỌC, không lưu DB, nên không được kéo theo nó.
        var firstShopLogin = string.IsNullOrWhiteSpace(shops[0].LoginName) ? shops[0].ShopName : shops[0].LoginName;
        L($"Mở 'Chi tiết' shop đầu (id={firstShopId}) bằng trusted click...");

        // Cột tiến độ tab "Shops": lát cắt chỉ chạy shop đầu — vẫn báo bắt đầu/xong y như vòng RunAllShopsAsync.
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

    /// <summary>
    /// Đóng trình duyệt sạch của phiên này: kill <see cref="Process"/> (nếu còn) RỒI quét theo hồ sơ để chắc không
    /// sót tiến trình nào.
    /// <para><b>Vì sao không chỉ kill handle:</b> Brave/Chrome có thể fork tiến trình browser THẬT sang PID khác rồi
    /// stub thoát — lúc đó <c>Process.HasExited</c> đã true nên kill trượt, browser thật sống mồ côi giữ hồ sơ, và
    /// vòng SAU chết ngay ở bước đăng nhập ("Trình duyệt thoát ngay khi khởi động"). Quét theo <c>--user-data-dir</c>
    /// bắt được cả bản fork.</para>
    /// KHÔNG match 'shopee-orders' — chỉ đụng hồ sơ của chính phiên này, không cướp cửa sổ của phiên khác.
    /// Best-effort: nuốt mọi lỗi.
    /// </summary>
    public void DongTrinhDuyetSach()
    {
        try
        {
            var p = Process;
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch { /* bỏ qua */ }

        try { BrowserProfileGuard.FreeProfile(_userDataDir, alsoMatchBridgeExtension: false, _log); }
        catch { /* bỏ qua */ }
    }

    public void Dispose() => _channel.Dispose();
}
