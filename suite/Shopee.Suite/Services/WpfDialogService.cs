using System.Windows;

namespace Shopee.Suite.Services;

/// <summary>Impl WPF của <see cref="IDialogService"/> — dùng <see cref="MessageDialog"/> căn giữa cửa sổ
/// chính. Tự marshal UI thread (gọi được từ luồng nền). API giữ nguyên kiểu async dù
/// <c>ShowDialog()</c> của WPF là blocking.</summary>
public sealed class WpfDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Question) =>
        UiThread.InvokeAsync(() => Show(text, caption, confirm: true, icon));

    public Task InfoAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        UiThread.InvokeAsync(() => { Show(text, caption, confirm: false, icon); });

    public void Notify(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        UiThread.Post(() => Show(text, caption, confirm: false, icon));

    private static bool Show(string text, string caption, bool confirm, DialogIcon icon)
    {
        var owner = MainWindow();
        var dlg = new MessageDialog(text, caption, confirm, icon);
        if (owner is not null)
        {
            dlg.Owner = owner;
            dlg.ShowDialog();
            return dlg.Result;
        }
        dlg.Show();   // chưa có cửa sổ chính (lỗi lúc khởi động) → hiện không-modal, coi như false
        return false;
    }

    private static Window? MainWindow()
    {
        var mw = Application.Current?.MainWindow;
        return mw is { IsVisible: true } && mw.WindowState != WindowState.Minimized ? mw : null;
    }
}
