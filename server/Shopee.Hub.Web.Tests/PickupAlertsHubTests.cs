using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>Hub DB: dismiss tạo tombstone; upsert cũ hơn dismissed_at không clear dismiss.</summary>
public sealed class PickupAlertsHubTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-pickup-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Dismiss_KhiChuaCoDong_TaoTombstoneDismissed()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.True(db.DismissPickupAlert("acc@x", "shop.a", "machine-1", "2026-08-04T05:00:00Z"));

        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.Equal("shop.a", row.ShopLogin);
        Assert.False(string.IsNullOrEmpty(row.DismissedAt));
    }

    [Fact]
    public void UpsertCu_SauDismiss_KhongClearDismiss()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.True(db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1", "2026-08-04T04:00:00Z"));
        Assert.True(db.DismissPickupAlert("acc@x", "shop.a", "m1", "2026-08-04T04:40:00Z"));

        // Upsert "chậm" với OccurredAt trước dismissed_at → giữ dismiss.
        Assert.True(db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1", "2026-08-04T04:10:00Z"));
        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.False(string.IsNullOrEmpty(row.DismissedAt));
    }

    [Fact]
    public void UpsertMoi_SauDismiss_HienLaiBanner()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.True(db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1", "2026-08-04T04:00:00Z"));
        Assert.True(db.DismissPickupAlert("acc@x", "shop.a", "m1", "2026-08-04T04:40:00Z"));

        Assert.True(db.UpsertPickupAlert("acc@x", "shop.a", "HN", "m1", "2026-08-04T05:00:00Z"));
        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.True(string.IsNullOrEmpty(row.DismissedAt));
        Assert.Equal("HN", row.Province);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp */ }
    }
}
