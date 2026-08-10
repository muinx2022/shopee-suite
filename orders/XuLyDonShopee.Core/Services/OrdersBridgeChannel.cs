using System.Text.Json;
using Shopee.Toolkit.Ws;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Chặng chờ dở bị CẦU NỐI RỚT GIỮA CHỪNG: lệnh đã bay sang extension rồi WebSocket mới đứt ⇒ lệnh đó mất hẳn
/// (extension nối lại nhưng C# <b>KHÔNG tự gửi lại</b> — <c>prepareNextOrder</c> là thao tác THẬT, gửi lại có
/// thể chuẩn bị nhầm thêm một đơn). Khác hẳn timeout thường, nơi extension vẫn cầm lệnh mà làm chậm.
/// <para>Kế thừa <see cref="TimeoutException"/> cố ý: mọi chỗ đang <c>catch (TimeoutException)</c> best-effort
/// (set địa chỉ khác, tải lại phiếu thiếu, đóng tab shop) giữ NGUYÊN hành vi cũ; ai cần phân biệt thì bắt
/// riêng kiểu này TRƯỚC nhánh <see cref="TimeoutException"/>.</para>
/// </summary>
internal sealed class CauNoiRotGiuaChangException : TimeoutException
{
    public CauNoiRotGiuaChangException(string message) : base(message) { }
}

/// <summary>
/// Nhớ chặng TCS ĐANG được await để khi extension báo <c>error</c> ta CHỈ fault đúng chặng đó — không fault
/// hàng loạt TCS không ai await (11 chặng → 10 exception mồ côi → <c>UnobservedTaskException</c>). Các chặng
/// không được await được để NGUYÊN (pending → GC lặng, KHÔNG raise unobserved) rồi
/// <see cref="OrdersBridgeChannel.ResetStages"/> thay mới.
/// <para>Cũng là chỗ RÚT NGẮN hạn của chặng đang chờ khi socket đứt — xem <see cref="RutNganHanKhiDut"/>.</para>
/// <para>Lớp riêng (internal) để test được cơ chế mà không cần mở trình duyệt.</para>
/// </summary>
internal sealed class StageWaiter
{
    private volatile Action<Exception>? _faultCurrent;
    private volatile ChangDangCho? _changHienTai;

    /// <summary>Await <paramref name="tcs"/> (kèm timeout + ct) đồng thời ĐĂNG KÝ nó là "chặng hiện tại":
    /// <see cref="FaultCurrent"/> sẽ fault ĐÚNG tcs này, <see cref="RutNganHanKhiDut"/> rút đúng hạn của nó.
    /// Khôi phục chặng trước ở finally (các flow tuần tự nên thường là null giữa hai chặng).</summary>
    public async Task<T> AwaitAsync<T>(TaskCompletionSource<T> tcs, TimeSpan timeout, CancellationToken ct)
    {
        var prevFault = _faultCurrent;
        var prevChang = _changHienTai;
        using var chang = new ChangDangCho(timeout);
        _faultCurrent = ex => tcs.TrySetException(ex);
        _changHienTai = chang;
        try { return await chang.ChoAsync(tcs.Task, ct).ConfigureAwait(false); }
        finally { _faultCurrent = prevFault; _changHienTai = prevChang; }
    }

    /// <summary>Fault CHỈ chặng đang chờ (nếu có). Không có ai chờ → no-op (không tạo task mồ côi).</summary>
    public void FaultCurrent(Exception ex) => _faultCurrent?.Invoke(ex);

    /// <summary>
    /// Socket vừa ĐỨT trong lúc một chặng đang chờ ⇒ RÚT NGẮN hạn còn lại của chặng đó xuống tối đa
    /// <paramref name="choNoiLai"/> tính từ BÂY GIỜ.
    /// <para><b>Cố ý KHÔNG fault ngay</b>: ngày 10/08/2026 có 15 nhịp đứt trong một giờ mà 14 nhịp vô hại
    /// (extension nối lại trong 1 giây và vẫn trả lời đúng). Fault ngay là giết oan 14 lượt để cứu 1.</para>
    /// </summary>
    /// <returns><c>true</c> nếu ĐANG có chặng chờ dở (để caller ghi nhật ký); không có ai chờ → no-op.</returns>
    public bool RutNganHanKhiDut(TimeSpan choNoiLai)
    {
        var chang = _changHienTai;
        if (chang is null) { return false; }
        chang.RutNganHan(choNoiLai);
        return true;
    }

