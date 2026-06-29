using Shopee.Core.Scrape;

namespace Shopee.Core.Coordination;

/// <summary>
/// Hiện thực <see cref="ICoordinationHub"/> qua HTTP tới Hub. Giành/nhả khoá, heartbeat nền, đẩy
/// tiến độ/hoàn thành lên ledger, giành/nhả account-lease, và poll /fleet định kỳ để cập nhật bảng.
/// Offline (lỗi mạng) → AcquireAsync trả Blocked("mất kết nối hub") ⇒ điểm chạy CHẶN việc mới.
/// </summary>
public sealed class HttpCoordinationHub : ICoordinationHub, IDisposable
{
    private readonly HubClient _client;
    private readonly string _machineId;
    private readonly Timer _poller;
    private volatile FleetSnapshot _fleet = new();
    private int _foldedLedger;   // 0 = chưa fold ledger→tiến độ local; chỉ fold 1 lần khi Hub LẦN ĐẦU liên lạc được

    /// <summary>Tên hiển thị máy này, đọc LIVE → đổi tên trong Settings có hiệu lực ngay lượt gửi kế tiếp.</summary>
    private static string Host => MachineIdentity.Shared.DisplayName;

    public bool Enabled => true;
    public event Action? Changed;

    public HttpCoordinationHub(HubClient client, string machineId)
    {
        _client = client;
        _machineId = machineId;
        _poller = new Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(12));
    }

    public void Dispose() => _poller.Dispose();

    public FleetSnapshot CurrentFleet => _fleet;
    public IReadOnlyList<LeaseRecord> ActiveLeases() => _fleet.Leases;
    public IReadOnlyList<AccountLease> ActiveAccountLeases() => _fleet.AccountLeases;
    public string MachineId => _machineId;

    private async Task PollAsync()
    {
        try
        {
            await _client.MachineHeartbeatAsync(new MachineHeartbeatRequest(_machineId, Host, null));
            _fleet = await _client.FleetAsync();
            // Fold ledger→tiến độ local CHỈ 1 lần, SAU khi Hub thật sự trả lời (tránh race lúc máy-Hub vừa khởi động:
            // server localhost chưa kịp lắng nghe). Poller 12s sẽ tự fold ở tick thành công đầu tiên.
            if (Interlocked.Exchange(ref _foldedLedger, 1) == 0) _ = SyncIntoProgressAsync();
            try { Changed?.Invoke(); } catch { }
        }
        catch { /* offline: giữ snapshot cũ, không ném */ }
    }

    public async Task<LeaseAttempt> AcquireAsync(CoordKey key, bool force, CancellationToken ct)
    {
        try
        {
            var req = new LeaseAcquireRequest(
                key.Id, key.BigsellerId, key.ShopId, key.Sheet, OpStr(key.Op), _machineId, Host, force);
            var resp = await _client.AcquireAsync(req, ct);
            if (!resp.Granted) return new LeaseAttempt(AcquireResult.Blocked(resp.BlockedByHostname), null);
            var handle = new LeaseHandle(this, key);
            handle.StartHeartbeat();
            return new LeaseAttempt(AcquireResult.Ok(), handle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // người dùng/hệ thống HUỶ → để caller xử lý như "đã dừng", KHÔNG coi là bị máy khác giữ
        }
        catch (Exception ex)
        {
            // Không xác nhận được khoá → CHẶN việc mới cho an toàn. Phân biệt Hub-báo-lỗi với mất-kết-nối để
            // người dùng biết "Chạy đè" có ích hay không (timeout của HttpClient cũng rơi vào nhánh mất-kết-nối).
            var why = ex is System.Net.Http.HttpRequestException { StatusCode: not null } ? "(hub báo lỗi)" : "(mất kết nối hub)";
            return new LeaseAttempt(AcquireResult.Blocked(why), null);
        }
    }

    public void PublishProgress(CoordKey key, int from, int to) => _ = TryPublish(new WorkLedgerRecord
    {
        Key = key.Id, BigsellerId = key.BigsellerId, ShopId = key.ShopId, Sheet = key.Sheet, Op = OpStr(key.Op),
        Completed = [new RowRange { From = from, To = to }], LastRowReached = to,
        Status = "running", LastMachineId = _machineId, LastHostname = Host, LastRunAt = DateTimeOffset.Now,
    });

    public void PublishCompletion(CoordKey key, string status, int lastRow) => _ = TryPublish(new WorkLedgerRecord
    {
        Key = key.Id, BigsellerId = key.BigsellerId, ShopId = key.ShopId, Sheet = key.Sheet, Op = OpStr(key.Op),
        LastRowReached = lastRow, Status = status,
        LastMachineId = _machineId, LastHostname = Host, LastRunAt = DateTimeOffset.Now,
    });

    private async Task TryPublish(WorkLedgerRecord rec)
    {
        try { await _client.PublishLedgerAsync(rec); } catch { }
    }

    // ── Account-lease (chống dùng trùng acc Shopee xuyên máy) ──
    /// <summary>Giành các acc; trả về tập ĐƯỢC cấp. Offline → cấp hết (degrade về như 1 máy).</summary>
    public async Task<HashSet<string>> ReserveAccountsAsync(IEnumerable<string> ids)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return [];
        try
        {
            var r = await _client.ReserveAccountsAsync(new AccountReserveRequest(list, _machineId, Host));
            return r.Granted.ToHashSet(StringComparer.Ordinal);
        }
        catch { return list.ToHashSet(StringComparer.Ordinal); }
    }

    public async Task ReleaseAccountsAsync(IEnumerable<string> ids)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return;
        try { await _client.ReleaseAccountsAsync(new AccountReleaseRequest(list, _machineId)); } catch { }
    }

    public async Task HeartbeatAccountsAsync(IEnumerable<string> ids)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return;
        try { await _client.HeartbeatAccountsAsync(new AccountReleaseRequest(list, _machineId)); } catch { }
    }

    // ── Vai trò máy + giao việc (Hub đẩy việc cho client) ──
    /// <summary>Đặt vai trò cho 1 máy (Hub gọi từ bảng điều phối).</summary>
    public async Task SetRoleAsync(string machineId, string role)
    {
        try { await _client.SetRoleAsync(new SetRoleRequest(machineId, role)); } catch { }
    }

    /// <summary>Tạo 1 việc giao (Hub gọi). Lỗi mạng → trả Assignment rỗng.</summary>
    public async Task<Assignment?> CreateAssignmentAsync(CreateAssignmentRequest req)
    {
        try { return await _client.CreateAssignmentAsync(req); } catch { return null; }
    }

    /// <summary>Máy này xin nhận tối đa <paramref name="max"/> việc đúng vai trò (client gọi).</summary>
    public async Task<List<Assignment>> ClaimAssignmentsAsync(string role, int max)
    {
        try { return await _client.ClaimAssignmentsAsync(new ClaimAssignmentsRequest(_machineId, role, max)); }
        catch { return []; }
    }

    /// <summary>Báo kết quả 1 việc (running/done/failed) về Hub (client gọi).</summary>
    public async Task ReportAssignmentAsync(string id, string status, string? error = null)
    {
        try { await _client.ReportAssignmentAsync(new AssignmentStatusRequest(id, _machineId, status, error)); } catch { }
    }

    public async Task CancelAssignmentAsync(string id)
    {
        try { await _client.CancelAssignmentAsync(new CancelAssignmentRequest(id)); } catch { }
    }

    /// <summary>Đọc TRẠNG THÁI ledger TƯƠI cho 1 key (round-trip thật, KHÔNG dùng snapshot poll 12s) — để
    /// worker kết luận done/failed chuẩn, tránh báo nhầm do snapshot trễ. null nếu lỗi/chưa có.</summary>
    public async Task<string?> FetchLedgerStatusAsync(string coordId)
    {
        try { return (await _client.AllLedgerAsync()).FirstOrDefault(l => l.Key == coordId)?.Status; }
        catch { return null; }
    }

    // ── Ledger → tiến độ local (để Resume bỏ qua dòng máy khác đã làm) ──
    public async Task SyncIntoProgressAsync()
    {
        try
        {
            foreach (var r in await _client.AllLedgerAsync())
            {
                if (string.IsNullOrEmpty(r.BigsellerId) || r.Completed.Count == 0) continue;
                foreach (var rr in r.Completed)
                    ScrapeProgressStore.Shared.MarkCompleted(r.BigsellerId, r.Sheet, rr.From, rr.To);
            }
        }
        catch { }
    }

    internal Task HeartbeatLeaseAsync(CoordKey key) => _client.HeartbeatLeaseAsync(key.Id, _machineId);
    internal Task ReleaseLeaseAsync(CoordKey key) => _client.ReleaseLeaseAsync(key.Id, _machineId);

    internal static string OpStr(CoordOp op) => op.ToString().ToLowerInvariant();
}

/// <summary>Handle khoá đang giữ: heartbeat nền mỗi 30s; Dispose = nhả khoá.</summary>
public sealed class LeaseHandle : ILeaseHandle
{
    private readonly HttpCoordinationHub _hub;
    private Timer? _timer;
    private int _disposed;

    public CoordKey Key { get; }
    public bool Held => Volatile.Read(ref _disposed) == 0;

    public LeaseHandle(HttpCoordinationHub hub, CoordKey key) { _hub = hub; Key = key; }

    public void StartHeartbeat() =>
        _timer = new Timer(_ => _ = Beat(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private async Task Beat() { try { await _hub.HeartbeatLeaseAsync(Key); } catch { } }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_timer is not null) await _timer.DisposeAsync();
        try { await _hub.ReleaseLeaseAsync(Key); } catch { }
    }
}
