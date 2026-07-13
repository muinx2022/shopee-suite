using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Workspace;

/// <summary>
/// Thẻ THỐNG KÊ 1 op (scrape/import/update/tên SP) của 1 shop — cùng dữ liệu tab "Thống kê" bên Hub, đọc từ
/// sổ hoàn thành (ledger) + lease + assignment trong <see cref="FleetSnapshot"/>. Update-in-place (không tạo
/// mới mỗi nhịp) nên cột đứng yên, không giật scroll.
/// </summary>
public sealed partial class WorkspaceOpStat : ObservableObject
{
    public string OpLabel { get; }
    public WorkspaceOpStat(string opLabel) => OpLabel = opLabel;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private IBrush _statusBg = Brushes.Transparent;
    [ObservableProperty] private IBrush _statusFg = Brushes.Black;
    [ObservableProperty] private string _rowsText = "0 dòng";
    [ObservableProperty] private string _lastRowText = "";
    [ObservableProperty] private string _rangesText = "—";
    [ObservableProperty] private string _lastText = "—";
    [ObservableProperty] private string _machinesText = "";
    [ObservableProperty] private string _tooltip = "";
}

/// <summary>Thống kê 1 SHOP: tên + sheet (ẩn ở hub-mode) + 4 thẻ op (scrape/import/update/rewrite) tạo 1 lần
/// rồi update-in-place theo <see cref="WorkspaceStatsViewModel.OpKeys"/>.</summary>
public sealed partial class WorkspaceShopStats : ObservableObject
{
    public string ShopId { get; }

    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _sheetText = "";

    public ObservableCollection<WorkspaceOpStat> Ops { get; } =
    [
        new WorkspaceOpStat("Scrape"),
        new WorkspaceOpStat("Import"),
        new WorkspaceOpStat("Update"),
        new WorkspaceOpStat("Tên SP"),
    ];

    public WorkspaceShopStats(string shopId) => ShopId = shopId;
}

/// <summary>KPI tổng cả tài khoản của 1 op: tổng dòng đã làm + "x/y shop ✓" (đếm shop có ledger completed).</summary>
public sealed partial class WorkspaceOpKpi : ObservableObject
{
    public string OpLabel { get; }
    public WorkspaceOpKpi(string opLabel) => OpLabel = opLabel;

    [ObservableProperty] private string _rowsText = "0";
    [ObservableProperty] private string _shopsDoneText = "0/0 shop ✓";
}

/// <summary>
/// VM của tab "Thống kê" trong Workspace client — port tab Thống kê của Hub (Fleet.razor _tab=="stats"): với tài
/// khoản đang xem, mỗi shop × 4 op dựng trạng thái + số dòng từ <see cref="CoordinationRuntime.Hub"/>.CurrentFleet.
/// Chỉ ĐỌC snapshot; không thao tác gì. Hub tắt (Hub null) → chỉ hiện gợi ý, không crash.
/// </summary>
public sealed partial class WorkspaceStatsViewModel : ObservableObject
{
    /// <summary>Thứ tự op — index KHỚP <see cref="WorkspaceShopStats.Ops"/> và <see cref="Kpis"/>.</summary>
    internal static readonly string[] OpKeys = ["scrape", "import", "update", "rewrite"];

    public ObservableCollection<WorkspaceShopStats> Shops { get; } = [];
    public ObservableCollection<WorkspaceOpKpi> Kpis { get; } =
    [
        new WorkspaceOpKpi("Scrape"),
        new WorkspaceOpKpi("Import"),
        new WorkspaceOpKpi("Update"),
        new WorkspaceOpKpi("Tên SP"),
    ];

    [ObservableProperty] private bool _hubConnected;
    [ObservableProperty] private bool _showEmptyHint = true;
    [ObservableProperty] private string _emptyHint = "";

    private BigSellerAccount? _account;
    private readonly DispatcherTimer _agoTimer;

    public WorkspaceStatsViewModel()
    {
        // Fleet đổi (poll 12s / máy khác chạy/xong) → dựng lại. Event bắn từ luồng nền → marshal về UI thread.
        // Sống suốt đời app (như AccountData) nên không cần gỡ handler. Event Changed sau Reconnect sẽ mồ côi
        // (giống WorkspaceAccountViewModel) — timer dưới đây vẫn kéo dữ liệu tươi lại.
        Coordination.Hub.Changed += OnFleetChanged;

        // Nhãn "bao lâu trước" phải trôi dù không có event fleet → nhịp nhẹ 15s dựng lại (vài chục row, rất rẻ).
        _agoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _agoTimer.Tick += (_, _) => Rebuild();
        _agoTimer.Start();

        Rebuild();
    }

