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
    // Parallel auto-run logs in concurrently (login write-back), so serialize disk writes.
    private readonly object _saveLock = new();

    public LauncherSettings Settings { get; private set; } = new();

    public AppSettingsService()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShopeeStatApp");
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
    }

    /// <summary>Đọc/ghi cấu hình ở đường dẫn chỉ định — chỉ dùng cho TEST (bản thường luôn nằm trong
    /// %AppData%\ShopeeStatApp\settings.json, test mà đụng vào là xoá cấu hình thật của user).</summary>
    internal AppSettingsService(string settingsPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _settingsPath = settingsPath;
    }

    /// <summary>Nạp <c>settings.json</c> vào <see cref="Settings"/>. PHẢI gọi ngay sau khi tạo service:
    /// <see cref="SaveSettings"/> ghi đè NGUYÊN file bằng <see cref="Settings"/> đang có, nên bỏ Load là
    /// mất cấu hình của user ngay lần ghi đầu tiên.</summary>
    /// <summary>File tồn tại nhưng ĐỌC/PARSE hỏng (đang bị ghi dở bởi instance khác, AV khoá, JSON nát) →
    /// Settings đành về mặc định để chạy tiếp, nhưng <see cref="SaveSettings"/> bị CẤM: ghi lúc này là đè
    /// object mặc định lên settings.json của user — đúng cái lỗi "mất sạch cấu hình" vừa vá. Phơi ra ngoài
    /// (<see cref="LoadFailed"/>) để caller cảnh báo user rằng cấu hình phiên này KHÔNG được lưu.</summary>
    public bool LoadFailed { get; private set; }

    public void Load()
    {
        if (!File.Exists(_settingsPath)) return;   // chưa từng có file → mặc định là ĐÚNG, ghi được
        try
        {
            Settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_settingsPath), Opts) ?? new();
            LoadFailed = false;
        }
        catch { Settings = new(); LoadFailed = true; }
    }

    public void SaveSettings()
    {
        lock (_saveLock)
        {
            if (LoadFailed) return;   // xem LoadFailed — thà mất lần ghi này còn hơn mất file của user
            // tmp + move như ExcelExporter: WriteAllText ghi thẳng = file rỗng vài ms giữa chừng, instance
            // khác Load() trúng lúc đó là dính _loadFailed oan (hoặc tệ hơn, đọc được file cụt).
            var tmp = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Opts), Encoding.UTF8);
                File.Move(tmp, _settingsPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

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
            if (attempt == 0 || attempt == 3)
            {
                // Giết brave + crashpad mồ côi giữ ĐÚNG profile này; nếu có process bị giết, chờ 400ms cho
                // khoá profile (delete-pending) buông trước khi thử lại CreateDirectory.
                try
                {
                    Shopee.Core.Browser.BraveTeardown.Reap(
                        dir, includeCrashpadOrphans: true, sleepAfterReapMs: 400);
                }
                catch { }
            }
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
