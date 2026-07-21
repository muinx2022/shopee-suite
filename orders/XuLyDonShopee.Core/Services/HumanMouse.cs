namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Sinh <b>đường cong di chuột kiểu người</b>: chuỗi điểm (x,y) đi từ điểm đầu tới điểm cuối theo
/// đường cong Bézier bậc 3 với 2 điểm điều khiển <b>lệch ngẫu nhiên vuông góc</b> (nên đường cong,
/// KHÔNG thẳng), tốc độ biến thiên (lấy mẫu tham số theo ease + jitter), kèm rung nhẹ (micro-tremor)
/// ở các điểm giữa. Hàm thuần (nhận <see cref="Random"/> để test tất định), không đụng trình duyệt —
/// tầng gọi tự <c>Mouse.MoveAsync</c> từng điểm để đi cong thật (KHÔNG dùng <c>steps</c> lớn đi thẳng).
/// </summary>
public static class HumanMouse
{
    /// <summary>
    /// Sinh chuỗi <paramref name="steps"/> điểm (x,y) đi từ (<paramref name="x0"/>,<paramref name="y0"/>)
    /// tới (<paramref name="x1"/>,<paramref name="y1"/>) theo đường cong Bézier bậc 3 với 2 điểm điều
    /// khiển lệch ngẫu nhiên vuông góc (đường cong, KHÔNG thẳng). Điểm đầu đúng bằng (x0,y0), điểm cuối
    /// <b>ép</b> đúng bằng (x1,y1). Nhận <see cref="Random"/> để test tất định.
    /// </summary>
    /// <remarks>
    /// - 2 điểm điều khiển P1,P2 nằm trên đoạn thẳng (tại t≈1/3, 2/3) + lệch vuông góc biên độ
    ///   ±(15–35%) khoảng cách, <b>cùng một phía</b> (một cung cong nhất quán → luôn không thẳng hàng).
    /// - Tham số t lấy mẫu theo ease smoothstep (chậm–nhanh–chậm → tốc độ biến thiên) + jitter nhỏ.
    /// - <paramref name="steps"/> ≤ 1 hoặc điểm đầu ≡ điểm cuối → trả về điểm cuối lặp lại (không NaN).
    /// </remarks>
    public static IReadOnlyList<(double X, double Y)> GeneratePath(
        double x0, double y0, double x1, double y1, int steps, Random rng)
    {
        // Số điểm tối thiểu hợp lệ: ít nhất 1. steps ≤ 1 → chỉ có điểm cuối.
        var count = Math.Max(steps, 1);
        var result = new List<(double X, double Y)>(count);

        var dx = x1 - x0;
        var dy = y1 - y0;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        // Suy biến: đường quá ngắn / trùng điểm / chỉ 1 điểm → trả điểm cuối lặp lại (không NaN).
        if (count < 2 || dist < 1e-9)
        {
            for (var i = 0; i < count; i++)
            {
                result.Add((x1, y1));
            }
            return result;
        }

        // Véc-tơ đơn vị vuông góc với đường thẳng đầu→cuối.
        var perpX = -dy / dist;
        var perpY = dx / dist;

        // Hai điểm điều khiển lệch CÙNG một phía (một cung cong) — đảm bảo đường luôn không thẳng.
        var side = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
        var amp1 = (0.15 + rng.NextDouble() * 0.20) * dist * side; // 15–35% khoảng cách
        var amp2 = (0.15 + rng.NextDouble() * 0.20) * dist * side;

        var p1x = x0 + dx / 3.0 + perpX * amp1;
        var p1y = y0 + dy / 3.0 + perpY * amp1;
        var p2x = x0 + dx * 2.0 / 3.0 + perpX * amp2;
        var p2y = y0 + dy * 2.0 / 3.0 + perpY * amp2;

        // Rung nhẹ (micro-tremor) tối đa vài px ở các điểm giữa cho tự nhiên hơn.
        var tremor = Math.Min(2.0, dist * 0.01);

        for (var i = 0; i < count; i++)
        {
            // Điểm đầu/cuối ép chính xác (điểm cuối phải đúng đích).
            if (i == 0)
            {
                result.Add((x0, y0));
                continue;
            }
            if (i == count - 1)
            {
                result.Add((x1, y1));
                continue;
            }

            var lin = (double)i / (count - 1);
            // Ease smoothstep → tốc độ biến thiên (chậm ở hai đầu, nhanh ở giữa).
            var t = lin * lin * (3.0 - 2.0 * lin);
            // Jitter nhỏ trên t (giữ trong (0,1)).
            t += (rng.NextDouble() - 0.5) * (0.5 / count);
            t = Math.Clamp(t, 0.0, 1.0);

            var mt = 1.0 - t;
            // Bézier bậc 3: B(t) = mt^3 P0 + 3 mt^2 t P1 + 3 mt t^2 P2 + t^3 P3.
            var b0 = mt * mt * mt;
            var b1 = 3.0 * mt * mt * t;
            var b2 = 3.0 * mt * t * t;
            var b3 = t * t * t;

            var x = b0 * x0 + b1 * p1x + b2 * p2x + b3 * x1;
            var y = b0 * y0 + b1 * p1y + b2 * p2y + b3 * y1;

            // Rung nhẹ ngẫu nhiên.
            x += (rng.NextDouble() - 0.5) * tremor;
            y += (rng.NextDouble() - 0.5) * tremor;

            result.Add((x, y));
        }

        return result;
    }
}
