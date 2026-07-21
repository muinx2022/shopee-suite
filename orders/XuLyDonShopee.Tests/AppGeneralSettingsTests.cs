using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cấu hình chung màn "Cài đặt": hàm thuần <see cref="AppGeneralSettings.Normalize"/> /
/// <see cref="AppGeneralSettings.Parse"/> (kẹp chu kỳ, trim thư mục) và vòng ghi-đọc thư mục hóa đơn +
/// chu kỳ theo dõi đơn qua <see cref="SettingsRepository"/> (DB tạm), gồm mặc định thư mục cạnh app.db.
/// </summary>
public class AppGeneralSettingsTests
{
    // ===================== Normalize (kẹp chu kỳ + trim thư mục) =====================

    [Theory]
    [InlineData(0, 1)]         // dưới min → về min
    [InlineData(-5, 1)]
    [InlineData(1, 1)]         // đúng min → giữ
    [InlineData(30, 30)]       // hợp lệ → giữ
    [InlineData(1440, 1440)]   // đúng max → giữ
    [InlineData(1441, 1440)]   // vừa vượt max → về max
    [InlineData(999999, 1440)] // vượt nhiều → về max
    public void Normalize_KepChuKyVaoKhoang(int input, int expected)
    {
        Assert.Equal(expected, AppGeneralSettings.Normalize(null, input).OrderIntervalMinutes);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(@"  D:\Hoa-don  ", @"D:\Hoa-don")]  // trim hai đầu
    public void Normalize_TrimThuMuc(string? input, string expected)
    {
        Assert.Equal(expected, AppGeneralSettings.Normalize(input, 30).InvoiceFolder);
    }

    // ===================== Parse (từ chuỗi DB) =====================

    [Fact]
    public void Parse_TatCaNull_TraVeMacDinh()
    {
        var s = AppGeneralSettings.Parse(null, null);

        Assert.Equal(string.Empty, s.InvoiceFolder);
        Assert.Equal(AppGeneralSettings.DefaultOrderIntervalMinutes, s.OrderIntervalMinutes); // 30
        Assert.Equal(AppGeneralSettings.Default, s);
    }

    [Fact]
    public void Parse_ChuKyHong_RoiVeMacDinh()
    {
        var s = AppGeneralSettings.Parse(@"D:\x", "abc");
        Assert.Equal(@"D:\x", s.InvoiceFolder);
        Assert.Equal(AppGeneralSettings.DefaultOrderIntervalMinutes, s.OrderIntervalMinutes); // "abc" → 30
    }

    [Fact]
    public void Parse_ChuKyHopLe_DungGiaTri()
    {
        var s = AppGeneralSettings.Parse(@"C:\hd", "45");
        Assert.Equal(@"C:\hd", s.InvoiceFolder);
        Assert.Equal(45, s.OrderIntervalMinutes);
    }

    // ===================== Database.DefaultInvoiceDir (cạnh app.db) =====================

    [Fact]
    public void DefaultInvoiceDir_NamCanhAppDb()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();

        var expected = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(db.Path)!, "Phieu-giao-hang");
        Assert.Equal(expected, db.DefaultInvoiceDir());
    }

    // ===================== Vòng ghi-đọc qua SettingsRepository (DB tạm) =====================

    [Fact]
    public void GetInvoiceFolder_ChuaLuu_TraVeMacDinhCanhAppDb()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new SettingsRepository(db);

        Assert.Equal(db.DefaultInvoiceDir(), repo.GetInvoiceFolder());
    }

    [Fact]
    public void SetGetInvoiceFolder_GiuGiaTriDaTrim()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetInvoiceFolder(@"  E:\Phieu  ");
        Assert.Equal(@"E:\Phieu", repo.GetInvoiceFolder());
    }

    [Fact]
    public void SetInvoiceFolder_Rong_QuayVeMacDinh()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new SettingsRepository(db);

        repo.SetInvoiceFolder(@"E:\Phieu");
        repo.SetInvoiceFolder("   "); // rỗng → xóa cấu hình
        Assert.Equal(db.DefaultInvoiceDir(), repo.GetInvoiceFolder());
    }

    [Fact]
    public void GetOrderIntervalMinutes_ChuaLuu_MacDinh30()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        Assert.Equal(AppGeneralSettings.DefaultOrderIntervalMinutes, repo.GetOrderIntervalMinutes());
    }

    [Fact]
    public void SetOrderIntervalMinutes_ChuanHoaKhiGhi()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetOrderIntervalMinutes(0);   // dưới min → về 1
        Assert.Equal(AppGeneralSettings.MinOrderIntervalMinutes, repo.GetOrderIntervalMinutes());

        repo.SetOrderIntervalMinutes(99999); // vượt max → về 1440
        Assert.Equal(AppGeneralSettings.MaxOrderIntervalMinutes, repo.GetOrderIntervalMinutes());

        repo.SetOrderIntervalMinutes(20);
        Assert.Equal(20, repo.GetOrderIntervalMinutes());
    }

    [Fact]
    public void MoLaiDb_ConfigVanCon()
    {
        using var temp = new TempDatabase();
        new SettingsRepository(temp.Open()).SetInvoiceFolder(@"F:\HD");
        new SettingsRepository(temp.Open()).SetOrderIntervalMinutes(12);

        // Mở lại DB (instance mới trỏ cùng file) → cấu hình vẫn còn (bền).
        var repo = new SettingsRepository(temp.Open());
        Assert.Equal(@"F:\HD", repo.GetInvoiceFolder());
        Assert.Equal(12, repo.GetOrderIntervalMinutes());
    }
}
