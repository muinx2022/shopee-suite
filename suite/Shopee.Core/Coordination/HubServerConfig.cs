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
    /// <summary>Token bảo vệ API (client phải gửi khớp ở header X-Api-Token). Tự đặt/tự sinh.</summary>
    public string ApiToken { get; set; } = HubDefaults.ApiToken;

    public HubServerConfig Clone() => new() { Enabled = Enabled, Port = Port, ApiToken = ApiToken };
}

/// <summary>Kho cấu hình chế độ Hub, đọc từ %AppData%\ShopeeSuite\hub-server.json (local-only, CHỈ ĐỌC —
/// hub web nay chạy riêng trên VM nên app không còn ghi file này). Singleton.</summary>
public sealed class HubServerConfigStore
{
    private static readonly Lazy<HubServerConfigStore> _shared = new(() => new HubServerConfigStore());
    public static HubServerConfigStore Shared => _shared.Value;

    private static readonly string FilePath = SuitePaths.RootFile("hub-server.json");

    private readonly object _lock = new();
    private HubServerConfig _config = new();

    private HubServerConfigStore() => Load();

    public HubServerConfig Current { get { lock (_lock) return _config.Clone(); } }

    /// <summary>Đọc file cấu hình một lần lúc dựng singleton (không có nơi nào nạp lại lúc chạy).</summary>
    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return;   // chưa có file → GIỮ bản đang có (khác file hỏng → về mặc định)
            _config = JsonAtomicFile.TryLoad<HubServerConfig>(FilePath) ?? new HubServerConfig();
        }
    }
}
