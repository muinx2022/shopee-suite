using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

public class AccountRepositoryTests
{
    private static Account SampleAccount() => new()
    {
        Email = "test@shopee.vn",
        Password = "matkhau123",
        Phone = "0900000000",
        Cookie = "SPC_SI=abc",
        Note = "tài khoản test",
        Status = AccountStatus.HoatDong
    };

    [Fact]
    public void Insert_RoiGetById_TraVeDayDu()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var account = SampleAccount();
        var id = repo.Insert(account);

        Assert.True(id > 0);
        Assert.Equal(id, account.Id);
        Assert.NotEqual(default, account.CreatedAt);

        var loaded = repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Equal("test@shopee.vn", loaded!.Email);
        Assert.Equal("matkhau123", loaded.Password);
        Assert.Equal("0900000000", loaded.Phone);
        Assert.Equal("SPC_SI=abc", loaded.Cookie);
        Assert.Equal("tài khoản test", loaded.Note);
        Assert.Equal(AccountStatus.HoatDong, loaded.Status);
    }

    [Fact]
    public void GetAll_TraVeTatCa()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        repo.Insert(new Account { Email = "a@x.com", Password = "1" });
        repo.Insert(new Account { Email = "b@x.com", Password = "2" });

        var all = repo.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Update_ThayDoiDuLieu_VaUpdatedAt()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var account = SampleAccount();
        repo.Insert(account);
        var createdAt = account.CreatedAt;

        account.Email = "new@shopee.vn";
        account.Status = AccountStatus.BiKhoa;
        repo.Update(account);

        var loaded = repo.GetById(account.Id);
        Assert.NotNull(loaded);
        Assert.Equal("new@shopee.vn", loaded!.Email);
        Assert.Equal(AccountStatus.BiKhoa, loaded.Status);
        Assert.True(loaded.UpdatedAt >= createdAt);
    }

    [Fact]
    public void Insert_CoProxyKey_RoiGetById_TraVeProxyKey()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var account = new Account { Email = "p@x.com", Password = "1", ProxyKey = "KIOT-KEY-123" };
        var id = repo.Insert(account);

        var loaded = repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Equal("KIOT-KEY-123", loaded!.ProxyKey);
    }

    [Fact]
    public void Insert_KhongCoProxyKey_TraVeNull()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var id = repo.Insert(new Account { Email = "q@x.com", Password = "1" });

        var loaded = repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.ProxyKey);
    }

    [Fact]
    public void Update_ThayDoiProxyKey_LuuDung_VaXoaVeNull()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var account = new Account { Email = "u@x.com", Password = "1" };
        repo.Insert(account);

        // Gán key mới → đọc lại đúng.
        account.ProxyKey = "NEW-KEY";
        repo.Update(account);
        Assert.Equal("NEW-KEY", repo.GetById(account.Id)!.ProxyKey);

        // Xóa key (null) → đọc lại null (không lẫn với trường khác — kiểm thứ tự cột SELECT/Map).
        account.ProxyKey = null;
        repo.Update(account);
        var reloaded = repo.GetById(account.Id)!;
        Assert.Null(reloaded.ProxyKey);
        Assert.Equal("u@x.com", reloaded.Email); // các trường khác không bị lệch chỉ số
    }

    [Fact]
    public void Insert_CoPickupAddress_RoiGetById_TraVePickupAddress()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var account = new Account { Email = "pa@x.com", Password = "1", PickupAddress = "Hà Nội" };
        var id = repo.Insert(account);

        var loaded = repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Equal("Hà Nội", loaded!.PickupAddress);
    }

    [Fact]
    public void Insert_KhongCoPickupAddress_TraVeNull()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var id = repo.Insert(new Account { Email = "pb@x.com", Password = "1" });

        var loaded = repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.PickupAddress);
    }

    [Fact]
    public void Update_ThayDoiPickupAddress_LuuDung_VaXoaVeNull()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var account = new Account { Email = "pc@x.com", Password = "1", ProxyKey = "K1" };
        repo.Insert(account);

        // Gán giá trị mới → đọc lại đúng.
        account.PickupAddress = "TP Hồ Chí Minh";
        repo.Update(account);
        Assert.Equal("TP Hồ Chí Minh", repo.GetById(account.Id)!.PickupAddress);

        // Xóa (null) → đọc lại null; các trường lân cận (ProxyKey/Email/Status) KHÔNG bị lệch chỉ số.
        account.PickupAddress = null;
        repo.Update(account);
        var reloaded = repo.GetById(account.Id)!;
        Assert.Null(reloaded.PickupAddress);
        Assert.Equal("K1", reloaded.ProxyKey);
        Assert.Equal("pc@x.com", reloaded.Email);
    }

    [Fact]
    public void Insert_ProxyKeyVaPickupAddress_KhongLanChiSoCot()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        // Hai cột TEXT liền nhau (ProxyKey, PickupAddress) đặt giá trị KHÁC nhau để bắt lệch SELECT/Map.
        var account = new Account
        {
            Email = "pd@x.com",
            Password = "pw",
            ProxyKey = "KIOT-XYZ",
            PickupAddress = "Thanh Hóa",
            Status = AccountStatus.BiKhoa
        };
        var id = repo.Insert(account);

        var loaded = repo.GetById(id)!;
        Assert.Equal("KIOT-XYZ", loaded.ProxyKey);
        Assert.Equal("Thanh Hóa", loaded.PickupAddress);
        Assert.Equal(AccountStatus.BiKhoa, loaded.Status);
        Assert.Equal("pd@x.com", loaded.Email);
    }

    [Fact]
    public void Delete_XoaKhoiDb()
    {
        using var temp = new TempDatabase();
        var repo = new AccountRepository(temp.Open());

        var account = SampleAccount();
        repo.Insert(account);

        repo.Delete(account.Id);

        Assert.Null(repo.GetById(account.Id));
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void DuLieu_ConNguyen_SauKhiMoLaiDatabase()
    {
        using var temp = new TempDatabase();

        long id;
        // Phiên 1: ghi dữ liệu.
        {
            var repo1 = new AccountRepository(temp.Open());
            var account = SampleAccount();
            id = repo1.Insert(account);
        }

        // Phiên 2: mở lại Database mới trỏ cùng file, dữ liệu vẫn còn.
        {
            var repo2 = new AccountRepository(temp.Open());
            var loaded = repo2.GetById(id);
            Assert.NotNull(loaded);
            Assert.Equal("test@shopee.vn", loaded!.Email);
            Assert.Equal(AccountStatus.HoatDong, loaded.Status);
        }
    }
}
