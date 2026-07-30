using System.Windows;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Data;

/// <summary>Cửa sổ modal thêm/sửa 1 dòng dữ liệu. Lưu (validate + gọi Hub) do <see cref="RowEditViewModel"/> lo;
/// thành công → DialogResult=true để VM cha đọc kết quả; lỗi → giữ window mở, hiện lỗi trong form.</summary>
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

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (await _vm.SaveAsync()) CloseWith(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CloseWith(false);

    /// <summary>Đóng kèm kết quả. <c>DialogResult</c> chỉ gán được khi mở bằng <c>ShowDialog()</c>; đường
    /// fallback <c>Show()</c> của <c>WindowHost</c> (chưa có cửa sổ chính) thì đóng thẳng.</summary>
    private void CloseWith(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }

    /// <summary>Hộp xác nhận "SKU trùng" — owner là CHÍNH modal này để không bị khuất sau form.</summary>
    private Task<bool> ShowConfirmAsync(string text)
    {
        var dlg = new MessageDialog(text, "SKU trùng", confirm: true, DialogIcon.Question) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.Result);
    }
}
