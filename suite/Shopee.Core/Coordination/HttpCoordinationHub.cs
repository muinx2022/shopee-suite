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
    private int _pulling;        // 0/1 chống chồng lấn auto-pull (client)
    private int _pushing;        // 0/1 chống chồng lấn auto-push (hub)
    private DateTimeOffset _lastAutoPull = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAutoPush = DateTimeOffset.MinValue;
    private static readonly TimeSpan AutoSyncEvery = TimeSpan.FromMinutes(3);

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
            // CHỈ 1 lần, SAU khi Hub thật sự trả lời (tránh race lúc máy-Hub vừa khởi động: server localhost chưa
            // kịp lắng nghe). Poller 12s tự chạy ở tick thành công đầu tiên.
            // Fold ledger 1 LẦN (resume xuyên máy); auto-pull cấu hình/cookie/AI thì chạy ĐỊNH KỲ (xem dưới).
            if (Interlocked.Exchange(ref _foldedLedger, 1) == 0) _ = SyncIntoProgressAsync();
            _ = MaybeAutoPullAsync();   // client: kéo cấu hình/cookie/workbook MỚI từ Hub
            _ = MaybeAutoPushAsync();   // hub: publish cấu hình/cookie/workbook ĐÃ ĐỔI để client kéo
            try { Changed?.Invoke(); } catch { }
        }
        catch { /* offline: giữ snapshot cũ, không ném */ }
    }

    /// <summary>CLIENT (không phải máy Hub) tự kéo cấu hình + cookie + AI + workbook theo Hub: NGAY khi vừa kết
    /// nối (lần poll đầu) rồi ĐỊNH KỲ mỗi vài phút → Hub thêm tài khoản / đổi cookie SAU NÀY, client tự nhận mà
    /// KHÔNG cần khởi động lại. Máy Hub KHÔNG kéo (giữ bản gốc). Có chốt chống chồng lấn + giãn theo chu kỳ.</summary>
    private async Task MaybeAutoPullAsync()
    {
        if (HubServerConfigStore.Shared.Current.Enabled) return;              // máy Hub: giữ workbook/cookie gốc
        if (CoordinationRuntime.ConfigSync is not { } sync) return;
        if ((DateTimeOffset.UtcNow - _lastAutoPull) < AutoSyncEvery) return;  // chưa tới chu kỳ
        if (Interlocked.Exchange(ref _pulling, 1) == 1) return;               // đang kéo → bỏ lượt này
        try { await sync.PullAccountsAsync(); }
        catch { }
        finally { _lastAutoPull = DateTimeOffset.UtcNow; Interlocked.Exchange(ref _pulling, 0); }
    }

    /// <summary>MÁY HUB tự publish cấu hình + cookie + workbook ĐÃ ĐỔI lên Hub định kỳ (Hub là nguồn sự thật,
    /// client chỉ nhận) → "Hub đổi data → client tự có". PushAsync chỉ đẩy file hash KHÁC nên rất rẻ. Client
    /// KHÔNG push (scrape/update không ghi ngược data lên Hub). Có chốt chống chồng lấn + giãn theo chu kỳ.</summary>
    private async Task MaybeAutoPushAsync()
    {
        if (!HubServerConfigStore.Shared.Current.Enabled) return;            // chỉ máy Hub publish
        if (CoordinationRuntime.ConfigSync is not { } sync) return;
        if ((DateTimeOffset.UtcNow - _lastAutoPush) < AutoSyncEvery) return; // chưa tới chu kỳ
        if (Interlocked.Exchange(ref _pushing, 1) == 1) return;              // đang đẩy → bỏ lượt này
        try { await sync.PushAsync(); }
        catch { }
        finally { _lastAutoPush = DateTimeOffset.UtcNow; Interlocked.Exchange(ref _pushing, 0); }
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

    /// <summary>Hub ĐẶT TAY trạng thái sổ cho 1 việc (operator bấm trên bảng Giao việc): completed = ✓ xong;
    /// stopped = ■ dừng; idle = chưa chạy (xoá bản ghi → giao lại được). Ghi đè, không gộp.</summary>
    public async Task SetLedgerStatusAsync(CoordKey key, string status)
    {
        try
        {
            await _client.SetLedgerStatusAsync(new SetLedgerStatusRequest(
                key.Id, key.BigsellerId, key.ShopId, key.Sheet, OpStr(key.Op), status));
        }
        catch { }
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

    // ── Client báo acc Shopee lỗi/captcha về Hub; Hub đọc + operator quyết giữ/xóa ──
    public async Task ReportErroredAccountAsync(string accountId, string reason, string? captchaUrl, string status)
    {
        try { await _client.ReportErroredAccountAsync(new AccountErrorRequest(accountId, _machineId, Host, reason, captchaUrl, status)); } catch { }
    }
    public async Task<List<AccountError>> ErroredAccountsAsync()
    {
        try { return await _client.ErroredAccountsAsync(); } catch { return []; }
    }
    public async Task ClearErroredAccountAsync(string accountId)
    {
        try { await _client.ClearErroredAccountAsync(new ClearAccountErrorRequest(accountId)); } catch { }
    }

    /// <summary>Báo Hub xoá máy này khỏi danh sách (người dùng chủ động Ngắt kết nối). Lỗi → bỏ qua.</summary>
    public async Task LeaveAsync() { try { await _client.LeaveAsync(); } catch { } }

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
