using System.Diagnostics;
using System.Management;

namespace Shopee.Core.Browser;

/// <summary>
/// Dọn sạch tiến trình Brave còn sót của một profile. Brave (Chromium) hay fork rồi thoát tiến
/// trình gốc mà launcher giữ tham chiếu → <see cref="Process.Kill(bool)"/> trên PID gốc bỏ sót
/// browser thật + GPU/renderer/utility con. Reaper tìm theo command-line (khớp ĐÚNG giá trị
/// <c>--user-data-dir</c> của profile) để giết tận gốc, tránh tích tụ zombie khi xoay vòng.
/// Dùng chung cho mọi module phóng Brave (MultiBrave/UpdateProduct).
/// </summary>
public static class BraveProcessReaper
{
    /// <summary>
    /// Giết mọi brave.exe có cờ <c>--user-data-dir</c> trỏ ĐÚNG tới <paramref name="userDataDir"/>.
    /// Khớp đúng giá trị (không phải Contains) nên không đụng Brave cá nhân hay profile khác.
    /// Best-effort, không ném lỗi.
    /// </summary>
    public static int KillByUserDataDir(string? userDataDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
            return 0;

        var needle = NormalizePath(userDataDir);
        if (needle.Length == 0)
            return 0;

        var killed = 0;
        foreach (var pid in FindBravePidsByCommandLine(needle, log))
        {
            if (TryKillTree(pid))
                killed++;
        }

        if (killed > 0)
            log?.Invoke($"Đã dọn {killed} tiến trình Brave còn sót của profile.");
        return killed;
    }

    private static List<int> FindBravePidsByCommandLine(string normalizedNeedle, Action<string>? log)
    {
        var pids = new List<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'brave.exe'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                try
                {
                    var commandLine = obj["CommandLine"] as string;
                    if (string.IsNullOrEmpty(commandLine))
                        continue;

                    // Phải khớp ĐÚNG giá trị --user-data-dir (không dùng Contains): profile "acc_1" là
                    // chuỗi con của "acc_10" → Contains sẽ giết nhầm browser account khác khi có ≥10 account.
                    var dir = ExtractUserDataDir(commandLine);
                    if (dir is not null &&
                        string.Equals(NormalizePath(dir), normalizedNeedle, StringComparison.OrdinalIgnoreCase))
                    {
                        var pid = Convert.ToInt32(obj["ProcessId"]);
                        if (pid > 0)
                            pids.Add(pid);
                    }
                }
                catch
                {
                    // bỏ qua tiến trình không đọc được thuộc tính
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // WMI có thể bị tắt/hạn chế quyền — không chặn việc đóng profile.
            log?.Invoke($"Quét tiến trình Brave (WMI) lỗi: {ex.Message}");
        }

        return pids;
    }

    private static bool TryKillTree(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
                return false;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Trích giá trị của cờ <c>--user-data-dir=</c> từ command-line (hỗ trợ có/không dấu nháy).</summary>
    private static string? ExtractUserDataDir(string commandLine)
    {
        const string flag = "--user-data-dir=";
        var idx = commandLine.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var rest = commandLine[(idx + flag.Length)..];
        if (rest.Length == 0)
            return null;

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            return end < 0 ? rest[1..] : rest[1..end];
        }

        var space = rest.IndexOf(' ');
        return space < 0 ? rest : rest[..space];
    }

    private static string NormalizePath(string path) =>
        path.Trim().Trim('"').TrimEnd('\\', '/');
}
