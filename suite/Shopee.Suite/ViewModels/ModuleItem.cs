namespace Shopee.Suite.ViewModels;

/// <summary>Một mục điều hướng: nhãn + icon + ViewModel của module tương ứng.</summary>
public sealed class ModuleItem(string title, string glyph, string subtitle, object viewModel, string? navTitle = null)
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;        // emoji icon
    public string Subtitle { get; } = subtitle;
    public object ViewModel { get; } = viewModel;

    /// <summary>Nhãn ngắn dùng cho tab trên top bar (mặc định = Title nếu không đặt riêng).</summary>
    public string NavTitle { get; } = navTitle ?? title;
}