    /// <summary>
    /// HẠN của MỘT chặng đang được await — rút ngắn được giữa chừng. Hạn gốc dài (Prepare 300s) là ngân sách
    /// cho extension LÀM VIỆC; khi socket đứt sau lúc lệnh đã bay thì lệnh mất hẳn, chờ nốt hạn gốc chỉ là chờ
    /// mù (13:32 → 13:37 ngày 10/08/2026: trọn 300s im lặng rồi mới ném, cả vòng chết theo).
    /// </summary>
    private sealed class ChangDangCho : IDisposable
    {
        private readonly CancellationTokenSource _hanCts = new();

        /// <summary>Hạn UTC hiện hành (ticks) — đọc/ghi qua <see cref="Interlocked"/> vì lượt rút hạn đến từ
        /// thread nhận WebSocket, không phải thread đang await.</summary>
        private long _hanTicks;

        /// <summary>Đã có lượt socket đứt TRONG lúc chặng này chờ dở — quyết định ném kiểu ngoại lệ nào.</summary>
        private volatile bool _rotGiuaChang;

        public ChangDangCho(TimeSpan han)
        {
            _hanTicks = (DateTime.UtcNow + han).Ticks;
            _hanCts.CancelAfter(han);
        }

        /// <summary>Kéo hạn về <c>bây giờ + <paramref name="conLai"/></c>. CHỈ RÚT NGẮN, không bao giờ NỚI:
        /// chặng vốn ngắn hơn (đọc DOM 30s) phải giữ nguyên hạn gốc của nó.</summary>
        public void RutNganHan(TimeSpan conLai)
        {
            _rotGiuaChang = true;
            var ticksMoi = (DateTime.UtcNow + conLai).Ticks;
            long cu;
            do
            {
                cu = Interlocked.Read(ref _hanTicks);
                if (ticksMoi >= cu) { return; }
            }
            while (Interlocked.CompareExchange(ref _hanTicks, ticksMoi, cu) != cu);

            // Chặng có thể vừa xong + Dispose xen giữa — hạn không còn ý nghĩa nữa, bỏ qua.
            try { _hanCts.CancelAfter(conLai); } catch (ObjectDisposedException) { }
        }

        /// <summary>Chờ <paramref name="task"/> tới khi xong / hết hạn / user Dừng.</summary>
        public async Task<T> ChoAsync<T>(Task<T> task, CancellationToken ct)
        {
            using var gop = CancellationTokenSource.CreateLinkedTokenSource(_hanCts.Token, ct);
            try
            {
                return await task.WaitAsync(gop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Hết hạn chặng — KHÔNG phải user bấm Dừng (nhánh đó để OperationCanceledException đi tiếp như cũ).
                throw _rotGiuaChang
                    ? new CauNoiRotGiuaChangException(
                        "Cầu nối rớt giữa chặng — lệnh đã gửi bị mất, không chờ thêm (KHÔNG tự gửi lại).")
                    : new TimeoutException("Hết thời gian chờ phản hồi từ extension.");
            }
        }

        public void Dispose() => _hanCts.Dispose();
    }
}

/// <summary>
/// <b>KÊNH LỆNH của một phiên cầu nối</b>: giữ <see cref="WebSocketServer"/> trên cổng loopback, nhận message
/// extension gửi về và HOÀN TẤT đúng "chặng" đang chờ. Tách khỏi <see cref="OrdersBridgeSession"/> (đợt dọn
/// 2026-07-30) vì phần này KHÔNG đụng trình duyệt — chạy được trên cổng bất kỳ với một client WebSocket giả làm
/// extension, nên test được cả fan-out captcha/lỗi lẫn timeout từng chặng.
/// <para>
/// Message đến xử lý ĐỒNG BỘ trong handler — rút mọi giá trị cần thiết ra (dạng chuỗi) rồi mới đẩy vào
/// <see cref="TaskCompletionSource"/>, KHÔNG giữ tham chiếu <see cref="JsonDocument"/> qua ranh giới async.
/// </para>
/// <para>
/// Quy ước dùng: chặng nào gửi lệnh rồi mới chờ thì gọi <c>Arm…()</c> (tạo TCS MỚI) NGAY TRƯỚC khi
/// <see cref="SendAsync"/>, rồi <see cref="AwaitAsync"/> chính TCS đó; chặng dùng lại TCS của
/// <see cref="ResetStages"/> (lát cắt đầu phiên) thì await thẳng property tương ứng.
/// </para>
/// </summary>
internal sealed class OrdersBridgeChannel : IDisposable
{
    /// <summary>Cổng cầu nối CỐ ĐỊNH — extension dùng cổng này khi hash <c>#_od_ws</c> bị rụng lúc Shopee redirect
    /// trang đăng nhập (khớp <c>DEFAULT_PORT</c> trong extension). Một phiên/lần test nên cố định là đủ.</summary>
    public const int BridgePort = 47821;

