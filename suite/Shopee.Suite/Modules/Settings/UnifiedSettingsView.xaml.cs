using System.Windows.Controls;

namespace Shopee.Suite.Modules.Settings;

/// <summary>Màn "Cài đặt" DUY NHẤT của app — 4 tab: Chế độ ứng dụng · Phiên bản &amp; cập nhật · Hiệu năng
/// &amp; Đồng bộ (gộp hiệu năng + đồng bộ nhiều máy) · Đơn hàng. Gần như thuần binding; code-behind chỉ có
/// đúng một việc: hạ cờ chọn tab khi rời màn (xem ctor).</summary>
public partial class UnifiedSettingsView : UserControl
{
    public UnifiedSettingsView()
    {
        InitializeComponent();

        // Cờ ChonTabPhienBan sống trên SettingsViewModel SINGLETON, còn VIEW này bị DỰNG LẠI mỗi lần quay về
        // màn Cài đặt (shell bind ContentControl vào ViewModel + DataTemplate, không giữ instance view).
        // Không hạ cờ khi rời màn thì sau MỘT lần bấm "Kiểm tra cập nhật", mọi lần mở Cài đặt sau đó đều nhảy
        // thẳng vào tab "Phiên bản & cập nhật" thay vì tab đầu — hành vi người dùng không hề yêu cầu.
        // Hạ ở Unloaded (không phải trong lệnh): lúc còn ở trên màn mà gán false thì binding TwoWay đẩy ngược
        // IsSelected = false ⇒ TabControl rơi về SelectedIndex = -1, không tab nào được chọn.
        Unloaded += (_, _) =>
        {
            if (DataContext is UnifiedSettingsViewModel vm)
            {
                vm.Suite.ChonTabPhienBan = false;
            }
        };
    }
}
