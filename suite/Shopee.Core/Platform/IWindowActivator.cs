using System.Diagnostics;

namespace Shopee.Core.Platform;

/// <summary>Đưa cửa sổ chính của 1 tiến trình lên trước (click dòng process). Windows = user32
/// SetForegroundWindow/EnumWindows; Linux (GĐ3) = no-op / wmctrl. Tính năng phụ, degrade được.</summary>
public interface IWindowActivator
{
    void BringProcessWindowToFront(Process? process);
}
