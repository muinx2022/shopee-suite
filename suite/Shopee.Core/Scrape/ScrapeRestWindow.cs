namespace Shopee.Core.Scrape;

/// <summary>
/// KHOẢNG NGHỈ giữa 2 link của một lượt scrape (trước đây là 2 hằng <c>MinRestMs</c>/<c>MaxRestMs</c> cứng
/// 120–240s trong <c>LauncherRunnerLoop</c>). Đây là chỗ DUY NHẤT giữ con số mặc định + luật hợp lệ hoá, để
/// client (cấu hình máy) và Hub (tham số lượt giao) không lệch nhau.
///
/// <para>ĐƠN VỊ: cấu hình + UI + giao thức Hub đều tính bằng GIÂY; engine chờ bằng MILLI-giây. Quy đổi CHỈ ở
/// đây (<see cref="MinMs"/>/<see cref="MaxMs"/>) và tên thành viên luôn mang đơn vị — đừng nhân/chia 1000 rải
/// rác ở chỗ khác.</para>
///
/// <para>QUY ƯỚC 0 = DÙNG MẶC ĐỊNH (khớp khuôn tham số hub-run-params: 0 = dùng cấu hình client): giá trị
/// ≤ 0 ở BẤT KỲ tầng nào (JSON cũ thiếu field, Hub gửi 0, ô nhập để trống) đều rơi về
/// <see cref="DefaultMinSeconds"/>/<see cref="DefaultMaxSeconds"/> ⇒ hành vi mặc định KHÔNG đổi so với 2 hằng cũ.</para>
/// </summary>
public readonly record struct ScrapeRestWindow(int MinSeconds, int MaxSeconds)
{
    /// <summary>Mặc định 120s — ĐÚNG hằng <c>MinRestMs = 120_000</c> trước đây.</summary>
    public const int DefaultMinSeconds = 120;
    /// <summary>Mặc định 240s — ĐÚNG hằng <c>MaxRestMs = 240_000</c> trước đây.</summary>
    public const int DefaultMaxSeconds = 240;
    /// <summary>Trần cho phép (1 giờ) — chặn ô nhập gõ nhầm số khổng lồ làm job đứng cả ngày.</summary>
    public const int MaxAllowedSeconds = 3600;
    /// <summary>Sàn cho phép (1s) — 0 đã có nghĩa "dùng mặc định" nên giá trị nhỏ nhất đặt được là 1s.</summary>
    public const int MinAllowedSeconds = 1;

    /// <summary>Khoảng nghỉ mặc định (120–240s) — hành vi trước khi có cấu hình.</summary>
    public static ScrapeRestWindow Default => new(DefaultMinSeconds, DefaultMaxSeconds);

    /// <summary>Hợp lệ hoá 1 cặp (min, max) tính bằng GIÂY: ≤0 → mặc định; kẹp trong
    /// [<see cref="MinAllowedSeconds"/>, <see cref="MaxAllowedSeconds"/>]; max &lt; min → kéo max lên bằng min
    /// (giữ nguyên min mà người dùng vừa gõ). TOTAL: không ném với input bất kỳ.
    /// <para>Ca "CHỈ đặt max" (min bỏ trống, vd Hub gửi <c>0/60</c>): min mặc định 120 &gt; max 60 mà kéo max lên
    /// 120 thì người gõ 60 để NGHỈ NGẮN LẠI hoá ra nghỉ LÂU HƠN và mất luôn tính ngẫu nhiên (nghỉ cố định
    /// 120–120s) — mà ngẫu nhiên chính là thứ chống bot. Nên khi min bỏ trống thì HẠ min xuống theo max.</para>
    /// </summary>
    public static ScrapeRestWindow Resolve(int minSeconds, int maxSeconds)
    {
        var max = maxSeconds > 0 ? Math.Clamp(maxSeconds, MinAllowedSeconds, MaxAllowedSeconds) : DefaultMaxSeconds;
        var min = minSeconds > 0
            ? Math.Clamp(minSeconds, MinAllowedSeconds, MaxAllowedSeconds)
            : Math.Min(DefaultMinSeconds, max);   // min bỏ trống → bám theo max, không đẩy ngược max lên
        if (max < min) max = min;                 // cả hai đều do người dùng đặt mà ngược nhau → giữ min họ gõ
        return new ScrapeRestWindow(min, max);
    }

    /// <summary>Cận dưới tính bằng milli-giây (engine chờ theo ms).</summary>
    public int MinMs => MinSeconds * 1000;
    /// <summary>Cận trên tính bằng milli-giây (engine chờ theo ms).</summary>
    public int MaxMs => MaxSeconds * 1000;

    /// <summary>Chọn ngẫu nhiên một khoảng nghỉ trong [<see cref="MinMs"/>, <see cref="MaxMs"/>] (bao gồm cận trên).</summary>
    public int NextRestMs(Random random) => random.Next(MinMs, MaxMs + 1);

    /// <summary>Mô tả ngắn để ghi log đầu lượt ("120–240s").</summary>
    public override string ToString() => $"{MinSeconds}–{MaxSeconds}s";
}
