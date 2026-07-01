using Shopee.Core.Accounts;
using Shopee.Core.Infrastructure;

namespace Shopee.Core.Proxy;

/// <summary>
/// Kho KiotProxy DÙNG CHUNG (danh sách key hoặc proxy trực tiếp host:port), tách khỏi tài khoản.
/// Lúc chạy, mỗi acc lấy proxy theo VỊ TRÍ trong kho: acc thứ i → proxy[i % số proxy] (xoay vòng).
/// Máy Hub quản lý; client tự nhận qua đồng bộ (HubConfigSync ghi đè bằng bản Hub). Lưu tại
/// %AppData%\ShopeeSuite\shared\kiot-proxies.json. Mirror <see cref="AccountStore"/>.
/// </summary>
public sealed class KiotProxyPoolStore
{
    private static readonly Lazy<KiotProxyPoolStore> _shared = new(() => new KiotProxyPoolStore());
    public static KiotProxyPoolStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "kiot-proxies.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<string> _keys = [];

    /// <summary>Phát khi kho đổi (lưu/đồng bộ về) để UI làm mới.</summary>
    public event Action? Changed;

    private KiotProxyPoolStore() => Load();

    /// <summary>Danh sách entry (key KiotProxy hoặc host:port), theo thứ tự xoay vòng.</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_lock) return _keys.ToList(); }
    }

    public int Count { get { lock (_lock) return _keys.Count; } }

    /// <summary>Thay toàn bộ kho (dùng cho Lưu tay + đồng bộ từ Hub). Bỏ dòng trống/trùng, giữ thứ tự.</summary>
    public bool ReplaceAll(IEnumerable<string> entries)
    {
        var incoming = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries ?? [])
        {
            var e = (raw ?? "").Trim();
            if (e.Length == 0) continue;
            if (seen.Add(e)) incoming.Add(e);
        }
        lock (_lock)
        {
            var prev = _keys.ToList();
            _keys.Clear();
            _keys.AddRange(incoming);
            if (SaveLocked()) return true;
            _keys.Clear();
            _keys.AddRange(prev);
            return false;
        }
    }

    /// <summary>
    /// Proxy cho 1 acc theo XOAY VÒNG vị trí acc trong kho tài khoản: idx = vị trí trong
    /// <see cref="AccountStore.Accounts"/>; entry = keys[idx % keys.Count]. Trả về (KiotKey, Manual) đã tách
    /// (entry trực tiếp host:port → Manual; ngược lại → KiotKey). null nếu kho proxy RỖNG hoặc không thấy acc
    /// (khi đó caller giữ proxy gắn sẵn của acc — fallback tương thích).
    /// </summary>
    public (string KiotKey, string Manual)? ProxyForAccount(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return null;
        // Vị trí acc trong danh sách acc CÒN BẬT (BỎ acc disabled — chúng không chạy; nếu tính cả sẽ tạo lỗ
        // hổng index → nhiều acc sống dồn vào 1 proxy, proxy khác bỏ trống). Snapshot NGOÀI _lock (khỏi lồng khoá).
        var accounts = AccountStore.Shared.Accounts;
        var idx = -1; var pos = 0;
        for (var i = 0; i < accounts.Count; i++)
        {
            if (accounts[i].Disabled) continue;
            if (string.Equals(accounts[i].Id, accountId, StringComparison.Ordinal)) { idx = pos; break; }
            pos++;
        }
        if (idx < 0) return null;   // acc bị tắt / không thấy → giữ proxy gắn sẵn của acc (caller lo)
        string entry;
        lock (_lock)
        {
            if (_keys.Count == 0) return null;
            entry = _keys[idx % _keys.Count];
        }
        return ProxyPool.IsDirectProxy(entry) ? ("", entry) : (entry, "");
    }

    public void Load()
    {
        lock (_lock)
        {
            _keys.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (list is not null)
                        foreach (var e in list) { var t = (e ?? "").Trim(); if (t.Length > 0) _keys.Add(t); }
                }
            }
            catch { }
        }
    }

    private bool SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(_keys, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
            Changed?.Invoke();
            return true;
        }
        catch { return false; }
    }
}
