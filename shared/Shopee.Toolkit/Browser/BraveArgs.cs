namespace Shopee.Toolkit.Browser;

/// <summary>
/// Dựng tham số dòng lệnh cho Brave/Chromium dùng CHUNG cho mọi nơi app phóng trình duyệt — cả phía suite
/// (Core <c>BrowserLauncher</c>, Search BraveManager, MultiBrave scrape BraveProfileManager, BigSeller runner
/// Update/Import) lẫn phía orders (cầu nối Đơn hàng, POC "mở sạch"). Gộp các cờ TRÙNG LẶP về một chỗ (khối
/// cửa sổ nền, giới hạn cache, proxy, extension, remote-debugging-port) trong khi vẫn cho từng call-site thêm
/// cờ RIÊNG của nó qua <see cref="Add(string)"/>/<see cref="AddRange"/>. Builder chỉ nối cờ theo ĐÚNG thứ tự
/// gọi → mỗi call-site gọi các phương thức theo đúng thứ tự cờ gốc thì kết quả GIỐNG HỆT bản cũ (refactor
/// không đổi hành vi). NGOẠI LỆ DUY NHẤT: phần <c>--disable-features</c> được gộp + bổ sung lúc dựng kết quả —
/// xem <see cref="NormalizeDisableFeatures"/>.
/// <para>
/// KHÁC BIỆT DUY NHẤT giữa hai phía là CÁCH GIAO args cho tiến trình, nên nó được tham số hoá bằng chế độ
/// khởi tạo chứ không phải hai lớp:
/// <list type="bullet">
/// <item><see cref="Create"/>/<see cref="Window"/> — chế độ CHUỖI: giá trị đường dẫn/URL được bọc ngoặc kép
/// vì kết quả <see cref="Build"/> đi vào <c>Process.Start(exe, argsString)</c> (phía suite).</item>
/// <item><see cref="CreateRaw"/>/<see cref="WindowRaw"/> — chế độ DANH SÁCH: KHÔNG bọc ngoặc vì kết quả
/// <see cref="BuildList"/> đi vào <c>ProcessStartInfo.ArgumentList</c> / <c>args</c> của Playwright, nơi mỗi
/// phần tử đã là một tham số riêng (bọc ngoặc ở đây sẽ lọt dấu " vào chính giá trị) (phía orders).</item>
/// </list>
/// </para>
/// </summary>
public sealed class BraveArgs
{
    private readonly List<string> _parts = new();
    private readonly bool _quotePaths;

    private BraveArgs(bool quotePaths) => _quotePaths = quotePaths;

    /// <summary>Bọc ngoặc kép giá trị đường dẫn/URL khi ở chế độ CHUỖI; giữ nguyên khi ở chế độ DANH SÁCH.</summary>
    private string Q(string value) => _quotePaths ? $"\"{value}\"" : value;

    /// <summary>Builder rỗng chế độ CHUỖI (không cờ nền) — dùng cho runner CDP tự lắp từng cờ theo thứ tự riêng.</summary>
    public static BraveArgs Create() => new(quotePaths: true);

    /// <summary>Builder rỗng chế độ DANH SÁCH (không cờ nền) — cho call-site truyền args dạng mảng.</summary>
    public static BraveArgs CreateRaw() => new(quotePaths: false);

    /// <summary>Khối cờ NỀN cho cửa sổ Brave "thường" (BrowserLauncher, Search, MultiBrave scrape), theo đúng
    /// thứ tự 6 cờ đầu của cả 3 nơi: user-data-dir → profile-directory=Default → new-window → no-first-run →
    /// no-default-browser-check → hide-crash-restore-bubble. KHÔNG kèm cache-limit (thêm qua <see cref="DiskCacheLimit"/>).</summary>
    public static BraveArgs Window(string userDataDir) => Create().WindowBlock(userDataDir);

    /// <summary>Như <see cref="Window"/> nhưng ở chế độ DANH SÁCH (không bọc ngoặc).</summary>
    public static BraveArgs WindowRaw(string userDataDir) => CreateRaw().WindowBlock(userDataDir);

    /// <summary>Nối khối 6 cờ nền cửa sổ vào builder ĐANG có (dùng khi call-site cần cờ khác đứng TRƯỚC khối này,
    /// vd cầu nối Đơn hàng đặt --remote-debugging-port lên đầu).</summary>
    public BraveArgs WindowBlock(string userDataDir)
    {
        _parts.Add($"--user-data-dir={Q(userDataDir)}");
        _parts.Add("--profile-directory=Default");
        _parts.Add("--new-window");
        _parts.Add("--no-first-run");
        _parts.Add("--no-default-browser-check");
        _parts.Add("--hide-crash-restore-bubble");
        return this;
    }

