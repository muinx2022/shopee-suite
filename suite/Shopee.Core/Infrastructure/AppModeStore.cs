namespace Shopee.Core.Infrastructure;

/// <summary>
/// Chế độ ứng dụng — quyết định nhóm module nào được DỰNG + HIỆN cho gọn. Vẫn MỘT bản build duy nhất
/// (Full); mỗi máy chọn chế độ tuỳ nhu cầu. Đổi chế độ = khởi động lại app (không hot-swap).
/// </summary>
public enum AppMode
{
    /// <summary>Tất cả: Workspace + Cấu hình BigSeller + Shopee (đơn hàng) + Cài đặt.</summary>
    Full,

    /// <summary>Workspace + Cấu hình BigSeller + Cài đặt (ẩn Shopee/đơn hàng).</summary>
    Workspace,

    /// <summary>Chỉ Shopee (đơn hàng) + Cài đặt (ẩn Workspace + Cấu hình BigSeller).</summary>
    Shopee,
}

/// <summary>
/// Kho cấu hình "Chế độ ứng dụng", lưu tại <c>%AppData%\ShopeeSuite\app-mode.json</c> qua
/// <see cref="SuitePaths.RootFile"/> — NGOÀI thư mục bản cài Velopack ⇒ cập nhật KHÔNG xoá. Thuần I/O +
/// parse, KHÔNG phụ thuộc Avalonia để dùng chung ở cả <c>App.axaml</c> (gate init engine) lẫn
/// <c>ShellViewModel</c> (gate dựng tab). Thiếu/hỏng/không hợp lệ → <see cref="AppMode.Full"/> (an toàn).
/// Đọc 1 lần khi khởi tạo; đổi chế độ đi qua <see cref="Save"/> + restart nên Current không đổi giữa vòng đời.
/// </summary>
public sealed class AppModeStore
{
    private static readonly Lazy<AppModeStore> _shared = new(() => new AppModeStore());
    public static AppModeStore Shared => _shared.Value;

    private static readonly string FilePath = SuitePaths.RootFile("app-mode.json");
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly object _lock = new();

    /// <summary>Chế độ hiện hành (mặc định Full khi chưa cấu hình / file hỏng). Tham số dòng lệnh
    /// <c>--mode &lt;X&gt;</c> (nếu có) THẮNG file — để mỗi shortcut khoá cứng một chế độ, chạy song song
    /// trên cùng bản cài + dùng chung dữ liệu.</summary>
    public AppMode Current { get; private set; } = AppMode.Full;

    /// <summary>true khi <see cref="Current"/> đến từ <c>--mode</c> ở dòng lệnh (shortcut khoá chế độ). UI dùng
    /// để báo "khoá bởi shortcut" + ẩn nút đổi-mode-rồi-restart (restart giữ <c>--mode</c> nên đổi ở đây vô
    /// nghĩa). <see cref="Save"/> vẫn ghi file được (áp cho lần chạy KHÔNG kèm <c>--mode</c>).</summary>
    public bool ModeLockedByArg { get; }

    private AppModeStore()
    {
        Load();
        // --mode <X> / --mode=<X> THẮNG app-mode.json (mỗi shortcut một chế độ). Không có/không hợp lệ → giữ file.
        var arg = ModeArg();
        if (arg is { } m) Current = m;
        ModeLockedByArg = arg is not null;
    }

    /// <summary>Parser thuần: tìm <c>--mode &lt;X&gt;</c> hoặc <c>--mode=&lt;X&gt;</c> trong <paramref name="args"/>,
    /// trả về <see cref="AppMode"/> khớp (không phân biệt hoa/thường; <see cref="Enum.IsDefined"/> loại số ngoài
    /// dải). Không tìm thấy / không hợp lệ → null. Tách riêng để test được không cần môi trường tiến trình.</summary>
    internal static AppMode? ParseModeArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? val = null;
            if (a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
                val = a["--mode=".Length..];
            else if (a.Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                val = args[i + 1];
            if (val is null) continue;
            if (Enum.TryParse<AppMode>(val, ignoreCase: true, out var m) && Enum.IsDefined(m))
                return m;
        }
        return null;
    }

    /// <summary>Chế độ khoá bởi <c>--mode</c> của tiến trình hiện tại (null nếu không có / không hợp lệ / lỗi đọc args).</summary>
    private static AppMode? ModeArg()
    {
        try { return ParseModeArg(Environment.GetCommandLineArgs()); }
        catch { return null; }
    }

    /// <summary>DTO tối giản: <c>{ "mode": "Full" }</c>.</summary>
    private sealed class Dto
    {
        [JsonPropertyName("mode")] public string? Mode { get; set; }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath, Encoding.UTF8), ReadOpts);
                    // IsDefined loại số ngoài dải (vd "5") mà TryParse vẫn nhận → tránh trạng thái không tab.
                    Current = Enum.TryParse<AppMode>(dto?.Mode, ignoreCase: true, out var m) && Enum.IsDefined(m)
                        ? m : AppMode.Full;
                }
                else Current = AppMode.Full;
            }
            catch { Current = AppMode.Full; }
        }
    }

    /// <summary>Ghi chế độ mới (nguyên tử: file tạm → move) + cập nhật <see cref="Current"/>. Lưu ý: khi tiến
    /// trình chạy kèm <c>--mode</c> (<see cref="ModeLockedByArg"/>), file này chỉ áp cho lần chạy KHÔNG có
    /// <c>--mode</c> — shortcut khoá chế độ vẫn thắng ở lần chạy sau.</summary>
    public void Save(AppMode mode)
    {
        lock (_lock)
        {
            Current = mode;
            try
            {
                Directory.CreateDirectory(SuitePaths.Root);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Dto { Mode = mode.ToString() }, WriteOpts), Encoding.UTF8);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }

    /// <summary>Chế độ này có hiện nhóm "Workspace" (Workspace + Cấu hình BigSeller) không.</summary>
    public static bool ShowsWorkspace(AppMode m) => m is AppMode.Full or AppMode.Workspace;

    /// <summary>Chế độ này có hiện module Shopee (đơn hàng) không.</summary>
    public static bool ShowsShopee(AppMode m) => m is AppMode.Full or AppMode.Shopee;
}
