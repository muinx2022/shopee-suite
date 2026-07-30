using Shopee.Core.Accounts;

namespace Shopee.Core.Tests;

/// <summary>
/// <see cref="ShopeeAuth.ParseLoginLine"/> gộp BA bản parse dòng tài khoản trước đây (MultiBrave /
/// Search / Kiểm tra tài khoản) vốn LỆCH nhau. Bộ case dưới khoá lại ngữ nghĩa hợp nhất theo từng bộ cờ:
/// <list type="bullet">
/// <item>MB <see cref="ShopeeLoginLineOptions.Strict"/> — đủ user + pass + cookie.</item>
/// <item>SE <see cref="ShopeeLoginLineOptions.AllowEmptyPassword"/> — password được phép rỗng.</item>
/// <item>CA <see cref="ShopeeLoginLineOptions.AllowMissingCookie"/> — "user|pass" là đủ.</item>
/// </list>
/// Hai điểm NỚI LỎNG có chủ đích so với bản cũ cũng được khoá ở đây (xem 2 test cuối): MB nay nhận cookie
/// tên khác <c>SPC_F</c>, và giá trị cookie chứa '|' KHÔNG còn bị cắt ở bản SE/CA.
/// </summary>
public sealed class ShopeeAuthParseLoginLineTests
{
    // ── Dòng MB chuẩn: có prefix SPC_F= và '|' NẰM TRONG giá trị cookie ────────────────────────────
    [Fact]
    public void MbChuan_CoPrefixSpcF_VaDauGachDungTrongCookie()
    {
        var r = ShopeeAuth.ParseLoginLine(
            "user1|pass1|.shopee.vn=SPC_F=abc|123", ShopeeLoginLineOptions.Strict);

        Assert.True(r.Ok);
        Assert.Equal("user1", r.Username);
        Assert.Equal("pass1", r.Password);
        Assert.Equal(".shopee.vn", r.CookieDomain);
        // Ghép lại parts[2..]: giá trị cookie giữ nguyên cả dấu '|' (bản SE/CA cũ cắt mất "|123").
        Assert.Equal("abc|123", r.SpcF);
    }

    [Fact]
    public void MbChuan_PrefixChuThuong_VanNhan()
    {
        var r = ShopeeAuth.ParseLoginLine(
            "user1|pass1|.shopee.vn=spc_f=abc123", ShopeeLoginLineOptions.Strict);

        Assert.True(r.Ok);
        Assert.Equal("abc123", r.SpcF);
    }

    // ── Dòng SE: KHÔNG prefix, lấy phần sau dấu '=' thứ hai ────────────────────────────────────────
    [Fact]
    public void Se_KhongPrefix_LayPhanSauDauBangThuHai()
    {
        var r = ShopeeAuth.ParseLoginLine(
            "user1|pass1|.shopee.vn=FOO=abc123", ShopeeLoginLineOptions.AllowEmptyPassword);

        Assert.True(r.Ok);
        Assert.Equal(".shopee.vn", r.CookieDomain);
        Assert.Equal("abc123", r.SpcF);
    }

    // ── Dòng CA: 2 phần, không cookie ──────────────────────────────────────────────────────────────
    [Fact]
    public void Ca_HaiPhanKhongCookie_VanHopLe()
    {
        var r = ShopeeAuth.ParseLoginLine("user1|pass1", ShopeeLoginLineOptions.AllowMissingCookie);

        Assert.True(r.Ok);
        Assert.Equal("user1", r.Username);
        Assert.Equal("pass1", r.Password);
        Assert.Equal("", r.CookieDomain);
        Assert.Equal("", r.SpcF);
    }

    [Fact]
    public void Mb_HaiPhanKhongCookie_Loi()
    {
        var r = ShopeeAuth.ParseLoginLine("user1|pass1", ShopeeLoginLineOptions.Strict);

        Assert.False(r.Ok);
        Assert.NotEmpty(r.Error);
    }

    // ── Thiếu password: SE pass, MB/CA fail ────────────────────────────────────────────────────────
    [Fact]
    public void ThieuPassword_SeChoQua()
    {
        var r = ShopeeAuth.ParseLoginLine(
            "user1||.shopee.vn=SPC_F=abc123", ShopeeLoginLineOptions.AllowEmptyPassword);

        Assert.True(r.Ok);
        Assert.Equal("user1", r.Username);
        Assert.Equal("", r.Password);
        Assert.Equal("abc123", r.SpcF);
    }

