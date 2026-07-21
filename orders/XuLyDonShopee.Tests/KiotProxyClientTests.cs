using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

public class KiotProxyClientTests
{
    // JSON mẫu theo tài liệu KiotProxy — chỉ ASCII để tránh rắc rối encoding.
    private const string SuccessJson =
        "{\"data\":{\"http\":\"171.229.10.20:39008\",\"socks5\":\"171.229.10.20:39009\"," +
        "\"host\":\"171.229.10.20\",\"httpPort\":39008},\"success\":true,\"status\":\"SUCCESS\"}";

    private const string FailJson =
        "{\"success\":false,\"code\":40400006,\"message\":\"Key not found\"," +
        "\"status\":\"FAIL\",\"error\":\"KEY_NOT_FOUND\"}";

    // expirationAt rất xa trong tương lai → luôn còn hạn so với đồng hồ thực.
    private const long FarFuture = 9999999999999L; // ~ năm 2286

    // JSON /current thành công kèm expirationAt (ms epoch) truyền vào để kiểm còn/hết hạn.
    private static string CurrentSuccessJson(long expirationAt) =>
        "{\"data\":{\"http\":\"171.229.10.20:39008\",\"host\":\"171.229.10.20\"," +
        "\"httpPort\":39008,\"expirationAt\":" + expirationAt + "},\"success\":true,\"status\":\"SUCCESS\"}";

    // JSON /current FAIL khi key chưa có proxy gán.
    private const string CurrentFailJson =
        "{\"success\":false,\"code\":40001050,\"message\":\"Could not find the proxy\"," +
        "\"status\":\"FAIL\",\"error\":\"PROXY_NOT_FOUND_BY_KEY\"}";

    /// <summary>Stub HttpMessageHandler: trả (status, body) theo hàm cấu hình, ghi lại URL đã gọi.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (System.Net.HttpStatusCode, string)> _fn;
        public List<string> RequestedUrls { get; } = new();
        public StubHandler(Func<HttpRequestMessage, (System.Net.HttpStatusCode, string)> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            RequestedUrls.Add(req.RequestUri!.ToString());
            var (code, body) = _fn(req);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public void ParseResponse_ThanhCong_TraVeHttpProxy()
    {
        var proxy = KiotProxyClient.ParseResponse(SuccessJson);
        Assert.NotNull(proxy);
        Assert.Equal("171.229.10.20", proxy!.Host);
        Assert.Equal(39008, proxy.Port);
        Assert.Equal(ProxyType.Http, proxy.Type);
    }

    [Fact]
    public void ParseResponse_ThatBai_TraVeNull()
    {
        Assert.Null(KiotProxyClient.ParseResponse(FailJson));
    }

    [Fact]
    public async Task GetNewProxyAsync_KhongCoKey_TraVeNull_KhongGoiHttp()
    {
        var stub = new StubHandler(_ => (System.Net.HttpStatusCode.OK, SuccessJson));
        var client = new KiotProxyClient(Array.Empty<string>(), httpClient: new HttpClient(stub));

        var proxy = await client.GetNewProxyAsync();

        Assert.Null(proxy);
        Assert.Equal(0, client.KeyCount);
        Assert.Empty(stub.RequestedUrls); // không gửi request nào
    }

    [Fact]
    public async Task GetNewProxyAsync_KeyLoi_ThuKeTiep()
    {
        // key=bad → FailJson (HTTP 200); key=good → SuccessJson.
        var stub = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("key=good")
                ? (System.Net.HttpStatusCode.OK, SuccessJson)
                : (System.Net.HttpStatusCode.OK, FailJson);
        });
        var client = new KiotProxyClient(new[] { "bad", "good" }, httpClient: new HttpClient(stub));

        var proxy = await client.GetNewProxyAsync();

