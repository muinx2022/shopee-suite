using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Fleet;

/// <summary>1 dòng trên bảng THEO DÕI: máy nào đang/đã làm gì với shop nào.</summary>
public sealed class FleetRow
{
    public string MachineName { get; init; } = "";
    public string AccountLabel { get; init; } = "";
    public string ShopName { get; init; } = "";
    public string Op { get; init; } = "";
    public string State { get; init; } = "";
    public string Updated { get; init; } = "";
    public bool Running { get; init; }
}

/// <summary>
/// "Trạng thái & Giao việc". Tab THEO DÕI: tổng hợp từ Hub. Tab GIAO VIỆC (CLIENT): "việc của máy này"
/// (tạm dừng nhận việc / xem việc gián đoạn để chạy tiếp). Đọc <see cref="HttpCoordinationHub.CurrentFleet"/>,
/// làm mới khi hub poll (event Changed).
/// </summary>
public sealed partial class FleetViewModel : ObservableObject
{
    private readonly AssignmentWorker? _worker;

    // ── Tab "Theo dõi" ──
    public ObservableCollection<FleetRow> Rows { get; } = [];
    [ObservableProperty] private string _status = "Máy này chưa bật đồng bộ Hub.";
    [ObservableProperty] private string _machines = "";

    // ── Tab "Giao việc" (bản CLIENT) ──
    public bool IsClientPanel => CoordinationRuntime.Active;
    public bool IsCoordOff => !CoordinationRuntime.Active;

    // Client panel
    [ObservableProperty] private string _myRole = "—";
    public ObservableCollection<FleetMyJobRow> MyJobs { get; } = [];

    // ── Khối "Việc gián đoạn" (failed/canceled, chưa xong) — hub tính sẵn (FleetSnapshot.Interrupted); client
    //    bấm ▶ Tiếp tục để đưa lại hàng chờ hub (chạy tiếp thay vì mất việc). ──
    public ObservableCollection<FleetInterruptedRow> InterruptedJobs { get; } = [];
    /// <summary>Hiện cả khối khi CÓ việc gián đoạn (rỗng → ẩn).</summary>
    [ObservableProperty] private bool _hasInterrupted;
    public string InterruptedHeader => $"⏯  Việc gián đoạn ({InterruptedJobs.Count})";
    /// <summary>Phản hồi ngắn cho thao tác Tiếp tục (message lỗi hub trả về hiện ở đây).</summary>
    [ObservableProperty] private string _interruptedStatus = "";

    // ── Tab "Log" — nhật ký nhiều máy gửi về Hub (xem tập trung, mới nhất ở trên) ──
    public ObservableCollection<FleetLogRow> Logs { get; } = [];
    private long _lastLogId;
    private bool _logsRefreshing;
    public bool PauseReceiving
    {
        get => _worker?.Paused ?? false;
        set { if (_worker is not null) _worker.Paused = value; OnPropertyChanged(); OnPropertyChanged(nameof(ReceiveToggleText)); OnPropertyChanged(nameof(ReceiveStateText)); }
    }

    /// <summary>Nhãn nút bật/tắt nhận việc (mặc định ĐANG nhận → nút để tạm dừng).</summary>
    public string ReceiveToggleText => PauseReceiving ? "▶  Bắt đầu nhận việc" : "⏸  Tạm dừng nhận việc";
    public string ReceiveStateText => PauseReceiving
        ? "⏸ Đã tạm dừng — máy này KHÔNG nhận việc mới (việc đang chạy vẫn xong)."
        : "🟢 Đang nhận việc — máy tự chạy khi Hub giao việc cho nó (không cần bấm gì thêm).";

    /// <summary>Bật/tắt nhận việc. Client TỰ chạy khi đang nhận — đây là công tắc duy nhất cần biết.</summary>
    [RelayCommand] private void ToggleReceive() => PauseReceiving = !PauseReceiving;

    /// <summary>Bật → các lượt scrape/update tới chạy ĐÈ khoá máy khác (van thoát khi khoá sót).</summary>
    public bool ForceNextRun
    {
        get => CoordinationRuntime.ForceNextRun;
        set { CoordinationRuntime.ForceNextRun = value; OnPropertyChanged(); }
    }

