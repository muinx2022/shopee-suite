using System.Net;
using Shopee.Proxy.Kiot;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho client chung <see cref="KiotApiClient"/> + parser <see cref="KiotApiParser"/>
/// (project <c>Shopee.Proxy.Kiot</c>, tham chiếu transitive qua Core).
/// </summary>
public class KiotApiClientTests
{
    // JSON /new hoặc /current thành công, đủ field (chỉ ASCII để tránh rắc rối encoding).
    private const string SuccessJson =
        "{\"data\":{\"http\":\"171.229.10.20:39008\",\"socks5\":\"171.229.10.20:39009\"," +
        "\"host\":\"171.229.10.20\",\"httpPort\":39008,\"socks5Port\":39009," +
        "\"realIpAddress\":\"1.2.3.4\",\"nextRequestAt\":1700000000000," +
        "\"expirationAt\":1800000000000},\"success\":true,\"status\":\"SUCCESS\"}";

    private const string FailJson =
        "{\"success\":false,\"code\":40400006,\"message\":\"Key not found\"," +
        "\"status\":\"FAIL\",\"error\":\"KEY_NOT_FOUND\"}";

    /// <summary>Stub trả (status, body) theo hàm cấu hình, ghi lại URL đã gọi.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _fn;
        public List<string> RequestedUrls { get; } = new();
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            RequestedUrls.Add(req.RequestUri!.ToString());
            var (code, body) = _fn(req);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Handler ném để mô phỏng timeout/lỗi mạng.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => throw new HttpRequestException("network down");
    }

    private static KiotApiClient NewClient(HttpMessageHandler handler)
        => new(new HttpClient(handler), "https://api.kiotproxy.com");

    // ===== KiotApiParser (thuần) =====

    [Fact]
    public void ParseBody_ThanhCong_BocDuField_VaGiuRaw()
    {
        var r = KiotApiParser.ParseBody(SuccessJson, 200);

        Assert.True(r.Success);
        Assert.NotNull(r.Data);
        Assert.Equal("171.229.10.20", r.Data!.Host);
        Assert.Equal(39008, r.Data.HttpPort);
        Assert.Equal(39009, r.Data.Socks5Port);
        Assert.Equal("171.229.10.20:39008", r.Data.Http);
        Assert.Equal("171.229.10.20:39009", r.Data.Socks5);
        Assert.Equal("1.2.3.4", r.Data.RealIpAddress);
        Assert.Equal(1700000000000L, r.Data.NextRequestAtMs);
        Assert.Equal(1800000000000L, r.Data.ExpirationAtMs);
        Assert.Equal(200, r.HttpStatus);
        // Raw giữ nguyên toàn bộ data (phase 2b cần cho fingerprint).
        Assert.Equal("171.229.10.20:39008", (string?)r.Data.Raw["http"]);
        Assert.True(r.Data.Raw.ContainsKey("realIpAddress"));
    }

    [Fact]
    public void ParseBody_ThieuField_TraNullChoFieldThieu()
    {
        // data chỉ có host → các field khác null; Raw vẫn chứa host.
        var json = "{\"data\":{\"host\":\"9.9.9.9\"},\"success\":true,\"status\":\"SUCCESS\"}";
        var r = KiotApiParser.ParseBody(json, 200);

        Assert.True(r.Success);
        Assert.Equal("9.9.9.9", r.Data!.Host);
        Assert.Null(r.Data.HttpPort);
        Assert.Null(r.Data.Http);
        Assert.Null(r.Data.ExpirationAtMs);
        Assert.Null(r.Data.RealIpAddress);
    }

    [Fact]
    public void ParseBody_SuccessFalse_KhongThanhCong_TraMessage()
    {
        var r = KiotApiParser.ParseBody(FailJson, 200);

        Assert.False(r.Success);
        Assert.Equal("Key not found", r.Message);
    }

    [Fact]
    public void ParseBody_StatusFail_KhongThanhCong()
    {
        var json = "{\"data\":{\"http\":\"1.2.3.4:80\"},\"status\":\"FAIL\"}";
        var r = KiotApiParser.ParseBody(json, 200);

        Assert.False(r.Success);
    }

    [Fact]
    public void ParseBody_JsonHong_TraSuccessFalse_KhongNem()
    {
        var r = KiotApiParser.ParseBody("<html>Bad Gateway</html>", 200);

        Assert.False(r.Success);
        Assert.Null(r.Data);
    }

    [Fact]
    public void ParseBody_BodyRong_TraSuccessFalse()
    {
        Assert.False(KiotApiParser.ParseBody("", 200).Success);
        Assert.False(KiotApiParser.ParseBody(null, 200).Success);
    }

    // ===== KiotApiClient (URL + hành vi HTTP) =====

    [Fact]
    public async Task GetNewAsync_CoRegionTrongUrl()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, SuccessJson));
        var client = NewClient(stub);

        var r = await client.GetNewAsync("k1", "bac");

        Assert.True(r.Success);
        Assert.Single(stub.RequestedUrls);
        Assert.Contains("/proxies/new", stub.RequestedUrls[0]);
        Assert.Contains("key=k1", stub.RequestedUrls[0]);
        Assert.Contains("region=bac", stub.RequestedUrls[0]);
    }

    [Fact]
    public async Task GetNewAsync_RegionRong_MacDinhRandom()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, SuccessJson));
        var client = NewClient(stub);

        await client.GetNewAsync("k1", null);

        Assert.Contains("region=random", stub.RequestedUrls[0]);
    }

    [Fact]
    public async Task GetCurrentAsync_KhongCoRegionTrongUrl()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, SuccessJson));
        var client = NewClient(stub);

        var r = await client.GetCurrentAsync("k1");

        Assert.True(r.Success);
        Assert.Single(stub.RequestedUrls);
        Assert.Contains("/proxies/current", stub.RequestedUrls[0]);
        Assert.Contains("key=k1", stub.RequestedUrls[0]);
        Assert.DoesNotContain("region=", stub.RequestedUrls[0]);
    }

    [Fact]
    public async Task GetNewAsync_Http500_SuccessFalse_CoHttpStatus_KhongData()
    {
        var stub = new StubHandler(_ =>
            (HttpStatusCode.InternalServerError, "{\"message\":\"server error\"}"));
        var client = NewClient(stub);

        var r = await client.GetNewAsync("k1", "random");

        Assert.False(r.Success);
        Assert.Equal(500, r.HttpStatus);
        Assert.Null(r.Data);
        Assert.Equal("server error", r.Message);
    }

    [Fact]
    public async Task GetNewAsync_LoiMang_SuccessFalse_KhongNem_KhongHttpStatus()
    {
        var client = NewClient(new ThrowingHandler());

        var r = await client.GetNewAsync("k1", "random");

        Assert.False(r.Success);
        Assert.Null(r.HttpStatus);
        Assert.NotNull(r.Message);
    }

    [Fact]
    public async Task GetCurrentAsync_ThanhCong_DataMangExpirationVaRaw()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, SuccessJson));
        var client = NewClient(stub);

        var r = await client.GetCurrentAsync("k1");

        Assert.True(r.Success);
        Assert.Equal(200, r.HttpStatus);
        Assert.Equal(1800000000000L, r.Data!.ExpirationAtMs);
        Assert.Equal("171.229.10.20:39008", (string?)r.Data.Raw["http"]);
    }
}
