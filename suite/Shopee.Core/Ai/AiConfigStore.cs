using Shopee.Core.Infrastructure;

namespace Shopee.Core.Ai;

/// <summary>Kho cấu hình AI dùng chung, lưu tại %AppData%\ShopeeSuite\shared\ai.json. Singleton.</summary>
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
            try
            {
                if (File.Exists(FilePath))
                    _config = JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new AiConfig();
            }
            catch { _config = new AiConfig(); }
        }
    }

    public void Save(AiConfig config)
    {
        try
        {
            string json;
            lock (_lock) { _config = config.Clone(); json = JsonSerializer.Serialize(_config, JsonOpts); }
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
        Changed?.Invoke();
    }
}
