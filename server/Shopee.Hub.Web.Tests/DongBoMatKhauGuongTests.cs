using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Đồng bộ 3 ô đăng nhập (mật khẩu tài khoản phụ · hòm thư xác minh · mật khẩu hòm thư) trong GƯƠNG danh bạ
/// Đơn hàng — thêm 11/08/2026.
/// <para>Bẫy chết người ở đây: <c>UpsertOrdersAccounts</c> XOÁ rồi GHI LẠI toàn bộ danh bạ của máy đó mỗi lượt
/// đẩy, mà worker gương đẩy lại mỗi 3s khi có thay đổi / 60s khi rảnh. Nếu ô rỗng được phép ghi đè thì mật khẩu
/// biến mất ngay nhịp kế — và biến mất IM LẶNG, không lỗi, không log.</para>
/// </summary>
public sealed class DongBoMatKhauGuongTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-matkhau-test-" + Guid.NewGuid().ToString("N"));

    private const string May1 = "may-1:orders";
    private const string May2 = "may-2:orders";
    private const string Login = "subacc@shopee.vn";

    private static OrdersAccountsPushRequest Day(
        string machineId, string password = "", string verifyEmail = "", string verifyEmailPassword = "")
        => new(machineId, "pc-" + machineId, [
            new OrdersAccountItem(Login, "", [new OrdersShopItem("shop-a", "Shop A")], false, null)
            {
                Password = password,
                VerifyEmail = verifyEmail,
                VerifyEmailPassword = verifyEmailPassword,
            }
        ]);

    // ── Luật thuần ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("moi", "cu", "moi")]     // ô mới có chữ → thắng (user đổi mật khẩu thì Hub phải theo kịp)
    [InlineData("", "cu", "cu")]         // ô mới rỗng → GIỮ cũ (máy chưa nhập không được xoá dữ liệu máy khác)
    [InlineData("   ", "cu", "cu")]      // toàn khoảng trắng cũng là "chưa nhập"
    [InlineData(null, "cu", "cu")]
    [InlineData("moi", "", "moi")]
    [InlineData("", "", "")]
    [InlineData(null, null, "")]
    public void GiuLaiNeuRong_ORongKhongBaoGioXoaOCu(string? moi, string? cu, string mongDoi)
        => Assert.Equal(mongDoi, HubDatabase.GiuLaiNeuRong(moi, cu));

    [Theory]
    [InlineData("da-co", "them", "da-co")]   // gộp NGANG giữa các máy: ô đã có chữ thì giữ
    [InlineData("", "them", "them")]
    [InlineData("  ", "them", "them")]
    [InlineData("", "", "")]
    [InlineData("", null, "")]
    public void OCoChuDauTien_MayNaoNhapTruocThiThang(string? dangCo, string? themVao, string mongDoi)
        => Assert.Equal(mongDoi, HubDatabase.OCoChuDauTien(dangCo, themVao));

    // ── Vòng thật trên DB ───────────────────────────────────────────────────────

    /// <summary>Ca sống-chết: lượt 2 đẩy ô RỖNG (máy vừa bị xoá mật khẩu / bản ghi lỗi) → Hub PHẢI giữ nguyên.</summary>
    [Fact]
    public void DayLuot2VoiORong_HubVanGiuMatKhauLuot1()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertOrdersAccounts(Day(May1, "matkhau-1", "mail@hotmail.com", "matkhau-mail"));
        db.UpsertOrdersAccounts(Day(May1));   // lượt sau: ba ô rỗng

        var acc = Assert.Single(db.OrdersAccountsOf(May1));
        Assert.Equal("matkhau-1", acc.Password);
        Assert.Equal("mail@hotmail.com", acc.VerifyEmail);
        Assert.Equal("matkhau-mail", acc.VerifyEmailPassword);
    }

    /// <summary>Đổi mật khẩu ở client → Hub phải cập nhật, KHÔNG được đóng băng giá trị đầu tiên (nếu đóng băng
    /// thì Hub phát tán mật khẩu cũ sang mọi máy mới kéo về).</summary>
    [Fact]
    public void DayMatKhauMoiKhacRong_HubCapNhat()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertOrdersAccounts(Day(May1, "matkhau-cu"));
        db.UpsertOrdersAccounts(Day(May1, "matkhau-moi"));

        Assert.Equal("matkhau-moi", Assert.Single(db.OrdersAccountsOf(May1)).Password);
    }

    /// <summary>Máy 1 đã nhập, máy 2 chưa → danh bạ GỘP lấy được của máy 1. Đây chính là mục đích của cả tính
    /// năng: máy mới kéo về là dùng được ngay.</summary>
    [Fact]
    public void DanhBaGop_LayODaNhapCuaMayKhac()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertOrdersAccounts(Day(May1, "matkhau-1", "mail@hotmail.com", "matkhau-mail"));
        db.UpsertOrdersAccounts(Day(May2));   // máy 2 chưa nhập gì

        var gop = Assert.Single(db.AllOrdersAccountsDistinct());
        Assert.Equal("matkhau-1", gop.Password);
        Assert.Equal("mail@hotmail.com", gop.VerifyEmail);
        Assert.Equal("matkhau-mail", gop.VerifyEmailPassword);
    }

    /// <summary>Máy 2 đẩy ô rỗng KHÔNG được làm hỏng dòng của máy 1 (hợp đồng gương: không đụng máy khác).</summary>
    [Fact]
    public void MayKhacDayORong_KhongDungToiDongCuaMayNay()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertOrdersAccounts(Day(May1, "matkhau-1"));
        db.UpsertOrdersAccounts(Day(May2));

        Assert.Equal("matkhau-1", Assert.Single(db.OrdersAccountsOf(May1)).Password);
        Assert.Equal("", Assert.Single(db.OrdersAccountsOf(May2)).Password);
    }

    /// <summary>Chưa máy nào nhập → danh bạ gộp trả ô RỖNG (không null, không nổ) để client hiểu "phải tự nhập".</summary>
    [Fact]
    public void ChuaMayNaoNhap_DanhBaGopTraORong()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertOrdersAccounts(Day(May1));

        var gop = Assert.Single(db.AllOrdersAccountsDistinct());
        Assert.Equal("", gop.Password);
        Assert.Equal("", gop.VerifyEmail);
        Assert.Equal("", gop.VerifyEmailPassword);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }
}
