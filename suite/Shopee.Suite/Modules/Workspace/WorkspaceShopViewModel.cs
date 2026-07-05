using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Suite.Modules.UpdateProduct;

namespace Shopee.Suite.Modules.Workspace;

/// <summary>
/// 1 dòng SHOP trong lưới pipeline của màn gộp v1.1: tên + sheet + trạng thái scrape (đã/đang/chưa) +
/// nút hành động (scrape / import / update / tên SP / map). Trỏ về <see cref="WorkspaceAccountViewModel"/>
/// cha để các lệnh biết chạy cho tk BigSeller nào (giữ mô hình 1 shop/account như hiện tại).
/// </summary>
public sealed partial class WorkspaceShopViewModel : ObservableObject
{
    public WorkspaceAccountViewModel Parent { get; }
    public BigSellerShop Shop { get; }

    public WorkspaceShopViewModel(WorkspaceAccountViewModel parent, BigSellerShop shop)
    {
        Parent = parent;
        Shop = shop;
        RefreshStatus();
    }

    public string Name => Shop.DisplayName;
    public string Sheet => string.IsNullOrWhiteSpace(Shop.ShopeeDataSheet) ? "— chưa gán sheet" : Shop.ShopeeDataSheet;

    [ObservableProperty] private string _scrapeStatusText = "";
    [ObservableProperty] private IBrush _scrapeBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _scrapeForeground = Brushes.Black;
    [ObservableProperty] private string _scrapeTooltip = "";

    /// <summary>true nếu shop này đang là shop được chọn của tk (1 shop/account) → tô đậm dòng đang chọn.</summary>
    public bool IsCurrent => ReferenceEquals(Parent.ScrapeTarget.SelectedShop, Shop);

    /// <summary>true nếu CHÍNH shop này đang scrape (job LIVE) → bật nút ■, các shop khác tắt ▶/⏯.</summary>
    public bool IsScraping => Parent.ScrapeTarget.IsShopRunning?.Invoke(Shop) ?? false;

    /// <summary>true khi tk KHÔNG có shop nào đang scrape (máy này) VÀ không máy khác bận tk này → mới cho chạy
    /// (1 shop/account + khoá khi máy khác đang chạy tk).</summary>
    public bool CanScrape => !Parent.IsScraping && !OtherMachineBusy;

    // ── Trạng thái workflow UPDATE (import/update/tên SP) — đổi nút ⇄ ■ Dừng + khoá các shop cùng tk ──
    public bool IsImporting => RunningUpdate(UpdateKind.Import);
    public bool IsUpdatingShop => RunningUpdate(UpdateKind.Update);
    public bool IsRewriting => RunningUpdate(UpdateKind.Rewrite);

    private bool RunningUpdate(UpdateKind kind) =>
        Parent.UpdateVm.TryGetRunningUpdate(Parent.Account.Id, out var sid, out var k)
        && k == kind && string.Equals(sid, Shop.Id, StringComparison.Ordinal);

    /// <summary>true khi tk CHƯA chạy workflow update nào (máy này) VÀ không máy khác bận tk này → mới cho bấm
    /// Import/Update/Tên SP (1 workflow update/tk + khoá khi máy khác đang chạy tk).</summary>
    public bool CanStartUpdate => !Parent.UpdateVm.IsUpdateRunning(Parent.Account.Id) && !OtherMachineBusy;

    public void RefreshUpdateState()
    {
        OnPropertyChanged(nameof(IsImporting));
        OnPropertyChanged(nameof(IsUpdatingShop));
        OnPropertyChanged(nameof(IsRewriting));
        OnPropertyChanged(nameof(CanStartUpdate));
        RefreshFleet();
    }

    // ── Nút TOGGLE 1-nút/op: bấm chạy ↔ bấm lại dừng. "Đang chạy" tính cả máy khác (fleet). ──

