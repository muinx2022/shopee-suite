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

    public bool Add(ShopeeAccount account)
    {
        account.EnsureProfilePath();
        lock (_lock)
        {
            _accounts.Add(account);
            if (SaveLocked()) return true;
            _accounts.Remove(account);
            return false;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _accounts.Where(a => a.Id == id).ToList();
            if (removed.Count == 0) return true;
            _accounts.RemoveAll(a => a.Id == id);
            if (SaveLocked()) return true;
            _accounts.AddRange(removed);
            return false;
        }
    }

    /// <summary>Thay toàn bộ danh sách (dùng khi import/sắp xếp lại) rồi lưu.</summary>
    public bool ReplaceAll(IEnumerable<ShopeeAccount> accounts)
    {
        var incoming = accounts.ToList();
        foreach (var a in incoming) a.EnsureProfilePath();
        lock (_lock)
        {
            var previous = _accounts.ToList();
            _accounts.Clear();
            _accounts.AddRange(incoming);
            if (SaveLocked()) return true;
            _accounts.Clear();
            _accounts.AddRange(previous);
            return false;
        }
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

    public bool Save()
    {
        lock (_lock)
        {
            return SaveLocked();
        }
    }

    private bool SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(_accounts, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
            Changed?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
