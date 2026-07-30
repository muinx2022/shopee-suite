using Shopee.Core.Infrastructure;

namespace Shopee.Core.Cdp;

/// <summary>
/// CDP client "port-based" (HTTP <c>/json</c> + WebSocket dùng-một-lần) dùng chung cho các module phóng
/// Brave (MultiBrave/UpdateProduct). Khác <see cref="CdpSession"/> (kết nối bền, đa lệnh qua flat-session):
/// lớp này mở/đóng WebSocket theo từng thao tác — hợp với luồng login/cookie ngắn. Gộp về Core từ 2 bản
/// nhân đôi byte-identical ở 2 module (chỉ khác namespace).
/// </summary>
public sealed class CdpClient(int cdpPort)
{
    public int Port { get; } = cdpPort;

    /// <summary>
    /// Đọc danh sách target (<c>/json/list</c>). NÉM khi HTTP lỗi hoặc thân phản hồi không phải mảng.
    /// <paramref name="timeoutMs"/> &gt; 0 = trần riêng cho lần gọi này (mặc định theo
    /// <see cref="AppServices.DirectHttp"/> = 15s).
    /// </summary>
    public static async Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(
        int port, CancellationToken cancellationToken = default, int timeoutMs = 0)
    {
        using var response = await GetAsync(CdpEndpoints.List(port), cancellationToken, timeoutMs)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return CdpTarget.ParseList(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Như <see cref="ListTargetsAsync"/> nhưng NUỐT mọi lỗi → danh sách rỗng (chỗ best-effort:
    /// Brave có thể đang khởi động lại / vừa bị kill).</summary>
    public static async Task<IReadOnlyList<CdpTarget>> TryListTargetsAsync(
        int port, CancellationToken cancellationToken = default, int timeoutMs = 0)
    {
        try
        {
            return await ListTargetsAsync(port, cancellationToken, timeoutMs).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Đóng 1 target theo id (<c>/json/close</c>). Best-effort: id rỗng hoặc lỗi mạng → bỏ qua.</summary>
    public static async Task CloseTargetAsync(
        int port, string? targetId, CancellationToken cancellationToken = default, int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return;

        try
        {
            using var _ = await GetAsync(CdpEndpoints.Close(port, targetId), cancellationToken, timeoutMs)
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>GET qua HttpClient no-proxy dùng chung; <paramref name="timeoutMs"/> &gt; 0 thì bọc thêm CTS
    /// riêng (KHÔNG đụng Timeout chung 15s của client).</summary>
    private static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct, int timeoutMs)
    {
        if (timeoutMs <= 0)
            return await AppServices.DirectHttp.GetAsync(url, ct).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        return await AppServices.DirectHttp.GetAsync(url, linked.Token).ConfigureAwait(false);
    }

    public async Task<string> GetPageWebSocketUrlAsync()
    {
        foreach (var target in await ListTargetsAsync(Port).ConfigureAwait(false))
        {
            if (target.IsPage && target.HasWsUrl)
                return target.WsUrl!;
        }

        throw new InvalidOperationException($"Khong co tab tren CDP port {Port}.");
    }

    public async Task<string> GetBrowserWebSocketUrlAsync()
    {
        using var response = await AppServices.DirectHttp.GetAsync(CdpEndpoints.Version(Port)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
               ?? throw new InvalidOperationException("CDP /json/version thieu browser WebSocket.");
    }

    public async Task<string?> FindPageWebSocketUrlAsync(Func<string, bool> urlMatches)
    {
        foreach (var target in await ListTargetsAsync(Port).ConfigureAwait(false))
        {
            if (target.IsPage && urlMatches(target.Url) && target.HasWsUrl)
                return target.WsUrl;
        }

        return null;
    }

    public async Task<string> EnsurePageTargetAsync(Func<string, bool> urlMatches, string createUrl)
    {
        var existing = await FindPageWebSocketUrlAsync(urlMatches).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), CancellationToken.None)
            .ConfigureAwait(false);
        await SendAsync(browser, 90, "Target.createTarget", new
        {
            url = createUrl,
            background = true,
        }).ConfigureAwait(false);

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(300).ConfigureAwait(false);
            var ws = await FindPageWebSocketUrlAsync(urlMatches).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ws))
                return ws;
        }

        return await GetPageWebSocketUrlAsync().ConfigureAwait(false);
    }

    public async Task<bool> WaitForReadyAsync(
        int attempts = 40,
        int delayMs = 500,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await AppServices.DirectHttp
                    .GetAsync(CdpEndpoints.Version(Port), cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // retry
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public async Task ReloadPageTargetsAsync(Func<string, bool> urlMatches)
    {
        foreach (var target in await TryListTargetsAsync(Port).ConfigureAwait(false))
        {
            if (!target.IsPage || !urlMatches(target.Url) || !target.HasWsUrl)
                continue;

            using var page = new ClientWebSocket();
            await page.ConnectAsync(new Uri(target.WsUrl!), CancellationToken.None).ConfigureAwait(false);
            await SendAsync(page, 91, "Page.reload", new { ignoreCache = true }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Điều hướng (Page.navigate) các tab khớp <paramref name="urlMatches"/> tới <paramref name="targetUrl"/>.
    /// Khác reload: ép tab rời khỏi trang hiện tại (vd trang marketing/login bị redirect khi chưa có cookie)
    /// để nạp thẳng khu app đã đăng nhập sau khi cookie vừa được set.
    /// </summary>
    public async Task NavigatePageTargetsAsync(Func<string, bool> urlMatches, string targetUrl)
    {
        foreach (var target in await TryListTargetsAsync(Port).ConfigureAwait(false))
        {
            if (!target.IsPage || !urlMatches(target.Url) || !target.HasWsUrl)
                continue;

            using var page = new ClientWebSocket();
            await page.ConnectAsync(new Uri(target.WsUrl!), CancellationToken.None).ConfigureAwait(false);
            await SendAsync(page, 93, "Page.navigate", new { url = targetUrl }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gửi 1 lệnh CDP qua WebSocket đang mở rồi chờ phản hồi mang đúng <paramref name="id"/>.
    /// <paramref name="sessionId"/>: gửi trong flat-session (Target.attachToTarget) — bỏ trống = lệnh cấp
    /// browser/page của chính socket. <paramref name="receiveTimeoutMs"/>: trần chờ phản hồi, hết giờ ném
    /// <see cref="TimeoutException"/> (Brave treo giữa chừng thì không để task kẹt vĩnh viễn); huỷ qua
    /// <paramref name="cancellationToken"/> vẫn ném OperationCanceledException như thường.
    /// </summary>
    public static async Task<JsonElement> SendAsync(
        ClientWebSocket socket, int id, string method, object? @params,
        CancellationToken cancellationToken = default, int receiveTimeoutMs = 30000,
        string? sessionId = null)
    {
        using var timeoutCts = new CancellationTokenSource(receiveTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linked.Token;

        // params = null thì BỎ HẲN khoá "params" (không gửi null) — đúng chuẩn CDP và khớp bản module cũ.
        var json = (@params, sessionId) switch
        {
            (null, null) => JsonSerializer.Serialize(new { id, method }),
            (null, _) => JsonSerializer.Serialize(new { id, method, sessionId }),
            (_, null) => JsonSerializer.Serialize(new { id, method, @params }),
            _ => JsonSerializer.Serialize(new { id, method, sessionId, @params }),
        };

        var bytes = Encoding.UTF8.GetBytes(json);

        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token)
                .ConfigureAwait(false);

            var buffer = new byte[1024 * 512];
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult recv;
                do
                {
                    recv = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (recv.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException("CDP socket dong.");
                    ms.Write(buffer, 0, recv.Count);
                } while (!recv.EndOfMessage);

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                    continue;
                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException($"CDP error: {err}");
                if (!root.TryGetProperty("result", out var result))
                    throw new InvalidOperationException("CDP result thieu.");
                return result.Clone();
            }
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Hết trần chờ (không phải người dùng huỷ) → TimeoutException có nêu tên lệnh: các vòng retry của
            // module nhận diện lỗi này qua thông điệp ("quá thời gian") để mở lại popup/SW thay vì bỏ cuộc.
            throw new TimeoutException($"CDP {method} quá thời gian chờ ({receiveTimeoutMs / 1000.0:0}s).");
        }
    }
}
