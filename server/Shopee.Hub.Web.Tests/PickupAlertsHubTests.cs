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

    /// <summary>
    /// Bấm X LUÔN thắng, kể cả khi mốc dismiss cũ hơn <c>created_at</c> (hai máy lệch đồng hồ).
    /// Cố ý: so mốc chéo máy ở đây từng làm Hub từ chối dismiss vĩnh viễn → banner không gỡ được.
    /// </summary>
    [Fact]
    public void DismissMocCuHonCreatedAt_VanDong()
    {
        using var db = new HubDatabase(_dataDir);
        // Máy phát hiện lỗi chạy nhanh giờ → created_at 05:00; máy bấm X giờ đúng → dismiss 04:00.
        Assert.True(db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1", "2026-08-04T05:00:00Z"));
        Assert.True(db.DismissPickupAlert("acc@x", "shop.a", "m2", "2026-08-04T04:00:00Z"));

        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.False(string.IsNullOrEmpty(row.DismissedAt));
    }

    /// <summary>Lỗi còn thật → vòng shop kế upsert mốc mới hơn dismissed_at ⇒ banner tự hiện lại (ca tự lành).</summary>
    [Fact]
    public void SauDismiss_VongKeUpsertMocMoi_BannerHienLai()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.True(db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1", "2026-08-04T04:00:00Z"));
        Assert.True(db.DismissPickupAlert("acc@x", "shop.a", "m1", "2026-08-04T04:40:00Z"));
        Assert.True(db.UpsertPickupAlert("acc@x", "shop.a", "TH", "m1", "2026-08-04T04:45:00Z"));

        var row = Assert.Single(db.ListPickupAlerts("acc@x"));
        Assert.True(string.IsNullOrEmpty(row.DismissedAt));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp */ }
    }
}