    [Fact]
    public void ThieuPassword_MbVaCaDeuLoi()
    {
        var mb = ShopeeAuth.ParseLoginLine(
            "user1||.shopee.vn=SPC_F=abc123", ShopeeLoginLineOptions.Strict);
        var ca = ShopeeAuth.ParseLoginLine("user1|", ShopeeLoginLineOptions.AllowMissingCookie);

        Assert.False(mb.Ok);
        Assert.False(ca.Ok);
    }

    [Fact]
    public void ThieuUsername_LoiOMoiBoCo()
    {
        Assert.False(ShopeeAuth.ParseLoginLine("|pass1|.shopee.vn=SPC_F=abc", ShopeeLoginLineOptions.Strict).Ok);
        Assert.False(ShopeeAuth.ParseLoginLine("|pass1|.shopee.vn=SPC_F=abc", ShopeeLoginLineOptions.AllowEmptyPassword).Ok);
        Assert.False(ShopeeAuth.ParseLoginLine("|pass1", ShopeeLoginLineOptions.AllowMissingCookie).Ok);
    }

    // ── Cookie prefix lạ: MB NAY NHẬN (nới lỏng có chủ đích so với bản MB cũ) ───────────────────────
    [Fact]
    public void CookiePrefixLa_MbNayNhan()
    {
        var r = ShopeeAuth.ParseLoginLine(
            "user1|pass1|.shopee.vn=FOO=abc123", ShopeeLoginLineOptions.Strict);

        Assert.True(r.Ok);
        Assert.Equal(".shopee.vn", r.CookieDomain);
        Assert.Equal("abc123", r.SpcF);
    }

    // ── Cookie hỏng / thiếu: cookie BẮT BUỘC thì lỗi, CA thì bỏ qua ────────────────────────────────
    [Fact]
    public void CookieKhongCoDauBang_MbLoi_CaBoQua()
    {
        var mb = ShopeeAuth.ParseLoginLine("user1|pass1|rac", ShopeeLoginLineOptions.Strict);
        var ca = ShopeeAuth.ParseLoginLine("user1|pass1|rac", ShopeeLoginLineOptions.AllowMissingCookie);

        Assert.False(mb.Ok);
        Assert.True(ca.Ok);
        Assert.Equal("", ca.SpcF);   // cookie hỏng → bỏ qua, KHÔNG lỗi (đúng bản CA cũ)
    }

    [Fact]
    public void CookieRong_MbLoi_CaBoQua()
    {
        Assert.False(ShopeeAuth.ParseLoginLine("user1|pass1|", ShopeeLoginLineOptions.Strict).Ok);
        Assert.True(ShopeeAuth.ParseLoginLine("user1|pass1|", ShopeeLoginLineOptions.AllowMissingCookie).Ok);
    }

    [Fact]
    public void KhongCoTenCookie_ChiCaLayCaPhanConLai()
    {
        // ".shopee.vn=abc123": sau domain không còn dấu '=' nào nữa.
        var mb = ShopeeAuth.ParseLoginLine("user1|pass1|.shopee.vn=abc123", ShopeeLoginLineOptions.Strict);
        var ca = ShopeeAuth.ParseLoginLine("user1|pass1|.shopee.vn=abc123", ShopeeLoginLineOptions.AllowMissingCookie);

        Assert.False(mb.Ok);
        Assert.True(ca.Ok);
        Assert.Equal("abc123", ca.SpcF);
    }

    [Fact]
    public void GiaTriSpcFRong_MbLoi()
    {
        var r = ShopeeAuth.ParseLoginLine("user1|pass1|.shopee.vn=SPC_F=", ShopeeLoginLineOptions.Strict);

        Assert.False(r.Ok);
    }

    // ── Khoảng trắng thừa + đầu vào rỗng ───────────────────────────────────────────────────────────
    [Fact]
    public void KhoangTrangThua_BiCatOTungPhan()
    {
        var r = ShopeeAuth.ParseLoginLine(
            "  user1 | pass1 | .shopee.vn=SPC_F=abc123 ", ShopeeLoginLineOptions.Strict);

        Assert.True(r.Ok);
        Assert.Equal("user1", r.Username);
        Assert.Equal("pass1", r.Password);
        Assert.Equal(".shopee.vn", r.CookieDomain);
        Assert.Equal("abc123", r.SpcF);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("chi-mot-phan")]
    public void DauVaoRongHoacMotPhan_LoiOMoiBoCo(string? line)
    {
        Assert.False(ShopeeAuth.ParseLoginLine(line, ShopeeLoginLineOptions.Strict).Ok);
        Assert.False(ShopeeAuth.ParseLoginLine(line, ShopeeLoginLineOptions.AllowEmptyPassword).Ok);
        Assert.False(ShopeeAuth.ParseLoginLine(line, ShopeeLoginLineOptions.AllowMissingCookie).Ok);
    }
}
