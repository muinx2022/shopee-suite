using System.Management;
using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Windows;

/// <summary>Quét tiến trình + command-line qua WMI (Win32_Process). WMI có thể bị tắt/hạn chế quyền →
/// nuốt lỗi, trả rỗng (không chặn việc đóng profile).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserProcessFinder : IBrowserProcessFinder
{
    public IReadOnlyList<ProcessCommandLine> Enumerate(IReadOnlyList<string>? names, Action<string>? log = null)
    {
        var list = new List<ProcessCommandLine>();
        try
        {
            var query = "SELECT ProcessId, Name, CommandLine FROM Win32_Process";
            if (names is { Count: > 0 })
            {
                var where = string.Join(" OR ", names.Select(n => $"Name = '{n.Replace("'", "''")}'"));
                query += " WHERE " + where;
            }

            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                try
                {
                    var cl = obj["CommandLine"] as string;
                    if (string.IsNullOrEmpty(cl)) continue;
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    if (pid <= 0) continue;
                    var name = obj["Name"] as string ?? "";
                    list.Add(new ProcessCommandLine(pid, name, cl));
                }
                catch { /* bỏ qua tiến trình không đọc được thuộc tính */ }
                finally { obj.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Quét tiến trình (WMI) lỗi: {ex.Message}");
        }
        return list;
    }
}
