using System.Diagnostics;

namespace Shopee.Core.Platform;

/// <summary>
/// "Bọc vòng đời tiến trình con": mọi Brave phóng qua đây phải CHẾT THEO app (đóng thường/crash/force-kill).
/// Windows = Job Object KILL_ON_JOB_CLOSE (OS tự ép). Linux (GĐ3) = systemd-run --user --scope / process-group.
/// Thay cho toàn bộ P/Invoke kernel32 trong <see cref="Shopee.Core.Browser.BraveJobObject"/>.
/// </summary>
public interface IProcessLifetimeScope
{
    /// <summary>Đặt trần số tiến trình + RAM-commit của scope. CHỈ có tác dụng nếu gọi TRƯỚC lần phóng đầu.</summary>
    void ConfigureLimits(int activeProcessLimit, ulong jobMemoryLimitBytes);

    /// <summary>Phóng tiến trình đã nằm SẴN trong scope (con cháu cũng thuộc scope). Không bao giờ ném —
    /// interop lỗi thì fallback phóng thường best-effort.</summary>
    Process Start(string fileName, string arguments);
}
