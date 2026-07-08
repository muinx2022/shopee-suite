using Avalonia.Media;

namespace Shopee.Suite.ViewModels;

/// <summary>Một mục điều hướng: nhãn + icon vector đơn sắc + ViewModel của module tương ứng.</summary>
public sealed class ModuleItem(string title, string iconData, string subtitle, object viewModel, string? navTitle = null)
{
    public string Title { get; } = title;

    /// <summary>Icon vector (path 24×24) — render qua PathIcon, tô theo Foreground nên tự đổi màu theo
    /// trạng thái tab. Xem <see cref="Infrastructure.AppIcons"/>.</summary>
    public Geometry Icon { get; } = StreamGeometry.Parse(iconData);

    public string Subtitle { get; } = subtitle;
    public object ViewModel { get; } = viewModel;

    /// <summary>Nhãn ngắn dùng cho tab trên top bar (mặc định = Title nếu không đặt riêng).</summary>
    public string NavTitle { get; } = navTitle ?? title;
}
