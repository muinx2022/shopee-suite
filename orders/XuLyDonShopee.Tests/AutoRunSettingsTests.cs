using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test đọc/ghi/chuẩn hóa cấu hình "Chạy tự động": hàm thuần <see cref="AutoRunSettings.Normalize"/> /
/// <see cref="AutoRunSettings.Parse"/> và vòng ghi-đọc qua <see cref="SettingsRepository"/> (DB tạm).
/// </summary>
public class AutoRunSettingsTests
{
    // ===================== Normalize (kẹp cả tối thiểu và tối đa) =====================

    [Theory]
    [InlineData(0, 0, 1, 1)]              // dưới min → về min
    [InlineData(-3, -9, 1, 1)]
    [InlineData(1, 1, 1, 1)]              // đúng min → giữ
    [InlineData(5, 30, 5, 30)]           // hợp lệ → giữ
    [InlineData(20, 1440, 20, 1440)]     // đúng max → giữ
    [InlineData(21, 1441, 20, 1440)]     // vừa vượt max → về max
    [InlineData(100, 99999, 20, 1440)]   // vượt max nhiều → về max (chặn Task.Delay tràn int ms)
    public void Normalize_KepBatchVaGapVaoKhoang(int batch, int gap, int expBatch, int expGap)
    {
        var s = AutoRunSettings.Normalize(batch, gap, doSync: true, doProcess: false);

        Assert.Equal(expBatch, s.BatchSize);
        Assert.Equal(expGap, s.GapMinutes);
        Assert.True(s.DoSync);
        Assert.False(s.DoProcess);
    }

    [Fact]
    public void Parse_SoVuotMax_DuocKepVeMax()
    {
        var s = AutoRunSettings.Parse("100", "99999", "false", "false");

        Assert.Equal(AutoRunSettings.MaxBatchSize, s.BatchSize);  // 20
        Assert.Equal(AutoRunSettings.MaxGapMinutes, s.GapMinutes); // 1440
    }

    // ===================== Parse (từ chuỗi DB) =====================

    [Fact]
    public void Parse_TatCaNull_TraVeMacDinh()
    {
        var s = AutoRunSettings.Parse(null, null, null, null);

        Assert.Equal(AutoRunSettings.DefaultBatchSize, s.BatchSize);   // 10
        Assert.Equal(AutoRunSettings.DefaultGapMinutes, s.GapMinutes); // 15
        Assert.False(s.DoSync);
        Assert.False(s.DoProcess);
        Assert.Equal(AutoRunSettings.Default, s);
    }

    [Fact]
    public void Parse_ChuoiHopLe_DungGiaTri()
    {
        var s = AutoRunSettings.Parse("4", "20", "true", "false");

        Assert.Equal(4, s.BatchSize);
        Assert.Equal(20, s.GapMinutes);
        Assert.True(s.DoSync);
        Assert.False(s.DoProcess);
    }

    [Fact]
    public void Parse_SoHong_RoiVeMacDinh_RoiChuanHoa()
    {
        var s = AutoRunSettings.Parse("abc", "", "1", "0");

        Assert.Equal(AutoRunSettings.DefaultBatchSize, s.BatchSize);   // "abc" → mặc định 10
        Assert.Equal(AutoRunSettings.DefaultGapMinutes, s.GapMinutes); // "" → mặc định 15
        Assert.True(s.DoSync);   // "1" → true
        Assert.False(s.DoProcess); // "0" → false
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("linh tinh", false)]
    public void Parse_DocBoolBen(string? raw, bool expected)
    {
        var s = AutoRunSettings.Parse("3", "15", raw, raw);

        Assert.Equal(expected, s.DoSync);
        Assert.Equal(expected, s.DoProcess);
    }

    [Fact]
    public void Parse_SoDuoiMin_DuocKep()
    {
        var s = AutoRunSettings.Parse("0", "0", "false", "false");

        Assert.Equal(AutoRunSettings.MinBatchSize, s.BatchSize);  // 1
        Assert.Equal(AutoRunSettings.MinGapMinutes, s.GapMinutes); // 1
    }

    // ===================== Vòng ghi-đọc qua SettingsRepository (DB tạm) =====================

    [Fact]
    public void Repository_MacDinh_KhiChuaLuu()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        var s = repo.GetAutoRunSettings();

        Assert.Equal(AutoRunSettings.Default, s); // chưa lưu gì → mặc định
    }

    [Fact]
    public void Repository_GhiRoiDoc_TronVen()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetAutoRunSettings(new AutoRunSettings(BatchSize: 6, GapMinutes: 25, DoSync: true, DoProcess: true));

        var s = repo.GetAutoRunSettings();
        Assert.Equal(6, s.BatchSize);
        Assert.Equal(25, s.GapMinutes);
        Assert.True(s.DoSync);
        Assert.True(s.DoProcess);
    }

    [Fact]
    public void Repository_GhiGiaTriLoi_DuocChuanHoaKhiGhi()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        // Ghi số dưới min → repo chuẩn hóa trước khi lưu.
        repo.SetAutoRunSettings(new AutoRunSettings(BatchSize: 0, GapMinutes: 0, DoSync: false, DoProcess: false));

        var s = repo.GetAutoRunSettings();
        Assert.Equal(AutoRunSettings.MinBatchSize, s.BatchSize);
        Assert.Equal(AutoRunSettings.MinGapMinutes, s.GapMinutes);
    }

    [Fact]
    public void Repository_MoLaiDb_ConfigVanCon()
    {
        using var temp = new TempDatabase();
        new SettingsRepository(temp.Open())
            .SetAutoRunSettings(new AutoRunSettings(2, 10, DoSync: true, DoProcess: false));

        // Mở lại DB (instance mới trỏ cùng file) → cấu hình vẫn còn (bền).
        var s = new SettingsRepository(temp.Open()).GetAutoRunSettings();

        Assert.Equal(2, s.BatchSize);
        Assert.Equal(10, s.GapMinutes);
        Assert.True(s.DoSync);
        Assert.False(s.DoProcess);
    }
}
