using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Components;

/// <summary>Số liệu ledger của MỘT việc (op) trên một shop — thẻ Thống kê + cột lưới của trang BigSeller đọc.</summary>
public sealed class FleetOpStat
{
    public string StatusText = "", StatusCss = "", Ranges = "—", Machine = "", LastAgo = "", LastAbs = "";
    public int RowCount, LastRow;
    public List<string> Machines = new();   // hostnames đã tham gia việc này
}

/// <summary>Một dòng shop của trang BigSeller: shop trong config (<c>InConfig</c>) hoặc shop mồ côi chỉ còn ở ledger.</summary>
public sealed class FleetShopRow
{
    public string AccountId = "", AccountName = "", ShopId = "", ShopName = "", Sheet = "";
    public bool InConfig;
    public Dictionary<string, FleetOpStat> Ops = new();
}

public sealed record FleetAcctGroup(string AccountId, string AccountName, List<FleetShopRow> Shops);

/// <summary>Số của hàng KPI + dòng tóm tắt trên trang BigSeller (dựng trong cùng một lượt quét để KPI khớp lưới).</summary>
public sealed record FleetSummary(int Machines, int Running, int Queued, string Text);

/// <summary>Dựng dòng/nhóm/tóm tắt cho trang BigSeller ("/") — thuần dữ liệu: không đọc DB, không giữ state UI.</summary>
public static class FleetRowsBuilder
{
    // Cột lưới + thẻ Tiến độ (4 op). Đếm running/queued/failed cho summary CHỈ 3 op hub-điều-phối.
    public static readonly string[] OpOrder =
        [AssignmentOps.Scrape, AssignmentOps.Import, AssignmentOps.Update, AssignmentOps.Rewrite];
    public static readonly string[] SummaryOps =
        [AssignmentOps.Scrape, AssignmentOps.Import, AssignmentOps.Update];

