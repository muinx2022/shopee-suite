using System.Windows;

namespace Shopee.Suite.Services;

/// <summary>
/// Impl WPF của <see cref="IDialogService"/> — dùng <see cref="MessageDialog"/> (căn giữa cửa sổ app,
/// không lệch khi DPI ≠ 100%); fallback MessageBox native khi chưa có cửa sổ app. Tự marshal UI thread.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Question) =>
        UiThread.InvokeAsync(() => ShowCore(text, caption, MessageBoxButton.YesNo, ToImage(icon)) == MessageBoxResult.Yes);

    public Task InfoAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        UiThread.InvokeAsync(() => { ShowCore(text, caption, MessageBoxButton.OK, ToImage(icon)); });

    public void Notify(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        UiThread.Post(() => ShowCore(text, caption, MessageBoxButton.OK, ToImage(icon)));

    private static MessageBoxResult ShowCore(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var owner = OwnerWindow();
        if (owner is null)
            return MessageBox.Show(text, caption, button, icon);
        var dlg = new MessageDialog(text, caption, button, icon) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    private static Window? OwnerWindow()
    {
        var mw = Application.Current?.MainWindow;
        return mw is { IsVisible: true } && mw.WindowState != WindowState.Minimized ? mw : null;
    }

    private static MessageBoxImage ToImage(DialogIcon icon) => icon switch
    {
        DialogIcon.Info => MessageBoxImage.Information,
        DialogIcon.Warning => MessageBoxImage.Warning,
        DialogIcon.Error => MessageBoxImage.Error,
        DialogIcon.Question => MessageBoxImage.Question,
        _ => MessageBoxImage.None,
    };
}
