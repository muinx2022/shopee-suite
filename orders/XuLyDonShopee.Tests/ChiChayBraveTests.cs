using Shopee.Toolkit.Browser;
using XuLyDonShopee.Core.Services;
// Hai assembly cùng có kiểu tên 'BrowserLocator' (bộ dò dùng chung ở Toolkit vs lớp của module Đơn hàng) →
// đặt bí danh cho bản của module để khỏi nhập nhằng.
using OrdersLocator = XuLyDonShopee.Core.Services.BrowserLocator;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Luật <b>"app chỉ chạy được bằng Brave"</b> (chốt 11/08/2026): cổng chặn lúc khởi động
/// (<see cref="BraveRequirement.LyDoChanKhoiDong"/>) và hằng hậu tố thư mục hồ sơ
/// (<see cref="OrdersLocator.LoaiHoSo"/>).
/// </summary>
public class ChiChayBraveTests
{
    // ── Cổng chặn khởi động ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void KhongCoBrave_ChanKhoiDong_VaCauBaoCoChoTai(string? bravePath)
    {
        var lyDo = BraveRequirement.LyDoChanKhoiDong(bravePath);

        Assert.NotNull(lyDo);
        // Câu báo phải nói CÁCH KHẮC PHỤC, không chỉ "không tìm thấy trình duyệt": người dùng gặp hộp thoại này
        // là lúc app vừa từ chối mở, không có chỗ nào khác để tra.
        Assert.Contains("Brave", lyDo);
        Assert.Contains("brave.com/download", lyDo);
    }

    [Fact]
    public void CoBrave_KhongChan()
    {
        Assert.Null(BraveRequirement.LyDoChanKhoiDong(@"C:\Program Files\BraveSoftware\brave.exe"));
    }

    /// <summary>Một câu chữ DUY NHẤT cho cả cổng khởi động lẫn lúc phóng trình duyệt — người dùng không được
    /// thấy hai cách diễn đạt khác nhau cho cùng một bệnh.</summary>
    [Fact]
    public void HaiPhiaDungChungMotCauBao()
    {
        Assert.Equal(BraveRequirement.ThieuBraveMessage, OrdersLocator.ThieuBraveMessage);
    }

    // ── Hậu tố thư mục hồ sơ ────────────────────────────────────────────────────

    /// <summary>
    /// KHOÁ CHUỖI <c>"brave"</c>. Đây là một phần tên thư mục hồ sơ ĐANG TỒN TẠI trên máy người dùng
    /// (<c>…\profiles\1-brave</c>), nơi giữ cookie đăng nhập subaccount. Trước 11/08/2026 chuỗi này do
    /// <c>ResolveBrowserKind</c> tính theo lựa chọn trình duyệt; khi bỏ lựa chọn, hằng thay thế mà lệch một ký
    /// tự là mọi tài khoản trỏ sang thư mục RỖNG ⇒ phải đăng nhập lại từ đầu và ăn captcha. Test này đứng đó để
    /// một lần "dọn dẹp đặt tên" trong tương lai không âm thầm làm việc đó.
    /// </summary>
    [Fact]
    public void LoaiHoSo_PhaiLaChuoiBrave_KhongDuocDoi()
    {
        Assert.Equal("brave", OrdersLocator.LoaiHoSo);
    }

    [Fact]
    public void DuongDanHoSo_GiuNguyenDangCu()
    {
        var dir = BrowserProfilePaths.ForAccount(@"C:\data", 1, OrdersLocator.LoaiHoSo);

        Assert.Equal(System.IO.Path.Combine(@"C:\data", "profiles", "1-brave"), dir);
    }
}
