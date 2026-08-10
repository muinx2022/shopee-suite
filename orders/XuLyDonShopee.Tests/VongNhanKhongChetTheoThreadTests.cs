using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Toolkit.Ws;

namespace XuLyDonShopee.Tests;

/// <summary>
/// VÒNG NHẬN KHÔNG ĐƯỢC CHẾT THEO THREAD PHÁT RA LỆNH I/O.
/// <para>
/// Bệnh gốc (đo được ở vòng chạy thật 10/08/2026, 22:08–22:24): mỗi kết nối cầu nối sống đúng <b>211 giây</b>
/// rồi chết với <c>ngoai-le · loi=WebSocketException [wsError=Success, win32=0] &lt;- HttpListenerException:
/// The I/O operation has been aborted because of either a thread exit or an application request</c>, lặp lại y
/// hệt 4 lần liền (22:11:59 · 22:15:59 · 22:19:59 · 22:23:59). Chuỗi đó là <c>ERROR_OPERATION_ABORTED</c> (995)
/// của Windows: <b>overlapped I/O bị huỷ khi thread phát ra nó kết thúc</b>. Vòng nhận cũ chạy trên thread pool
/// nên lệnh <c>ReceiveAsync</c> treo suốt kỳ nghỉ giữa hai shop bị chính thread pool giết khi nó thu hồi thread
/// nhàn rỗi — cả hai đầu đều khoẻ mà cầu nối vẫn đứt.
/// </para>
/// <para>
/// KHÔNG có "chu kỳ 240 giây" như từng đoán: 211s sống + 29s nối lại = 240s, nhìn từ xa mới ra chu kỳ.
/// </para>
/// <para>
/// Hai tầng canh ở đây: <see cref="VongNhan_OLaiDungMotLuongNen_KhongPhaiThreadPool"/> cố định CƠ CHẾ (chạy
/// nhanh, đỏ ngay khi ai đó thả vòng nhận về thread pool), còn
/// <see cref="GiuKetNoi240Giay_KhongCoCuDutNao"/> canh TRIỆU CHỨNG (chạy ~4,5 phút — xem <c>[Trait]</c>).
/// </para>
/// </summary>
public class VongNhanKhongChetTheoThreadTests
{
    /// <summary>Một lần quan sát thread đang chạy handler <c>MessageReceived</c> — tức chính thread vừa phát ra
    /// lượt <c>ReceiveAsync</c> tương ứng.</summary>
    private sealed record LanQuanSat(int MaThread, string? TenThread);

    /// <summary>Tiền tố tên thread nền của vòng nhận (<c>WebSocketServer.Start</c>/vòng chấp nhận đặt).
    /// Thread pool KHÔNG bao giờ mang tên này, nên đây là dấu nhận diện phân biệt được hai bản.</summary>
    private const string TienToTenLuongNhan = "ws-";

    [Fact]
    public async Task VongNhan_OLaiDungMotLuongNen_KhongPhaiThreadPool()
    {
        // Đây là bài chốt CƠ CHẾ của bản vá: mọi lượt ReceiveAsync phải được phát ra từ CÙNG một thread nền
        // sống suốt đời kết nối. Chỉ cần vòng nhận rơi lại về thread pool là bài này đỏ ngay — không phải chờ
        // 211 giây mới thấy bệnh.
        var quanSat = new ConcurrentQueue<LanQuanSat>();
        using var duGoi = new SemaphoreSlim(0);

        var port = CongTrong();
        using var server = new WebSocketServer(port);
        server.MessageReceived += _ =>
        {
            quanSat.Enqueue(new LanQuanSat(
                Environment.CurrentManagedThreadId, Thread.CurrentThread.Name));
            duGoi.Release();
        };
        server.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);

