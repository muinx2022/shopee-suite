using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
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
    }
}
