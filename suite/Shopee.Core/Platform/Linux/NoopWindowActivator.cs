using System.Diagnostics;
using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Linux;

/// <summary>Focus cửa sổ khi click dòng process — no-op trên Linux (tính năng phụ). Có thể nâng cấp dùng
/// wmctrl/xdotool sau nếu cần; degrade về no-op là chấp nhận được.</summary>
[SupportedOSPlatform("linux")]
internal sealed class NoopWindowActivator : IWindowActivator
{
    public void BringProcessWindowToFront(Process? process) { }
}
