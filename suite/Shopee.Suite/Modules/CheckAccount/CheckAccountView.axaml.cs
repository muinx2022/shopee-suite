using Avalonia.Controls;

namespace Shopee.Suite.Modules.CheckAccount;

public partial class CheckAccountView : UserControl
{
    public CheckAccountView()
    {
        InitializeComponent();
    }

    // Tải lại lưới "TK OK" mỗi khi chuyển sang tab đó (account mới check xong sẽ xuất hiện).
    // Log giờ do behavior b:LogText.Source lo (nối dòng + cuộn), không cần xử lý ở code-behind nữa.
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Chỉ nhận sự kiện của chính TabControl (ComboBox "Số luồng"/DataGrid bên trong cũng phát
        // SelectionChanged và bong bóng lên đây).
        if (!ReferenceEquals(e.Source, Tabs)) return;
        if (Tabs.SelectedIndex == 1 && DataContext is CheckAccountViewModel vm)
            vm.LoadOkGrid();
    }
}