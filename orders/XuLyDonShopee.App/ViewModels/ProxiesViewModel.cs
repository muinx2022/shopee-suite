using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Màn hình quản lý proxy: nhập (dán) danh sách, hiển thị, xóa.
/// </summary>
public partial class ProxiesViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ProxiesViewModel(AppServices services)
    {
        _services = services;
        Reload();
    }

    public ObservableCollection<ProxyEntry> Proxies { get; } = new();

    [ObservableProperty]
    private ProxyEntry? _selectedProxy;

    [ObservableProperty]
    private string? _importResultMessage;

    /// <summary>Văn bản danh sách API key KiotProxy (mỗi dòng một key).</summary>
    [ObservableProperty]
    private string _keys = string.Empty;

    /// <summary>Thông báo sau khi lưu key (null = ẩn).</summary>
    [ObservableProperty]
    private string? _savedKeysMessage;

    /// <summary>Nhãn tổng số proxy.</summary>
    public string TotalText => $"Tổng: {Proxies.Count} proxy";

    public void Reload()
    {
        Proxies.Clear();
        foreach (var proxy in _services.Proxies.GetAll())
        {
            Proxies.Add(proxy);
        }
        OnPropertyChanged(nameof(TotalText));

        Keys = _services.Settings.Get(SettingsRepository.KiotProxyApiKeys) ?? string.Empty;
        SavedKeysMessage = null;
    }

    /// <summary>Lưu danh sách API key KiotProxy (chuẩn hóa: bỏ trống/trùng) vào pool chung. Key được app tự
    /// chia cho tài khoản khi chạy (ưu tiên key rảnh) — KHÔNG gán cố định vào từng tài khoản.</summary>
    [RelayCommand]
    private void SaveKeys()
    {
        var keys = KiotProxyKeyParser.Parse(Keys);
        _services.Settings.SetKiotProxyKeys(keys);
        Keys = KiotProxyKeyParser.Join(keys); // hiển thị bản đã chuẩn hóa

        SavedKeysMessage = keys.Count == 0
            ? "Đã lưu (chưa có key — sẽ dùng IP máy)."
            : $"Đã lưu {keys.Count} key vào pool — app tự chia cho tài khoản khi chạy (ưu tiên key rảnh).";
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var text = await DialogService.ImportProxyAsync();
        if (text is null)
        {
            return; // người dùng bấm Hủy
        }

        var result = ProxyParser.Parse(text);
        if (result.Valid.Count > 0)
        {
            _services.Proxies.InsertMany(result.Valid);
        }

        Reload();

        var message = new StringBuilder();
        message.Append($"Đã nhập {result.Valid.Count} proxy");
        if (result.Errors.Count > 0)
        {
            message.Append($", {result.Errors.Count} dòng không hợp lệ:");
            foreach (var error in result.Errors)
            {
                message.Append($"\n  • Dòng {error.LineNumber}: \"{error.Content}\" — {error.Reason}");
            }
        }
        else
        {
            message.Append('.');
        }

        ImportResultMessage = message.ToString();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedProxy is null)
        {
            await DialogService.InfoAsync("Xóa proxy", "Vui lòng chọn một dòng proxy để xóa.");
            return;
        }

        var target = SelectedProxy;
        var ok = await DialogService.ConfirmAsync(
            "Xóa proxy",
            $"Xóa proxy {target.Host}:{target.Port}?");
        if (!ok)
        {
            return;
        }

        _services.Proxies.Delete(target.Id);
        Reload();
        ImportResultMessage = null;
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        if (Proxies.Count == 0)
        {
            return;
        }

        var ok = await DialogService.ConfirmAsync(
            "Xóa tất cả proxy",
            $"Xóa toàn bộ {Proxies.Count} proxy trong danh sách? Thao tác này không thể hoàn tác.");
        if (!ok)
        {
            return;
        }

        _services.Proxies.DeleteAll();
        Reload();
        ImportResultMessage = null;
    }
}
