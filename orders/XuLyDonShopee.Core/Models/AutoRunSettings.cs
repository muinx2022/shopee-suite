using System.Globalization;

namespace XuLyDonShopee.Core.Models;

/// <summary>
/// Cấu hình bộ "Chạy tự động theo lô": <see cref="BatchSize"/> tài khoản mỗi lô (mở song song),
/// nghỉ <see cref="GapMinutes"/> phút giữa các lô, có bật Sync đơn hàng / Xử lý đơn hay không.
/// Kiểm tra đơn (đọc số "Chờ Lấy Hàng") LUÔN chạy nên KHÔNG có cờ riêng ở đây.
/// <para>
/// Bất biến (record) + các hàm thuần <see cref="Parse"/> / <see cref="Normalize"/> để đọc/ghi từ bảng
/// <c>settings</c> và test được không cần DB. Trạng thái "đang chạy" là runtime — KHÔNG lưu ở đây.
/// </para>
/// </summary>
public sealed record AutoRunSettings(int BatchSize, int GapMinutes, bool DoSync, bool DoProcess)
{
    /// <summary>Số tài khoản mỗi lô mặc định (yêu cầu người dùng 17/7).</summary>
    public const int DefaultBatchSize = 10;

    /// <summary>Số tài khoản mỗi lô tối thiểu (không cho ≤ 0 kẻo lô rỗng / chia vô hạn).</summary>
    public const int MinBatchSize = 1;

    /// <summary>Số tài khoản mỗi lô tối đa (mở &gt; 20 Brave song song sẽ treo máy).</summary>
    public const int MaxBatchSize = 20;

    /// <summary>Số phút nghỉ giữa các lô mặc định.</summary>
    public const int DefaultGapMinutes = 15;

    /// <summary>Số phút nghỉ tối thiểu.</summary>
    public const int MinGapMinutes = 1;

    /// <summary>Số phút nghỉ tối đa (1 ngày). Chặn <c>Task.Delay(TimeSpan.FromMinutes(khổng-lồ))</c> vượt
    /// <see cref="int.MaxValue"/> ms → ném ArgumentOutOfRange bị catch-all nuốt làm vòng tự kết thúc âm thầm.</summary>
    public const int MaxGapMinutes = 1440;

    /// <summary>Cấu hình mặc định (N=10, M=15, tắt Sync, tắt Xử lý đơn) — Xử lý đơn GHI lên Shopee nên mặc định TẮT.</summary>
    public static AutoRunSettings Default => new(DefaultBatchSize, DefaultGapMinutes, false, false);

    /// <summary>
    /// Chuẩn hóa: kẹp <see cref="BatchSize"/> vào [<see cref="MinBatchSize"/>, <see cref="MaxBatchSize"/>] và
    /// <see cref="GapMinutes"/> vào [<see cref="MinGapMinutes"/>, <see cref="MaxGapMinutes"/>] (số người dùng
    /// nhập / DB hỏng có thể ≤ 0 hoặc khổng lồ). Cờ bool giữ nguyên.
    /// </summary>
    public static AutoRunSettings Normalize(int batchSize, int gapMinutes, bool doSync, bool doProcess)
        => new(
            Clamp(batchSize, MinBatchSize, MaxBatchSize),
            Clamp(gapMinutes, MinGapMinutes, MaxGapMinutes),
            doSync,
            doProcess);

    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    /// <summary>
    /// Dựng từ các chuỗi lưu trong bảng <c>settings</c> (null / rỗng / hỏng → mặc định của từng trường) rồi
    /// <see cref="Normalize"/>. Không ném — mọi giá trị lạ đều rơi về mặc định an toàn.
    /// </summary>
    public static AutoRunSettings Parse(string? batchSize, string? gapMinutes, string? doSync, string? doProcess)
        => Normalize(
            ParseInt(batchSize, DefaultBatchSize),
            ParseInt(gapMinutes, DefaultGapMinutes),
            ParseBool(doSync),
            ParseBool(doProcess));

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Đọc bool bền: nhận "true"/"false" (bất kể hoa/thường) và "1"/"0"; null/rỗng/lạ → false.</summary>
    private static bool ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var t = s.Trim();
        if (bool.TryParse(t, out var b))
        {
            return b;
        }

        return t == "1";
    }

    /// <summary>Chuỗi lưu DB cho một cờ bool ("true"/"false").</summary>
    public static string BoolToStorage(bool value) => value ? "true" : "false";

    /// <summary>Chuỗi lưu DB cho một số nguyên (invariant culture).</summary>
    public static string IntToStorage(int value) => value.ToString(CultureInfo.InvariantCulture);
}
