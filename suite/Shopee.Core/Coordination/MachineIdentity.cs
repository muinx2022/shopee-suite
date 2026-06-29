using Shopee.Core.Infrastructure;

namespace Shopee.Core.Coordination;

/// <summary>Danh tính cục bộ của một máy chạy app (id ổn định + tên máy). KHÔNG đồng bộ.</summary>
public sealed class MachineInfo
{
    public string MachineId { get; set; } = "";
    public string Hostname { get; set; } = "";
    /// <summary>Tên hiển thị do người dùng đặt (vd "Máy của ABC"); trống = dùng <see cref="Hostname"/>.</summary>
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Danh tính máy này, lưu tại %AppData%\ShopeeSuite\machine.json (NGAY DƯỚI Root, KHÔNG dưới
/// shared\ và KHÔNG bao giờ đồng bộ — mỗi máy phải có id riêng để điều phối/khoá phân biệt được).
/// Sinh GUID 1 lần rồi giữ nguyên; tên máy làm tươi mỗi lần nạp. Singleton.
/// </summary>
public sealed class MachineIdentity
{
    private static readonly Lazy<MachineIdentity> _shared = new(() => new MachineIdentity());
    public static MachineIdentity Shared => _shared.Value;

    private static readonly string FilePath = SuitePaths.RootFile("machine.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private MachineInfo _info;

    private MachineIdentity() => _info = LoadOrCreate();

    public string MachineId { get { lock (_lock) return _info.MachineId; } }
    public string Hostname { get { lock (_lock) return _info.Hostname; } }
    public MachineInfo Current { get { lock (_lock) return Clone(_info); } }

    /// <summary>Tên hiển thị (người dùng đặt) — dùng để báo "Scraping by …", bảng Fleet. Trống → tên máy.</summary>
    public string DisplayName
    {
        get { lock (_lock) return string.IsNullOrWhiteSpace(_info.DisplayName) ? _info.Hostname : _info.DisplayName; }
    }

    /// <summary>Đặt tên hiển thị máy này (lưu vào machine.json). Trống = quay về tên máy.</summary>
    public void SetDisplayName(string name)
    {
        lock (_lock)
        {
            _info.DisplayName = (name ?? "").Trim();
            Save(_info);
        }
    }

    /// <summary>Nhãn: "Máy của ABC (a1b2c3)".</summary>
    public string Label { get { lock (_lock) return $"{DisplayName} ({Short(_info.MachineId)})"; } }

    private static MachineInfo LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<MachineInfo>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.MachineId))
                {
                    var host = Environment.MachineName;
                    if (!string.Equals(loaded.Hostname, host, StringComparison.Ordinal))
                    {
                        loaded.Hostname = host;
                        Save(loaded);
                    }
                    return loaded;
                }
            }
        }
        catch { }

        var created = new MachineInfo
        {
            MachineId = Guid.NewGuid().ToString("N"),
            Hostname = Environment.MachineName,
            CreatedAt = DateTimeOffset.Now,
        };
        Save(created);
        return created;
    }

    private static void Save(MachineInfo info)
    {
        try
        {
            Directory.CreateDirectory(SuitePaths.Root);
            var json = JsonSerializer.Serialize(info, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    private static MachineInfo Clone(MachineInfo m) =>
        new() { MachineId = m.MachineId, Hostname = m.Hostname, DisplayName = m.DisplayName, CreatedAt = m.CreatedAt };

    private static string Short(string id) => id.Length <= 6 ? id : id[..6];
}
