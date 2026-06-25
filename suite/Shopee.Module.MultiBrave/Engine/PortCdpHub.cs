using System.Collections.Concurrent;
using System.Text.Json;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Kết nối CDP cấp BROWSER DÙNG CHUNG cho mỗi cdpPort (1 <see cref="CdpSession"/> sống dài, multiplex).
/// Mọi thao tác CDP (pinner, probe, lệnh scrape per-link…) GẮN FLAT-SESSION vào kết nối này thay vì
/// mở <c>new ClientWebSocket()</c> mỗi lần — đó chính là nguồn churn làm cạn cổng TCP/treo DevTools sau
/// vài giờ (xem chẩn đoán 25/06: Brave sống càng lâu, churn WS càng tích tụ → SW câm "đoạn sau").
///
/// Tự NỐI LẠI: khi Brave bị Kill/relaunch (cùng port, tiến trình mới), kết nối cũ chết →
/// <see cref="EnsureAsync"/> phát hiện <c>IsOpen=false</c> và nối lại tới Brave mới ở lần dùng kế.
/// Hub được GIỮ theo port (port tái dùng giữa các chunk) nên không cần dọn thủ công.
/// </summary>
internal sealed class PortCdpHub
{
    private static readonly ConcurrentDictionary<int, PortCdpHub> ByPort = new();

    /// <summary>Hub dùng chung của 1 cdpPort (tạo nếu chưa có).</summary>
    public static PortCdpHub For(int port) => ByPort.GetOrAdd(port, p => new PortCdpHub(p));

    private readonly int _port;
    private readonly SemaphoreSlim _connectGate = new(1, 1);   // serialize (tái) kết nối
    private CdpSession? _session;

    private PortCdpHub(int port) => _port = port;

    private async Task<CdpSession> EnsureAsync(CancellationToken ct)
    {
        var s = _session;
        if (s is { IsOpen: true }) return s;

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { IsOpen: true }) return _session;
            if (_session is not null) { try { await _session.DisposeAsync().ConfigureAwait(false); } catch { } _session = null; }
            _session = await CdpSession.ConnectToBrowserAsync(_port, ct).ConfigureAwait(false);
            return _session;
        }
        finally { _connectGate.Release(); }
    }

    /// <summary>Gắn flat-session vào 1 target (page/SW) → trả sessionId (null nếu lỗi). Lệnh sau gửi kèm sessionId.</summary>
    public async Task<string?> AttachAsync(string targetId, CancellationToken ct = default)
    {
        var s = await EnsureAsync(ct).ConfigureAwait(false);
        return await s.AttachToTargetAsync(targetId, ct).ConfigureAwait(false);
    }

    /// <summary>Gửi lệnh CDP (kèm sessionId nếu chạy trong flat-session) qua kết nối dùng chung.</summary>
    public async Task<JsonElement> SendAsync(string method, object? @params = null, string? sessionId = null, CancellationToken ct = default, int timeoutMs = 20_000)
    {
        var s = await EnsureAsync(ct).ConfigureAwait(false);
        return await s.SendAsync(method, @params, ct, sessionId, timeoutMs).ConfigureAwait(false);
    }

    /// <summary>Gửi lệnh không chờ phản hồi (vd Input.* trung gian).</summary>
    public async Task SendNoReplyAsync(string method, object? @params = null, string? sessionId = null, CancellationToken ct = default)
    {
        var s = await EnsureAsync(ct).ConfigureAwait(false);
        await s.SendNoReplyAsync(method, @params, ct, sessionId).ConfigureAwait(false);
    }

    /// <summary>Rút kết nối hiện tại (best-effort) — gọi khi Brave bị Kill. CHỈ Abort WS (thread-safe), KHÔNG
    /// dispose ở đây: dispose ngoài-gate (fire-and-forget Task.Run cũ) ĐUA với caller đang giữ ref session →
    /// ObjectDisposedException trên _sendLock/_ws. Abort → ReadLoop chết → pending bị hủy + IsOpen=false →
    /// <see cref="EnsureAsync"/> sẽ dispose+nối lại AN TOÀN dưới _connectGate ở lần dùng kế.</summary>
    public void ResetSoon()
    {
        try { _session?.AbortConnection(); } catch { }
    }
}
