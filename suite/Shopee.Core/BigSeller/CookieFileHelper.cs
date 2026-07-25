namespace Shopee.Core.BigSeller;

/// <summary>
/// Tiện ích đọc/ghi file cookie (JSON) dùng chung cho các module xuất-nhập cookie BigSeller. Gộp về Core
/// từ 2 bản nhân đôi byte-identical (MultiBrave/UpdateProduct). Ghi qua file .tmp rồi <c>File.Move</c>
/// overwrite (atomic) + retry IOException → không để file cookie hỏng dở khi 2 lane cùng ghi.
/// </summary>
public static class CookieFileHelper
{
    public static JsonElement ParseCookiesRoot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("cookies", out var cp) ? cp.Clone() : root.Clone();
    }

    public static async Task<JsonElement> ParseCookiesRootFromFileAsync(
        string cookieFile,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(cookieFile);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return ParseCookiesRoot(json);
    }

    public static void ValidateCookiesArray(JsonElement cookiesEl)
    {
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie không hợp lệ.");
    }
}
