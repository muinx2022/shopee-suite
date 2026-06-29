using System.Windows.Media;
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
    [ObservableProperty] private Brush _scrapeBackground = Brushes.Transparent;
    [ObservableProperty] private Brush _scrapeForeground = Brushes.Black;
    [ObservableProperty] private string _scrapeTooltip = "";

    /// <summary>true nếu shop này đang là shop được chọn của tk (1 shop/account) → tô đậm dòng đang chọn.</summary>
    public bool IsCurrent => ReferenceEquals(Parent.ScrapeTarget.SelectedShop, Shop);

    /// <summary>true nếu CHÍNH shop này đang scrape (job LIVE) → bật nút ■, các shop khác tắt ▶/⏯.</summary>
    public bool IsScraping => Parent.ScrapeTarget.IsShopRunning?.Invoke(Shop) ?? false;

    /// <summary>true khi tài khoản KHÔNG có shop nào đang scrape → mới cho ▶/⏯ (1 shop/account: đang chạy
    /// 1 shop thì khoá chạy mọi shop trong tk này).</summary>
    public bool CanScrape => !Parent.IsScraping;

    // ── Trạng thái workflow UPDATE (import/update/tên SP) — đổi nút ⇄ ■ Dừng + khoá các shop cùng tk ──
    public bool IsImporting => RunningUpdate(UpdateKind.Import);
    public bool IsUpdatingShop => RunningUpdate(UpdateKind.Update);
    public bool IsRewriting => RunningUpdate(UpdateKind.Rewrite);

    private bool RunningUpdate(UpdateKind kind) =>
        Parent.UpdateVm.TryGetRunningUpdate(Parent.Account.Id, out var sid, out var k)
        && k == kind && string.Equals(sid, Shop.Id, StringComparison.Ordinal);

    /// <summary>true khi tk CHƯA chạy workflow update nào → mới cho bấm Import/Update/Tên SP (các shop cùng
    /// tk không chạy update song song).</summary>
    public bool CanStartUpdate => !Parent.UpdateVm.IsUpdateRunning(Parent.Account.Id);

    public void RefreshUpdateState()
    {
        OnPropertyChanged(nameof(IsImporting));
        OnPropertyChanged(nameof(IsUpdatingShop));
        OnPropertyChanged(nameof(IsRewriting));
        OnPropertyChanged(nameof(CanStartUpdate));
        RefreshFleet();
    }

    // ── Trạng thái action xuyên máy: xanh khi đang chạy (máy này HOẶC máy khác) + tooltip "… by <máy>" ──
    private static readonly Brush RunningBrush = MakeFrozen(Color.FromRgb(0x1E, 0xA0, 0x55));   // xanh lá
    private static readonly Brush IdleBrush = Brushes.Black;
    private static Brush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

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

    public Brush ScrapeIconBrush => OpState(CoordOp.Scrape, IsScraping).running ? RunningBrush : IdleBrush;
    public string ScrapeRunTip { get { var (r, by) = OpState(CoordOp.Scrape, IsScraping); return r ? $"Scraping by {by}" : "Scrape shop này (reset từ 'Từ dòng')"; } }

    public Brush ImportIconBrush => OpState(CoordOp.Import, IsImporting).running ? RunningBrush : IdleBrush;
    public string ImportTip { get { var (r, by) = OpState(CoordOp.Import, IsImporting); return r ? $"Importing by {by}" : "Import to store cho shop này"; } }

    public Brush UpdateIconBrush => OpState(CoordOp.Update, IsUpdatingShop).running ? RunningBrush : IdleBrush;
    public string UpdateTip { get { var (r, by) = OpState(CoordOp.Update, IsUpdatingShop); return r ? $"Updating by {by}" : "Update product cho shop này"; } }

    public Brush RewriteIconBrush => OpState(CoordOp.Rewrite, IsRewriting).running ? RunningBrush : IdleBrush;
    public string RewriteTip { get { var (r, by) = OpState(CoordOp.Rewrite, IsRewriting); return r ? $"Đặt tên SP by {by}" : "Update tên SP (AI) cho shop này"; } }

    /// <summary>true khi shop này đang chạy 1 workflow update TRÊN MÁY NÀY → bật nút ■ Dừng (chỉ dừng việc của mình).</summary>
    public bool IsUpdatingAnyLocal => IsImporting || IsUpdatingShop || IsRewriting;

    /// <summary>Làm mới màu/tooltip action (gọi khi hub đổi fleet + khi trạng thái local đổi).</summary>
    public void RefreshFleet()
    {
        OnPropertyChanged(nameof(ScrapeIconBrush)); OnPropertyChanged(nameof(ScrapeRunTip));
        OnPropertyChanged(nameof(ImportIconBrush)); OnPropertyChanged(nameof(ImportTip));
        OnPropertyChanged(nameof(UpdateIconBrush)); OnPropertyChanged(nameof(UpdateTip));
        OnPropertyChanged(nameof(RewriteIconBrush)); OnPropertyChanged(nameof(RewriteTip));
        OnPropertyChanged(nameof(IsUpdatingAnyLocal));
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
