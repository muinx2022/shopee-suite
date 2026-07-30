using Shopee.Core.Infrastructure;

namespace Shopee.Core.Ai;

/// <summary>Kho cấu hình AI dùng chung, lưu tại %AppData%\ShopeeSuite\shared\ai.json. Singleton.
/// Giờ CHỈ là CACHE/FALLBACK của bản Hub (nguồn sự thật) — client lấy tươi qua <see cref="HubAiConfig"/>.</summary>
public sealed class AiConfigStore
{
    private static readonly Lazy<AiConfigStore> _shared = new(() => new AiConfigStore());
    public static AiConfigStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "ai.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private AiConfig _config = new();

    public event Action? Changed;

    private AiConfigStore() => Load();

    /// <summary>Bản sao cấu hình hiện tại (an toàn để bind/sửa rồi Save).</summary>
    public AiConfig Current { get { lock (_lock) return _config.Clone(); } }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return;   // chưa có file → GIỮ bản đang có (khác file hỏng → về mặc định)
            _config = JsonAtomicFile.TryLoad<AiConfig>(FilePath) ?? new AiConfig();
        }
    }

    public void Save(AiConfig config)
    {
        try
        {
            string json;
            lock (_lock) { _config = config.Clone(); json = JsonSerializer.Serialize(_config, JsonOpts); }
            JsonAtomicFile.SaveText(FilePath, json);   // serialize trong lock, ghi đĩa ngoài lock (giữ như cũ)
        }
        catch { }
        Changed?.Invoke();
    }
}
