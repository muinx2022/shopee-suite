using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Modules.Search;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.UpdateProduct;
using ShopeeStatApp.Models;
using ShopeeStatApp.Services;

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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignManualCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetStateCommand))]
    private FleetQueueRow? _selectedQueue;
    public IReadOnlyList<string> PinOps { get; } = ["Scrape", "Import", "Update"];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignManualCommand))]
    private string _pinOp = "Scrape";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignManualCommand))]
    private FleetMachineRow? _pinMachine;
    /// <summary>Khoảng dòng Hub đặt cho việc giao tay (0 = để client tự dùng cấu hình của nó).</summary>
    [ObservableProperty] private int _pinStartRow;
    [ObservableProperty] private int _pinEndRow;
    /// <summary>Phản hồi ngắn cho thao tác Ghim/Huỷ ("⏳ đang…", "✔ đã…", "✘ lỗi…") — hiện cạnh 2 nút.</summary>
    [ObservableProperty] private string _actionStatus = "";

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

    // ── Tab "Search (đa máy)" — bảng điều phối Search phía HUB ──
    /// <summary>Danh sách link đã nạp từ file (theo thứ tự file) — nguồn để CHIA ĐỀU cho các client được chọn.</summary>
    private readonly List<string> _searchLinks = [];

    public ObservableCollection<FleetSearchClientRow> SearchClients { get; } = [];
    [ObservableProperty] private string _searchFileDisplay = "(chưa chọn file link)";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private int _searchLinkCount;
    [ObservableProperty] private int _accountsPerClient = 5;
    [ObservableProperty] private int _searchLanes = 3;
    [ObservableProperty] private string _searchRegion = "Hà Nội";
    [ObservableProperty] private string _searchActionStatus = "";
    /// <summary>Số sản phẩm trong KHO GỘP trên Hub (các client đẩy kết quả về) — hiển thị cạnh nút Xuất.</summary>
    [ObservableProperty] private string _mergedProductText = "Kho gộp ở Hub: (chưa tải)";
    /// <summary>Lưới sản phẩm gộp (đọc từ Hub) — tự làm mới khi số SP đổi (kết quả sync về hiện lên liên tục).</summary>
    public ObservableCollection<SearchProductRow> MergedProducts { get; } = [];
    private int _lastMergedLoaded = -1;
    private bool _mergedRefreshing;

    /// <summary>Số máy đang được tick (sẽ được chia link).</summary>
    private int SelectedClientCount => SearchClients.Count(r => r.IsSelected);
    public string SearchSummary => SearchLinkCount == 0
        ? "Chưa nạp file link."
        : $"Tổng {SearchLinkCount} link · {SelectedClientCount}/{SearchClients.Count} máy được chọn · chia đều.";

    // Client panel
    [ObservableProperty] private string _myRole = "—";
    public ObservableCollection<FleetMyJobRow> MyJobs { get; } = [];
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
        if (IsHubBoard) { SyncMachines(f); BuildQueue(f); UpdateSearchRows(f); }
        if (IsClientPanel) BuildMyJobs(f, hub.MachineId);
        // Trạng thái assignment/lease đổi sau mỗi poll → tính lại enable/disable các nút.
        AssignManualCommand.NotifyCanExecuteChanged();
        CancelSelectedCommand.NotifyCanExecuteChanged();
        SetStateCommand.NotifyCanExecuteChanged();
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
        {
            var firstInAcct = true;   // gộp theo tk: chỉ dòng shop ĐẦU của mỗi tk mới hiện tên tài khoản
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
                    AccountLabel = firstInAcct ? AcctName(acct, acct.Id) : "",
                    IsAccountFirstRow = firstInAcct,
                    ShopName = shop.DisplayName,
                    ScrapeText = sc.text, ScrapeBrush = sc.brush,
                    ImportText = im.text, ImportBrush = im.brush,
                    UpdateText = up.text, UpdateBrush = up.brush,
                    NextOpText = next is null ? "✓ hoàn tất" : $"kế tiếp: {OpVi(next)}",
                };
                Queue.Add(row);
                firstInAcct = false;
                foreach (var c in new[] { sc, im, up }) { if (c.kind == 1) running++; else if (c.kind == 2) queued++; else if (c.kind == 3) failed++; }
            }
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
    [RelayCommand(CanExecute = nameof(CanAssignManual))]
    private async Task AssignManual()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null || SelectedQueue is not { } row || PinMachine is not { } m) return;
        var op = PinOp.ToLowerInvariant();
        ActionStatus = $"⏳ Đang ghim {PinOp} · {row.ShopName} → {m.Name}…";
        try
        {
            await hub.CreateAssignmentAsync(new CreateAssignmentRequest(
                row.BigsellerId, row.ShopId, row.Sheet, op, m.MachineId, true, Math.Max(0, PinStartRow), Math.Max(0, PinEndRow)));
            ActionStatus = $"✔ Đã ghim {PinOp} · {row.ShopName} → {m.Name}";
        }
        catch (Exception ex) { ActionStatus = "✘ Lỗi ghim việc: " + ex.Message; }
        Refresh();
    }

    /// <summary>Cho Ghim khi: có Hub + đã chọn shop + chọn máy, và (đúng op + shop + ĐÚNG MÁY đó) CHƯA có việc
    /// đang chạy/đã xếp. Đang thực hiện rồi → tắt nút để khỏi ghim trùng.</summary>
    private bool CanAssignManual()
    {
        if (CoordinationRuntime.Hub is not { } hub || SelectedQueue is not { } row || PinMachine is not { } m) return false;
        var op = PinOp.ToLowerInvariant();
        var alreadyForThisMachine = hub.CurrentFleet.Assignments.Any(a =>
            a.BigsellerId == row.BigsellerId && a.ShopId == row.ShopId && a.Op == op
            && a.Status is "queued" or "running"
            && (a.TargetMachineId == m.MachineId || a.ClaimedByMachineId == m.MachineId));
        return !alreadyForThisMachine;
    }

    /// <summary>Huỷ mọi việc đang xếp/chạy của shop đang chọn.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private async Task CancelSelected()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null || SelectedQueue is not { } row) return;
        var targets = hub.CurrentFleet.Assignments.Where(a =>
            a.BigsellerId == row.BigsellerId && a.ShopId == row.ShopId && a.Status is "queued" or "running").ToList();
        ActionStatus = $"⏳ Đang huỷ {targets.Count} việc · {row.ShopName}…";
        try
        {
            foreach (var a in targets) await hub.CancelAssignmentAsync(a.Id);
            ActionStatus = $"✔ Đã huỷ {targets.Count} việc · {row.ShopName}";
        }
        catch (Exception ex) { ActionStatus = "✘ Lỗi huỷ việc: " + ex.Message; }
        Refresh();
    }

    /// <summary>Cho Huỷ khi: có Hub + đã chọn shop, và shop đó CÓ ít nhất 1 việc đang chạy/đã xếp. Chưa chạy gì
    /// → tắt nút.</summary>
    private bool CanCancelSelected()
    {
        if (CoordinationRuntime.Hub is not { } hub || SelectedQueue is not { } row) return false;
        return hub.CurrentFleet.Assignments.Any(a =>
            a.BigsellerId == row.BigsellerId && a.ShopId == row.ShopId && a.Status is "queued" or "running");
    }

    /// <summary>Đặt TAY trạng thái 1 op của shop đang chọn (ghi ledger). arg = "op:status", vd "scrape:idle",
    /// "import:completed". status: completed = ✓ xong · idle = chưa chạy (reset → scrape giao + chạy lại từ
    /// đầu) · stopped = ■ dừng. Cho operator lập baseline + giao lại shop đã-xong.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedQueue))]
    private async Task SetState(string? arg)
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null || SelectedQueue is not { } row || string.IsNullOrWhiteSpace(arg)) return;
        var parts = arg.Split(':');
        if (parts.Length != 2) return;
        var op = parts[0].Trim().ToLowerInvariant();
        var status = parts[1].Trim().ToLowerInvariant();
        var coordOp = op switch { "import" => CoordOp.Import, "update" => CoordOp.Update, "rewrite" => CoordOp.Rewrite, _ => CoordOp.Scrape };
        var coordKey = new CoordKey(row.BigsellerId, row.ShopId, row.Sheet, coordOp);
        var stVi = status switch { "completed" => "✓ xong", "idle" => "chưa chạy", "stopped" => "■ dừng", _ => status };
        ActionStatus = $"⏳ Đặt {OpVi(op)} · {row.ShopName} = {stVi}…";
        try { await hub.SetLedgerStatusAsync(coordKey, status); ActionStatus = $"✔ {OpVi(op)} · {row.ShopName} = {stVi}"; }
        catch (Exception ex) { ActionStatus = "✘ Lỗi đặt trạng thái: " + ex.Message; }
        Refresh();
    }

    private bool HasSelectedQueue() => CoordinationRuntime.Hub is not null && SelectedQueue is not null;

    // ── Tab "Search (đa máy)": bản HUB ─────────────────────────────────────────
    private string _searchFileName = "";
    /// <summary>Bộ nạp link tái dùng (tránh mở SQLite mới mỗi lần chọn file). Chỉ dùng LoadFileLinks (đọc file).</summary>
    private SearchRunner? _linkParser;
    private SearchRunner LinkParser => _linkParser ??= new SearchRunner();

    /// <summary>Nạp file link (tái dùng parser của engine Search) → chia đều cho các máy đang tick.</summary>
    [RelayCommand]
    private void ChooseSearchFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Text (mỗi dòng 1 link)|*.txt|Excel|*.xlsx;*.xlsm|Tất cả|*.*",
            Title = "Chọn file link category để chia cho các máy",
        };
        if (dlg.ShowDialog() != true) return;
        _searchLinks.Clear();
        try
        {
            _searchLinks.AddRange(LinkParser.LoadFileLinks(dlg.FileName)
                .Select(x => x.Link).Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch (Exception ex) { SearchActionStatus = "✘ Lỗi nạp file: " + ex.Message; }
        _searchFileName = Path.GetFileName(dlg.FileName);
        SearchFileDisplay = _searchLinks.Count > 0
            ? $"{_searchFileName} — {_searchLinks.Count} link"
            : $"{_searchFileName} — (không có link hợp lệ)";
        SearchLinkCount = _searchLinks.Count;
        SearchActionStatus = "";
        if (CoordinationRuntime.Hub is { } h) UpdateSearchRows(h.CurrentFleet);
        RecomputePartition();   // chia đều cho các máy đang tick NGAY khi chọn file (poll không tự chia)
    }

    /// <summary>Giao đúng PHẦN LINK chia đều của 1 máy (ghim, op "search"). Máy đó tự chạy. Chạy lại = resume phần đó.</summary>
    [RelayCommand(CanExecute = nameof(CanRunSearch))]
    private async Task RunSearchForClient(FleetSearchClientRow? row)
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null || row is null || row.RangeCount <= 0) return;
        var start = row.RangeStart;
        var end = start + row.RangeCount;            // 0-based, [start, end)
        var links = _searchLinks.GetRange(start, row.RangeCount);
        var payload = new SearchJobPayload
        {
            Links = links, AccountsPerClient = Math.Max(1, AccountsPerClient),
            Lanes = Math.Max(1, SearchLanes), Region = SearchRegion, SourceFile = _searchFileName,
        };
        SearchActionStatus = $"⏳ Giao link {start + 1}–{end} → {row.Name}…";
        try
        {
            await hub.CreateAssignmentAsync(new CreateAssignmentRequest(
                "", $"{row.MachineId}:{start}", _searchFileName, MachineRoles.Search, row.MachineId, true,
                start + 1, end, JsonSerializer.Serialize(payload)));
            SearchActionStatus = $"✔ Đã giao link {start + 1}–{end} → {row.Name}";
        }
        catch (Exception ex) { SearchActionStatus = "✘ Lỗi giao việc Search: " + ex.Message; }
        Refresh();
    }

    /// <summary>Cho Chạy khi: có Hub + máy ĐƯỢC TICK + đang RẢNH + có phần link chia cho nó.</summary>
    private bool CanRunSearch(FleetSearchClientRow? row)
        => CoordinationRuntime.Hub is not null && row is not null && row.IsSelected && !row.Busy
           && _searchLinks.Count > 0 && row.RangeCount > 0;

    /// <summary>Đồng bộ dòng máy + trạng thái việc search, rồi CHIA ĐỀU link cho các máy đang tick.</summary>
    private void UpdateSearchRows(FleetSnapshot f)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in f.Machines)
        {
            seen.Add(m.MachineId);
            var row = SearchClients.FirstOrDefault(x => x.MachineId == m.MachineId);
            if (row is null) { row = new FleetSearchClientRow(m.MachineId, m.Hostname, RecomputePartition); SearchClients.Add(row); }
            row.Name = m.Hostname;

            var asn = f.Assignments.FirstOrDefault(a => a.Op == MachineRoles.Search && a.Status is "queued" or "running"
                && (a.ClaimedByMachineId == m.MachineId || a.TargetMachineId == m.MachineId));
            if (asn is { Status: "running" }) { row.Busy = true; row.StateText = $"▶ đang chạy (link {asn.StartRow}–{asn.EndRow})"; row.StateBrush = RunningBrush; }
            else if (asn is { Status: "queued" }) { row.Busy = true; row.StateText = $"⏱ chờ nhận (link {asn.StartRow}–{asn.EndRow})"; row.StateBrush = QueuedBrush; }
            else { row.Busy = false; row.StateText = "• rảnh"; row.StateBrush = IdleBrush; }
        }
        for (var i = SearchClients.Count - 1; i >= 0; i--)
            if (!seen.Contains(SearchClients[i].MachineId)) SearchClients.RemoveAt(i);

        // KHÔNG chia lại link theo mỗi nhịp poll — nếu 1 máy rớt/đổi giữa chừng, khoảng link của máy CHƯA giao
        // sẽ dịch ngầm → trùng/sót link. Chỉ chia lại khi người dùng CHỌN FILE hoặc TICK/BỎ TICK (callback).
        OnPropertyChanged(nameof(SearchSummary));
        RunSearchForClientCommand.NotifyCanExecuteChanged();
        _ = RefreshMergedAsync();   // cập nhật số SP kho gộp + lưới (best-effort)
    }

    /// <summary>Chia ĐỀU tổng số link cho các máy ĐANG TICK (theo thứ tự hiển thị): mỗi máy nhận
    /// base hoặc base+1 link (các máy đầu nhận phần dư). Máy bỏ tick / không còn link → "(không dùng)".</summary>
    private void RecomputePartition()
    {
        var selected = SearchClients.Where(r => r.IsSelected).ToList();
        var k = selected.Count;
        var n = _searchLinks.Count;
        var baseShare = k == 0 ? 0 : n / k;
        var remainder = k == 0 ? 0 : n % k;

        var cursor = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            var share = baseShare + (i < remainder ? 1 : 0);
            var r = selected[i];
            r.RangeStart = cursor;
            r.RangeCount = share;
            r.RangeLabel = share > 0 ? $"link {cursor + 1}–{cursor + share}" : "(không có link)";
            cursor += share;
        }
        foreach (var r in SearchClients.Where(x => !x.IsSelected))
        {
            r.RangeStart = 0; r.RangeCount = 0; r.RangeLabel = "(không dùng)";
        }

        OnPropertyChanged(nameof(SearchSummary));
        RunSearchForClientCommand.NotifyCanExecuteChanged();
    }

    // ── Kho gộp kết quả Search trên Hub (client đẩy về → Hub gộp) ──────────────
    /// <summary>Cập nhật số SP kho gộp; nếu số ĐỔI (hoặc force) thì tải lại LƯỚI sản phẩm (deserialize ở luồng nền).
    /// Chống chồng lấn; best-effort (offline → giữ nguyên). Gọi mỗi nhịp poll → kết quả sync về hiện lên liên tục.</summary>
    private async Task RefreshMergedAsync(bool force = false)
    {
        var client = CoordinationRuntime.Client;
        if (client is null || _mergedRefreshing) return;
        _mergedRefreshing = true;
        try
        {
            var n = await client.SearchProductCountAsync();
            MergedProductText = $"Kho gộp ở Hub: {n} sản phẩm";
            if (!force && n == _lastMergedLoaded) return;   // số chưa đổi → khỏi tải lại lưới
            var jsons = await client.SearchProductsAsync();
            var rows = await Task.Run(() =>
            {
                var list = new List<SearchProductRow>(jsons.Count);
                foreach (var j in jsons)
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<ProductResult>(j) is { } p)
                            list.Add(new SearchProductRow(0, p.Name, p.PriceVnd, p.MonthlySold, p.Rating,
                                p.Category, p.ShopLocation, p.ShopName, p.Link, p.ShopId));
                    }
                    catch { }
                }
                return list;
            });
            MergedProducts.Clear();
            foreach (var r in rows) MergedProducts.Add(r);
            _lastMergedLoaded = n;
        }
        catch { }
        finally { _mergedRefreshing = false; }
    }

    /// <summary>Làm mới lưới kho gộp theo yêu cầu (nút ⟳).</summary>
    [RelayCommand]
    private Task RefreshMerged() => RefreshMergedAsync(force: true);

    /// <summary>Tải toàn bộ kho gộp từ Hub → dedup theo ItemId → xuất 1 file Excel gộp (mở thư mục sau khi xong).</summary>
    [RelayCommand]
    private async Task ExportMerged()
    {
        var client = CoordinationRuntime.Client;
        if (client is null) { SearchActionStatus = "Chưa kết nối Hub."; return; }
        SearchActionStatus = "⏳ Đang tải kho gộp từ Hub…";
        try
        {
            var jsons = await client.SearchProductsAsync();
            var products = new List<ProductResult>();
            foreach (var j in jsons)
            {
                try { if (JsonSerializer.Deserialize<ProductResult>(j) is { } p) products.Add(p); } catch { }
            }
            if (products.Count == 0) { SearchActionStatus = "Kho gộp trống — chưa có sản phẩm nào."; return; }
            var deduped = products.GroupBy(p => p.ItemId).Select(g => g.First()).ToList();

            // Hộp thoại bộ lọc (giá / đã bán / danh mục) — cùng bộ lọc tab Search, để lọc trước khi xuất.
            var cats = deduped.Select(p => p.Category).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
            var dlg = new SearchExportFilterWindow(cats) { Owner = Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true) { SearchActionStatus = "Đã hủy xuất."; return; }

            var filtered = SearchRunner.ApplyFilter(deduped, dlg.Filter);
            if (filtered.Count == 0) { SearchActionStatus = "Không có sản phẩm nào khớp bộ lọc."; return; }
            var dir = Path.Combine(SuitePaths.ModuleDir("search"), "hub-merged");
            var path = ExcelExporter.Export(filtered, dir, $"tonghop-hub-{filtered.Count}sp");
            SearchActionStatus = $"✔ Đã xuất {filtered.Count}/{deduped.Count} sản phẩm → {path}";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex) { SearchActionStatus = "✘ Lỗi xuất kho gộp: " + ex.Message; }
        finally { _ = RefreshMergedAsync(force: true); }
    }

    /// <summary>Xóa toàn bộ kho gộp trên Hub (làm sạch trước 1 mẻ chạy mới).</summary>
    [RelayCommand]
    private async Task ClearMerged()
    {
        var client = CoordinationRuntime.Client;
        if (client is null) return;
        if (Dialogs.Show("Xóa toàn bộ kho gộp kết quả Search trên Hub? Không thể hoàn tác.",
                "Xóa kho gộp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await client.ClearSearchProductsAsync(); SearchActionStatus = "✔ Đã xóa kho gộp."; }
        catch (Exception ex) { SearchActionStatus = "✘ Lỗi xóa kho gộp: " + ex.Message; }
        _ = RefreshMergedAsync(force: true);
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
            // Search: không gắn với BigSeller shop → hiện tên file + khoảng link thay cho tài khoản/shop.
            if (a.Op == MachineRoles.Search)
            {
                MyJobs.Add(new FleetMyJobRow
                {
                    Id = a.Id, Op = OpVi(a.Op),
                    AccountLabel = "Search",
                    ShopName = string.IsNullOrWhiteSpace(a.Sheet) ? "(file Hub giao)" : a.Sheet,
                    Rows = RowRange(a.StartRow, a.EndRow),
                    StateText = a.Status == "running" ? "▶ đang chạy" : "⏱ chờ tới lượt",
                    StateBrush = a.Status == "running" ? RunningBrush : QueuedBrush,
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
        "scrape" => "Scrape", "import" => "Import", "update" => "Update", "rewrite" => "Tên SP",
        "search" => "Search", _ => op,
    };

    /// <summary>Hiển thị khoảng dòng Hub giao: "X→Y" (Y=0 ⇒ "hết"); 0/0 ⇒ "theo client" (Hub không đặt).</summary>
    private static string RowRange(int start, int end) =>
        start <= 0 && end <= 0 ? "theo client" : $"{(start > 0 ? start : 2)}→{(end > 0 ? end.ToString() : "hết")}";

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
    /// <summary>true nếu là dòng shop ĐẦU của một tài khoản → kẻ vạch phân nhóm + in đậm tên tk.</summary>
    public bool IsAccountFirstRow { get; init; }
    public string ShopName { get; init; } = "";
    public string ScrapeText { get; init; } = ""; public Brush ScrapeBrush { get; init; } = Brushes.Gray;
    public string ImportText { get; init; } = ""; public Brush ImportBrush { get; init; } = Brushes.Gray;
    public string UpdateText { get; init; } = ""; public Brush UpdateBrush { get; init; } = Brushes.Gray;
    public string NextOpText { get; init; } = "";
}

/// <summary>1 dòng máy trên bảng điều phối Search: tick chọn · tên máy · trạng thái · phần link chia · nút Chạy.</summary>
public sealed partial class FleetSearchClientRow : ObservableObject
{
    private readonly Action _onSelectedChanged;
    public string MachineId { get; }
    [ObservableProperty] private string _name;
    /// <summary>Tick = dùng máy này (mặc định). Bỏ tick → loại khỏi việc chia link (tính lại phần các máy khác).</summary>
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private Brush _stateBrush = Brushes.Gray;
    /// <summary>Nhãn phần link chia cho máy này, vd "link 1–6" (hoặc "(không dùng)" khi bỏ tick).</summary>
    [ObservableProperty] private string _rangeLabel = "";
    /// <summary>Máy đang có việc Search (queued/running) → tắt nút Chạy để khỏi giao chồng.</summary>
    [ObservableProperty] private bool _busy;

    /// <summary>Phần link chia cho máy (0-based start + số lượng) — nguồn để giao đúng slice khi bấm Chạy.</summary>
    public int RangeStart { get; set; }
    public int RangeCount { get; set; }

    public FleetSearchClientRow(string id, string name, Action onSelectedChanged)
    { MachineId = id; _name = name; _onSelectedChanged = onSelectedChanged; }

    partial void OnIsSelectedChanged(bool value) => _onSelectedChanged();
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
