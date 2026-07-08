using System.Diagnostics;
using System.IO;

namespace Shopee.Suite.Services;

/// <summary>
/// Mở thư mục / hiện file trong trình quản lý file của HĐH — viết cross-platform sẵn (Windows: explorer,
/// Linux: xdg-open) để không phải sửa lại khi chạy Ubuntu. Nuốt lỗi: đây là tiện ích phụ, không được
/// làm gãy flow chính.
/// </summary>
public static class ShellOpener
{
    /// <summary>Mở 1 thư mục trong Explorer / trình quản lý file.</summary>
    public static void OpenFolder(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = false });
        }
        catch { }
    }

    /// <summary>Mở thư mục chứa và bôi đậm file (Windows); Linux chưa có chuẩn chung → mở thư mục chứa.</summary>
    public static void RevealFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = false });
        }
        catch { }
    }
}
