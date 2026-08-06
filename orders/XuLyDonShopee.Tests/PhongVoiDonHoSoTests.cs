using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Canh LUỒNG ĐIỀU KHIỂN của bước mở trình duyệt đăng nhập: <b>dọn hồ sơ trước MỖI lần phóng</b>, thử lại đúng
/// một lần và CHỈ cho ca "trình duyệt thoát sớm", không nuốt lỗi thật, không nuốt lệnh Dừng của user.
/// <para>Vì sao có bộ test này: bản vá gốc chỉ có test cho các hàm phụ trợ (dựng chuỗi lọc, xóa file) nên
/// gỡ nguyên bước dọn ra khỏi <c>LaunchAndConnectAsync</c> mà cả 1558 test vẫn xanh — phản biện 06/08 chứng
/// minh bằng đột biến. Bộ này khoá phần luồng.</para>
/// </summary>
public class PhongVoiDonHoSoTests
{
    private static Task<string> Ok(string v) => Task.FromResult(v);

    private static Exception ThoatSom() =>
        new LoginBrowserBootstrap.BrowserExitedEarlyException("thoát sớm");

    [Fact]
    public async Task ThanhCongLanDau_DonHoSoDungMotLan_KhongCho()
    {
        var don = new List<int>();
        var cho = new List<int>();

        var kq = await LoginBrowserBootstrap.PhongVoiDonHoSoAsync(
            donHoSo: don.Add,
            phongLan: _ => Ok("xong"),
            choTruocKhiThuLai: lan => { cho.Add(lan); return Task.CompletedTask; },
            soLanToiDa: 2,
            ct: default);

        Assert.Equal("xong", kq);
        Assert.Equal(new[] { 1 }, don);          // dọn TRƯỚC khi phóng, đúng 1 lần
        Assert.Empty(cho);
    }

    [Fact]
    public async Task ThoatSomLanDau_ThiDonLaiVaThuLan2()
    {
        var don = new List<int>();
        var cho = new List<int>();

        var kq = await LoginBrowserBootstrap.PhongVoiDonHoSoAsync(
            donHoSo: don.Add,
            phongLan: lan => lan == 1 ? throw ThoatSom() : Ok("lan2"),
            choTruocKhiThuLai: lan => { cho.Add(lan); return Task.CompletedTask; },
            soLanToiDa: 2,
            ct: default);

        Assert.Equal("lan2", kq);
        Assert.Equal(new[] { 1, 2 }, don);       // lần thử 2 PHẢI được dọn lại, không phóng chay
        Assert.Equal(new[] { 1 }, cho);          // có chờ settle giữa hai lần
    }

    [Fact]
    public async Task ThoatSomCaHaiLan_ThiNemRaNgoai_KhongLapVoTan()
    {
        var don = new List<int>();

        await Assert.ThrowsAsync<LoginBrowserBootstrap.BrowserExitedEarlyException>(() =>
            LoginBrowserBootstrap.PhongVoiDonHoSoAsync<string>(
                donHoSo: don.Add,
                phongLan: _ => throw ThoatSom(),
                choTruocKhiThuLai: _ => Task.CompletedTask,
                soLanToiDa: 2,
                ct: default));

        Assert.Equal(new[] { 1, 2 }, don);       // đúng 2 lần rồi dừng
    }

    [Fact]
    public async Task LoiKHAC_ThiNemNGAY_KhongThuLai()
    {
        // Thử lại một lỗi thật (mạng/CDP/Playwright) chỉ tổ tốn thêm một lần mở trình duyệt rồi vẫn hỏng.
        var don = new List<int>();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            LoginBrowserBootstrap.PhongVoiDonHoSoAsync<string>(
                donHoSo: don.Add,
                phongLan: _ => throw new TimeoutException("CDP không lên"),
                choTruocKhiThuLai: _ => Task.CompletedTask,
                soLanToiDa: 2,
                ct: default));

        Assert.Equal(new[] { 1 }, don);
    }

    [Fact]
    public async Task DaHuy_ThiNemNgay_ChuaKipDonHoSo()
    {
        // User bấm Dừng → không được đi giết trình duyệt rồi mở phiên mới.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var don = new List<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LoginBrowserBootstrap.PhongVoiDonHoSoAsync(
                donHoSo: don.Add,
                phongLan: _ => Ok("khong-nen-toi-day"),
                choTruocKhiThuLai: _ => Task.CompletedTask,
                soLanToiDa: 2,
                ct: cts.Token));

        Assert.Empty(don);
    }

    [Fact]
    public async Task HuyTrongLucCho_ThiThoat_KhongThuTiep()
    {
        using var cts = new CancellationTokenSource();
        var don = new List<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LoginBrowserBootstrap.PhongVoiDonHoSoAsync<string>(
                donHoSo: don.Add,
                phongLan: _ => throw ThoatSom(),
                choTruocKhiThuLai: _ => { cts.Cancel(); return Task.FromCanceled(cts.Token); },
                soLanToiDa: 2,
                ct: cts.Token));

        Assert.Equal(new[] { 1 }, don);
    }

    [Fact]
    public void SoLanThuMoTrinhDuyet_LaHai()
    {
        // Đường thật truyền hằng này vào soLanToiDa: 1 = mất hẳn lần thử lại, >2 = kéo dài vòng chết vô ích.
        Assert.Equal(2, LoginBrowserBootstrap.SoLanThuMoTrinhDuyet);
    }
}
