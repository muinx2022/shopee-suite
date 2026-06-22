using System.Windows;

namespace Shopee.Suite;

/// <summary>
/// Hộp thoại căn giữa theo CỬA SỔ APP (owner). Dùng <see cref="MessageDialog"/> (Window WPF) thay cho
/// MessageBox native — native căn owner SAI khi Windows scaling ≠ 100% (DPI), gây lệch ra góc. Tự dispatch
/// về UI thread nếu gọi từ luồng nền; fallback MessageBox native khi chưa có cửa sổ app.
/// </summary>
public static class Dialogs
{
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var owner = OwnerWindow();
        if (owner is null)
            return MessageBox.Show(text, caption, button, icon);
        if (!owner.Dispatcher.CheckAccess())
            return owner.Dispatcher.Invoke(() => ShowCore(owner, text, caption, button, icon));
        return ShowCore(owner, text, caption, button, icon);
    }

    public static MessageBoxResult Show(string text, string caption) =>
        Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string text) =>
        Show(text, "", MessageBoxButton.OK, MessageBoxImage.None);

    private static MessageBoxResult ShowCore(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var dlg = new MessageDialog(text, caption, button, icon) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    private static Window? OwnerWindow()
    {
        var mw = Application.Current?.MainWindow;
        return mw is { IsVisible: true } && mw.WindowState != WindowState.Minimized ? mw : null;
    }
}
