using Shopee.Core.Infrastructure;

namespace Shopee.Core.Coordination;

/// <summary>
/// Cấu hình để máy này CHẠY chế độ Hub (server đồng bộ/điều phối). Cục bộ-theo-máy, KHÔNG đồng bộ.
/// <see cref="Enabled"/>=true ⇒ máy này là HUB; ngược lại là CLIENT thường. Đúng 1 máy nên bật.
/// </summary>
public sealed class HubServerConfig
{
    public bool Enabled { get; set; }
    /// <summary>Cổng local Kestrel lắng nghe (cloudflared map api.&lt;domain&gt; → 127.0.0.1:Port).</summary>
    public int Port { get; set; } = HubDefaults.Port;
    /// <summary>Tên miền công khai, ví dụ "api.schedra.net" (để hiển thị URL + tạo DNS/tunnel).</summary>
    public string Domain { get; set; } = HubDefaults.Domain;
    /// <summary>Token bảo vệ API (client phải gửi khớp ở header X-Api-Token). Tự đặt/tự sinh.</summary>
    public string ApiToken { get; set; } = HubDefaults.ApiToken;
    /// <summary>Cloudflare API token (tùy chọn) — để app TỰ tạo Named Tunnel + DNS.</summary>
    public string CloudflareApiToken { get; set; } = "";
    /// <summary>Tunnel token dán sẵn (tùy chọn) — nếu người dùng tự tạo tunnel trên dashboard CF.</summary>
    public string TunnelToken { get; set; } = "";
    /// <summary>Thư mục dữ liệu Hub (DB + blob). Trống = %AppData%\ShopeeSuite\hub-data.</summary>
    public string DataDir { get; set; } = "";

    public string PublicUrl => string.IsNullOrWhiteSpace(Domain) ? "" : $"https://{Domain.Trim().TrimStart('/')}";

    public HubServerConfig Clone() => new()
    {
        Enabled = Enabled, Port = Port, Domain = Domain, ApiToken = ApiToken,
        CloudflareApiToken = CloudflareApiToken, TunnelToken = TunnelToken, DataDir = DataDir,
    };
}

/// <summary>Kho cấu hình chế độ Hub, lưu tại %AppData%\ShopeeSuite\hub-server.json (local-only). Singleton.</summary>
public sealed class HubServerConfigStore
{
    private static readonly Lazy<HubServerConfigStore> _shared = new(() => new HubServerConfigStore());
    public static HubServerConfigStore Shared => _shared.Value;

    private static readonly string FilePath = SuitePaths.RootFile("hub-server.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private HubServerConfig _config = new();

    public event Action? Changed;

    private HubServerConfigStore() => Load();

    public HubServerConfig Current { get { lock (_lock) return _config.Clone(); } }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                    _config = JsonSerializer.Deserialize<HubServerConfig>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new HubServerConfig();
            }
            catch { _config = new HubServerConfig(); }
        }
    }

    public void Save(HubServerConfig config)
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
