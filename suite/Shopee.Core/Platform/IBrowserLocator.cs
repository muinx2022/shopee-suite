using Shopee.Core.Browser;

namespace Shopee.Core.Platform;

/// <summary>
/// Định vị exe + thư mục "User Data" của trình duyệt. Windows = đường dẫn cố định (Program Files/LocalAppData)
/// + registry App Paths cho Brave; Linux (GĐ3) = which/usr/bin/snap/flatpak + ~/.config/BraveSoftware.
/// Trả null nếu không tìm thấy.
/// </summary>
public interface IBrowserLocator
{
    string? DetectExe(BrowserKind kind);
    string? DetectUserData(BrowserKind kind);
}
