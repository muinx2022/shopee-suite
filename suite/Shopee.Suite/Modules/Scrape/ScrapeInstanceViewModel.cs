using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.Accounts;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>
/// Một dòng trong bảng "Phân công scrape". Hai cách dùng:
///  - Manual (tắt auto): 1 dòng = 1 account Shopee được tick, có khối dòng cố định.
///  - Auto: 1 dòng = 1 "process" (slot) chạy song song; Account/Dòng đổi liên tục khi xoay vòng tk.
/// </summary>
public sealed partial class ScrapeInstanceViewModel : ObservableObject
{
    public string Key { get; }
    public ShopeeAccount? Account { get; }   // null với dòng process auto

    /// <summary>Màu nền dòng theo tk BigSeller (mỗi tk BigSeller 1 màu) — để dễ phân biệt các process cùng
    /// 1 tk BigSeller khi chạy nhiều tk song song. null = không tô (mặc định).</summary>
    public IBrush? AccountBrush { get; }

    /// <summary>Manual: dòng theo 1 account.</summary>
    public ScrapeInstanceViewModel(ShopeeAccount account)
    {
        Key = account.Id;
        Account = account;
        _label = account.DisplayName;
        _accountName = account.Username;
    }

    /// <summary>Auto: dòng theo 1 process/slot (accountBrush = màu tk BigSeller của process này).</summary>
    public ScrapeInstanceViewModel(string key, string label, IBrush? accountBrush = null)
    {
        Key = key;
        _label = label;
        AccountBrush = accountBrush;
    }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private int _linkCount;
    [ObservableProperty] private string _status = "Chờ";

    // Manual: khối dòng để dựng spec.
    public int FromRow { get; set; }
    public int ToRow { get; set; }

    public void SetRange(int from, int to)
    {
        FromRow = from; ToRow = to;
        RangeText = from > 0 ? $"{from}–{to}" : "";
    }
}
