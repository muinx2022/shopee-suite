using CommunityToolkit.Mvvm.ComponentModel;

namespace Shopee.Suite.ViewModels;

/// <summary>Placeholder cho các module chưa được di chuyển sang shell (v31, Update Product, Stat).</summary>
public sealed partial class ComingSoonViewModel : ObservableObject
{
    public ComingSoonViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _description;
}
