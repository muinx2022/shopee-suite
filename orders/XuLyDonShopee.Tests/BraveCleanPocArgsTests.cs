using System;
using System.Linq;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho hàm thuần <see cref="BraveLaunchArgs.BuildCleanPocArgs"/> — args của đường POC "mở sạch":
/// KHÔNG remote-debugging-port, KHÔNG proxy, CÓ load-extension + start URL ở cuối.
/// </summary>
public class BraveCleanPocArgsTests
{
    private static IReadOnlyList<string> Build() =>
        BraveLaunchArgs.BuildCleanPocArgs(
            "C:/tmp/prof", "C:/ext/ext-mau", "https://banhang.shopee.vn/portal/shop");

    [Fact]
    public void KhongCo_RemoteDebuggingPort()
    {
        // Bất biến cốt lõi của POC: KHÔNG mở endpoint CDP (không có kênh để anti-bot soi / Playwright attach).
        Assert.DoesNotContain(Build(), a => a.StartsWith("--remote-debugging-port"));
    }

    [Fact]
    public void KhongCo_ProxyServer()
    {
        // POC mở trực tiếp IP máy (mirror Chrome mở tay chạy tốt) — không nhánh proxy.
        Assert.DoesNotContain(Build(), a => a.StartsWith("--proxy-server"));
    }

    [Fact]
    public void CoLoadExtension_DungDuongDan()
    {
        Assert.Contains("--load-extension=C:/ext/ext-mau", Build());
    }

    [Fact]
    public void CoUserDataDir_VaStartUrlOCuoi()
    {
        var args = Build();

        Assert.Contains("--user-data-dir=C:/tmp/prof", args);
        Assert.Equal("https://banhang.shopee.vn/portal/shop", args[^1]);
    }

    [Fact]
    public void DisableFeatures_CoDisableLoadExtensionCommandLineSwitch()
    {
        // POC luôn nạp extension → phải kèm cờ cho phép --load-extension trên Chrome/Brave 137+.
        Assert.Contains(Build(),
            a => a.StartsWith("--disable-features") && a.Contains("DisableLoadExtensionCommandLineSwitch"));
    }

    /// <summary>Hồ sơ POC cũng là hồ sơ Chrome bền → phải chặn tải model AI on-device (~4 GB/hồ sơ, đo 07/08/2026),
    /// và tất cả phải nằm trong ĐÚNG MỘT cờ (hai cờ thì Chromium chỉ nhận một, cờ cho phép load-extension mất).</summary>
    [Fact]
    public void DisableFeatures_ChanTaiModelAi_TrongDungMotCo()
    {
        var co = Assert.Single(Build(), a => a.StartsWith("--disable-features="));

        Assert.Contains("OptimizationGuideOnDeviceModel", co);
        Assert.Contains("OptimizationGuideModelDownloading", co);
        Assert.Contains("TextSafetyClassifier", co);
    }

    /// <summary>Model AI on-device được CÀI QUA COMPONENT UPDATER về gốc hồ sơ → chặn updater là đường chặn trực
    /// tiếp nhất (nhóm feature ở trên là lớp thứ hai). Bằng chứng: hồ sơ rò 3,98 GB đều là hồ sơ orders — đường
    /// phóng duy nhất từng thiếu cờ này.</summary>
    [Fact]
    public void CoChanComponentUpdater()
    {
        Assert.Contains("--disable-component-update", Build());
    }
    /// <summary>
    /// ⛔ Nhóm cờ chặn discard/freeze tab TUYỆT ĐỐI KHÔNG được có mặt. Test này là bản ĐẢO NGƯỢC của test cũ
    /// <c>ChanVutTabKhoiBoNho_CoTrongDisableFeatures</c> (thêm sáng 10/08/2026, gỡ chiều cùng ngày) — giữ lại ở
    /// dạng đảo để lần sau ai gặp lại lỗi "tab bị vứt khỏi bộ nhớ" thì không đi lại đúng vết xe đó.
    /// <para>
    /// Cái giá của nhóm cờ ấy: từ lúc thêm, KHÔNG vòng nào đi hết 12 shop nữa — trình duyệt sạch TỰ CHẾT giữa
    /// vòng, luôn rơi vào kỳ nghỉ 3–4' (đúng lúc máy đóng băng/vứt tab chạy), luôn sau ~23 phút. Bằng chứng:
    /// <c>⚠ Trình duyệt sạch (PID 22024) đã THOÁT lúc 11:54:38 — mã thoát -2147483645 (0x80000003)</c>.
    /// 0x80000003 = STATUS_BREAKPOINT = Chromium tự kết liễu vì CHECK thất bại — không phải hết RAM (còn
    /// 15,7 GB), không phải bị kill từ ngoài (0x40010004), không phải thoát êm (0).
    /// </para>
    /// <para>Lỗi tab discarded nguyên bản vẫn có lưới bên extension (nạp lại tab rồi thử lại một lượt), và ba
    /// vòng gần nhất không tái phát lần nào.</para>
    /// </summary>
    /// <summary>
    /// Cờ chặn "GPU chết đủ số lần thì giết cả trình duyệt" phải LUÔN có mặt. Không có nó thì trình duyệt sạch
    /// tự thoát sau ~23,5 phút với <c>FATAL ... GPU process isn't usable. Goodbye.</c> — đã đo bốn vòng liên
    /// tiếp ngày 10/08/2026, sai số 2 giây, và KHÔNG vòng nào đi hết 12 shop. Lỗi chỉ lộ sau hơn 20 phút chạy
    /// thật nên tuyệt đối không thể bắt bằng thử tay.
    /// </summary>
    [Fact]
    public void CoCoChanGietTrinhDuyetKhiGpuChet()
    {
        Assert.Contains("--disable-gpu-process-crash-limit", Build());
    }

    [Fact]
    public void KhongChanVutTabKhoiBoNho_VonLamTrinhDuyetTuChet()
    {
        var df = Build().Single(a => a.StartsWith("--disable-features="));
        foreach (var ten in new[]
        {
            "HighEfficiencyModeAvailable", "BatterySaverModeAvailable",
            "PerformanceControlsPerformanceInterventions", "FreezingOnEnergySaver", "ModernDiscardStrategy",
        })
        {
            Assert.DoesNotContain(ten, df, StringComparison.Ordinal);
        }
    }
}
