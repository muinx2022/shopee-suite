using System.Windows;

namespace Shopee.Suite.Services;

/// <summary>
/// Khởi động lại CHÍNH app đang chạy (KHÁC Velopack update — không đổi phiên bản): dùng khi đổi "Chế độ
/// ứng dụng" cần dựng lại shell. Relaunch đúng exe hiện hành qua <see cref="Environment.ProcessPath"/>
/// (đúng cả khi cài single-file/Velopack; KHÔNG dùng Assembly.Location — rỗng khi single-file) KÈM lại mọi
/// tham số dòng lệnh gốc (để <c>--mode</c> của shortcut sống qua restart), rồi đóng app ÊM qua
/// <see cref="Application.Shutdown()"/> (kích event Exit → hook dừng module chạy như đóng thường).
/// </summary>
public static class AppRestart
{
    public static void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Dialogs.Notify("Không xác định được đường dẫn app để khởi động lại.", "Lỗi", DialogIcon.Error);
                return;
            }
            // KÈM lại mọi arg gốc (bỏ [0] = đường dẫn exe) để --mode (và mọi tham số) sống qua restart.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
            foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Relaunch hỏng → GIỮ app đang chạy (đừng đóng để người dùng còn dùng được), chỉ báo.
            Dialogs.Notify($"Không khởi động lại được: {ex.Message}", "Lỗi", DialogIcon.Error);
            return;
        }

        // Đóng êm: Shutdown() kích event Exit (đã dừng module đơn hàng + lưu store trong App.xaml.cs).
        // Không có Application (không nên xảy ra) → Environment.Exit.
        if (Application.Current is { } app)
            app.Shutdown();
        else
            Environment.Exit(0);
    }
}
