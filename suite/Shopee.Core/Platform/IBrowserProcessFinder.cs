namespace Shopee.Core.Platform;

/// <summary>1 tiến trình + command-line của nó. StartTime KHÔNG nằm đây (caller tự lấy qua
/// Process.GetProcessById khi cần) để không tốn call thừa ở nơi chỉ khớp theo command-line.</summary>
public readonly record struct ProcessCommandLine(int Pid, string Name, string CommandLine);

/// <summary>
/// Liệt kê tiến trình kèm command-line — thay đúng phần "quét tiến trình" của WMI (Win32_Process) trên
/// Windows; Linux (GĐ3) = đọc /proc/&lt;pid&gt;/cmdline. Logic KHỚP (ExtractUserDataDir, Contains vs Equals,
/// NormalizePath) giữ nguyên Ở CALLER — đây chỉ là nguồn dữ liệu tiến trình, không đổi ngữ nghĩa khớp.
/// </summary>
public interface IBrowserProcessFinder
{
    /// <summary>Liệt kê tiến trình. <paramref name="names"/> = lọc theo tên tiến trình (khớp đúng trên
    /// Windows, vd "brave.exe"); null/rỗng = MỌI tiến trình. <paramref name="log"/> nhận thông báo lỗi quét
    /// (best-effort, không ném).</summary>
    IReadOnlyList<ProcessCommandLine> Enumerate(IReadOnlyList<string>? names, Action<string>? log = null);
}
