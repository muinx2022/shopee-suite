extern alias ordersCore;

using System.Text.Json;
using Shopee.Core.Coordination;
using Shopee.Hub;
using MergeAction = ordersCore::XuLyDonShopee.Core.Services.MergePickupAlertAction;
using PickupMerge = ordersCore::XuLyDonShopee.Core.Services.PickupAlertMerge;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// HỢP ĐỒNG API với client ĐỜI CŨ (≤ v1.7.18) — đừng gỡ hai field này khi trên fleet còn máy chưa cập nhật.
/// Client cũ merge banner bằng <c>createdAt</c>/<c>dismissedAt</c>; thiếu là chúng rơi vào nhánh "Hub thiếu
/// mốc → đẩy lại dismiss cũ" và dập chết banner của lỗi vừa phát hiện. Đã từng lỡ gỡ và phải deploy vá gấp.
/// </summary>
public sealed class PickupAlertsApiContractTests
{
    [Fact]
    public void OrdersPickupAlertItem_VanCoCreatedAtVaDismissedAt()
    {
        // Dùng đúng bộ tuỳ chọn của minimal API (Results.Json → JsonSerializerDefaults.Web, camelCase).
        var json = JsonSerializer.Serialize(
            new OrdersPickupAlertItem
            {
                ShopLogin = "shop.a",
                Province = "TH",
                Dismissed = true,
                Rev = 3,
                CreatedAt = "2026-08-04T04:00:00.0000000+00:00",
                DismissedAt = "2026-08-04T05:00:00.0000000+00:00",
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"createdAt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dismissedAt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"rev\"", json, StringComparison.Ordinal);
    }
}

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

    // ── Section "Cảnh báo địa chỉ" trên web hub (H1.2) ───────────────────────────
    /// <summary>Nguồn của section: CHỈ banner đang mở, gộp mọi tài khoản, kèm máy báo + rev.</summary>
    [Fact]
    public void ActivePickupAlerts_ChiDongDangMo_GopMoiTaiKhoan()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertPickupAlert("acc@x", "shop.a", "TH", "may-1");
        db.UpsertPickupAlert("acc@y", "shop.b", "HN", "may-2");
        db.UpsertPickupAlert("acc@x", "shop.c", "DN", "may-1");
        db.DismissPickupAlert("acc@x", "shop.c", "may-1");   // đã xử → phải biến khỏi section

        var rows = db.ActivePickupAlerts();

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.ShopLogin == "shop.c");
        var a = rows.Single(r => r.ShopLogin == "shop.a");
        Assert.Equal("acc@x", a.AccountLogin);
        Assert.Equal("TH", a.Province);
        Assert.Equal("may-1", a.UpdatedByMachine);
        Assert.Equal(1, a.Rev);
    }

    /// <summary>Lỗi còn thật → vòng shop kế upsert lại ⇒ dòng QUAY LẠI section (tombstone không khoá vĩnh viễn).</summary>
    [Fact]
    public void ActivePickupAlerts_SauDismissRoiUpsertLai_HienLai()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertPickupAlert("acc@x", "shop.a", "TH", "may-1");
        db.DismissPickupAlert("acc@x", "shop.a", "hub-web");
        Assert.Empty(db.ActivePickupAlerts());

        db.UpsertPickupAlert("acc@x", "shop.a", "TH", "may-1");

        Assert.Equal("shop.a", Assert.Single(db.ActivePickupAlerts()).ShopLogin);
    }

    /// <summary>
    /// CHỐT CHẶN của H1.2: admin bấm "✓ Đã xử lý" trên web đi ĐÚNG hàm <see cref="HubDatabase.DismissPickupAlert"/>
    /// mà endpoint của client gọi ⇒ máy đang chạy tài khoản đó, khi merge lượt kế bằng ĐÚNG luật của client
    /// (<c>PickupAlertMerge.QuyetDinh</c> — so <c>rev</c>, KHÔNG so mốc giờ), phải quyết định NHẬN trạng thái Hub
    /// và tắt banner. Không có đường ghi riêng nào cho web.
    /// </summary>
    [Fact]
    public void DismissTuWeb_ClientMergeTheoRev_TatBanner()
    {
        using var db = new HubDatabase(_dataDir);
        // Máy client phát hiện lỗi và đã đồng bộ xong (local nhớ rev này, không còn gì chờ đẩy).
        var revMay = db.UpsertPickupAlert("acc@x", "shop.a", "TH", "may-1");

        // Admin đóng banner từ web — CÙNG hàm mà POST /orders/pickup-alerts/dismiss gọi.
        var revWeb = db.DismissPickupAlert("acc@x", "shop.a", "hub-web");
        Assert.True(revWeb > revMay, "dismiss phải tăng rev thì máy khác mới nhận ra là bản mới hơn");

        var hub = Assert.Single(db.ListPickupAlerts("acc@x"));   // đúng payload GET mà client kéo về
        Assert.False(string.IsNullOrEmpty(hub.DismissedAt));

        var quyetDinh = PickupMerge.QuyetDinh(
            localCoDong: true, localChoDay: false, localHubRev: revMay,
            hubCoDong: true, hubRev: hub.Rev);

        Assert.Equal(MergeAction.TheoHub, quyetDinh);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp */ }
    }
}
