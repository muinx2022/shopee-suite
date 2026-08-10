using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Bàn thử CẦU NỐI THẬT cho test: <see cref="OrdersBridgeChannel"/> chạy trên một cổng loopback TRỐNG (không đụng
/// cổng production 47821) + một <see cref="ClientWebSocket"/> đóng vai EXTENSION. Nhờ đó test được nguyên hợp đồng
/// message (captcha/lỗi/timeout/roundtrip) mà KHÔNG cần mở trình duyệt.
/// <para>
/// Bắt tay như production: extension nối rồi gửi <c>ready</c>; rig chờ chặng Ready xong mới trả về — đảm bảo phía
/// server đã ghi nhận socket, nên lệnh gửi ngay sau đó không rơi vào fail-fast "chưa kết nối".
/// </para>
/// </summary>
internal sealed class BridgeTestRig : IAsyncDisposable
{
    private ClientWebSocket _client;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Nhật ký của channel + flow (test soi câu log nghiệp vụ). Ghi từ nhiều thread → ConcurrentQueue.</summary>
    public ConcurrentQueue<string> Logs { get; }

    public OrdersBridgeChannel Channel { get; }

    /// <summary>Cổng loopback đang dùng (mỗi rig một cổng riêng → chạy song song được).</summary>
    public int Port { get; }

    private BridgeTestRig(OrdersBridgeChannel channel, ClientWebSocket client, int port, ConcurrentQueue<string> logs)
    {
        Channel = channel;
        _client = client;
        Port = port;
        Logs = logs;
    }

    /// <summary>Hàm log rót vào flow (cùng hàng đợi với channel).</summary>
    public Action<string> Log => m => Logs.Enqueue(m);

    /// <summary>True nếu có dòng log nào CHỨA <paramref name="phan"/>.</summary>
    public bool CoLog(string phan) => Logs.Any(m => m.Contains(phan, StringComparison.Ordinal));

    /// <summary>Số lần đổi CỔNG KHÁC khi cổng vừa xin bị rig song song cướp mất (xem <see cref="CongTrong"/>).</summary>
    private const int SoLanDoiCong = 5;

    /// <param name="nhipGiuSong">Nhịp giữ-sống rót cho channel (test nào canh nhịp thì truyền số nhỏ).
    /// Bỏ trống = dùng nhịp production 20s, tức trong một bài test bình thường sẽ KHÔNG có gói ping nào xen vào.</param>
    /// <param name="choNoiLai">Hạn chờ extension nối lại (cũng là hạn rút ngắn của chặng khi socket đứt giữa
    /// chừng). Bỏ trống = 90s như production — test nào canh lượt rút hạn phải truyền số nhỏ.</param>
    public static async Task<BridgeTestRig> StartAsync(TimeSpan? nhipGiuSong = null, TimeSpan? choNoiLai = null)
    {
        var logs = new ConcurrentQueue<string>();

        // CongTrong() có khe hở: nó ĐÓNG listener rồi mới trả cổng, nên giữa lúc đó một rig khác (xUnit chạy các
        // collection SONG SONG) có thể chiếm đúng cổng ấy và GIỮ suốt bài test của nó. OrdersBridgeChannel.Start
        // có retry nhưng retry ĐÚNG CỔNG CŨ — vô ích khi cổng bị chiếm thật, hết 5 lượt là ném và bài test đổ oan.
        // Ở đây phải đổi sang cổng KHÁC. (Đã bắt được một lượt đổ kiểu này ngày 08/08/2026.)
        OrdersBridgeChannel? channel = null;
        var port = 0;
        for (var lan = 0; lan < SoLanDoiCong; lan++)
        {
            port = CongTrong();
            var thu = new OrdersBridgeChannel(logs.Enqueue);
            try { thu.Start(port, nhipGiuSong, choNoiLai); channel = thu; break; }
            catch (InvalidOperationException) when (lan < SoLanDoiCong - 1)
            {
                thu.Dispose();
            }
        }
        if (channel is null)
        {
            throw new InvalidOperationException(
                $"Không xin được cổng loopback trống sau {SoLanDoiCong} lượt — máy đang cạn cổng?");
        }

        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);

        var rig = new BridgeTestRig(channel, client, port, logs);

        // Bắt tay: extension báo ready → chờ chặng Ready xong (server đã ghi nhận socket).
        await rig.GuiAsync(new { action = "ready" });
        await channel.AwaitAsync(channel.Ready, TimeSpan.FromSeconds(10), CancellationToken.None);
        return rig;
    }

    /// <summary>Extension gửi một message lên C# (object → JSON camelCase như bản thật).</summary>
    public Task GuiAsync(object message)
    {
        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        return GuiJsonAsync(json);
    }

    /// <summary>Extension gửi JSON THÔ (dùng khi cần khuôn dữ liệu y hệt trang thật).</summary>
    public async Task GuiJsonAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
    }

    /// <summary>Đóng socket phía "extension" — mô phỏng service worker MV3 bị trình duyệt giết lúc vòng nghỉ.
    /// Chờ tới khi server GHI NHẬN đứt (<c>IsConnected == false</c>) để test không đua với luồng nhận.</summary>
    public async Task NgatKetNoiAsync()
    {
        try { _client.Abort(); } catch { }
        var dl = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < dl && Channel.DaNoi)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>"Extension" sống lại và nối cầu lần nữa (đúng đường <c>chrome.alarms</c> dựng service worker dậy
    /// rồi gọi <c>bridge.connect</c>). Gửi luôn <c>ready</c> như bản thật.</summary>
    public async Task NoiLaiAsync()
    {
        _client = new ClientWebSocket();
        await _client.ConnectAsync(new Uri($"ws://localhost:{Port}/"), CancellationToken.None);
        await GuiAsync(new { action = "ready" });
        var dl = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < dl && !Channel.DaNoi)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>Extension NHẬN một lệnh C# vừa gửi (một message = một lệnh). Hết giờ → ném để test thấy ngay.
    /// <paramref name="boQuaPing"/>: bỏ qua các gói giữ-sống <c>ping</c> xen giữa (test nhịp ngắn hay dính).</summary>
    public async Task<JsonDocument> NhanLenhAsync(TimeSpan? timeout = null, bool boQuaPing = false)
    {
        while (true)
        {
            var doc = await NhanMotLenhAsync(timeout);
            if (!boQuaPing) { return doc; }
            var act = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;
            if (act != "ping") { return doc; }
            doc.Dispose();
        }
    }

    private async Task<JsonDocument> NhanMotLenhAsync(TimeSpan? timeout)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        to.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var r = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), to.Token);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            if (r.EndOfMessage)
            {
                return JsonDocument.Parse(sb.ToString());
            }
        }
    }

    /// <summary>Chờ tới khi nhật ký có dòng chứa <paramref name="phan"/>; hết giờ → false.</summary>
    public async Task<bool> ChoLogAsync(string phan, TimeSpan? timeout = null)
    {
        var han = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < han && !CoLog(phan))
        {
            await Task.Delay(20);
        }
        return CoLog(phan);
    }

    /// <summary>Cổng loopback TRỐNG (bind port 0 rồi nhả) — không đụng cổng cầu nối thật 47821.</summary>
    private static int CongTrong()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _client.Dispose(); } catch { }
        try { Channel.Dispose(); } catch { }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
