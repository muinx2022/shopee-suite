using System.Diagnostics;
using System.Text;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Hook phóng trình duyệt (Brave/Chrome/Edge) của module Đơn hàng. Shell Suite rót
/// <c>BraveJobObject.Start</c> để trình duyệt vào Job Object <c>KILL_ON_JOB_CLOSE</c> — app
/// crash/force-kill → OS giết hết. Module Core/App KHÔNG tham chiếu <c>Shopee.Core</c> nên không
/// gọi Job Object trực tiếp (cùng pattern hook hub push).
/// <para>Hook <c>null</c> (app Đơn hàng chạy độc lập / chưa rót) → <see cref="Process.Start"/> thường.</para>
/// </summary>
public static class BrowserProcessStarter
{
    /// <summary>
    /// Hook do shell Suite gán: <c>(exe, args) → Process</c>. Args là từng token (như
    /// <see cref="ProcessStartInfo.ArgumentList"/>); phía Suite nối bằng <see cref="JoinArguments"/>
    /// trước khi đưa vào Job Object.
    /// </summary>
    public static Func<string, IReadOnlyList<string>, Process>? Start { get; set; }

    /// <summary>
    /// Phóng <paramref name="fileName"/> với <paramref name="arguments"/>. Có hook → gọi hook;
    /// không → <see cref="Process.Start"/> + <see cref="ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public static Process StartOrFallback(string fileName, IReadOnlyList<string> arguments)
    {
        var hook = Start;
        if (hook is not null)
            return hook(fileName, arguments);

        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        return Process.Start(psi)
               ?? throw new InvalidOperationException("Không khởi chạy được tiến trình trình duyệt.");
    }

    /// <summary>
    /// Nối danh sách token thành chuỗi <c>Arguments</c> an toàn cho CreateProcess / Job Object.
    /// Token có khoảng trắng hoặc <c>"</c> được bọc ngoặc kép (escape <c>"</c> kiểu Windows).
    /// </summary>
    public static string JoinArguments(IReadOnlyList<string> arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            AppendQuoted(sb, arguments[i] ?? string.Empty);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        var needsQuote = arg.Length == 0
            || arg.Contains(' ', StringComparison.Ordinal)
            || arg.Contains('\t', StringComparison.Ordinal)
            || arg.Contains('"', StringComparison.Ordinal);

        if (!needsQuote)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var c = arg[i];
            if (c == '"')
                sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
    }
}
