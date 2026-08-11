using Shopee.Core.Browser;

namespace Shopee.Core.Tests;

/// <summary>
/// Tách <c>--user-data-dir</c> khỏi dòng lệnh trình duyệt (<see cref="BraveProcessReaper.ExtractUserDataDir"/>).
/// <para>Ca sống-chết là <b>đường dẫn CÓ DẤU CÁCH</b>: hồ sơ của app nằm dưới
/// <c>C:\Users\&lt;tên người dùng&gt;\AppData\…</c> và tên người dùng thật trên máy dev có dấu cách. Bản trước
/// 11/08/2026 trả về <c>C:\Users\Ng</c> ở dạng ngoặc bọc-cả-tham-số (dạng mà
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> sinh ra — chính là đường phía orders dùng),
/// nên <c>BraveFleet</c> không nhận ra trình duyệt của app và bước dọn hồ sơ mồ côi lúc khởi động im lặng
/// không làm gì suốt nhiều bản.</para>
/// </summary>
public class TachDuongDanHoSoTests
{
    private const string CoCach = @"C:\Users\Ng Xuan Mui\AppData\Roaming\XuLyDonShopee\profiles\acc_1";
    private const string KhongCach = @"C:\App\persistent-data\acc_1";

    private const string CoDuoi = " --profile-directory=Default --no-first-run";

    // ── Ba dạng ngoặc, đường dẫn CÓ dấu cách ────────────────────────────────────

    /// <summary>Dạng .NET <c>ArgumentList</c> sinh ra: ngoặc bọc CẢ tham số. Đây là ca đã hỏng.</summary>
    [Fact]
    public void NgoacBocCaThamSo_CoDauCach_TraDuDuongDan()
    {
        var cmd = $"\"C:\\Program Files\\BraveSoftware\\brave.exe\" \"--user-data-dir={CoCach}\"{CoDuoi}";
        Assert.Equal(CoCach, BraveProcessReaper.ExtractUserDataDir(cmd));
    }

    /// <summary>Dạng phía suite tự bọc: ngoặc SAU dấu <c>=</c>.</summary>
    [Fact]
    public void NgoacSauDauBang_CoDauCach_TraDuDuongDan()
    {
        var cmd = $"brave.exe --user-data-dir=\"{CoCach}\"{CoDuoi}";
        Assert.Equal(CoCach, BraveProcessReaper.ExtractUserDataDir(cmd));
    }

    /// <summary>Không ngoặc + không dấu cách — dạng duy nhất bản cũ làm đúng, phải giữ nguyên.</summary>
    [Fact]
    public void KhongNgoac_KhongDauCach_TraDuDuongDan()
    {
        var cmd = $"brave.exe --user-data-dir={KhongCach}{CoDuoi}";
        Assert.Equal(KhongCach, BraveProcessReaper.ExtractUserDataDir(cmd));
    }

    // ── Cờ nằm CUỐI dòng lệnh (không có tham số nào theo sau) ───────────────────

    [Fact]
    public void CoDungCuoiDong_NgoacBocCaThamSo_TraDuDuongDan()
    {
        var cmd = $"brave.exe \"--user-data-dir={CoCach}\"";
        Assert.Equal(CoCach, BraveProcessReaper.ExtractUserDataDir(cmd));
    }

    [Fact]
    public void CoDungCuoiDong_KhongNgoac_TraDuDuongDan()
    {
        Assert.Equal(KhongCach, BraveProcessReaper.ExtractUserDataDir($"brave.exe --user-data-dir={KhongCach}"));
    }

    // ── Ca rỗng / không có cờ ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("brave.exe --profile-directory=Default")]
    [InlineData("brave.exe --user-data-dir=")]            // cờ có mà giá trị rỗng
    [InlineData("brave.exe \"--user-data-dir=\"")]
    public void KhongCoGiaTri_TraNull(string cmd)
    {
        Assert.Null(BraveProcessReaper.ExtractUserDataDir(cmd));
    }

    /// <summary>Tiến trình con (renderer/gpu) mang cờ dài loằng ngoằng nhưng vẫn ĐÚNG một tham số
    /// <c>--user-data-dir</c> — không được lẫn sang cờ khác có chứa đường dẫn.</summary>
    [Fact]
    public void CoNhieuCoKhacCungMangDuongDan_VanLayDungCoUserDataDir()
    {
        var cmd = $"\"C:\\Program Files\\BraveSoftware\\brave.exe\" --type=renderer "
                + $"\"--user-data-dir={CoCach}\" \"--log-file=C:\\Users\\Ng Xuan Mui\\log.txt\" --mute-audio";
        Assert.Equal(CoCach, BraveProcessReaper.ExtractUserDataDir(cmd));
    }

    // ── Hàm tách tham số (dùng chung) ───────────────────────────────────────────

    [Fact]
    public void TachThamSo_GiuNguyenDauCachTrongNgoac_VaCatDungNgoaiNgoac()
    {
        var parts = BraveProcessReaper.TachThamSo("\"C:\\Program Files\\a.exe\" --x=1  \"b c\" --y").ToList();
        Assert.Equal(new[] { @"C:\Program Files\a.exe", "--x=1", "b c", "--y" }, parts);
    }
}
