namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Sinh <b>lịch delay gõ phím kiểu người</b>: khoảng nghỉ (ms) trước mỗi ký tự kế tiếp. Đa số nhịp
/// nhanh (~80–220ms), thỉnh thoảng "ngập ngừng" lâu hơn (~350–800ms) như người thật đắn đo. Hàm thuần
/// (nhận <see cref="Random"/> để test tất định).
/// </summary>
public static class HumanTyping
{
    /// <summary>Xác suất "ngập ngừng" (delay dài) trước một ký tự.</summary>
    private const double HesitationChance = 0.12;

    /// <summary>
    /// Trả về delay (ms) nên chờ trước khi gõ ký tự kế tiếp: thường ~80–220ms; ~12% cơ hội "ngập ngừng"
    /// 350–800ms. Luôn nằm trong [80, 800].
    /// </summary>
    public static int NextCharDelayMs(Random rng)
    {
        // ~12% ngập ngừng lâu hơn (người thật đôi lúc khựng lại).
        if (rng.NextDouble() < HesitationChance)
        {
            return rng.Next(350, 801); // 350–800ms
        }

        return rng.Next(80, 221); // 80–220ms
    }
}
