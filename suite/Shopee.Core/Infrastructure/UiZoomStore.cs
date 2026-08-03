namespace Shopee.Core.Infrastructure;

/// <summary>
/// Kho mức THU PHÓNG giao diện (zoom), lưu tại <c>%AppData%\ShopeeSuite\ui-zoom.json</c> qua
/// <see cref="SuitePaths.RootFile"/> — NGOÀI thư mục bản cài Velopack ⇒ cập nhật app KHÔNG xoá.
/// Thuần I/O + số học, KHÔNG phụ thuộc WPF để test được và để tầng UI (<c>UiZoom</c>) chỉ lo việc vẽ.
/// Thiếu file / file hỏng / số vô lý → <see cref="Default"/> = 100%.
/// </summary>
public sealed class UiZoomStore
{
    /// <summary>Mức mặc định (100%).</summary>
    public const double Default = 1.0;

    /// <summary>
    /// Các NẤC thu phóng theo kiểu trình duyệt (tăng/giảm nhảy nấc, không trượt liên tục). Phải sắp TĂNG dần
    /// và phải chứa <see cref="Default"/> — <see cref="Next"/> dựa vào cả hai điều đó.
    /// </summary>
    public static readonly double[] Steps = { 0.75, 0.85, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0 };

    public static double MinZoom => Steps[0];
    public static double MaxZoom => Steps[^1];

    private static readonly Lazy<UiZoomStore> _shared = new(() => new UiZoomStore());
    public static UiZoomStore Shared => _shared.Value;

    private static readonly string FilePath = SuitePaths.RootFile("ui-zoom.json");
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly object _lock = new();

    /// <summary>Mức thu phóng hiện hành (đã kẹp vào dải nấc). Đổi mức đi qua <see cref="Save"/>.</summary>
    public double Current { get; private set; } = Default;

    private UiZoomStore() => Load();

    /// <summary>DTO tối giản: <c>{ "zoom": 1.15 }</c>.</summary>
    private sealed class Dto
    {
        [JsonPropertyName("zoom")] public double? Zoom { get; set; }
    }

    public void Load()
    {
        lock (_lock)
        {
            // Thiếu file / file hỏng → TryLoad trả null → rơi về 100% (an toàn, y khuôn AppModeStore).
            var dto = JsonAtomicFile.TryLoad<Dto>(FilePath, ReadOpts);
            Current = Sanitize(dto?.Zoom);
        }
    }

    /// <summary>Ghi mức mới (nguyên tử: file tạm → move) + cập nhật <see cref="Current"/>. Giá trị được kẹp
    /// về dải hợp lệ trước khi ghi nên file trên đĩa luôn dùng được.</summary>
    public void Save(double zoom)
    {
        lock (_lock)
        {
            Current = Sanitize(zoom);
            JsonAtomicFile.Save(FilePath, new Dto { Zoom = Current }, WriteOpts);
        }
    }

    /// <summary>Kẹp một giá trị bất kỳ về dải hợp lệ: null/NaN/vô cực → 100%; ngoài dải → biên gần nhất.
    /// KHÔNG ép về đúng nấc — mức đến từ file cũ / cấu hình tay vẫn dùng được miễn nằm trong dải.</summary>
    public static double Sanitize(double? zoom)
    {
        if (zoom is not { } z || double.IsNaN(z) || double.IsInfinity(z)) return Default;
        return Math.Clamp(z, MinZoom, MaxZoom);
    }

    /// <summary>
    /// Nấc kế tiếp theo hướng <paramref name="direction"/> (+1 phóng to, −1 thu nhỏ) tính từ
    /// <paramref name="current"/>. Đang ở giữa hai nấc (mức lẻ từ file cũ) → nhảy sang nấc gần nhất theo
    /// hướng đó. Hết dải → giữ nguyên biên (không vòng lại).
    /// </summary>
    public static double Next(double current, int direction)
    {
        var cur = Sanitize(current);
        if (direction > 0)
        {
            foreach (var s in Steps)
                if (s > cur + 1e-6) return s;
            return MaxZoom;
        }
        if (direction < 0)
        {
            for (int i = Steps.Length - 1; i >= 0; i--)
                if (Steps[i] < cur - 1e-6) return Steps[i];
            return MinZoom;
        }
        return cur;
    }

    /// <summary>Nhãn phần trăm cho UI: 1.15 → "115%". Làm tròn nên mức lẻ vẫn đọc được.</summary>
    public static string Percent(double zoom) => $"{Math.Round(Sanitize(zoom) * 100)}%";
}
