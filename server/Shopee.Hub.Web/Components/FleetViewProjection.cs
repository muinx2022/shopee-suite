using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;

namespace Shopee.Hub.Web.Components;

public sealed record ConfiguredShopView(
    string Key,
    string AccountId,
    string AccountName,
    string ShopId,
    string ShopName,
    string Sheet)
{
    public bool HasSheet => !string.IsNullOrWhiteSpace(Sheet);
}

/// <summary>Projection thuan dung chung cho Fleet va Dispatch; khong doc DB, khong giu state UI.</summary>
public static class FleetViewProjection
{
    public static List<ConfiguredShopView> ConfiguredShops(IEnumerable<BigSellerAccount> accounts)
    {
        var rows = new List<ConfiguredShopView>();
        foreach (var account in accounts)
        foreach (var shop in account.Shops)
            rows.Add(new ConfiguredShopView(
                account.Id + "__" + shop.Id,
                account.Id,
                account.DisplayName,
                shop.Id,
                shop.DisplayName,
                shop.ShopeeDataSheet ?? ""));
        return rows;
    }

    /// <summary>Tên máy để hiển thị: hostname máy đang có trong fleet, cạn thì chính id (KHÔNG rút gọn — trang
    /// Giao việc cố ý phô đủ id để còn đối chiếu). Id rỗng → chuỗi rỗng.</summary>
    public static string HostName(FleetSnapshot snap, string machineId)
    {
        if (string.IsNullOrEmpty(machineId)) return "";
        var m = snap.Machines.FirstOrDefault(x => x.MachineId == machineId);
        return m is not null && !string.IsNullOrWhiteSpace(m.Hostname) ? m.Hostname : machineId;
    }

    public static string OperationLabel(string op) => op switch
    {
        AssignmentOps.Scrape => "Scrape",
        AssignmentOps.Import => "Import",
        AssignmentOps.Update => "Update",
        AssignmentOps.Rewrite => "Tên SP",
        AssignmentOps.Orders => "📦 Đơn hàng",
        _ => op,
    };
}