    /// <summary>
    /// TRẦN CHỜ của từng chặng cầu nối. Gom về đây (lớp sở hữu các chặng) vì trước đây là số trần rải khắp
    /// <see cref="OrdersBridgeSession"/> + <see cref="ShopFlowRunner"/> — sửa hạn một chặng phải đi tìm, và
    /// hai chỗ cùng chặng dễ lệch nhau. Mỗi tên khớp ĐÚNG một chặng (<c>Arm…</c>/property cùng tên).
    /// <para>Giá trị GIỮ NGUYÊN như trước khi đặt tên — đợt này chỉ đặt tên, KHÔNG nới/siết chặng nào.</para>
    /// </summary>
    internal static class ChoChang
    {
        /// <summary>Extension nối cầu sau khi trình duyệt mở (chặng <see cref="OrdersBridgeChannel.Ready"/>).</summary>
        internal static readonly TimeSpan Ready = TimeSpan.FromSeconds(45);

        /// <summary>SSO "Kênh Người bán" → về trang chọn shop: đi qua nhiều lần redirect của Shopee nên rộng nhất
        /// trong nhóm điều hướng.</summary>
        internal static readonly TimeSpan AtSeller = TimeSpan.FromSeconds(120);

        /// <summary>Đọc bảng shop ở <c>/portal/shop</c> (chỉ quét DOM trang đang mở).</summary>
        internal static readonly TimeSpan ShopList = TimeSpan.FromSeconds(30);

        /// <summary>Mở "Chi tiết" một shop (trusted click + chờ tab shop lên).</summary>
        internal static readonly TimeSpan Detail = TimeSpan.FromSeconds(45);

        /// <summary>Đọc số "Chờ Lấy Hàng" trên tab shop (chỉ quét DOM).</summary>
        internal static readonly TimeSpan ToShip = TimeSpan.FromSeconds(30);

        /// <summary>Đọc đơn tab "Tất cả": extension phân trang tới <c>MAX_ORDER_PAGES</c> trang.</summary>
        internal static readonly TimeSpan Orders = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Đặt địa chỉ lấy hàng (mở modal "Sửa Địa chỉ" + chọn tỉnh + Lưu).
        /// <para>Nới 90s → 240s ngày 08/08/2026: extension nay dọn modal thông báo CHẮN trang rồi THỬ LẠI đúng
        /// một lượt (xem <c>flow-address.js/doSetPickupAddress</c>), tức ngân sách phải ôm HAI lượt.</para>
        /// <para>Ngân sách XẤU NHẤT của MỘT lượt ≈ <b>72s</b>: điều hướng ≤20s + sleep 1s + dọn modal ~5s +
        /// tìm tab "Địa Chỉ" ≤10s + sleep 1,2s + tìm địa chỉ khớp tỉnh ≤15s + chờ modal ≤10s + sleep 0,8s +
        /// vòng tick checkbox 8×~1,1s ≈ 9s + Lưu 1,2s + hộp xác nhận 1s. Hai lượt + lượt dọn xen giữa ≈
        /// <b>150s</b>.</para>
        /// <para>Vì sao 240s chứ không phải 180s (biên 30s là quá mỏng): quá hạn ở chặng này KHÔNG chỉ hỏng một
        /// shop — <c>AwaitAsync</c> ném <see cref="TimeoutException"/> → <c>OrdersBridgeSession</c> catch ở
        /// ngoài vòng shop ⇒ <b>cả vòng dừng, KHÔNG ghi banner, KHÔNG gửi cảnh báo</b>, tệ hơn hẳn hành vi cũ.
        /// Cái giá của biên rộng chỉ là: shop hỏng THẬT giữ chỗ lâu hơn trong đúng vòng đó.</para>
        /// </summary>
        internal static readonly TimeSpan Pickup = TimeSpan.FromSeconds(240);

