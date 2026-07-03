using Shopee.Core.Ai;

namespace Shopee.Core.BigSeller;

/// <summary>
/// Giải captcha login BigSeller (ảnh 4 ký tự méo màu, vd "4F58") bằng AI vision — dùng đúng provider/model/key
/// ở <see cref="AiConfig"/> (đã verify OpenAI gpt-4.1-mini đọc chuẩn). Trả về chuỗi ký tự IN HOA đã lọc còn
/// [A-Z0-9]; rỗng nếu lỗi/không đọc được. RETRY là việc của caller: submit sai → BigSeller đổi captcha mới →
/// gọi lại. Chi phí ~$0.0001/lần.
/// </summary>
public static class BigSellerCaptchaSolver
{
    private const string SystemPrompt = "You are a precise OCR assistant for reading login CAPTCHA codes.";
    private const string UserPrompt =
        "This image is a login CAPTCHA containing exactly 4 characters (uppercase A-Z letters and/or 0-9 digits), " +
        "distorted and colored, possibly crossed by a line. Reply with ONLY those 4 characters in uppercase — " +
        "no spaces, no punctuation, no explanation.";

    /// <summary>Đọc captcha từ ảnh PNG. Trả về 4 ký tự (đã lọc [A-Z0-9]); "" nếu lỗi.</summary>
    public static async Task<string> SolveAsync(AiConfig cfg, byte[] imagePng, CancellationToken ct = default)
    {
        if (imagePng is null || imagePng.Length == 0) return "";
        var raw = await AiChat.CompleteVisionAsync(cfg, SystemPrompt, UserPrompt, imagePng, ct).ConfigureAwait(false);
        return new string((raw ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