    private void OnFleetChanged() => UiThread.Post(Rebuild);

    /// <summary>Đổi tài khoản đang xem (WorkspaceViewModel gọi khi đổi selection) → dựng lại ngay.</summary>
    public void Rescope(BigSellerAccount? account)
    {
        _account = account;
        Rebuild();
    }

    /// <summary>Dựng lại toàn bộ số liệu từ snapshot (chạy trên UI thread). ĐỌC TƯƠI CurrentFleet mỗi lần để
    /// sống sót qua Reconnect. Update-in-place: khớp shop theo ShopId, set property từng thẻ op.</summary>
    private void Rebuild()
    {
        var fleet = CoordinationRuntime.Hub?.CurrentFleet;
        HubConnected = fleet is not null;
        var account = _account;

        if (fleet is null)
        {
            ShowEmptyHint = true;
            EmptyHint = "Chưa kết nối Hub — thống kê đọc từ sổ hoàn thành trên Hub. Cấu hình Hub trong Cài đặt.";
            ClearAll();
            return;
        }
        if (account is null || account.Shops.Count == 0)
        {
            ShowEmptyHint = true;
            EmptyHint = account is null
                ? "Chọn 1 tài khoản BigSeller bên trái để xem thống kê."
                : "Tài khoản này chưa có shop nào.";
            ClearAll();
            return;
        }
        ShowEmptyHint = false;

        var accId = account.Id;

        // ── KPI tổng tài khoản (tổng dòng + shop đã ✓ mỗi op) ──
        for (int i = 0; i < OpKeys.Length; i++)
        {
            var op = OpKeys[i];
            long rows = 0;
            var done = 0;
            foreach (var shop in account.Shops)
            {
                var led = FindLedger(fleet, accId, shop.Id, op);
                if (led is null) continue;
                rows += CountRows(led.Completed);
                if (string.Equals(led.Status, "completed", StringComparison.OrdinalIgnoreCase)) done++;
            }
            Kpis[i].RowsText = rows.ToString("N0");
            Kpis[i].ShopsDoneText = $"{done}/{account.Shops.Count} shop ✓";
        }

        // ── Lưới shop: update-in-place theo ShopId (thêm/xoá/di chuyển khi tập shop đổi) ──
        for (int i = Shops.Count - 1; i >= 0; i--)
            if (account.Shops.All(s => !string.Equals(s.Id, Shops[i].ShopId, StringComparison.Ordinal)))
                Shops.RemoveAt(i);

        for (int i = 0; i < account.Shops.Count; i++)
        {
            var shop = account.Shops[i];
            var existing = Shops.FirstOrDefault(x => string.Equals(x.ShopId, shop.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new WorkspaceShopStats(shop.Id);
                UpdateShopStats(existing, shop, account, fleet, accId);
                Shops.Insert(i, existing);
            }
            else
            {
                var cur = Shops.IndexOf(existing);
                if (cur != i) Shops.Move(cur, i);
                UpdateShopStats(existing, shop, account, fleet, accId);
            }
        }
    }

    /// <summary>Rỗng lưới + zero KPI (khi hub tắt / chưa chọn acc / acc không shop).</summary>
    private void ClearAll()
    {
        if (Shops.Count > 0) Shops.Clear();
        foreach (var k in Kpis) { k.RowsText = "0"; k.ShopsDoneText = "0/0 shop ✓"; }
    }

    private static void UpdateShopStats(WorkspaceShopStats stat, BigSellerShop shop, BigSellerAccount account,
        FleetSnapshot fleet, string accId)
    {
        stat.ShopName = shop.DisplayName;
        // Hub-mode: sheet là GUID ngăn nội bộ, không phô cho user → để trống; excel-mode hiện sheet (hoặc nhắc chưa gán).
        stat.SheetText = account.UsesHubData
            ? ""
            : (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet) ? "— chưa gán sheet" : shop.ShopeeDataSheet);

        for (int i = 0; i < OpKeys.Length; i++)
        {
            var op = OpKeys[i];
            var s = stat.Ops[i];

            var (text, bg, fg) = BuildOpStatus(fleet, accId, shop.Id, op);
            s.StatusText = text;
            s.StatusBg = bg;
            s.StatusFg = fg;

            // Số liệu (dòng/khoảng/gần nhất/máy tham gia) lấy từ ledger nếu có, BẤT KỂ trạng thái.
            var led = FindLedger(fleet, accId, shop.Id, op);
            if (led is not null)
            {
                s.RowsText = $"{CountRows(led.Completed):N0} dòng";
                s.LastRowText = led.LastRowReached > 0 ? $"tới dòng {led.LastRowReached}" : "";
                s.RangesText = FormatRanges(led.Completed);
                var last = led.LastRunAt ?? led.UpdatedAt;
                var machine = Machine(fleet, led);
                var ago = Ago(last);
                s.LastText = string.IsNullOrEmpty(machine) && string.IsNullOrEmpty(ago)
                    ? "—"
                    : $"Gần nhất: {(string.IsNullOrEmpty(machine) ? "—" : machine)}{(string.IsNullOrEmpty(ago) ? "" : $" · {ago}")}";
                var hosts = led.MachineIds.Select(id => HostName(fleet, id)).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
                s.MachinesText = hosts.Count > 1 ? $"Đã tham gia ({hosts.Count} máy): {string.Join(", ", hosts)}" : "";
                s.Tooltip = last == default ? "" : last.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss");
            }
            else
            {
                s.RowsText = "0 dòng";
                s.LastRowText = "";
                s.RangesText = "—";
                s.LastText = "—";
                s.MachinesText = "";
                s.Tooltip = "";
            }
        }
    }

