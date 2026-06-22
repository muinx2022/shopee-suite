using Shopee.Core.Infrastructure;

namespace Shopee.Core.BigSeller;

/// <summary>
/// Kho tài khoản BigSeller dùng chung, lưu tại %AppData%\ShopeeSuite\shared\bigseller.json.
/// Scrape và Update Product đọc cùng kho này (workbook, shop/sheet, cookie chung).
/// </summary>
public sealed class BigSellerStore
{
    private static readonly Lazy<BigSellerStore> _shared = new(() => new BigSellerStore());
    public static BigSellerStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "bigseller.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<BigSellerAccount> _accounts = [];

    public event Action? Changed;

    private BigSellerStore() => Load();

    public IReadOnlyList<BigSellerAccount> Accounts
    {
        get { lock (_lock) return _accounts.ToList(); }
    }

    public BigSellerAccount? Find(string id)
    {
        lock (_lock) return _accounts.FirstOrDefault(a => a.Id == id);
    }

    public void Add(BigSellerAccount account)
    {
        lock (_lock) _accounts.Add(account);
        Save();
    }

    public void Remove(string id)
    {
        lock (_lock) _accounts.RemoveAll(a => a.Id == id);
        Save();
    }

    /// <summary>Thay toàn bộ danh sách (dùng khi import/khôi phục) rồi lưu.</summary>
    public void ReplaceAll(IEnumerable<BigSellerAccount> accounts)
    {
        lock (_lock)
        {
            _accounts.Clear();
            _accounts.AddRange(accounts);
        }
        Save();
    }

    public void Load()
    {
        lock (_lock)
        {
            _accounts.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<BigSellerAccount>>(
                        File.ReadAllText(FilePath, Encoding.UTF8));
                    if (list is not null) _accounts.AddRange(list);
                }
            }
            catch { }
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_accounts, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
        Changed?.Invoke();
    }
}
