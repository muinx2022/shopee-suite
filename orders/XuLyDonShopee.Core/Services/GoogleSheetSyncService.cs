using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Một đơn hàng GỬI LÊN Web App Google Sheet (Apps Script). Trường null bị BỎ khi tuần tự hóa JSON
/// (hợp đồng với <c>doPost</c>: chỉ ghi ô đang trống). <see cref="DoanhThu"/> gửi dạng SỐ (JSON number),
/// KHÔNG format chuỗi — sheet của người dùng đang cộng tổng theo cột. <see cref="DaHuy"/> (JSON
/// <c>daHuy</c>) LUÔN xuất hiện kể cả <c>false</c> — script cần giá trị tường minh để đổi màu 2 chiều
/// (hủy → nền đỏ; hết hủy → xóa nền đỏ script đã tô).
/// </summary>
public sealed record GsheetOrderRow(
    string MaDon,
    string? MaVanDon,
    string? TenShop,
    long? DoanhThu,
    string? Ngay,
    string? Sku,
    string? FileName,
    string? FileBase64,
    bool DaHuy);

/// <summary>
/// Kết quả xử lý MỘT đơn phía Web App (đọc từ mảng <c>results</c> trong phản hồi):
/// <see cref="Ok"/> = ghi thành công; <see cref="Added"/> = thêm dòng mới (khác với điền bổ sung dòng cũ);
/// <see cref="FileUrl"/> = nội dung cột C sau khi ghi (link phiếu hiện có, null nếu chưa có);
/// <see cref="Error"/> = mô tả lỗi phía script (null nếu không lỗi).
/// </summary>
public sealed record GsheetOrderResult(
    string MaDon,
    bool Ok,
    bool Added,
    string? FileUrl,
    string? Error);

/// <summary>
/// Đẩy đơn hàng (kèm file phiếu PDF base64) lên <b>Google Apps Script Web App</b> bằng HTTP thuần
/// (<see cref="HttpClient"/> — KHÔNG dùng thư viện Google, tránh DLL mới bị WDAC chặn). Web App chạy dưới
/// tài khoản Google của chính người dùng nên file phiếu nằm trong Drive của họ (Google chặn service account
/// lưu Drive từ 2025 → không xài Sheets API + service account nữa).
/// <para>
/// Nhiều phiên tài khoản sync SONG SONG → có <see cref="SemaphoreSlim"/> xếp hàng phía client (server đã có
/// LockService, nhưng không dội cho đỡ tranh chấp). Chia lô ≤ 10 đơn/POST (mỗi PDF base64 ~100–300KB).
/// Apps Script trả HTTP 302 sang <c>script.googleusercontent.com</c> → .NET tự GET theo (AllowAutoRedirect
/// mặc định bật) và nhận JSON — ĐỪNG tắt redirect.
/// </para>
/// </summary>
public class GoogleSheetSyncService
{
    /// <summary>Số đơn tối đa mỗi lô POST (payload base64 PDF ~100–300KB/file → giữ lô nhỏ cho an toàn).</summary>
    private const int BatchSize = 10;

    /// <summary>Thời hạn mỗi lô (giây) — CTS liên kết với ct của caller.</summary>
    private const int PerBatchTimeoutSec = 120;

