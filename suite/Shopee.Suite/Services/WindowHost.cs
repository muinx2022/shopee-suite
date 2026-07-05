using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Shopee.Suite.Services;

/// <summary>
/// Mở cửa sổ modal: gán owner (cửa sổ chính) + marshal UI thread ở MỘT chỗ. Avalonia ShowDialog vốn async.
/// Trả DialogResult (null nếu đóng bằng X); các cửa sổ tuỳ biến Close(true)/Close(false).
/// </summary>
public static class WindowHost
{
    public static Task<bool?> ShowDialogAsync(Window dialog) => OnUi(async () =>
    {
        var owner = MainWindow();
        if (owner is { IsVisible: true })
            return await dialog.ShowDialog<bool?>(owner);
        dialog.Show();
        return (bool?)null;
    });

    private static Window? MainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static async Task<T> OnUi<T>(Func<Task<T>> f)
    {
        if (Dispatcher.UIThread.CheckAccess()) return await f();
        return await Dispatcher.UIThread.InvokeAsync(f);
    }
}
