using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

public class ProxyRepositoryTests
{
    private static ProxyEntry Proxy(string host, int port) => new()
    {
        Host = host,
        Port = port,
        Type = ProxyType.Http,
        Status = ProxyStatus.ChuaKiemTra
    };

    [Fact]
    public void Insert_RoiGetById()
    {
        using var temp = new TempDatabase();
        var repo = new ProxyRepository(temp.Open());

        var p = Proxy("1.2.3.4", 8080);
        p.Username = "u";
        p.Password = "pw";
        var id = repo.Insert(p);

        var loaded = repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Equal("1.2.3.4", loaded!.Host);
        Assert.Equal(8080, loaded.Port);
        Assert.Equal("u", loaded.Username);
        Assert.Equal("pw", loaded.Password);
        Assert.NotEqual(default, loaded.CreatedAt);
    }

    [Fact]
    public void InsertMany_ThemNhieu()
    {
        using var temp = new TempDatabase();
        var repo = new ProxyRepository(temp.Open());

        var list = new[]
        {
            Proxy("1.1.1.1", 80),
            Proxy("2.2.2.2", 3128),
            Proxy("3.3.3.3", 8080)
        };

        var count = repo.InsertMany(list);

        Assert.Equal(3, count);
        Assert.Equal(3, repo.GetAll().Count);
    }

    [Fact]
    public void Update_SuaMoiTruong_VaKhongAnhHuongDongKhac()
    {
        using var temp = new TempDatabase();
        var repo = new ProxyRepository(temp.Open());

        var p1 = Proxy("1.1.1.1", 80);
        var p2 = Proxy("2.2.2.2", 81);
        p2.Username = "olduser";
        p2.Password = "oldpass";
        repo.Insert(p1);
        repo.Insert(p2);

        // Sửa toàn bộ các trường của p1.
        p1.Host = "9.9.9.9";
        p1.Port = 3128;
        p1.Username = "newuser";
        p1.Password = "newpass";
        p1.Type = ProxyType.Socks5;
        p1.Status = ProxyStatus.Song;
        repo.Update(p1);

        var loaded = repo.GetById(p1.Id);
        Assert.NotNull(loaded);
        Assert.Equal("9.9.9.9", loaded!.Host);
        Assert.Equal(3128, loaded.Port);
        Assert.Equal("newuser", loaded.Username);
        Assert.Equal("newpass", loaded.Password);
        Assert.Equal(ProxyType.Socks5, loaded.Type);
        Assert.Equal(ProxyStatus.Song, loaded.Status);

        // Bản ghi p2 không bị ảnh hưởng bởi UPDATE (đúng cột/đúng WHERE).
        var other = repo.GetById(p2.Id);
        Assert.NotNull(other);
        Assert.Equal("2.2.2.2", other!.Host);
        Assert.Equal(81, other.Port);
        Assert.Equal("olduser", other.Username);
        Assert.Equal("oldpass", other.Password);
        Assert.Equal(ProxyType.Http, other.Type);
        Assert.Equal(ProxyStatus.ChuaKiemTra, other.Status);
    }

    [Fact]
    public void DeleteAll_XoaHet()
    {
        using var temp = new TempDatabase();
        var repo = new ProxyRepository(temp.Open());

        repo.InsertMany(new[] { Proxy("1.1.1.1", 80), Proxy("2.2.2.2", 81) });
        repo.DeleteAll();

        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Delete_XoaMotDong()
    {
        using var temp = new TempDatabase();
        var repo = new ProxyRepository(temp.Open());

        var p1 = Proxy("1.1.1.1", 80);
        var p2 = Proxy("2.2.2.2", 81);
        repo.Insert(p1);
        repo.Insert(p2);

        repo.Delete(p1.Id);

        var all = repo.GetAll();
        Assert.Single(all);
        Assert.Equal("2.2.2.2", all[0].Host);
    }

    [Fact]
    public void DuLieu_ConNguyen_SauKhiMoLaiDatabase()
    {
        using var temp = new TempDatabase();

        // Phiên 1: ghi.
        {
            var repo1 = new ProxyRepository(temp.Open());
            repo1.InsertMany(new[] { Proxy("1.1.1.1", 80), Proxy("2.2.2.2", 81) });
        }

        // Phiên 2: mở lại, dữ liệu còn nguyên.
        {
            var repo2 = new ProxyRepository(temp.Open());
            var all = repo2.GetAll();
            Assert.Equal(2, all.Count);
        }
    }
}