        // Nhiều gói, CÁCH NHAU đủ để thread pool kịp xáo thread giữa hai lượt nhận — bản cũ hầu như chắc chắn
        // trả về những thread khác nhau; bản vá thì không đổi thread lần nào.
        const int SoGoi = 6;
        for (var i = 0; i < SoGoi; i++)
        {
            await GuiAsync(client, $"{{\"action\":\"ping\",\"stt\":{i}}}");
            Assert.True(await duGoi.WaitAsync(TimeSpan.FromSeconds(10)),
                $"Không nhận được gói thứ {i} — vòng nhận đã chết giữa chừng?");
            await Task.Delay(120);
        }

        var lan = quanSat.ToArray();
        Assert.Equal(SoGoi, lan.Length);

        // (1) MỘT thread duy nhất suốt đời kết nối. Bản cũ (thả về thread pool) cho ra mỗi lượt một thread khác —
        // đo được đúng vậy khi thử phá: 6 gói → 6 mã thread [22, 28, 34, 20, 27, …]. Mỗi lượt đổi thread là một
        // lần lệnh nhận đang treo bị buộc vào một thread có thể bị thu hồi bất cứ lúc nào.
        var maThread = new HashSet<int>(Array.ConvertAll(lan, l => l.MaThread));
        Assert.True(maThread.Count == 1,
            $"Vòng nhận nhảy qua {maThread.Count} thread khác nhau ([{string.Join(", ", maThread)}]) — nó đang " +
            "chạy trên thread pool, và thread pool thu hồi thread nhàn rỗi thì lệnh nhận đang treo bị huỷ " +
            "(ERROR_OPERATION_ABORTED 995), đúng cú đứt 211s của vòng chạy 10/08/2026.");

        // (2) Và đó phải là thread NỀN CHUYÊN DỤNG của WebSocketServer, không phải một thread mượn tạm.
        Assert.All(lan, l => Assert.StartsWith(TienToTenLuongNhan, l.TenThread ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoiRoiDut_NhieuLan_ThreadCuaTungKetNoiDeuCHET()
    {
        // Bản vá cấp cho MỖI kết nối một thread nền riêng ⇒ thread đó PHẢI CHẾT khi kết nối đóng. Không thì cứ
        // mỗi lượt extension nối lại là app ôm thêm một thread nằm không — mà vòng chạy thật nối lại vài lần
        // mỗi giờ, chạy cả ngày.
        // Đo bằng chính đối tượng Thread tóm được trong handler (Thread.IsAlive), KHÔNG phải bằng "vòng nhận có
        // bắn DutVoiLyDo hay không": bơm mà quên đóng hàng đợi thì sự kiện vẫn bắn đủ, thread vẫn nằm lại vĩnh
        // viễn trong GetConsumingEnumerable, và bài test đếm sự kiện sẽ xanh trong khi thread rò thật.
        var port = CongTrong();
        using var server = new WebSocketServer(port);
        var dut = new ConcurrentQueue<LyDoDut>();
        server.DutVoiLyDo += l => dut.Enqueue(l);

        var luong = new ConcurrentQueue<Thread>();
        using var duGoi = new SemaphoreSlim(0);
        server.MessageReceived += _ => { luong.Enqueue(Thread.CurrentThread); duGoi.Release(); };
        server.Start();

        const int SoLuot = 10;
        for (var i = 0; i < SoLuot; i++)
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
            await ChoNoiAsync(server);
            // Một gói để tóm ĐÚNG thread đang chạy vòng nhận của kết nối này.
            await GuiAsync(client, "{\"action\":\"ping\"}");
            Assert.True(await duGoi.WaitAsync(TimeSpan.FromSeconds(10)), $"Lượt {i}: không nhận được gói.");
            client.Abort();
            await ChoDuDutAsync(dut, i + 1);
        }

        Assert.Equal(SoLuot, luong.Count);
        foreach (var t in luong)
        {
            var dl = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < dl && t.IsAlive) { await Task.Delay(20); }
            Assert.False(t.IsAlive,
                $"Thread '{t.Name}' của một kết nối ĐÃ ĐÓNG vẫn còn sống — mỗi lượt extension nối lại là rò " +
                "thêm một thread.");
        }

        // Và cổng vẫn đón được kết nối kế — vòng CHẤP NHẬN không chết theo mấy lượt đứt đó.
        using var them = new ClientWebSocket();
        await them.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        await ChoNoiAsync(server);
    }

