namespace ShopeeStatApp.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _settingsPath;
    private readonly string _apiConfigPath;
    // Parallel auto-run logs in concurrently (login write-back), so serialize disk writes.
    private readonly object _saveLock = new();

    public LauncherSettings Settings { get; private set; } = new();
    public ShopeeApiConfig ApiConfig { get; private set; } = new();

    public AppSettingsService()
    {
        var appDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShopeeStatApp");
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
        _apiConfigPath = Path.Combine(appDir, "appsettings.json");
    }

    public void Load()
    {
        if (File.Exists(_settingsPath))
        {
            try { Settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_settingsPath), Opts) ?? new(); }
            catch { Settings = new(); }
        }

        if (File.Exists(_apiConfigPath))
        {
            try { ApiConfig = JsonSerializer.Deserialize<ShopeeApiConfig>(File.ReadAllText(_apiConfigPath), Opts) ?? new(); }
            catch { ApiConfig = new(); }
        }
    }

    public void SaveSettings()
    {
        lock (_saveLock)
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, Opts), Encoding.UTF8);
    }

    public void SaveApiConfig(string json)
    {
        File.WriteAllText(_apiConfigPath, json, Encoding.UTF8);
        try { ApiConfig = JsonSerializer.Deserialize<ShopeeApiConfig>(json, Opts) ?? new(); }
        catch { }
    }

    public string GetApiConfigJson() =>
        JsonSerializer.Serialize(ApiConfig, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    public string GetProfileDir(InstanceConfig config)
    {
        config.EnsureProfileRelativePath();
        var dir = Path.GetFullPath(config.ProfileRelativePath);
        var defaultDir = Path.Combine(dir, "Default");
        // CreateDirectory có thể ném "Access denied" (UnauthorizedAccessException) khi profile đang bị Brave
        // (mồ côi / đang đóng) khoá / ở trạng thái delete-pending, hoặc antivirus vừa quét file mới ghi.
        // Thử lại vài nhịp; giữa chừng KILL Brave đang giữ ĐÚNG profile này (an toàn: mỗi profile chỉ 1 lane
        // dùng nhờ account-lease) → phần lớn tự hồi (lock nhả sau ~1–2s), khỏi "bỏ qua link" oan.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try { Directory.CreateDirectory(defaultDir); return dir; }
            catch (Exception) { }
            if (attempt == 1) { try { BraveManager.KillBraveProcessesForProfile(dir); } catch { } }
            Thread.Sleep(300);
        }
        Directory.CreateDirectory(defaultDir);   // lần cuối KHÔNG nuốt → surface lỗi thật (vd sai quyền) nếu vẫn hỏng
        return dir;
    }

}
