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
    private readonly string _hostname;
    private readonly Timer _poller;
    private volatile FleetSnapshot _fleet = new();

    public bool Enabled => true;
    public event Action? Changed;

    public HttpCoordinationHub(HubClient client, string machineId, string hostname)
    {
        _client = client;
        _machineId = machineId;
        _hostname = hostname;
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
            await _client.MachineHeartbeatAsync(new MachineHeartbeatRequest(_machineId, _hostname, null));
            _fleet = await _client.FleetAsync();
            try { Changed?.Invoke(); } catch { }
        }
        catch { /* offline: giữ snapshot cũ, không ném */ }
    }

    public async Task<LeaseAttempt> AcquireAsync(CoordKey key, bool force, CancellationToken ct)
    {
        try
        {
            var req = new LeaseAcquireRequest(
                key.Id, key.BigsellerId, key.ShopId, key.Sheet, OpStr(key.Op), _machineId, _hostname, force);
            var resp = await _client.AcquireAsync(req, ct);
            if (!resp.Granted) return new LeaseAttempt(AcquireResult.Blocked(resp.BlockedByHostname), null);
            var handle = new LeaseHandle(this, key);
            handle.StartHeartbeat();
            return new LeaseAttempt(AcquireResult.Ok(), handle);
        }
        catch
        {
            return new LeaseAttempt(AcquireResult.Blocked("(mất kết nối hub)"), null);
        }
    }

    public void PublishProgress(CoordKey key, int from, int to) => _ = TryPublish(new WorkLedgerRecord
    {
        Key = key.Id, BigsellerId = key.BigsellerId, ShopId = key.ShopId, Sheet = key.Sheet, Op = OpStr(key.Op),
        Completed = [new RowRange { From = from, To = to }], LastRowReached = to,
        Status = "running", LastMachineId = _machineId, LastHostname = _hostname, LastRunAt = DateTimeOffset.Now,
    });

    public void PublishCompletion(CoordKey key, string status, int lastRow) => _ = TryPublish(new WorkLedgerRecord
    {
        Key = key.Id, BigsellerId = key.BigsellerId, ShopId = key.ShopId, Sheet = key.Sheet, Op = OpStr(key.Op),
        LastRowReached = lastRow, Status = status,
        LastMachineId = _machineId, LastHostname = _hostname, LastRunAt = DateTimeOffset.Now,
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
            var r = await _client.ReserveAccountsAsync(new AccountReserveRequest(list, _machineId, _hostname));
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
