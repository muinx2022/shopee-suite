using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test QUYẾT ĐỊNH GỬI của <see cref="HubOutbox.PushOrdersToGsheetAsync"/> với ĐƠN HỦY MẤT VẬN ĐƠN — chạy trên
/// CSDL thật (file tạm) + Web App Apps Script GIẢ trên loopback (quyết định nằm trong thân hàm nên kiểm qua
/// chính đường của repo, KHÔNG tách kiến trúc chỉ để test):
/// <list type="bullet">
/// <item>đơn hủy + không vận đơn + ĐÃ có dòng trên sheet → PHẢI gửi kèm <c>daHuy: true</c> (Apps Script tô đỏ);</item>
/// <item>đơn hủy + không vận đơn + CHƯA từng ghi sheet → vẫn BỎ QUA (không spam dòng đỏ) rồi được dọn.</item>
/// </list>
/// </summary>
public class HubOutboxGsheetHuyTests
{
    private static (AppServices Services, long AccountId) Dung(TempDatabase temp, FakeGsheetWebApp web)
    {
        var services = new AppServices(temp.Path);
        var accountId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        services.Settings.SetGsheetWebAppUrl(web.Url);
        return (services, accountId);
    }

    /// <summary>Đơn HỦY không vận đơn (Shopee hủy → danh sách không còn hiện mã), lý do hủy có sẵn.</summary>
    private static SyncedOrder DonHuyKhongVanDon(string sn)
        => new() { OrderSn = sn, Status = "Đã hủy", CancelReason = "Người mua hủy", TotalPrice = 166500 };

    private static Task<KetQuaDay> DayAsync(long accountId, AppServices services)
        => HubOutbox.PushOrdersToGsheetAsync(
            accountId, services, shopId: null, shopLogin: "alina99.store",
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: _ => { },
            ct: CancellationToken.None);

    [Fact]
    public async Task DonHuy_MatVanDon_DaGhiSheet_ThiGui_KemDaHuy()
    {
        using var temp = new TempDatabase();
        using var web = new FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);

