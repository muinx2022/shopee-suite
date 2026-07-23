using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Máy chủ WebSocket cục bộ (loopback) làm CẦU NỐI giữa C# và extension trình duyệt cho module Đơn hàng.
/// Chép khuôn <c>WebSocketServer</c> của module Search (suite/Shopee.Module.Search) — cùng cơ chế: một
/// <see cref="HttpListener"/> nghe <c>http://localhost:{port}/</c>, nâng cấp lên WebSocket, giữ 1 socket
/// mới nhất, gom frame tới hết message rồi raise <see cref="MessageReceived"/> với <see cref="JsonDocument"/>.
/// <para>
/// KHÁC Search ở chỗ dùng: ở Đơn hàng ta KHÔNG mở <c>--remote-debugging-port</c> (không có kênh CDP cho C#),
/// input "thật" do extension tự bắn qua <c>chrome.debugger</c>; WebSocket này CHỈ trao đổi lệnh/dữ liệu.
/// Hàm thuần về IO mạng — không phụ thuộc Playwright/Avalonia nên test/độc lập được.
/// </para>
/// </summary>
public sealed class OrdersWebSocketServer : IDisposable
{
    private readonly int _port;
    private HttpListener? _listener;
    private WebSocket? _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<JsonDocument>? MessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public OrdersWebSocketServer(int port) => _port = port;

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
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
                _ = HandleConnectionAsync(wsCtx.WebSocket, ct);
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
        Connected?.Invoke();

        var buffer = new byte[256 * 1024];
        // Buffer theo từng kết nối: dùng chung một field sẽ hỏng frame khi kết nối cũ còn đang xả trong lúc cái mới tới.
        var msgBuffer = new List<byte>();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close)
            {
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
                    MessageReceived?.Invoke(doc);
                }
                catch { }
            }
        }

        Interlocked.CompareExchange(ref _socket, null, ws);
        Disconnected?.Invoke();
    }

    public async Task SendAsync(object message)
    {
        var ws = _socket;
        if (ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
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
