using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Shopee.Core.BigSeller;

namespace Shopee.Core.Coordination;

/// <summary>
/// Client HTTP gọi tới Hub (qua Cloudflare Tunnel). Gửi sẵn header X-Api-Token + X-Machine-Id.
/// Timeout ngắn để phát hiện offline nhanh; lỗi mạng → ném exception cho lớp trên xử lý (chặn việc).
/// </summary>
public sealed class HubClient
{
    private readonly HttpClient _http;
    /// <summary>Client riêng cho endpoint TẢI/ĐẨY dữ liệu KHỐI LỚN (kho gộp Search) — timeout dài, vì 8s của
    /// _http là để phát hiện offline nhanh cho control-plane, không đủ cho vài MB JSON qua tunnel.</summary>
    private readonly HttpClient _bulkHttp;
    private readonly string _machineId;
    public string BaseUrl { get; }

    public HubClient(HubClientConfig cfg, string machineId)
    {
        BaseUrl = (cfg.BaseUrl ?? "").TrimEnd('/');
        _machineId = machineId ?? "";
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(8) };
        _bulkHttp = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(cfg.ApiToken))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Token", cfg.ApiToken);
            _bulkHttp.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Token", cfg.ApiToken);
        }
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Machine-Id", machineId);
            _bulkHttp.DefaultRequestHeaders.TryAddWithoutValidation("X-Machine-Id", machineId);
        }
    }

    /// <summary>Báo Hub xoá máy này khỏi danh sách (khi người dùng chủ động Ngắt kết nối).</summary>
    public Task LeaveAsync(CancellationToken ct = default) => PostAsync(HubRoutes.MachineLeave, new MachineLeaveRequest(_machineId), ct);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { return (await _http.GetAsync(HubRoutes.Health, ct)).IsSuccessStatusCode; }
        catch { return false; }
    }

    // ── Khoá việc ──
    public async Task<LeaseAcquireResponse> AcquireAsync(LeaseAcquireRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.LeasesAcquire, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<LeaseAcquireResponse>(ct) ?? new LeaseAcquireResponse(false, null);
    }
    public Task HeartbeatLeaseAsync(string key, string machineId, CancellationToken ct = default)
        => PostAsync(HubRoutes.LeasesHeartbeat, new LeaseHeartbeatRequest(key, machineId), ct);
    public Task ReleaseLeaseAsync(string key, string machineId, CancellationToken ct = default)
        => PostAsync(HubRoutes.LeasesRelease, new LeaseReleaseRequest(key, machineId), ct);

    // ── Khoá tài khoản ──
    public async Task<AccountReserveResponse> ReserveAccountsAsync(AccountReserveRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.AccountsReserve, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<AccountReserveResponse>(ct) ?? new AccountReserveResponse([], []);
    }
    public Task ReleaseAccountsAsync(AccountReleaseRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.AccountsRelease, req, ct);
    public Task HeartbeatAccountsAsync(AccountReleaseRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.AccountsHeartbeat, req, ct);

    // ── Sổ hoàn thành ──
    public Task PublishLedgerAsync(WorkLedgerRecord rec, CancellationToken ct = default) => PostAsync(HubRoutes.Ledger, rec, ct);
    public Task SetLedgerStatusAsync(SetLedgerStatusRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.LedgerSet, req, ct);
    public async Task<List<WorkLedgerRecord>> AllLedgerAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<WorkLedgerRecord>>(HubRoutes.Ledger, ct) ?? [];

    // ── Nhịp máy + bảng ──
    /// <summary>Gửi nhịp máy; TRẢ VỀ phản hồi (kênh Hub đẩy lệnh xuống client, vd lệnh update app). Lỗi HTTP/mạng
    /// GIỮ NÉM (EnsureSuccessStatusCode) — PollAsync dựa vào catch để giữ đúng hành vi offline cũ. Nhưng body RỖNG
    /// (Hub CŨ chưa nâng cấp trả 200 body rỗng) / JSON hỏng → trả null trong try/catch riêng, KHÔNG ném: không có
    /// lệnh, chứ không phải mất kết nối (kẻo nuốt luôn heartbeat vừa gửi THÀNH CÔNG thành "offline").</summary>
    public async Task<MachineHeartbeatResponse?> MachineHeartbeatAsync(MachineHeartbeatRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.MachineHeartbeat, req, ct);
        r.EnsureSuccessStatusCode();
        try { return await r.Content.ReadFromJsonAsync<MachineHeartbeatResponse>(ct); }
        catch { return null; }
    }
    /// <summary>Client báo tiến trình/kết quả tự-update app về Hub (lỗi mạng → ném theo convention PostAsync;
    /// chỗ gọi ở HttpCoordinationHub tự nuốt).</summary>
    public Task AckUpdateAsync(UpdateAckRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.MachineUpdateAck, req, ct);
    public async Task<FleetSnapshot> FleetAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<FleetSnapshot>(HubRoutes.Fleet, ct) ?? new FleetSnapshot();

    // ── Vai trò máy + giao việc ──
    public Task SetRoleAsync(SetRoleRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.Roles, req, ct);
    public async Task<Assignment> CreateAssignmentAsync(CreateAssignmentRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.Assignments, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<Assignment>(ct) ?? new Assignment();
    }
    public async Task<List<Assignment>> ClaimAssignmentsAsync(ClaimAssignmentsRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.AssignmentsClaim, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<List<Assignment>>(ct) ?? [];
    }
    public Task ReportAssignmentAsync(AssignmentStatusRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.AssignmentsStatus, req, ct);
    public Task CancelAssignmentAsync(CancelAssignmentRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.AssignmentsCancel, req, ct);
    public async Task<string?> ResumeAssignmentAsync(ResumeAssignmentRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.AssignmentsResume, req, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ResumeAssignmentResponse>(ct))?.Error;
    }
    public async Task<int> ResumeMineAsync(ResumeMineRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.AssignmentsResumeMine, req, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ResumeMineResponse>(ct))?.Requeued ?? 0;
    }

    // ── Kho gộp kết quả Search ── (dùng _bulkHttp timeout dài cho push/fetch khối lớn)
    public async Task PushSearchProductsAsync(SearchProductsPushRequest req, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.SearchProducts, req, ct);
        r.EnsureSuccessStatusCode();
    }
    public async Task<List<string>> SearchProductsAsync(CancellationToken ct = default)
        => await _bulkHttp.GetFromJsonAsync<List<string>>(HubRoutes.SearchProducts, ct) ?? [];
    public async Task<int> SearchProductCountAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<int>(HubRoutes.SearchProductsCount, ct);
    public async Task ClearSearchProductsAsync(CancellationToken ct = default)
    {
        var r = await _http.PostAsync(HubRoutes.SearchProductsClear, null, ct);
        r.EnsureSuccessStatusCode();
    }

    // ── Kho sản phẩm (Postgres — thay dần workbook Excel) ── (dùng _bulkHttp: payload dòng có thể vài MB qua tunnel)
    // Lỗi theo convention GET/POST hiện có: non-2xx → HttpRequestException (kèm StatusCode → 503 pg-not-ready
    // phân biệt được với mất kết nối StatusCode=null); timeout → TaskCanceledException. Lớp trên tự nuốt/chặn việc.
    // Sheet có thể chứa dấu cách/ký tự Việt → Uri.EscapeDataString từng tham số.
    public async Task<List<ProductSheetInfo>?> GetProductSheetsAsync(string acct, CancellationToken ct = default)
        => await _bulkHttp.GetFromJsonAsync<List<ProductSheetInfo>>(
            $"{HubRoutes.ProductsSheets}?acct={Uri.EscapeDataString(acct)}", ct);

    public async Task<List<ProductLinkRow>?> GetProductLinksAsync(string acct, string sheet, int fromDense, int toDense, CancellationToken ct = default)
        => await _bulkHttp.GetFromJsonAsync<List<ProductLinkRow>>(
            $"{HubRoutes.ProductsLinks}?acct={Uri.EscapeDataString(acct)}&sheet={Uri.EscapeDataString(sheet)}&fromDense={fromDense}&toDense={toDense}", ct);

    public async Task<List<ProductRecordRow>?> GetProductRecordMapAsync(string acct, string sheet, int fromRow, int toRow, CancellationToken ct = default)
        => await _bulkHttp.GetFromJsonAsync<List<ProductRecordRow>>(
            $"{HubRoutes.ProductsRecordMap}?acct={Uri.EscapeDataString(acct)}&sheet={Uri.EscapeDataString(sheet)}&fromRow={fromRow}&toRow={toRow}", ct);

    public async Task<List<ProductImportIdRow>?> GetProductImportIdsAsync(string acct, string sheet, int fromRow, int toRow, CancellationToken ct = default)
        => await _bulkHttp.GetFromJsonAsync<List<ProductImportIdRow>>(
            $"{HubRoutes.ProductsImportIds}?acct={Uri.EscapeDataString(acct)}&sheet={Uri.EscapeDataString(sheet)}&fromRow={fromRow}&toRow={toRow}", ct);

    public async Task<List<ProductRewritePendingRow>?> GetProductRewritePendingAsync(string acct, string sheet, int fromRow, int toRow, CancellationToken ct = default)
        => await _bulkHttp.GetFromJsonAsync<List<ProductRewritePendingRow>>(
            $"{HubRoutes.ProductsRewritePending}?acct={Uri.EscapeDataString(acct)}&sheet={Uri.EscapeDataString(sheet)}&fromRow={fromRow}&toRow={toRow}", ct);

    public async Task<ProductRewrittenResponse?> PostProductRewrittenAsync(ProductRewrittenRequest req, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsRewritten, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ProductRewrittenResponse>(ct);
    }

    public async Task<ProductAppendResponse?> PostProductAppendAsync(ProductAppendRequest req, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsAppend, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ProductAppendResponse>(ct);
    }

    // ── RESUME per-SP: báo Hub đã Import / đã Update N itemId (tối ưu lọc lượt sau). Lỗi mạng → ném theo convention;
    //    chỗ GỌI ở runner tự try/catch nuốt (store local là nguồn chính, mark-* thất bại KHÔNG làm hỏng lượt chạy). ──
    public async Task<int> MarkProductImportedAsync(string acct, string sheet, string[] itemIds, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsMarkImported, new ProductMarkStoreRequest(acct, sheet, itemIds), ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductMarkStoreResponse>(ct))?.Updated ?? 0;
    }

    public async Task<int> MarkProductUpdatedAsync(string acct, string sheet, string[] itemIds, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsMarkUpdated, new ProductMarkStoreRequest(acct, sheet, itemIds), ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductMarkStoreResponse>(ct))?.Updated ?? 0;
    }

    public async Task<int> ResetProductStoreProgressAsync(string acct, string sheet, string op, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsResetStoreProgress, new ProductResetStoreRequest(acct, sheet, op), ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductResetStoreResponse>(ct))?.Reset ?? 0;
    }

    // ── Trang "📦 Dữ liệu" (mọi shop) — client desktop thao tác qua HTTP ──
    public async Task<AllDataPage?> QueryProductAllDataAsync(AllDataQueryRequest req, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsAllData, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<AllDataPage>(ct);
    }

    public async Task<int> MarkProductsSoldAsync(List<ProductRowKey> keys, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsMarkSold, new ProductKeysRequest(keys), ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductCountResponse>(ct))?.Count ?? 0;
    }

    /// <summary>+1 "Đã bán" theo SKU khớp tuyệt đối (mọi shop) trên kho hub. Trả true = hub nhận OK (2xx);
    /// EnsureSuccessStatusCode ném khi lỗi → caller (wire ở OrdersModuleHost) bắt và trả false để lượt sync sau
    /// thử lại (đơn CHƯA đánh cờ sold_counted_at).</summary>
    public async Task<bool> MarkProductsSoldBySkuAsync(IReadOnlyList<string> skus, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsMarkSoldBySku, new ProductMarkSoldBySkuRequest(skus), ct);
        r.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<int> ResetProductsSoldAsync(List<ProductRowKey> keys, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsResetSold, new ProductKeysRequest(keys), ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductCountResponse>(ct))?.Count ?? 0;
    }

    public async Task<int> RegenProductSkusAsync(List<ProductRowKey> keys, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsRegenSkus, new ProductKeysRequest(keys), ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductCountResponse>(ct))?.Count ?? 0;
    }

    public async Task<int> DeleteProductRowsAsync(List<ProductRowKey> keys, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsDeleteRows, new ProductKeysRequest(keys), ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductCountResponse>(ct))?.Count ?? 0;
    }

    public async Task<bool> UpdateProductRowAsync(ProductUpdateRowRequest req, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsUpdateRow, req, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProductUpdateRowResponse>(ct))?.Ok ?? false;
    }

    public async Task<ProductInsertRowResponse?> InsertProductRowAsync(ProductInsertRowRequest req, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.ProductsInsertRow, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ProductInsertRowResponse>(ct);
    }

    public async Task<bool> ProductSkuExistsAsync(string acct, string sheet, string sku, int excludeRowNo, CancellationToken ct = default)
    {
        var url = $"{HubRoutes.ProductsSkuExists}?acct={Uri.EscapeDataString(acct)}&sheet={Uri.EscapeDataString(sheet)}"
                + $"&sku={Uri.EscapeDataString(sku)}&excludeRowNo={excludeRowNo}";
        return (await _bulkHttp.GetFromJsonAsync<ProductSkuExistsResponse>(url, ct))?.Exists ?? false;
    }

    // ── Log tập trung (tab Log gom log nhiều máy) ──
    public Task AppendLogAsync(AppendLogRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.Logs, req, ct);
    public async Task<List<LogEntry>> LogsAsync(long after, int max, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<LogEntry>>($"{HubRoutes.Logs}?after={after}&max={max}", ct) ?? [];
    public async Task ClearLogsAsync(CancellationToken ct = default)
    { var r = await _http.PostAsync(HubRoutes.LogsClear, null, ct); r.EnsureSuccessStatusCode(); }

    // ── Client báo acc Shopee lỗi/captcha ──
    public Task ReportErroredAccountAsync(AccountErrorRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.AccountsErrored, req, ct);
    public async Task<List<AccountError>> ErroredAccountsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<AccountError>>(HubRoutes.AccountsErrored, ct) ?? [];
    public Task ClearErroredAccountAsync(ClearAccountErrorRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.AccountsErroredClear, req, ct);

    // ── Upsert acc/shop BigSeller client → hub (client là nguồn phát sinh; hub gộp KHÔNG xóa) ──
    // Dùng _http (control-plane 8s): payload chỉ là danh sách acc/shop (không có workbook) → nhỏ.
    public async Task<BigSellerUpsertResult?> PostBigSellerUpsertAsync(List<BigSellerAccount> accounts, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(HubRoutes.BigSellerUpsert, accounts, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<BigSellerUpsertResult>(ct);
    }

    // ── Đơn hàng: client đẩy lô đơn đã sync của 1 shop lên hub (hub tự đăng ký shop theo username) ──
    // Dùng _bulkHttp (timeout dài): lô đơn 1 lượt sync có thể lớn (nhiều KB JSON qua tunnel).
    public async Task<OrdersPushResult?> PushOrdersAsync(OrdersPushRequest req, CancellationToken ct = default)
    {
        var r = await _bulkHttp.PostAsJsonAsync(HubRoutes.OrdersPush, req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<OrdersPushResult>(ct);
    }

    // ── File-sync ──
    public async Task<List<FileManifestEntry>> ManifestAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<FileManifestEntry>>(HubRoutes.Manifest, ct) ?? [];

    public async Task<byte[]?> DownloadAsync(string name, CancellationToken ct = default)
    {
        // _bulkHttp (5') CHỨ KHÔNG _http (8s): workbook Excel vài MB tải qua Cloudflare Tunnel thường > 8s →
        // trước đây TimeoutException bị nuốt ở PullAccountsAsync → WorkbookPath KHÔNG rebase (giữ đường máy Hub)
        // → client ở XA scrape lỗi, dù client cùng LAN (tải nhanh) vẫn chạy. 8s chỉ hợp control-plane JSON nhỏ.
        var r = await _bulkHttp.GetAsync(HubRoutes.Files + EncodePath(name), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<FilePutResponse> UploadAsync(string name, byte[] data, int? ifMatch, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, HubRoutes.Files + EncodePath(name)) { Content = new ByteArrayContent(data) };
        if (ifMatch.HasValue) req.Headers.TryAddWithoutValidation("If-Match", ifMatch.Value.ToString());
        // _bulkHttp (5'): đẩy workbook vài MB qua tunnel cũng cần timeout dài (Hub đẩy lên localhost thì nhanh,
        // nhưng client đẩy Search/handoff qua tunnel thì 8s không đủ). Đối xứng với DownloadAsync.
        var r = await _bulkHttp.SendAsync(req, ct);
        // 200 (ok) và 409 (conflict) đều trả JSON FilePutResponse; còn lại (401/5xx) body là text → trả lỗi
        // HTTP rõ ràng thay vì để ReadFromJsonAsync ném JsonException khó hiểu.
        if (!r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.Conflict)
            return new FilePutResponse(false, 0, $"http-{(int)r.StatusCode}");
        return await r.Content.ReadFromJsonAsync<FilePutResponse>(ct) ?? new FilePutResponse(false, 0, "no-response");
    }

    private async Task PostAsync<T>(string path, T body, CancellationToken ct)
    {
        var r = await _http.PostAsJsonAsync(path, body, ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Encode từng đoạn tên (giữ '/') để URL an toàn với tên có dấu cách/ký tự lạ.</summary>
    private static string EncodePath(string name) =>
        string.Join('/', name.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
}