        /// <summary>Trả địa chỉ lấy hàng về "địa chỉ khác" sau khi xử xong (cùng modal, ít bước hơn
        /// <see cref="Pickup"/>).</summary>
        internal static readonly TimeSpan PickupOther = TimeSpan.FromSeconds(60);

        /// <summary>Chuẩn bị hàng MỘT đơn: extension chờ Shopee tạo vận đơn (≤90s) TRƯỚC khi in, rồi chờ tab
        /// phiếu (≤120s) — nới hạn cho đủ cả hai.</summary>
        internal static readonly TimeSpan Prepare = TimeSpan.FromSeconds(300);

        /// <summary>Đóng tab shop, về lại trang chọn shop.</summary>
        internal static readonly TimeSpan CloseShop = TimeSpan.FromSeconds(30);

        /// <summary>Tải LẠI phiếu một đơn: về danh sách "Tất cả" → định vị đơn → in → tải PDF.</summary>
        internal static readonly TimeSpan Redownload = TimeSpan.FromSeconds(180);

        /// <summary>Đọc trang đơn trả hàng (bước phụ cuối vòng).</summary>
        internal static readonly TimeSpan Returns = TimeSpan.FromSeconds(90);

        /// <summary>Nền + mỗi đơn (giây) của <see cref="Finals"/>: extension mở TUẦN TỰ từng tab chi tiết nên hạn
        /// phải co giãn theo số đơn, không cố định được.</summary>
        private const int FinalsNenGiay = 20, FinalsMoiDonGiay = 20;

        /// <summary>Trần CỨNG của <see cref="Finals"/> — dài hơn nữa thì coi như extension kẹt, thà bỏ bước final
        /// (best-effort) còn hơn treo cả vòng shop.</summary>
        private const int FinalsTranGiay = 300;

        /// <summary>Hạn chặng "Số tiền cuối cùng" cho <paramref name="soDon"/> đơn cần mở chi tiết:
        /// <c>20s + 20s/đơn</c>, trần cứng 300s.</summary>
        internal static TimeSpan Finals(int soDon)
            => TimeSpan.FromSeconds(Math.Min(FinalsTranGiay, FinalsNenGiay + (FinalsMoiDonGiay * soDon)));
    }

    private readonly Action<string>? _log;
    private WebSocketServer? _ws;

    /// <summary>Nhịp bắn gói giữ-sống cho service worker MV3 — xem <see cref="NhipGiuSong"/>.</summary>
    private System.Threading.Timer? _giuSong;

    // Chặng đang chờ (để extension báo "error" CHỈ fault đúng chặng đó). Xem OnMessage case "error".
    private readonly StageWaiter _waiter = new();

    /// <summary>GĐ3: kết quả extension "chuẩn bị hàng" 1 đơn (mã đơn + phiếu base64 + mã vận đơn). null qua TCS = hết đơn.</summary>
    internal sealed record PrepareResult(string OrderCode, string SlipBase64, string? Tracking);

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

    public OrdersBridgeChannel(Action<string>? log = null) => _log = log;

    /// <summary>True khi cổng WebSocket đã mở (<see cref="Start"/> đã chạy) — chưa mở thì không gửi lệnh được.</summary>
    public bool Started => _ws is not null;