    /// <summary>Trạng thái 1 op của shop này: chạy không + máy nào. Ưu tiên local (tức thời), rồi fleet.</summary>
    private (bool running, string by) OpState(CoordOp op, bool localRunning)
    {
        if (localRunning) return (true, MachineIdentity.Shared.DisplayName);
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return (false, "");
        var key = new CoordKey(Parent.Account.Id, Shop.Id, Shop.ShopeeDataSheet ?? "", op).Id;   // dùng định dạng khoá CHUẨN
        var lease = hub.CurrentFleet.Leases.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.Ordinal));
        return lease is null ? (false, "") : (true, lease.Hostname);
    }

    /// <summary>true nếu MÁY KHÁC đang giữ bất kỳ lease nào trên tài khoản này → khoá MỌI action ở máy này (kể cả
    /// update). Fleet poll ~12s nên disable có trễ; lease lúc Acquire vẫn là lưới an toàn cuối.</summary>
    private bool OtherMachineBusy
    {
        get
        {
            var hub = CoordinationRuntime.Hub;
            if (hub is null) return false;
            var myId = hub.MachineId;
            return hub.CurrentFleet.Leases.Any(l =>
                string.Equals(l.BigsellerId, Parent.Account.Id, StringComparison.Ordinal) &&
                !string.Equals(l.MachineId, myId, StringComparison.Ordinal));
        }
    }

    // Đang chạy (máy này HOẶC máy khác) — XAML dựa vào đây tô nút xanh (viền + nền nhạt + icon ■).
    public bool ScrapeRunning => OpState(CoordOp.Scrape, IsScraping).running;
    public bool ImportRunning => OpState(CoordOp.Import, IsImporting).running;
    public bool UpdateRunning => OpState(CoordOp.Update, IsUpdatingShop).running;
    public bool RewriteRunning => OpState(CoordOp.Rewrite, IsRewriting).running;

    // Icon nút: ■ khi đang chạy (gợi ý bấm để dừng), ngược lại icon op.
    public string ScrapeToggleContent => ScrapeRunning ? "■" : "▶";
    public string ImportToggleContent => ImportRunning ? "■" : "⬆";
    public string UpdateToggleContent => UpdateRunning ? "■" : "✎";
    public string RewriteToggleContent => RewriteRunning ? "■" : "🏷";

    // Bật nút khi: MÁY NÀY đang chạy op đó (để dừng) HOẶC được phép bắt đầu (theo quy tắc khoá).
    public bool ScrapeToggleEnabled => IsScraping || CanScrape;
    public bool ImportToggleEnabled => IsImporting || CanStartUpdate;
    public bool UpdateToggleEnabled => IsUpdatingShop || CanStartUpdate;
    public bool RewriteToggleEnabled => IsRewriting || CanStartUpdate;

    public string ScrapeToggleTip => ToggleTip("scrape", IsScraping, CoordOp.Scrape, "Scrape shop này (tiếp tục dòng còn thiếu)");
    public string ImportToggleTip => ToggleTip("import", IsImporting, CoordOp.Import, "Import to store cho shop này");
    public string UpdateToggleTip => ToggleTip("update", IsUpdatingShop, CoordOp.Update, "Update product cho shop này");
    public string RewriteToggleTip => ToggleTip("đặt tên SP", IsRewriting, CoordOp.Rewrite, "Update tên SP (AI) cho shop này");

    private string ToggleTip(string verb, bool localRunning, CoordOp op, string idleText)
    {
        if (localRunning) return $"Đang {verb} — bấm để dừng";
        var (r, by) = OpState(op, false);
        return r ? $"Đang {verb} (máy {by}) — dừng tại máy đó" : idleText;
    }

    /// <summary>Op này đã được đánh dấu "✓ xong" trong sổ Hub (ledger.status=completed) chưa — để MỌI client
    /// thấy trạng thái xong (scrape auto báo; import/update operator đặt tay). KHÔNG tính lúc đang chạy.</summary>
    private bool IsOpDone(CoordOp op)
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return false;
        var key = new CoordKey(Parent.Account.Id, Shop.Id, Shop.ShopeeDataSheet ?? "", op).Id;
        return hub.CurrentFleet.Ledger.Any(l => string.Equals(l.Key, key, StringComparison.Ordinal)
                                                && string.Equals(l.Status, "completed", StringComparison.OrdinalIgnoreCase));
    }
    public bool ScrapeDone => !ScrapeRunning && IsOpDone(CoordOp.Scrape);
    public bool ImportDone => !ImportRunning && IsOpDone(CoordOp.Import);
    public bool UpdateDone => !UpdateRunning && IsOpDone(CoordOp.Update);
    public bool RewriteDone => !RewriteRunning && IsOpDone(CoordOp.Rewrite);

    /// <summary>true khi shop này đang chạy 1 workflow update TRÊN MÁY NÀY (giữ cho chỗ khác nếu còn dùng).</summary>
    public bool IsUpdatingAnyLocal => IsImporting || IsUpdatingShop || IsRewriting;

    /// <summary>Làm mới màu/tooltip/enabled/icon nút action (gọi khi hub đổi fleet + khi trạng thái local đổi).</summary>
    public void RefreshFleet()
    {
        foreach (var n in new[]
        {
            nameof(ScrapeRunning), nameof(ImportRunning), nameof(UpdateRunning), nameof(RewriteRunning),
            nameof(ScrapeToggleContent), nameof(ImportToggleContent), nameof(UpdateToggleContent), nameof(RewriteToggleContent),
            nameof(ScrapeToggleEnabled), nameof(ImportToggleEnabled), nameof(UpdateToggleEnabled), nameof(RewriteToggleEnabled),
            nameof(ScrapeToggleTip), nameof(ImportToggleTip), nameof(UpdateToggleTip), nameof(RewriteToggleTip),
            nameof(CanScrape), nameof(CanStartUpdate), nameof(IsUpdatingAnyLocal),
            nameof(ScrapeDone), nameof(ImportDone), nameof(UpdateDone), nameof(RewriteDone),
        }) OnPropertyChanged(n);
    }

    /// <summary>Tính lại trạng thái từ tiến độ scrape (mượn nguyên logic chip của ScrapeTargetViewModel).</summary>
    public void RefreshStatus()
    {
        var st = Parent.ScrapeTarget.StatusFor(Shop);
        ScrapeStatusText = st.StatusText;
        ScrapeBackground = st.Background;
        ScrapeForeground = st.Foreground;
        ScrapeTooltip = st.Tooltip;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Sheet));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsScraping));
        OnPropertyChanged(nameof(CanScrape));
        RefreshFleet();
    }
}
