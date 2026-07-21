using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test logic canh proxy <see cref="ProxyWatchdog.TryGetReplacementAsync"/> bằng stub client + checker.
/// Watchdog gọi <c>IsAliveAsync</c> trên proxy hiện tại (tối đa 2 lần: xác nhận chết 2 lần) rồi
/// <see cref="ProxySelector.SelectKiotProxyAsync"/> (gọi <c>IsAliveAsync</c> trên ứng viên) ⇒ checker dùng
/// HÀNG ĐỢI kết quả theo đúng thứ tự gọi. Dùng recheckDelayMs=0 để test không phải chờ.
/// </summary>
public class ProxyWatchdogTests
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

    /// <summary>Stub checker: trả kết quả sống/chết theo HÀNG ĐỢI (đúng thứ tự gọi), đếm số lần gọi.
    /// Hết hàng đợi → mặc định false (không dùng tới trong các ca test).</summary>
    private sealed class QueueChecker : IProxyHealthChecker
    {
        private readonly Queue<bool> _results;
        public int Calls { get; private set; }

        public QueueChecker(params bool[] results) => _results = new Queue<bool>(results);

        public Task<bool> IsAliveAsync(ProxyEntry proxy, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_results.Count > 0 && _results.Dequeue());
        }
    }

    private static ProxyEntry P(string host, int port = 8080) => new() { Host = host, Port = port };

    [Fact]
    public async Task CurrentNull_TraNull_KhongGoiKiot_KhongGoiChecker()
    {
        var kiot = new StubKiot(P("2.2.2.2"), P("3.3.3.3"));
        var checker = new QueueChecker();

        var result = await ProxyWatchdog.TryGetReplacementAsync(kiot, checker, current: null, recheckDelayMs: 0);

        Assert.Null(result);                       // IP máy — không có gì để canh
        Assert.Equal(0, kiot.CurrentCalls);
        Assert.Equal(0, kiot.NewCalls);
        Assert.Equal(0, checker.Calls);
    }

    [Fact]
    public async Task CurrentSong_TraNull_KhongGoiKiot_Chi1LanKiem()
    {
        var current = P("1.1.1.1");
        var kiot = new StubKiot(P("2.2.2.2"), P("3.3.3.3"));
        var checker = new QueueChecker(true);      // lần kiểm đầu: sống

        var result = await ProxyWatchdog.TryGetReplacementAsync(kiot, checker, current, recheckDelayMs: 0);

        Assert.Null(result);
        Assert.Equal(1, checker.Calls);            // sống ngay lần 1 → không kiểm lại
        Assert.Equal(0, kiot.CurrentCalls);
        Assert.Equal(0, kiot.NewCalls);
    }

    [Fact]
    public async Task CurrentChetLan1_SongLan2_HoiPhuc_TraNull_KhongGoiKiot()
    {
        var current = P("1.1.1.1");
        var kiot = new StubKiot(P("2.2.2.2"), P("3.3.3.3"));
        var checker = new QueueChecker(false, true); // chết lần 1, sống lần 2 (false-negative)

        var result = await ProxyWatchdog.TryGetReplacementAsync(kiot, checker, current, recheckDelayMs: 0);

        Assert.Null(result);
        Assert.Equal(2, checker.Calls);            // xác nhận 2 lần, hồi phục → thôi
        Assert.Equal(0, kiot.CurrentCalls);
        Assert.Equal(0, kiot.NewCalls);
    }

    [Fact]
    public async Task CurrentChetCa2_CurrentTraProxyKhac_UngVienSong_TraProxyMoi()
    {
        var current = P("1.1.1.1");
        var replacement = P("2.2.2.2");
        var kiot = new StubKiot(replacement, P("3.3.3.3"));
        var checker = new QueueChecker(false, false, true); // current chết x2, ứng viên sống

        var result = await ProxyWatchdog.TryGetReplacementAsync(kiot, checker, current, recheckDelayMs: 0);

        Assert.Same(replacement, result);          // trả proxy mới để relaunch
        Assert.Equal(1, kiot.CurrentCalls);
        Assert.Equal(0, kiot.NewCalls);            // /current đủ dùng → KHÔNG spam /new
    }

    [Fact]
    public async Task CurrentChetCa2_ApiKhongCapProxy_TraNull()
    {
        var current = P("1.1.1.1");
        var kiot = new StubKiot(null, null);       // /current & /new đều null → SelectKiotProxyAsync trả null
        var checker = new QueueChecker(false, false);

        var result = await ProxyWatchdog.TryGetReplacementAsync(kiot, checker, current, recheckDelayMs: 0);

        Assert.Null(result);
        Assert.Equal(2, checker.Calls);            // chỉ kiểm current (2 lần); ứng viên không có → không kiểm thêm
    }

    [Fact]
    public async Task CurrentChetCa2_UngVienChet_TraNull()
    {
        var current = P("1.1.1.1");
        var kiot = new StubKiot(P("2.2.2.2"), null); // /current có proxy nhưng ứng viên chết
        var checker = new QueueChecker(false, false, false); // current chết x2, ứng viên cũng chết

        var result = await ProxyWatchdog.TryGetReplacementAsync(kiot, checker, current, recheckDelayMs: 0);

        Assert.Null(result);                       // SelectKiotProxyAsync trả null (ứng viên chết)
        Assert.Equal(3, checker.Calls);
    }

    [Fact]
    public async Task CurrentChetCa2_UngVienTrungEndpoint_TraNull_ChoChuKySau()
    {
        var current = P("1.1.1.1", 8080);
        var sameEndpoint = P("1.1.1.1", 8080);     // khác instance nhưng CÙNG host:port (chưa xoay xong)
        var kiot = new StubKiot(sameEndpoint, null);
        var checker = new QueueChecker(false, false, true); // current chết x2, ứng viên "sống"

        var result = await ProxyWatchdog.TryGetReplacementAsync(kiot, checker, current, recheckDelayMs: 0);

        Assert.Null(result);                       // trùng endpoint cũ → chờ chu kỳ sau, KHÔNG relaunch vô cớ
    }

    [Fact]
    public void ProxyEndpointsEqual_SoKhopHostPort_KhongPhanBietHoaThuong()
    {
        Assert.True(ProxyWatchdog.ProxyEndpointsEqual(P("1.1.1.1", 8080), P("1.1.1.1", 8080)));  // cùng host+port
        Assert.False(ProxyWatchdog.ProxyEndpointsEqual(P("1.1.1.1", 8080), P("1.1.1.1", 9090))); // khác port
        Assert.False(ProxyWatchdog.ProxyEndpointsEqual(P("1.1.1.1", 8080), P("2.2.2.2", 8080))); // khác host
        Assert.True(ProxyWatchdog.ProxyEndpointsEqual(P("HOST.A", 8080), P("host.a", 8080)));    // hoa/thường
    }
}