    public FleetViewModel(AssignmentWorker? worker)
    {
        _worker = worker;
        var hub = CoordinationRuntime.Hub;
        if (hub is not null)
        {
            hub.Changed += OnHubChanged;
            Refresh();
        }
    }

    private void OnHubChanged() => UiThread.Post(Refresh);

    [RelayCommand]
    private void Refresh()
    {
        OnPropertyChanged(nameof(IsClientPanel));
        OnPropertyChanged(nameof(IsCoordOff));

        var hub = CoordinationRuntime.Hub;
        if (hub is null) { Status = "Máy này chưa bật đồng bộ Hub (Cài đặt → Đồng bộ nhiều máy)."; return; }

        var f = hub.CurrentFleet;
        BuildMonitor(f);
        BuildInterrupted(f);   // khối "Việc gián đoạn" (hub tính sẵn) — hiện dưới việc của máy này
        if (IsClientPanel) BuildMyJobs(f, hub.MachineId);
        _ = RefreshLogsAsync();   // kéo log mới từ Hub (tab Log)
    }

    // ── Tab Theo dõi ──────────────────────────────────────────────────────────
    private void BuildMonitor(FleetSnapshot f)
    {
        // Gom tất cả dòng KÈM mốc thời gian rồi xếp MỚI NHẤT LÊN ĐẦU (việc/nhịp gần đây nhất ở trên; việc cũ
        // trôi xuống cuối). Lease đang chạy có heartbeat ~bây giờ nên tự nằm trên cùng.
        var rows = new List<(FleetRow Row, DateTimeOffset At)>();
        foreach (var l in f.Leases)
            rows.Add((MakeRow(l.BigsellerId, l.ShopId, l.Op, l.Hostname, "⏳ đang chạy", l.HeartbeatAt, true), l.HeartbeatAt));

        var leasedKeys = f.Leases.Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var g in f.Ledger.Where(g => !leasedKeys.Contains(g.Key) && g.Status is not (LedgerStatus.Idle or "")))
            rows.Add((MakeRow(g.BigsellerId, g.ShopId, g.Op, g.LastHostname, StateIcon(g.Status), g.UpdatedAt, false), g.UpdatedAt));

        Rows.Clear();
        foreach (var r in rows.OrderByDescending(x => x.At))
            Rows.Add(r.Row);

