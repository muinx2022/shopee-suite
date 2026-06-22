using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.Scrape;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>Một tk Shopee đang được 1 BigSeller (theo sheet) giữ chỗ — kèm nút nhả tay.</summary>
public sealed class ReservedRow(string sheet, string shopeeId, string name)
{
    public string Sheet { get; } = sheet;
    public string ShopeeId { get; } = shopeeId;
    public string Name { get; } = name;
}

/// <summary>Tiến độ scrape của 1 sheet thuộc tk BigSeller đang xem.</summary>
public sealed class SheetProgressRow
{
    public string Sheet { get; }
    public string Header { get; }
    public string RangesText { get; }
    public ObservableCollection<ReservedRow> Reserved { get; } = [];

    public SheetProgressRow(ScrapeProgress p)
    {
        Sheet = p.Sheet;
        var status = p.Status switch
        {
            "completed" => "✔ Hoàn thành",
            "running" => "▶ Đang chạy",
            "stopped" => "■ Dừng giữa chừng",
            _ => "—",
        };
        var sheetName = string.IsNullOrWhiteSpace(p.Sheet) ? "(mặc định)" : p.Sheet;
        var last = p.LastRunAt is { } t ? t.LocalDateTime.ToString("dd/MM HH:mm") : "—";
        Header = $"Sheet \"{sheetName}\" · {status} · đã xong tới dòng {p.LastRowReached} / tổng {p.TotalRowsAtLastRun} · " +
                 $"giữ {p.ReservedShopeeAccountIds.Count}/{p.ShopeeAccountCount} tk Shopee · lượt cuối {last}";
        RangesText = p.Completed.Count == 0
            ? "Đã cào: (chưa có)"
            : "Đã cào: " + string.Join(", ", p.Completed.Select(r => r.From == r.To ? $"{r.From}" : $"{r.From}–{r.To}"));

        foreach (var id in p.ReservedShopeeAccountIds)
        {
            var name = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id)?.DisplayName ?? id;
            Reserved.Add(new ReservedRow(p.Sheet, id, name));
        }
    }
}

public sealed partial class ScrapeStatsViewModel : ObservableObject
{
    private readonly string _accountId;

    public string Title { get; }
    public ObservableCollection<SheetProgressRow> Sheets { get; } = [];

    [ObservableProperty] private string _summary = "";

    public ScrapeStatsViewModel(string accountId, string accountName)
    {
        _accountId = accountId;
        Title = $"Thống kê scrape — BigSeller: {accountName}";
        Load();
    }

    public void Load()
    {
        Sheets.Clear();
        var items = ScrapeProgressStore.Shared.All()
            .Where(p => p.AccountId == _accountId)
            .OrderBy(p => p.Sheet)
            .ToList();
        foreach (var p in items) Sheets.Add(new SheetProgressRow(p));

        var reserved = items.Sum(p => p.ReservedShopeeAccountIds.Count);
        Summary = items.Count == 0
            ? "Chưa có lượt scrape nào cho tài khoản BigSeller này."
            : $"{items.Count} sheet có tiến độ · đang giữ tổng {reserved} tk Shopee.";
    }

    [RelayCommand] private void Refresh() => Load();

    /// <summary>Nhả TẤT CẢ tk Shopee đang giữ của 1 sheet (khi treo / muốn dùng tk cho BigSeller khác).</summary>
    [RelayCommand]
    private void ReleaseAll(SheetProgressRow row)
    {
        if (row is null) return;
        ScrapeProgressStore.Shared.ReleaseAllShopeeAccounts(_accountId, row.Sheet);
        Load();
    }

    /// <summary>Nhả 1 tk Shopee cụ thể.</summary>
    [RelayCommand]
    private void ReleaseOne(ReservedRow row)
    {
        if (row is null) return;
        ScrapeProgressStore.Shared.ReleaseShopeeAccount(_accountId, row.Sheet, row.ShopeeId);
        Load();
    }

    /// <summary>Xoá hẳn tiến độ của 1 sheet (lần Chạy/Tiếp tục sau sẽ coi như mới).</summary>
    [RelayCommand]
    private void ClearProgress(SheetProgressRow row)
    {
        if (row is null) return;
        ScrapeProgressStore.Shared.Clear(_accountId, row.Sheet);
        Load();
    }
}
