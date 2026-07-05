using Shopee.Suite.Services;

namespace Shopee.Suite;

/// <summary>
/// Facade tĩnh hộp thoại của app — ViewModel gọi qua đây, không đụng MessageBox/framework UI trực tiếp.
/// Ruột là <see cref="IDialogService"/> (hiện <see cref="WpfDialogService"/>, sẽ swap khi chuyển Avalonia).
/// API async vì dialog Avalonia vốn async; impl tự marshal UI thread nên gọi được từ luồng nền.
/// </summary>
public static class Dialogs
{
    public static IDialogService Service { get; set; } = new WpfDialogService();

    /// <summary>Hộp Có/Không. true = Có.</summary>
    public static Task<bool> ConfirmAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Question) =>
        Service.ConfirmAsync(text, caption, icon);

    /// <summary>Hộp OK, đợi người dùng đóng.</summary>
    public static Task InfoAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        Service.InfoAsync(text, caption, icon);

    /// <summary>Hộp OK bắn-rồi-quên — an toàn từ mọi thread.</summary>
    public static void Notify(string text, string caption = "", DialogIcon icon = DialogIcon.Info) =>
        Service.Notify(text, caption, icon);
}