    // ── Logic trạng thái 1 ô op — PORT FleetStateService.OpCell (giữ nguyên thứ tự ưu tiên) ──────────────
    private static readonly TimeSpan LeaseFresh = TimeSpan.FromSeconds(120);

    private static (string text, IBrush bg, IBrush fg) BuildOpStatus(FleetSnapshot f, string accId, string shopId, string op)
    {
        var key = $"{accId}__{shopId}__{op}";

        // 1) Lease còn tươi (<120s) → đang chạy thật (client còn nhịp).
        var lease = f.Leases.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.Ordinal));
        if (lease is not null && (DateTimeOffset.Now - lease.HeartbeatAt) < LeaseFresh)
            return ($"⏳ đang chạy · {Host(lease.Hostname)}", RunningBg, RunningFg);

        var asn = f.Assignments.FirstOrDefault(a =>
            string.Equals(a.BigsellerId, accId, StringComparison.Ordinal) &&
            string.Equals(a.ShopId, shopId, StringComparison.Ordinal) &&
            string.Equals(a.Op, op, StringComparison.Ordinal) &&
            a.Status is "queued" or "running");

        // 2) Assignment 'running' mà máy claim KHÔNG offline → đang chạy.
        if (asn is { Status: "running" } && !MachineOffline(f, asn.ClaimedByMachineId))
            return ($"⏳ đang chạy · {Host(asn.ClaimedByHostname)}", RunningBg, RunningFg);

        // 3) Ledger xong / dừng dở.
        var led = f.Ledger.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.Ordinal));
        if (led?.Status == "completed") return ("✓ xong", DoneBg, DoneFg);
        if (led?.Status == "stopped") return ("■ dừng dở", WarnBg, WarnFg);

        // 4) Assignment 'queued' → đã xếp (kèm máy đích / cảnh báo máy tắt).
        if (asn is { Status: "queued" })
        {
            if (!string.IsNullOrEmpty(asn.TargetMachineId))
            {
                var tm = f.Machines.FirstOrDefault(m => string.Equals(m.MachineId, asn.TargetMachineId, StringComparison.Ordinal));
                if (MachineOffline(f, asn.TargetMachineId))
                    return ($"⚠ chờ {Host(tm?.Hostname ?? "")} (máy tắt)", WarnBg, WarnFg);
                return ($"• đã xếp → {Host(tm?.Hostname ?? "")}", TodoBg, TodoFg);
            }
            return ("• đã xếp", TodoBg, TodoFg);
        }

        // 5) Ledger 'running' nhưng không còn lease/assignment đại diện → tiến trình chạy tay chết giữa chừng.
        if (led?.Status == "running" && asn is not { Status: "running" })
            return ("■ dừng dở (mất kết nối)", WarnBg, WarnFg);

        // 6) Assignment 'failed' còn mới (<3 phút).
        var failed = f.Assignments
            .Where(a => string.Equals(a.BigsellerId, accId, StringComparison.Ordinal)
                && string.Equals(a.ShopId, shopId, StringComparison.Ordinal)
                && string.Equals(a.Op, op, StringComparison.Ordinal)
                && a.Status == "failed")
            .OrderByDescending(a => a.UpdatedAt).FirstOrDefault();
        if (failed is not null && (DateTimeOffset.Now - failed.UpdatedAt) < TimeSpan.FromMinutes(3))
            return ($"✘ lỗi ({Host(failed.ClaimedByHostname)})", ErrorBg, ErrorFg);

        return ("· chưa chạy", TodoBg, TodoFg);
    }

    // ── Helper port từ Hub (Fleet.razor + FleetStateService) ──────────────────────────────────────────
    private static WorkLedgerRecord? FindLedger(FleetSnapshot f, string accId, string shopId, string op)
    {
        var key = $"{accId}__{shopId}__{op}";
        return f.Ledger.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.Ordinal));
    }

    private static int CountRows(List<RowRange> rr) => rr.Sum(r => Math.Max(0, r.To - r.From + 1));

    private static string FormatRanges(List<RowRange> rr) => rr.Count == 0
        ? "—"
        : string.Join(", ", rr.OrderBy(r => r.From).Select(r => r.From == r.To ? $"{r.From}" : $"{r.From}–{r.To}"));

    private static string Ago(DateTimeOffset at)
    {
        if (at == default || at == DateTimeOffset.MinValue) return "";
        var s = (DateTimeOffset.Now - at).TotalSeconds;
        if (s < 0) s = 0;
        return s < 60 ? $"{(int)s}s trước" : s < 3600 ? $"{(int)(s / 60)} phút trước" : $"{(int)(s / 3600)} giờ trước";
    }

    /// <summary>Tên máy chạy gần nhất của 1 ledger: LastMachineId → hostname (Machines), fallback LastHostname.</summary>
    private static string Machine(FleetSnapshot f, WorkLedgerRecord l)
    {
        if (!string.IsNullOrEmpty(l.LastMachineId))
        {
            var m = f.Machines.FirstOrDefault(x => string.Equals(x.MachineId, l.LastMachineId, StringComparison.Ordinal));
            if (m is not null && !string.IsNullOrWhiteSpace(m.Hostname)) return m.Hostname;
        }
        return string.IsNullOrWhiteSpace(l.LastHostname) ? "" : l.LastHostname;
    }

    private static string HostName(FleetSnapshot f, string machineId)
    {
        if (string.IsNullOrEmpty(machineId)) return "";
        var m = f.Machines.FirstOrDefault(x => string.Equals(x.MachineId, machineId, StringComparison.Ordinal));
        return m is not null && !string.IsNullOrWhiteSpace(m.Hostname) ? m.Hostname : ShortId(machineId);
    }

    private static string ShortId(string id) => string.IsNullOrEmpty(id) ? "?" : (id.Length <= 8 ? id : id[..8] + "…");

    private static bool MachineOffline(FleetSnapshot f, string? machineId)
    {
        if (string.IsNullOrEmpty(machineId)) return false;
        var m = f.Machines.FirstOrDefault(x => string.Equals(x.MachineId, machineId, StringComparison.Ordinal));
        return m is null || (DateTimeOffset.Now - m.LastSeen).TotalSeconds >= 180;
    }

    private static string Host(string h) => string.IsNullOrWhiteSpace(h) ? "?" : h;

    // ── Màu chip (nền nhạt + chữ đậm) — freeze để tái dùng, hài hoà với ScrapeTargetViewModel ──
    private static readonly IBrush DoneBg = FrozenBrush("#E8F5E9"), DoneFg = FrozenBrush("#2E7D32");
    private static readonly IBrush RunningBg = FrozenBrush("#FFF3E0"), RunningFg = FrozenBrush("#E65100");
    private static readonly IBrush TodoBg = FrozenBrush("#ECEFF1"), TodoFg = FrozenBrush("#546E7A");
    private static readonly IBrush WarnBg = FrozenBrush("#FFF8E1"), WarnFg = FrozenBrush("#B26A00");
    private static readonly IBrush ErrorBg = FrozenBrush("#FDECEA"), ErrorFg = FrozenBrush("#C62828");

    private static IBrush FrozenBrush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
