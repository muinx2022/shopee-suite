using System.Windows.Controls;

namespace Shopee.Suite.Modules.Fleet;

/// <summary>Màn "Trạng thái &amp; Giao việc" — 4 tab: Theo dõi · Giao việc · Search (đa máy) · Log.
/// Toàn bộ hành vi nằm ở <see cref="FleetViewModel"/>; view không có code-behind nghiệp vụ.</summary>
public partial class FleetView : UserControl
{
    public FleetView() => InitializeComponent();
}
