using System.Net;
using System.Net.Sockets;
using System.Text;
using Shopee.Core.Cdp;

namespace Shopee.Core.Tests;

/// <summary>
/// Hợp đồng của lớp đọc danh sách target CDP dùng chung (<see cref="CdpEndpoints"/> + <see cref="CdpTarget"/>
/// + <see cref="CdpClient"/>): ~20 chỗ trong suite trước đây tự dựng URL + tự parse <c>/json/list</c>, giờ đi
/// qua đây nên phần "fetch + parse" phải chắc.
/// </summary>
public sealed class CdpTargetsTests
{
    // ── CdpEndpoints ──────────────────────────────────────────────────────────────

    [Fact]
    public void Endpoint_LuonDungIPv4_127001_KhongDungLocalhost()
    {
        Assert.Equal("127.0.0.1", CdpEndpoints.Host);
        Assert.Equal("http://127.0.0.1:9222", CdpEndpoints.Base(9222));
        Assert.Equal("http://127.0.0.1:9222/json/list", CdpEndpoints.List(9222));
        Assert.Equal("http://127.0.0.1:9222/json/version", CdpEndpoints.Version(9222));
        Assert.Equal("http://127.0.0.1:9222/json", CdpEndpoints.Targets(9222));
        Assert.DoesNotContain("localhost", CdpEndpoints.List(9222), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Endpoint_New_VaClose_EscapeThamSo()
    {
        Assert.Equal(
            "http://127.0.0.1:9222/json/new?chrome-extension%3A%2F%2Fabc%2Fpopup.html",
            CdpEndpoints.New(9222, "chrome-extension://abc/popup.html"));
        Assert.Equal(
            "http://127.0.0.1:9222/json/close/A%2FB%20C",
            CdpEndpoints.Close(9222, "A/B C"));
    }

    // ── CdpTarget.ParseList ───────────────────────────────────────────────────────

    [Fact]
    public void ParseList_DocDuTruong_VaPhanLoaiTarget()
    {
        var targets = CdpTarget.ParseList("""
            [
              {"id":"T1","type":"page","url":"https://shopee.vn/x","title":"SP","webSocketDebuggerUrl":"ws://127.0.0.1:1/a"},
              {"id":"T2","type":"service_worker","url":"chrome-extension://abc/sw.js","webSocketDebuggerUrl":"ws://127.0.0.1:1/b"}
            ]
            """);

        Assert.Equal(2, targets.Count);

        var page = targets[0];
        Assert.Equal("T1", page.Id);
        Assert.Equal("https://shopee.vn/x", page.Url);
        Assert.Equal("SP", page.Title);
        Assert.Equal("ws://127.0.0.1:1/a", page.WsUrl);
        Assert.True(page.IsPage);
        Assert.False(page.IsServiceWorker);
        Assert.True(page.HasWsUrl);

        Assert.True(targets[1].IsServiceWorker);
        Assert.False(targets[1].IsPage);
    }

    [Fact]
    public void ParseList_TypeVietHoa_VanNhanDung()
    {
        var targets = CdpTarget.ParseList("""[{"id":"1","type":"PAGE","url":"u"}]""");
        Assert.True(targets[0].IsPage);
    }

    [Fact]
    public void ParseList_ThieuTruong_TraChuoiRong_RiengWsUrlLaNull()
    {
        var targets = CdpTarget.ParseList("""[{"type":"page"}]""");

        Assert.Equal("", targets[0].Id);
        Assert.Equal("", targets[0].Url);
        Assert.Equal("", targets[0].Title);
        // WsUrl null = entry KHÔNG có webSocketDebuggerUrl (target không cho gắn) — khác "" (có nhưng rỗng).
        Assert.Null(targets[0].WsUrl);
        Assert.False(targets[0].HasWsUrl);
    }

    [Fact]
    public void ParseList_MangRong_TraDanhSachRong()
    {
        Assert.Empty(CdpTarget.ParseList("[]"));
    }

    [Fact]
    public void ParseList_PhanTuKhongPhaiObject_BiBoQua()
    {
        var targets = CdpTarget.ParseList("""["rac", {"id":"1","type":"page","url":"u"}]""");
        Assert.Single(targets);
        Assert.Equal("1", targets[0].Id);
    }

    [Fact]
    public void ParseList_KhongPhaiMang_Nem()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CdpTarget.ParseList("""{"error":"khong phai mang"}"""));
        Assert.Contains("/json/list", ex.Message);
    }

