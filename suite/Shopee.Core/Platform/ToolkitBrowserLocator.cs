using Shopee.Core.Browser;
using ToolkitLocator = Shopee.Toolkit.Browser.BrowserLocator;

namespace Shopee.Core.Platform;

/// <summary>
/// Wrapper MỎNG: nối contract <see cref="IBrowserLocator"/> của engine vào bộ dò dùng chung
/// <see cref="ToolkitLocator"/> (shared/Shopee.Toolkit). Thay cho cặp WindowsBrowserLocator/LinuxBrowserLocator
/// cũ — bản dùng chung TỰ phân nhánh theo HĐH bên trong (Windows: đường dẫn cố định + registry App Paths cho
/// Brave; Linux: /usr/bin + snap + flatpak + dò PATH) nên ở đây chỉ còn việc ánh xạ <see cref="BrowserKind"/>.
/// Toàn bộ đường dẫn ứng viên và thứ tự ưu tiên nằm ở bản dùng chung — sửa ở đó, không sửa ở đây.
/// </summary>
internal sealed class ToolkitBrowserLocator : IBrowserLocator
{
    public string? DetectExe(BrowserKind kind) => kind == BrowserKind.Brave
        ? ToolkitLocator.FindBraveExecutable()
        : ToolkitLocator.FindEdgeExecutable();

    public string? DetectUserData(BrowserKind kind) => kind == BrowserKind.Brave
        ? ToolkitLocator.FindBraveUserData()
        : ToolkitLocator.FindEdgeUserData();
}
