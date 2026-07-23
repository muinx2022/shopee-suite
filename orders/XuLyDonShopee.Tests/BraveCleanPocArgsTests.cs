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
            "C:/tmp/prof", "C:/ext/shopee-orders-test", "https://banhang.shopee.vn/portal/shop");

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
        Assert.Contains("--load-extension=C:/ext/shopee-orders-test", Build());
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
}
