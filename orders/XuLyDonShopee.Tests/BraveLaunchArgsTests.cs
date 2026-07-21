using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho hàm thuần <see cref="BraveLaunchArgs.BuildBraveArgs"/> — chuỗi tham số launch Brave/Chromium.
/// </summary>
public class BraveLaunchArgsTests
{
    [Fact]
    public void CoUserDataDir_RemoteDebuggingPort_KhongEpWebdriver()
    {
        var args = BraveLaunchArgs.BuildBraveArgs(@"C:\profiles\1", 9222, null);

        // KHÔNG ép navigator.webdriver=false nữa (khớp shopee-suite — Shopee không gate theo webdriver).
        // Không còn cờ nào tắt AutomationControlled, cũng không dùng họ --disable-blink-features.
        Assert.DoesNotContain(args, a => a.Contains("--disable-blink-features"));
        Assert.DoesNotContain(args, a => a.Contains("AutomationControlled"));
        Assert.Contains(@"--user-data-dir=C:\profiles\1", args);
        Assert.Contains("--remote-debugging-port=9222", args);
    }

    [Fact]
    public void CoNhomCoChongTreoNen()
    {
        // Nhóm cờ khớp shopee-suite: chống Brave bóp renderer khi cửa sổ nền + mở đúng profile/cửa sổ mới.
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, null);

        Assert.Contains("--disable-background-timer-throttling", args);
        Assert.Contains("--disable-backgrounding-occluded-windows", args);
        Assert.Contains("--disable-renderer-backgrounding", args);
        Assert.Contains("--profile-directory=Default", args);
        Assert.Contains("--new-window", args);
        Assert.Contains("--hide-crash-restore-bubble", args);
    }

    [Fact]
    public void CoDisableFeaturesOnDinh_KhongCoAutomationControlled()
    {
        // Chuỗi --disable-features đúng như shopee-suite: Translate + CalculateNativeWinOcclusion +
        // IntensiveWakeUpThrottling — và tuyệt đối KHÔNG chứa AutomationControlled.
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, null);

        Assert.Contains("--disable-features=Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--disable-features") && a.Contains("AutomationControlled"));
    }

    [Fact]
    public void CoCoLocaleTiengViet()
    {
        // Locale VN đặt bằng cờ trình duyệt (không hook navigator.languages bằng JS để tránh lộ bot).
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, null);

        Assert.Contains("--lang=vi-VN", args);
    }

    [Fact]
    public void CoCoDisablePopupBlocking()
    {
        // Nút "In phiếu giao" mở tab phiếu bằng window.open — không chặn popup để tab phiếu luôn mở ra
        // (nếu bị chặn thì không bắt được tab để tải/in). Cờ này BẮT BUỘC có cho bước In phiếu giao.
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, null);

        Assert.Contains("--disable-popup-blocking", args);
    }

    [Fact]
    public void KhongChua_EnableAutomation_VaKhongChua_Headless()
    {
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, null);

        // Không có bất kỳ tham số nào bật cờ automation hoặc chạy ẩn.
        Assert.DoesNotContain(args, a => a.Contains("--enable-automation"));
        Assert.DoesNotContain(args, a => a.Contains("--headless"));
        Assert.DoesNotContain(args, a => a.Contains("--remote-debugging-pipe"));
    }

    [Fact]
    public void CoProxyHttp_ChuaProxyServerHttp()
    {
        var proxy = new ProxyEntry { Host = "1.2.3.4", Port = 8080, Type = ProxyType.Http };

        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, proxy);

        Assert.Contains("--proxy-server=http://1.2.3.4:8080", args);
    }

    [Fact]
    public void CoProxySocks5_ChuaProxyServerSocks5()
    {
        var proxy = new ProxyEntry { Host = "9.9.9.9", Port = 1080, Type = ProxyType.Socks5 };

        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, proxy);

        Assert.Contains("--proxy-server=socks5://9.9.9.9:1080", args);
    }

    [Fact]
    public void CoProxyCoUserPass_ProxyServerKhongKemUserPass()
    {
        // User/pass KHÔNG được nhét vào --proxy-server (Chromium không hỗ trợ) — xử lý auth qua CDP.
        var proxy = new ProxyEntry
        {
            Host = "1.2.3.4",
            Port = 8080,
            Username = "u",
            Password = "p",
            Type = ProxyType.Http
        };

        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, proxy);

        Assert.Contains("--proxy-server=http://1.2.3.4:8080", args);
        Assert.DoesNotContain(args, a => a.Contains("u:p@"));
        Assert.DoesNotContain(args, a => a.Contains("--proxy-server=http://u:p"));
    }

    [Fact]
    public void ProxyNull_KhongCoProxyServer()
    {
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, null);

        Assert.DoesNotContain(args, a => a.StartsWith("--proxy-server"));
    }
}
