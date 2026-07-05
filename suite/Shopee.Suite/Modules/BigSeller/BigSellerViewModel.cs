using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.BigSeller;

/// <summary>
/// Mục "BigSeller" dùng chung (Scrape + Update Product). Quản lý kho <see cref="BigSellerStore"/>:
/// tài khoản BigSeller + workbook + danh sách shop (mỗi shop 1 sheet) + đăng nhập lấy cookie chung.
/// </summary>
public sealed partial class BigSellerViewModel : ObservableObject
{
    public ObservableCollection<BigSellerAccountItemViewModel> Items { get; } = [];

    public ObservableCollection<string> LoginLog { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(LoginCommand), nameof(AddShopCommand))]
    private BigSellerAccountItemViewModel? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShopSelection))]
    private BigSellerShopViewModel? _selectedShop;

    public bool HasSelection => Selected is not null;
    public bool HasShopSelection => SelectedShop is not null;

    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(StopLoginCommand))]
    private bool _isLoggingIn;

    public bool IsIdle => !IsLoggingIn;

    private CancellationTokenSource? _loginCts;

    public BigSellerViewModel()
    {
        Reload();
        // Khôi phục/import (BackupService) cập nhật kho dùng chung rồi bắn Changed → tab này phải nạp lại
        // (trước đây chỉ Reload lúc khởi tạo nên import xong vẫn trống tới khi mở lại app).
        BigSellerStore.Shared.Changed += OnStoreChanged;
    }

    private void OnStoreChanged() => UiThread.Post(SyncFromStore);

    /// <summary>Nạp lại toàn bộ khi TẬP tài khoản đổi (import/khôi phục, Add/Delete) — KHÔNG rebuild khi chỉ
    /// sửa thuộc tính (tránh mất focus lúc đang nhập + tránh nhân đôi dòng). Khi tập acc không đổi, vẫn đối
    /// chiếu danh sách SHOP của từng acc: Hub sync có thể thêm/bớt shop trên một acc ĐÃ có (tập acc y nguyên
    /// nên guard trên không bắt được) → trước đây shop mới từ Hub không hiện tới khi khởi động lại app.</summary>
    private void SyncFromStore()
    {
        var storeIds = BigSellerStore.Shared.Accounts.Select(a => a.Id).ToHashSet();
        var itemIds = Items.Select(i => i.Model.Id).ToHashSet();
        if (!storeIds.SetEquals(itemIds)) { Reload(); return; }
        foreach (var item in Items) item.SyncShopsFromModel();   // fast-path rẻ khi tập shop không đổi
    }

    private void Reload()
    {
        var prevId = Selected?.Model.Id;
        Items.Clear();
        foreach (var a in BigSellerStore.Shared.Accounts)
            Items.Add(new BigSellerAccountItemViewModel(a));
        Selected = Items.FirstOrDefault(i => i.Model.Id == prevId) ?? Items.FirstOrDefault();
        Status = $"{Items.Count} tài khoản BigSeller.";
    }

    private bool SaveStore(string success, string failure)
    {
        if (BigSellerStore.Shared.Save())
        {
            Status = success;
            return true;
        }

        Status = failure;
        return false;
    }

    [RelayCommand]
    private void Add()
    {
        var model = new BigSellerAccount { Label = "BigSeller mới" };
        if (BigSellerStore.Shared.Add(model))   // → Changed → SyncFromStore dựng lại Items (gồm acc mới)
        {
            Selected = Items.FirstOrDefault(i => i.Model.Id == model.Id) ?? Selected;
            Status = $"{Items.Count} tài khoản BigSeller.";
        }
        else
        {
            Status = "Không thêm được tài khoản BigSeller mới.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var sel = Selected;
        if (sel is null) return;
        if (!await Dialogs.ConfirmAsync($"Xóa tài khoản BigSeller \"{sel.DisplayName}\"?", "Xóa"))
            return;
        if (BigSellerStore.Shared.Remove(sel.Model.Id))   // → Changed → SyncFromStore dựng lại Items
        {
            Selected = Items.FirstOrDefault();
            Status = $"{Items.Count} tài khoản BigSeller.";
        }
        else
        {
            Status = $"Không xóa được \"{sel.DisplayName}\".";
        }
    }

    [RelayCommand]
    private void Save()
    {
        SaveStore("Đã lưu cấu hình BigSeller.", "Không lưu được cấu hình BigSeller.");
    }

    [RelayCommand]
    private async Task BrowseWorkbookAsync()
    {
        var sel = Selected;
        if (sel is null) return;
        var path = await FilePicker.OpenFileAsync("Chọn workbook", "Excel|*.xlsx;*.xlsm|Tất cả|*.*");
        if (path is null) return;
        sel.WorkbookPath = path;
        SaveStore(
            $"Workbook có {sel.SheetOptions.Count} sheet.",
            "Không lưu được đường dẫn workbook.");
    }

    [RelayCommand]
    private async Task BrowseCookieAsync()
    {
        var sel = Selected;
        if (sel is null) return;
        var path = await FilePicker.SaveFileAsync("File cookie BigSeller", "JSON|*.json",
            defaultFileName: string.IsNullOrWhiteSpace(sel.CookieFile) ? "bigseller-cookies.json" : Path.GetFileName(sel.CookieFile),
            overwritePrompt: false);
        if (path is null) return;
        sel.CookieFile = path;
        SaveStore("Đã cập nhật file cookie BigSeller.", "Không lưu được file cookie BigSeller.");
    }

    [RelayCommand]
    private void RefreshSheets()
    {
        if (Selected is null) return;
        Selected.RefreshSheets();
        Status = $"Workbook có {Selected.SheetOptions.Count} sheet.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddShop()
    {
        if (Selected is null) return;
        SelectedShop = Selected.AddShop();
        SaveStore("Đã thêm shop mới.", "Không lưu được shop mới.");
    }

    [RelayCommand]
    private void RemoveShop()
    {
        if (Selected is null || SelectedShop is null) return;
        Selected.RemoveShop(SelectedShop);
        SelectedShop = Selected.Shops.FirstOrDefault();
        SaveStore("Đã xóa shop.", "Không lưu được thay đổi danh sách shop.");
    }

    // ── Đăng nhập BigSeller (lấy cookie chung) ──────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (Selected is null) return;

        // Mặc định file cookie nếu chưa đặt.
        if (string.IsNullOrWhiteSpace(Selected.CookieFile))
        {
            Selected.CookieFile = Path.Combine(
                SuitePaths.ModuleDir("shared"), "bigseller-cookies", Selected.Model.Id + ".json");
            Save();
        }

        IsLoggingIn = true;
        LoginLog.Clear();
        _loginCts = new CancellationTokenSource();
        var cookieFile = Selected.CookieFile;
        var account = Selected;
        try
        {
            // Profile Brave RIÊNG theo từng tài khoản (theo Id) → mỗi tk BigSeller 1 phiên/cookie độc
            // lập, không bị "đăng nhập vào tk cũ" do dùng chung profile.
            var profileDir = Path.Combine(SuitePaths.ModuleDir("bigseller-login"), account.Model.Id);

            // Tk có proxy riêng (KiotProxy key) → ĐĂNG NHẬP qua đúng proxy đó để token lưu ra khớp IP với
            // lúc scrape (scrape route bigseller.com qua proxy này). Không có key → mở IP máy như cũ.
            string? proxyServer = null;
            if (account.Model.HasProxy)
            {
                AppendLog("Đang lấy proxy riêng của tk BigSeller (KiotProxy)…");
                proxyServer = await OpenMultiBraveLauncherV3.BigSellerProxyResolver.ResolveServerAsync(
                    account.Model.KiotProxyKey, account.Model.Region, account.Model.ProxyType, AppendLog);
            }

            // Lưu cookie xong, cửa sổ Brave GIỮ NGUYÊN — chỉ đóng khi bấm Dừng. onSaved báo ngay
            // khi vừa lưu (lúc cửa sổ còn đang mở) để UI cập nhật trạng thái cookie tức thì.
            var ok = await BigSellerLoginRunner.RunLoginAsync(
                cookieFile, profileDir, AppendLog, _loginCts.Token, () => OnLoginSaved(account), proxyServer);
            account.NotifyCookieChanged();
            Status = ok ? "Đăng nhập BigSeller thành công." : "Chưa lấy được cookie BigSeller.";

            // MÁY HUB: đẩy cookie vừa lấy lên Hub NGAY để client kéo về liền, khỏi chờ auto-push (3 phút).
            // Chỉ Hub là nguồn → chỉ Hub push; client (hiếm khi tự login) thì không. KHÔNG truyền _loginCts.Token
            // (lúc này đã bị Cancel do bấm Dừng để đóng cửa sổ) — push phải chạy độc lập. Best-effort.
            if (ok && HubServerConfigStore.Shared.Current.Enabled
                   && CoordinationRuntime.ConfigSync is { } sync)
            {
                AppendLog("Đang đẩy cookie mới lên Hub để các máy khác đồng bộ…");
                try { AppendLog(await sync.PushAsync()); }
                catch (Exception ex) { AppendLog("✘ Đẩy lên Hub lỗi (sẽ tự đẩy lại ở chu kỳ auto): " + ex.Message); }
            }
        }
        finally
        {
            IsLoggingIn = false;
            _loginCts.Dispose();
            _loginCts = null;
        }
    }

    /// <summary>Gọi từ luồng nền của runner ngay khi cookie vừa được lưu (cửa sổ vẫn mở).</summary>
    private void OnLoginSaved(BigSellerAccountItemViewModel account)
    {
        void Apply()
        {
            account.NotifyCookieChanged();
            Status = "✔ Đã lưu cookie — cửa sổ vẫn mở, bấm Dừng khi xong.";
        }
        UiThread.Post(Apply);
    }

    private bool CanLogin() => HasSelection && IsIdle;

    [RelayCommand(CanExecute = nameof(IsLoggingIn))]
    private void StopLogin() => _loginCts?.Cancel();

    private void AppendLog(string text) => UiThread.Post(() => LoginLog.Add(text));
}
