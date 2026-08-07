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
}
