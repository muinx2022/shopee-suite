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
    private readonly ClientWebSocket _client;
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

    public static async Task<BridgeTestRig> StartAsync()
    {
        var port = CongTrong();
        var logs = new ConcurrentQueue<string>();
        var channel = new OrdersBridgeChannel(logs.Enqueue);
        channel.Start(port);

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

    /// <summary>Extension NHẬN một lệnh C# vừa gửi (một message = một lệnh). Hết giờ → ném để test thấy ngay.</summary>
    public async Task<JsonDocument> NhanLenhAsync(TimeSpan? timeout = null)
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
