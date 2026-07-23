using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Kết quả một "lát cắt kiểm chứng" GĐ1 của cầu nối extension↔C#: đọc danh sách shop → mở "Chi tiết"
/// shop đầu (trusted click, kỳ vọng KHÔNG captcha) → đọc số "Chờ Lấy Hàng". Chỉ để chứng minh kiến trúc,
/// CHƯA port business logic.
/// </summary>
/// <param name="Shops">Danh sách shop parse từ bảng <c>/portal/shop</c> (rỗng nếu chưa/không đọc được).</param>
/// <param name="FirstShopId">Mã shop đầu đã thử mở "Chi tiết" (null nếu không có shop nào).</param>
/// <param name="ToShipCount">Số "Chờ Lấy Hàng" đọc được ở shop đầu (null nếu không đọc được).</param>
/// <param name="Captcha">True nếu extension báo rơi vào trang verify/captcha khi mở "Chi tiết".</param>
/// <param name="Error">Thông báo lỗi (null nếu chạy trọn lát cắt không lỗi).</param>
public sealed record OrdersBridgeSliceResult(
    IReadOnlyList<ShopListItem> Shops,
    string? FirstShopId,
    int? ToShipCount,
    bool Captcha,
    string? Error);

/// <summary>
/// Vòng đời MỘT phiên cầu nối GĐ1: cấp cổng loopback trống → chạy <see cref="OrdersWebSocketServer"/> →
/// mở trình duyệt SẠCH (không CDP, không remote-debugging-port) qua <see cref="PocCleanLauncher"/> với
/// <c>startUrl</c> có hash <c>#_od_ws=&lt;port&gt;</c> để extension đọc cổng → chờ extension báo
/// <c>ready</c> → gửi tuần tự 3 lệnh <c>readShopList</c> / <c>openShopDetail</c> / <c>readToShip</c> và
/// nhận dữ liệu, parse qua các hàm THUẦN sẵn có (<see cref="ShopeeLoginService.ParseShopListJson"/>,
/// <see cref="ShopeeDashboard.ParseToShipCount"/>).
/// <para>
/// Message đến (WebSocket) được xử lý ĐỒNG BỘ trong handler — mọi giá trị cần thiết được rút ra ngay
/// (dạng chuỗi) rồi mới đẩy vào các <see cref="TaskCompletionSource"/>, KHÔNG giữ tham chiếu tới
/// <see cref="JsonDocument"/> qua ranh giới async (bài học từ Search <c>OnMessage</c> — doc có thể bị dispose).
/// </para>
/// <para>GĐ1 chạy MỘT phiên/lần test (chưa đa-lane). Cấp cổng đơn giản bằng <see cref="TcpListener"/> port 0.</para>
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
    private TaskCompletionSource<string?> _shopListTcs = NewTcs<string?>();
    private TaskCompletionSource<string> _detailTcs = NewTcs<string>();
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

    /// <summary>
    /// Chạy trọn lát cắt kiểm chứng. Ném KHÔNG mong đợi được bọc lại thành <see cref="OrdersBridgeSliceResult.Error"/>;
    /// riêng <see cref="OperationCanceledException"/> (user hủy) được ném ra ngoài.
    /// </summary>
    public async Task<OrdersBridgeSliceResult> RunSliceAsync(CancellationToken ct = default)
    {
        // Reset trạng thái (cho phép tái dùng đối tượng nếu cần).
        _readyTcs = NewTcs<bool>();
        _shopListTcs = NewTcs<string?>();
        _detailTcs = NewTcs<string>();
        _toShipTcs = NewTcs<string?>();
        _captchaSeen = false;

        IReadOnlyList<ShopListItem> shops = Array.Empty<ShopListItem>();
        string? firstShopId = null;
        int? toShip = null;

        var wsPort = AllocateFreePort();
        _ws = new OrdersWebSocketServer(wsPort);
        _ws.MessageReceived += OnMessage;
        _ws.Start();
        L($"Cầu nối: WebSocket lắng nghe ws://localhost:{wsPort} — mở trình duyệt sạch...");

        // Extension đọc cổng từ hash của URL đầu tiên. Chromium giữ nguyên hash khi load.
        var startUrl = $"{ShopeeLoginService.ShopListUrl}#_od_ws={wsPort}";
        var extPath = BraveLaunchArgs.ResolveOrdersBridgeExtension()
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension 'shopee-orders' (cạnh app hoặc trong repo). " +
                "Cầu nối GĐ1 cần extension này để nối WebSocket + bắn trusted click.");

        Process = PocCleanLauncher.Open(_userDataDir, _browserChoice, startUrl, extPath);

        try
        {
            // 1) Chờ extension nối WS + báo ready (user phải đã đăng nhập tay tới /portal/shop).
            L("Chờ extension nối cầu (ready) — tối đa 45s...");
            await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            L("Extension đã nối cầu.");

            // 2) Đọc danh sách shop.
            await _ws.SendAsync(new { action = "readShopList" }).ConfigureAwait(false);
            var shopListJson = await _shopListTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            shops = ShopeeLoginService.ParseShopListJson(shopListJson);
            L($"Đọc được {shops.Count} shop từ /portal/shop.");
            if (shops.Count == 0)
            {
                return new OrdersBridgeSliceResult(shops, null, null, false,
                    "Không đọc được shop nào (đã đăng nhập tới /portal/shop chưa?).");
            }

            // 3) Mở "Chi tiết" shop đầu bằng trusted click (kỳ vọng KHÔNG captcha).
            firstShopId = shops[0].ShopId;
            L($"Mở 'Chi tiết' shop đầu (id={firstShopId}) bằng trusted click...");
            await _ws.SendAsync(new { action = "openShopDetail", shopId = firstShopId }).ConfigureAwait(false);
            var detail = await _detailTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            if (detail == "captcha" || _captchaSeen)
            {
                L("PHÁT HIỆN captcha/verify khi mở Chi tiết — kiến trúc CHƯA né được (cần soi lại).");
                return new OrdersBridgeSliceResult(shops, firstShopId, null, true,
                    "Rơi vào trang verify/captcha khi mở Chi tiết.");
            }
            L("Đã mở tab shop (không captcha).");

            // 4) Đọc số "Chờ Lấy Hàng".
            await _ws.SendAsync(new { action = "readToShip" }).ConfigureAwait(false);
            var raw = await _toShipTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            toShip = ShopeeDashboard.ParseToShipCount(raw);
            L($"Số 'Chờ Lấy Hàng' đọc được: {(toShip?.ToString() ?? "null")} (raw='{raw}').");

            return new OrdersBridgeSliceResult(shops, firstShopId, toShip, false, null);
        }
        catch (OperationCanceledException)
        {
            throw; // user hủy → để tầng trên xử lý
        }
        catch (TimeoutException)
        {
            L("Cầu nối: hết thời gian chờ phản hồi từ extension.");
            return new OrdersBridgeSliceResult(shops, firstShopId, toShip, _captchaSeen,
                "Hết thời gian chờ phản hồi từ extension (extension chưa nối / trang chưa sẵn sàng).");
        }
        catch (Exception ex)
        {
            L("Cầu nối lỗi: " + ex.Message);
            return new OrdersBridgeSliceResult(shops, firstShopId, toShip, _captchaSeen, ex.Message);
        }
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
                    // GĐ1: 'progress' chỉ được extension gửi khi mở Chi tiết xong → hoàn tất chặng detail.
                    _detailTcs.TrySetResult("ok");
                    break;
                }

                case "captcha":
                {
                    _captchaSeen = true;
                    _detailTcs.TrySetResult("captcha");
                    break;
                }

                case "error":
                {
                    var m = root.TryGetProperty("message", out var mm) ? mm.GetString() : "lỗi extension";
                    L("extension LỖI: " + m);
                    var ex = new InvalidOperationException("Extension báo lỗi: " + m);
                    // Fault mọi chặng đang chờ để RunSliceAsync thoát sớm thay vì đợi timeout.
                    _readyTcs.TrySetException(ex);
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

    /// <summary>Đọc trường <c>data</c>: nếu là chuỗi → lấy nguyên; nếu là object/array → lấy JSON thô
    /// (để hàm parse thuần xử lý tiếp). Không có → null.</summary>
    private static string? ReadDataAsString(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var d))
        {
            return null;
        }
        return d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText();
    }

    /// <summary>Chọn một cổng loopback trống bằng cách bind port 0 rồi nhả (GĐ1 một phiên/lần test là đủ).</summary>
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
