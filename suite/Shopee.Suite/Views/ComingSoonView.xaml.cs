using System.Windows.Controls;
using Shopee.Suite.ViewModels;

namespace Shopee.Suite.Views;

/// <summary>
/// Màn "Sắp có" — dùng cho 2 việc:
/// <list type="bullet">
///   <item><see cref="ComingSoonViewModel"/>: màn thật chưa làm (tiêu đề + mô tả lấy từ VM);</item>
///   <item>PLACEHOLDER tạm của các màn module đang chờ dựng lại bằng WPF (đợt 2–5): tiêu đề suy ra từ
///         tên kiểu ViewModel.</item>
/// </list>
/// Chữ điền bằng code (không bind) vì DataContext ở ca thứ hai là ViewModel module — bind Title/Description
/// vào đó sẽ đổ hàng loạt "System.Windows.Data Error" ra Output.
/// </summary>
public partial class ComingSoonView : UserControl
{
    public ComingSoonView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Apply();
        Apply();
    }

    private void Apply()
    {
        if (DataContext is ComingSoonViewModel vm)
        {
            TitleText.Text = vm.Title;
            DescriptionText.Text = vm.Description;
            BadgeText.Text = "Sắp có";
            return;
        }

        TitleText.Text = ScreenName(DataContext);
        BadgeText.Text = "Đang port";
        DescriptionText.Text = "Màn này đang được port sang bản Windows (WPF). Phần lõi xử lý vẫn chạy bình " +
                               "thường ở nền — chỉ giao diện của màn là chưa dựng lại.";
    }

    /// <summary>"WorkspaceViewModel" → "Workspace"; DataContext null → nhãn chung.</summary>
    private static string ScreenName(object? dataContext)
    {
        if (dataContext is null) return "Màn hình";
        var name = dataContext.GetType().Name;
        return name.EndsWith("ViewModel", StringComparison.Ordinal)
            ? name[..^"ViewModel".Length]
            : name;
    }
}
