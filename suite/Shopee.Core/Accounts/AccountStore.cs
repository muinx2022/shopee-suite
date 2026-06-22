using Shopee.Core.Infrastructure;

namespace Shopee.Core.Accounts;

/// <summary>
/// Kho tài khoản Shopee dùng chung cho toàn suite, lưu tại %AppData%\ShopeeSuite\accounts.json.
/// Mọi module (Scrape, Search) đọc cùng một danh sách này; sửa ở mục "Tài khoản & Proxy" là
/// các module thấy ngay. Singleton <see cref="Shared"/> để mọi ViewModel dùng chung một instance.
/// </summary>
public sealed class AccountStore
{
    private static readonly Lazy<AccountStore> _shared = new(() => new AccountStore());
    public static AccountStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "accounts.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<ShopeeAccount> _accounts = [];

    /// <summary>Phát khi danh sách thay đổi (thêm/xóa/lưu) để các ViewModel khác làm mới.</summary>
    public event Action? Changed;

    private AccountStore() => Load();

    public IReadOnlyList<ShopeeAccount> Accounts
    {
        get { lock (_lock) return _accounts.ToList(); }
    }

    public ShopeeAccount? Find(string id)
    {
        lock (_lock) return _accounts.FirstOrDefault(a => a.Id == id);
    }

    public void Add(ShopeeAccount account)
    {
        account.EnsureProfilePath();
        lock (_lock) _accounts.Add(account);
        Save();
    }

    public void Remove(string id)
    {
        lock (_lock) _accounts.RemoveAll(a => a.Id == id);
        Save();
    }

    /// <summary>Thay toàn bộ danh sách (dùng khi import/sắp xếp lại) rồi lưu.</summary>
    public void ReplaceAll(IEnumerable<ShopeeAccount> accounts)
    {
        lock (_lock)
        {
            _accounts.Clear();
            foreach (var a in accounts) { a.EnsureProfilePath(); _accounts.Add(a); }
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
                    var list = JsonSerializer.Deserialize<List<ShopeeAccount>>(
                        File.ReadAllText(FilePath, Encoding.UTF8));
                    if (list is not null)
                        foreach (var a in list) { a.EnsureProfilePath(); _accounts.Add(a); }
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
