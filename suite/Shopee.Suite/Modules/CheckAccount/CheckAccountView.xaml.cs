using System.Windows.Controls;

namespace Shopee.Suite.Modules.CheckAccount;

public partial class CheckAccountView : UserControl
{
    public CheckAccountView()
    {
        InitializeComponent();
    }

    // Tải lại lưới "TK OK" mỗi khi chuyển sang tab đó (account mới check xong sẽ xuất hiện).
    // Log giờ do behavior b:LogText.Source lo (nối dòng + cuộn), không cần xử lý ở code-behind nữa.
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Chỉ nhận sự kiện của chính TabControl: SelectionChanged là routed event BONG BÓNG ở WPF, nên
        // ComboBox "Số luồng" và DataGrid "TK OK" bên trong cũng đẩy sự kiện của chúng lên handler này.
        // OriginalSource = control đã phát sự kiện (UIElement.RaiseEvent gán) → so sánh tham chiếu là đủ.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedIndex == 1 && DataContext is CheckAccountViewModel vm)
            vm.LoadOkGrid();
    }
}
