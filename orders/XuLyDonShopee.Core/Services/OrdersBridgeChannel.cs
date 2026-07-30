using System.Text.Json;
using Shopee.Toolkit.Ws;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Nhớ chặng TCS ĐANG được await để khi extension báo <c>error</c> ta CHỈ fault đúng chặng đó — không fault
/// hàng loạt TCS không ai await (11 chặng → 10 exception mồ côi → <c>UnobservedTaskException</c>). Các chặng
/// không được await được để NGUYÊN (pending → GC lặng, KHÔNG raise unobserved) rồi
/// <see cref="OrdersBridgeChannel.ResetStages"/> thay mới.
/// <para>Lớp riêng (internal) để test được cơ chế mà không cần mở trình duyệt.</para>
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

    private readonly Action<string>? _log;
    private WebSocketServer? _ws;

    // Chặng đang chờ (để extension báo "error" CHỈ fault đúng chặng đó). Xem OnMessage case "error".
    private readonly StageWaiter _waiter = new();

    /// <summary>GĐ3: kết quả extension "chuẩn bị hàng" 1 đơn (mã đơn + URL tab phiếu). null qua TCS = hết đơn.</summary>
    internal sealed record PrepareResult(string OrderCode, string SlipTabUrl, string SlipBase64, string? Tracking);

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

    /// <summary>Mở cổng WebSocket <paramref name="port"/> (mặc định <see cref="BridgePort"/>) rồi nối handler.
    /// Phiên trước vừa đóng có thể chưa nhả hẳn cổng → retry vài nhịp; hết lượt vẫn không mở được thì NÉM.</summary>
    public void Start(int port = BridgePort)
    {
        WebSocketServer? ws = null;
        for (var attempt = 0; attempt < 5 && ws is null; attempt++)
        {
            try { var s = new WebSocketServer(port); s.Start(); ws = s; }
            catch when (attempt < 4) { System.Threading.Thread.Sleep(400); }
        }
        _ws = ws ?? throw new InvalidOperationException(
            $"Không mở được cổng cầu nối {port} (đang bận? đóng phiên cũ rồi thử lại).");
        _ws.MessageReceived += OnMessage;
    }

    /// <summary>Gửi lệnh cho extension. FAIL-FAST hai tầng: cổng chưa mở → ném ngay ở đây; extension chưa/không
    /// còn kết nối → <see cref="WebSocketServer.SendAsync"/> ném (caller phân biệt được với "extension kẹt").</summary>
    public Task SendAsync(object message)
    {
        var ws = _ws ?? throw new InvalidOperationException(
            "Cầu nối chưa khởi động (chưa mở cổng WebSocket) — không gửi được lệnh.");
        return ws.SendAsync(message);
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
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }
}
