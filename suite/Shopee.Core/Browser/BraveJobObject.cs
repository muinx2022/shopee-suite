using System.Diagnostics;
using Shopee.Core.Platform;

namespace Shopee.Core.Browser;

/// <summary>
/// Facade ổn định cho việc phóng Brave "chết theo app". Giữ nguyên chữ ký cũ (mọi call site không đổi),
/// uỷ thác cho <see cref="PlatformServices.ProcessLifetime"/>: Windows = Job Object KILL_ON_JOB_CLOSE,
/// Linux (GĐ3) = systemd-run --user --scope / process-group. Xem <see cref="IProcessLifetimeScope"/>.
/// </summary>
public static class BraveJobObject
{
    /// <summary>Đặt trần cho scope: số tiến trình tối đa (0=tắt) + tổng RAM-commit tối đa (0=tắt). CHỈ có
    /// tác dụng nếu gọi TRƯỚC lần phóng Brave đầu tiên.</summary>
    public static void ConfigureLimits(int activeProcessLimit, ulong jobMemoryLimitBytes) =>
        PlatformServices.ProcessLifetime.ConfigureLimits(activeProcessLimit, jobMemoryLimitBytes);

    /// <summary>Phóng Brave trong scope "chết theo app". Không bao giờ ném — lỗi thì fallback phóng thường.
    /// <paramref name="startMinimized"/>=true → cửa sổ mở THU NHỎ, không chiếm màn hình/không cướp focus
    /// (Windows). Nhớ BỎ cờ '--start-maximized' ở args khi bật, kẻo cờ dòng lệnh đè lại thành maximize.</summary>
    public static Process Start(string fileName, string arguments, bool startMinimized = false)
    {
        var proc = PlatformServices.ProcessLifetime.Start(fileName, arguments, startMinimized);
        // STARTUPINFO chỉ ép được cửa sổ ĐẦU của tiến trình stub; Brave fork browser THẬT (PID khác, không
        // thừa hưởng show-state) + cửa sổ mở LẠI khi relaunch/hồi phục không qua STARTUPINFO → bung ra cướp
        // focus. Watchdog quét thêm ~10s hạ MỌI cửa sổ của đúng profile xuống taskbar + trả focus về app đang
        // dùng. Chỉ khi startMinimized (luồng automation); luồng tương tác (BrowserLauncher) phải hiện + focus.
        if (startMinimized)
            BraveWindowMinimizer.Register(arguments);
        return proc;
    }
}
