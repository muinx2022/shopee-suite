namespace Shopee.Core.Cdp;

/// <summary>
/// Một entry trong danh sách target CDP (<c>/json/list</c>): tab, service worker, iframe out-of-process…
/// Thuộc tính thiếu trong JSON → chuỗi rỗng; riêng <see cref="WsUrl"/> = <c>null</c> nghĩa là entry KHÔNG có
/// <c>webSocketDebuggerUrl</c> (target đang bị debugger khác chiếm / không cho gắn).
/// </summary>
public sealed record CdpTarget(string Id, string Type, string Url, string? WsUrl, string Title = "")
{
    public bool IsPage => string.Equals(Type, "page", StringComparison.OrdinalIgnoreCase);

    public bool IsServiceWorker => string.Equals(Type, "service_worker", StringComparison.OrdinalIgnoreCase);

    public bool HasWsUrl => !string.IsNullOrWhiteSpace(WsUrl);

    /// <summary>
    /// Đọc mảng JSON của <c>/json/list</c>. Ném <see cref="InvalidOperationException"/> nếu thân phản hồi
    /// không phải mảng (endpoint lạ / Brave trả lỗi dạng object).
    /// </summary>
    public static IReadOnlyList<CdpTarget> ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CDP /json/list khong hop le.");

        var targets = new List<CdpTarget>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            targets.Add(new CdpTarget(
                Id: Str(item, "id"),
                Type: Str(item, "type"),
                Url: Str(item, "url"),
                WsUrl: item.TryGetProperty("webSocketDebuggerUrl", out var ws) ? ws.GetString() ?? "" : null,
                Title: Str(item, "title")));
        }

        return targets;
    }

    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";
}
