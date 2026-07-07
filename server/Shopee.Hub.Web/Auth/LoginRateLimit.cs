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
