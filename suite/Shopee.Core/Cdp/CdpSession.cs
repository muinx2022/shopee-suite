using System.Collections.Concurrent;

namespace Shopee.Core.Cdp;

/// <summary>
/// Minimal Chrome DevTools Protocol (CDP) client over WebSocket. Connects to a single page
/// target and sends JSON-RPC commands. Works against any Chromium browser (Edge, Brave, …)
/// vì CDP giống hệt nhau giữa các trình duyệt Chromium.
/// </summary>
public sealed class CdpSession : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte> _recvBuf = [];

    public bool IsOpen => _ws?.State == WebSocketState.Open;

    /// <summary>Polls until the browser's CDP HTTP endpoint responds, then connects to the first page.</summary>
    public static async Task<CdpSession> ConnectToPageAsync(int cdpPort, CancellationToken ct = default)
    {
        var wsUrl = await WaitForPageWsUrlAsync(cdpPort, ct);
        var session = new CdpSession();
        await session.ConnectAsync(wsUrl, ct);
        return session;
    }

    /// <summary>
    /// Kết nối tới WebSocket cấp BROWSER (endpoint <c>/json/version</c>) — KHÔNG gắn vào một page
    /// cụ thể nên KHÔNG chết khi trang điều hướng/redirect. Dùng cho việc poll cookie xuyên suốt
    /// quá trình đăng nhập (Storage.getCookies hoạt động trên session cấp browser).
    /// </summary>
    public static async Task<CdpSession> ConnectToBrowserAsync(
        int cdpPort, CancellationToken ct = default, int waitTimeoutMs = 20_000)
    {
        var wsUrl = await WaitForBrowserWsUrlAsync(cdpPort, ct, waitTimeoutMs);
        var session = new CdpSession();
        await session.ConnectAsync(wsUrl, ct);
        return session;
    }

    /// <summary>Lấy <c>webSocketDebuggerUrl</c> cấp browser từ <c>/json/version</c>. Ném TimeoutException
    /// nếu endpoint không phản hồi (Brave chưa mở hoặc đã đóng).</summary>
    public static async Task<string> WaitForBrowserWsUrlAsync(
        int cdpPort, CancellationToken ct = default, int timeoutMs = 20_000)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{cdpPort}/json/version", ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var u))
                {
                    var url = u.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return url!;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"CDP browser endpoint on port {cdpPort} did not respond within {timeoutMs / 1000}s.");
    }

    /// <summary>Kiểm tra nhanh trình duyệt còn sống (endpoint CDP còn phản hồi). Không ném.</summary>
    public static async Task<bool> IsBrowserAliveAsync(int cdpPort, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync($"http://127.0.0.1:{cdpPort}/json/version", ct);
            return !string.IsNullOrWhiteSpace(json);
        }
        catch { return false; }
    }

    public static async Task<string> WaitForPageWsUrlAsync(
        int cdpPort, CancellationToken ct = default, int timeoutMs = 20_000)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // 127.0.0.1 (KHÔNG dùng "localhost") — Brave/Chromium chỉ nghe CDP trên IPv4
                // 127.0.0.1; "localhost" trên Windows phân giải ::1 (IPv6) trước → timeout/đứt.
                var json = await http.GetStringAsync($"http://127.0.0.1:{cdpPort}/json", ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var t) && t.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var u))
                    {
                        var url = u.GetString();
                        if (!string.IsNullOrWhiteSpace(url)) return url!;
                    }
                }
            }
            catch { }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"CDP on port {cdpPort} did not respond within {timeoutMs / 1000}s.");
    }

    private async Task ConnectAsync(string wsUrl, CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        _ = ReadLoopAsync(_cts.Token);
    }

    public async Task SendNoReplyAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var msg = JsonSerializer.Serialize(new { id, method, @params }, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(msg);
        await _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public async Task<JsonElement> SendAsync(
        string method, object? @params = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = @params is null
            ? $"{{\"id\":{id},\"method\":\"{method}\"}}"
            : JsonSerializer.Serialize(new { id, method, @params }, JsonOpts);

        var bytes = Encoding.UTF8.GetBytes(msg);
        await _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

        using var timeout = new CancellationTokenSource(20_000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        linked.Token.Register(() => tcs.TrySetCanceled());

        return await tcs.Task;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[128 * 1024];
        while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            _recvBuf.AddRange(new ArraySegment<byte>(buf, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var json = Encoding.UTF8.GetString([.. _recvBuf]);
            _recvBuf.Clear();

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id)
                    && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : root.Clone());
                }
            }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();

        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _cts.Dispose();
    }
}
