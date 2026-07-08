namespace Shopee.Suite.Services;

/// <summary>Icon hộp thoại — thay MessageBoxImage của WPF để interface không dính framework UI.</summary>
public enum DialogIcon { None, Info, Warning, Error, Question }

/// <summary>
/// Hộp thoại thông báo/xác nhận, không dính framework UI để ViewModel dùng chung WPF ↔ Avalonia.
/// Impl tự lo marshal về UI thread — gọi được từ luồng nền.
/// </summary>
public interface IDialogService
{
    /// <summary>Hộp Có/Không. true = Có.</summary>
    Task<bool> ConfirmAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Question);

    /// <summary>Hộp OK, đợi người dùng đóng.</summary>
    Task InfoAsync(string text, string caption = "", DialogIcon icon = DialogIcon.Info);

    /// <summary>Hộp OK kiểu bắn-rồi-quên — an toàn từ mọi thread, không đợi.</summary>
    void Notify(string text, string caption = "", DialogIcon icon = DialogIcon.Info);
}
