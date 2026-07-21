using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Chỉ test hàm thuần <see cref="ProxyHealthChecker.ToProxyAddress"/> (không test HTTP thật — cần mạng).
/// </summary>
public class ProxyHealthCheckerTests
{
    [Fact]
    public void ToProxyAddress_Http_TraVeHttpScheme()
    {
        var p = new ProxyEntry { Host = "h", Port = 8080, Type = ProxyType.Http };

        Assert.Equal("http://h:8080", ProxyHealthChecker.ToProxyAddress(p));
    }

    [Fact]
    public void ToProxyAddress_Socks5_TraVeSocks5Scheme()
    {
        var p = new ProxyEntry { Host = "h", Port = 1080, Type = ProxyType.Socks5 };

        Assert.Equal("socks5://h:1080", ProxyHealthChecker.ToProxyAddress(p));
    }
}
