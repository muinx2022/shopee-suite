using Shopee.Core.BigSeller;
using Shopee.Hub.Web.Components;

namespace Shopee.Hub.Web.Tests;

public sealed class FleetViewProjectionTests
{
    [Fact]
    public void ConfiguredShops_PreservesAccountShopAndMissingSheet()
    {
        var account = new BigSellerAccount
        {
            Id = "acct-1",
            Label = "Account 1",
            Shops =
            [
                new BigSellerShop { Id = "shop-1", Name = "Shop 1", ShopeeDataSheet = "sheet-1" },
                new BigSellerShop { Id = "shop-2", Name = "Shop 2", ShopeeDataSheet = "" },
            ],
        };

        var rows = FleetViewProjection.ConfiguredShops([account]);

        Assert.Collection(rows,
            first =>
            {
                Assert.Equal("acct-1__shop-1", first.Key);
                Assert.True(first.HasSheet);
                Assert.Equal("sheet-1", first.Sheet);
            },
            second =>
            {
                Assert.Equal("acct-1__shop-2", second.Key);
                Assert.False(second.HasSheet);
            });
    }

    [Theory]
    [InlineData("scrape", "Scrape")]
    [InlineData("rewrite", "Tên SP")]
    [InlineData("orders", "📦 Đơn hàng")]
    [InlineData("custom", "custom")]
    public void OperationLabel_UsesOneSharedMapping(string operation, string expected)
        => Assert.Equal(expected, FleetViewProjection.OperationLabel(operation));
}
