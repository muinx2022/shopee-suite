using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Suite.Modules.BigSeller;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.UpdateProduct;

namespace Shopee.Suite.Modules.Workspace;

/// <summary>
/// MÀN GỘP v1.1 — gộp BigSeller (cấu hình) + Scrape + Update vào MỘT chỗ, xoay quanh từng tk BigSeller:
/// chọn 1 tk → thấy ngay có shop nào, shop nào đã/đang/chưa scrape, rồi Scrape / Import / Update / Tên SP
/// NGAY tại chỗ cho từng shop — hết phải nhảy qua lại 3 màn.
///
/// KHÔNG viết lại engine: tái dùng nguyên 3 ViewModel cũ (Shell truyền vào, dùng CHUNG với 3 màn cũ) nên
/// state luôn đồng bộ, không xung đột. Đây chỉ là lớp UI gắn kết + uỷ lệnh xuống 3 VM đó.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    public BigSellerViewModel BigSeller { get; }
    public ScrapeViewModel Scrape { get; }
    public UpdateProductViewModel Update { get; }

    public ObservableCollection<WorkspaceAccountViewModel> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private WorkspaceAccountViewModel? _selectedAccount;

    public bool HasSelection => SelectedAccount is not null;

    [ObservableProperty] private string _status = "";

    /// <summary>Shell cấp: điều hướng sidebar sang ViewModel khác (mở tab BigSeller để đăng nhập/cấu hình).</summary>
    public Action<object>? RequestNavigate { get; set; }

    public WorkspaceViewModel(BigSellerViewModel bigSeller, ScrapeViewModel scrape, UpdateProductViewModel update)
    {
        BigSeller = bigSeller;
        Scrape = scrape;
        Update = update;

        Rebuild();
        // Handler này đăng ký SAU 3 sub-VM (Shell tạo chúng trước) → khi store đổi, chúng reload trước,
        // rồi tới đây zip lại danh sách hợp nhất.
        BigSellerStore.Shared.Changed += OnStoreChanged;
    }

    private void OnStoreChanged()
    {
        // Đang chạy: các sub-VM giữ nguyên list (Scrape bỏ qua reload khi busy) → khỏi rebuild để không
        // churn tham chiếu job đang chạy. Bao gồm cả update inline per-shop (HasActiveWsJob, không set IsRunning).
        if (Scrape.IsBusy || Update.IsRunning || Update.HasActiveWsJob) return;
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Rebuild();
        else d.BeginInvoke(Rebuild);
    }

    private void Rebuild()
    {
        var prevId = SelectedAccount?.Account.Id;
        foreach (var a in Accounts) a.Detach();
        Accounts.Clear();

        // Zip 3 list theo Account.Id (cùng nguồn BigSellerStore nên khớp tập tk).
        foreach (var bs in BigSeller.Items)
        {
            var scrape = Scrape.ScrapeTargets.FirstOrDefault(t => t.Account.Id == bs.Model.Id);
            var update = Update.RunTargets.FirstOrDefault(t => t.Account.Id == bs.Model.Id);
            if (scrape is null || update is null) continue;   // sub-VM chưa kịp đồng bộ → bỏ qua vòng này
            Accounts.Add(new WorkspaceAccountViewModel(bs, scrape, update, Update));
        }

        SelectedAccount = Accounts.FirstOrDefault(a => a.Account.Id == prevId) ?? Accounts.FirstOrDefault();
        Status = $"{Accounts.Count} tài khoản BigSeller.";
    }

    partial void OnSelectedAccountChanged(WorkspaceAccountViewModel? value)
    {
        // Đồng bộ cho Thống kê/Map chạy đúng tk đang xem. KHÔNG set BigSeller.Selected ở đây — nếu set, mỗi
        // lần kho BigSeller bắn Changed (vd sửa shop ở tab Cấu hình) sẽ kéo màn Cấu hình nhảy về tk Workspace
        // đang chọn. BigSeller.Selected chỉ set khi bấm "Đăng nhập / cấu hình" (GoToBigSellerConfig).
        Scrape.SelectedTarget = value?.ScrapeTarget;
        Update.SelectedTarget = value?.UpdateTarget;
        value?.RefreshAll();
    }

    // ── Tài khoản ────────────────────────────────────────────────────────────
    // (Thêm/xoá/đăng nhập tài khoản BigSeller đều nằm ở tab "BigSeller" — không lặp lại ở đây.)
    [RelayCommand] private void Reload()
    {
        Scrape.ReloadCommand.Execute(null);
        Update.ReloadCommand.Execute(null);
        Rebuild();
    }

    // ── Đăng nhập / cấu hình: KHÔNG làm tại đây (tránh trùng 2 nơi) — chuyển sang tab BigSeller ──
    [RelayCommand]
    private void GoToBigSellerConfig()
    {
        if (SelectedAccount is not null) BigSeller.Selected = SelectedAccount.Bs;   // mở đúng tk bên tab kia
        RequestNavigate?.Invoke(BigSeller);
    }

    // ── Chọn shop (1 shop/account) ───────────────────────────────────────────
    [RelayCommand]
    private void PickShop(WorkspaceShopViewModel? shop)
    {
        if (shop is null) return;
        shop.Parent.ScrapeTarget.SelectedShop = shop.Shop;
        shop.Parent.UpdateTarget.SelectedShop = shop.Shop;
        foreach (var s in shop.Parent.Shops) s.RefreshStatus();
    }

    // ── Scrape theo shop ─────────────────────────────────────────────────────
    [RelayCommand] private Task ScrapeShop(WorkspaceShopViewModel? shop) => RunScrapeShop(shop, resume: false);
    [RelayCommand] private Task ResumeShop(WorkspaceShopViewModel? shop) => RunScrapeShop(shop, resume: true);

    private Task RunScrapeShop(WorkspaceShopViewModel? shop, bool resume)
    {
        if (shop is null) return Task.CompletedTask;
        shop.Parent.ScrapeTarget.SelectedShop = shop.Shop;
        return Scrape.RunSingleAsync(shop.Parent.ScrapeTarget, resume);
    }

    /// <summary>Dừng RIÊNG scrape của tk chứa shop này (1 shop/account) — tk khác chạy tiếp.</summary>
    [RelayCommand]
    private Task StopShop(WorkspaceShopViewModel? shop)
        => shop is null ? Task.CompletedTask : Scrape.StopSingleAsync(shop.Parent.ScrapeTarget);

    // ── Update theo shop (Import / Update / Tên SP) ──────────────────────────
    [RelayCommand] private Task ImportShop(WorkspaceShopViewModel? shop) => RunUpdateShop(shop, t => Update.RunImportSingleAsync(t));
    [RelayCommand] private Task UpdateShop(WorkspaceShopViewModel? shop) => RunUpdateShop(shop, t => Update.RunUpdateSingleAsync(t));
    [RelayCommand] private Task RewriteShop(WorkspaceShopViewModel? shop) => RunUpdateShop(shop, t => Update.RunNameRewriteSingleAsync(t));

    private Task RunUpdateShop(WorkspaceShopViewModel? shop, Func<UpdateRunTargetViewModel, Task> run)
    {
        if (shop is null) return Task.CompletedTask;
        shop.Parent.UpdateTarget.SelectedShop = shop.Shop;
        return run(shop.Parent.UpdateTarget);
    }

    /// <summary>Dừng RIÊNG workflow update (import/update/tên SP) của tk chứa shop này — nút ■ inline.</summary>
    [RelayCommand]
    private void StopUpdateShop(WorkspaceShopViewModel? shop)
    {
        if (shop is null) return;
        Update.StopSingle(shop.Parent.Account.Id);
    }

    // (Map field ↔ cột Excel làm bên tab "Cấu hình BigSeller" khi cài đặt shop — không lặp ở workspace.)

    // ── Dừng ──────────────────────────────────────────────────────────────────
    // (Dừng RIÊNG 1 tk = bấm ■ trên shop đang chạy của tk đó — mỗi tk chỉ 1 action/1 shop tại 1 thời điểm
    //  nên nút ■ của shop chính là "dừng action của tk này". Không cần nút "Dừng tk đang chọn" riêng.)

    /// <summary>Dừng TẤT CẢ scrape + update đang chạy.</summary>
    [RelayCommand]
    private void StopAll()
    {
        Update.StopAllSingle();
        if (Scrape.StopCommand.CanExecute(null)) Scrape.StopCommand.Execute(null);
    }
}
