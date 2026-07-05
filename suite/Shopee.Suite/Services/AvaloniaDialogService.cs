using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Shopee.Suite.Services;

/// <summary>Impl Avalonia của <see cref="IDialogService"/> — dùng <see cref="MessageDialog"/> căn giữa cửa sổ
/// chính. Tự marshal UI thread (gọi được từ luồng nền).</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Question) =>
        OnUi(() => ShowAsync(text, caption, confirm: true, icon));

    public async Task InfoAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        await OnUi(() => ShowAsync(text, caption, confirm: false, icon));

    public void Notify(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        UiThread.Post(() => _ = ShowAsync(text, caption, confirm: false, icon));

    private static async Task<bool> ShowAsync(string text, string caption, bool confirm, DialogIcon icon)
    {
        var owner = MainWindow();
        var dlg = new MessageDialog(text, caption, confirm, icon);
        if (owner is { IsVisible: true })
            return await dlg.ShowDialog<bool>(owner);
        dlg.Show();   // chưa có cửa sổ chính (lỗi lúc khởi động) → hiện không-modal, coi như false
        return false;
    }

    private static async Task<T> OnUi<T>(Func<Task<T>> f)
    {
        if (Dispatcher.UIThread.CheckAccess()) return await f();
        return await Dispatcher.UIThread.InvokeAsync(f);
    }

    private static Window? MainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