    // ── CdpClient: fetch + parse ─────────────────────────────────────────────────

    [Fact]
    public async Task ListTargetsAsync_DocDungEndpoint_VaTraTargets()
    {
        using var server = new FakeCdpServer(
            """[{"id":"T1","type":"page","url":"https://shopee.vn/","webSocketDebuggerUrl":"ws://x/1"}]""");

        var targets = await CdpClient.ListTargetsAsync(server.Port);

        Assert.Single(targets);
        Assert.Equal("T1", targets[0].Id);
        Assert.Equal("GET /json/list", server.LastRequestLine);
    }

    [Fact]
    public async Task ListTargetsAsync_HttpLoi_Nem()
    {
        using var server = new FakeCdpServer("khong tim thay", status: 404);
        await Assert.ThrowsAsync<HttpRequestException>(() => CdpClient.ListTargetsAsync(server.Port));
    }

    [Fact]
    public async Task TryListTargetsAsync_HttpLoi_TraRong_KhongNem()
    {
        using var server = new FakeCdpServer("khong tim thay", status: 404);
        Assert.Empty(await CdpClient.TryListTargetsAsync(server.Port));
    }

    [Fact]
    public async Task TryListTargetsAsync_KhongCoBrave_TraRong_KhongNem()
    {
        // Cổng chắc chắn không ai nghe (server vừa tắt) — nhánh Brave đã chết/chưa mở.
        int port;
        using (var server = new FakeCdpServer("[]"))
            port = server.Port;

        Assert.Empty(await CdpClient.TryListTargetsAsync(port, timeoutMs: 2_000));
    }

    [Fact]
    public async Task CloseTargetAsync_GoiDungDuongDan_VaBoQuaIdRong()
    {
        using var server = new FakeCdpServer("Target is closing");

        await CdpClient.CloseTargetAsync(server.Port, "T 1");
        Assert.Equal("GET /json/close/T%201", server.LastRequestLine);

        // id rỗng → KHÔNG gọi gì cả (giữ nguyên request cũ).
        await CdpClient.CloseTargetAsync(server.Port, "  ");
        Assert.Equal("GET /json/close/T%201", server.LastRequestLine);
    }

    /// <summary>Server HTTP tối giản trên 127.0.0.1 (cổng tự cấp) đóng vai endpoint CDP của Brave.</summary>
    private sealed class FakeCdpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _body;
        private readonly int _status;
        private volatile string _lastRequestLine = "";

        public int Port { get; }
        public string LastRequestLine => _lastRequestLine;

        public FakeCdpServer(string body, int status = 200)
        {
            _body = body;
            _status = status;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }   // listener đã đóng

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        var read = await stream.ReadAsync(buffer);
                        var request = Encoding.UTF8.GetString(buffer, 0, read);
                        var firstLine = request.Split("\r\n")[0].Split(" HTTP/")[0];
                        _lastRequestLine = firstLine;

                        var bodyBytes = Encoding.UTF8.GetBytes(_body);
                        var header = Encoding.UTF8.GetBytes(
                            $"HTTP/1.1 {_status} {(_status == 200 ? "OK" : "Not Found")}\r\n" +
                            "Content-Type: application/json\r\n" +
                            $"Content-Length: {bodyBytes.Length}\r\n" +
                            "Connection: close\r\n\r\n");
                        await stream.WriteAsync(header);
                        await stream.WriteAsync(bodyBytes);
                        await stream.FlushAsync();
                    }
                    catch
                    {
                        // client ngắt giữa chừng — bỏ qua
                    }
                }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }
}
