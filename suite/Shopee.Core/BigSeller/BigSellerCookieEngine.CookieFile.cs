namespace Shopee.Core.BigSeller;

// Partial của BigSellerCookieEngine: FILE cookie — đọc token từ file + mọi lối ghi file (atomic tmp+move). Pure move.
public static partial class BigSellerCookieEngine
{
    private static readonly JsonSerializerOptions FileJsonOpts = new() { WriteIndented = true };

    /// <summary>muc_token trong FILE cookie (đọc trực tiếp file). null nếu thiếu.</summary>
    public static AuthTokenInfo? GetFileAuthTokenInfo(string cookieFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cookieFile) || !File.Exists(cookieFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(cookieFile));
            var cookiesEl = doc.RootElement.TryGetProperty("cookies", out var cp) ? cp : doc.RootElement;
            if (cookiesEl.ValueKind != JsonValueKind.Array) return null;
            foreach (var ck in cookiesEl.EnumerateArray())
            {
                if (ck.ValueKind != JsonValueKind.Object) continue;
                var name = ck.TryGetProperty("name", out var np) ? np.GetString() : null;
                if (!string.Equals(name, AuthCookieName, StringComparison.OrdinalIgnoreCase)) continue;
                var domain = ck.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
                if (!domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase)) continue;
                var map = new Dictionary<string, object?>();
                if (ck.TryGetProperty("value", out var vp)) map["value"] = vp.GetString();
                if (ck.TryGetProperty("expires", out var ep) && ep.ValueKind == JsonValueKind.Number)
                    map["expires"] = ep.TryGetInt64(out var l) ? l : ep.GetDouble();
                return ToAuthTokenInfo(map);
            }
            return null;
        }
        catch { return null; }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Ghi cookie ra file
    // ──────────────────────────────────────────────────────────────────────────────

    public static bool TryWriteCookieFile(
        string cookieFile,
        IReadOnlyCollection<Dictionary<string, object?>> bigSellerCookies,
        Action<string>? log = null)
        => WriteAtomic(cookieFile,
            JsonSerializer.Serialize(new { exportedAt = DateTimeOffset.Now, cookies = bigSellerCookies }, FileJsonOpts),
            log);

    /// <summary>Overload ghi trực tiếp danh sách <see cref="JsonElement"/> (dùng cho login runner vốn giữ
    /// cookie ở dạng JsonElement thô) — CÙNG cơ chế atomic tmp+move, để chỉ có MỘT bản ghi file cookie.</summary>
    public static bool TryWriteCookieFile(
        string cookieFile,
        IReadOnlyCollection<JsonElement> cookies,
        Action<string>? log = null)
        => WriteAtomic(cookieFile,
            JsonSerializer.Serialize(new { exportedAt = DateTimeOffset.Now, cookies }, FileJsonOpts),
            log);

    /// <summary>Ghi BYTES THÔ ra file cookie theo cùng cơ chế nguyên tử (tmp unique → Move retry) — dùng khi
    /// đồng bộ cookie từ Hub (kéo về byte[] rồi ghi đè). Trả false + log nếu lỗi, KHÔNG ném. Mọi nơi ghi file
    /// cookie PHẢI đi qua đây (hoặc <see cref="TryWriteCookieFile"/>).</summary>
    public static bool TryWriteCookieFileBytes(string cookieFile, byte[] bytes, Action<string>? log = null)
        => WriteAtomicBytes(cookieFile, bytes, log);

    // Ghi NGUYÊN TỬ: tmp unique (tránh race đa-instance) → File.Move(overwrite) có retry. File cookie này
    // được Hub sync + các importer đọc đồng thời; ghi trực tiếp (WriteAllText/Bytes) sinh torn-read → cookie
    // hỏng lan ra đa máy. Mọi nơi ghi file cookie PHẢI đi qua đây.
    // Bản string chuyển sang UTF-8 KHÔNG BOM (đúng như File.WriteAllText mặc định trước đây) rồi ghi qua lõi bytes.
    private static bool WriteAtomic(string cookieFile, string json, Action<string>? log)
        => WriteAtomicBytes(cookieFile, Encoding.UTF8.GetBytes(json), log);

    private static bool WriteAtomicBytes(string cookieFile, byte[] bytes, Action<string>? log)
    {
        var tmp = $"{cookieFile}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cookieFile));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(tmp, bytes);

            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, cookieFile, overwrite: true); return true; }
                // Windows trả ERROR_ACCESS_DENIED (→ UnauthorizedAccessException, KHÔNG phải IOException) khi
                // 2 tiến trình cùng thay một file đích — chỉ bắt IOException thì retry không bao giờ chạy đúng
                // ca nó sinh ra để đỡ (đã kiểm chứng bằng test đột biến ở JsonAtomicFile).
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)
                {
                    Thread.Sleep(150);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            log?.Invoke($"BigSeller cookie: không lưu được ra file: {ex.Message}");
            return false;
        }
    }
}