    /// <summary>Extension đã báo rơi vào trang verify/captcha trong phiên này. Đặt được từ ngoài để bước PHỤ
    /// (check đơn trả hàng) trả cờ về đúng như trước bước — xem <see cref="ShopFlowRunner"/>.</summary>
    public bool CaptchaSeen { get; set; }

    // ── Các chặng dùng lại TCS của ResetStages (không tự Arm trước khi gửi) ─────────────────────────────
    public TaskCompletionSource<bool> Ready => _readyTcs;
    public TaskCompletionSource<string?> ShopList => _shopListTcs;
    public TaskCompletionSource<string> Detail => _detailTcs;
    public TaskCompletionSource<string?> ToShip => _toShipTcs;

    // ── Arm: tạo chặng MỚI ngay trước khi gửi lệnh (trả về chính TCS đó để caller await) ────────────────
    public TaskCompletionSource<bool> ArmAtSeller() => _atSellerTcs = NewTcs<bool>();
    public TaskCompletionSource<string?> ArmShopList() => _shopListTcs = NewTcs<string?>();
    public TaskCompletionSource<string> ArmDetail() => _detailTcs = NewTcs<string>();
    public TaskCompletionSource<string?> ArmToShip() => _toShipTcs = NewTcs<string?>();
    public TaskCompletionSource<string?> ArmOrders() => _ordersTcs = NewTcs<string?>();
    public TaskCompletionSource<string?> ArmFinals() => _finalsTcs = NewTcs<string?>();
    public TaskCompletionSource<bool> ArmPickup() => _pickupTcs = NewTcs<bool>();
    public TaskCompletionSource<bool> ArmPickupOther() => _pickupOtherTcs = NewTcs<bool>();
    public TaskCompletionSource<PrepareResult?> ArmPrepare() => _prepareTcs = NewTcs<PrepareResult?>();
    public TaskCompletionSource<bool> ArmCloseShop() => _closeShopTcs = NewTcs<bool>();
    public TaskCompletionSource<string?> ArmRedownload() => _redownloadTcs = NewTcs<string?>();
    public TaskCompletionSource<string?> ArmReturns() => _returnsTcs = NewTcs<string?>();

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void L(string m) => _log?.Invoke(m);

    /// <summary>
    /// NHỊP GIỮ SỐNG service worker của extension. Service worker MV3 bị trình duyệt GIẾT sau ~30s không có
    /// hoạt động nào, và Chromium tính hoạt động WebSocket là hoạt động của service worker — nên chỉ cần bắn
    /// một gói đều đặn DƯỚI 30s là nó sống suốt phiên.
    /// <para>
    /// Vì sao cần: giữa hai shop, vòng chạy NGHỈ ~3 phút. Trong lúc nghỉ không có lệnh nào ⇒ service worker
    /// chết ⇒ WebSocket đóng theo ⇒ lệnh đầu tiên của shop kế ném "Cầu nối extension chưa kết nối" và CẢ VÒNG
    /// dừng (đã xảy ra thật 10/08/2026: shop 1/12 xong, nghỉ 3', shop 2/12 chết ngay lệnh mở Chi tiết).
    /// Service worker chết rồi thì KHÔNG tự nối lại được — <c>scheduleReconnect</c> của ws-bridge chết theo nó;
    /// nó chỉ sống lại khi có sự kiện (vd content.js gửi 'wake' lúc trang load), mà lúc nghỉ thì không có.
    /// </para>
    /// <para>20s: dưới mốc 30s một khoảng đủ rộng để lỡ một nhịp (máy nghẽn, GC) vẫn chưa chạm hạn.</para>
    /// </summary>
    internal static readonly TimeSpan NhipGiuSong = TimeSpan.FromSeconds(20);

