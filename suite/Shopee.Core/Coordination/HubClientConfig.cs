using Shopee.Core.Infrastructure;

namespace Shopee.Core.Coordination;

/// <summary>
/// Cấu hình để máy này KẾT NỐI tới Hub (server đồng bộ/điều phối). Cục bộ-theo-máy, KHÔNG đồng bộ
/// (mỗi máy có thể trỏ URL/token khác nhau). Rỗng/tắt = chạy single-machine như cũ.
/// </summary>
public sealed class HubClientConfig
{
    public bool Enabled { get; set; }
    /// <summary>URL gốc của Hub, ví dụ https://api.schedra.net (qua Cloudflare Tunnel).</summary>
    public string BaseUrl { get; set; } = HubDefaults.BaseUrl;
    /// <summary>API token khớp với token Hub đặt (gửi ở header X-Api-Token).</summary>
    public string ApiToken { get; set; } = HubDefaults.ApiToken;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);

    public HubClientConfig Clone() => new() { Enabled = Enabled, BaseUrl = BaseUrl, ApiToken = ApiToken };
}

/// <summary>Kho cấu hình client→Hub, lưu tại %AppData%\ShopeeSuite\hub-client.json (local-only). Singleton.</summary>
public sealed class HubClientConfigStore
{
    private static readonly Lazy<HubClientConfigStore> _shared = new(() => new HubClientConfigStore());
    public static HubClientConfigStore Shared => _shared.Value;

    private static readonly string FilePath = SuitePaths.RootFile("hub-client.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private HubClientConfig _config = new();

    public event Action? Changed;

    private HubClientConfigStore() => Load();

    public HubClientConfig Current { get { lock (_lock) return _config.Clone(); } }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                    _config = JsonSerializer.Deserialize<HubClientConfig>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new HubClientConfig();
            }
            catch { _config = new HubClientConfig(); }
        }
    }

    public void Save(HubClientConfig config)
    {
        try
        {
            string json;
            lock (_lock) { _config = config.Clone(); json = JsonSerializer.Serialize(_config, JsonOpts); }
            Directory.CreateDirectory(SuitePaths.Root);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
        Changed?.Invoke();
    }
}
