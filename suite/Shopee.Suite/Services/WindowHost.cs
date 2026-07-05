using System.Windows;

namespace Shopee.Suite.Services;

/// <summary>
/// Mở cửa sổ modal: gán owner (cửa sổ chính) + marshal UI thread ở MỘT chỗ, để ViewModel không tự đụng
/// Application.Current/ShowDialog. Chữ ký async sẵn — Avalonia ShowDialog vốn là async, WPF bọc lại.
/// </summary>
public static class WindowHost
{
    /// <summary>Mở <paramref name="dialog"/> dạng modal trên cửa sổ chính. Trả DialogResult (null nếu đóng bằng X).</summary>
    public static Task<bool?> ShowDialogAsync(Window dialog) =>
        UiThread.InvokeAsync<bool?>(() =>
        {
            var mw = Application.Current?.MainWindow;
            if (mw is { IsVisible: true }) dialog.Owner = mw;
            return dialog.ShowDialog();
        });
}
