using System.Runtime.Versioning;
using Shopee.Core.Browser;

namespace Shopee.Core.Platform.Linux;

/// <summary>
/// Định vị Brave/Edge trên Linux: đường dẫn cố định (/usr/bin, snap, flatpak exports) + dò trên PATH.
/// User-data ở ~/.config/BraveSoftware/Brave-Browser (flatpak: ~/.var/app/com.brave.Browser/config/...).
/// Edge trên Linux hiếm → best-effort, thường trả null (Check Account gắn Windows). Trả null nếu không thấy.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxBrowserLocator : IBrowserLocator
{
    public string? DetectExe(BrowserKind kind)
    {
        var home = HomeDir();
        var candidates = kind == BrowserKind.Brave
            ? new[]
            {
                "/usr/bin/brave-browser",
                "/usr/bin/brave-browser-stable",
                "/usr/bin/brave",
                "/snap/bin/brave",
                "/var/lib/flatpak/exports/bin/com.brave.Browser",
                Path.Combine(home, ".local/share/flatpak/exports/bin/com.brave.Browser"),
            }
            : new[]
            {
                "/usr/bin/microsoft-edge",
                "/usr/bin/microsoft-edge-stable",
                "/opt/microsoft/msedge/microsoft-edge",
            };

        var hit = candidates.FirstOrDefault(File.Exists);
        if (hit is not null) return hit;

        // Fallback: dò trên PATH theo các tên khả dĩ.
        var binNames = kind == BrowserKind.Brave
            ? new[] { "brave-browser", "brave-browser-stable", "brave" }
            : new[] { "microsoft-edge", "microsoft-edge-stable" };
        foreach (var bin in binNames)
            if (FindOnPath(bin) is { } p) return p;

        return null;
    }

    public string? DetectUserData(BrowserKind kind)
    {
        var home = HomeDir();
        var candidates = kind == BrowserKind.Brave
            ? new[]
            {
                Path.Combine(home, ".config/BraveSoftware/Brave-Browser"),
                Path.Combine(home, ".var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser"),
            }
            : new[]
            {
                Path.Combine(home, ".config/microsoft-edge"),
            };

        return candidates.FirstOrDefault(p => Directory.Exists(Path.Combine(p, "Default")));
    }

    private static string HomeDir() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