    public static List<FleetShopRow> BuildRows(List<BigSellerAccount> accounts, FleetSnapshot snap)
    {
        var byId = accounts.ToDictionary(a => a.Id, a => a);
        var shops = new Dictionary<string, FleetShopRow>(StringComparer.Ordinal);

        // 1) Từ config: mọi acct, MỌI shop (kể cả chưa gán sheet) → dòng danh sách (InConfig = true).
        //    Shop chưa sheet vẫn hiện (glyph ⚠) để đợt 2 còn cấu hình được — nhưng KHÔNG ghim/đặt-tay được.
        foreach (var shop in FleetViewProjection.ConfiguredShops(accounts))
        {
            shops[shop.Key] = new FleetShopRow
            {
                AccountId = shop.AccountId,
                AccountName = shop.AccountName,
                ShopId = shop.ShopId,
                ShopName = shop.ShopName,
                Sheet = shop.Sheet,
                InConfig = true,
            };
        }

        // 2) Overlay ledger: shop không còn trong config → dòng mồ côi (InConfig = false, chỉ xem tiến độ).
        foreach (var l in snap.Ledger)
        {
            if (string.IsNullOrEmpty(l.BigsellerId)) continue;   // (search dùng bảng khác, không nằm ở ledger)
            byId.TryGetValue(l.BigsellerId, out var acct);
            var key = l.BigsellerId + "__" + l.ShopId;
            if (!shops.TryGetValue(key, out var row))
            {
                var shop = acct?.Shops.FirstOrDefault(s => s.Id == l.ShopId);
                row = new FleetShopRow
                {
                    AccountId = l.BigsellerId,
                    AccountName = acct?.DisplayName ?? ShortId(l.BigsellerId),
                    ShopId = l.ShopId,
                    ShopName = shop?.DisplayName ?? (string.IsNullOrWhiteSpace(l.Sheet) ? ShortId(l.ShopId) : l.Sheet),
                    Sheet = !string.IsNullOrWhiteSpace(shop?.ShopeeDataSheet) ? shop!.ShopeeDataSheet : (l.Sheet ?? ""),
                    InConfig = false,
                };
                shops[key] = row;
            }
            var (stText, stCss) = LedgerCell(l.Status);
            var last = l.LastRunAt ?? l.UpdatedAt;
            row.Ops[l.Op] = new FleetOpStat
            {
                StatusText = stText, StatusCss = stCss,
                RowCount = CountRows(l.Completed), LastRow = l.LastRowReached,
                Ranges = FormatRanges(l.Completed), Machine = Machine(snap, l),
                LastAgo = FleetStateService.Ago(last),
                LastAbs = last == default ? "" : last.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss"),
                Machines = l.MachineIds.Select(m => HostName(snap, m)).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList(),
            };
        }

        return shops.Values
            .OrderBy(r => r.AccountName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.ShopName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // Nhóm shop theo account cho danh sách trái (gồm mồ côi + acc config chưa có shop nào), sắp theo tên acc.
    public static List<FleetAcctGroup> OrderedGroups(List<FleetShopRow> rows, List<BigSellerAccount> accounts)
    {
        var groups = rows
            .GroupBy(r => r.AccountId)
            .ToDictionary(g => g.Key, g => new FleetAcctGroup(g.Key, g.First().AccountName, g.ToList()));
        foreach (var a in accounts)
            if (!groups.ContainsKey(a.Id))
                groups[a.Id] = new FleetAcctGroup(a.Id, a.DisplayName, new List<FleetShopRow>());
        return groups.Values
            .OrderBy(g => g.AccountName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // Lọc danh sách trái theo ô tìm: acc khớp → cả nhóm; shop khớp → shop + dòng acc; không khớp → ẩn nhóm.
    public static IEnumerable<FleetAcctGroup> Filter(List<FleetAcctGroup> ordered, string? search)
    {
        var q = search?.Trim() ?? "";
        if (q.Length == 0) return ordered;
        var result = new List<FleetAcctGroup>();
        foreach (var g in ordered)
        {
            if (g.AccountName.Contains(q, StringComparison.OrdinalIgnoreCase))
                result.Add(g);
            else
            {
                var matched = g.Shops.Where(s => s.ShopName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matched.Count > 0) result.Add(g with { Shops = matched });
            }
        }
        return result;
    }

    public static FleetSummary Summarize(List<FleetShopRow> rows, FleetSnapshot snap)
    {
        var machines = snap.Machines.Count(FleetStateService.IsOnline);
        int running = 0, queued = 0, failed = 0;
        foreach (var r in rows)
        foreach (var op in SummaryOps)
        {
            var k = FleetStateService.OpCell(snap, r.AccountId, r.ShopId, op).Kind;
            if (k == 1) running++; else if (k == 2) queued++; else if (k == 3) failed++;
        }
        var tScrape = rows.Sum(r => r.Ops.GetValueOrDefault(AssignmentOps.Scrape)?.RowCount ?? 0);
        var tImport = rows.Sum(r => r.Ops.GetValueOrDefault(AssignmentOps.Import)?.RowCount ?? 0);
        var tUpdate = rows.Sum(r => r.Ops.GetValueOrDefault(AssignmentOps.Update)?.RowCount ?? 0);
        var tRewrite = rows.Sum(r => r.Ops.GetValueOrDefault(AssignmentOps.Rewrite)?.RowCount ?? 0);
        var text = $"{running} đang chạy · {queued} chờ · {failed} dừng/lỗi · {snap.Machines.Count} máy · "
            + $"{tScrape:N0} scrape · {tImport:N0} import · {tUpdate:N0} update · {tRewrite:N0} tên SP · cập nhật {DateTimeOffset.Now:HH:mm:ss}";
        // KPI phải KHỚP lưới → lấy từ cùng nguồn OpCell (không đếm Assignments.Status, field đó không mang "running").
        return new FleetSummary(machines, running, queued, text);
    }

    /// <summary>Tên máy cho trang BigSeller: hostname đang có trong fleet, cạn thì id RÚT GỌN (khác
    /// <see cref="FleetViewProjection.HostName"/> — trang Giao việc cố ý phô đủ id).</summary>
    public static string HostName(FleetSnapshot snap, string machineId)
    {
        if (string.IsNullOrEmpty(machineId)) return "";
        var m = snap.Machines.FirstOrDefault(x => x.MachineId == machineId);
        return m is not null && !string.IsNullOrWhiteSpace(m.Hostname) ? m.Hostname : ShortId(machineId);
    }

    public static string ShortId(string id) => string.IsNullOrEmpty(id) ? "?" : (id.Length <= 8 ? id : id[..8] + "…");

    private static string Machine(FleetSnapshot snap, WorkLedgerRecord l)
    {
        if (!string.IsNullOrEmpty(l.LastMachineId))
        {
            var m = snap.Machines.FirstOrDefault(x => x.MachineId == l.LastMachineId);
            if (m is not null && !string.IsNullOrWhiteSpace(m.Hostname)) return m.Hostname;
        }
        return string.IsNullOrWhiteSpace(l.LastHostname) ? "" : l.LastHostname;
    }

    /// <summary>Nhãn + lớp CSS cho một trạng thái sổ hoàn thành (tên KHÔNG được trùng lớp hằng
    /// <see cref="LedgerStatus"/> — trùng thì <c>LedgerStatus.Completed</c> trong file này không phân giải được).</summary>
    private static (string text, string css) LedgerCell(string s) => s switch
    {
        LedgerStatus.Completed => ("✓ xong", "done"),
        LedgerStatus.Stopped => ("■ dừng dở", "warn"),
        LedgerStatus.Running => ("⏳ đang chạy", "run"),
        _ => ("· chưa xong", "idle"),
    };

    private static int CountRows(List<RowRange> rr) => rr.Sum(r => Math.Max(0, r.To - r.From + 1));

    private static string FormatRanges(List<RowRange> rr) => rr.Count == 0
        ? "—"
        : string.Join(", ", rr.OrderBy(r => r.From).Select(r => r.From == r.To ? $"{r.From}" : $"{r.From}–{r.To}"));
}
