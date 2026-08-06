using Shopee.Core.BigSeller;
using Shopee.Core.Scrape;

namespace Shopee.Core.Tests;

/// <summary>
/// Khoảng nghỉ giữa 2 link của Scrape (H3.2) — trước đợt này là 2 hằng cứng 120_000/240_000 ms trong
/// <c>LauncherRunnerLoop</c>. Bất biến QUAN TRỌNG NHẤT: <b>người dùng chưa đụng cấu hình mới thì hành vi
/// KHÔNG ĐỔI</b> (vẫn 120–240s) — cả khi cấu hình cũ trên đĩa không có 2 field này (giá trị 0), cả khi Hub
/// giao việc mà không đặt tham số (quy ước 0 = dùng cấu hình client).
/// </summary>
public sealed class ScrapeRestWindowTests
{
    // ===== 1. MẶC ĐỊNH = đúng 2 hằng cũ (120–240s) =====
    [Fact]
    public void MacDinh_DungBangHaiHangCu()
    {
        Assert.Equal(120, ScrapeRestWindow.DefaultMinSeconds);
        Assert.Equal(240, ScrapeRestWindow.DefaultMaxSeconds);

        var w = ScrapeRestWindow.Default;
        Assert.Equal(120_000, w.MinMs);
        Assert.Equal(240_000, w.MaxMs);
    }

    // ===== 2. Quy ước 0 = dùng mặc định (khớp khuôn hub-run-params) =====
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 240)]     // chỉ đặt max đúng bằng mặc định
    [InlineData(-5, -1)]     // số âm (ô nhập gõ bậy) cũng coi như "không đặt"
    public void KhongDat_VeMacDinh(int min, int max)
    {
        var w = ScrapeRestWindow.Resolve(min, max);
        Assert.Equal(120, w.MinSeconds);
        Assert.Equal(240, w.MaxSeconds);
    }

    // ===== 3. Chỉ đặt MỘT vế: vế còn lại vẫn là mặc định =====
    [Fact]
    public void ChiDatMin_MaxGiuMacDinh()
    {
        var w = ScrapeRestWindow.Resolve(30, 0);
        Assert.Equal(30, w.MinSeconds);
        Assert.Equal(240, w.MaxSeconds);
    }

    /// <summary>Chỉ đặt MAX (min bỏ trống) và max NHỎ HƠN mặc định 120: min phải HẠ theo max, KHÔNG được lấy
    /// mặc định 120 rồi kéo max lên — người gõ 60 để nghỉ ngắn lại mà thành nghỉ 120s cố định là ngược ý,
    /// lại mất luôn tính ngẫu nhiên (thứ chống bot).</summary>
    [Fact]
    public void ChiDatMax_NhoHonMacDinh_MinHaTheoMax_VanNgauNhien()
    {
        var w = ScrapeRestWindow.Resolve(0, 60);
        Assert.Equal(60, w.MaxSeconds);
        Assert.True(w.MinSeconds <= 60, $"min phải ≤ max, đang là {w.MinSeconds}");
        Assert.True(w.MinSeconds < w.MaxSeconds || w.MaxSeconds == w.MinSeconds && w.MaxSeconds == 60,
            "không được biến thành khoảng cố định 120–120");
    }

    /// <summary>Chỉ đặt MAX nhưng LỚN hơn mặc định: min vẫn là mặc định 120 (không hạ vô cớ).</summary>
    [Fact]
    public void ChiDatMax_LonHonMacDinh_MinGiuMacDinh()
    {
        var w = ScrapeRestWindow.Resolve(0, 300);
        Assert.Equal(120, w.MinSeconds);
        Assert.Equal(300, w.MaxSeconds);
    }

    // ===== 4. max < min → kéo max lên bằng min (không bao giờ trả khoảng ngược) =====
    [Fact]
    public void MaxNhoHonMin_KeoMaxLenBangMin()
    {
        var w = ScrapeRestWindow.Resolve(300, 60);
        Assert.Equal(300, w.MinSeconds);
        Assert.Equal(300, w.MaxSeconds);
    }

    // ===== 5. Kẹp trong [1, 3600] giây =====
    [Fact]
    public void KepTranVaSan()
    {
        var w = ScrapeRestWindow.Resolve(99_999, 99_999);
        Assert.Equal(ScrapeRestWindow.MaxAllowedSeconds, w.MinSeconds);
        Assert.Equal(ScrapeRestWindow.MaxAllowedSeconds, w.MaxSeconds);
    }

    // ===== 6. NextRestMs luôn nằm trong khoảng (bao gồm 2 cận) =====
    [Fact]
    public void NextRestMs_LuonTrongKhoang()
    {
        var w = ScrapeRestWindow.Resolve(2, 5);
        var rnd = new Random(12345);
        for (var i = 0; i < 500; i++)
        {
            var ms = w.NextRestMs(rnd);
            Assert.InRange(ms, 2_000, 5_000);
        }
    }

    /// <summary>min = max → khoảng nghỉ CỐ ĐỊNH, không ném (Random.Next(a, a+1) hợp lệ).</summary>
    [Fact]
    public void MinBangMax_NghiCoDinh()
    {
        var w = ScrapeRestWindow.Resolve(10, 10);
        Assert.Equal(10_000, w.NextRestMs(new Random(1)));
    }

    // ===== 7. Cấu hình chạy MỚI TINH (chưa ai đụng) = mặc định =====
    [Fact]
    public void RunConfigMoi_MangSanMacDinh()
    {
        var cfg = new BigSellerRunConfig();
        Assert.Equal(120, cfg.RestMinSeconds);
        Assert.Equal(240, cfg.RestMaxSeconds);
    }

    /// <summary>bigseller.json CŨ (không có 2 field) → deserialize giữ nguyên khởi tạo 120/240; và kể cả nếu
    /// giá trị về 0 bằng đường nào khác, Resolve vẫn kéo lại mặc định ⇒ hành vi mặc định không đổi.</summary>
    [Fact]
    public void JsonCu_ThieuField_VanNghi120Den240()
    {
        var cfg = System.Text.Json.JsonSerializer.Deserialize<BigSellerRunConfig>(
            """{"StartRow":2,"EndRow":0,"Processes":3,"FrameSize":10,"RowsPerAccount":60,"ReloadSeconds":20}""")!;
        Assert.Equal(120, cfg.RestMinSeconds);
        Assert.Equal(240, cfg.RestMaxSeconds);

        var w = ScrapeRestWindow.Resolve(cfg.RestMinSeconds, cfg.RestMaxSeconds);
        Assert.Equal(120_000, w.MinMs);
        Assert.Equal(240_000, w.MaxMs);
    }

    /// <summary>Khoảng nghỉ là cấu hình RIÊNG-MÁY (nằm trên <c>BigSellerAccount.RunConfig</c>) → TUYỆT ĐỐI
    /// không được lọt vào chữ ký sync, kẻo đặt trên client là bị bản Hub đè ngược (lớp lỗi đã lặp nhiều lần).</summary>
    [Fact]
    public void KhoangNghi_KhongLotVaoChuKySync()
    {
        var a = new BigSellerAccount { Id = "acc1", Label = "A" };
        a.RunConfig = new BigSellerRunConfig();
        var truoc = Infrastructure.BackupService.SharedSignature(a);

        a.RunConfig.RestMinSeconds = 7;
        a.RunConfig.RestMaxSeconds = 9;
        Assert.Equal(truoc, Infrastructure.BackupService.SharedSignature(a));
    }
}
