using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm thuần khớp text của luồng đăng nhập qua Nền tảng tài khoản phụ (subaccount.shopee.com):
/// nhận diện nav "Tài khoản của tôi" (tín hiệu ĐÃ đăng nhập) và entry "Kênh Người bán" (mở Seller Centre).
/// Cả hai matcher chuẩn hóa không dấu (<c>NormalizeForMatch</c>) rồi khớp regex nên chịu được có/không dấu,
/// chữ HOA, và khoảng trắng thừa như InnerText thật.
/// </summary>
public class SubaccountNavMatchTests
{
    // ===== MatchesMyAccountNav: đúng với "Tài khoản của tôi" (mọi biến thể), sai với mục nav khác =====
    [Theory]
    [InlineData("Tài khoản của tôi", true)]
    [InlineData(" Tài khoản của tôi ", true)]            // space thừa như InnerText thật
    [InlineData("Tài khoản\n của tôi", true)]            // newline thừa (NormalizeForMatch gộp space)
    [InlineData("tai khoan cua toi", true)]              // không dấu
    [InlineData("TÀI KHOẢN CỦA TÔI", true)]              // chữ HOA
    [InlineData("My Account", true)]                     // tiếng Anh
    [InlineData("Phân bổ chat", false)]                  // mục nav khác — đừng khớp nhầm
    [InlineData("Tài khoản", false)]                     // thiếu "của tôi"
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesMyAccountNav_ChiKhopTaiKhoanCuaToi(string? text, bool expected)
    {
        Assert.Equal(expected, ShopeeLoginService.MatchesMyAccountNav(text));
    }

    // ===== MatchesSellerChannelEntry: đúng với "Kênh Người bán"/"Seller Centre", sai với entry khác =====
    [Theory]
    [InlineData("Kênh Người bán", true)]
    [InlineData("Kênh Người bán\n", true)]               // InnerText của span có icon chevron
    [InlineData("kenh nguoi ban", true)]                 // không dấu
    [InlineData("KÊNH NGƯỜI BÁN", true)]                 // chữ HOA
    [InlineData("Seller Centre", true)]                  // en (Anh-Anh)
    [InlineData("Seller Center", true)]                  // en (Anh-Mỹ)
    [InlineData("Kênh", false)]                          // thiếu "Người bán"
    [InlineData("Hiệu quả hoạt động CSKH", false)]       // entry khác
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesSellerChannelEntry_ChiKhopKenhNguoiBan(string? text, bool expected)
    {
        Assert.Equal(expected, ShopeeLoginService.MatchesSellerChannelEntry(text));
    }
}
