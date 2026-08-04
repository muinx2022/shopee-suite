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
    public void GhiPhatHien_ListActive_Dismiss_HienLaiSauDismiss()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.GhiPhatHienTaiCho(1, "alina99.store", "Thanh Hóa");
        var one = Assert.Single(repo.ListActive(1));
        Assert.Equal("alina99.store", one.ShopLogin);
        Assert.Equal("Thanh Hóa", one.Province);
        Assert.Null(one.DismissedAt);

        repo.DismissTaiCho(1, "alina99.store");
        Assert.Empty(repo.ListActive(1));

        repo.GhiPhatHienTaiCho(1, "alina99.store", "Hà Nội");
        var again = Assert.Single(repo.ListActive(1));
        Assert.Equal("Hà Nội", again.Province);
        Assert.Null(again.DismissedAt);
    }

    [Fact]
    public void ListActive_TachTheoTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.GhiPhatHienTaiCho(1, "shop-a", null);
        repo.GhiPhatHienTaiCho(2, "shop-b", null);

        Assert.Equal("shop-a", Assert.Single(repo.ListActive(1)).ShopLogin);
        Assert.Equal("shop-b", Assert.Single(repo.ListActive(2)).ShopLogin);
    }

    [Fact]
    public void GhiPhatHien_NhieuShop_ListActive_DuDong()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.GhiPhatHienTaiCho(1, "a.store", "TH");
        repo.GhiPhatHienTaiCho(1, "b.store", "TH");
        Assert.Equal(2, repo.ListActive(1).Count);
    }

    // ===== Outbox: thay đổi tại chỗ phải chờ Hub NHẬN mới hạ cờ =====

    [Fact]
    public void GhiPhatHienTaiCho_BatCoChoDay_DanhDauDaDay_MoiHa()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.GhiPhatHienTaiCho(1, "a.store", "TH");
        var cho = Assert.Single(repo.ListChoDay(1));
        Assert.True(cho.ChoDay);
        Assert.Equal(0, cho.HubRev);

        repo.DanhDauDaDay(1, "a.store", 7);
        Assert.Empty(repo.ListChoDay(1));
        var xong = Assert.Single(repo.ListAll(1));
        Assert.False(xong.ChoDay);
        Assert.Equal(7, xong.HubRev);
    }

    [Fact]
    public void DismissTaiCho_BatCoChoDay_VaAnBanner()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.ApDungTuHub(1, "a.store", "TH", dismissed: false, hubRev: 3);
        Assert.Single(repo.ListActive(1));
        Assert.Empty(repo.ListChoDay(1));

        repo.DismissTaiCho(1, "a.store");
        Assert.Empty(repo.ListActive(1));
        var cho = Assert.Single(repo.ListChoDay(1));
        Assert.Equal(3, cho.HubRev); // thay đổi tại chỗ KHÔNG được hạ hub_rev đang giữ
    }

    [Fact]
    public void ApDungTuHub_NhanTrangThaiVaRev_HaCoChoDay()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.GhiPhatHienTaiCho(1, "a.store", "TH");
        repo.ApDungTuHub(1, "a.store", "", dismissed: true, hubRev: 9);

        Assert.Empty(repo.ListActive(1));
        Assert.Empty(repo.ListChoDay(1));
        var row = Assert.Single(repo.ListAll(1));
        Assert.Equal(9, row.HubRev);
        Assert.Equal("TH", row.Province); // province rỗng từ tombstone KHÔNG xoá tỉnh đã biết
    }

    [Fact]
    public void DanhDauDaDay_RevCu_KhongHaCoCuaThayDoiMoiHon()
    {
        using var temp = new TempDatabase();
        var repo = new PickupAddressAlertsRepository(temp.Open());

        repo.ApDungTuHub(1, "a.store", "TH", dismissed: false, hubRev: 10);
        repo.DismissTaiCho(1, "a.store");            // thay đổi mới, chờ đẩy

        repo.DanhDauDaDay(1, "a.store", 4);          // ack của lượt đẩy CŨ đáp muộn
        Assert.Single(repo.ListChoDay(1));           // vẫn phải còn chờ đẩy
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

    // ── Merge Hub ↔ local: theo rev + outbox, KHÔNG đụng đồng hồ ──────────────────────────────────────

    /// <summary>
    /// CA MÀ BẢN CŨ THUA: Hub chết đúng lúc vòng shop phát hiện lỗi (cờ chờ đẩy còn nguyên), trên Hub vẫn là
    /// tombstone cũ của lần bấm X trước. Bản cũ nghe Hub → xoá mất cảnh báo của lỗi CHƯA sửa. Nay giữ + đẩy lên.
    /// </summary>
    [Fact]
    public void Merge_LocalChoDay_KhongBiTombstoneHubXoa()
        => Assert.Equal(MergePickupAlertAction.DayLenHub,
            PickupAlertMerge.QuyetDinh(
                localCoDong: true, localChoDay: true, localHubRev: 5, hubRev: 5));

    /// <summary>Cờ chờ đẩy thắng cả khi Hub có rev MỚI hơn — thay đổi tại chỗ chưa ai biết, phải đẩy trước.</summary>
    [Fact]
    public void Merge_LocalChoDay_ThangCaKhiHubRevMoiHon()
        => Assert.Equal(MergePickupAlertAction.DayLenHub,
            PickupAlertMerge.QuyetDinh(
                localCoDong: true, localChoDay: true, localHubRev: 2, hubRev: 9));

    /// <summary>Máy khác bấm X (Hub tăng rev) → máy này nghe Hub, gỡ banner. Chức năng CHÍNH, không được hồi quy.</summary>
    [Fact]
    public void Merge_HubRevMoiHon_TheoHub()
        => Assert.Equal(MergePickupAlertAction.TheoHub,
            PickupAlertMerge.QuyetDinh(
                localCoDong: true, localChoDay: false, localHubRev: 4, hubRev: 5));

    /// <summary>Đã nhận rev này rồi → không ghi lại DB mỗi 60 giây.</summary>
    [Fact]
    public void Merge_HubRevBang_GiuNguyen()
        => Assert.Equal(MergePickupAlertAction.GiuNguyen,
            PickupAlertMerge.QuyetDinh(
                localCoDong: true, localChoDay: false, localHubRev: 5, hubRev: 5));

    /// <summary>Rev Hub LÙI (Hub khôi phục từ bản sao lưu cũ) → không nghe, giữ trạng thái đang có.</summary>
    [Fact]
    public void Merge_HubRevLui_GiuNguyen()
        => Assert.Equal(MergePickupAlertAction.GiuNguyen,
            PickupAlertMerge.QuyetDinh(
                localCoDong: true, localChoDay: false, localHubRev: 9, hubRev: 3));

    /// <summary>Máy này chưa biết shop → nhận Hub (banner lan sang máy mới).</summary>
    [Fact]
    public void Merge_LocalChuaCoDong_TheoHub()
        => Assert.Equal(MergePickupAlertAction.TheoHub,
            PickupAlertMerge.QuyetDinh(
                localCoDong: false, localChoDay: false, localHubRev: 0, hubRev: 1));

    /// <summary>Chỉ local có dòng, đã đồng bộ xong, Hub không có → không có gì để làm.</summary>
    [Fact]
    public void Merge_HubKhongCoDong_DaDongBo_GiuNguyen()
        => Assert.Equal(MergePickupAlertAction.GiuNguyen,
            PickupAlertMerge.QuyetDinh(
                localCoDong: true, localChoDay: false, localHubRev: 2, hubRev: 0));

    /// <summary>Dòng chờ đẩy mà Hub CHƯA hề có → vẫn phải đẩy (nếu bỏ sót thì kẹt cờ vĩnh viễn).</summary>
    [Fact]
    public void Merge_ChoDay_HubChuaCoDong_VanDay()
        => Assert.Equal(MergePickupAlertAction.DayLenHub,
            PickupAlertMerge.QuyetDinh(
                localCoDong: true, localChoDay: true, localHubRev: 0, hubRev: 0));
}
