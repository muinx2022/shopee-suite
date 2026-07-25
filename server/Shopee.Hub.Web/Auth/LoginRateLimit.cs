using System.Collections.Concurrent;

namespace Shopee.Hub.Web.Auth;

/// <summary>Giới hạn đăng nhập sai: tối đa 5 lần / 5 phút / IP (cửa sổ trượt đơn giản). Chống dò mật khẩu admin
/// qua tunnel. Đọc IP thực từ header CF-Connecting-IP (cloudflared) nếu có.</summary>
public sealed class LoginRateLimit
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _hits = new();

    /// <summary>IP còn được phép thử không (chưa quá 5 lần sai trong 5 phút).</summary>
    public bool Allow(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        var list = _hits.GetOrAdd(ip, _ => new List<DateTimeOffset>());
        lock (list)
        {
            list.RemoveAll(t => now - t > Window);
            return list.Count < MaxAttempts;
        }
    }

    /// <summary>Ghi nhận 1 lần thử SAI cho IP.</summary>
    public void RecordFailure(string ip)
    {
        var list = _hits.GetOrAdd(ip, _ => new List<DateTimeOffset>());
        lock (list) list.Add(DateTimeOffset.UtcNow);
        Prune();
    }

    /// <summary>Dọn IP đã hết hạn (mọi lần thử đều ngoài cửa sổ 5') khỏi <see cref="_hits"/> — chống rò bộ nhớ
    /// chậm khi nhiều IP lạ thử 1-2 lần rồi thôi (Reset chỉ xoá IP đăng nhập THÀNH CÔNG). Gọi khi ghi thất bại —
    /// tần suất thấp nên quét toàn bộ ở đây không tốn. TryRemove theo cặp key+value → chỉ xoá đúng list đang rỗng
    /// (không đụng list vừa được thread khác thay/ghi lại).</summary>
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _hits)
        {
            bool empty;
            lock (kv.Value) { kv.Value.RemoveAll(t => now - t > Window); empty = kv.Value.Count == 0; }
            if (empty) _hits.TryRemove(kv);   // xoá đúng cặp key+value (reference) → không đụng list vừa bị ghi lại
        }
    }

    /// <summary>Xoá lịch sử sai của IP (gọi khi đăng nhập thành công).</summary>
    public void Reset(string ip) => _hits.TryRemove(ip, out _);

    /// <summary>IP thực của request: CF-Connecting-IP (qua Cloudflare) → RemoteIpAddress → "?".</summary>
    public static string IpOf(HttpContext ctx)
    {
        var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
        if (!string.IsNullOrWhiteSpace(cf)) return cf;
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    }
}
