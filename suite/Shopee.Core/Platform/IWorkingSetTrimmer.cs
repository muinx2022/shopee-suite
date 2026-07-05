namespace Shopee.Core.Platform;

/// <summary>Trả working set của tiến trình app về OS (sau khi GC nén heap). Windows = EmptyWorkingSet;
/// Linux (GĐ3) = malloc_trim best-effort / no-op. Phần GC nằm ở caller (cross-platform).</summary>
public interface IWorkingSetTrimmer
{
    void TrimCurrentProcess();
}