    public BraveArgs UserDataDir(string dir) { _parts.Add($"--user-data-dir={Q(dir)}"); return this; }
    public BraveArgs NoFirstRun() { _parts.Add("--no-first-run"); return this; }
    public BraveArgs NoDefaultBrowserCheck() { _parts.Add("--no-default-browser-check"); return this; }
    public BraveArgs RemoteDebuggingPort(int port) { _parts.Add($"--remote-debugging-port={port}"); return this; }
    public BraveArgs WindowSize(int width, int height) { _parts.Add($"--window-size={width},{height}"); return this; }
    public BraveArgs DisableGpu() { _parts.Add("--disable-gpu"); return this; }

    /// <summary>
    /// Cờ dòng lệnh chặn cache phình khi chạy — NGUỒN DUY NHẤT (<c>Shopee.Core.Browser.BraveCachePolicy.DiskLimitArgs</c>
    /// trỏ về đây). Thêm vào MỌI lệnh phóng Brave của app:
    ///  - disk-cache-size: trần 50 MB cho Default\Cache (mặc định Chromium tự cap ~320 MB/profile).
    ///  - media-cache-size: trần 32 MB cho cache media.
    ///  - disable-gpu-shader-disk-cache: bỏ GrShaderCache/ShaderCache trên đĩa.
    ///  - disable-component-update: chặn tải component (~75 MB/profile: Widevine, danh sách…) → không cần cho scrape.
    /// </summary>
    public static readonly IReadOnlyList<string> DiskCacheLimitFlags = new[]
    {
        "--disk-cache-size=52428800",
        "--media-cache-size=33554432",
        "--disable-gpu-shader-disk-cache",
        "--disable-component-update",
    };

    /// <summary>Nối các cờ giới hạn cache đĩa (<see cref="DiskCacheLimitFlags"/>) — bắt buộc cho mọi profile bền
    /// để cache không phình.</summary>
    public BraveArgs DiskCacheLimit() { _parts.AddRange(DiskCacheLimitFlags); return this; }

    /// <summary>Thêm <c>--proxy-server=…</c> nếu <paramref name="proxy"/> không rỗng (no-op nếu rỗng, khớp mọi caller).</summary>
    public BraveArgs ProxyServer(string? proxy)
    {
        if (!string.IsNullOrWhiteSpace(proxy))
            _parts.Add($"--proxy-server={proxy}");
        return this;
    }

    /// <summary>
    /// Feature Chromium phải TẮT ở MỌI hồ sơ do app tạo: chúng tải model AI on-device (Gemini Nano) về ngay gốc
    /// user-data-dir. Đo 07/08/2026 trên máy dev: <c>OptGuideOnDeviceModel\2025.8.8.1141\weights.bin</c> =
    /// <b>3,98 GB mỗi hồ sơ</b>, 2 hồ sơ đã ăn ~8 GB trong tổng 16 GB của 25 hồ sơ. App KHÔNG dùng tính năng AI
    /// nào của trình duyệt và hồ sơ là loại dùng-rồi-bỏ ⇒ rác thuần, còn tăng theo số hồ sơ.
    /// <para>Feature nào trình duyệt không có (vd Brave) thì Chromium bỏ qua, không lỗi.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> OnDeviceAiModelFeatures = new[]
    {
        "OptimizationGuideOnDeviceModel",
        "OptimizationGuideModelDownloading",
        "TextSafetyClassifier",
    };

    /// <summary>Tên thư mục model AI đã tải — nằm ngay GỐC user-data-dir (KHÔNG phải trong <c>Default</c>).
    /// Dùng chung cho các bước dọn hai phía (suite: BraveCachePolicy; orders: ProfileJanitor).</summary>
    public const string OnDeviceAiModelDirName = "OptGuideOnDeviceModel";

    private const string DisableFeaturesPrefix = "--disable-features=";

    /// <summary>
    /// Thêm cờ <c>--disable-features=…</c> với các feature RIÊNG của call-site. Nhận cả chuỗi đã ghép sẵn bằng
    /// dấu phẩy (<c>"A,B"</c>) lẫn nhiều tham số rời. Dùng thay cho <see cref="Add(string)"/> chép tay để bước
    /// gộp ở <see cref="Build"/>/<see cref="BuildList"/> nhìn thấy được.
    /// </summary>
    public BraveArgs DisableFeatures(params string[] features)
    {
        var list = SplitFeatures(features);
        if (list.Count > 0)
            _parts.Add(DisableFeaturesPrefix + string.Join(",", list));
        return this;
    }

    /// <summary>Thêm <c>--load-extension=…</c> (đường dẫn 1 extension hoặc chuỗi nhiều path ngăn bởi dấu phẩy).</summary>
    public BraveArgs LoadExtension(string path) { _parts.Add($"--load-extension={Q(path)}"); return this; }

    /// <summary>Thêm URL mở đầu (positional) — thường gọi cuối cùng.</summary>
    public BraveArgs StartUrl(string url) { _parts.Add(Q(url)); return this; }

    /// <summary>Thêm 1 cờ RIÊNG của call-site (không có phương thức chuyên biệt).</summary>
    public BraveArgs Add(string flag) { _parts.Add(flag); return this; }

