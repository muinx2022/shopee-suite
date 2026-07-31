using System.Windows;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Máy màn to mở y như cũ (1500×940). Máy màn nhỏ hơn (1440×900 @100% → WorkingArea ~1440×860) thì
        // kẹp lại cho gọn trong vùng làm việc + canh giữa, RỒI maximize để dùng hết chỗ — kẹp trước nên bấm
        // nút khôi phục vẫn ra cửa sổ vừa màn, không bật lại 1500×940.
        this.FitOnOpen(maximizeIfTooSmall: true);
    }
}
