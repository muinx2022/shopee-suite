using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Shopee.Core.Platform.Windows;

/// <summary>DPAPI: giải bọc theo user hiện tại (dùng để mở key os_crypt của Chromium + cookie cũ pre-v10).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsOsCrypt : IOsCrypt
{
    public byte[]? UnprotectCurrentUser(byte[] data)
    {
        try { return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser); }
        catch { return null; }
    }
}
