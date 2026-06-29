using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Shopee.Core.Coordination;

/// <summary>
/// Client HTTP gọi tới Hub (qua Cloudflare Tunnel). Gửi sẵn header X-Api-Token + X-Machine-Id.
/// Timeout ngắn để phát hiện offline nhanh; lỗi mạng → ném exception cho lớp trên xử lý (chặn việc).
/// </summary>
public sealed class HubClient
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public HubClient(HubClientConfig cfg, string machineId)
    {
        BaseUrl = (cfg.BaseUrl ?? "").TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(cfg.ApiToken))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Token", cfg.ApiToken);
        if (!string.IsNullOrWhiteSpace(machineId))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Machine-Id", machineId);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { return (await _http.GetAsync("/health", ct)).IsSuccessStatusCode; }
        catch { return false; }
    }

    // ── Khoá việc ──
    public async Task<LeaseAcquireResponse> AcquireAsync(LeaseAcquireRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync("/leases/acquire", req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<LeaseAcquireResponse>(ct) ?? new LeaseAcquireResponse(false, null);
    }
    public Task HeartbeatLeaseAsync(string key, string machineId, CancellationToken ct = default)
        => PostAsync("/leases/heartbeat", new LeaseHeartbeatRequest(key, machineId), ct);
    public Task ReleaseLeaseAsync(string key, string machineId, CancellationToken ct = default)
        => PostAsync("/leases/release", new LeaseReleaseRequest(key, machineId), ct);

    // ── Khoá tài khoản ──
    public async Task<AccountReserveResponse> ReserveAccountsAsync(AccountReserveRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync("/accounts/reserve", req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<AccountReserveResponse>(ct) ?? new AccountReserveResponse([], []);
    }
    public Task ReleaseAccountsAsync(AccountReleaseRequest req, CancellationToken ct = default) => PostAsync("/accounts/release", req, ct);
    public Task HeartbeatAccountsAsync(AccountReleaseRequest req, CancellationToken ct = default) => PostAsync("/accounts/heartbeat", req, ct);
    public async Task<List<AccountLease>> ActiveAccountsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<AccountLease>>("/accounts/active", ct) ?? [];

    // ── Sổ hoàn thành ──
    public Task PublishLedgerAsync(WorkLedgerRecord rec, CancellationToken ct = default) => PostAsync("/ledger", rec, ct);
    public async Task<List<WorkLedgerRecord>> AllLedgerAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<WorkLedgerRecord>>("/ledger", ct) ?? [];

    // ── Nhịp máy + bảng ──
    public Task MachineHeartbeatAsync(MachineHeartbeatRequest req, CancellationToken ct = default) => PostAsync("/machines/heartbeat", req, ct);
    public async Task<FleetSnapshot> FleetAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<FleetSnapshot>("/fleet", ct) ?? new FleetSnapshot();

    // ── Vai trò máy + giao việc ──
    public async Task<List<MachineRoleInfo>> RolesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<MachineRoleInfo>>("/roles", ct) ?? [];
    public Task SetRoleAsync(SetRoleRequest req, CancellationToken ct = default) => PostAsync("/roles", req, ct);
    public async Task<List<Assignment>> AssignmentsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Assignment>>("/assignments", ct) ?? [];
    public async Task<Assignment> CreateAssignmentAsync(CreateAssignmentRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync("/assignments", req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<Assignment>(ct) ?? new Assignment();
    }
    public async Task<List<Assignment>> ClaimAssignmentsAsync(ClaimAssignmentsRequest req, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync("/assignments/claim", req, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<List<Assignment>>(ct) ?? [];
    }
    public Task ReportAssignmentAsync(AssignmentStatusRequest req, CancellationToken ct = default) => PostAsync("/assignments/status", req, ct);
    public Task CancelAssignmentAsync(CancelAssignmentRequest req, CancellationToken ct = default) => PostAsync("/assignments/cancel", req, ct);

    // ── File-sync ──
    public async Task<List<FileManifestEntry>> ManifestAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<FileManifestEntry>>("/manifest", ct) ?? [];

    public async Task<byte[]?> DownloadAsync(string name, CancellationToken ct = default)
    {
        var r = await _http.GetAsync("/files/" + EncodePath(name), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<FilePutResponse> UploadAsync(string name, byte[] data, int? ifMatch, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, "/files/" + EncodePath(name)) { Content = new ByteArrayContent(data) };
        if (ifMatch.HasValue) req.Headers.TryAddWithoutValidation("If-Match", ifMatch.Value.ToString());
        var r = await _http.SendAsync(req, ct);
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
