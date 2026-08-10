using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Toolkit.Ws;

namespace XuLyDonShopee.Tests;

/// <summary>
/// AI ĐÓNG SOCKET — nhật ký phải trả lời được câu đó.
/// <para>
/// Bối cảnh (10/08/2026): cầu nối đứt đều đặn 240,0s/lần suốt mọi vòng, jitter dưới 1 giây. Cả ngày đổ oan cho
/// Chromium (GPU, network service, service worker MV3, SDK chat) mà mọi thí nghiệm đều trượt — vì phía C# chỉ
/// ghi được "extension ĐỨT lúc HH:mm:ss", không nói vì sao. <c>WebSocketServer</c> khi ấy nuốt trắng mọi đường
/// đứt bằng <c>catch { break; }</c>, nên "client chủ động đóng" và "server/http.sys ngắt" nhìn y hệt nhau.
/// </para>
/// <para>
/// Nay mỗi cú đứt kèm <see cref="LyDoDut"/>: nguồn + <c>WebSocketCloseStatus</c> + mô tả + ngoại lệ. Mấy bài
/// dưới đây cố định đúng ba đường thoát của vòng nhận, và cố định luôn việc chuỗi lý do CHẠM TỚI nhật ký người
/// dùng (không phải chỉ nằm trong object).
/// </para>
/// </summary>
public class LyDoDutCauNoiTests
{
    /// <summary>Hạn chờ event <c>DutVoiLyDo</c> nổ — vòng nhận chạy ở thread khác nên phải chờ, nhưng đường này
    /// tức thì nên 10s đã là biên rất rộng.</summary>
    private static readonly TimeSpan ChoLyDo = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ClientDongTuTe_ThiBaoNguonClientDong_KemMaDongVaMoTa()
    {
        // Extension/Chromium tự đóng cầu → phải nhận diện là CLIENT đóng, kèm nguyên mã đóng nó gửi.
        using var ban = await BanWsAsync();

        await ban.Client.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure, "extension tat", CancellationToken.None);

        var lyDo = await ban.ChoLyDoAsync();
        Assert.Equal(LyDoDut.NguonClientDong, lyDo.Nguon);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, lyDo.CloseStatus);
        Assert.Equal("extension tat", lyDo.MoTaDong);
        Assert.Null(lyDo.Loi);
        Assert.False(lyDo.DaBiThay);
    }

    [Fact]
    public async Task SocketBiAbort_ThiBaoNguonNgoaiLe_KemChuoiLoiCoMaWebSocket()
    {
        // Đường của "bị ngắt từ dưới lên" (http.sys / Winsock / network service sập): ReceiveAsync NÉM.
        // Chuỗi lỗi phải mang mã WebSocket + mã Win32 — đó mới là thứ chỉ đích danh thủ phạm.
        using var ban = await BanWsAsync();

        ban.Client.Abort();

        var lyDo = await ban.ChoLyDoAsync();
        Assert.Equal(LyDoDut.NguonNgoaiLe, lyDo.Nguon);
        Assert.False(string.IsNullOrWhiteSpace(lyDo.Loi));
        Assert.Contains("wsError=", lyDo.Loi, StringComparison.Ordinal);
        Assert.Contains("win32=", lyDo.Loi, StringComparison.Ordinal);
    }

    [Fact]
    public void ChuoiLyDo_ToString_GomDuNguonMaDongVaLoi()
    {
        // Hàm thuần: đây chính là chuỗi nhét vào nhật ký, nên khuôn của nó là hợp đồng.
        var lyDo = new LyDoDut(
            LyDoDut.NguonNgoaiLe, WebSocketCloseStatus.EndpointUnavailable, "mat dien",
            "WebSocketException: bum [wsError=ConnectionClosedPrematurely, win32=995]", DaBiThay: true);

        var s = lyDo.ToString();
        Assert.Contains(LyDoDut.NguonNgoaiLe, s, StringComparison.Ordinal);
        Assert.Contains("close=EndpointUnavailable", s, StringComparison.Ordinal);
        Assert.Contains("mat dien", s, StringComparison.Ordinal);
        Assert.Contains("wsError=ConnectionClosedPrematurely", s, StringComparison.Ordinal);
        Assert.Contains("da-bi-thay", s, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NhatKyCauNoi_CoLyDoDut_ChuKhongChiCoGio()
    {
        // Bài QUAN TRỌNG NHẤT của nhóm này: lý do phải chạm tới ĐÚNG dòng log người dùng đọc. Nằm trong object
        // mà không ra nhật ký thì vẫn là mù như cũ.
        await using var rig = await BridgeTestRig.StartAsync();

        await rig.NgatKetNoiAsync(); // rig Abort socket → đường ngoại lệ

        Assert.True(await rig.ChoLogAsync("extension ĐỨT lúc"));
        Assert.True(await rig.ChoLogAsync(LyDoDut.NguonNgoaiLe),
            "Dòng 'extension ĐỨT' không kèm lý do — nhật ký vẫn không nói được ai đóng socket.");
    }

    // ===== Bàn thử tối giản: WebSocketServer thật + một ClientWebSocket =====

    private sealed class BanWs : IDisposable
    {
        public required WebSocketServer Server { get; init; }
        public required ClientWebSocket Client { get; init; }
        public required TaskCompletionSource<LyDoDut> LyDo { get; init; }

        public async Task<LyDoDut> ChoLyDoAsync()
            => await LyDo.Task.WaitAsync(ChoLyDo);

        public void Dispose()
        {
            try { Client.Dispose(); } catch { }
            try { Server.Dispose(); } catch { }
        }
    }

    private static async Task<BanWs> BanWsAsync()
    {
        var port = CongTrong();
        var server = new WebSocketServer(port);
        var lyDo = new TaskCompletionSource<LyDoDut>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.DutVoiLyDo += l => lyDo.TrySetResult(l);
        server.Start();

        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);

        // Chờ server ghi nhận socket, để cú đóng ngay sau đó không đua với vòng accept.
        var dl = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < dl && !server.IsConnected) { await Task.Delay(20); }

        return new BanWs { Server = server, Client = client, LyDo = lyDo };
    }

    /// <summary>Cổng loopback trống (bind 0 rồi nhả) — không đụng cổng cầu nối thật 47821.</summary>
    private static int CongTrong()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