        if (HubServerConfigStore.Shared.Current.Enabled)
            // Chấm trạng thái theo Presence: 🟢 online (≤45s) · 🟡 vừa mất nhịp (≤3') · ⚪ offline — thay icon 🖥 chung.
            Machines = f.Machines.Count == 0 ? "⚪ (chưa có máy nào kết nối)"
                : "Máy đang kết nối:   " + string.Join("        ", f.Machines.Select(m => $"{Presence(m.LastSeen).dot} {m.Hostname} · {Ago(m.LastSeen)}"));
        else
            Machines = "(danh sách máy đang kết nối chỉ hiển thị trên máy Hub)";
        Status = $"{f.Leases.Count} việc đang chạy · cập nhật {DateTimeOffset.Now:HH:mm:ss}";
    }

    // ── Tab Giao việc: bản CLIENT ──────────────────────────────────────────────
    private void BuildMyJobs(FleetSnapshot f, string myId)
    {
        MyRole = RoleDisplay(f.Roles.FirstOrDefault(r => r.MachineId == myId)?.Role ?? MachineRoles.Off);
        MyJobs.Clear();
        foreach (var a in f.Assignments
                     .Where(a => (a.Status == AssignmentStatus.Running && a.ClaimedByMachineId == myId)
                                 || (a.Status == AssignmentStatus.Queued && a.TargetMachineId == myId))
                     .OrderByDescending(a => a.Status == AssignmentStatus.Running))
        {
            // Search: không gắn với BigSeller shop → hiện tên file + khoảng link thay cho tài khoản/shop.
            if (a.Op == AssignmentOps.Search)
            {
                MyJobs.Add(new FleetMyJobRow
                {
                    Id = a.Id, Op = OpVi(a.Op),
                    AccountLabel = "Search",
                    ShopName = string.IsNullOrWhiteSpace(a.Sheet) ? "(file Hub giao)" : a.Sheet,
                    Rows = RowRange(a.StartRow, a.EndRow),
                    StateText = a.Status == AssignmentStatus.Running ? "▶ đang chạy" : "⏱ chờ tới lượt",
                    StateBrush = a.Status == AssignmentStatus.Running ? RunningBrush : QueuedBrush,
                });
                continue;
            }
            var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(x => x.Id == a.BigsellerId);
            var shop = acct?.Shops.FirstOrDefault(s => s.Id == a.ShopId);
            MyJobs.Add(new FleetMyJobRow
            {
                Id = a.Id, Op = OpVi(a.Op),
                AccountLabel = AcctName(acct, a.BigsellerId),
                ShopName = shop?.DisplayName ?? Short(a.ShopId),
                Rows = RowRange(a.StartRow, a.EndRow),
                StateText = a.Status == AssignmentStatus.Running ? "▶ đang chạy" : "⏱ chờ tới lượt",
                StateBrush = a.Status == AssignmentStatus.Running ? RunningBrush : QueuedBrush,
            });
        }
    }

    // ── Khối "Việc gián đoạn" — dựng từ FleetSnapshot.Interrupted (hub tính sẵn) ──
    private void BuildInterrupted(FleetSnapshot f)
    {
        InterruptedJobs.Clear();
        foreach (var a in f.Interrupted)
            InterruptedJobs.Add(new FleetInterruptedRow { Id = a.Id, Title = DescribeAssignment(a), Detail = InterruptedDetail(a) });
        HasInterrupted = InterruptedJobs.Count > 0;
        OnPropertyChanged(nameof(InterruptedHeader));
    }

    /// <summary>Mô tả 1 việc: "Import · Shop (Tài khoản)" / "Search · file" (giống AssignmentWorker.Describe —
    /// viết riêng vì method của worker là private static).</summary>
    private static string DescribeAssignment(Assignment a)
    {
        if (a.Op == AssignmentOps.Search)
            return $"Search · {(string.IsNullOrWhiteSpace(a.Sheet) ? "(file Hub giao)" : a.Sheet)}";
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(x => x.Id == a.BigsellerId);
        var shop = acct?.Shops.FirstOrDefault(s => s.Id == a.ShopId);
        return $"{OpVi(a.Op)} · {shop?.DisplayName ?? Short(a.ShopId)} ({AcctName(acct, a.BigsellerId)})";
    }

    /// <summary>Dòng phụ: máy giữ cuối · lý do (LastError) · thời điểm (giờ local).</summary>
    private static string InterruptedDetail(Assignment a)
    {
        var host = string.IsNullOrWhiteSpace(a.ClaimedByHostname) ? "—" : a.ClaimedByHostname;
        var reason = string.IsNullOrWhiteSpace(a.LastError) ? (a.Status == AssignmentStatus.Canceled ? "đã huỷ" : "lỗi") : a.LastError;
        return $"Máy: {host}   ·   {reason}   ·   {a.UpdatedAt.ToLocalTime():HH:mm:ss}";
    }

    /// <summary>▶ Tiếp tục 1 việc gián đoạn → hub đưa về hàng chờ. Message lỗi hub trả về (non-null) hiện ra
    /// <see cref="InterruptedStatus"/>; OK → gỡ optimistic khỏi list ngay (nhịp poll kế cũng tự cập nhật).</summary>
    [RelayCommand]
    private async Task ResumeJob(FleetInterruptedRow? row)
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null || row is null) return;
        InterruptedStatus = $"⏳ Đang tiếp tục: {row.Title}…";
        var err = await hub.ResumeAssignmentAsync(row.Id);
        if (err is null)
        {
            InterruptedStatus = $"✔ Đã đưa lại hàng chờ: {row.Title}";
            InterruptedJobs.Remove(row);
            HasInterrupted = InterruptedJobs.Count > 0;
            OnPropertyChanged(nameof(InterruptedHeader));
        }
        else InterruptedStatus = $"✘ {row.Title}: {err}";
    }

    /// <summary>▶ Tiếp tục TẤT CẢ việc gián đoạn (lặp từng việc); gom lỗi cuối (nếu có) ra status.</summary>
    [RelayCommand]
    private async Task ResumeAllJobs()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;
        var rows = InterruptedJobs.ToList();
        if (rows.Count == 0) return;
        InterruptedStatus = $"⏳ Đang tiếp tục {rows.Count} việc…";
        var ok = 0; string? lastErr = null;
        foreach (var row in rows)
        {
            var err = await hub.ResumeAssignmentAsync(row.Id);
            if (err is null) { ok++; InterruptedJobs.Remove(row); }
            else lastErr = err;
        }
        HasInterrupted = InterruptedJobs.Count > 0;
        OnPropertyChanged(nameof(InterruptedHeader));
        InterruptedStatus = lastErr is null
            ? $"✔ Đã đưa lại hàng chờ {ok} việc."
            : $"⚠ Tiếp tục {ok}/{rows.Count} việc — lỗi cuối: {lastErr}";
    }

    // ── Tab "Log" — kéo log tập trung từ Hub theo mỗi nhịp poll ─────────────────
    private async Task RefreshLogsAsync()
    {
        var client = CoordinationRuntime.Client;
        if (client is null || _logsRefreshing) return;
        _logsRefreshing = true;
        try
        {
            var entries = await client.LogsAsync(_lastLogId, 300);
            if (entries.Count == 0) return;
            void Apply()
            {
                foreach (var e in entries)   // tăng dần → Insert(0) đưa MỚI NHẤT lên đầu
                {
                    Logs.Insert(0, MakeLogRow(e));
                    if (e.Id > _lastLogId) _lastLogId = e.Id;
                }
                while (Logs.Count > 1000) Logs.RemoveAt(Logs.Count - 1);   // giữ 1000 dòng gần nhất
            }
            // Đợi Apply chạy XONG trên UI thread rồi mới nhả guard — _lastLogId cập nhật trong Apply;
            // nhả sớm sẽ cho lượt poll sau đọc trùng log cũ → dòng lặp đôi.
            await UiThread.InvokeAsync(Apply);
        }
        catch { }
        finally { _logsRefreshing = false; }
    }

    private static FleetLogRow MakeLogRow(LogEntry e) => new()
    {
        Time = e.Ts.ToLocalTime().ToString("HH:mm:ss"),
        Machine = e.Hostname,
        Text = e.Text,
        Brush = e.Level switch { "ok" => DoneBrush, "warn" => WarnBrush, "error" => FailBrush, _ => IdleBrush },
    };

    /// <summary>Xoá toàn bộ log trên Hub + lưới (làm sạch trước 1 mẻ theo dõi mới).</summary>
    [RelayCommand]
    private async Task ClearLogs()
    {
        var client = CoordinationRuntime.Client;
        if (client is null) return;
        try { await client.ClearLogsAsync(); } catch { }
        Logs.Clear();
        _lastLogId = 0;
    }

    // ── Tiện ích chung ──────────────────────────────────────────────────────────
    private static FleetRow MakeRow(string bsId, string shopId, string op, string host, string state, DateTimeOffset at, bool running)
    {
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(a => a.Id == bsId);
        var shop = acct?.Shops.FirstOrDefault(s => s.Id == shopId);
        return new FleetRow
        {
            // Ledger đặt tay ("✓ Xong" Bước 3) / set completed KHÔNG do máy nào chạy → last_hostname rỗng.
            // Hiện "—" (không rõ máy) thay vì "?" cho đỡ trông như lỗi.
            MachineName = string.IsNullOrWhiteSpace(host) ? "—" : host,
            AccountLabel = AcctName(acct, bsId),
            ShopName = shop?.Name is { Length: > 0 } n ? n : (shop?.ShopeeDataSheet is { Length: > 0 } sh ? sh : Short(shopId)),
            Op = OpVi(op), State = state, Updated = Ago(at), Running = running,
        };
    }

    private static string AcctName(BigSellerAccount? a, string id) =>
        a is null ? Short(id)
        : !string.IsNullOrWhiteSpace(a.Label) ? a.Label
        : !string.IsNullOrWhiteSpace(a.Email) ? a.Email
        : Short(id);

    private static string OpVi(string op) => op switch
    {
        AssignmentOps.Scrape => "Scrape", AssignmentOps.Import => "Import", AssignmentOps.Update => "Update",
        AssignmentOps.Rewrite => "Tên SP", AssignmentOps.Search => "Search", _ => op,
    };

    /// <summary>Hiển thị khoảng dòng Hub giao: "X→Y" (Y=0 ⇒ "hết"); 0/0 ⇒ "theo client" (Hub không đặt).</summary>
    private static string RowRange(int start, int end) =>
        start <= 0 && end <= 0 ? "theo client" : $"{(start > 0 ? start : 2)}→{(end > 0 ? end.ToString() : "hết")}";

    internal static string RoleDisplay(string key) => key switch
    {
        MachineRoles.Scrape => "Scrape", MachineRoles.Import => "Import",
        MachineRoles.Update => "Update", MachineRoles.All => "Mọi việc", _ => "Tắt",
    };

    private static (string dot, string online) Presence(DateTimeOffset at)
    {
        var s = (DateTimeOffset.Now - at).TotalSeconds;
        if (s < 45) return ("🟢", "online · " + Ago(at));
        if (s < 180) return ("🟡", Ago(at));
        return ("⚪", "offline · " + Ago(at));
    }

    private static string StateIcon(string status) => status switch
    {
        LedgerStatus.Completed => "✓ xong", LedgerStatus.Stopped => "■ dừng dở",
        LedgerStatus.Running => "⏳ đang chạy", _ => status,
    };

    private static string Ago(DateTimeOffset at)
    {
        if (at == default || at == DateTimeOffset.MinValue) return "";
        var s = (DateTimeOffset.Now - at).TotalSeconds;
        if (s < 0) s = 0;
        return s < 60 ? $"{(int)s}s trước" : s < 3600 ? $"{(int)(s / 60)} phút trước" : $"{(int)(s / 3600)} giờ trước";
    }

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "?" : id[..Math.Min(8, id.Length)];

    private static readonly Brush RunningBrush = Frozen(0x1E, 0xA0, 0x55);
    private static readonly Brush DoneBrush = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush WarnBrush = Frozen(0xC8, 0x6A, 0x00);
    private static readonly Brush QueuedBrush = Frozen(0x00, 0x78, 0xD7);
    private static readonly Brush IdleBrush = Frozen(0x6E, 0x72, 0x7A);
    private static readonly Brush FailBrush = Frozen(0xD1, 0x34, 0x38);
    // Brush cho dòng lưới dựng từ luồng nền → PHẢI Freeze (AppBrushes lo parse + Freeze + cache).
    private static Brush Frozen(byte r, byte g, byte b) => AppBrushes.From(r, g, b);
}

