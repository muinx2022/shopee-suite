namespace Shopee.Core.Platform;

/// <summary>
/// Giải bọc dữ liệu mã hoá theo user hiện tại (os_crypt của Chromium). Windows = DPAPI
/// ProtectedData.Unprotect(CurrentUser). Linux (GĐ3) = no-op trả null → đọc cookie fallback về login thường
/// (import cookie Edge/Brave là đường tiện lợi chỉ-Windows, caller đã xử lý 0 cookie). Trả null nếu không giải được.
/// </summary>
public interface IOsCrypt
{
    byte[]? UnprotectCurrentUser(byte[] data);
}
