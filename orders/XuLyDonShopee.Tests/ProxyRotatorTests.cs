using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

public class ProxyRotatorTests
{
    /// <summary>Fake client trả một proxy cố định (hoặc null), đếm số lần gọi.</summary>
    private sealed class FakeKiotProxyClient : IKiotProxyClient
    {
        private readonly ProxyEntry? _proxy;
        public int CallCount { get; private set; }

        public FakeKiotProxyClient(ProxyEntry? proxy) => _proxy = proxy;

        public Task<ProxyEntry?> GetNewProxyAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_proxy);
        }

        // ProxyRotator không gọi hàm này; chỉ cần có để implement đầy đủ interface.
        public Task<ProxyEntry?> GetCurrentProxyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_proxy);
    }

    private static List<ProxyEntry> MakeProxies(params string[] hosts)
        => hosts.Select(h => new ProxyEntry { Host = h, Port = 8080 }).ToList();

    [Fact]
    public async Task GetNext_RoundRobin_Quay3ProxyGoi7Lan()
    {
        var rotator = new ProxyRotator(MakeProxies("1", "2", "3"));

        var order = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var p = await rotator.GetNextAsync();
            Assert.NotNull(p);
            order.Add(p!.Host);
        }

        Assert.Equal(new[] { "1", "2", "3", "1", "2", "3", "1" }, order);
    }

    [Fact]
    public async Task GetNext_DanhSachTrong_CoKiotProxy_TraVeProxyDo()
    {
        var kiotProxy = new ProxyEntry { Host = "10.0.0.1", Port = 9999 };
        var fake = new FakeKiotProxyClient(kiotProxy);
        var rotator = new ProxyRotator(proxies: null, kiotClient: fake);

        var p = await rotator.GetNextAsync();

        Assert.NotNull(p);
        Assert.Equal("10.0.0.1", p!.Host);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task GetNext_DanhSachTrong_KhongCoClient_TraVeNull()
    {
        var rotator = new ProxyRotator();

        var p = await rotator.GetNextAsync();

        Assert.Null(p); // null = dùng IP máy
    }

    [Fact]
    public async Task GetNext_DanhSachTrong_KiotProxyLoi_TraVeNull()
    {
        // Client trả null (mô phỏng lỗi/không có proxy).
        var fake = new FakeKiotProxyClient(null);
        var rotator = new ProxyRotator(proxies: null, kiotClient: fake);

        var p = await rotator.GetNextAsync();

        Assert.Null(p);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Reload_DoiDanhSachGiuaChung()
    {
        var rotator = new ProxyRotator(MakeProxies("a", "b"));

        Assert.Equal("a", (await rotator.GetNextAsync())!.Host);

        rotator.Reload(MakeProxies("x", "y", "z"));

        // Sau Reload, vị trí reset về đầu danh sách mới.
        Assert.Equal("x", (await rotator.GetNextAsync())!.Host);
        Assert.Equal("y", (await rotator.GetNextAsync())!.Host);
        Assert.Equal("z", (await rotator.GetNextAsync())!.Host);
        Assert.Equal("x", (await rotator.GetNextAsync())!.Host);
        Assert.Equal(3, rotator.Count);
    }
}