/// <summary>1 dòng log tập trung (nhiều máy gửi về Hub) hiển thị ở tab Log.</summary>
public sealed class FleetLogRow
{
    public string Time { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Text { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.Gray;
}

/// <summary>1 việc Hub giao cho máy này (bản client).</summary>
public sealed class FleetMyJobRow
{
    public string Id { get; init; } = "";
    public string Op { get; init; } = "";
    public string AccountLabel { get; init; } = "";
    public string ShopName { get; init; } = "";
    /// <summary>Khoảng dòng Hub giao cho việc này ("X→Y" hoặc "theo client").</summary>
    public string Rows { get; init; } = "";
    public string StateText { get; init; } = "";
    public Brush StateBrush { get; init; } = Brushes.Gray;
}

/// <summary>1 việc GIÁN ĐOẠN (failed/canceled, chưa xong) ở khối "Việc gián đoạn" — bấm ▶ Tiếp tục để đưa lại
/// hàng chờ hub. Id để gọi ResumeAssignmentAsync; Title/Detail chỉ để hiển thị.</summary>
public sealed class FleetInterruptedRow
{
    public string Id { get; init; } = "";
    /// <summary>Mô tả việc: "Import · Shop (Tài khoản)" / "Search · file".</summary>
    public string Title { get; init; } = "";
    /// <summary>Máy giữ cuối · lý do · thời điểm (giờ local).</summary>
    public string Detail { get; init; } = "";
}
