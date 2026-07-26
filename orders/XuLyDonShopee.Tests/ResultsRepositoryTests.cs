using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test <see cref="ResultsRepository"/> (tab "Kết quả"): UpsertShops lưu/đọc danh sách shop (login làm khóa, tên để
/// hiển thị, gọi lại cập nhật tên không trùng, login rỗng dùng ShopName làm khóa để khớp nhãn đếm, GIỮ ĐÚNG thứ tự
/// nguồn của trang <c>/portal/shop</c>); IncrementPrepared cộng dồn theo (tài khoản, shop, ngày); GetPreparedByDay
/// tách theo ngày; mọi thứ tách theo tài khoản.
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
        // Đúng thứ tự nguồn đã truyền vào.
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

    // ===================== Thứ tự shop = ĐÚNG thứ tự trang /portal/shop của subaccount =====================

    /// <summary>Ghi thẳng SQL một dòng <c>account_shops</c> KIỂU CŨ (không có <c>sort_order</c> → NULL) — mô phỏng
    /// dữ liệu lưu trước bản có cột thứ tự, chưa đọc lại shop-list lần nào.</summary>
    private static void ChenShopCu(string path, long accountId, string login, string name)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO account_shops (account_id, shop_login, shop_name, updated_at)
    VALUES ($a, $login, $name, '2020-01-01T00:00:00.0000000');";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$login", login);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetShops_GiuDungThuTuNguon_KhongSapTheoBangChuCai()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        // Thứ tự Shopee trả về là C, A, B — app phải hiện Y HỆT, không sắp lại theo tên.
        repo.UpsertShops(1, new[]
        {
            new ShopListItem("333", "Shop C", "c.store"),
            new ShopListItem("111", "Shop A", "a.store"),
            new ShopListItem("222", "Shop B", "b.store"),
        });

        Assert.Equal(new[] { "c.store", "a.store", "b.store" }, repo.GetShops(1).Select(s => s.ShopLogin));
    }

    [Fact]
    public void UpsertShops_LuotSauDoiThuTu_GetShopsDoiTheo()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.UpsertShops(1, new[]
        {
            new ShopListItem("333", "Shop C", "c.store"),
            new ShopListItem("111", "Shop A", "a.store"),
            new ShopListItem("222", "Shop B", "b.store"),
        });

        // Lượt đọc sau Shopee đảo thứ tự → sort_order cập nhật ở nhánh DO UPDATE, app đổi theo.
        repo.UpsertShops(1, new[]
        {
            new ShopListItem("222", "Shop B", "b.store"),
            new ShopListItem("111", "Shop A", "a.store"),
            new ShopListItem("333", "Shop C", "c.store"),
        });

        Assert.Equal(new[] { "b.store", "a.store", "c.store" }, repo.GetShops(1).Select(s => s.ShopLogin));
    }

    [Fact]
    public void UpsertShops_ShopBiBoGiuaDanhSach_KhongLamLechThuTu()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.UpsertShops(1, new[]
        {
            new ShopListItem("111", "Shop A", "a.store"),
            new ShopListItem("000", "", ""),              // không định danh → bỏ, KHÔNG chiếm số thứ tự
            new ShopListItem("222", "Shop B", "b.store"),
            new ShopListItem("333", "Shop C", "c.store"),
        });

        Assert.Equal(new[] { "a.store", "b.store", "c.store" }, repo.GetShops(1).Select(s => s.ShopLogin));
    }

    [Fact]
    public void GetShops_DongCuChuaCoThuTu_XuongCuoiTheoTen()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open()); // khởi tạo schema

        // Dữ liệu CŨ: sort_order NULL (ghi trước bản có cột thứ tự).
        ChenShopCu(temp.Path, 1, "yyy.store", "Shop Y cũ");
        ChenShopCu(temp.Path, 1, "xxx.store", "Shop X cũ");

        // Dòng MỚI đã biết thứ tự nguồn → đứng TRƯỚC, dù tên xếp sau theo bảng chữ cái.
        repo.UpsertShops(1, new[] { new ShopListItem("999", "Shop Zulu", "zulu.store") });

        Assert.Equal(
            new[] { "zulu.store", "xxx.store", "yyy.store" }, // NULL xuống cuối, giữa chúng sắp theo tên
            repo.GetShops(1).Select(s => s.ShopLogin));
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
