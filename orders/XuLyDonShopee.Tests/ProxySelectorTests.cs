using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test logic điều phối <see cref="ProxySelector.SelectKiotProxyAsync"/> bằng stub client + checker.
/// </summary>
public class ProxySelectorTests
{
    /// <summary>Stub client: trả proxy cấu hình sẵn cho current/new, đếm số lần gọi.</summary>
    private sealed class StubKiot : IKiotProxyClient
    {
        private readonly ProxyEntry? _current;
        private readonly ProxyEntry? _new;
        public int CurrentCalls { get; private set; }
        public int NewCalls { get; private set; }

        public StubKiot(ProxyEntry? current, ProxyEntry? @new)
        {
            _current = current;
            _new = @new;
        }

        public Task<ProxyEntry?> GetCurrentProxyAsync(CancellationToken cancellationToken = default)
        {
            CurrentCalls++;
            return Task.FromResult(_current);
        }

        public Task<ProxyEntry?> GetNewProxyAsync(CancellationToken cancellationToken = default)
        {
            NewCalls++;
            return Task.FromResult(_new);
        }
    }

    /// <summary>Stub checker: trả kết quả sống/chết cấu hình sẵn, đếm số lần gọi.</summary>
    private sealed class StubChecker : IProxyHealthChecker
    {
        private readonly bool _alive;
        public int Calls { get; private set; }

        public StubChecker(bool alive) => _alive = alive;

        public Task<bool> IsAliveAsync(ProxyEntry proxy, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_alive);
        }
    }

    private static ProxyEntry P(string host) => new() { Host = host, Port = 8080 };

    [Fact]
    public async Task KiotNull_TraNull()
    {
        var result = await ProxySelector.SelectKiotProxyAsync(null, new StubChecker(true));

        Assert.Null(result);
    }

    [Fact]
    public async Task CurrentCoProxy_CheckerTrue_TraProxyDo_KhongGoiNew()
    {
        var current = P("1.1.1.1");
        var kiot = new StubKiot(current, P("2.2.2.2"));

        var result = await ProxySelector.SelectKiotProxyAsync(kiot, new StubChecker(true));

        Assert.Same(current, result);
        Assert.Equal(0, kiot.NewCalls); // đã có current còn hạn → không xin new
    }

    [Fact]
    public async Task CurrentNull_NewCoProxy_CheckerTrue_TraProxy()
    {
        var neu = P("2.2.2.2");
        var kiot = new StubKiot(null, neu);

        var result = await ProxySelector.SelectKiotProxyAsync(kiot, new StubChecker(true));

        Assert.Same(neu, result);
        Assert.Equal(1, kiot.NewCalls);
    }

    [Fact]
    public async Task CoProxy_CheckerFalse_TraNull()
    {
        var kiot = new StubKiot(P("1.1.1.1"), null);
        var checker = new StubChecker(false);

        var result = await ProxySelector.SelectKiotProxyAsync(kiot, checker);

        Assert.Null(result);
        Assert.Equal(1, checker.Calls); // đã thử kết nối, chết → null
    }

    [Fact]
    public async Task CurrentVaNewDeuNull_TraNull_KhongGoiChecker()
    {
        var kiot = new StubKiot(null, null);
        var checker = new StubChecker(true);

        var result = await ProxySelector.SelectKiotProxyAsync(kiot, checker);

        Assert.Null(result);
        Assert.Equal(0, checker.Calls); // không có proxy → không cần kiểm kết nối
    }
}
