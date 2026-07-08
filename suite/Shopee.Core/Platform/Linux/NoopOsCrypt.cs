using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Linux;

/// <summary>Không giải os_crypt trên Linux (khác DPAPI: libsecret/kwallet — chưa port). Trả null → ChromiumCookieReader
/// trả 0 cookie → scrape tự fallback đăng nhập thường / mint-token (caller đã xử lý 0 cookie). Import cookie
/// từ Edge/Brave trên máy là đường tiện lợi chỉ-Windows.</summary>
[SupportedOSPlatform("linux")]
internal sealed class NoopOsCrypt : IOsCrypt
{
    public byte[]? UnprotectCurrentUser(byte[] data) => null;
}
