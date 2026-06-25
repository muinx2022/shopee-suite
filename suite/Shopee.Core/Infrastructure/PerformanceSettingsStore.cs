namespace Shopee.Core.Infrastructure;

/// <summary>Cấu hình hiệu năng do người dùng đặt (mục Cài đặt → Hiệu năng). Lưu bền qua các lần build.
/// Trần cửa sổ Brave được TÍNH từ ngân sách CPU/RAM: max = min(usableCpu, usableRamGb/2).</summary>
public sealed class PerformanceSettings
{
    /// <summary>Số nhân CPU cho phép app dùng (mỗi cửa sổ Brave ~1 nhân). 0 = mặc định (nửa số nhân máy).</summary>
    public int UsableCpuCores { get; set; }

    /// <summary>RAM (GB) cho phép app dùng (mỗi cửa sổ Brave ~2GB). 0 = mặc định (toàn bộ RAM máy).</summary>
    public int UsableRamGb { get; set; }
}

/// <summary>
/// Kho cấu hình hiệu năng, lưu tại %AppData%\ShopeeSuite\shared\performance.json. Thread-safe; lưu
/// nguyên tử (file tạm → move). Cùng phong cách với các store khác.
/// </summary>
public sealed class PerformanceSettingsStore
{
    private static readonly Lazy<PerformanceSettingsStore> _shared = new(() => new PerformanceSettingsStore());
    public static PerformanceSettingsStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "performance.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    public PerformanceSettings Current { get; private set; } = new();

    private PerformanceSettingsStore() => Load();

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                    Current = JsonSerializer.Deserialize<PerformanceSettings>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new();
            }
            catch { Current = new(); }
        }
    }

    public void Save(PerformanceSettings settings)
    {
        lock (_lock)
        {
            Current = settings;
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts), Encoding.UTF8);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