    /// <summary>
    /// Bài canh TRIỆU CHỨNG: giữ một kết nối THẬT quá mốc 240 giây với nhịp ping 20s y production, và khẳng
    /// định KHÔNG có cú đứt nào. Cú đứt thật rơi vào giây thứ 211 nên bài dưới 240s là bài mù.
    /// <para>
    /// ⚠⚠ <b>ĐỌC TRƯỚC KHI TIN BÀI NÀY: nó CHƯA chứng minh được là bắt đúng bệnh.</b> Đã chạy thử nó trên bản
    /// CHƯA VÁ (vòng nhận + vòng chấp nhận đều trả về thread pool) ngày 10/08/2026 và nó vẫn <b>XANH</b> sau
    /// 4 phút 20. Tức tiến trình test không tái hiện được cú huỷ I/O của vòng chạy thật: thread pool chỉ thu
    /// hồi thread khi số thread vượt mức tối thiểu, mà tiến trình test thì im lìm (<see cref="XaoTronThreadPool"/>
    /// chỉ làm tăng cơ hội, không ép được).
    /// </para>
    /// <para>
    /// Vậy nên bài này là <b>canh gác</b>, KHÔNG phải bằng chứng: nó chỉ nói "kịch bản giữ kết nối 260s không
    /// tự nhiên hỏng". Bài chứng minh cơ chế là
    /// <see cref="VongNhan_OLaiDungMotLuongNen_KhongPhaiThreadPool"/> — bài đó ĐỎ thật với bản chưa vá.
    /// Muốn biết bản vá có diệt được cú đứt 240s ngoài đời hay không thì phải đo bằng VÒNG CHẠY THẬT.
    /// </para>
    /// <para><b>Chạy lâu (~4,5 phút) nên tách khỏi lượt test nhanh:</b></para>
    /// <para><c>dotnet test orders\XuLyDonShopee.Tests --filter "Loai!=Dai"</c> — lượt nhanh (bỏ bài này).</para>
    /// <para><c>dotnet test orders\XuLyDonShopee.Tests --filter "Loai=Dai"</c> — chạy riêng bài này.</para>
    /// </summary>
    [Fact]
    [Trait("Loai", "Dai")]
    public async Task GiuKetNoi240Giay_KhongCoCuDutNao()
    {
        var port = CongTrong();
        using var server = new WebSocketServer(port);
        var dut = new ConcurrentQueue<LyDoDut>();
        server.DutVoiLyDo += l => dut.Enqueue(l);
        server.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        await ChoNoiAsync(server);

        // Ép thread pool PHÌNH rồi để nó rảnh: chỉ khi số thread vượt mức tối thiểu thì pool mới thu hồi thread
        // nhàn rỗi — tức mới có cửa tái hiện đúng cú huỷ I/O của bản cũ. App thật (WPF + Playwright + timer) lúc
        // nào cũng có mức xáo trộn này, còn tiến trình test thì im lìm nên phải tự tạo.
        XaoTronThreadPool();

        // Extension THẬT đọc gói ping rồi bỏ qua (background.js không có nhánh `ping`) — làm y vậy để đường
        // truyền giống production, và cũng để client phát hiện được nếu server lặng lẽ đứt.
        using var ngungDoc = new CancellationTokenSource();
        var docNen = DocBoAsync(client, ngungDoc.Token);

        // Nhịp giữ-sống y production: C# bắn `ping` mỗi 20s, extension KHÔNG trả lời — nên phía server chỉ có
        // ĐÚNG MỘT lệnh ReceiveAsync treo suốt từ đầu tới cuối. Chính lệnh treo đó là thứ bị huỷ ở bản cũ.
        var het = DateTime.UtcNow + GiuKetNoi;
        var soPing = 0;
        while (DateTime.UtcNow < het)
        {
            await Task.Delay(NhipPing);
            if (!dut.IsEmpty) { break; }
            try { await server.SendAsync(new { action = "ping" }); }
            catch (Exception) { break; } // socket vừa chết — để mấy assert dưới nói đúng nguyên nhân
            soPing++;
        }

        // ⚠ CHỤP TRẠNG THÁI TRƯỚC KHI DỌN. Huỷ token của `ClientWebSocket.ReceiveAsync` là ABORT luôn socket
        // (hợp đồng của ClientWebSocket), nên dọn trước rồi mới assert thì bài tự tay tạo ra đúng cú đứt mình
        // đang đi tìm — đã dính một lượt đỏ oan kiểu đó, mất 4 phút 20 mới lộ.
        var luoc = dut.ToArray();
        var conNoi = server.IsConnected;
        var trangThaiClient = client.State;

        ngungDoc.Cancel();
        await docNen;

        Assert.True(luoc.Length == 0,
            "Cầu nối ĐỨT trong lúc giữ kết nối: " + (luoc.Length > 0 ? luoc[0].ToString() : "?"));
        Assert.True(conNoi, "Server không còn coi là đang nối sau khi giữ quá 240s.");
        Assert.True(trangThaiClient == WebSocketState.Open, $"Client rời trạng thái Open: {trangThaiClient}.");
        Assert.True(soPing >= 12, $"Chỉ bắn được {soPing} nhịp ping — bài chưa chạm mốc 240s.");
    }