        Assert.NotNull(proxy);
        Assert.Equal("171.229.10.20", proxy!.Host);
        Assert.Equal(2, stub.RequestedUrls.Count); // đã thử bad rồi good
        Assert.Contains("key=bad", stub.RequestedUrls[0]);
        Assert.Contains("key=good", stub.RequestedUrls[1]);
    }

    [Fact]
    public async Task GetNewProxyAsync_XoayVongGiuaCacKey()
    {
        var stub = new StubHandler(_ => (System.Net.HttpStatusCode.OK, SuccessJson));
        var client = new KiotProxyClient(new[] { "k1", "k2" }, httpClient: new HttpClient(stub));

        await client.GetNewProxyAsync();
        await client.GetNewProxyAsync();

        Assert.Equal(2, stub.RequestedUrls.Count);
        Assert.Contains("key=k1", stub.RequestedUrls[0]);
        Assert.Contains("key=k2", stub.RequestedUrls[1]);
    }

    // ===== GetCurrentProxyAsync =====

    [Fact]
    public async Task GetCurrentProxyAsync_ConHan_TraProxy_UrlCurrentKhongCoRegion()
    {
        var stub = new StubHandler(_ => (System.Net.HttpStatusCode.OK, CurrentSuccessJson(FarFuture)));
        var client = new KiotProxyClient(new[] { "k1" }, httpClient: new HttpClient(stub));

        var proxy = await client.GetCurrentProxyAsync();

        Assert.NotNull(proxy);
        Assert.Equal("171.229.10.20", proxy!.Host);
        Assert.Equal(39008, proxy.Port);
        Assert.Single(stub.RequestedUrls);
        Assert.Contains("/proxies/current", stub.RequestedUrls[0]);
        Assert.DoesNotContain("region=", stub.RequestedUrls[0]); // /current KHÔNG gửi region
    }

    [Fact]
    public async Task GetCurrentProxyAsync_ProxyNotFoundByKey_TraNull()
    {
        var stub = new StubHandler(_ => (System.Net.HttpStatusCode.OK, CurrentFailJson));
        var client = new KiotProxyClient(new[] { "k1" }, httpClient: new HttpClient(stub));

        var proxy = await client.GetCurrentProxyAsync();

        Assert.Null(proxy);
    }

    [Fact]
    public async Task GetCurrentProxyAsync_KhongCoKey_TraVeNull_KhongGoiHttp()
    {
        var stub = new StubHandler(_ => (System.Net.HttpStatusCode.OK, CurrentSuccessJson(FarFuture)));
        var client = new KiotProxyClient(Array.Empty<string>(), httpClient: new HttpClient(stub));

        var proxy = await client.GetCurrentProxyAsync();

        Assert.Null(proxy);
        Assert.Empty(stub.RequestedUrls);
    }

    [Fact]
    public async Task GetCurrentProxyAsync_XoayVongGiuaCacKey()
    {
        var stub = new StubHandler(_ => (System.Net.HttpStatusCode.OK, CurrentSuccessJson(FarFuture)));
        var client = new KiotProxyClient(new[] { "k1", "k2" }, httpClient: new HttpClient(stub));

        await client.GetCurrentProxyAsync();
        await client.GetCurrentProxyAsync();

        Assert.Equal(2, stub.RequestedUrls.Count);
        Assert.Contains("key=k1", stub.RequestedUrls[0]);
        Assert.Contains("key=k2", stub.RequestedUrls[1]);
    }

    // ===== ParseProxyIfAlive (thuần, truyền nowUnixMs cố định) =====

    [Fact]
    public void ParseProxyIfAlive_ConHan_TraProxy()
    {
        var proxy = KiotProxyClient.ParseProxyIfAlive(CurrentSuccessJson(2000), nowUnixMs: 1000);

        Assert.NotNull(proxy);
        Assert.Equal("171.229.10.20", proxy!.Host);
    }

    [Fact]
    public void ParseProxyIfAlive_DaHetHan_TraNull()
    {
        Assert.Null(KiotProxyClient.ParseProxyIfAlive(CurrentSuccessJson(1000), nowUnixMs: 2000));
    }

    [Fact]
    public void ParseProxyIfAlive_KhongCoExpirationAt_TraProxy()
    {
        var proxy = KiotProxyClient.ParseProxyIfAlive(SuccessJson, nowUnixMs: FarFuture);

        Assert.NotNull(proxy);
        Assert.Equal("171.229.10.20", proxy!.Host);
    }

    [Fact]
    public void ParseProxyIfAlive_FailJson_TraNull()
    {
        Assert.Null(KiotProxyClient.ParseProxyIfAlive(FailJson, nowUnixMs: 1000));
    }
}