    // HttpClient DÙNG CHUNG (tránh cạn socket). Timeout để INFINITE ở đây, kiểm soát thời hạn từng lô bằng
    // CTS liên kết (HttpClient.Timeout áp cho cả thao tác nên không dùng riêng cho từng lô được).
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Xếp hàng phía client: nhiều phiên gọi PushAsync song song → không dội cùng lúc.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Đẩy <paramref name="rows"/> lên <paramref name="webAppUrl"/> (ghi vào tab <paramref name="tabName"/>)
    /// theo từng lô ≤ 10 đơn. Trả về kết quả gộp của mọi lô đã gửi. URL trống / danh sách rỗng → trả rỗng
    /// ngay. Một lô LỖI (timeout / HTTP ≠ 2xx / response không đọc được / script trả <c>{"error":…}</c> khi
    /// không tìm thấy tab) → ném <see cref="InvalidOperationException"/> (kèm mã HTTP + 200 ký tự đầu body /
    /// message lỗi của script) và DỪNG các lô sau (mạng đang hỏng — lượt sync sau tự đẩy lại nhờ cờ DB). Hủy
    /// chủ động (ct) → ném <see cref="OperationCanceledException"/> xuyên để caller dừng sạch.
    /// </summary>
    public async Task<IReadOnlyList<GsheetOrderResult>> PushAsync(
        string webAppUrl, string tabName, IReadOnlyList<GsheetOrderRow> rows, Action<string> log, CancellationToken ct)
    {
        var all = new List<GsheetOrderResult>();
        if (string.IsNullOrWhiteSpace(webAppUrl) || rows is null || rows.Count == 0)
        {
            return all;
        }

        var batches = ChiaLo(rows, BatchSize);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int i = 0; i < batches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var batch = batches[i];
                if (batches.Count > 1)
                {
                    log($"GSheet: đang gửi lô {i + 1}/{batches.Count} ({batch.Count} đơn)...");
                }

                var body = TaoJsonBody(tabName, batch);

                // Thời hạn từng lô: CTS liên kết ct + hết giờ sau PerBatchTimeoutSec.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(PerBatchTimeoutSec));

                int status;
                string respBody;
                try
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var resp = await Http.PostAsync(webAppUrl, content, cts.Token).ConfigureAwait(false);
                    status = (int)resp.StatusCode;
                    respBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct); // hủy chủ động → ném xuyên
                }
                catch (OperationCanceledException)
                {
                    // cts hết giờ (KHÔNG do ct) → timeout lô. Message rõ (không phải "A task was canceled").
                    throw new InvalidOperationException(
                        $"gọi Web App quá {PerBatchTimeoutSec} giây/lô — mạng chậm hoặc script treo.");
                }
                catch (Exception ex)
                {
                    // Lỗi mạng → ném để log + DỪNG các lô sau. KHÔNG thêm tiền tố "GSheet:" (caller tự thêm).
                    throw new InvalidOperationException("gọi Web App thất bại — " + ex.Message, ex);
                }

                if (status < 200 || status >= 300)
                {
                    throw new InvalidOperationException(
                        $"trả HTTP {status}: {Truncate(respBody, 200)}");
                }

                IReadOnlyList<GsheetOrderResult> parsed;
                try
                {
                    parsed = DocKetQua(respBody);
                }
                catch (JsonException ex)
                {
                    // JSON RÁC → bọc kèm body để chẩn đoán. InvalidOperationException từ DocKetQua ({"error":…}:
                    // không tìm thấy tab) KHÔNG bắt ở đây — cho XUYÊN QUA nguyên vẹn để giữ thông điệp người dùng cần.
                    throw new InvalidOperationException(
                        $"trả về không đọc được (HTTP {status}): {Truncate(respBody, 200)}", ex);
                }

                all.AddRange(parsed);
            }
        }
        finally
        {
            _gate.Release();
        }

        return all;
    }

    /// <summary>
    /// Chia <paramref name="rows"/> thành các lô tối đa <paramref name="max"/> phần tử (giữ nguyên thứ tự).
    /// Danh sách rỗng / <paramref name="max"/> ≤ 0 → trả danh sách rỗng. Tách static để test được.
    /// </summary>
    internal static List<List<GsheetOrderRow>> ChiaLo(IReadOnlyList<GsheetOrderRow> rows, int max)
    {
        var batches = new List<List<GsheetOrderRow>>();
        if (rows is null || rows.Count == 0 || max <= 0)
        {
            return batches;
        }

        for (int i = 0; i < rows.Count; i += max)
        {
            var batch = new List<GsheetOrderRow>();
            var end = Math.Min(i + max, rows.Count);
            for (int j = i; j < end; j++)
            {
                batch.Add(rows[j]);
            }
            batches.Add(batch);
        }

        return batches;
    }

    // Tùy chọn JSON: camelCase (hợp đồng script: maDon/maVanDon/tenShop/doanhThu/ngay/sku/fileName/fileBase64),
    // BỎ field null (chỉ điền ô trống), Relaxed escaping (đỡ escape '+' '/' của base64 → payload gọn hơn).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Tạo JSON body <c>{"tab":"tháng 4","orders":[{...}]}</c> cho một lô: camelCase, BỎ field null, số để
    /// dạng SỐ (không bọc nháy), <c>daHuy</c> LUÔN có mặt (bool). <paramref name="tabName"/> = tab đích của
    /// script. Tách static để test được hợp đồng với script.
    /// </summary>
    internal static string TaoJsonBody(string tabName, IEnumerable<GsheetOrderRow> rows)
        => JsonSerializer.Serialize(new { tab = tabName, orders = rows }, JsonOpts);

    /// <summary>
    /// Parse phản hồi <c>{"results":[{maDon,ok,added,fileUrl,error}, ...]}</c> thành danh sách
    /// <see cref="GsheetOrderResult"/>. Field thiếu → mặc định an toàn (ok/added=false, fileUrl/error=null).
    /// Phản hồi lỗi cấp script <c>{"error":"Không tìm thấy tab …"}</c> → ném
    /// <see cref="InvalidOperationException"/> với đúng message đó (người dùng cần sửa tên tab). JSON rác →
    /// ném (<see cref="JsonException"/>). Tách static để test được.
    /// </summary>
    internal static IReadOnlyList<GsheetOrderResult> DocKetQua(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<GsheetOrderResult>();

        // Script không tìm thấy tab đích (hoặc lỗi cấp script) → {"error":"..."} → ném với message đó.
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out var errEl)
            && errEl.ValueKind == JsonValueKind.String)
        {
            throw new InvalidOperationException(errEl.GetString() ?? "GSheet trả về lỗi không rõ.");
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                list.Add(new GsheetOrderResult(
                    MaDon: GetString(r, "maDon") ?? string.Empty,
                    Ok: GetBool(r, "ok"),
                    Added: GetBool(r, "added"),
                    FileUrl: GetString(r, "fileUrl"),
                    Error: GetString(r, "error")));
            }
        }

        return list;
    }

    /// <summary>Đọc chuỗi từ property (chỉ nhận String; thiếu / null / kiểu khác → null).</summary>
    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Đọc bool từ property (chỉ true khi kiểu là true; thiếu / false / kiểu khác → false).</summary>
    private static bool GetBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Cắt chuỗi tối đa <paramref name="max"/> ký tự (cho thông báo lỗi chẩn đoán).</summary>
    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max));
}
