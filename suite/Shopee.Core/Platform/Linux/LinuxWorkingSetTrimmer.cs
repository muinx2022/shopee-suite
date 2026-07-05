using System.Runtime.Versioning;
using Shopee.Core.Platform.Linux.Native;

namespace Shopee.Core.Platform.Linux;

/// <summary>Trả heap chưa dùng về OS qua malloc_trim (best-effort). Phần GC nén heap nằm ở caller
/// (BraveFleet.TrimAppWorkingSet) — mới là phần chính; malloc_trim chỉ bồi thêm.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxWorkingSetTrimmer : IWorkingSetTrimmer
{
    public void TrimCurrentProcess()
    {
        try { Libc.malloc_trim(0); } catch { }
    }
}
