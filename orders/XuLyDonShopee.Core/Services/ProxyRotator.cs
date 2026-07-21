using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Chọn proxy để dùng khi chạy một tài khoản. Quy tắc:
/// <list type="bullet">
///   <item>Có proxy trong danh sách → xoay vòng (round-robin), thread-safe.</item>
///   <item>Danh sách trống + có KiotProxy client → lấy proxy từ KiotProxy.</item>
///   <item>Danh sách trống + không có client (hoặc KiotProxy lỗi) → trả <c>null</c> = dùng IP máy.</item>
/// </list>
/// </summary>
public class ProxyRotator
{
    private readonly object _lock = new();
    private readonly IKiotProxyClient? _kiotClient;
    private List<ProxyEntry> _proxies;
    private int _index;

    public ProxyRotator(IEnumerable<ProxyEntry>? proxies = null, IKiotProxyClient? kiotClient = null)
    {
        _proxies = proxies?.ToList() ?? new List<ProxyEntry>();
        _kiotClient = kiotClient;
    }

    /// <summary>Số proxy hiện có trong danh sách.</summary>
    public int Count
    {
        get { lock (_lock) { return _proxies.Count; } }
    }

    /// <summary>Cập nhật danh sách proxy mới và reset vị trí xoay vòng.</summary>
    public void Reload(IEnumerable<ProxyEntry> proxies)
    {
        lock (_lock)
        {
            _proxies = proxies?.ToList() ?? new List<ProxyEntry>();
            _index = 0;
        }
    }

    /// <summary>
    /// Lấy proxy kế tiếp. Trả <c>null</c> nghĩa là dùng IP máy (kết nối trực tiếp).
    /// </summary>
    public async Task<ProxyEntry?> GetNextAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_proxies.Count > 0)
            {
                var proxy = _proxies[_index];
                _index = (_index + 1) % _proxies.Count;
                return proxy;
            }
        }

        // Danh sách trống → thử KiotProxy nếu có; lỗi/không có → null (dùng IP máy).
        if (_kiotClient != null)
        {
            return await _kiotClient.GetNewProxyAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
