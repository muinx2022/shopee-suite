using System.Collections.Concurrent;

namespace Shopee.Core.BigSeller;

/// <summary>
/// Sổ phiên đăng nhập BigSeller THEO TỪNG MÁY (client-local): mốc thời điểm auto-login mint token tươi cho mỗi
/// acc. Trong <see cref="Ttl"/> thì coi phiên còn sống → KHÔNG login lại (dùng lại token cho cả dây chuyền
/// scrape→import→update). Quá TTL / chưa từng login → login lại đầu job.
/// (Phase 4 bản đơn giản: CHỈ theo TTL, KHÔNG bắt chết-giữa-chừng — thêm sau nếu cần.)
/// </summary>
public static class BigSellerSessionRegistry
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _loginAt = new(StringComparer.Ordinal);

    /// <summary>TTL phiên: trong khoảng này không login lại. Mặc định 4h.</summary>
    public static TimeSpan Ttl { get; set; } = TimeSpan.FromHours(4);

    public static bool IsFresh(string accountId) =>
        !string.IsNullOrEmpty(accountId) && _loginAt.TryGetValue(accountId, out var t) && (DateTimeOffset.Now - t) < Ttl;

    public static void MarkLoggedIn(string accountId)
    { if (!string.IsNullOrEmpty(accountId)) _loginAt[accountId] = DateTimeOffset.Now; }

    public static void Invalidate(string accountId)
    { if (!string.IsNullOrEmpty(accountId)) _loginAt.TryRemove(accountId, out _); }
}
