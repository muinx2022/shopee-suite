namespace Shopee.Core.Infrastructure;

/// <summary>Cài đặt UI DÙNG CHUNG của module "Bigseller Update Product" (ảnh/video áp cho mọi tk) —
/// lưu để khôi phục sau khi mở lại app (trước đây là UI-state nên mất khi đóng app).</summary>
public sealed class UpdateProductUiSettings
{
    public string ImagePath { get; set; } = "";
    public string VideoFolder { get; set; } = "";
    /// <summary>LEGACY — không còn UI/engine nào đọc (key AI giờ lấy từ AiConfig trên Hub); giữ để JSON cũ đọc được.</summary>
    public string OpenAiKeyFile { get; set; } = "";
}

/// <summary>Kho cài đặt UI của module Update Product, lưu tại %AppData%\ShopeeSuite\shared\update-product-ui.json.
/// Thread-safe, lưu nguyên tử (file tạm → move). Cùng phong cách <see cref="PerformanceSettingsStore"/>.</summary>
public sealed class UpdateProductUiStore
{
    private static readonly Lazy<UpdateProductUiStore> _shared = new(() => new UpdateProductUiStore());
    public static UpdateProductUiStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "update-product-ui.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    public UpdateProductUiSettings Current { get; private set; } = new();

    private UpdateProductUiStore() => Load();

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return;   // chưa có file → GIỮ bản đang có (khác file hỏng → về mặc định)
            Current = JsonAtomicFile.TryLoad<UpdateProductUiSettings>(FilePath) ?? new();
        }
    }

    public void Save(UpdateProductUiSettings settings)
    {
        lock (_lock)
        {
            Current = settings;
            JsonAtomicFile.Save(FilePath, settings, JsonOpts);
        }
    }
}
