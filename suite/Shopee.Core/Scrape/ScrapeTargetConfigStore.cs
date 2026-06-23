using Shopee.Core.Infrastructure;

namespace Shopee.Core.Scrape;

/// <summary>
/// Cấu hình scrape RIÊNG của một tài khoản BigSeller (đích scrape) do người dùng đặt ở mục Shopee
/// Scrape: shop/sheet đang chọn, có tick chạy không, và các tham số dòng/process. Tách khỏi tiến độ
/// (<see cref="ScrapeProgress"/>) vì đây là "cài đặt người dùng" cần GIỮ LẠI qua reload + khởi động lại.
/// </summary>
public sealed class ScrapeTargetConfig
{
    public string AccountId { get; set; } = "";       // tài khoản BigSeller
    public string? SelectedShopId { get; set; }        // shop (↔ sheet) đang chọn
    public bool IsSelected { get; set; }               // có tick để chạy không

    public int StartRow { get; set; } = 2;
    public int EndRow { get; set; }
    public int RowsPerAccount { get; set; } = 30;
    public int MaxProcess { get; set; } = 2;
}

/// <summary>
/// Kho cấu hình đích scrape, lưu tại %AppData%\ShopeeSuite\shared\scrape-targets.json, khoá theo
/// AccountId (BigSeller). Thread-safe; lưu nguyên tử (file tạm → move). Cùng phong cách với
/// <see cref="ScrapeProgressStore"/>.
/// </summary>
public sealed class ScrapeTargetConfigStore
{
    private static readonly Lazy<ScrapeTargetConfigStore> _shared = new(() => new ScrapeTargetConfigStore());
    public static ScrapeTargetConfigStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "scrape-targets.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<ScrapeTargetConfig> _items = [];

    private ScrapeTargetConfigStore() => Load();

    public void Load()
    {
        lock (_lock)
        {
            _items.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<ScrapeTargetConfig>>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (list is not null) _items.AddRange(list);
                }
            }
            catch { }
        }
    }

    /// <summary>Lấy cấu hình đã lưu của 1 tài khoản BigSeller — null nếu chưa từng lưu.</summary>
    public ScrapeTargetConfig? Find(string accountId)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => x.AccountId == accountId);
            return p is null ? null : Clone(p);
        }
    }

    /// <summary>Lưu (upsert) cấu hình của 1 tài khoản BigSeller.</summary>
    public void Save(ScrapeTargetConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.AccountId)) return;
        lock (_lock)
        {
            _items.RemoveAll(x => x.AccountId == config.AccountId);
            _items.Add(Clone(config));
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    private static ScrapeTargetConfig Clone(ScrapeTargetConfig c) => new()
    {
        AccountId = c.AccountId,
        SelectedShopId = c.SelectedShopId,
        IsSelected = c.IsSelected,
        StartRow = c.StartRow,
        EndRow = c.EndRow,
        RowsPerAccount = c.RowsPerAccount,
        MaxProcess = c.MaxProcess,
    };
}
