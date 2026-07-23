using System.IO;
using System.Runtime.Versioning;

namespace Shopee.Suite.Services;

/// <summary>
/// Tạo shortcut (.lnk) ngoài Desktop trỏ thẳng exe hiện hành kèm tham số dòng lệnh — để mỗi shortcut khoá
/// một <c>--mode</c> chạy song song trên cùng bản cài. CHỈ Windows (dùng COM <c>WScript.Shell</c> qua
/// <c>dynamic</c>/IDispatch); nền khác trả về (false, thông báo). Nuốt lỗi thành (false, message) — KHÔNG
/// ném, để nút Cài đặt gọi nó không làm gãy app.
/// </summary>
public static class ShortcutCreator
{
    /// <summary>
    /// Tạo <paramref name="name"/>.lnk trên Desktop trỏ <paramref name="targetExe"/> với đối số
    /// <paramref name="args"/>. WorkingDirectory = thư mục exe; IconLocation = <paramref name="iconPath"/>,0
    /// (rỗng → lấy icon của chính exe). Trả (true, đường dẫn .lnk) khi xong; (false, lý do) khi lỗi hoặc
    /// không phải Windows. KHÔNG ném.
    /// </summary>
    public static (bool ok, string message) CreateDesktopShortcut(string name, string targetExe, string args, string? iconPath)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Chỉ hỗ trợ tạo shortcut trên Windows.");
        try
        {
            return CreateWindowsShortcut(name, targetExe, args, iconPath);
        }
        catch (Exception ex)
        {
            return (false, $"Không tạo được shortcut: {ex.Message}");
        }
    }

    // COM Windows Script Host: tách riêng + [SupportedOSPlatform] để CA1416 không kêu (chỉ gọi sau guard IsWindows).
    [SupportedOSPlatform("windows")]
    private static (bool ok, string message) CreateWindowsShortcut(string name, string targetExe, string args, string? iconPath)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnkPath = Path.Combine(desktop, name + ".lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Không khởi tạo được WScript.Shell (thiếu Windows Script Host?).");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = targetExe;
        shortcut.Arguments = args;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? "";
        shortcut.IconLocation = (string.IsNullOrEmpty(iconPath) ? targetExe : iconPath) + ",0";
        shortcut.Save();
        return (true, lnkPath);
    }
}