    /// <summary>Mở cổng WebSocket <paramref name="port"/> (mặc định <see cref="BridgePort"/>) rồi nối handler
    /// và bật nhịp giữ sống (<paramref name="nhipGiuSong"/>, mặc định <see cref="NhipGiuSong"/> — chỉ test mới
    /// truyền giá trị khác). <paramref name="choNoiLai"/> (mặc định <see cref="ChoNoiLai"/>, cũng chỉ test rót
    /// giá trị khác) là hạn chờ extension nối lại — dùng cho cả <see cref="SendAsync"/> lẫn lượt rút hạn chặng.
    /// Phiên trước vừa đóng có thể chưa nhả hẳn cổng → retry vài nhịp; hết lượt vẫn không mở được thì NÉM.</summary>
    public void Start(int port = BridgePort, TimeSpan? nhipGiuSong = null, TimeSpan? choNoiLai = null)
    {
        _choNoiLai = choNoiLai ?? ChoNoiLai;
        WebSocketServer? ws = null;
        for (var attempt = 0; attempt < 5 && ws is null; attempt++)
        {
            try { var s = new WebSocketServer(port); s.Start(); ws = s; }
            catch when (attempt < 4) { System.Threading.Thread.Sleep(400); }
        }
        _ws = ws ?? throw new InvalidOperationException(
            $"Không mở được cổng cầu nối {port} (đang bận? đóng phiên cũ rồi thử lại).");
        _ws.MessageReceived += OnMessage;

        // Ghi ĐÚNG GIÂY socket nối/đứt. Không có hai dòng này thì chỉ biết "lúc gửi lệnh đã đứt" — không phân
        // biệt được service worker chết ngay 30s đầu kỳ nghỉ (nhịp đánh thức của content script không chạy) với
        // socket chập chờn nối lại được vài lần rồi mới thua. Đúng hai giả thuyết cần tách ở ca 10/08/2026 (hai
        // vòng liền chết ở shop 6). Lượng dòng nhỏ: bình thường mỗi vòng đúng một lần nối.
        _ws.Connected += () => L($"Cầu nối: extension NỐI vào lúc {DateTime.Now:HH:mm:ss}.");

        // Dùng DutVoiLyDo (KHÔNG phải Disconnected) để câu log nói luôn AI đóng socket. Không có phần lý do thì
        // chỉ biết "đứt lúc mấy giờ", không phân biệt được client chủ động đóng với server/http.sys ngắt — mà cả
        // ngày 10/08/2026 đổ oan cho Chromium chính vì thiếu con số này.
        _ws.DutVoiLyDo += lyDo =>
        {
            L($"Cầu nối: extension ĐỨT lúc {DateTime.Now:HH:mm:ss} — {lyDo}.");
            RutNganChangDangCho();
        };

        var nhip = nhipGiuSong ?? NhipGiuSong;
        _giuSong = new System.Threading.Timer(_ => GuiGiuSong(), null, nhip, nhip);
    }

