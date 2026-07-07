using System;
using System.Reflection;

namespace Shopee.Core.Infrastructure;

/// <summary>
/// Phiên bản app — nướng vào assembly lúc build từ <c>version.txt</c> (xem Shopee.Suite.csproj).
/// Dùng cho: nhịp máy báo Hub (Fleet thấy máy nào bản nào) + hiển thị ở màn Cài đặt + so khớp bản cập nhật.
/// Đặt ở Core để cả engine (heartbeat) lẫn UI dùng chung, KHÔNG phụ thuộc Velopack.
/// </summary>
public static class AppInfo
{
    private static readonly Lazy<string> _version = new(Resolve);

    /// <summary>Chuỗi phiên bản sạch (vd "1.0.0"), đã bỏ phần metadata "+&lt;git-hash&gt;" nếu có.</summary>
    public static string Version => _version.Value;

    private static string Resolve()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');   // SourceLink hay chèn "+<hash>" → cắt bỏ cho khớp tag release
            return (plus >= 0 ? info[..plus] : info).Trim();
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