        services.Orders.UpsertMany(accId, new[] { DonHuyKhongVanDon("DAGHI") }, DateTime.UtcNow);
        // Đơn ĐÃ có dòng trên sheet (ghi lúc còn "Chờ lấy hàng", lúc đó có vận đơn) — cờ hủy lần đẩy trước = 0.
        services.Orders.MarkGsheetSynced(accId, "DAGHI", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: false, tab: "Tháng 07-2026", DateTime.UtcNow);

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services));

        var body = Assert.Single(web.Bodies);
        Assert.Contains("\"maDon\":\"DAGHI\"", body);
        Assert.Contains("\"daHuy\":true", body);   // Apps Script tô đỏ đúng dòng cũ
    }

    [Fact]
    public async Task DonHuy_KhongVanDon_ChuaGhiSheet_ThiBoQua_VanDuocDon()
    {
        using var temp = new TempDatabase();
        using var web = new FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);

        // Chưa từng MarkGsheetSynced → hủy trước khi vào pipeline giao, không thuộc sổ theo dõi.
        services.Orders.UpsertMany(accId, new[] { DonHuyKhongVanDon("CHUAGHI") }, DateTime.UtcNow);

        Assert.Equal(KetQuaDay.KhongCanDay, await DayAsync(accId, services));

        Assert.Empty(web.Bodies);                              // KHÔNG gửi gì (không spam dòng đỏ vô nghĩa)
        Assert.Empty(services.Orders.GetOrderSns(accId));      // settled by design → đã dọn khỏi app
    }

    /// <summary>
    /// Web App Apps Script GIẢ trên loopback (TcpListener + HTTP/1.1 tối giản): GHI LẠI body từng POST rồi trả
    /// <c>{"results":[…ok…]}</c> cho đúng các <c>maDon</c> trong body. Đủ để kiểm ĐƠN NÀO được gửi và gửi kèm cờ
    /// <c>daHuy</c> nào — không đụng mạng ngoài, không cần đăng ký cổng với hệ điều hành.
    /// <para>Dùng lại ở <see cref="HubOutboxGsheetSanPhamTests"/> (cùng một hợp đồng POST) nên để
    /// <c>internal</c>.</para>
    /// </summary>
    internal sealed class FakeGsheetWebApp : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly List<string> _bodies = new();

        public FakeGsheetWebApp()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _loop = Task.Run(VongLapAsync);
        }

        /// <summary>URL truyền vào cấu hình "Web App URL" của test.</summary>
        public string Url { get; }

        /// <summary>Body JSON của MỖI lô POST đã nhận, theo thứ tự nhận.</summary>
        public IReadOnlyList<string> Bodies
        {
            get { lock (_bodies) { return _bodies.ToList(); } }
        }

        private async Task VongLapAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return; // đã Dispose (hủy / listener đóng)
                }

                using (client)
                using (var stream = client.GetStream())
                {
                    try
                    {
                        var body = await DocBodyAsync(stream);
                        lock (_bodies)
                        {
                            _bodies.Add(body);
                        }

                        var payload = Encoding.UTF8.GetBytes(TaoPhanHoi(body));
                        var header = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n"
                            + "Content-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header);
                        await stream.WriteAsync(payload);
                        await stream.FlushAsync();
                    }
                    catch
                    {
                        // Client ngắt giữa chừng / body không đọc được → bỏ qua, test sẽ tự thấy qua Bodies.
                    }
                }
            }
        }

        /// <summary>Đọc một yêu cầu HTTP: gom tới hết header ("\r\n\r\n") rồi đọc đúng Content-Length byte body.</summary>
        private static async Task<string> DocBodyAsync(NetworkStream stream)
        {
            var raw = new List<byte>();
            var buf = new byte[8192];
            var ketThucHeader = -1;
            while (ketThucHeader < 0)
            {
                var n = await stream.ReadAsync(buf);
                if (n <= 0)
                {
                    return string.Empty;
                }
                raw.AddRange(new ArraySegment<byte>(buf, 0, n));
                ketThucHeader = TimKetThucHeader(raw);
            }

            var header = Encoding.ASCII.GetString(raw.ToArray(), 0, ketThucHeader);
            var soByte = DocContentLength(header);
            var batDau = ketThucHeader + 4;
            while (raw.Count - batDau < soByte)
            {
                var n = await stream.ReadAsync(buf);
                if (n <= 0)
                {
                    break;
                }
                raw.AddRange(new ArraySegment<byte>(buf, 0, n));
            }

            return Encoding.UTF8.GetString(raw.ToArray(), batDau, Math.Min(soByte, raw.Count - batDau));
        }

        /// <summary>Vị trí byte đầu của "\r\n\r\n" (kết thúc header); -1 = chưa đủ.</summary>
        private static int TimKetThucHeader(List<byte> raw)
        {
            for (var i = 0; i + 3 < raw.Count; i++)
            {
                if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static int DocContentLength(string header)
        {
            foreach (var line in header.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Content-Length:".Length..].Trim(), out var n))
                {
                    return n;
                }
            }
            return 0;
        }

        /// <summary>Phản hồi kiểu Apps Script: mỗi đơn trong body → một kết quả ok/added.</summary>
        private static string TaoPhanHoi(string body)
        {
            var sb = new StringBuilder("{\"results\":[");
            using var doc = JsonDocument.Parse(body);
            var dau = true;
            foreach (var o in doc.RootElement.GetProperty("orders").EnumerateArray())
            {
                if (!dau)
                {
                    sb.Append(',');
                }
                dau = false;
                sb.Append("{\"maDon\":")
                  .Append(JsonSerializer.Serialize(o.GetProperty("maDon").GetString()))
                  .Append(",\"ok\":true,\"added\":true}");
            }
            return sb.Append("]}").ToString();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Vòng lặp đã dừng theo cách khác — không quan tâm khi dọn.
            }
            _cts.Dispose();
        }
    }
}
