using System.Windows;

namespace Shopee.Suite.Services;

/// <summary>
/// Mở cửa sổ modal: gán owner (cửa sổ chính) + marshal UI thread ở MỘT chỗ. Chữ ký async (ViewModel gọi
/// <c>await</c>) dù <c>ShowDialog()</c> của WPF là blocking.
/// Trả DialogResult (null nếu đóng bằng X); các cửa sổ tuỳ biến gán <c>DialogResult = true/false</c>.
/// </summary>
public static class WindowHost
{
    public static Task<bool?> ShowDialogAsync(Window dialog) => UiThread.InvokeAsync<bool?>(() =>
    {
        var owner = MainWindow();
        if (owner is not null)
        {
            dialog.Owner = owner;
            return dialog.ShowDialog();
        }
        dialog.Show();
        return null;
    });

    /// <summary>Mở cửa sổ KHÔNG modal (không chặn cửa sổ chính), gán owner nếu có. Marshal về UI thread.</summary>
    public static void Show(Window window) => UiThread.Post(() =>
    {
        var owner = MainWindow();
        if (owner is not null) window.Owner = owner;
        window.Show();
    });

    private static Window? MainWindow()
    {
        var mw = Application.Current?.MainWindow;
        return mw is { IsVisible: true } ? mw : null;
    }
}
