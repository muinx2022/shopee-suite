using System.Runtime.Versioning;
using System.Text;

namespace Shopee.Core.Platform.Linux;

/// <summary>
/// Liệt kê tiến trình + command-line qua /proc (thay WMI Win32_Process). CHỦ Ý BỎ QUA lọc theo tên: trên Linux
/// /proc/&lt;pid&gt;/comm bị cắt còn 15 ký tự và tên không ổn định (brave / brave-browser / wrapper), trong khi
/// giá trị --user-data-dir trong cmdline là KHỚP CHÍNH XÁC. Trả MỌI tiến trình có cmdline; caller lọc tiếp theo
/// cmdline (ExtractUserDataDir / Contains) — không bao giờ bỏ sót Brave cần dọn. Tiến trình không phải Brave
/// không có --user-data-dir nên caller tự loại. Đọc vài trăm file /proc nhỏ, chạy không thường xuyên → rẻ.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxBrowserProcessFinder : IBrowserProcessFinder
{
    public IReadOnlyList<ProcessCommandLine> Enumerate(IReadOnlyList<string>? names, Action<string>? log = null)
    {
        var list = new List<ProcessCommandLine>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, out var pid) || pid <= 0) continue;

                string cmdline;
                try
                {
                    var raw = File.ReadAllBytes($"/proc/{pid}/cmdline");
                    cmdline = CmdlineFromNul(raw);
                }
                catch { continue; }   // tiến trình đã thoát / không đọc được
                if (string.IsNullOrEmpty(cmdline)) continue;   // kernel thread → cmdline rỗng

                string comm = "";
                try { comm = File.ReadAllText($"/proc/{pid}/comm").Trim(); } catch { }

                list.Add(new ProcessCommandLine(pid, comm, cmdline));
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Quét /proc lỗi: {ex.Message}");
        }
        return list;
    }

    /// <summary>/proc/&lt;pid&gt;/cmdline là các argv ngăn cách bằng NUL. Ghép lại bằng dấu cách để khớp
    /// --user-data-dir như một chuỗi command-line (giá trị đã tách khỏi cờ, không còn dấu nháy).</summary>
    private static string CmdlineFromNul(byte[] raw)
    {
        if (raw.Length == 0) return "";
        var end = raw.Length;
        if (raw[end - 1] == 0) end--;   // bỏ NUL cuối
        var s = Encoding.UTF8.GetString(raw, 0, end);
        return s.Replace('\0', ' ');
    }
}