    /// <summary>Thêm nhiều cờ RIÊNG của call-site.</summary>
    public BraveArgs AddRange(IEnumerable<string> flags) { _parts.AddRange(flags); return this; }

    /// <summary>Kết quả dạng CHUỖI (nối bằng dấu cách) cho <c>Process.Start(exe, argsString)</c>.
    /// Đã qua <see cref="NormalizeDisableFeatures"/>.</summary>
    public string Build() => string.Join(" ", NormalizeDisableFeatures(_parts));

    /// <summary>Kết quả dạng DANH SÁCH cho <c>ProcessStartInfo.ArgumentList</c> / <c>args</c> của Playwright.
    /// Đã qua <see cref="NormalizeDisableFeatures"/>. Trả bản MỚI (không phải view của builder) để caller
    /// không sửa ngược vào trong.</summary>
    public IReadOnlyList<string> BuildList() => NormalizeDisableFeatures(_parts);

    /// <summary>
    /// Chuẩn hoá phần <c>--disable-features</c> của một danh sách tham số — hàm THUẦN (không đụng builder, gọi
    /// bao nhiêu lần cũng ra cùng kết quả):
    /// <list type="number">
    /// <item>Gom MỌI cờ <c>--disable-features=</c> thành ĐÚNG MỘT cờ, đặt tại vị trí cờ đầu tiên; giữ nguyên thứ
    /// tự feature, khử trùng lặp.</item>
    /// <item>LUÔN nối thêm <see cref="OnDeviceAiModelFeatures"/> (chặn tải model AI ~4 GB/hồ sơ) — kể cả khi
    /// call-site không khai cờ nào thì cũng tự chèn một cờ mới.</item>
    /// <item>Cờ chèn mới đặt TRƯỚC tham số positional đầu tiên (URL của <see cref="StartUrl"/>, kể cả bản bọc
    /// ngoặc kép) để URL vẫn là tham số cuối như Chromium đòi.</item>
    /// </list>
    /// <para><b>VÌ SAO phải gộp, đừng bao giờ thêm cờ thứ hai:</b> Chromium giữ switch trong một map theo TÊN —
    /// có hai <c>--disable-features</c> thì chỉ một cái sống, cái kia mất trắng. Mất
    /// <c>DisableLoadExtensionCommandLineSwitch</c> = extension (cầu nối Đơn hàng / Search / scrape) ngừng nạp mà
    /// KHÔNG có thông báo lỗi nào.</para>
    /// <para>Đặt luật ở đây (chứ không sửa tay từng call-site) vì hai nơi phóng trình duyệt — BrowserLauncher và
    /// BigSellerBraveRunner — vốn KHÔNG có cờ <c>--disable-features</c> nào, và mọi call-site tương lai cũng
    /// được phủ mà không phải nhớ.</para>
    /// </summary>
    public static IReadOnlyList<string> NormalizeDisableFeatures(IReadOnlyList<string> parts)
    {
        var result = new List<string>(parts.Count + 1);
        var features = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // tên feature Chromium PHÂN BIỆT hoa/thường
        var slot = -1;

        foreach (var part in parts)
        {
            if (part is not null && part.StartsWith(DisableFeaturesPrefix, StringComparison.Ordinal))
            {
                foreach (var f in SplitFeatures(new[] { part[DisableFeaturesPrefix.Length..] }))
                {
                    if (seen.Add(f))
                        features.Add(f);
                }
                // Chỗ của cờ gộp = vị trí cờ ĐẦU TIÊN; các cờ sau bị bỏ (đã hút hết feature vào đây).
                if (slot < 0)
                {
                    slot = result.Count;
                    result.Add(string.Empty);   // giữ chỗ, điền lại ở cuối
                }
                continue;
            }
            result.Add(part!);
        }

        foreach (var f in OnDeviceAiModelFeatures)
        {
            if (seen.Add(f))
                features.Add(f);
        }

        var co = DisableFeaturesPrefix + string.Join(",", features);
        if (slot >= 0)
        {
            result[slot] = co;
        }
        else
        {
            // Chưa có cờ nào → chèn TRƯỚC positional đầu tiên (start URL), không thì thêm cuối.
            var idx = result.FindIndex(p => !string.IsNullOrEmpty(p) && !p.StartsWith('-'));
            result.Insert(idx >= 0 ? idx : result.Count, co);
        }

        return result.AsReadOnly();
    }

    /// <summary>Tách danh sách feature: nhận cả chuỗi ghép sẵn bằng dấu phẩy lẫn nhiều tham số rời;
    /// trim + bỏ phần tử rỗng. Giữ nguyên thứ tự, KHÔNG khử trùng lặp (việc đó ở nơi gộp).</summary>
    private static List<string> SplitFeatures(IEnumerable<string>? features)
    {
        var list = new List<string>();
        if (features is null)
            return list;

        foreach (var raw in features)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            foreach (var token in raw.Split(','))
            {
                var f = token.Trim();
                if (f.Length > 0)
                    list.Add(f);
            }
        }
        return list;
    }
}
