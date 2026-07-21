using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test migration MỘT LẦN <see cref="ProxyKeyPoolMigration.EnsureMigrated"/>: gộp <c>Account.ProxyKey</c>
/// cố định (cơ chế cũ) vào pool KiotProxy chung — dedup, giữ pool cũ, idempotent qua cờ, KHÔNG xóa cột vestigial.
/// </summary>
public class ProxyKeyPoolMigrationTests
{
    [Fact]
    public void EnsureMigrated_GopProxyKeyTaiKhoanVaoPool_DedupGiuPoolCu()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var accounts = new AccountRepository(db);
        var settings = new SettingsRepository(db);

        // Pool chung sẵn có 1 key.
        settings.SetKiotProxyKeys(new[] { "poolA" });

        // 3 tài khoản: keyX (mới), poolA (trùng pool → dedup), rỗng (bỏ qua).
        accounts.Insert(new Account { Email = "a@x.com", Password = "p", ProxyKey = "keyX" });
        accounts.Insert(new Account { Email = "b@x.com", Password = "p", ProxyKey = "poolA" });
        accounts.Insert(new Account { Email = "c@x.com", Password = "p", ProxyKey = null });

        var ran = ProxyKeyPoolMigration.EnsureMigrated(accounts, settings);

        Assert.True(ran);
        // Pool cũ TRƯỚC, thêm keyX; poolA KHÔNG nhân đôi.
        Assert.Equal(new[] { "poolA", "keyX" }, settings.GetKiotProxyKeys());
        Assert.Equal("1", settings.Get(SettingsRepository.ProxyKeyMigratedV1));
    }

    [Fact]
    public void EnsureMigrated_ChayLan2_BoQua_TraFalse_PoolKhongDoi()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var accounts = new AccountRepository(db);
        var settings = new SettingsRepository(db);

        accounts.Insert(new Account { Email = "a@x.com", Password = "p", ProxyKey = "keyX" });

        Assert.True(ProxyKeyPoolMigration.EnsureMigrated(accounts, settings)); // lần 1 gộp
        Assert.Equal(new[] { "keyX" }, settings.GetKiotProxyKeys());

        // Thêm account SAU lần 1 → lần 2 KHÔNG gộp (đã có cờ) → keyY không vào pool.
        accounts.Insert(new Account { Email = "b@x.com", Password = "p", ProxyKey = "keyY" });

        Assert.False(ProxyKeyPoolMigration.EnsureMigrated(accounts, settings));
        Assert.Equal(new[] { "keyX" }, settings.GetKiotProxyKeys());
    }

    [Fact]
    public void EnsureMigrated_KhongKeyNao_KhongTaoPoolRong_VanDatCo()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var accounts = new AccountRepository(db);
        var settings = new SettingsRepository(db);

        var ran = ProxyKeyPoolMigration.EnsureMigrated(accounts, settings);

        Assert.True(ran);
        Assert.Empty(settings.GetKiotProxyKeys());
        // Không tạo settings row pool rỗng vô nghĩa; nhưng cờ đã đặt để không chạy lại.
        Assert.Null(settings.Get(SettingsRepository.KiotProxyApiKeys));
        Assert.Equal("1", settings.Get(SettingsRepository.ProxyKeyMigratedV1));
    }

    [Fact]
    public void EnsureMigrated_KhongXoaProxyKeyTaiKhoan_GiuVestigial()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var accounts = new AccountRepository(db);
        var settings = new SettingsRepository(db);

        accounts.Insert(new Account { Email = "a@x.com", Password = "p", ProxyKey = "keyX" });

        ProxyKeyPoolMigration.EnsureMigrated(accounts, settings);

        // Cột/giá trị ProxyKey của tài khoản GIỮ NGUYÊN (chỉ ngừng dùng, không xóa).
        Assert.Equal("keyX", accounts.GetAll()[0].ProxyKey);
    }
}
