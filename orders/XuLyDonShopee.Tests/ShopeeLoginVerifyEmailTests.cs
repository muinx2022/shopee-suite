using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm thuần của luồng verify-email trong <see cref="ShopeeLoginService"/>: lọc mail
/// "Cảnh báo bảo mật", chỉ khớp link "TẠI ĐÂY" (KHÔNG khớp "here"), và nhận diện trang link hết hạn.
/// </summary>
public class ShopeeLoginVerifyEmailTests
{
    // ===== NormalizeForMatch: bỏ dấu (kể cả đ→d), gộp space, hạ chữ thường =====
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Cảnh Báo Bảo Mật", "canh bao bao mat")]
    [InlineData("Cảnh  báo\nbảo\tmật", "canh bao bao mat")]
    [InlineData("Đơn hàng", "don hang")]
    [InlineData("HẾT HIỆU LỰC", "het hieu luc")]
    public void NormalizeForMatch_BoDauGopSpaceHaChu(string? input, string expected)
    {
        Assert.Equal(expected, ShopeeLoginService.NormalizeForMatch(input));
    }

    // ===== IsSecurityWarningMailRow: chỉ mail người-gửi shopee + tiêu đề chứa "cảnh báo bảo mật" =====
    [Theory]
    // Mail cảnh báo bảo mật (có/không dấu, hoa/thường) → giữ.
    [InlineData("Shopee\nCảnh báo bảo mật Tài khoản Shopee\nXin chào, chúng tôi phát hiện...", true)]
    [InlineData("SHOPEE\nCANH BAO BAO MAT TAI KHOAN SHOPEE\n...", true)]
    [InlineData("Shopee Việt Nam · Cảnh Báo Bảo Mật · nhấn TẠI ĐÂY", true)]
    // Mail Shopee khác (trả hàng/khuyến mãi) — dù có link "tại đây" vẫn LOẠI.
    [InlineData("Shopee\nĐơn trả hàng thành công\nXem chi tiết tại đây", false)]
    [InlineData("Shopee\nƯu đãi tháng 7 dành cho bạn", false)]
    // Người gửi KHÔNG phải shopee → loại (dù có chữ "cảnh báo bảo mật").
    [InlineData("Facebook\nCảnh báo bảo mật tài khoản của bạn", false)]
    // Rỗng/null → loại.
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSecurityWarningMailRow_ChiGiuMailCanhBaoBaoMat(string? rowText, bool expected)
    {
        Assert.Equal(expected, ShopeeLoginService.IsSecurityWarningMailRow(rowText));
    }

    // ===== MatchesConfirmLink: khớp "TẠI ĐÂY"/cụm xác nhận VN, KHÔNG khớp "here"/"click here" =====
    [Theory]
    [InlineData("TẠI ĐÂY", true)]
    [InlineData("tại đây", true)]
    [InlineData("tai day", true)]
    [InlineData("Xác nhận", true)]
    [InlineData("Đúng là tôi", true)]
    [InlineData("Nhấn vào đây", true)]
    public void MatchesConfirmLink_KhopTaiDayVaCumXacNhan(string text, bool expected)
    {
        Assert.Equal(expected, ShopeeLoginService.MatchesConfirmLink(text));
    }

    [Theory]
    // Sau khi bỏ nhánh "here"/"click here": các chuỗi có "here" mà KHÔNG có cụm xác nhận VN → false.
    [InlineData("here")]
    [InlineData("click here")]
    [InlineData("Click here to view your return")]
    [InlineData("there was a problem")]
    [InlineData("where is my order")]
    [InlineData("")]
    [InlineData(null)]
    public void MatchesConfirmLink_KhongCongKhopHere(string? text)
    {
        Assert.False(ShopeeLoginService.MatchesConfirmLink(text));
    }

    // ===== MatchesConfirmExpired: nhận diện trang link hết hạn/hết hiệu lực =====
    [Theory]
    [InlineData("Liên kết xác thực đã hết hiệu lực", true)]
    [InlineData("Liên kết xác thực gửi qua Email đã hết hạn", true)]
    [InlineData("Lien ket da het hieu luc", true)]
    [InlineData("This verification link has expired", true)]
    [InlineData("The link is no longer valid", true)]
    // Trang thành công / khác → KHÔNG phải hết hạn.
    [InlineData("Xác nhận đăng nhập thành công", false)]
    [InlineData("Đăng nhập thành công", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesConfirmExpired_NhanDienTrangHetHan(string? text, bool expected)
    {
        Assert.Equal(expected, ShopeeLoginService.MatchesConfirmExpired(text));
    }
}
