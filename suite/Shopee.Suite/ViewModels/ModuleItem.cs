namespace Shopee.Suite.ViewModels;

/// <summary>Một mục trên sidebar: nhãn + icon + ViewModel của module tương ứng.</summary>
public sealed class ModuleItem(string title, string glyph, string subtitle, object viewModel)
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;        // ký tự Segoe MDL2 Assets
    public string Subtitle { get; } = subtitle;
    public object ViewModel { get; } = viewModel;
}
