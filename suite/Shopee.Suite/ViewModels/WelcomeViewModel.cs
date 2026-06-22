using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Shopee.Suite.ViewModels;

/// <summary>Màn hình chào mặc định (không chọn module nào). Hiển thị các mục dưới dạng thẻ bấm
/// được để điều hướng nhanh.</summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public ObservableCollection<ModuleItem> Tiles { get; }

    public WelcomeViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Tiles = shell.Modules;
    }

    [RelayCommand]
    private void Open(ModuleItem? item)
    {
        if (item is not null) _shell.Selected = item;
    }
}
