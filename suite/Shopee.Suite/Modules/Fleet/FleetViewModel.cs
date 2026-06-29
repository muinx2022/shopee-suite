using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.UpdateProduct;

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
/// "Trạng thái & Giao việc" — 2 tab. Tab THEO DÕI: tổng hợp từ Hub (như cũ). Tab GIAO VIỆC: trên máy
/// HUB là bảng điều phối (gán vai trò máy + xếp/giao việc theo dây chuyền, ghim tay); trên CLIENT là
/// "việc của máy này" (tạm dừng nhận việc / chạy tay). Đọc <see cref="HttpCoordinationHub.CurrentFleet"/>,
/// làm mới khi hub poll (event Changed).
/// </summary>
public sealed partial class FleetViewModel : ObservableObject
{
    private readonly AssignmentWorker? _worker;

    // ── Tab "Theo dõi" (giữ nguyên) ──
    public ObservableCollection<FleetRow> Rows { get; } = [];
    [ObservableProperty] private string _status = "Máy này chưa bật đồng bộ Hub.";
    [ObservableProperty] private string _machines = "";

    // ── Tab "Giao việc" ──
    public bool IsHubBoard => HubServerConfigStore.Shared.Current.Enabled;
    public bool IsClientPanel => CoordinationRuntime.Active && !IsHubBoard;
    public bool IsCoordOff => !CoordinationRuntime.Active;

    // Hub board
    public ObservableCollection<FleetMachineRow> MachineRows { get; } = [];
    public ObservableCollection<FleetQueueRow> Queue { get; } = [];
    [ObservableProperty] private FleetQueueRow? _selectedQueue;
    public IReadOnlyList<string> PinOps { get; } = ["Scrape", "Import", "Update"];
    [ObservableProperty] private string _pinOp = "Scrape";
    [ObservableProperty] private FleetMachineRow? _pinMachine;

