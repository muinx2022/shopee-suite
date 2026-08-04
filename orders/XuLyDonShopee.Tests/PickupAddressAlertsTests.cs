using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>Test kho banner lỗi địa chỉ + tách tên shop + kịch bản shop đầu lỗi → dừng shop + banner + dấu X.</summary>
public class PickupAddressAlertsTests
{
    private const string ShopDau = "alina99.store";
    private const string ShopHai = "shop9x.store";

    [Fact]
    public void Upsert_ListActive_Dismiss_HienLaiSauDismiss()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.Upsert(1, "alina99.store", "Thanh Hóa");
        var one = Assert.Single(repo.ListActive(1));
        Assert.Equal("alina99.store", one.ShopLogin);
        Assert.Equal("Thanh Hóa", one.Province);
        Assert.Null(one.DismissedAt);

        repo.Dismiss(1, "alina99.store");
        Assert.Empty(repo.ListActive(1));

        repo.Upsert(1, "alina99.store", "Hà Nội");
        var again = Assert.Single(repo.ListActive(1));
        Assert.Equal("Hà Nội", again.Province);
        Assert.Null(again.DismissedAt);
    }

    [Fact]
    public void ListActive_TachTheoTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.Upsert(1, "shop-a", null);
        repo.Upsert(2, "shop-b", null);

        Assert.Equal("shop-a", Assert.Single(repo.ListActive(1)).ShopLogin);
        Assert.Equal("shop-b", Assert.Single(repo.ListActive(2)).ShopLogin);
    }

    [Fact]
    public void Upsert_NhieuShop_ListActive_DuDong()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.Upsert(1, "a.store", "TH");
        repo.Upsert(1, "b.store", "TH");
        Assert.Equal(2, repo.ListActive(1).Count);
    }

    [Fact]
    public void TachTenShop_NullHoacRong_KhongRoShop()
    {
        Assert.Equal(["(không rõ shop)"], OrderPersistPipeline.TachTenShopLoiDiaChi(null));
        Assert.Equal(["(không rõ shop)"], OrderPersistPipeline.TachTenShopLoiDiaChi("  "));
    }

    [Fact]
    public void TachTenShop_NoiBangPhay_TachVaKhuTrung()
    {
        var list = OrderPersistPipeline.TachTenShopLoiDiaChi("a.store, b.store, A.store");
        Assert.Equal(2, list.Count);
        Assert.Contains("a.store", list);
        Assert.Contains("b.store", list);
    }

    /// <summary>
    /// Kịch bản test chính: shop ĐẦU TIÊN lỗi địa chỉ → dừng shop đó (không in phiếu) + ghi banner;
    /// shop hai vẫn có thể chạy (không bị dính cờ lỗi). UI: banner + dấu X đỏ trên dòng shop đầu.
    /// </summary>
    [Fact]
    public void ShopDauLoiDiaChi_DungShop_HienBannerVaDauXDo()
    {
        // 1) Luật dừng shop: pickupOk=false → DungViDiaChi (tuyệt đối không XuDon / không in phiếu).
        Assert.Equal(SauDatDiaChi.DungViDiaChi,
            ShopFlowRunner.QuyetDinhSauDatDiaChi(pickupOk: false, captchaSeen: false));
        Assert.NotEqual(SauDatDiaChi.XuDon,
            ShopFlowRunner.QuyetDinhSauDatDiaChi(pickupOk: false, captchaSeen: false));

        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "hoangdh200392", Password = "p" });

        var vm = new AccountsViewModel(services);
        var accountId = vm.Accounts.First().Id;
        services.Results.UpsertShops(accountId, new[]
        {
            new ShopListItem("111", "Alina Store1", ShopDau),
            new ShopListItem("222", "Shop 9X", ShopHai),
        });
        vm.SelectedRow = vm.Accounts.First();
        Assert.Equal(2, vm.ResultRows.Count);
        Assert.Empty(vm.AddressAlertRows);

        // 2) Giả lập kết quả vòng: shop đầu fail → GhiBanner (cùng đường AccountSession gọi khi PickupAddressFailed).
        var pipeline = new OrderPersistPipeline(accountId, services);
        pipeline.GhiBannerLoiDiaChi(ShopDau, "Thanh Hóa", _ => { }, CancellationToken.None);

        // 3) Banner hiện đúng 1 dòng cho shop đầu.
        var banner = Assert.Single(vm.AddressAlertRows);
        Assert.Equal(ShopDau, banner.ShopLogin);
        Assert.Contains(ShopDau, banner.Message);
        Assert.StartsWith("Cảnh báo: Lỗi địa chỉ", banner.Message);

        // 4) Dấu X đỏ: shop đầu CoLoiDiaChi; shop hai sạch.
        var rowDau = vm.ResultRows.Single(r => r.ShopLogin == ShopDau);
        var rowHai = vm.ResultRows.Single(r => r.ShopLogin == ShopHai);
        Assert.True(rowDau.CoLoiDiaChi);
        Assert.True(rowDau.ShowLoiDiaChi);
        Assert.False(rowHai.CoLoiDiaChi);
        Assert.False(rowHai.ShowLoiDiaChi);

        // 5) Sau khi "check xong" shop đầu (bỏ qua): tick bị X đỏ che — không hiện tick xanh cạnh lỗi.
        services.RaiseShopCheckChanged(accountId, ShopDau, checking: true);
        services.RaiseShopCheckChanged(accountId, ShopDau, checking: false);
        Assert.True(rowDau.DaKiemTra);
        Assert.True(rowDau.ShowLoiDiaChi);
        Assert.False(rowDau.ShowTick);

        // 6) Shop hai vẫn chạy được (bắt đầu check) — không bị dính lỗi của shop đầu.
        services.RaiseShopCheckChanged(accountId, ShopHai, checking: true);
        Assert.True(rowHai.IsChecking);
        Assert.False(rowHai.CoLoiDiaChi);

        // 7) Kết quả vòng: PickupAddressFailed + tên shop đầu (hợp đồng AccountSession đọc).
        var result = new OrdersBridgeRunResult(
            ShopCount: 2, ShopsDone: 1, TotalOrders: 0, TotalSlips: 0,
            Captcha: false, Error: null,
            PickupAddressFailed: true, PickupFailedShop: ShopDau);
        Assert.True(result.PickupAddressFailed);
        Assert.Equal(ShopDau, result.PickupFailedShop);
    }

    [Fact]
    public void ShopPrepareRow_CoLoiDiaChi_UuTienXDoHonTick()
    {
        var row = new ShopPrepareRow("Alina", ShopDau, 0)
        {
            DaKiemTra = true,
            CoLoiDiaChi = true,
        };
        Assert.True(row.ShowLoiDiaChi);
        Assert.False(row.ShowTick);

        row.CoLoiDiaChi = false;
        Assert.False(row.ShowLoiDiaChi);
        Assert.True(row.ShowTick);
    }

    [Fact]
    public void DismissBanner_TatDauXTrenDongShop()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        var accountId = vm.Accounts.First().Id;
        services.Results.UpsertShops(accountId, new[]
        {
            new ShopListItem("111", "Alina", ShopDau),
        });
        vm.SelectedRow = vm.Accounts.First();

        new OrderPersistPipeline(accountId, services)
            .GhiBannerLoiDiaChi(ShopDau, "TH", _ => { }, CancellationToken.None);
        Assert.True(vm.ResultRows.Single().ShowLoiDiaChi);

        vm.DismissAddressAlertCommand.Execute(vm.AddressAlertRows.Single());
        Assert.Empty(vm.AddressAlertRows);
        Assert.False(vm.ResultRows.Single().CoLoiDiaChi);
        Assert.False(vm.ResultRows.Single().ShowLoiDiaChi);
    }

    // ── Merge Hub ↔ local: mốc thời gian mới hơn thắng, HAI CHIỀU ──────────────────────────────────────
    private static readonly DateTimeOffset HubDismiss0440 = new(2026, 8, 4, 4, 40, 0, TimeSpan.Zero);

    /// <summary>Máy khác bấm X SAU khi banner sinh ra → lan dismiss (luồng chính của tính năng).</summary>
    [Fact]
    public void Merge_HubDismissMoiHonBannerLocal_LocalDismiss()
        => Assert.Equal(MergePickupAlertAction.LocalDismiss,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: new DateTime(2026, 8, 4, 4, 0, 0, DateTimeKind.Utc),
                localDismissedAt: null,
                hubDismissed: true,
                hubCreatedAt: new DateTimeOffset(2026, 8, 4, 4, 0, 0, TimeSpan.Zero),
                hubDismissedAt: HubDismiss0440));

    /// <summary>Lỗi MỚI phát hiện lúc Hub chết (upsert Hub fail) + Hub còn tombstone cũ → GIỮ banner, sửa Hub.</summary>
    [Fact]
    public void Merge_BannerLocalMoiHonTombstoneHub_KeepActiveVaRepush()
        => Assert.Equal(MergePickupAlertAction.KeepLocalActiveRepushHub,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: new DateTime(2026, 8, 4, 5, 0, 0, DateTimeKind.Utc),
                localDismissedAt: null,
                hubDismissed: true,
                hubCreatedAt: new DateTimeOffset(2026, 8, 4, 4, 0, 0, TimeSpan.Zero),
                hubDismissedAt: HubDismiss0440));

    /// <summary>Local cũng đã đóng rồi → cứ dismiss, không có gì để giữ.</summary>
    [Fact]
    public void Merge_HubDismissed_LocalDaDismiss_LocalDismiss()
        => Assert.Equal(MergePickupAlertAction.LocalDismiss,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: new DateTime(2026, 8, 4, 5, 0, 0, DateTimeKind.Utc),
                localDismissedAt: new DateTime(2026, 8, 4, 5, 10, 0, DateTimeKind.Utc),
                hubDismissed: true,
                hubCreatedAt: null,
                hubDismissedAt: HubDismiss0440));

    /// <summary>Local chưa có dòng nào → nghe Hub (không dựng banner từ hư không).</summary>
    [Fact]
    public void Merge_HubDismissed_LocalChuaCoDong_LocalDismiss()
        => Assert.Equal(MergePickupAlertAction.LocalDismiss,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: null,
                localDismissedAt: null,
                hubDismissed: true,
                hubCreatedAt: null,
                hubDismissedAt: HubDismiss0440));

    /// <summary>Hub báo dismissed nhưng THIẾU mốc → không giữ bừa banner local.</summary>
    [Fact]
    public void Merge_HubDismissed_ThieuDismissedAt_LocalDismiss()
        => Assert.Equal(MergePickupAlertAction.LocalDismiss,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: new DateTime(2026, 8, 4, 5, 0, 0, DateTimeKind.Utc),
                localDismissedAt: null,
                hubDismissed: true,
                hubCreatedAt: null,
                hubDismissedAt: null));

    /// <summary>Mốc local đời cũ (Kind=Unspecified) coi như UTC, không lệch múi giờ máy.</summary>
    [Fact]
    public void Merge_MocLocalKhongKind_CoiNhuUtc()
        => Assert.Equal(MergePickupAlertAction.KeepLocalActiveRepushHub,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: new DateTime(2026, 8, 4, 5, 0, 0, DateTimeKind.Unspecified),
                localDismissedAt: null,
                hubDismissed: true,
                hubCreatedAt: null,
                hubDismissedAt: HubDismiss0440));

    [Fact]
    public void Merge_LocalDismissMoiHonHubActive_KeepVaRepush()
    {
        var hubCreated = new DateTimeOffset(2026, 8, 4, 4, 0, 0, TimeSpan.Zero);
        var localDismiss = new DateTime(2026, 8, 4, 4, 40, 0, DateTimeKind.Utc);
        Assert.Equal(MergePickupAlertAction.KeepLocalDismissRepushHub,
            PickupAlertMerge.QuyetDinh(null, localDismiss, hubDismissed: false, hubCreated, null));
    }

    [Fact]
    public void Merge_HubActiveMoiHonLocalDismiss_LocalUpsert()
    {
        var localDismiss = new DateTime(2026, 8, 4, 4, 0, 0, DateTimeKind.Utc);
        var hubCreated = new DateTimeOffset(2026, 8, 4, 5, 0, 0, TimeSpan.Zero);
        Assert.Equal(MergePickupAlertAction.LocalUpsert,
            PickupAlertMerge.QuyetDinh(null, localDismiss, hubDismissed: false, hubCreated, null));
    }

    [Fact]
    public void Merge_LocalDismiss_HubActiveThieuCreatedAt_KeepVaRepush()
        => Assert.Equal(MergePickupAlertAction.KeepLocalDismissRepushHub,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: null,
                localDismissedAt: DateTime.UtcNow,
                hubDismissed: false,
                hubCreatedAt: null,
                hubDismissedAt: null));

    /// <summary>Local chưa dismiss + Hub active → upsert (banner lan sang máy này).</summary>
    [Fact]
    public void Merge_HubActive_LocalChuaDismiss_LocalUpsert()
        => Assert.Equal(MergePickupAlertAction.LocalUpsert,
            PickupAlertMerge.QuyetDinh(
                localCreatedAt: null,
                localDismissedAt: null,
                hubDismissed: false,
                hubCreatedAt: new DateTimeOffset(2026, 8, 4, 5, 0, 0, TimeSpan.Zero),
                hubDismissedAt: null));
}
