using Shopee.Core.Browser;

namespace Shopee.Core.Tests;

/// <summary>
/// Luật "trình duyệt nào bị coi là MỒ CÔI cần giết" của <see cref="BraveFleet"/> (hàm thuần
/// <c>LaMoCoiCanGiet</c>). Ca quan trọng nhất: hồ sơ của module Đơn hàng — module đó KHÔNG đăng ký hồ sơ
/// đang chạy nên trình duyệt đang làm việc trông y hệt mồ côi; root của nó phải được đánh dấu "chỉ quét lúc
/// khởi động" thì nhịp dọn định kỳ mới không giết nhầm.
/// </summary>
public class BraveFleetMoCoiTests
{
    private const string RootSuite = @"C:\App\persistent-data";
    private const string RootDonHang = @"C:\Users\Ng Xuan Mui\AppData\Roaming\XuLyDonShopee\profiles";

    private static readonly string[] DinhKy = { RootSuite };
    private static readonly string[] ChiKhoiDong = { RootDonHang };
    private static readonly string[] KhongCoRoot = System.Array.Empty<string>();

    private static readonly TimeSpan Gia = TimeSpan.FromMinutes(30);

    private static bool MoCoi(
        string dir,
        TimeSpan? tuoi = null,
        bool quetLucKhoiDong = false,
        IEnumerable<string>? dinhKy = null,
        IEnumerable<string>? chiKhoiDong = null,
        IEnumerable<string>? dangHoatDong = null)
        => BraveFleet.LaMoCoiCanGiet(
            dir,
            tuoi ?? Gia,
            quetLucKhoiDong,
            dinhKy ?? DinhKy,
            chiKhoiDong ?? ChiKhoiDong,
            dangHoatDong ?? KhongCoRoot);

    [Fact]
    public void HoSoDonHang_DangChay_KhongBiNhipDinhKyCoiLaMoCoi()
    {
        // Đây là lỗ đã có: root Đơn hàng được đăng ký nhưng module không bao giờ RegisterActiveProfile.
        Assert.False(MoCoi(RootDonHang + @"\1-brave"));
    }

    [Fact]
    public void HoSoDonHang_LuotKhoiDong_VanBiDon()
    {
        // Không được đánh đổi việc dọn rác lần chạy trước lấy sự an toàn của nhịp định kỳ.
        Assert.True(MoCoi(RootDonHang + @"\1-brave", quetLucKhoiDong: true));
    }

    [Fact]
    public void HoSoSuite_KhongDangKySong_LaMoCoi()
    {
        Assert.True(MoCoi(RootSuite + @"\acc_1"));
    }

    [Fact]
    public void HoSoSuite_DangDangKySong_ThiChua()
    {
        Assert.False(MoCoi(RootSuite + @"\acc_1", dangHoatDong: new[] { RootSuite + @"\acc_1" }));
    }

    [Fact]
    public void HoSoSuite_DangDangKySong_KhopKhongPhanBietHoaThuong()
    {
        Assert.False(MoCoi(RootSuite + @"\acc_1", dangHoatDong: new[] { RootSuite.ToUpperInvariant() + @"\ACC_1" }));
    }

    [Fact]
    public void TienTrinhVuaSinh_ThiChua()
    {
        Assert.False(MoCoi(RootSuite + @"\acc_1", tuoi: TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void TienTrinhDuTuoi_ThiGiet()
    {
        Assert.True(MoCoi(RootSuite + @"\acc_1", tuoi: TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void KhongDocDuocTuoi_ThiVanGiet()
    {
        // started = null (không mở được tiến trình) — giữ đúng hành vi cũ: chỉ tuổi ĐO ĐƯỢC và còn non mới chừa.
        Assert.True(MoCoi(RootSuite + @"\acc_1", tuoi: null));
    }

    [Theory]
    [InlineData(@"C:\Users\Ng Xuan Mui\AppData\Local\BraveSoftware\Brave-Browser\User Data")] // Brave cá nhân
    [InlineData(@"C:\App\persistent-data-khac\acc_1")]                                        // trùng TIỀN TỐ, khác root
    [InlineData(@"C:\Users\Ng")]                                                              // path cụt
    public void NgoaiMoiRoot_TuyetDoiKhongDung(string dir)
    {
        Assert.False(MoCoi(dir));
        Assert.False(MoCoi(dir, quetLucKhoiDong: true));
    }

    [Fact]
    public void ChinhRoot_CungLaMucTieu()
    {
        // SweepOrphans vốn coi cả chính thư mục root là "dưới root" (IsUnderRoot khớp cả bằng nhau).
        Assert.True(MoCoi(RootSuite));
    }

    [Fact]
    public void DirRong_ThiBoQua()
    {
        Assert.False(MoCoi(""));
        Assert.False(MoCoi("   ", quetLucKhoiDong: true));
    }

    [Fact]
    public void RootVuaDinhKyVuaChiKhoiDong_ThiDinhKyThang()
    {
        // Ca biên: cùng một root lọt vào cả hai danh sách → giữ hành vi quét định kỳ (không âm thầm tắt lưới).
        Assert.True(MoCoi(RootSuite + @"\acc_1", chiKhoiDong: new[] { RootSuite }));
    }
}