    public bool DispatchEnabled
    {
        get => HubDispatcher.Shared.Enabled;
        set { HubDispatcher.Shared.Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(DispatchButtonText)); }
    }
    public string DispatchButtonText => DispatchEnabled ? "■  Dừng điều phối" : "▶  Bật điều phối";

    public bool AutoMode
    {
        get => HubDispatcher.Shared.AutoMode;
        set { HubDispatcher.Shared.AutoMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ManualMode)); }
    }
    public bool ManualMode { get => !AutoMode; set => AutoMode = !value; }

    public bool AutoSyncHandoff
    {
        get => _worker?.AutoSyncHandoff ?? false;
        set { if (_worker is not null) _worker.AutoSyncHandoff = value; OnPropertyChanged(); }
    }

    // Client panel
    [ObservableProperty] private string _myRole = "—";
    public ObservableCollection<FleetMyJobRow> MyJobs { get; } = [];
    public bool PauseReceiving
    {
        get => _worker?.Paused ?? false;
        set { if (_worker is not null) _worker.Paused = value; OnPropertyChanged(); }
    }

    /// <summary>Bật → các lượt scrape/update tới chạy ĐÈ khoá máy khác (van thoát khi khoá sót).</summary>
    public bool ForceNextRun
    {
        get => CoordinationRuntime.ForceNextRun;
        set { CoordinationRuntime.ForceNextRun = value; OnPropertyChanged(); }
    }

    public FleetViewModel() : this(null) { }

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

    private void OnHubChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Refresh();
        else d.BeginInvoke(Refresh);
    }

    [RelayCommand]
    private void Refresh()
    {
        OnPropertyChanged(nameof(IsHubBoard));
        OnPropertyChanged(nameof(IsClientPanel));
        OnPropertyChanged(nameof(IsCoordOff));

        var hub = CoordinationRuntime.Hub;
        if (hub is null) { Status = "Máy này chưa bật đồng bộ Hub (Cài đặt → Đồng bộ nhiều máy)."; return; }

        var f = hub.CurrentFleet;
        BuildMonitor(f);
        if (IsHubBoard) { SyncMachines(f); BuildQueue(f); }
        if (IsClientPanel) BuildMyJobs(f, hub.MachineId);
    }

    // ── Tab Theo dõi ──────────────────────────────────────────────────────────
    private void BuildMonitor(FleetSnapshot f)
    {
        Rows.Clear();
        foreach (var l in f.Leases)
            Rows.Add(MakeRow(l.BigsellerId, l.ShopId, l.Op, l.Hostname, "⏳ đang chạy", l.HeartbeatAt, true));

        var leasedKeys = f.Leases.Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var g in f.Ledger.Where(g => !leasedKeys.Contains(g.Key) && g.Status is not ("idle" or "")))
            Rows.Add(MakeRow(g.BigsellerId, g.ShopId, g.Op, g.LastHostname, StateIcon(g.Status), g.UpdatedAt, false));

        if (HubServerConfigStore.Shared.Current.Enabled)
            Machines = f.Machines.Count == 0 ? "🖥 (chưa có máy nào kết nối)"
                : "Máy đang kết nối:   " + string.Join("        ", f.Machines.Select(m => $"🖥 {m.Hostname} · {Ago(m.LastSeen)}"));
        else
            Machines = "(danh sách máy đang kết nối chỉ hiển thị trên máy Hub)";
        Status = $"{f.Leases.Count} việc đang chạy · cập nhật {DateTimeOffset.Now:HH:mm:ss}";
    }

    // ── Tab Giao việc: bản HUB ─────────────────────────────────────────────────
    /// <summary>Đồng bộ strip vai trò máy TẠI CHỖ (không rebuild để khỏi đá combo người dùng đang chọn).</summary>
    private void SyncMachines(FleetSnapshot f)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in f.Machines)
        {
            seen.Add(m.MachineId);
            var roleKey = f.Roles.FirstOrDefault(r => r.MachineId == m.MachineId)?.Role ?? MachineRoles.Off;
            var (dot, online) = Presence(m.LastSeen);
            var existing = MachineRows.FirstOrDefault(x => x.MachineId == m.MachineId);
            if (existing is null)
                MachineRows.Add(new FleetMachineRow(m.MachineId, m.Hostname, online, dot, roleKey, OnRolePicked));
            else
                existing.UpdateLive(m.Hostname, online, dot, roleKey);
        }
        for (var i = MachineRows.Count - 1; i >= 0; i--)
            if (!seen.Contains(MachineRows[i].MachineId)) MachineRows.RemoveAt(i);
    }

    private static void OnRolePicked(string machineId, string roleKey)
        => _ = CoordinationRuntime.Hub?.SetRoleAsync(machineId, roleKey);

    private void BuildQueue(FleetSnapshot f)
    {
        (string bs, string shop)? prevSel = SelectedQueue is null ? null : (SelectedQueue.BigsellerId, SelectedQueue.ShopId);
        Queue.Clear();
        var running = 0; var queued = 0; var failed = 0;
        foreach (var acct in BigSellerStore.Shared.Accounts)
        foreach (var shop in acct.Shops)
        {
            if (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet)) continue;
            var sc = OpCell(f, acct.Id, shop.Id, "scrape");
            var im = OpCell(f, acct.Id, shop.Id, "import");
            var up = OpCell(f, acct.Id, shop.Id, "update");
            var next = HubDispatcher.NextOp(f, acct.Id, shop.Id);
            var row = new FleetQueueRow
            {
                BigsellerId = acct.Id, ShopId = shop.Id, Sheet = shop.ShopeeDataSheet,
                AccountLabel = AcctName(acct, acct.Id), ShopName = shop.DisplayName,
                ScrapeText = sc.text, ScrapeBrush = sc.brush,
                ImportText = im.text, ImportBrush = im.brush,
                UpdateText = up.text, UpdateBrush = up.brush,
                NextOpText = next is null ? "✓ hoàn tất" : $"kế tiếp: {OpVi(next)}",
            };
            Queue.Add(row);
            foreach (var c in new[] { sc, im, up }) { if (c.kind == 1) running++; else if (c.kind == 2) queued++; else if (c.kind == 3) failed++; }
        }
        if (prevSel is { } p) SelectedQueue = Queue.FirstOrDefault(r => r.BigsellerId == p.bs && r.ShopId == p.shop);
        Status = $"{running} đang chạy · {queued} chờ · {failed} dừng/lỗi · cập nhật {DateTimeOffset.Now:HH:mm:ss}";
    }

    /// <summary>Tính ô trạng thái 1 op của 1 shop. kind: 0 nghỉ/xong · 1 đang chạy · 2 đã xếp · 3 dừng/lỗi.</summary>
    private static (string text, Brush brush, int kind) OpCell(FleetSnapshot f, string bsId, string shopId, string op)
    {
        var key = $"{bsId}__{shopId}__{op}";
        var lease = f.Leases.FirstOrDefault(l => l.Key == key);
        if (lease is not null) return ($"⏳ {Host(lease.Hostname)}", RunningBrush, 1);

        var asn = f.Assignments.FirstOrDefault(a => a.BigsellerId == bsId && a.ShopId == shopId && a.Op == op && a.Status is "queued" or "running");
        if (asn is { Status: "running" }) return ($"⏳ {Host(asn.ClaimedByHostname)}", RunningBrush, 1);

        var led = f.Ledger.FirstOrDefault(l => l.Key == key);
        if (led?.Status == "completed") return ("✓ xong", DoneBrush, 0);
        if (led?.Status == "stopped") return ("■ dừng dở", WarnBrush, 3);
        if (asn is { Status: "queued" }) return ("• đã xếp", QueuedBrush, 2);
        return ("· chờ", IdleBrush, 0);
    }

    [RelayCommand]
    private void ToggleDispatch() => DispatchEnabled = !DispatchEnabled;

    /// <summary>Giao TAY: ghim (op, shop đang chọn) cho 1 máy cụ thể (đè vai trò mặc định).</summary>
    [RelayCommand]
    private async Task AssignManual()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null || SelectedQueue is not { } row || PinMachine is not { } m) return;
        var op = PinOp.ToLowerInvariant();
        await hub.CreateAssignmentAsync(new CreateAssignmentRequest(row.BigsellerId, row.ShopId, row.Sheet, op, m.MachineId, true));
        Refresh();
    }

    /// <summary>Huỷ mọi việc đang xếp/chạy của shop đang chọn.</summary>
    [RelayCommand]
    private async Task CancelSelected()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null || SelectedQueue is not { } row) return;
        foreach (var a in hub.CurrentFleet.Assignments.Where(a =>
                     a.BigsellerId == row.BigsellerId && a.ShopId == row.ShopId && a.Status is "queued" or "running"))
            await hub.CancelAssignmentAsync(a.Id);
        Refresh();
    }

    // ── Tab Giao việc: bản CLIENT ──────────────────────────────────────────────
    private void BuildMyJobs(FleetSnapshot f, string myId)
    {
        MyRole = RoleDisplay(f.Roles.FirstOrDefault(r => r.MachineId == myId)?.Role ?? MachineRoles.Off);
        MyJobs.Clear();
        foreach (var a in f.Assignments
                     .Where(a => (a.Status == "running" && a.ClaimedByMachineId == myId)
                                 || (a.Status == "queued" && a.TargetMachineId == myId))
                     .OrderByDescending(a => a.Status == "running"))
        {
            var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(x => x.Id == a.BigsellerId);
            var shop = acct?.Shops.FirstOrDefault(s => s.Id == a.ShopId);
            MyJobs.Add(new FleetMyJobRow
            {
                Id = a.Id, Op = OpVi(a.Op),
                AccountLabel = AcctName(acct, a.BigsellerId),
                ShopName = shop?.DisplayName ?? Short(a.ShopId),
                StateText = a.Status == "running" ? "▶ đang chạy" : "⏱ chờ tới lượt",
                StateBrush = a.Status == "running" ? RunningBrush : QueuedBrush,
            });
        }
    }

    // ── Tiện ích chung ──────────────────────────────────────────────────────────
    private static FleetRow MakeRow(string bsId, string shopId, string op, string host, string state, DateTimeOffset at, bool running)
    {
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(a => a.Id == bsId);
        var shop = acct?.Shops.FirstOrDefault(s => s.Id == shopId);
        return new FleetRow
        {
            MachineName = string.IsNullOrWhiteSpace(host) ? "?" : host,
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
        "scrape" => "Scrape", "import" => "Import", "update" => "Update", "rewrite" => "Tên SP", _ => op,
    };

    internal static string RoleDisplay(string key) => key switch
    {
        MachineRoles.Scrape => "Scrape", MachineRoles.Import => "Import",
        MachineRoles.Update => "Update", MachineRoles.All => "Mọi việc", _ => "Tắt",
    };
    internal static string RoleKey(string display) => display switch
    {
        "Scrape" => MachineRoles.Scrape, "Import" => MachineRoles.Import,
        "Update" => MachineRoles.Update, "Mọi việc" => MachineRoles.All, _ => MachineRoles.Off,
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
        "completed" => "✓ xong", "stopped" => "■ dừng dở", "running" => "⏳ đang chạy", _ => status,
    };

    private static string Host(string h) => string.IsNullOrWhiteSpace(h) ? "?" : h;

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
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
}

