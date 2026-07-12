using Avalonia.Controls;
using Avalonia.Interactivity;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Data;

/// <summary>Cửa sổ modal thêm/sửa 1 dòng dữ liệu. Lưu (validate + gọi Hub) do <see cref="RowEditViewModel"/> lo;
/// thành công → Close(true) để VM cha đọc kết quả; lỗi → giữ window mở, hiện lỗi trong form.</summary>
public partial class RowEditWindow : Window
{
    private readonly RowEditViewModel _vm = null!;   // ctor rỗng chỉ cho designer; runtime luôn qua ctor(vm).

    public RowEditWindow() => InitializeComponent();

    public RowEditWindow(RowEditViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        vm.ConfirmOwner = ShowConfirmAsync;   // hộp "SKU trùng" bám chính modal này
        DataContext = vm;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (await _vm.SaveAsync()) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private Task<bool> ShowConfirmAsync(string text) =>
        new MessageDialog(text, "SKU trùng", confirm: true, DialogIcon.Question).ShowDialog<bool>(this);
}
