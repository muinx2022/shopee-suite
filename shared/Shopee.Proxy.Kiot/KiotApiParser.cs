using System.Text.Json;

namespace Shopee.Proxy.Kiot;

/// <summary>
/// Bóc phản hồi JSON của KiotProxy thành <see cref="KiotApiResult"/>. Thuần (không I/O),
/// KHÔNG BAO GIỜ ném: mọi lỗi parse → <c>Success=false</c>. Dùng chung cho
/// <see cref="KiotApiClient"/> và cho adapter tĩnh của từng module.
/// </summary>
public static class KiotApiParser
{
    /// <summary>
    /// Bóc thân JSON. <paramref name="httpStatus"/> (nếu biết) được gắn vào kết quả.
    /// <c>Success=false</c> khi: body rỗng, JSON hỏng, <c>success == false</c>, hoặc
    /// <c>status == "FAIL"</c> (không phân biệt hoa/thường). Toàn bộ <c>data</c> luôn được
    /// nhét vào <see cref="KiotProxyInfo.Raw"/> khi có.
    /// </summary>
    public static KiotApiResult ParseBody(string? json, int? httpStatus = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new KiotApiResult(false, null, null, httpStatus);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? message = null;
            var success = true;

            if (root.ValueKind == JsonValueKind.Object)
            {
                message = GetString(root, "message") ?? GetString(root, "error");

                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    success = false;
                }
                if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String &&
                    string.Equals(st.GetString(), "FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    success = false;
                }
            }

            // Ưu tiên đọc từ nhánh "data" nếu có; nếu không, coi cả root là data (JSON phẳng).
            var dataEl = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                dataEl = data;
            }

            return new KiotApiResult(success, message, ParseInfo(dataEl), httpStatus);
        }
        catch (JsonException)
        {
            return new KiotApiResult(false, json.Trim(), null, httpStatus);
        }
    }

    /// <summary>
    /// Ghép thông điệp lỗi từ <c>message</c> và <c>error</c> (nối bằng " | "). Không có field
    /// nào → trả JSON thô đã trim, hoặc "Loi khong xac dinh." khi rỗng. Dùng cho phản hồi
    /// HTTP không thành công (giữ nguyên chuỗi lỗi mà suite/MultiBrave đang dựa vào).
    /// </summary>
    public static string ExtractError(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty);
            var root = doc.RootElement;
            var parts = new List<string>();
            var msg = GetString(root, "message");
            if (!string.IsNullOrWhiteSpace(msg)) parts.Add(msg!);
            var err = GetString(root, "error");
            if (!string.IsNullOrWhiteSpace(err)) parts.Add(err!);
            if (parts.Count > 0) return string.Join(" | ", parts);
        }
        catch
        {
            // JSON không đọc được → rơi xuống trả body thô.
        }

        return string.IsNullOrWhiteSpace(json) ? "Loi khong xac dinh." : json.Trim();
    }

    private static KiotProxyInfo ParseInfo(JsonElement dataEl)
    {
        var raw = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (dataEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in dataEl.EnumerateObject())
            {
                raw[prop.Name] = ConvertValue(prop.Value);
            }
        }

        return new KiotProxyInfo(
            Host: GetString(dataEl, "host"),
            HttpPort: GetInt(dataEl, "httpPort"),
            Socks5Port: GetInt(dataEl, "socks5Port"),
            Http: GetString(dataEl, "http"),
            Socks5: GetString(dataEl, "socks5"),
            RealIpAddress: GetString(dataEl, "realIpAddress"),
            NextRequestAtMs: GetLong(dataEl, "nextRequestAt"),
            ExpirationAtMs: GetLong(dataEl, "expirationAt"),
            Raw: raw);
    }

    private static object? ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var i) ? i : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.ToString(),
    };

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object &&
           el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int? GetInt(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object &&
           el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number &&
           p.TryGetInt32(out var v)
            ? v
            : null;

    private static long? GetLong(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object &&
           el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number &&
           p.TryGetInt64(out var v)
            ? v
            : null;
}
