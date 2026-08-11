using System.Diagnostics;
using System.Text;
using Shopee.Core.Platform;

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
    /// <paramref name="includeCrashpadOrphans"/> = true → giết THÊM crashpad_handler mồ côi còn giữ profile
    /// (delete-pending) sau khi brave cha đã thoát; khớp theo RANH GIỚI path để không giết nhầm profile khác.
    /// Best-effort, không ném lỗi. Trả về số tiến trình đã giết.
    /// </summary>
    public static int KillByUserDataDir(
        string? userDataDir, Action<string>? log = null, bool includeCrashpadOrphans = false)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
            return 0;

        var needle = NormalizePath(userDataDir);
        if (needle.Length == 0)
            return 0;

        var killed = 0;
        foreach (var pid in FindPidsByUserDataDir(userDataDir, log))
        {
            if (TryKillTree(pid))
                killed++;
        }

        // Crashpad mồ côi: brave cha đã thoát nên nó KHÔNG nằm trong cây tiến trình nào (kill brave ở trên
        // bỏ sót nó) mà vẫn giữ profile ở trạng thái delete-pending → CreateDirectory fail. Chỉ quét khi
        // caller yêu cầu (luồng Search) để giữ nguyên hành vi các caller khác.
        if (includeCrashpadOrphans)
        {
            foreach (var pid in FindCrashpadOrphanPidsByCommandLine(needle, log))
            {
                if (TryKillTree(pid))
                    killed++;
            }
        }

        if (killed > 0)
            log?.Invoke($"Đã dọn {killed} tiến trình Brave còn sót của profile.");
        return killed;
    }

    private static readonly string[] BraveOnly = ["brave.exe"];
    private static readonly string[] CrashpadOnly = ["crashpad_handler.exe"];

    /// <summary>Tìm PID mọi brave.exe có cờ <c>--user-data-dir</c> trỏ ĐÚNG tới <paramref name="userDataDir"/>
    /// (khớp CHÍNH XÁC giá trị đã chuẩn hoá, KHÔNG Contains: profile "acc_1" là chuỗi con của "acc_10" → Contains
    /// sẽ đụng account khác khi có ≥10 account). Dùng chung cho <see cref="KillByUserDataDir"/> lẫn
    /// <see cref="BraveWindowMinimizer"/> (không đổi hành vi kill). Trả rỗng nếu path rỗng.</summary>
    internal static List<int> FindPidsByUserDataDir(string userDataDir, Action<string>? log = null)
    {
        var pids = new List<int>();
        if (string.IsNullOrWhiteSpace(userDataDir))
            return pids;
        var needle = NormalizePath(userDataDir);
        if (needle.Length == 0)
            return pids;

        foreach (var p in PlatformServices.ProcessFinder.Enumerate(BraveOnly, log))
        {
            var dir = ExtractUserDataDir(p.CommandLine);
            if (dir is not null &&
                string.Equals(NormalizePath(dir), needle, StringComparison.OrdinalIgnoreCase))
            {
                if (p.Pid > 0)
                    pids.Add(p.Pid);
            }
        }
        return pids;
    }

    // crashpad_handler KHÔNG mang cờ --user-data-dir riêng (đường dẫn profile nằm trong --database=…\Crashpad,
    // --metrics-dir=… v.v.) nên không thể khớp-đúng-giá-trị như brave.exe. Khớp theo RANH GIỚI path: needle phải
    // theo sau bằng \ / " hoặc hết chuỗi/khoảng trắng → tránh giết nhầm acc_1 khi profile là acc_10 (chặt hơn
    // bản cũ vốn dùng Contains trần).
    private static List<int> FindCrashpadOrphanPidsByCommandLine(string normalizedNeedle, Action<string>? log)
    {
        var pids = new List<int>();
        foreach (var p in PlatformServices.ProcessFinder.Enumerate(CrashpadOnly, log))
        {
            if (p.Pid > 0 && CommandLineReferencesPath(p.CommandLine, normalizedNeedle))
                pids.Add(p.Pid);
        }
        return pids;
    }

    /// <summary>true nếu <paramref name="commandLine"/> nhắc tới ĐÚNG <paramref name="normalizedNeedle"/> theo ranh
    /// giới path (ký tự ngay sau là <c>\</c> <c>/</c> <c>"</c>, khoảng trắng, hoặc hết chuỗi) — không khớp
    /// acc_1 nằm trong acc_10.</summary>
    private static bool CommandLineReferencesPath(string commandLine, string normalizedNeedle)
    {
        if (string.IsNullOrEmpty(commandLine) || normalizedNeedle.Length == 0)
            return false;

        var from = 0;
        while (from <= commandLine.Length - normalizedNeedle.Length)
        {
            var idx = commandLine.IndexOf(normalizedNeedle, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            var after = idx + normalizedNeedle.Length;
            if (after >= commandLine.Length)
                return true; // path ở cuối chuỗi

            var c = commandLine[after];
            if (c == '\\' || c == '/' || c == '"' || char.IsWhiteSpace(c))
                return true; // ranh giới hợp lệ

            from = idx + 1; // trùng dở (vd acc_1 trong acc_10) → dò tiếp
        }
        return false;
    }

    /// <summary>Giết cả cây tiến trình theo PID (best-effort). Dùng chung với <see cref="BraveFleet"/>.</summary>
    internal static bool TryKillTree(int pid)
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

    /// <summary>
    /// Trích giá trị của cờ <c>--user-data-dir=</c> từ command-line. Dùng chung với <see cref="BraveFleet"/>
    /// và <see cref="BraveWindowMinimizer"/>.
    /// <para><b>Phải tách theo luật ngoặc, không được bóc chuỗi.</b> Có BA dạng ngoặc thật ngoài đời và bản cũ
    /// chỉ đúng hai:</para>
    /// <list type="number">
    /// <item><c>--user-data-dir=C:\ho-so\acc_1</c> — không dấu cách, không ngoặc.</item>
    /// <item><c>--user-data-dir="C:\ho so\acc_1"</c> — ngoặc SAU dấu <c>=</c>, do phía suite tự bọc
    /// (<c>BraveArgs.Create()</c> → <c>Process.Start(exe, argsString)</c>).</item>
    /// <item><c>"--user-data-dir=C:\ho so\acc_1"</c> — ngoặc bọc CẢ tham số. Đây là dạng phía orders
    /// (<c>BraveArgs.CreateRaw()</c> → <see cref="ProcessStartInfo.ArgumentList"/>): .NET tự bọc ngoặc quanh
    /// tham số nào có dấu cách, nên dấu <c>"</c> rơi vào TRƯỚC <c>--user-data-dir</c>.</item>
    /// </list>
    /// <para>Bản cũ chỉ xét <c>rest[0] == '"'</c> nên dạng (3) rơi xuống nhánh cắt-tại-dấu-cách và trả về
    /// <c>C:\Users\Ng</c> trên máy có dấu cách trong đường dẫn ⇒ <see cref="BraveFleet"/> không nhận ra trình
    /// duyệt của app ⇒ bước dọn hồ sơ mồ côi lúc khởi động im lặng không làm gì. Sửa 11/08/2026.</para>
    /// </summary>
    internal static string? ExtractUserDataDir(string commandLine)
    {
        const string flag = "--user-data-dir=";
        if (string.IsNullOrEmpty(commandLine))
            return null;

        foreach (var token in TachThamSo(commandLine))
        {
            if (!token.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = token[flag.Length..];
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    /// <summary>Tách một dòng lệnh thành từng THAM SỐ theo luật ngoặc kép của Windows: khoảng trắng NGOÀI ngoặc
    /// là dấu ngắt, ngoặc kép chỉ bật/tắt chế độ "trong ngoặc" và không vào giá trị. Đủ cho dòng lệnh Chromium
    /// (không có escape <c>\"</c>). Hàm THUẦN — test thẳng được.</summary>
    internal static IEnumerable<string> TachThamSo(string commandLine)
    {
        var sb = new StringBuilder();
        var trongNgoac = false;
        var coKyTu = false;   // đã mở một tham số (kể cả tham số rỗng dạng "") → khác với "chưa gặp gì"

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                trongNgoac = !trongNgoac;
                coKyTu = true;
                continue;
            }
            if (!trongNgoac && char.IsWhiteSpace(c))
            {
                if (coKyTu)
                {
                    yield return sb.ToString();
                    sb.Clear();
                    coKyTu = false;
                }
                continue;
            }
            sb.Append(c);
            coKyTu = true;
        }

        if (coKyTu)
            yield return sb.ToString();
    }

    /// <summary>Chuẩn hoá đường dẫn để so khớp: bỏ khoảng trắng + dấu nháy hai đầu + dấu <c>\</c>/<c>/</c> cuối.
    /// Dùng chung với <see cref="BraveWindowMinimizer"/>.</summary>
    internal static string NormalizePath(string path) =>
        path.Trim().Trim('"').TrimEnd('\\', '/');
}
