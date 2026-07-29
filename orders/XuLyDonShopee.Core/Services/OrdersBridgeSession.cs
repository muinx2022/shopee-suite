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

/// <summary>GĐ4: kết quả một VÒNG qua MỌI shop (<see cref="OrdersBridgeSession.RunAllShopsAsync"/>).</summary>
/// <param name="ShopCount">Số shop đọc được từ picker.</param>
/// <param name="ShopsDone">Số shop đã xử trọn trong vòng này.</param>
/// <param name="TotalOrders">Tổng số đơn đọc được (cộng qua các shop).</param>
/// <param name="TotalSlips">Tổng số phiếu PDF đã lưu.</param>
/// <param name="Captcha">True nếu dừng vòng vì captcha/verify.</param>
/// <param name="Error">Thông báo lỗi (null nếu vòng chạy trọn không lỗi).</param>
/// <param name="PickupAddressFailed">True nếu dừng vòng vì KHÔNG đặt được địa chỉ lấy hàng (chưa in phiếu nào
/// cho shop đó) — caller cảnh báo ra kênh ngoài. KHÁC <paramref name="Captcha"/>: nguyên nhân khác, xử khác.</param>
/// <param name="PickupFailedShop">Nhãn shop không đặt được địa chỉ (null nếu không dính lỗi này).</param>
public sealed record OrdersBridgeRunResult(
    int ShopCount, int ShopsDone, int TotalOrders, int TotalSlips, bool Captcha, string? Error,
    bool PickupAddressFailed = false, string? PickupFailedShop = null);

/// <summary>Quyết định sau bước "đặt địa chỉ lấy hàng" (xem <see cref="OrdersBridgeSession.QuyetDinhSauDatDiaChi"/>).</summary>
internal enum SauDatDiaChi
{
    /// <summary>Đã đặt được địa chỉ → chạy tiếp vòng Chuẩn bị hàng (in phiếu).</summary>
    XuDon,

    /// <summary>Rơi vào captcha/verify → dừng shop này (hành vi cũ, không đổi).</summary>
    DungViCaptcha,

    /// <summary>KHÔNG đặt được địa chỉ lấy hàng → DỪNG, tuyệt đối không in phiếu (phiếu sẽ sai địa chỉ).</summary>
    DungViDiaChi,
}

