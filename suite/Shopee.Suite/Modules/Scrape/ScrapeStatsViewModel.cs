using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>Tiến độ scrape của 1 sheet thuộc tk BigSeller đang xem.</summary>
public sealed class SheetProgressRow
{
    public string Sheet { get; }
    public string Header { get; }
    public string RangesText { get; }
    /// <summary>Danh sách tk Shopee được "khoanh vùng" (khung) cho tk BigSeller này ở sheet đó.</summary>
    public string FrameText { get; }

    public SheetProgressRow(ScrapeProgress p, IReadOnlyList<string> frameLabels)
    {
        Sheet = p.Sheet;
        var status = p.Status switch
        {
            LedgerStatus.Completed => "✔ Hoàn thành",
            LedgerStatus.Running => "▶ Đang chạy",
            LedgerStatus.Stopped => "■ Dừng giữa chừng",
            _ => "—",
        };
        var sheetName = string.IsNullOrWhiteSpace(p.Sheet) ? "(mặc định)" : p.Sheet;
        var last = p.LastRunAt is { } t ? t.LocalDateTime.ToString("dd/MM HH:mm") : "—";
        // "bỏ n dòng" = dòng trong vùng phủ nhưng KHÔNG cào được (skip-ledger). Liệt kê hẳn số dòng ở
        // RangesText để user biết chính xác thiếu SP nào mà quyết định có Chạy (reset) lại không.
        var skip = p.SkippedRows.Count > 0 ? $" · bỏ {p.SkippedRows.Count} dòng" : "";
        Header = $"Sheet \"{sheetName}\" · {status} · đã xong tới dòng {p.LastRowReached}{skip} / tổng {p.TotalRowsAtLastRun} · lượt cuối {last}";
        RangesText = (p.Completed.Count == 0
            ? "Đã cào: (chưa có)"
            : "Đã cào: " + string.Join(", ", p.Completed.Select(r => r.From == r.To ? $"{r.From}" : $"{r.From}–{r.To}")))
            + (p.SkippedRows.Count == 0
                ? ""
                : $"   ⚠ Bỏ qua (không cào được): {string.Join(", ", p.SkippedRows)}");
        FrameText = frameLabels.Count == 0
            ? "Khung tk Shopee: (chưa cấp — lần Chạy đầu sẽ khoanh vùng)"
            : $"Khung tk Shopee ({frameLabels.Count}): " + string.Join(", ", frameLabels);
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
        foreach (var p in items)
        {
            // Resolve id tk Shopee trong khung → tên hiển thị; tk đã bị tắt/loại (captcha) đánh dấu "(lỗi)".
            var labels = p.FrameAccountIds
                .Select(id => AccountStore.Shared.Find(id) is { } a
                    ? (a.Disabled ? $"{a.DisplayName} (lỗi)" : a.DisplayName)
                    : "(tk đã xoá)")
                .ToList();
            Sheets.Add(new SheetProgressRow(p, labels));
        }

        Summary = items.Count == 0
            ? "Chưa có lượt scrape nào cho tài khoản BigSeller này."
            : $"{items.Count} sheet có tiến độ. Mỗi tk BigSeller được KHOANH VÙNG một bộ tk Shopee cố định (chỉ xoay vòng trong khung → không bị đá phiên).";
    }

    [RelayCommand] private void Refresh() => Load();

    /// <summary>Xoá hẳn tiến độ của 1 sheet (lần Chạy/Tiếp tục sau sẽ coi như mới). Đi qua
    /// <see cref="ScrapeProgressReset"/> — cùng đường với nút Chạy (reset) — để xoá CẢ ledger Hub: trước đây
    /// chỗ này chỉ Clear local nên khi có Hub, "Xoá tiến độ" là no-op (lượt sau fold ledger kéo tiến độ cũ về).</summary>
    [RelayCommand]
    private void ClearProgress(SheetProgressRow row)
    {
        if (row is null) return;
        string? note = null;
        ScrapeProgressReset.Reset(_accountId, row.Sheet, m => note = m);
        Load();
        if (note is not null) Summary = note;
    }
}
