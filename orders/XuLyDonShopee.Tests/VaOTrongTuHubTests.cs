using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Luật <b>"vá ô trống, KHÔNG đè"</b> khi kéo danh bạ từ Hub về
/// (<see cref="HubDirectoryPuller.VaOTrong"/>, thêm 11/08/2026).
/// <para>Đây là chốt an toàn của cả chiều Hub → client: Hub giữ 3 ô đăng nhập gộp từ mọi máy, nhưng giá trị
/// Hub gửi về có thể CŨ (máy khác chưa đẩy mật khẩu vừa đổi). Đè lên thứ người dùng vừa gõ trên máy này =
/// tài khoản đang chạy tốt bỗng đăng nhập sai — mà không có dòng log nào nói vì sao.</para>
/// </summary>
public class VaOTrongTuHubTests
{
    private static Account Acc(string pass = "", string vmail = "", string vpass = "") => new()
    {
        Email = "subacc@shopee.vn",
        Password = pass,
        VerifyEmail = vmail,
        VerifyEmailPassword = vpass,
    };

    [Fact]
    public void OTrong_DuocVaBangGiaTriHub()
    {
        var acc = Acc();

        var va = HubDirectoryPuller.VaOTrong(acc, "mk-hub", "mail@hotmail.com", "mk-mail-hub");

        Assert.Equal(3, va);
        Assert.Equal("mk-hub", acc.Password);
        Assert.Equal("mail@hotmail.com", acc.VerifyEmail);
        Assert.Equal("mk-mail-hub", acc.VerifyEmailPassword);
    }

    /// <summary>Ca sống-chết: ô đã có chữ thì Hub KHÔNG được đụng, kể cả khi Hub có giá trị khác.</summary>
    [Fact]
    public void ODaCoChu_TuyetDoiKhongBiDe()
    {
        var acc = Acc("mk-may", "mail-may@hotmail.com", "mk-mail-may");

        var va = HubDirectoryPuller.VaOTrong(acc, "mk-hub", "mail-hub@hotmail.com", "mk-mail-hub");

        Assert.Equal(0, va);
        Assert.Equal("mk-may", acc.Password);
        Assert.Equal("mail-may@hotmail.com", acc.VerifyEmail);
        Assert.Equal("mk-mail-may", acc.VerifyEmailPassword);
    }

    /// <summary>Vá TỪNG Ô độc lập: có mật khẩu rồi nhưng chưa có hòm thư xác minh thì chỉ ô hòm thư được vá.</summary>
    [Fact]
    public void VaTungODocLap_KhongPhaiTatCaHoacKhongGi()
    {
        var acc = Acc(pass: "mk-may");

        var va = HubDirectoryPuller.VaOTrong(acc, "mk-hub", "mail@hotmail.com", "mk-mail-hub");

        Assert.Equal(2, va);
        Assert.Equal("mk-may", acc.Password);              // giữ
        Assert.Equal("mail@hotmail.com", acc.VerifyEmail); // vá
        Assert.Equal("mk-mail-hub", acc.VerifyEmailPassword);
    }

    /// <summary>Hub cũng rỗng (chưa máy nào nhập) → không vá gì, và KHÔNG được xoá ô đang có.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void HubGuiORong_KhongVaVaKhongXoa(string? tuHub)
    {
        var acc = Acc("mk-may", "mail-may@hotmail.com", "mk-mail-may");

        Assert.Equal(0, HubDirectoryPuller.VaOTrong(acc, tuHub, tuHub, tuHub));
        Assert.Equal("mk-may", acc.Password);
        Assert.Equal("mail-may@hotmail.com", acc.VerifyEmail);
        Assert.Equal("mk-mail-may", acc.VerifyEmailPassword);
    }

    /// <summary>Ô local toàn khoảng trắng tính là TRỐNG (form có thể để lại một dấu cách).</summary>
    [Fact]
    public void OLocalToanKhoangTrang_TinhLaTrong()
    {
        var acc = Acc(pass: "   ");

        Assert.Equal(1, HubDirectoryPuller.VaOTrong(acc, "mk-hub", "", ""));
        Assert.Equal("mk-hub", acc.Password);
    }

    /// <summary>Trả về SỐ Ô vừa vá — caller dùng số này để quyết định có ghi DB hay không (0 ⇒ khỏi ghi).</summary>
    [Fact]
    public void KhongCoGiDeVa_TraVeKhong()
    {
        Assert.Equal(0, HubDirectoryPuller.VaOTrong(Acc("mk"), "mk-hub", null, null));
    }
}