/// <summary>
/// Vòng đời MỘT phiên cầu nối: cấp cổng loopback trống → chạy <see cref="OrdersWebSocketServer"/> →
/// mở trình duyệt SẠCH (không CDP, không remote-debugging-port) qua <see cref="PocCleanLauncher"/> với
/// <c>startUrl</c> có hash <c>#_od_ws=&lt;port&gt;</c> để extension đọc cổng → chờ extension báo <c>ready</c>.
/// <list type="bullet">
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
    // GĐ4: callback do App rót — gọi sau khi đọc xong đơn MỖI shop để App lưu DB/GSheet/hub (Core không ref App).
    private readonly Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>? _syncCallback;
    // Tab "Kết quả": callback do App rót (Core không ref App/DB) — CHỈ THÊM lời gọi, không đổi luồng. Null-safe.
    //  · _onShopListRead: gọi ngay sau khi parse xong danh sách shop → App lưu account_shops (mọi shop, kể cả 0 đơn).
    //  · _onOrderPrepared: gọi mỗi khi chuẩn bị xong 1 đơn (mỗi prep = 1 đơn arrange), kèm (nhãn shop, MÃ ĐƠN) →
    //    App +1 prepare_daily theo shop/ngày VÀ đánh dấu chính đơn đó "đã chuẩn bị hàng lúc nào" (nguồn đếm chung trên hub).
    //  · _onShopCheckStarted/_onShopCheckFinished: cột tiến độ — bắt đầu/xong MỘT shop (nhãn shop = khóa prepare_daily).
    private readonly Action<IReadOnlyList<ShopListItem>>? _onShopListRead;
    private readonly Action<string, string>? _onOrderPrepared;
    private readonly Action<string>? _onShopCheckStarted;
    private readonly Action<string>? _onShopCheckFinished;
    // App rót tập order_sn ĐÃ có "Số tiền cuối cùng" trong DB → bỏ qua, không mở lại chi tiết mỗi chu kỳ. null → không lọc.
    private readonly Func<IReadOnlySet<string>>? _finalDoneSns;
    // Bước CUỐI flow shop — check đơn trả hàng (callback do App rót vì Core không biết accountId). Null → bỏ hẳn bước.
    //  · _returnCountLast: mốc "số yêu cầu" lần check trước của shop (null = shop chưa từng check → chỉ ghi nhớ).
    //  · _saveReturnCount: ghi mốc mới SAU khi xử xong (kể cả lượt không check dòng nào).
    //  · _saveReturnCodes: lưu các cặp (mã đơn, mã yêu cầu) vào đơn → trả chuỗi tóm tắt để log.
    private readonly Func<string, int?>? _returnCountLast;
    private readonly Action<string, int>? _saveReturnCount;
    private readonly Func<IReadOnlyList<YeuCauTraHang>, string>? _saveReturnCodes;
    // Tập rỗng dùng khi _finalDoneSns null (tránh cấp phát mỗi shop).
    private static readonly IReadOnlySet<string> EmptyFinalSet = new HashSet<string>();
    // GĐ4: khoảng nghỉ ngẫu nhiên giữa các shop (ms) — kiểu người, tránh dồn dập.
    private readonly Random _rng = new();

    private OrdersWebSocketServer? _ws;

    /// <summary>GĐ3: kết quả extension "chuẩn bị hàng" 1 đơn (mã đơn + URL tab phiếu). null qua TCS = hết đơn.</summary>
    private sealed record PrepareResult(string OrderCode, string SlipTabUrl, string SlipBase64, string? Tracking);

    // Cờ hoàn tất từng chặng — tạo mới mỗi lần chạy; RunContinuationsAsynchronously để continuation KHÔNG chạy
    // trên thread nhận WebSocket (tránh nghẽn vòng nhận / deadlock).
    private TaskCompletionSource<bool> _readyTcs = NewTcs<bool>();
    private TaskCompletionSource<bool> _atSellerTcs = NewTcs<bool>();          // bản sạch: SSO về trang chọn shop
    private TaskCompletionSource<string?> _shopListTcs = NewTcs<string?>();
    private TaskCompletionSource<string> _detailTcs = NewTcs<string>();        // "ok" | "captcha"
    private TaskCompletionSource<string?> _toShipTcs = NewTcs<string?>();
    private TaskCompletionSource<string?> _ordersTcs = NewTcs<string?>();      // GĐ3: JSON mảng đơn
    private TaskCompletionSource<string?> _finalsTcs = NewTcs<string?>();      // GĐ4: JSON mảng {orderSn, finalText} (Số tiền cuối cùng)
    private TaskCompletionSource<bool> _pickupTcs = NewTcs<bool>();            // GĐ3: đặt địa chỉ lấy hàng xong
    private TaskCompletionSource<bool> _pickupOtherTcs = NewTcs<bool>();       // GĐ3: set địa chỉ VỀ địa chỉ khác xong
    private TaskCompletionSource<PrepareResult?> _prepareTcs = NewTcs<PrepareResult?>(); // GĐ3: 1 đơn (null=hết)
    private TaskCompletionSource<bool> _closeShopTcs = NewTcs<bool>();         // GĐ4: đóng tab shop, về picker xong
    private TaskCompletionSource<string?> _redownloadTcs = NewTcs<string?>();  // Tải lại phiếu 1 đơn (base64; ""/null=không lấy được)
    private TaskCompletionSource<string?> _returnsTcs = NewTcs<string?>();     // Bước cuối: JSON trang trả hàng

    // Chặng đang chờ (để extension báo "error" CHỈ fault đúng chặng đó — xem StageWaiter). Xem OnMessage case "error".
    private readonly StageWaiter _waiter = new();

    private bool _captchaSeen;

    // Nhãn shop KHÔNG đặt được địa chỉ lấy hàng (null = chưa dính). Cùng khuôn cờ với _captchaSeen: RunShopOrdersAsync
    // đặt, vòng ngoài (RunAllShopsAsync/RunSliceCoreAsync) đọc để DỪNG và trả lý do — KHÔNG đẻ kênh sự kiện thứ hai.
    private string? _pickupFailedShop;

    /// <summary>
    /// Nhớ chặng TCS ĐANG được await để khi extension báo <c>error</c> ta CHỈ fault đúng chặng đó — không fault
    /// hàng loạt TCS không ai await (11 chặng → 10 exception mồ côi → <c>UnobservedTaskException</c>). Các chặng
    /// không được await được để NGUYÊN (pending → GC lặng, KHÔNG raise unobserved) rồi <see cref="ResetTcs"/> thay mới.
    /// <para>Tách thành lớp riêng (internal) để test được cơ chế mà không cần mở trình duyệt.</para>
    /// </summary>
    internal sealed class StageWaiter
    {
        private volatile Action<Exception>? _faultCurrent;

        /// <summary>Await <paramref name="tcs"/> (kèm timeout + ct) đồng thời ĐĂNG KÝ nó là "chặng hiện tại":
        /// <see cref="FaultCurrent"/> sẽ fault ĐÚNG tcs này. Khôi phục chặng trước ở finally (các flow tuần tự nên
        /// thường là null giữa hai chặng).</summary>
        public async Task<T> AwaitAsync<T>(TaskCompletionSource<T> tcs, TimeSpan timeout, CancellationToken ct)
        {
            var prev = _faultCurrent;
            _faultCurrent = ex => tcs.TrySetException(ex);
            try { return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            finally { _faultCurrent = prev; }
        }

        /// <summary>Fault CHỈ chặng đang chờ (nếu có). Không có ai chờ → no-op (không tạo task mồ côi).</summary>
        public void FaultCurrent(Exception ex) => _faultCurrent?.Invoke(ex);
    }

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
        Func<IReadOnlyList<YeuCauTraHang>, string>? saveReturnCodes = null)
    {
        _userDataDir = userDataDir;
        _browserChoice = browserChoice;
        _log = log;
        _invoiceDir = invoiceDir;
        _province = string.IsNullOrWhiteSpace(province) ? "Thanh Hóa" : province;
        _syncCallback = syncCallback;
        _finalDoneSns = finalDoneSns;
        _onShopListRead = onShopListRead;
        _onOrderPrepared = onOrderPrepared;
        _onShopCheckStarted = onShopCheckStarted;
        _onShopCheckFinished = onShopCheckFinished;
        _returnCountLast = returnCountLast;
        _saveReturnCount = saveReturnCount;
        _saveReturnCodes = saveReturnCodes;
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
        _finalsTcs = NewTcs<string?>();
        _pickupTcs = NewTcs<bool>();
        _pickupOtherTcs = NewTcs<bool>();
        _prepareTcs = NewTcs<PrepareResult?>();
        _closeShopTcs = NewTcs<bool>();
        _redownloadTcs = NewTcs<string?>();
        _returnsTcs = NewTcs<string?>();
        _captchaSeen = false;
        _pickupFailedShop = null;
    }

    /// <summary>
    /// HÀM THUẦN (test được, không cần trình duyệt) — quyết định làm gì sau khi extension trả lời bước
    /// <c>setPickupAddress</c>:
    /// <list type="bullet">
    /// <item><paramref name="captchaSeen"/> → <see cref="SauDatDiaChi.DungViCaptcha"/> (ưu tiên: captcha nuốt luôn
    /// <c>pickupOk=false</c>, thông điệp phải là captcha kẻo người trực xử nhầm).</item>
    /// <item><paramref name="pickupOk"/> = false → <see cref="SauDatDiaChi.DungViDiaChi"/>: DỪNG, KHÔNG in phiếu.
    /// Bài học 28/07: app biết chưa đặt được địa chỉ mà vẫn in phiếu + giao đơn ⇒ shipper tới sai chỗ lấy hàng.</item>
    /// <item>còn lại → <see cref="SauDatDiaChi.XuDon"/>.</item>
    /// </list>
    /// </summary>
    internal static SauDatDiaChi QuyetDinhSauDatDiaChi(bool pickupOk, bool captchaSeen)
        => captchaSeen ? SauDatDiaChi.DungViCaptcha
            : pickupOk ? SauDatDiaChi.XuDon
            : SauDatDiaChi.DungViDiaChi;

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
            if (_captchaSeen)
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
            L("Cầu nối lỗi: " + ex.Message);
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Đăng nhập Playwright + SSO qua bản sạch để về TRANG CHỌN SHOP (picker /portal/shop). Trả <c>null</c> nếu
    /// đã về picker (kiểm <see cref="_captchaSeen"/> để phân biệt captcha — cũng trả null nhưng cờ bật); trả CHUỖI
    /// LỖI nếu login/SSO thất bại. Dùng chung cho <see cref="RunLoginThenSliceAsync"/> (một shop) và
    /// <see cref="RunAllShopsAsync"/> (mọi shop). Trình duyệt điều khiển login LUÔN được đóng (finally).
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
            return "Đăng nhập subaccount chưa xong (nhập mã?). Bấm lại để thử tiếp.";
        }

        // 2) Settle ngắn cho chắc nhả khoá file hồ sơ (Brave vừa kill) trước khi mở lại bằng trình duyệt sạch.
        await Task.Delay(800, ct).ConfigureAwait(false);

        // 3) Mở lại bằng trình duyệt SẠCH + extension tại /account → extension SSO "Kênh Người bán" → picker.
        //    KHÔNG mở thẳng /portal/shop vì Shopee sticky-redirect vào shop mở lần trước (server-side).
        L("Đăng nhập xong — mở lại bằng trình duyệt sạch + extension (subaccount /account → SSO → trang chọn shop)...");
        ResetTcs();
        StartBridgeAndLaunch(ShopeeLoginService.SubaccountAccountUrl);
        L("Chờ extension nối cầu (ready) — tối đa 45s...");
        await _waiter.AwaitAsync(_readyTcs, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        L("Extension đã nối cầu — SSO 'Kênh Người bán' để về trang chọn shop...");

        _atSellerTcs = NewTcs<bool>();
        await _ws!.SendAsync(new { action = "gotoSellerCentre" }).ConfigureAwait(false);
        var atSeller = await _waiter.AwaitAsync(_atSellerTcs, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (_captchaSeen)
        {
            L("PHÁT HIỆN captcha/verify khi vào Seller Centre.");
            return null; // caller kiểm _captchaSeen
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
        try
        {
            var err = await LoginAndReachPickerAsync(login, ct).ConfigureAwait(false);
            if (_captchaSeen)
            {
                return new OrdersBridgeRunResult(0, 0, 0, 0, true, "Rơi vào trang verify/captcha khi vào Seller Centre.");
            }
            if (err is not null)
            {
                return new OrdersBridgeRunResult(0, 0, 0, 0, false, err);
            }

            // Đọc danh sách shop (picker).
            _shopListTcs = NewTcs<string?>();
            await _ws!.SendAsync(new { action = "readShopList" }).ConfigureAwait(false);
            var json = await _waiter.AwaitAsync(_shopListTcs, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
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
                    // Mở Chi tiết shop (trusted click).
                    _detailTcs = NewTcs<string>();
                    await _ws.SendAsync(new { action = "openShopDetail", shopId = shop.ShopId }).ConfigureAwait(false);
                    var d = await _waiter.AwaitAsync(_detailTcs, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
                    if (_captchaSeen || d == "captcha")
                    {
                        return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, true, "Rơi vào captcha khi mở Chi tiết.");
                    }

                    // Đọc "Chờ Lấy Hàng".
                    _toShipTcs = NewTcs<string?>();
                    await _ws.SendAsync(new { action = "readToShip" }).ConfigureAwait(false);
                    var raw = await _waiter.AwaitAsync(_toShipTcs, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    var toShip = ShopeeDashboard.ParseToShipCount(raw);
                    L($"[Shop {i + 1}] Chờ Lấy Hàng: {(toShip?.ToString() ?? "?")}.");

                    // Đọc đơn (Phần A) + callback lưu DB + xử đơn (Phần B) + revert địa chỉ.
                    var (orders, slips) = await RunShopOrdersAsync(shop.ShopId, shopLogin, toShip ?? 0, ct).ConfigureAwait(false);
                    if (_captchaSeen)
                    {
                        return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders + orders, totalSlips + slips, true, "Rơi vào captcha khi đọc/xử đơn.");
                    }
                    if (_pickupFailedShop is not null)
                    {
                        // Không đặt được địa chỉ lấy hàng → BỎ LUÔN các shop còn lại: modal địa chỉ hỏng thường do
                        // Shopee đổi giao diện / extension lỗi ⇒ shop sau cũng hỏng, chạy tiếp chỉ tổ in thêm phiếu sai.
                        L($"⛔ Dừng cả vòng của tài khoản (bỏ {shops.Count - i - 1} shop còn lại) — sửa được địa chỉ rồi hãy chạy lại.");
                        return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders + orders, totalSlips + slips, false,
                            $"Không đặt được địa chỉ lấy hàng ({_province}) ở shop {_pickupFailedShop} — đã dừng vòng, chưa in phiếu nào cho shop này.",
                            PickupAddressFailed: true, PickupFailedShop: _pickupFailedShop);
                    }
                    totalOrders += orders;
                    totalSlips += slips;
                    shopsDone++;

                    // Đóng tab shop → về picker /portal/shop (listTabId picker giữ nguyên; extension đóng shopTabId).
                    // ⚠ PHẢI đọc cờ ok: bản trước vứt giá trị trả về, nên hễ picker không sẵn sàng là shop KẾ chết
                    // với thông báo lạc đề "chờ 30s chưa thấy tab shop mở" (3/3 lần trong nhật ký production, luôn
                    // đi ngay sau một lượt trang trả hàng không render).
                    if (!await DongTabShopAsync(ct).ConfigureAwait(false))
                    {
                        // Shop CUỐI thì picker hỏng không hại ai — vòng đằng nào cũng kết thúc và vòng sau mở cửa
                        // sổ mới. Chỉ dừng khi CÒN shop phía sau, kẻo báo động giả ở đúng lúc mọi việc đã xong.
                        if (i < shops.Count - 1)
                        {
                            L($"⛔ Dừng cả vòng của tài khoản (bỏ {shops.Count - i - 1} shop còn lại) — không đưa "
                              + $"được về trang chọn shop sau shop {shopName}.");
                            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false,
                                $"Không quay lại được trang chọn shop sau shop {shopName} — đã dừng vòng (shop kế "
                                + "sẽ không mở được). Sẽ thử lại ở vòng sau.");
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

            L($"Xong 1 vòng: {shopsDone}/{shopCount} shop, {totalOrders} đơn, {totalSlips} phiếu.");
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
            L("Cầu nối lỗi: " + ex.Message);
            return new OrdersBridgeRunResult(shopCount, shopsDone, totalOrders, totalSlips, false, ex.Message);
        }
    }

    /// <summary>
    /// <b>Tải LẠI phiếu MỘT đơn qua cầu nối extension</b> (nút "Tải phiếu" màn Đơn hàng). Gửi action
    /// <c>redownloadSlip</c> (extension về danh sách "Tất cả" → định vị card theo <paramref name="orderSn"/> →
    /// bấm "In phiếu giao" → tải PDF trong tab awbprint → trả base64) rồi LƯU PDF vào <see cref="_invoiceDir"/>
    /// đúng khuôn <see cref="TrySaveSlip"/> (tên file <c>SanitizeFileName(orderSn).pdf</c> — khớp chỗ
    /// <c>TryReadSlipBase64</c>/<c>ThieuPhieu</c> đọc, để cột "thiếu phiếu" tự hết đỏ). Trả <c>true</c> khi lưu
    /// được PDF hợp lệ. Ràng buộc mô hình cầu nối: phiên đang mở tab của shop nào thì tải lại được đơn của shop
    /// đó (extension quét danh sách trên tab đang mở) — đơn của shop khác/quá cũ → extension trả base64 rỗng → false.
    /// <para>
    /// FAIL-FAST: <see cref="OrdersWebSocketServer.SendAsync"/> ném <see cref="InvalidOperationException"/> khi
    /// extension chưa/không còn kết nối → NÉM ra ngoài cho caller (App) báo đúng "extension chưa kết nối", KHÔNG
    /// ngồi chờ timeout. Hủy chủ động (<paramref name="ct"/>) ném <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    public async Task<bool> RedownloadSlipAsync(string orderSn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderSn))
        {
            return false;
        }
        if (_ws is null)
        {
            throw new InvalidOperationException("Cầu nối chưa khởi động (chưa mở phiên cầu nối) — không tải lại được phiếu.");
        }
        if (string.IsNullOrWhiteSpace(_invoiceDir))
        {
            L("Chưa cấu hình thư mục lưu phiếu — bỏ tải lại phiếu.");
            return false;
        }

        _redownloadTcs = NewTcs<string?>();
        L($"Tải lại phiếu đơn {orderSn} qua cầu nối extension...");
        await _ws.SendAsync(new { action = "redownloadSlip", orderSn }).ConfigureAwait(false); // ném nếu extension chưa kết nối

        // 180s: extension có thể phải duyệt vài trang danh sách + chờ tab phiếu (~30s) + tải PDF (~25s).
        var b64 = await _waiter.AwaitAsync(_redownloadTcs, TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
        if (_captchaSeen)
        {
            L($"Gặp captcha khi tải lại phiếu đơn {orderSn} — dừng.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(b64))
        {
            L($"Không nhận được phiếu đơn {orderSn} (không thấy đơn trong shop đang mở / chưa có nút In phiếu).");
            return false;
        }

        var ok = TrySaveSlip(b64, orderSn, _invoiceDir!);
        L(ok
            ? $"Đã lưu phiếu đơn {orderSn}."
            : $"Nhận được dữ liệu phiếu nhưng KHÔNG phải PDF hợp lệ (đơn {orderSn}).");
        return ok;
    }

    // Lát cắt dùng chung (GĐ1 + đuôi GĐ2): readShopList → openShopDetail(shop đầu) → readToShip.
    private async Task<OrdersBridgeSliceResult> RunSliceCoreAsync(CancellationToken ct)
    {
        // 1) Đọc danh sách shop.
        await _ws!.SendAsync(new { action = "readShopList" }).ConfigureAwait(false);
        var shopListJson = await _waiter.AwaitAsync(_shopListTcs, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
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
            await _ws.SendAsync(new { action = "openShopDetail", shopId = firstShopId }).ConfigureAwait(false);
            var detail = await _waiter.AwaitAsync(_detailTcs, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            if (detail == "captcha" || _captchaSeen)
            {
                L("PHÁT HIỆN captcha/verify khi mở Chi tiết — cần soi lại.");
                return new OrdersBridgeSliceResult(shops, firstShopId, null, true,
                    "Rơi vào trang verify/captcha khi mở Chi tiết.");
            }
            L("Đã mở tab shop (không captcha).");

            // 3) Đọc số "Chờ Lấy Hàng".
            await _ws.SendAsync(new { action = "readToShip" }).ConfigureAwait(false);
            var raw = await _waiter.AwaitAsync(_toShipTcs, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var toShip = ShopeeDashboard.ParseToShipCount(raw);
            L($"Số 'Chờ Lấy Hàng' đọc được: {(toShip?.ToString() ?? "null")} (raw='{raw}').");

            // 4) GĐ3: đọc đơn (Phần A) + nếu ToShip>0 thì xử đơn (Phần B).
            var (ordersCount, slipsSaved) = await RunShopOrdersAsync(firstShopId, firstShopLogin, toShip ?? 0, ct).ConfigureAwait(false);
            if (_captchaSeen)
            {
                return new OrdersBridgeSliceResult(shops, firstShopId, toShip, true,
                    "Rơi vào trang verify/captcha khi đọc/xử đơn.", ordersCount, slipsSaved);
            }
            if (_pickupFailedShop is not null)
            {
                // "Chạy thử" cũng phải nói THẬT: dừng vì địa chỉ, đừng báo OK (người soi sẽ tưởng đã chạy trọn).
                return new OrdersBridgeSliceResult(shops, firstShopId, toShip, false,
                    $"Không đặt được địa chỉ lấy hàng ({_province}) — đã dừng, KHÔNG in phiếu.", ordersCount, slipsSaved);
            }

            return new OrdersBridgeSliceResult(shops, firstShopId, toShip, false, null, ordersCount, slipsSaved);
        }
        finally
        {
            // XONG shop (kể cả lỗi/captcha) → tắt vòng quay, chấm ở lại.
            _onShopCheckFinished?.Invoke(firstShopLogin);
        }
    }

    // ── GĐ3: đọc đơn (Phần A) + xử đơn (Phần B) trên tab shop đang mở ───────────────────────────────────
    private async Task<(int Orders, int Slips)> RunShopOrdersAsync(string shopId, string shopLogin, int toShip, CancellationToken ct)
    {
        // Phần A — đọc đơn tab "Tất cả" (test được ngay, kể cả shop 0 đơn chờ).
        _ordersTcs = NewTcs<string?>();
        await _ws!.SendAsync(new { action = "syncOrders" }).ConfigureAwait(false);
        var ordersJson = await _waiter.AwaitAsync(_ordersTcs, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (_captchaSeen)
        {
            L("PHÁT HIỆN captcha khi đọc đơn.");
            return (0, 0);
        }
        var orders = ShopeeLoginService.ParseOrdersJson(ordersJson);
        L($"Đọc được {orders.Count} đơn (Tất cả).");

        // Lấy "Số tiền cuối cùng" (cột Ước tính) cho đơn ĐANG chuẩn bị CHƯA có final: mở CHI TIẾT từng đơn (extension) →
        // đọc [type='FinalAmount'] .amount + DANH SÁCH SẢN PHẨM (SKU/phân loại thật) trong CÙNG lần mở đó →
        // MERGE vào DTO TRƯỚC callback persist (một lần upsert). finalDoneSns = tập
        // order_sn ĐÃ có final trong DB (App rót) → bỏ, không mở lại mỗi chu kỳ. Best-effort: lỗi/timeout/captcha → vẫn lưu phần đã có.
        var done = _finalDoneSns?.Invoke() ?? EmptyFinalSet;
        // Nhóm CHÍNH (đang chuẩn bị) + nhóm BÙ (đã rời trạng thái mà vẫn thiếu ước tính, ≤7 ngày, trần 5 đơn) —
        // xem ChonDonLayUocTinh. BÙ xếp SAU chính: extension cắt trần 30 đơn/lượt từ cuối nên phần bị cắt là bù.
        var (chinhFinal, buFinal) = ChonDonLayUocTinh(orders, done, DateTime.Now);
        var needFinal = chinhFinal.Concat(buFinal).ToList();
        if (needFinal.Count > 0)
        {
            try
            {
                _finalsTcs = NewTcs<string?>();
                await _ws.SendAsync(new
                {
                    action = "syncOrderFinals",
                    orders = needFinal.Select(o => new { orderSn = o.OrderSn, shopeeOrderId = o.ShopeeOrderId }),
                }).ConfigureAwait(false);
                // Đủ thời gian mở tuần tự nhiều tab chi tiết (20s/đơn), trần cứng 300s.
                var timeout = TimeSpan.FromSeconds(Math.Min(300, 20 + 20 * needFinal.Count));
                var finalsJson = await _waiter.AwaitAsync(_finalsTcs, timeout, ct).ConfigureAwait(false);
                if (_captchaSeen)
                {
                    L("PHÁT HIỆN captcha khi mở chi tiết lấy Số tiền cuối cùng — bỏ bước final (vẫn lưu phần đã có).");
                }
                else
                {
                    MergeFinalAmounts(orders, finalsJson, L);
                    // Đếm SAU khi merge: cả hai nhóm đều được chọn với FinalAmount là null nên khác null = vừa lấy được.
                    if (chinhFinal.Count > 0)
                    {
                        L($"Lấy Số tiền cuối cùng: {chinhFinal.Count(o => o.FinalAmount is not null)}/{chinhFinal.Count} đơn.");
                    }
                    if (buFinal.Count > 0)
                    {
                        L($"Lấy bù Số tiền cuối cùng (đơn đã rời trạng thái chuẩn bị): {buFinal.Count(o => o.FinalAmount is not null)}/{buFinal.Count} đơn.");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { L("Lấy Số tiền cuối cùng lỗi: " + ex.Message + " — vẫn lưu phần đã có."); }
        }

        // GĐ4: App lưu DB/GSheet/hub cho shop này (callback do App rót; null ở đường "Chạy thử" → chỉ đọc, không lưu).
        if (_syncCallback is not null)
        {
            try { await _syncCallback(shopId, shopLogin, orders, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { L("Lưu đơn (DB/GSheet/hub) lỗi: " + ex.Message); }
        }

        // Phần B — chỉ khi có đơn Chờ Lấy Hàng VÀ có thư mục lưu phiếu.
        var slips = 0;
        if (toShip > 0 && !string.IsNullOrWhiteSpace(_invoiceDir))
        {
            L($"Có {toShip} đơn Chờ Lấy Hàng — đặt địa chỉ lấy hàng ({_province}) rồi xử từng đơn...");
            _pickupTcs = NewTcs<bool>();
            await _ws.SendAsync(new { action = "setPickupAddress", province = _province }).ConfigureAwait(false);
            var pickupOk = await _waiter.AwaitAsync(_pickupTcs, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            var quyetDinh = QuyetDinhSauDatDiaChi(pickupOk, _captchaSeen);
            if (quyetDinh == SauDatDiaChi.DungViCaptcha)
            {
                L("PHÁT HIỆN captcha khi đặt địa chỉ lấy hàng.");
                return (orders.Count, 0);
            }
            if (quyetDinh == SauDatDiaChi.DungViDiaChi)
            {
                // KHÔNG chạy prepareNextOrder: in phiếu lúc này = phiếu sai địa chỉ lấy hàng, shipper tới sai chỗ
                // và không ai biết. Thà không giao đơn còn hơn giao sai địa chỉ (người dùng đã chốt 28/07).
                // KHÔNG revert (setPickupAddressToOther): mọi lối ok=false của extension đều CHƯA bấm "Lưu" trong
                // modal Sửa Địa chỉ ⇒ địa chỉ lấy hàng của shop còn NGUYÊN như trước vòng này, không có gì để trả về;
                // chạy revert lúc này chỉ là một lượt GHI nữa vào đúng màn hình đang hỏng.
                // Nhãn shop có thể RỖNG (picker không đọc được tên) — vẫn phải là chuỗi KHÁC null, kẻo tín hiệu
                // DỪNG (null = không lỗi) mất theo cái nhãn.
                _pickupFailedShop = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin;
                L($"⛔ Không đặt được địa chỉ lấy hàng ({_province}) — DỪNG vòng, KHÔNG in phiếu (tránh phiếu sai địa chỉ).");
                return (orders.Count, 0);
            }

            // Lặp Chuẩn bị hàng tới khi hết đơn / chốt chặn 50 đơn / captcha. Mã vận đơn bắt NGAY tại modal
            // "Thông Tin Chi Tiết" (extension đọc trước khi in phiếu) → gom theo mã đơn để cập nhật DB same-cycle.
            var capturedTracking = new Dictionary<string, string>(StringComparer.Ordinal);
            var guard = 0;
            while (guard++ < 50)
            {
                ct.ThrowIfCancellationRequested();
                _prepareTcs = NewTcs<PrepareResult?>();
                await _ws.SendAsync(new { action = "prepareNextOrder" }).ConfigureAwait(false);
                // 300s: extension chờ Shopee tạo vận đơn (≤90s) TRƯỚC khi in, rồi chờ tab phiếu (≤120s) — nới hạn cho đủ.
                var prep = await _waiter.AwaitAsync(_prepareTcs, TimeSpan.FromSeconds(300), ct).ConfigureAwait(false);
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

                // Tab "Kết quả": mỗi prep = 1 đơn arrange xong → App +1 đếm theo (shop, ngày) VÀ đánh dấu ĐÚNG đơn
                // (prep.OrderCode = order_sn — cùng khóa đang dùng cho capturedTracking/tên file phiếu) đã chuẩn bị
                // hàng, để hub đếm chung. Đếm theo ĐƠN (không theo phiếu) nên đặt TRƯỚC TrySaveSlip: phiếu lưu lỗi
                // vẫn tính đã chuẩn bị. Null-safe, không đổi luồng.
                _onOrderPrepared?.Invoke(shopLogin, prep.OrderCode);

                var saved = TrySaveSlip(prep.SlipBase64, prep.OrderCode, _invoiceDir!);
                if (saved) slips++;
                if (!string.IsNullOrWhiteSpace(prep.Tracking) && !string.IsNullOrWhiteSpace(prep.OrderCode))
                {
                    capturedTracking[prep.OrderCode] = prep.Tracking!;
                }
                L($"Đã chuẩn bị đơn {prep.OrderCode} — {(saved ? "lưu phiếu OK" : "CHƯA lưu được phiếu (kiểm tra tay)")}"
                  + (string.IsNullOrWhiteSpace(prep.Tracking) ? "" : $", mã vận đơn {prep.Tracking}") + ".");
            }
            L($"Xử đơn xong: {slips} phiếu đã lưu.");

            // Mã vận đơn bắt được NGAY lúc chuẩn bị hàng → cập nhật DTO + LƯU LẠI (DB tracking + GSheet "vận đơn mới" +
            // hub) SAME-CYCLE, khỏi chờ chu kỳ sync sau. Best-effort (lỗi không phá flow revert địa chỉ bên dưới).
            if (capturedTracking.Count > 0 && _syncCallback is not null)
            {
                var updated = 0;
                foreach (var o in orders)
                {
                    if (capturedTracking.TryGetValue(o.OrderSn, out var tn) && !string.IsNullOrWhiteSpace(tn))
                    {
                        o.TrackingNumber = tn;
                        updated++;
                    }
                }
                if (updated > 0)
                {
                    L($"Cập nhật {updated} mã vận đơn (lúc chuẩn bị hàng) → lưu lại + đẩy GSheet/hub.");
                    try { await _syncCallback(shopId, shopLogin, orders, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { L("Lưu lại mã vận đơn lỗi: " + ex.Message); }
                }
            }

            // Hết đơn → set địa chỉ lấy hàng VỀ ĐỊA CHỈ KHÁC (giữ tag "trả hàng" ở địa chỉ mặc định) — hoàn tất 1 flow shop.
            L("Set địa chỉ lấy hàng về địa chỉ khác (hoàn tất flow shop)...");
            _pickupOtherTcs = NewTcs<bool>();
            await _ws.SendAsync(new { action = "setPickupAddressToOther" }).ConfigureAwait(false);
            try { await _waiter.AwaitAsync(_pickupOtherTcs, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (TimeoutException) { L("Set địa chỉ khác: quá hạn — bỏ qua."); }
        }

        // ── Mắt xích CUỐI CÙNG của flow shop (bước PHỤ): check ĐƠN TRẢ HÀNG ──────────────────────────────
        // Bọc kín: lỗi/timeout/captcha ở đây KHÔNG được phá phần chuẩn bị hàng + in phiếu đã xong ở trên, cũng
        // KHÔNG được dừng vòng shop. Chạy cả khi toShip = 0 (yêu cầu trả hàng không liên quan đơn chờ lấy hàng).
        if (_returnCountLast is not null && _saveReturnCount is not null && _saveReturnCodes is not null
            && !string.IsNullOrWhiteSpace(shopLogin))
        {
            var captchaTruocBuoc = _captchaSeen;
            try
            {
                await CheckDonTraHangAsync(shopLogin, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                L("Check đơn trả hàng lỗi: " + ex.Message + " — bỏ qua bước này, phần đã xong không bị ảnh hưởng.");
            }
            finally
            {
                // Captcha ở bước PHỤ này KHÔNG được dừng cả vòng (phần chính của shop đã xong xuôi) → trả cờ về
                // đúng như trước bước. Captcha THẬT vẫn lộ ngay ở shop kế: openShopDetail rơi /verify → dừng vòng
                // đúng chỗ, không mất mát gì.
                _captchaSeen = captchaTruocBuoc;
            }
        }

        return (orders.Count, slips);
    }

    /// <summary>
    /// <b>Bước CUỐI flow shop — check ĐƠN TRẢ HÀNG.</b> Gửi <c>readReturnRequests</c>: extension mở trang
    /// "Trả hàng/Hoàn tiền/Hủy" của shop đang mở, CHỌN TAB "Đơn Trả hàng Hoàn tiền" (không chọn thì ô tổng là của
    /// tab "Tất cả" — gộp cả Đơn Hủy / Đơn Giao hàng không thành công, hai loại KHÔNG có mã yêu cầu trả hàng), đổi
    /// sắp xếp sang "Ngày yêu cầu (Mới - Cũ)" (mặc định trang là "Ngày đến hạn" — không đổi thì luật "N dòng đầu"
    /// bỏ sót ÂM THẦM), rồi trả text ô tổng + HTML đầu mỗi dòng.
    /// C# parse (<see cref="TraHangParser"/>), so số với MỐC lần trước của shop để biết check bao nhiêu dòng ĐẦU,
    /// ghép cặp (mã đơn, mã yêu cầu), LỌC theo cửa sổ <see cref="TraHangParser.SoNgayCuaSoTraHang"/> ngày (theo
    /// NGÀY YÊU CẦU suy từ mã yêu cầu) rồi lưu. Cập nhật mốc ở CUỐI, kể cả lượt không check dòng nào.
    /// <para>
    /// KHÔNG ném ra ngoài phần thân — caller đã bọc try/catch; ở đây chỉ return sớm + log khi không đọc được.
    /// </para>
    /// </summary>
    private async Task CheckDonTraHangAsync(string shopLogin, CancellationToken ct)
    {
        // ĐÃ dính captcha TRƯỚC bước này (vòng chuẩn bị hàng vừa break vì captcha) → bỏ hẳn, đừng gửi lệnh rồi
        // ngồi chờ 90s vô ích: trang đang là /verify nên extension không đọc được gì, mà mỗi shop tốn thêm 90s.
        if (_captchaSeen)
        {
            L("Check đơn trả hàng: đã dính captcha từ bước trước — bỏ bước này (mốc giữ nguyên).");
            return;
        }

        var mocCu = _returnCountLast!(shopLogin);

        _returnsTcs = NewTcs<string?>();
        await _ws!.SendAsync(new { action = "readReturnRequests" }).ConfigureAwait(false);
        // 90s: điều hướng sang trang trả hàng (≤20s) + đổi sắp xếp + chờ danh sách vẽ lại.
        var json = await _waiter.AwaitAsync(_returnsTcs, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        if (_captchaSeen)
        {
            L("Check đơn trả hàng: gặp captcha/verify — bỏ bước này (mốc giữ nguyên), đi tiếp.");
            return;
        }

        var doc = TraHangParser.ParseKetQua(json);
        if (doc.SoYeuCau is null)
        {
            L("Check đơn trả hàng: KHÔNG đọc được số yêu cầu (trang chưa render / Shopee đổi giao diện) — bỏ lượt, mốc giữ nguyên.");
            // 4 dấu hiệu extension thu ngay lúc bỏ lượt: phân biệt hết-giờ-THẬT / lạc-trang / sai-selector. Không
            // có nó thì nới thời gian chờ chỉ là đoán mò (xem pageChanDoanTraHang bên extension).
            if (!string.IsNullOrEmpty(doc.ChanDoan))
            {
                L("Check đơn trả hàng — chẩn đoán trang: " + doc.ChanDoan);
            }
            return;
        }
        if (!doc.SortApplied)
        {
            L("⚠ Check đơn trả hàng: KHÔNG đổi được sắp xếp sang 'Ngày yêu cầu (Mới - Cũ)' — 'N dòng đầu' có thể sót.");
        }
        if (!doc.TabTraHang)
        {
            L("⚠ Check đơn trả hàng: KHÔNG chọn được tab \"Đơn Trả hàng Hoàn tiền\" — số có thể lẫn đơn hủy.");
        }

        var soMoi = doc.SoYeuCau.Value;
        var quyetDinh = TraHangParser.QuyetDinhCheck(mocCu, soMoi);
        var mocCuText = mocCu?.ToString() ?? "chưa có";
        switch (quyetDinh.Luat)
        {
            case LuatSoYeuCau.LanDau:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — LẦN ĐẦU, check {quyetDinh.SoDongCanCheck} dòng đầu rồi ghi mốc.");
                break;
            case LuatSoYeuCau.KhongDoi:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — không đổi so với mốc {mocCuText}, bỏ qua.");
                break;
            case LuatSoYeuCau.Giam:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — GIẢM so với mốc {mocCuText} (đã xử xong), chỉ cập nhật mốc.");
                break;
            default:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — TĂNG {quyetDinh.SoDongCanCheck} so với mốc {mocCuText}, check {quyetDinh.SoDongCanCheck} dòng đầu.");
                break;
        }

        if (quyetDinh.SoDongCanCheck > 0)
        {
            var canCheck = doc.Dong.Take(quyetDinh.SoDongCanCheck).ToList();
            if (canCheck.Count < quyetDinh.SoDongCanCheck)
            {
                L($"Check đơn trả hàng: cần {quyetDinh.SoDongCanCheck} dòng nhưng trang chỉ có {canCheck.Count} — check phần đọc được.");
            }

            var ghep = TraHangParser.GhepCap(canCheck);
            var phanDonHuy = ghep.BoQuaDonHuy > 0 ? $", bỏ {ghep.BoQuaDonHuy} dòng vì href là ĐƠN HỦY" : string.Empty;
            L($"Check đơn trả hàng: đọc {canCheck.Count} dòng → {ghep.Cap.Count} cặp đủ hai mã, {ghep.ThieuMaYeuCau.Count} dòng THIẾU mã yêu cầu{phanDonHuy}.");
            // In nguyên văn tối đa 3 dòng thiếu (kèm nhãn + HTML thô): nếu luật nhận diện theo nhãn trượt thì
            // nhật ký lần chạy thật lộ ngay class/nhãn thật — class khối mã yêu cầu tới giờ vẫn CHƯA xác nhận.
            foreach (var mo in ghep.ThieuMaYeuCau.Take(3))
            {
                L("Check đơn trả hàng — dòng thiếu mã yêu cầu → " + mo);
            }

            // Chặn theo THỜI GIAN trên NGÀY YÊU CẦU (suy từ mã yêu cầu), cửa sổ TraHangParser.SoNgayCuaSoTraHang
            // = 15 ngày chính sách Shopee + biên. CỐ Ý không dùng SoNgayBuUocTinh (7 ngày, đo trên ngày ĐẶT ĐƠN,
            // cho việc lấy bù "Số tiền cuối cùng") — khác trục, khác ý nghĩa.
            var loc = TraHangParser.LocTheoCuaSo(ghep.Cap, DateTime.Now, TraHangParser.SoNgayCuaSoTraHang);
            if (loc.BoQuaViCu > 0 || loc.GiuViKhongRoNgay > 0)
            {
                var themPhan = loc.GiuViKhongRoNgay > 0
                    ? $", GIỮ {loc.GiuViKhongRoNgay} mã không đọc được ngày yêu cầu (thà thừa còn hơn mất)"
                    : string.Empty;
                L($"Check đơn trả hàng: bỏ {loc.BoQuaViCu} dòng vì yêu cầu cũ hơn {TraHangParser.SoNgayCuaSoTraHang} ngày{themPhan} — còn {loc.GiuLai.Count} mã để lưu.");
            }

            if (loc.GiuLai.Count > 0)
            {
                L("Check đơn trả hàng — lưu mã: " + _saveReturnCodes!(loc.GiuLai));
            }
        }

        // Cập nhật mốc SAU khi xử xong (kể cả lượt không check dòng nào) để lần sau so đúng.
        _saveReturnCount!(shopLogin, soMoi);
    }

    /// <summary>Số lần gửi <c>closeShopTab</c> tối đa cho MỘT shop: lần đầu + đúng MỘT lần thử lại.</summary>
    private const int SoLanThuDongTabShop = 2;

    /// <summary>
    /// Đóng tab shop rồi đưa picker <c>/portal/shop</c> về trạng thái SẴN SÀNG cho shop kế. Trả <c>false</c> khi
    /// vẫn không sẵn sàng sau <see cref="SoLanThuDongTabShop"/> lần — caller DỪNG vòng kèm đúng lý do.
    /// <para>
    /// <b>Vì sao gửi LẠI chính <c>closeShopTab</c> làm bước hồi phục</b> (chứ không thêm lệnh mới): ở lần hai,
    /// <c>shopTabId</c> bên extension đã null nên <c>doCloseShopTab</c> rơi vào nhánh khác hẳn — điều hướng THẲNG
    /// <c>listTabId</c> về <c>/portal/shop</c>, chờ tab load xong rồi <c>ensureShopPicker</c>. Đó đúng là "đưa
    /// picker về trạng thái sạch", dùng ngay đường đã có thay vì đẻ thêm một lệnh cầu nối phải nuôi.
    /// </para>
    /// <para>Hết giờ (30s/lần) KHÔNG ném ra ngoài — nó chỉ là một lần thử trượt, cùng nghĩa với <c>ok=false</c>.</para>
    /// </summary>
    private async Task<bool> DongTabShopAsync(CancellationToken ct)
    {
        for (var lan = 1; lan <= SoLanThuDongTabShop; lan++)
        {
            _closeShopTcs = NewTcs<bool>();
            await _ws!.SendAsync(new { action = "closeShopTab" }).ConfigureAwait(false);

            var ok = false;
            try
            {
                ok = await _waiter.AwaitAsync(_closeShopTcs, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                L($"closeShopTab quá hạn (lần {lan}/{SoLanThuDongTabShop}).");
            }

            if (ok)
            {
                return true;
            }
            if (lan < SoLanThuDongTabShop)
            {
                L("closeShopTab báo CHƯA về được trang chọn shop — thử đưa picker về trạng thái sạch một lần nữa.");
            }
        }
        return false;
    }

    /// <summary>Lý do đọc được từ cờ <c>nguon</c> extension gửi kèm (<c>pageChanDoanUocTinh</c>) — chỉ để LOG.</summary>
    private static string LyDoHutUocTinh(string? nguon) => nguon switch
    {
        "dang-tai" => "thẻ đang tải, hết giờ",
        "khong-thay" => "không thấy thẻ",
        _ => "không rõ",
    };

    /// <summary>Trần số đơn ĐÃ RỜI trạng thái "chuẩn bị hàng" được lấy BÙ "Số tiền cuối cùng" mỗi lượt. TÁCH RIÊNG
    /// khỏi trần 30 đơn/lượt của phần chính (extension) và xếp SAU phần chính, để đơn đang chuẩn bị — việc gấp —
    /// KHÔNG bị đơn cũ chiếm chỗ. Đừng nới: mỗi đơn bù là một lượt mở tab chi tiết, nới là nổ số tab + rủi ro captcha.</summary>
    internal const int MaxBuUocTinh = 5;

    /// <summary>Số ngày lùi tối đa (theo NGÀY trong mã đơn) còn được lấy bù "Số tiền cuối cùng" — đủ phủ vòng đời
    /// một đơn; cũ hơn thì coi như bỏ hẳn, đừng mở lại mãi.</summary>
    internal const int SoNgayBuUocTinh = 7;

    /// <summary>Ngày đặt đơn suy từ <b>6 ký tự ĐẦU</b> mã đơn Shopee (<c>yyMMdd</c>: "260726PN0HHCS5" → 26/07/2026).
    /// <para><see cref="SyncedOrder"/> KHÔNG có trường ngày (DTO quét DOM danh sách không đọc ngày đặt), mà mã đơn
    /// thì luôn mở đầu bằng ngày — đây là nguồn ngày DUY NHẤT có sẵn. Không đủ 6 chữ số / không phải ngày hợp lệ →
    /// <c>null</c> ⇒ đơn đó KHÔNG được lấy bù (thà bỏ còn hơn đoán).</para></summary>
    internal static DateTime? NgayDonTuMa(string? orderSn)
    {
        if (string.IsNullOrWhiteSpace(orderSn) || orderSn!.Length < 6)
        {
            return null;
        }

        var dau = orderSn[..6];
        foreach (var c in dau)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }

        return DateTime.TryParseExact(dau, "yyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Chọn đơn cần mở TRANG CHI TIẾT để lấy "Số tiền cuối cùng", tách làm HAI nhóm:
    /// <list type="bullet">
    /// <item><b>Chính</b> — đơn đang "chuẩn bị hàng"/"chờ lấy hàng" chưa có ước tính (luật cũ, không đổi).</item>
    /// <item><b>Bù</b> — đơn ĐÃ RỜI trạng thái đó mà vẫn thiếu ước tính, còn trong <see cref="SoNgayBuUocTinh"/> ngày.
    /// Không có nhóm này thì đơn nào rời trạng thái trước khi kịp lấy ước tính là mất VĨNH VIỄN (ô "tiền bán" trên
    /// Google Sheet trống mãi) — có đơn hỏng CÓ HỆ THỐNG, thử lại bao nhiêu lượt cũng không ra. Bù được chốt chặn
    /// bằng <see cref="MaxBuUocTinh"/> và ưu tiên đơn MỚI trước (Shopee còn dữ liệu, khả năng lấy được cao hơn).</item>
    /// </list>
    /// Đơn ĐÃ HỦY bị loại khỏi nhóm bù ("Số tiền cuối cùng" chỉ có nghĩa với đơn còn sống; luật cũ đã loại sẵn
    /// vì đơn hủy không ở trạng thái chuẩn bị). Cả hai nhóm đều đòi có <c>ShopeeOrderId</c> (không có thì không mở
    /// được trang chi tiết) và không nằm trong <paramref name="done"/>.
    /// <para>internal (không private) để test được luật chọn mà không cần mở trình duyệt — như
    /// <see cref="MergeFinalAmounts"/>.</para></summary>
    /// <param name="homNay">Ngày "hôm nay" (giờ máy) để tính cửa sổ <see cref="SoNgayBuUocTinh"/> ngày.</param>
    internal static (List<SyncedOrder> Chinh, List<SyncedOrder> Bu) ChonDonLayUocTinh(
        IReadOnlyList<SyncedOrder> orders, IReadOnlySet<string> done, DateTime homNay)
    {
        bool ThieuUocTinh(SyncedOrder o) =>
            o.FinalAmount is null
            && !string.IsNullOrWhiteSpace(o.ShopeeOrderId)
            && !done.Contains(o.OrderSn);

        var chinh = orders
            .Where(o => ShopeeShippingNav.LaChuanBiHang(o.Status) && ThieuUocTinh(o))
            .ToList();

        var mocCu = homNay.Date.AddDays(-SoNgayBuUocTinh);
        var bu = orders
            .Where(o => !ShopeeShippingNav.LaChuanBiHang(o.Status)
                && ThieuUocTinh(o)
                && !ShopeeShippingNav.LaDonHuy(o.Status, o.StatusDescription, o.CancelReason)
                && NgayDonTuMa(o.OrderSn) is DateTime ngay
                && ngay >= mocCu
                // Nới biên trên 1 ngày: ngày trong mã đơn là giờ Việt Nam, đồng hồ/múi giờ máy lệch một chút là
                // đơn VỪA đặt hôm nay — ca đáng lấy bù nhất — bị loại oan. Xa hơn 1 ngày thì mã đó là rác.
                && ngay <= homNay.Date.AddDays(1))
            .OrderByDescending(o => NgayDonTuMa(o.OrderSn)!.Value)   // đơn MỚI trước
            .ThenByDescending(o => o.OrderSn, StringComparer.Ordinal) // hoà ngày → thứ tự ĐỊNH được, khỏi phập phù
            .Take(MaxBuUocTinh)
            .ToList();

        return (chinh, bu);
    }

    /// <summary>Parse JSON mảng <c>{orderSn, finalText, sanPham:[…]}</c> (extension trả) → map theo <c>orderSn</c> → gán
    /// <see cref="SyncedOrder.FinalAmount"/> (<see cref="ShopeeShippingNav.ParseVndAmount"/>) + <see cref="SyncedOrder.FinalAmountText"/>
    /// cho đơn khớp trong <paramref name="orders"/> (CHỈ khi finalText khác rỗng). Trả số đơn đã gán. Best-effort (JSON lỗi → 0).
    /// <para>
    /// Gộp LUÔN danh sách SẢN PHẨM trang chi tiết (<see cref="SanPhamDonParser"/>) vào <see cref="SyncedOrder.ItemsJson"/>:
    /// trang chi tiết là nguồn CHUẨN (SKU thật, phân loại sạch, đủ sản phẩm hơn) nên THAY cả mảng; đọc được rỗng →
    /// GIỮ NGUYÊN mảng cũ quét ở trang danh sách, không xoá. Dòng meta lạ / danh sách bị cắt vì vượt trần → log
    /// qua <paramref name="log"/>, đừng nuốt im lặng.
    /// </para>
    /// <para>internal (không private) để test được luật gộp mà không cần mở trình duyệt — như
    /// <see cref="QuyetDinhSauDatDiaChi"/>.</para></summary>
    internal static int MergeFinalAmounts(IReadOnlyList<SyncedOrder> orders, string? finalsJson, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(finalsJson))
        {
            return 0;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var sanPhamMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var nhoBangDoanhThu = 0;          // đọc được TRONG KHI thẻ remote CHƯA có số ⇒ số đơn bảng doanh thu CỨU được
        var hut = new List<string>();     // đơn KHÔNG lấy được, kèm lý do phân biệt được (xem LyDoHutUocTinh)
        try
        {
            using var doc = JsonDocument.Parse(finalsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var sn = el.TryGetProperty("orderSn", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    var ft = el.TryGetProperty("finalText", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    var ng = el.TryGetProperty("nguon", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(sn))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(ft))
                    {
                        map[sn!] = ft!;
                        if (ng == "chi-bang")
                        {
                            nhoBangDoanhThu++;
                        }
                    }
                    else
                    {
                        hut.Add($"{sn} ({LyDoHutUocTinh(ng)})");
                    }
                    if (el.TryGetProperty("sanPham", out var sp) && sp.ValueKind == JsonValueKind.Array)
                    {
                        sanPhamMap[sn!] = sp.GetRawText();
                    }
                }
            }
        }
        catch { return 0; }

        // Bố cục nào đang phổ biến + đơn nào hụt vì gì — trước đây chỉ có "3/4 đơn", soi log không ra manh mối.
        if (nhoBangDoanhThu > 0)
        {
            log($"Số tiền cuối cùng: {nhoBangDoanhThu} đơn chỉ đọc được nhờ BẢNG DOANH THU trang chính (thẻ [type='FinalAmount'] chưa có số).");
        }
        if (hut.Count > 0)
        {
            var ke = string.Join(", ", hut.Take(3));
            log($"KHÔNG lấy được Số tiền cuối cùng {hut.Count} đơn: {ke}{(hut.Count > 3 ? ", …" : "")}.");
        }

        var got = 0;
        var donCoSanPham = 0;
        var tongSanPham = 0;
        foreach (var order in orders)
        {
            if (map.TryGetValue(order.OrderSn, out var finalText) && !string.IsNullOrWhiteSpace(finalText))
            {
                order.FinalAmount = ShopeeShippingNav.ParseVndAmount(finalText);
                order.FinalAmountText = finalText;
                got++;
            }

            if (!sanPhamMap.TryGetValue(order.OrderSn, out var raw))
            {
                continue;
            }
            var sanPham = SanPhamDonParser.Parse(raw);
            if (sanPham.Count == 0)
            {
                continue; // trang chi tiết không đọc được sản phẩm nào → GIỮ NGUYÊN mảng cũ
            }

            order.ItemsJson = SanPhamDonParser.TaoItemsJson(sanPham);
            order.ItemCount = sanPham.Count; // giữ đúng bất biến "item_count = độ dài mảng items"
            donCoSanPham++;
            tongSanPham += sanPham.Count;

            if (sanPham.Any(sp => sp.BiCat))
            {
                log($"Đơn {order.OrderSn}: danh sách sản phẩm VƯỢT trần của extension — đã cắt còn {sanPham.Count}, có thể thiếu sản phẩm.");
            }
            foreach (var la in sanPham.SelectMany(sp => sp.MetaLa).Distinct(StringComparer.Ordinal).Take(3))
            {
                log($"Đơn {order.OrderSn}: dòng thông tin sản phẩm KHÔNG khớp nhãn nào → '{la}'.");
            }
        }

        if (donCoSanPham > 0)
        {
            log($"Đọc sản phẩm trang chi tiết: {donCoSanPham} đơn, {tongSanPham} sản phẩm.");
        }
        return got;
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
                    else if (kind == "finals")
                    {
                        _finalsTcs.TrySetResult(data);
                    }
                    else if (kind == "returns")
                    {
                        _returnsTcs.TrySetResult(data);
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
                    var trk = root.TryGetProperty("tracking", out var tk) ? tk.GetString() : null;
                    _prepareTcs.TrySetResult(new PrepareResult(code, slip, b64, trk));
                    break;
                }

                case "noOrder":
                    _prepareTcs.TrySetResult(null);
                    break;

                case "shopTabClosed":
                {
                    var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    _closeShopTcs.TrySetResult(ok);
                    break;
                }

                case "slipRedownloaded":
                {
                    // base64 phiếu ("" khi không thấy đơn / chưa có nút In phiếu). Rút chuỗi ra ngay (không giữ doc).
                    var b64 = root.TryGetProperty("slipBase64", out var sb) ? sb.GetString() : null;
                    _redownloadTcs.TrySetResult(b64);
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
                    // Hoàn tất mọi chặng ĐANG chờ (bất kể pha nào) để C# thoát nhanh + kiểm _captchaSeen.
                    _atSellerTcs.TrySetResult(false);
                    _detailTcs.TrySetResult("captcha");
                    _ordersTcs.TrySetResult(null);
                    _finalsTcs.TrySetResult(null);
                    _pickupTcs.TrySetResult(false);
                    _pickupOtherTcs.TrySetResult(false);
                    _prepareTcs.TrySetResult(null);
                    _closeShopTcs.TrySetResult(false);
                    _redownloadTcs.TrySetResult(null);
                    _returnsTcs.TrySetResult(null);
                    break;
                }

                case "error":
                {
                    var m = root.TryGetProperty("message", out var mm) ? mm.GetString() : "lỗi extension";
                    L("extension LỖI: " + m);
                    var ex = new InvalidOperationException("Extension báo lỗi: " + m);
                    // CHỈ fault chặng ĐANG được await (StageWaiter) để phiên thoát sớm — KHÔNG fault hàng loạt TCS
                    // không ai await (trước đây fault cả 11 → 10 exception mồ côi → UnobservedTaskException). Các chặng
                    // không await được để NGUYÊN (pending → ResetTcs thay mới ở vòng sau; task pending KHÔNG raise unobserved).
                    _waiter.FaultCurrent(ex);
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
