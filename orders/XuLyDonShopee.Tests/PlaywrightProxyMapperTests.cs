using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho hàm thuần <see cref="PlaywrightProxyMapper.ToPlaywrightProxy"/>.
/// </summary>
public class PlaywrightProxyMapperTests
{
    [Fact]
    public void Null_TraVeNull()
    {
        Assert.Null(PlaywrightProxyMapper.ToPlaywrightProxy(null));
    }

    [Fact]
    public void Http_KhongAuth_ServerDung_UsernameNull()
    {
        var entry = new ProxyEntry { Host = "1.2.3.4", Port = 8080, Type = ProxyType.Http };

        var proxy = PlaywrightProxyMapper.ToPlaywrightProxy(entry);

        Assert.NotNull(proxy);
        Assert.Equal("http://1.2.3.4:8080", proxy!.Server);
        Assert.Null(proxy.Username);
        Assert.Null(proxy.Password);
    }

    [Fact]
    public void Http_CoAuth_GanUsernamePassword()
    {
        var entry = new ProxyEntry
        {
            Host = "1.2.3.4",
            Port = 8080,
            Username = "user1",
            Password = "pass1",
            Type = ProxyType.Http
        };

        var proxy = PlaywrightProxyMapper.ToPlaywrightProxy(entry);

        Assert.NotNull(proxy);
        Assert.Equal("http://1.2.3.4:8080", proxy!.Server);
        Assert.Equal("user1", proxy.Username);
        Assert.Equal("pass1", proxy.Password);
    }

    [Fact]
    public void Socks5_ServerBatDauSocks5()
    {
        var entry = new ProxyEntry { Host = "9.9.9.9", Port = 1080, Type = ProxyType.Socks5 };

        var proxy = PlaywrightProxyMapper.ToPlaywrightProxy(entry);

        Assert.NotNull(proxy);
        Assert.StartsWith("socks5://", proxy!.Server);
        Assert.Equal("socks5://9.9.9.9:1080", proxy.Server);
    }
}
