using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Shopee.Core.BigSeller;
using Shopee.Core.Infrastructure;

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

    private void OnStoreChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) SyncFromStore();
        else d.BeginInvoke(SyncFromStore);
    }

    /// <summary>Chỉ nạp lại khi TẬP tài khoản đổi (import/khôi phục, Add/Delete) — KHÔNG rebuild khi chỉ
    /// sửa thuộc tính (tránh mất focus lúc đang nhập + tránh nhân đôi dòng).</summary>
    private void SyncFromStore()
    {
        var storeIds = BigSellerStore.Shared.Accounts.Select(a => a.Id).ToHashSet();
        var itemIds = Items.Select(i => i.Model.Id).ToHashSet();
        if (storeIds.SetEquals(itemIds)) return;
        Reload();
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

    [RelayCommand]
    private void Add()
    {
        var model = new BigSellerAccount { Label = "BigSeller mới" };
        BigSellerStore.Shared.Add(model);   // → Changed → SyncFromStore dựng lại Items (gồm acc mới)
        Selected = Items.FirstOrDefault(i => i.Model.Id == model.Id);
        Status = $"{Items.Count} tài khoản BigSeller.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is null) return;
        if (Dialogs.Show($"Xóa tài khoản BigSeller \"{Selected.DisplayName}\"?", "Xóa",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        BigSellerStore.Shared.Remove(Selected.Model.Id);   // → Changed → SyncFromStore dựng lại Items
        Selected = Items.FirstOrDefault();
        Status = $"{Items.Count} tài khoản BigSeller.";
    }

    [RelayCommand]
    private void Save()
    {
        BigSellerStore.Shared.Save();
        Status = "Đã lưu cấu hình BigSeller.";
    }

    [RelayCommand]
    private void BrowseWorkbook()
    {
        if (Selected is null) return;
        var dlg = new OpenFileDialog { Filter = "Excel|*.xlsx;*.xlsm|Tất cả|*.*", Title = "Chọn workbook" };
        if (dlg.ShowDialog() == true)
        {
            Selected.WorkbookPath = dlg.FileName;
            Save();
            Status = $"Workbook có {Selected.SheetOptions.Count} sheet.";
        }
    }

    [RelayCommand]
    private void BrowseCookie()
    {
        if (Selected is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "JSON|*.json", Title = "File cookie BigSeller",
            FileName = string.IsNullOrWhiteSpace(Selected.CookieFile) ? "bigseller-cookies.json" : Path.GetFileName(Selected.CookieFile),
            OverwritePrompt = false,
        };
        if (dlg.ShowDialog() == true)
        {
            Selected.CookieFile = dlg.FileName;
            Save();
        }
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
        Save();
    }

    [RelayCommand]
    private void RemoveShop()
    {
        if (Selected is null || SelectedShop is null) return;
        Selected.RemoveShop(SelectedShop);
        SelectedShop = Selected.Shops.FirstOrDefault();
        Save();
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
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Apply();
        else d.BeginInvoke(Apply);
    }

    private bool CanLogin() => HasSelection && IsIdle;

    [RelayCommand(CanExecute = nameof(IsLoggingIn))]
    private void StopLogin() => _loginCts?.Cancel();

    private void AppendLog(string text)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) LoginLog.Add(text);
        else d.BeginInvoke(() => LoginLog.Add(text));
    }
}
