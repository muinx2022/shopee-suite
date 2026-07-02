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
        // CreateDirectory có thể ném "Access denied" (UnauthorizedAccessException) khi thư mục profile đang bị
        // Brave (mồ côi/đang đóng) hoặc tiến trình con crashpad_handler khoá / ở trạng thái delete-pending, bị
        // đặt cờ read-only, hoặc antivirus vừa quét file mới ghi. Thử lại CÓ BACKOFF; giữa chừng KILL Brave +
        // crashpad giữ ĐÚNG profile này (an toàn: mỗi profile chỉ 1 lane dùng nhờ account-lease) và bỏ cờ
        // read-only → phần lớn tự hồi, khỏi "bỏ qua link" oan.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.CreateDirectory(defaultDir); return dir; }
            catch (Exception) { }
            if (attempt == 0 || attempt == 3) { try { BraveManager.KillBraveProcessesForProfile(dir); } catch { } }
            if (attempt == 1) { try { ClearReadOnly(dir); } catch { } }
            Thread.Sleep(200 + attempt * 150);   // 200,350,…,1550ms (tổng ~8.6s) — đủ để lock/AV/delete-pending nhả
        }
        // Vẫn hỏng sau 10 lần → nêu RÕ tiến trình còn giữ profile (để chẩn đoán) rồi ném; link vẫn bị bỏ qua
        // như cũ nhưng KÈM LÝ DO cụ thể thay vì "Access denied" chung chung.
        try { Directory.CreateDirectory(defaultDir); return dir; }
        catch (Exception ex)
        {
            var holders = BraveManager.DescribeProfileHolders(dir);
            throw new IOException(
                $"Không tạo được profile '{defaultDir}' sau 10 lần thử" +
                (string.IsNullOrEmpty(holders) ? "" : $" — đang bị giữ bởi: {holders}") +
                $". Lỗi gốc: {ex.Message}", ex);
        }
    }

    // Bỏ cờ read-only trên cây thư mục profile (vài trường hợp copy/đồng bộ đặt read-only làm chặn tạo/ghi).
    private static void ClearReadOnly(string root)
    {
        var di = new DirectoryInfo(root);
        if (!di.Exists) return;
        if ((di.Attributes & FileAttributes.ReadOnly) != 0) di.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly; }
            catch { }
        }
    }

}
