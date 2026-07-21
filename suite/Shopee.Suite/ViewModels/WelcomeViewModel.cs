using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Shopee.Suite.ViewModels;

/// <summary>
/// Màn hình chào cũ (DORMANT sau khi chuyển sang Ribbon). Không còn nằm trong luồng điều hướng — GIỮ lại
/// file + DataTemplate để dọn ở phase sau, nhưng không tham chiếu <see cref="ShellViewModel"/>.Modules/Selected
/// nữa (đã thay bằng Tabs). Chưa được khởi tạo ở đâu.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    public ObservableCollection<ModuleItem> Tiles { get; } = new();

    public WelcomeViewModel()
    {
    }

    /// <summary>Dormant — không còn điều hướng (giữ command để view cũ vẫn bind được).</summary>
    [RelayCommand]
    private void Open(ModuleItem? item)
    {
    }
}
