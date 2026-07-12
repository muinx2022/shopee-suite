using Avalonia.Controls;

namespace Shopee.Suite.Modules.Data;

public partial class DataView : UserControl
{
    public DataView()
    {
        InitializeComponent();
        // Nạp option + trang 1 khi View hiển thị lần đầu (ctor VM không I/O).
        Loaded += (_, _) => (DataContext as DataViewModel)?.EnsureLoaded();
    }
}
