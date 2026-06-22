namespace ShopeeStatApp.Models;

/// <summary>Cấu hình AI cho chức năng phân loại danh mục. Lưu model + API key riêng cho từng nhà cung cấp,
/// <see cref="Provider"/> quyết định nhà cung cấp đang dùng.</summary>
public sealed class AiSettings
{
    /// <summary>Nhà cung cấp đang dùng: "OpenAI" | "Claude" | "Gemini".</summary>
    public string Provider { get; set; } = "OpenAI";

    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKey { get; set; } = "";

    public string ClaudeModel { get; set; } = "claude-haiku-4-5";
    public string ClaudeApiKey { get; set; } = "";

    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public string GeminiApiKey { get; set; } = "";
}
