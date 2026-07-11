using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Modules.BigSeller;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.UpdateProduct;

namespace Shopee.Suite.Modules.Workspace;

/// <summary>
/// Gộp 3 "đích" của CÙNG 1 tk BigSeller về một chỗ: cấu hình (BigSeller) + scrape + update. Màn gộp v1.1
/// xoay quanh đối tượng này — bên trái là danh sách các tk, bên phải là chi tiết + lưới shop. KHÔNG sở
/// hữu engine: chỉ bọc 3 ViewModel cũ (dùng chung với 3 màn cũ) nên trạng thái luôn đồng bộ.
/// </summary>
public sealed partial class WorkspaceAccountViewModel : ObservableObject
{
    public BigSellerAccount Account { get; }
    public BigSellerAccountItemViewModel Bs { get; }
    public ScrapeTargetViewModel ScrapeTarget { get; }
    public UpdateRunTargetViewModel UpdateTarget { get; }
    public UpdateProductViewModel UpdateVm { get; }

    /// <summary>Log scrape RIÊNG của tk này (buffer + file riêng, bền qua rebuild VM) — tab "Theo dõi Scrape" bind vào.</summary>
    public LogBuffer ScrapeLog { get; }
    /// <summary>Log import/update/tên SP RIÊNG của tk này — tab "Theo dõi Update" bind vào.</summary>
    public LogBuffer UpdateLog { get; }

    public ObservableCollection<WorkspaceShopViewModel> Shops { get; } = [];

    public WorkspaceAccountViewModel(
        BigSellerAccountItemViewModel bs, ScrapeTargetViewModel scrape, UpdateRunTargetViewModel update,
        UpdateProductViewModel updateVm, LogBuffer scrapeLog, LogBuffer updateLog)
    {
        Bs = bs;
        ScrapeTarget = scrape;
        UpdateTarget = update;
        UpdateVm = updateVm;
        ScrapeLog = scrapeLog;
        UpdateLog = updateLog;
        Account = bs.Model;

        foreach (var s in Account.Shops) Shops.Add(new WorkspaceShopViewModel(this, s));

        // Scrape bắn ShopStatuses → refresh chip scrape; Update bắn JobsChanged (start/finish) → đổi nút
        // Import/Update/Tên SP ⇄ Dừng + khoá các shop CÙNG tk (1 tk chỉ 1 workflow update tại 1 thời điểm).
        ScrapeTarget.PropertyChanged += OnScrapeChanged;
        UpdateVm.JobsChanged += OnUpdateJobsChanged;
        Coordination.Hub.Changed += OnFleetChanged;   // fleet đổi (máy khác chạy/xong) → đổi màu action
    }

    private void OnFleetChanged() => Services.UiThread.Post(RefreshFleetAll);

    private void RefreshFleetAll() { foreach (var s in Shops) s.RefreshFleet(); }

    private void OnUpdateJobsChanged()
    {
        foreach (var s in Shops) s.RefreshUpdateState();
        // JobsChanged bắn trên UI thread (RaiseJobsChanged dùng UiThread.Post) → raise thẳng được.
        OnPropertyChanged(nameof(HasRunningWork));
    }

    private void OnScrapeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScrapeTargetViewModel.ShopStatuses)
            or nameof(ScrapeTargetViewModel.SelectedShop)
            or nameof(ScrapeTargetViewModel.ProgressText))
        {
            OnPropertyChanged(nameof(IsScraping));
            OnPropertyChanged(nameof(HasRunningWork));
            foreach (var s in Shops) s.RefreshStatus();
            OnPropertyChanged(nameof(ScrapeSummary));
        }
    }

    /// <summary>true nếu tk này đang scrape 1 shop nào đó (job LIVE) → khoá ▶/⏯ các shop còn lại
    /// (1 shop/account: đang chạy 1 shop thì các shop khác không cho chạy chồng).</summary>
    public bool IsScraping => Shops.Any(s => ScrapeTarget.IsShopRunning?.Invoke(s.Shop) ?? false);

    /// <summary>Điều khiển nút "■ Dừng việc shop này" — ẩn khi acc không có action nào chạy (scrape LẪN update).</summary>
    public bool HasRunningWork => IsScraping || UpdateVm.IsUpdateRunning(Account.Id);

    /// <summary>Tick để chạy BATCH — đồng bộ cả scrape lẫn update (1 lần tick dùng cho cả 2 giai đoạn).</summary>
    public bool IsSelected
    {
        get => ScrapeTarget.IsSelected;
        set
        {
            if (ScrapeTarget.IsSelected == value) return;
            ScrapeTarget.IsSelected = value;   // tự Persist xuống ScrapeTargetConfigStore
            UpdateTarget.IsSelected = value;
            OnPropertyChanged();
        }
    }

    public string DisplayName => Account.DisplayName;
    public string CookieStatus => Bs.CookieStatus;
    public string WorkbookPath => Account.WorkbookPath;
    public int ShopCount => Shops.Count;

    /// <summary>Tóm tắt "đã scrape X/Y shop" cho dòng tài khoản bên trái (theo dõi nhanh).</summary>
    public string ScrapeSummary
    {
        get
        {
            var total = Shops.Count;
            if (total == 0) return "chưa có shop";
            var done = Shops.Count(s => s.ScrapeStatusText.Contains("đã scrape"));
            return $"{done}/{total} shop đã scrape";
        }
    }

    public void RefreshAll()
    {
        Bs.NotifyCookieChanged();
        foreach (var s in Shops) { s.RefreshStatus(); s.RefreshUpdateState(); }
        OnPropertyChanged(nameof(CookieStatus));
        OnPropertyChanged(nameof(ScrapeSummary));
        OnPropertyChanged(nameof(IsSelected));
    }

    /// <summary>Bỏ lắng nghe khi rebuild để không rò sự kiện / cập nhật dòng đã xoá.</summary>
    public void Detach()
    {
        ScrapeTarget.PropertyChanged -= OnScrapeChanged;
        UpdateVm.JobsChanged -= OnUpdateJobsChanged;
        Coordination.Hub.Changed -= OnFleetChanged;
    }
}
