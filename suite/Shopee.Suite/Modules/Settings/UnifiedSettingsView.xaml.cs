using System.Windows.Controls;

namespace Shopee.Suite.Modules.Settings;

/// <summary>Màn "Cài đặt" DUY NHẤT của app — 4 tab: Chế độ ứng dụng · Phiên bản &amp; cập nhật · Hiệu năng
/// &amp; Đồng bộ (gộp hiệu năng + đồng bộ nhiều máy) · Đơn hàng. Thuần binding, không code-behind.</summary>
public partial class UnifiedSettingsView : UserControl
{
    public UnifiedSettingsView() => InitializeComponent();
}
