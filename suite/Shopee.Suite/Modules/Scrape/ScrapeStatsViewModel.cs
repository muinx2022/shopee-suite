using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
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

    /// <summary>Số dòng trong sổ bỏ-qua. 0 → nút "Cào lại dòng đã bỏ" ẩn hẳn (không có gì để mở lại).</summary>
    public int SkippedCount { get; }
    public bool CanReopenSkipped => SkippedCount > 0;
    public string ReopenSkippedLabel => $"Cào lại dòng đã bỏ ({SkippedCount})";

    public SheetProgressRow(ScrapeProgress p, IReadOnlyList<string> frameLabels)
    {
        Sheet = p.Sheet;
        SkippedCount = p.SkippedRows.Count;
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

    /// <summary>
    /// CÀO LẠI các dòng đã bỏ qua của 1 sheet: khoét chúng khỏi vùng phủ để lượt Tiếp tục nhặt lại — nhẹ hơn
    /// hẳn "Xoá tiến độ" (vốn bắt cào lại cả sheet).
    /// <para>THỨ TỰ BẮT BUỘC: mở trên HUB TRƯỚC, sửa local SAU. Làm ngược mà Hub fail thì lượt fold ledger kế
    /// tiếp (mở app / trước resume) phủ lại đúng các dòng vừa mở → nút thành no-op im lặng còn user thì tưởng
    /// đã cào lại. Hub fail ⇒ KHÔNG đụng local + nói rõ trong Summary.</para>
    /// <para>Tra shop theo sheet như <see cref="ScrapeProgressReset"/>: UI không chặn nhiều shop cùng trỏ một
    /// sheet nên phải mở CẢ (sót một là ledger đó fold tiến độ cũ về lại).</para>
    /// </summary>
    [RelayCommand]
    private async Task ReopenSkippedAsync(SheetProgressRow row)
    {
        if (row is null || row.SkippedCount == 0) return;
        var sheet = row.Sheet ?? "";
        // Sổ dòng của LOCAL — gửi kèm cho hub union (dòng bỏ trước khi hub có cột skipped chỉ còn dấu ở đây),
        // và cũng là thứ còn nguyên nếu phải dừng giữa chừng → bấm lại vẫn mở được đủ.
        var localRows = ScrapeProgressStore.Shared.Find(_accountId, sheet)?.SkippedRows ?? [];

        if (CoordinationRuntime.Hub is { } hub)
        {
            var account = BigSellerStore.Shared.Find(_accountId);
            var shops = account?.Shops.Where(s =>
                string.Equals(s.ShopeeDataSheet ?? "", sheet, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
            if (shops.Count == 0)
            {
                Summary = $"⚠ Không tìm thấy shop nào khớp sheet \"{sheet}\" — chưa mở lại được trên Hub, lượt " +
                          "Tiếp tục có thể phủ lại các dòng này. Chưa đổi gì trên máy này.";
                return;
            }
            // Gọi HẾT vòng rồi mới kết luận (không return giữa chừng): dừng ở shop fail đầu tiên thì các shop
            // ĐÃ mở trước đó nằm ở trạng thái lệch mà thông báo lại nói "chưa đổi gì" — sai. Fail bất kỳ →
            // local giữ nguyên (sổ local còn → bấm lại gửi kèm danh sách dòng nên mở tiếp được, kể cả cho shop
            // mà hub đã xoá sổ ở lần bấm trước).
            var failed = new List<string>();
            foreach (var shop in shops)
            {
                var ok = await hub.ReopenSkippedAsync(new CoordKey(_accountId, shop.Id, sheet, CoordOp.Scrape), localRows);
                if (!ok) failed.Add(shop.DisplayName);
            }
            if (failed.Count > 0)
            {
                var okCount = shops.Count - failed.Count;
                Summary = $"⚠ Hub chưa mở lại được cho: {string.Join(", ", failed)}" +
                          (okCount > 0 ? $" (đã mở cho {okCount} shop khác)" : "") +
                          " — máy này GIỮ NGUYÊN sổ dòng bỏ qua; bấm lại khi có mạng (bấm lại an toàn).";
                return;
            }
        }

        var reopened = ScrapeProgressStore.Shared.ReopenSkipped(_accountId, sheet);
        Load();   // Load() ghi đè Summary → câu báo kết quả phải đặt SAU.
        Summary = $"Đã mở lại {reopened} dòng đã bỏ của sheet \"{(string.IsNullOrWhiteSpace(sheet) ? "(mặc định)" : sheet)}\" " +
                  "— bấm Tiếp tục để cào nốt các dòng này.";
    }
}