    /// <summary>Tổng thời gian giữ kết nối của bài dài. 260s &gt; mốc 211s đo được + biên cho máy chậm.</summary>
    private static readonly TimeSpan GiuKetNoi = TimeSpan.FromSeconds(260);

    /// <summary>Nhịp ping — bằng <c>OrdersBridgeChannel.NhipGiuSong</c> của production (20s).</summary>
    private static readonly TimeSpan NhipPing = TimeSpan.FromSeconds(20);

    /// <summary>Số việc chặn thread ném vào pool để nó phình quá mức tối thiểu (xem chỗ gọi).</summary>
    private const int SoViecXaoTron = 64;

    private static void XaoTronThreadPool()
    {
        for (var i = 0; i < SoViecXaoTron; i++)
        {
            ThreadPool.QueueUserWorkItem(_ => Thread.Sleep(3000));
        }
    }

    // ===== Tiện ích =====

    /// <summary>Đọc và VỨT mọi gói server gửi xuống, tới khi <paramref name="ct"/> huỷ hoặc socket chết.</summary>
    private static async Task DocBoAsync(ClientWebSocket client, CancellationToken ct)
    {
        var dem = new byte[8 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                await client.ReceiveAsync(new ArraySegment<byte>(dem), ct);
            }
        }
        catch (Exception) { /* huỷ hoặc socket đứt — mấy assert của bài lo phần kết luận */ }
    }

    private static Task GuiAsync(ClientWebSocket client, string json)
        => client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
            WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task ChoDuDutAsync(ConcurrentQueue<LyDoDut> dut, int can)
    {
        var dl = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < dl && dut.Count < can) { await Task.Delay(20); }
        Assert.True(dut.Count >= can, $"Chỉ có {dut.Count}/{can} vòng nhận kết thúc — có vòng còn treo (thread rò).");
    }

    private static async Task ChoNoiAsync(WebSocketServer server)
    {
        var dl = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < dl && !server.IsConnected) { await Task.Delay(20); }
        Assert.True(server.IsConnected, "Server chưa ghi nhận kết nối sau 5s.");
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
