using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shopee.Toolkit.Ws;

/// <summary>
/// Chạy MỘT vòng lặp async trên một thread nền RIÊNG và giữ mọi <c>await</c>-continuation của vòng đó Ở LẠI
/// đúng thread ấy (bơm message kiểu vòng lặp UI, nhưng không có UI).
/// <para>
/// <b>VÌ SAO phải có, chứ không để mặc thread pool:</b> trên Windows, overlapped I/O bị huỷ khi thread PHÁT RA
/// nó kết thúc (<c>ERROR_OPERATION_ABORTED</c> = 995). Vòng nhận WebSocket chạy trên thread pool thì mỗi lượt
/// <c>ReceiveAsync</c> lại được phát từ một thread pool thread khác nhau; thread pool thu hồi thread nhàn rỗi
/// ⇒ lệnh nhận ĐANG TREO bị huỷ oan dù cả hai đầu đều khoẻ. Đo được ở vòng chạy thật 10/08/2026: mỗi kết nối
/// sống đúng <b>211 giây</b> rồi chết với
/// <c>WebSocketException [wsError=Success, win32=0] &lt;- HttpListenerException: The I/O operation has been
/// aborted because of either a thread exit or an application request</c>, lặp lại y hệt 4 lần liền.
/// </para>
/// <para>
/// Thread ở đây nằm im trong <c>GetConsumingEnumerable</c> suốt đời kết nối nên KHÔNG bao giờ bị thu hồi ⇒
/// lệnh I/O nó phát ra không có ai huỷ.
/// </para>
/// </summary>
internal sealed class VongTrenLuongRieng : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Ham, object? TrangThai)> _hangDoi = new();

    private VongTrenLuongRieng() { }

    /// <summary>Continuation của một <c>await</c> trong vòng — xếp vào hàng đợi cho ĐÚNG thread của vòng chạy.
    /// Vòng đã kết thúc (hàng đợi đóng/dispose) thì bỏ im: continuation đến muộn không còn chỗ nào để chạy.</summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        // CompleteAdding → InvalidOperationException; Dispose → ObjectDisposedException (là con của cái trên).
        try { _hangDoi.Add((d, state)); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Chặn hẳn: <c>Send</c> đồng bộ từ chính thread của vòng sẽ tự khoá mình, mà không đường nào ở đây cần nó.</summary>
    public override void Send(SendOrPostCallback d, object? state)
        => throw new NotSupportedException("Vòng chạy trên luồng riêng không nhận lời gọi Send đồng bộ.");

    /// <summary>Khởi động <paramref name="than"/> trên một thread nền mới mang tên <paramref name="ten"/>.
    /// Thread sống tới khi <paramref name="than"/> hoàn tất (kể cả ném) rồi tự thoát.</summary>
    public static void Chay(string ten, Func<Task> than)
        => new Thread(() => Bom(than)) { IsBackground = true, Name = ten }.Start();

    private static void Bom(Func<Task> than)
    {
        var bom = new VongTrenLuongRieng();
        SetSynchronizationContext(bom);
        try
        {
            var viec = than();
            // Vòng xong (kể cả ném — đọc Exception để không thành UnobservedTaskException) → đóng hàng đợi cho
            // thread này thoát. ExecuteSynchronously + TaskScheduler.Default: lượt đóng chạy NGAY trên thread
            // vừa hoàn tất `viec` (chính là thread này), không phải xin thêm một lượt thread pool nữa.
            _ = viec.ContinueWith(
                t => { _ = t.Exception; bom.Dong(); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            foreach (var (ham, trangThai) in bom._hangDoi.GetConsumingEnumerable())
            {
                ham(trangThai);
            }
        }
        finally
        {
            SetSynchronizationContext(null);
            bom._hangDoi.Dispose();
        }
    }

    private void Dong()
    {
        try { _hangDoi.CompleteAdding(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Chạy <paramref name="viec"/> với <see cref="SynchronizationContext.Current"/> TẠM XOÁ.
    /// <para>Handler sự kiện (<c>MessageReceived</c>/<c>Connected</c>/…) trước đây luôn chạy trên thread pool,
    /// tức <c>SynchronizationContext.Current == null</c>. Nếu nay để chúng thấy bơm này thì mọi tác vụ async
    /// một handler khởi động sẽ ĐUA CHỖ với vòng nhận trên cùng một thread — nặng thì kẹt vòng nhận, xấu nhất
    /// là khoá chéo khi handler chờ một thứ chỉ hoàn tất được sau khi vòng nhận chạy tiếp. Xoá đi là giữ
    /// nguyên môi trường cũ của handler.</para>
    /// </summary>
    internal static void ChayNgoaiBom(Action viec)
    {
        var giu = Current;
        SetSynchronizationContext(null);
        try { viec(); }
        finally { SetSynchronizationContext(giu); }
    }
}

/// <summary>
/// VÌ SAO vòng nhận của một kết nối WebSocket kết thúc. Trước đây mọi đường đứt đều bị <c>catch { break; }</c>
/// nuốt sạch nên nhật ký chỉ nói "đứt lúc mấy giờ" — không phân biệt được <b>client chủ động đóng</b>
/// (extension/Chromium gửi frame Close) với <b>server/http.sys ngắt</b> (ngoại lệ, abort). Hai bệnh đó chữa hai
/// kiểu, nên phải đo chứ không đoán.
/// <para>Thuần dữ liệu — không đụng mạng, test thẳng được.</para>
/// </summary>
/// <param name="Nguon">Nhánh nào của vòng nhận kết thúc — xem các hằng <c>Nguon*</c>.</param>
/// <param name="CloseStatus">Mã đóng WebSocket (client gửi trong frame Close, hoặc mã socket tự ghi lại).</param>
/// <param name="MoTaDong"><c>CloseStatusDescription</c> — chuỗi tự do phía đóng kèm theo.</param>
/// <param name="Loi">Ngoại lệ (đã rút gọn thành chuỗi) nếu vòng nhận chết vì ném; null nếu đóng êm.</param>
/// <param name="DaBiThay"><c>true</c> khi socket này đã bị một kết nối MỚI thay chỗ trước lúc vòng nhận thoát —
/// đứt kiểu đó là do chính ta đóng, KHÔNG phải sự cố.</param>
public sealed record LyDoDut(
    string Nguon,
    WebSocketCloseStatus? CloseStatus,
    string? MoTaDong,
    string? Loi,
    bool DaBiThay)
{
    /// <summary>Client gửi frame Close — phía kia CHỦ ĐỘNG đóng.</summary>
    public const string NguonClientDong = "client-dong";

    /// <summary><c>ReceiveAsync</c> ném — đường của abort/http.sys/lỗi mạng.</summary>
    public const string NguonNgoaiLe = "ngoai-le";

    /// <summary>Vòng thoát vì <c>State != Open</c> (socket đã rời trạng thái mở mà không ném, không có frame Close).</summary>
    public const string NguonTrangThai = "trang-thai";

    /// <summary>Vòng thoát vì server đang tắt (<c>Dispose</c> huỷ token).</summary>
    public const string NguonServerTat = "server-tat";

    /// <summary>Một dòng gọn để nhét thẳng vào nhật ký người dùng.</summary>
    public override string ToString()
    {
        var phan = new List<string> { Nguon };
        if (CloseStatus is not null) { phan.Add($"close={CloseStatus}"); }
        if (!string.IsNullOrWhiteSpace(MoTaDong)) { phan.Add($"mota=\"{MoTaDong}\""); }
        if (!string.IsNullOrWhiteSpace(Loi)) { phan.Add($"loi={Loi}"); }
        if (DaBiThay) { phan.Add("da-bi-thay"); }
        return string.Join(" · ", phan);
    }

    /// <summary>Rút ngoại lệ thành MỘT dòng giữ đủ thứ cần để lần ra thủ phạm: kiểu + message + (với
    /// <see cref="WebSocketException"/>) mã lỗi WebSocket và mã lỗi Win32 — mã Win32 là thứ chỉ đích danh
    /// http.sys/Winsock khi socket bị ngắt từ dưới lên. Lồng theo <c>InnerException</c> tối đa
    /// <see cref="SauLongToiDa"/> tầng để không đẻ ra chuỗi dài vô hạn.</summary>
    internal static string RutGonNgoaiLe(Exception ex)
    {
        var sb = new StringBuilder();
        var hienTai = (Exception?)ex;
        for (var tang = 0; hienTai is not null && tang < SauLongToiDa; tang++)
        {
            if (tang > 0) { sb.Append(" <- "); }
            sb.Append(hienTai.GetType().Name).Append(": ").Append(hienTai.Message);
            if (hienTai is WebSocketException wse)
            {
                sb.Append(" [wsError=").Append(wse.WebSocketErrorCode)
                  .Append(", win32=").Append(wse.ErrorCode).Append(']');
            }
            hienTai = hienTai.InnerException;
        }
        return sb.ToString();
    }

    /// <summary>Số tầng <c>InnerException</c> tối đa gói vào chuỗi lý do — 3 tầng đã ôm trọn khuôn thường gặp
    /// (<c>WebSocketException</c> ← <c>HttpListenerException</c>/<c>SocketException</c>).</summary>
    private const int SauLongToiDa = 3;
}

/// <summary>
/// Máy chủ WebSocket cục bộ (loopback) làm CẦU NỐI giữa C# và extension trình duyệt. Một
/// <see cref="HttpListener"/> nghe <c>http://localhost:{port}/</c>, nâng cấp lên WebSocket, giữ 1 socket
/// mới nhất, gom frame tới hết message rồi raise <see cref="MessageReceived"/> với <see cref="JsonDocument"/>.
/// <para>
/// DÙNG CHUNG cho module Search (suite) và module Đơn hàng (orders) — trước đây là hai bản chép tay đã bắt
/// đầu lệch nhau (bản orders có fail-fast + tùy chọn serialize dùng chung, bản Search thì không). Bản hợp
/// nhất này lấy bản orders làm gốc; thứ duy nhất của bản Search được giữ là cổng mặc định 9111.
/// </para>
/// <para>
/// Hàm thuần về IO mạng — không phụ thuộc Playwright/Avalonia nên test/độc lập được. Lưu ý khác nhau giữa
/// hai chỗ dùng: ở Đơn hàng KHÔNG mở <c>--remote-debugging-port</c> (không có kênh CDP cho C#), input "thật"
/// do extension tự bắn qua <c>chrome.debugger</c>; WebSocket này CHỈ trao đổi lệnh/dữ liệu.
/// </para>
/// </summary>
public sealed class WebSocketServer : IDisposable
{
    private readonly int _port;
    private HttpListener? _listener;
    private WebSocket? _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<JsonDocument>? MessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;

    /// <summary>
    /// Như <see cref="Disconnected"/> nhưng KÈM LÝ DO (xem <see cref="LyDoDut"/>), raise NGAY TRƯỚC nó. Thêm
    /// event riêng thay vì đổi chữ ký <see cref="Disconnected"/> vì lớp này dùng chung với module Search —
    /// ai không cần lý do thì không phải sửa gì.
    /// </summary>
    public event Action<LyDoDut>? DutVoiLyDo;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    // Tùy chọn serialize DÙNG CHUNG (trước đây khởi tạo MỚI mỗi lần SendAsync — phí + rác GC theo tần suất lệnh).
    private static readonly JsonSerializerOptions SendOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WebSocketServer(int port = 9111) => _port = port;

    /// <summary>Số thứ tự kết nối — chỉ để đặt tên thread cho dễ soi trong debugger/dump.</summary>
    private int _soKetNoi;

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        // Vòng CHẤP NHẬN kết nối cũng phát overlapped I/O (GetContextAsync tới http.sys) nên cùng bệnh với vòng
        // nhận — cho nó thread nền riêng sống suốt đời server. Xem VongTrenLuongRieng.
        VongTrenLuongRieng.Chay($"ws-{_port}-nhan-ket-noi", () => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync().WaitAsync(ct);
                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                // MỖI kết nối một thread nền riêng — KHÔNG chạy trên thread của vòng chấp nhận (vòng đó phải rảnh
                // để đón kết nối kế) và cũng không thả về thread pool (đúng cái bệnh cần chữa).
                var ws = wsCtx.WebSocket;
                VongTrenLuongRieng.Chay(
                    $"ws-{_port}-nhan-{Interlocked.Increment(ref _soKetNoi)}",
                    () => HandleConnectionAsync(ws, ct));
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        // Chỉ kết nối mới nhất là "sống": đóng kết nối cũ để vòng nhận của nó thoát thay vì lơ lửng và đua với cái mới.
        var old = Interlocked.Exchange(ref _socket, ws);
        if (old is not null && old.State == WebSocketState.Open)
        {
            try { await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
            catch { }
        }
        VongTrenLuongRieng.ChayNgoaiBom(() => Connected?.Invoke());

        var buffer = new byte[256 * 1024];
        // Buffer theo từng kết nối: dùng chung một field sẽ hỏng frame khi kết nối cũ còn đang xả trong lúc cái mới tới.
        var msgBuffer = new List<byte>();

        // Lý do vòng nhận kết thúc — điền ở đúng nhánh thoát, để dành cho DutVoiLyDo ở cuối hàm.
        string? nguon = null;
        string? loi = null;
        WebSocketCloseStatus? closeStatus = null;
        string? moTaDong = null;

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
            catch (Exception ex)
            {
                // KHÔNG nuốt trắng nữa: đây là đường của abort/http.sys/lỗi mạng — chính nhánh cần đo.
                nguon = LyDoDut.NguonNgoaiLe;
                loi = LyDoDut.RutGonNgoaiLe(ex);
                closeStatus = ws.CloseStatus;
                moTaDong = ws.CloseStatusDescription;
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                nguon = LyDoDut.NguonClientDong;
                closeStatus = result.CloseStatus ?? ws.CloseStatus;
                moTaDong = result.CloseStatusDescription ?? ws.CloseStatusDescription;
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
                break;
            }

            msgBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString([.. msgBuffer]);
                msgBuffer.Clear();
                try
                {
                    var doc = JsonDocument.Parse(json);
                    VongTrenLuongRieng.ChayNgoaiBom(() => MessageReceived?.Invoke(doc));
                }
                catch { }
            }
        }

        // Trả về giá trị CŨ: khác ws ⇒ một kết nối MỚI đã thay chỗ socket này từ trước, tức cú đứt này là do
        // chính ta đóng kết nối cũ chứ không phải sự cố. Không cần thêm state nào để biết điều đó.
        var truoc = Interlocked.CompareExchange(ref _socket, null, ws);
        var daBiThay = !ReferenceEquals(truoc, ws);

        // Vòng thoát mà không qua nhánh nào ở trên: hoặc server đang tắt, hoặc socket đã rời trạng thái Open.
        nguon ??= ct.IsCancellationRequested ? LyDoDut.NguonServerTat : LyDoDut.NguonTrangThai;
        closeStatus ??= ws.CloseStatus;
        moTaDong ??= ws.CloseStatusDescription;

        var lyDo = new LyDoDut(nguon, closeStatus, moTaDong, loi, daBiThay);
        VongTrenLuongRieng.ChayNgoaiBom(() =>
        {
            DutVoiLyDo?.Invoke(lyDo);
            Disconnected?.Invoke();
        });

        // Trả socket + ngữ cảnh HttpListener về hệ điều hành NGAY. Trước đây vòng nhận chỉ `break` rồi bỏ mặc:
        // mỗi lượt đứt để lại một WebSocket + một request http.sys treo, và quan trọng hơn là phía client KHÔNG
        // được báo gì cả — nó ngồi ôm một socket "còn OPEN" nhưng đã chết, nên `onclose` (đường nối lại nhanh
        // 1200ms của ws-bridge) không hề bắn. Đóng dứt điểm ở đây là điều kiện cần để extension nối lại nhanh.
        // Đặt SAU sự kiện: handler còn đọc State/CloseStatus của socket này.
        try { ws.Abort(); } catch { }
        try { ws.Dispose(); } catch { }
    }

    /// <summary>
    /// Gửi <paramref name="message"/> (serialize JSON camelCase) cho extension đang nối. FAIL-FAST:
    /// socket CHƯA nối / đã rớt (<c>State != Open</c>) → ném <see cref="InvalidOperationException"/> NGAY thay vì
    /// return im lặng — nhờ đó caller phân biệt "extension chưa kết nối" (fail tức thì) với "extension kẹt"
    /// (TimeoutException khi chờ phản hồi). Trước đây return âm thầm → caller ngồi chờ TCS 30–300s rồi nhận
    /// TimeoutException sai hướng.
    /// </summary>
    public async Task SendAsync(object message)
    {
        var ws = _socket;
        if (ws?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException(
                "Cầu nối extension chưa kết nối (WebSocket chưa mở) — không gửi được lệnh.");
        }

        var json = JsonSerializer.Serialize(message, SendOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Dispose();
        _listener?.Stop();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
