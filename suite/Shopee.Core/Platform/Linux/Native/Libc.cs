using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Linux.Native;

/// <summary>P/Invoke libc cho impl Linux (.NET không có sẵn malloc_trim). Best-effort — nuốt lỗi ở caller.</summary>
[SupportedOSPlatform("linux")]
internal static class Libc
{
    /// <summary>Trả bộ nhớ heap chưa dùng về OS (giống EmptyWorkingSet trên Windows, best-effort).</summary>
    [DllImport("libc", SetLastError = true)]
    internal static extern int malloc_trim(nuint pad);
}