/// <summary>1 dòng máy trong strip vai trò: chọn vai trò → đẩy lên Hub qua callback.</summary>
public sealed partial class FleetMachineRow : ObservableObject
{
    private readonly Action<string, string> _onRole;
    public string MachineId { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _online;
    [ObservableProperty] private string _dot;

    public IReadOnlyList<string> RoleOptions { get; } = ["Tắt", "Scrape", "Import", "Update", "Mọi việc"];

    private string _role;
    private DateTimeOffset _pendingUntil;   // cửa sổ chờ server xác nhận sau khi user vừa đổi vai trò
    public string Role
    {
        get => _role;
        set
        {
            if (_role == value) return;
            _role = value;
            _pendingUntil = DateTimeOffset.Now.AddSeconds(15);   // > 1 nhịp poll (12s) → khỏi bị đá ngược
            OnPropertyChanged();
            _onRole(MachineId, FleetViewModel.RoleKey(value));
        }
    }

    public FleetMachineRow(string id, string name, string online, string dot, string roleKey, Action<string, string> onRole)
    {
        MachineId = id; _name = name; _online = online; _dot = dot;
        _role = FleetViewModel.RoleDisplay(roleKey); _onRole = onRole;
    }

    public void UpdateLive(string name, string online, string dot, string roleKey)
    {
        Name = name; Online = online; Dot = dot;
        var d = FleetViewModel.RoleDisplay(roleKey);
        if (d == _role) { _pendingUntil = default; return; }      // server đã khớp lựa chọn → hết chờ
        if (DateTimeOffset.Now < _pendingUntil) return;          // còn chờ xác nhận → đừng đá combo người dùng
        _role = d; OnPropertyChanged(nameof(Role));              // role do MÁY KHÁC đổi → cập nhật thật
    }

    public override string ToString() => Name;   // hiển thị trong combo "Giao tay cho máy"
}

/// <summary>1 dòng hàng đợi: 1 shop/account + trạng thái 3 op + gợi ý op kế tiếp.</summary>
public sealed class FleetQueueRow
{
    public string BigsellerId { get; init; } = "";
    public string ShopId { get; init; } = "";
    public string Sheet { get; init; } = "";
    public string AccountLabel { get; init; } = "";
    public string ShopName { get; init; } = "";
    public string ScrapeText { get; init; } = ""; public Brush ScrapeBrush { get; init; } = Brushes.Gray;
    public string ImportText { get; init; } = ""; public Brush ImportBrush { get; init; } = Brushes.Gray;
    public string UpdateText { get; init; } = ""; public Brush UpdateBrush { get; init; } = Brushes.Gray;
    public string NextOpText { get; init; } = "";
}

/// <summary>1 việc Hub giao cho máy này (bản client).</summary>
public sealed class FleetMyJobRow
{
    public string Id { get; init; } = "";
    public string Op { get; init; } = "";
    public string AccountLabel { get; init; } = "";
    public string ShopName { get; init; } = "";
    public string StateText { get; init; } = "";
    public Brush StateBrush { get; init; } = Brushes.Gray;
}
