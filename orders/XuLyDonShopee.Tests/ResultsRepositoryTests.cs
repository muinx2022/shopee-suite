using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test <see cref="ResultsRepository"/> (tab "Kết quả"): UpsertShops lưu/đọc danh sách shop (login làm khóa, tên để
/// hiển thị, gọi lại cập nhật tên không trùng, login rỗng dùng ShopName làm khóa để khớp nhãn đếm); IncrementPrepared
/// cộng dồn theo (tài khoản, shop, ngày); GetPreparedByDay tách theo ngày; mọi thứ tách theo tài khoản.
/// </summary>
public class ResultsRepositoryTests
{
    [Fact]
    public void UpsertShops_LuuVaDoc_LoginLamKhoa_TenDeHienThi()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.UpsertShops(1, new[]
        {
            new ShopListItem("111", "Alina Store1", "alina99.store"),
            new ShopListItem("222", "Shop 9X", "shop9x.store"),
        });

        var shops = repo.GetShops(1);
        Assert.Equal(2, shops.Count);
        // Sắp theo tên hiển thị: "Alina Store1" trước "Shop 9X".
        Assert.Equal("alina99.store", shops[0].ShopLogin);
        Assert.Equal("Alina Store1", shops[0].ShopName);
        Assert.Equal("shop9x.store", shops[1].ShopLogin);
        Assert.Equal("Shop 9X", shops[1].ShopName);
    }

    [Fact]
    public void UpsertShops_GoiLai_CapNhatTen_KhongTaoTrung()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.UpsertShops(1, new[] { new ShopListItem("111", "Tên cũ", "alina99.store") });
        repo.UpsertShops(1, new[] { new ShopListItem("111", "Tên mới", "alina99.store") });

        var only = Assert.Single(repo.GetShops(1));
        Assert.Equal("alina99.store", only.ShopLogin);
        Assert.Equal("Tên mới", only.ShopName); // shop_name cập nhật, KHÔNG thêm dòng trùng
    }

    [Fact]
    public void UpsertShops_LoginRong_DungShopNameLamKhoa()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        // LoginName rỗng → shop_login fallback ShopName (khớp nhãn đếm bên OrdersBridgeSession = LoginName||ShopName).
        repo.UpsertShops(1, new[] { new ShopListItem("111", "Shop A", "") });

        var only = Assert.Single(repo.GetShops(1));
        Assert.Equal("Shop A", only.ShopLogin);
        Assert.Equal("Shop A", only.ShopName);
    }

    [Fact]
    public void UpsertShops_KhongCoTenLanLogin_BiBo()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.UpsertShops(1, new[]
        {
            new ShopListItem("111", "", ""),                 // không định danh → bỏ
            new ShopListItem("222", "Shop B", "b.store"),    // giữ
        });

        var only = Assert.Single(repo.GetShops(1));
        Assert.Equal("b.store", only.ShopLogin);
    }

    [Fact]
    public void IncrementPrepared_CongDon_TheoShopVaNgay()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.IncrementPrepared(1, "alina99.store", "2026-07-26");
        repo.IncrementPrepared(1, "alina99.store", "2026-07-26");
        repo.IncrementPrepared(1, "shop9x.store", "2026-07-26");

        var day = repo.GetPreparedByDay(1, "2026-07-26");
        Assert.Equal(2, day["alina99.store"]); // +1 mỗi đơn arrange
        Assert.Equal(1, day["shop9x.store"]);
    }

    [Fact]
    public void GetPreparedByDay_NgayKhac_KhongCoDem()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.IncrementPrepared(1, "alina99.store", "2026-07-26");

        Assert.Equal(1, repo.GetPreparedByDay(1, "2026-07-26")["alina99.store"]);
        Assert.Empty(repo.GetPreparedByDay(1, "2026-07-25")); // ngày khác → không có dòng (đếm 0)
    }

    [Fact]
    public void IncrementPrepared_LoginRong_BoQua()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.IncrementPrepared(1, "", "2026-07-26");
        repo.IncrementPrepared(1, "   ", "2026-07-26");

        Assert.Empty(repo.GetPreparedByDay(1, "2026-07-26"));
    }

    [Fact]
    public void Shop_Va_Dem_TachTheoTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.UpsertShops(1, new[] { new ShopListItem("111", "Shop A", "a.store") });
        repo.IncrementPrepared(1, "a.store", "2026-07-26");
        repo.IncrementPrepared(2, "b.store", "2026-07-26");

        Assert.Single(repo.GetShops(1));
        Assert.Empty(repo.GetShops(2)); // account 2 chưa lưu shop nào

        Assert.True(repo.GetPreparedByDay(1, "2026-07-26").ContainsKey("a.store"));
        Assert.False(repo.GetPreparedByDay(1, "2026-07-26").ContainsKey("b.store"));
        Assert.True(repo.GetPreparedByDay(2, "2026-07-26").ContainsKey("b.store"));
    }

    [Fact]
    public void GetShops_TaiKhoanChuaCoShop_TraRong()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        Assert.Empty(repo.GetShops(99));
        Assert.Empty(repo.GetPreparedByDay(99, "2026-07-26"));
    }
}
