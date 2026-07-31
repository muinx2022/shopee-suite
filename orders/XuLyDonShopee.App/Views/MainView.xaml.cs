using System.Windows.Controls;

namespace XuLyDonShopee.App.Views;

/// <summary>Gốc của module Đơn hàng — vỏ chứa 3 màn con (Tài khoản / Đơn hàng / Thống kê), đổi theo
/// <c>MainViewModel.CurrentViewModel</c> qua DataTemplate khai trong XAML.</summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }
}
