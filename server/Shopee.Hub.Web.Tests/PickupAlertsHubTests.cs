using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Hub DB banner lỗi địa chỉ: dismiss tạo tombstone; mọi lượt ghi tăng <c>rev</c> (client merge theo số này,
/// KHÔNG theo mốc thời gian — xem xmldoc <c>HubDatabase</c> phần pickup alerts).
/// </summary>
public sealed class PickupAlertsHubTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-pickup-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Dismiss_KhiChuaCoDong_TaoTombstoneDismissed()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.Equal(1, db.DismissPickupAlert("acc@x", "shop.a", "machine-1"));

        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.Equal("shop.a", row.ShopLogin);
        Assert.False(string.IsNullOrEmpty(row.DismissedAt));
        Assert.Equal(1, row.Rev);
    }

    /// <summary>Mỗi lượt ghi (bất kể upsert hay dismiss) tăng rev đúng 1 — đây là "đồng hồ" duy nhất của fleet.</summary>
    [Fact]
    public void MoiLuotGhi_TangRevDungMot()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.Equal(1, db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1"));
        Assert.Equal(2, db.DismissPickupAlert("acc@x", "shop.a", "m1"));
        Assert.Equal(3, db.UpsertPickupAlert("acc@x", "shop.a", "HN", "m2"));

        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.Equal(3, row.Rev);
        Assert.True(string.IsNullOrEmpty(row.DismissedAt)); // lượt ghi CUỐI thắng
        Assert.Equal("HN", row.Province);
    }

    /// <summary>Bấm X sau cùng → banner đóng, rev cao nhất; không có luật nào cho phép Hub từ chối dismiss.</summary>
    [Fact]
    public void DismissSauCung_DongBanner()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1");
        var rev = db.DismissPickupAlert("acc@x", "shop.a", "m2");

        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.False(string.IsNullOrEmpty(row.DismissedAt));
        Assert.Equal(rev, row.Rev);
    }

    /// <summary>Lỗi còn thật → vòng shop kế upsert ⇒ rev tăng, banner hiện lại ở mọi máy.</summary>
    [Fact]
    public void SauDismiss_VongKeUpsert_BannerHienLaiVaRevTang()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1");
        var revDismiss = db.DismissPickupAlert("acc@x", "shop.a", "m1");
        var revUpsert = db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1");

        Assert.True(revUpsert > revDismiss);
        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.True(string.IsNullOrEmpty(row.DismissedAt));
    }

    [Fact]
    public void ThieuKhoa_TraKhong_KhongGhiGi()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.Equal(0, db.UpsertPickupAlert("", "shop.a", "TH", "m1"));
        Assert.Equal(0, db.DismissPickupAlert("acc@x", "  ", "m1"));
        Assert.Empty(db.ListPickupAlerts("acc@x"));
    }

    /// <summary>Rev tách theo từng shop, không dùng chung bộ đếm toàn bảng.</summary>
    [Fact]
    public void Rev_RiengTungShop()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1");
        db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1");
        Assert.Equal(1, db.UpsertPickupAlert("acc@x", "shop.b", "TH", "m1"));

        var rows = db.ListPickupAlerts("acc@x");
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Single(x => x.ShopLogin == "shop.a").Rev);
        Assert.Equal(1, rows.Single(x => x.ShopLogin == "shop.b").Rev);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp */ }
    }
}