    /// <summary>Bắn một gói giữ-sống. BEST-EFFORT tuyệt đối: chưa nối thì BỎ QUA im lặng (mỗi 20s một dòng log
    /// sẽ ngập nhật ký người dùng). Extension BỎ QUA action lạ (<c>switch</c> trong <c>background.js</c> không có
    /// nhánh <c>ping</c> và cũng không có <c>default</c>) nên gói này không sinh phản hồi, không đụng chặng nào.
    /// <para>
    /// ⚠ Gói này KHÔNG phải lưới an toàn: vòng 06:50 ngày 10/08/2026 cho thấy service worker vẫn bị giết trong
    /// lúc nghỉ dù ping đều 20s. Đường cứu THẬT là <c>chrome.alarms</c> bên extension dựng service worker dậy,
    /// cộng với <see cref="ChoNoiLai"/> ở <see cref="SendAsync"/>. Giữ ping lại vì nó rẻ và vẫn có ích khi
    /// trình duyệt CÓ tính hoạt động WebSocket vào hạn nhàn rỗi.
    /// </para>
    /// <para>Gửi THẲNG qua <c>_ws</c>, KHÔNG qua <see cref="SendAsync"/>: đường kia nay CHỜ nối lại tới 90s,
    /// mà nhịp giữ sống thì phải trả ngay để không xếp hàng chồng nhau.</para></summary>
    private void GuiGiuSong()
    {
        try
        {
            var ws = _ws;
            if (ws is null || !ws.IsConnected) { return; }
            _ = ws.SendAsync(new { action = "ping" })
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch { /* vừa rớt giữa chừng — nhịp sau thử lại */ }
    }

    /// <summary>
    /// Hạn CHỜ EXTENSION NỐI LẠI trước khi coi là đứt hẳn. Service worker MV3 bị trình duyệt giết trong lúc vòng
    /// chạy nghỉ giữa hai shop; extension tự dựng dậy bằng <c>chrome.alarms</c> (nhịp ~30–60s) rồi nối lại. Nếu
    /// C# ném NGAY khi thấy socket đóng thì cả vòng chết oan đúng lúc extension sắp quay lại — đã xảy ra thật
    /// 10/08/2026 ở shop 2 rồi shop 3.
    /// <para>90s = ôm trọn một nhịp alarm kể cả khi trình duyệt kẹp chu kỳ lên 1 phút, cộng biên cho máy chậm.</para>
    /// </summary>
    internal static readonly TimeSpan ChoNoiLai = TimeSpan.FromSeconds(90);

    /// <summary>Hạn chờ nối lại ĐANG DÙNG của phiên này (<see cref="Start"/> rót vào; chỉ test đổi khác
    /// <see cref="ChoNoiLai"/>).</summary>
    private TimeSpan _choNoiLai = ChoNoiLai;

    /// <summary>Extension đang nối cầu hay không (cổng chưa mở cũng tính là chưa nối).</summary>
    public bool DaNoi => _ws?.IsConnected == true;

    /// <summary>Nhịp hỏi lại <see cref="DaNoi"/> trong lúc chờ extension nối lại. 500ms: đủ dày để không phí giây
    /// nào của hạn chờ, đủ thưa để vòng chờ không đốt CPU (bằng nhịp <see cref="SendAsync"/> vẫn dùng).</summary>
    internal static readonly TimeSpan NhipHoiDaNoi = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// CHỜ extension nối (lại) cầu: trả <c>true</c> ngay khi <see cref="DaNoi"/>, trả <c>false</c> nếu hết
    /// <paramref name="han"/> (mặc định hạn chờ nối lại của phiên — xem <see cref="ChoNoiLai"/>) mà vẫn chưa nối.
    /// Người dùng bấm Dừng vẫn ném <see cref="OperationCanceledException"/> như mọi chỗ khác.
    /// <para>
    /// <b>Vì sao cần đường chờ RIÊNG thay vì để <see cref="SendAsync"/> tự chờ:</b> đường hồi phục trang chọn shop
    /// (<c>ShopFlowRunner.DongTabShopAsync</c>) phải BIẾT cầu nối có sống lại hay không mới nói đúng nguyên nhân —
    /// "cầu nối đang gãy" và "picker hỏng" là hai bệnh, chữa hai kiểu, mà <see cref="SendAsync"/> nuốt câu trả lời
    /// đó vào trong. Ngoài ra gửi lệnh lúc socket đang đứt là đốt một lượt thử vô ích.
    /// </para>
    /// </summary>
    public async Task<bool> ChoNoiLaiAsync(TimeSpan? han = null, CancellationToken ct = default)
    {
        if (DaNoi) { return true; }

        var hetHan = DateTime.UtcNow + (han ?? _choNoiLai);
        while (!DaNoi)
        {
            var conLai = hetHan - DateTime.UtcNow;
            if (conLai <= TimeSpan.Zero) { break; }
            await Task.Delay(conLai < NhipHoiDaNoi ? conLai : NhipHoiDaNoi, ct).ConfigureAwait(false);
        }
        return DaNoi;
    }

    /// <summary>
    /// Socket vừa ĐỨT: nếu đang có chặng chờ dở thì hạ hạn còn lại của nó xuống tối đa <see cref="_choNoiLai"/>.
    /// Lệnh của chặng đó đã bay đi rồi nên coi như MẤT — chờ nốt hạn gốc (Prepare tới 300s) là chờ mù, mà quá
    /// hạn ở đó thì ném ra tận ngoài vòng shop và giết cả vòng (13:32→13:37 ngày 10/08/2026, mất 6 shop cuối).
    /// <para>Đứt của socket CŨ vừa bị socket MỚI thay chỗ thì <b>không</b> rút: <c>WebSocketServer</c> raise
    /// <c>Connected</c> của cái mới TRƯỚC <c>Disconnected</c> của cái cũ, mà lúc đó cầu nối vẫn đang nối nên
    /// chặng đang chờ chưa chắc mất lệnh — rút hạn ở đây là giết oan một chặng còn sống.</para>
    /// </summary>
    private void RutNganChangDangCho()
    {
        if (DaNoi) { return; }
        if (_waiter.RutNganHanKhiDut(_choNoiLai))
        {
            L($"Cầu nối rớt GIỮA một chặng đang chờ — lệnh đó coi như mất, chỉ chờ thêm tối đa {_choNoiLai.TotalSeconds:0}s.");
        }
    }

    /// <summary>Gửi lệnh cho extension. Cổng chưa mở → ném NGAY (lỗi lập trình, chờ vô ích). Extension chưa/không
    /// còn kết nối → CHỜ nó nối lại tới <paramref name="choNoiLai"/> (mặc định hạn của phiên, xem
    /// <see cref="ChoNoiLai"/>) rồi mới
    /// gửi; hết hạn vẫn chưa có thì để <see cref="WebSocketServer.SendAsync"/> ném như cũ (caller phân biệt được
    /// "extension chưa kết nối" với "extension kẹt").</summary>
    public async Task SendAsync(object message, TimeSpan? choNoiLai = null)
    {
        var ws = _ws ?? throw new InvalidOperationException(
            "Cầu nối chưa khởi động (chưa mở cổng WebSocket) — không gửi được lệnh.");

        if (!ws.IsConnected)
        {
            var han = choNoiLai ?? _choNoiLai;
            // KHÔNG đoán nguyên nhân trong câu này nữa. Bản cũ ghi "(service worker ngủ trong lúc nghỉ?)" và câu
            // đó đổ oan suốt buổi sáng 10/08/2026 — hoá ra có lượt CẢ TRÌNH DUYỆT đã thoát, chẳng liên quan MV3.
            // Nguyên nhân thật đọc ở hai dòng mốc "Cầu nối: extension ĐỨT/NỐI" và dòng "Trình duyệt sạch đã THOÁT".
            L($"Cầu nối rớt — chờ extension nối lại tối đa {han.TotalSeconds:0}s...");
            var dl = DateTime.UtcNow + han;
            while (DateTime.UtcNow < dl && !ws.IsConnected)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
            L(ws.IsConnected ? "Extension đã nối lại — chạy tiếp." : "Extension KHÔNG nối lại trong hạn chờ.");
        }

        await ws.SendAsync(message).ConfigureAwait(false);
    }

    /// <summary>Chờ một chặng (kèm timeout + ct) qua <see cref="StageWaiter"/> — extension báo lỗi lúc đang chờ
    /// thì CHỈ chặng này bị fault.</summary>
    public Task<T> AwaitAsync<T>(TaskCompletionSource<T> tcs, TimeSpan timeout, CancellationToken ct)
        => _waiter.AwaitAsync(tcs, timeout, ct);

    /// <summary>Thay MỚI toàn bộ chặng + xóa cờ captcha — gọi ở đầu mỗi lần chạy (chặng cũ pending để GC lặng).</summary>
    public void ResetStages()
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
        CaptchaSeen = false;
    }

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
                    var b64 = root.TryGetProperty("slipBase64", out var sb) ? (sb.GetString() ?? string.Empty) : string.Empty;
                    var trk = root.TryGetProperty("tracking", out var tk) ? tk.GetString() : null;
                    _prepareTcs.TrySetResult(new PrepareResult(code, b64, trk));
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
                    CaptchaSeen = true;
                    // Hoàn tất mọi chặng ĐANG chờ (bất kể pha nào) để C# thoát nhanh + kiểm CaptchaSeen.
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
                    // không await được để NGUYÊN (pending → ResetStages thay mới ở vòng sau; task pending KHÔNG raise unobserved).
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
        // Dừng nhịp giữ-sống TRƯỚC khi bỏ server: timer còn chạy sẽ gọi SendAsync trên _ws vừa dispose.
        try { _giuSong?.Dispose(); } catch { }
        _giuSong = null;
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }
}
