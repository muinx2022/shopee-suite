namespace Shopee.Core.Ai;

public enum AiProviderKind { OpenAI, Anthropic, Gemini }

/// <summary>
/// Cấu hình AI dùng chung cho toàn suite: chọn nhà cung cấp + model + API key cho từng nhà cung cấp.
/// Dùng bởi: cập nhật danh mục (Search), viết lại tên SP, viết lại mô tả (Update Product).
/// </summary>
public sealed class AiConfig
{
    /// <summary>"OpenAI" | "Anthropic" | "Gemini".</summary>
    public string Provider { get; set; } = "OpenAI";

    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKey { get; set; } = "";

    public string AnthropicModel { get; set; } = "claude-haiku-4-5";
    public string AnthropicApiKey { get; set; } = "";

    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public string GeminiApiKey { get; set; } = "";

    /// <summary>Số mục gửi mỗi batch (rewrite tên/mô tả, phân loại danh mục).</summary>
    public int BatchSize { get; set; } = 40;

    /// <summary>System prompt người dùng đặt cho VIẾT LẠI TÊN sản phẩm. Rỗng = dùng mặc định
    /// (<see cref="AiPrompts.DefaultNameRewrite"/>). Có thể chứa placeholder <c>{versionCount}</c>.</summary>
    public string NameRewritePrompt { get; set; } = "";

    /// <summary>System prompt người dùng đặt cho VIẾT LẠI MÔ TẢ sản phẩm. Rỗng = dùng mặc định
    /// (<see cref="AiPrompts.DefaultDescription"/>).</summary>
    public string DescriptionPrompt { get; set; } = "";

    /// <summary>Prompt mô tả thực dùng (đã áp mặc định nếu người dùng để trống).</summary>
    public string EffectiveDescriptionPrompt =>
        string.IsNullOrWhiteSpace(DescriptionPrompt) ? AiPrompts.DefaultDescription : DescriptionPrompt;

    /// <summary>Prompt viết-lại-tên thực dùng (đã áp mặc định nếu người dùng để trống).</summary>
    public string EffectiveNameRewritePrompt =>
        string.IsNullOrWhiteSpace(NameRewritePrompt) ? AiPrompts.DefaultNameRewrite : NameRewritePrompt;

    public AiProviderKind ProviderKind => (Provider ?? "").Trim().ToLowerInvariant() switch
    {
        "anthropic" or "claude" => AiProviderKind.Anthropic,
        "gemini" or "google" => AiProviderKind.Gemini,
        _ => AiProviderKind.OpenAI,
    };

    public string ActiveModel => ProviderKind switch
    {
        AiProviderKind.Anthropic => AnthropicModel,
        AiProviderKind.Gemini => GeminiModel,
        _ => OpenAiModel,
    };

    public string ActiveApiKey => ProviderKind switch
    {
        AiProviderKind.Anthropic => AnthropicApiKey,
        AiProviderKind.Gemini => GeminiApiKey,
        _ => OpenAiApiKey,
    };

    public bool HasActiveKey => !string.IsNullOrWhiteSpace(ActiveApiKey);

    public AiConfig Clone() => (AiConfig)MemberwiseClone();
}
