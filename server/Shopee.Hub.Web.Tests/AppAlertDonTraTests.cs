using Shopee.Hub.Web.Api;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// App-alert <c>Kind="don_tra"</c>: client gửi mã trả hàng của các đơn ĐÃ bị dọn khỏi app (hub không thấy qua
/// <c>orders/push</c> vì không còn đơn để so mã) trong <c>Detail</c> dạng <c>"SN=CODE; SN=CODE"</c>. Kèm mốc giờ
/// hiển thị: hub chạy VM giờ UTC nên tin nhắn PHẢI quy đổi sang giờ Việt Nam.
/// </summary>
public class AppAlertDonTraTests
{
    [Fact]
    public void TachCapDonTra_TachDungNhieuCap()
    {
        var caps = ClientApiEndpoints.TachCapDonTra("SN1=R001; SN2=R002");

        Assert.Equal(2, caps.Count);
        Assert.Equal(("SN1", "R001"), caps[0]);
        Assert.Equal(("SN2", "R002"), caps[1]);
    }

    [Fact]
    public void TachCapDonTra_BoPhanRac_KhongNem()
    {
        // Thiếu '=', vế rỗng hai bên, khoảng trắng thừa, dấu ';' thừa — bỏ hết, giữ cặp hợp lệ.
        var caps = ClientApiEndpoints.TachCapDonTra("  SN1 = R001 ;; khong-co-dau-bang ; =R9 ; SN2= ;");

        Assert.Equal(("SN1", "R001"), Assert.Single(caps));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TachCapDonTra_RongHoacNull_TraListRong(string? detail)
        => Assert.Empty(ClientApiEndpoints.TachCapDonTra(detail));

    // ===== Mốc giờ trong tin nhắn: quy đổi Asia/Ho_Chi_Minh, KHÔNG dùng giờ máy chủ (VM chạy UTC) =====
    [Fact]
    public void GioVietNam_DoiSangUtcCong7()
    {
        var utc = new DateTimeOffset(2026, 7, 30, 22, 30, 0, TimeSpan.Zero);

        var vn = GioVietNam.Doi(utc);

        Assert.Equal(TimeSpan.FromHours(7), vn.Offset);
        Assert.Equal(utc, vn);                                    // CÙNG một thời điểm, khác cách biểu diễn
        Assert.Equal("31/07 05:30", vn.ToString("dd/MM HH:mm"));  // 22:30 UTC = 5:30 sáng HÔM SAU ở VN
    }

    [Fact]
    public void GioVietNam_DinhDang_MocRong_TraChuoiRong()
    {
        Assert.Equal("", GioVietNam.DinhDang(default, "MM-dd HH:mm"));
        Assert.Equal("", GioVietNam.DinhDang(DateTimeOffset.MinValue, "MM-dd HH:mm"));
    }
}
