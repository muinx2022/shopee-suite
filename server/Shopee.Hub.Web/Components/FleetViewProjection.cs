using Shopee.Core.BigSeller;

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

    public static string OperationLabel(string op) => op switch
    {
        "scrape" => "Scrape",
        "import" => "Import",
        "update" => "Update",
        "rewrite" => "Tên SP",
        "orders" => "📦 Đơn hàng",
        _ => op,
    };
}
