using System.Runtime.Versioning;
using Microsoft.Win32;
using Shopee.Core.Browser;

namespace Shopee.Core.Platform.Windows;

/// <summary>
/// Định vị Brave/Edge trên Windows: đường dẫn cố định (LocalAppData / Program Files / Program Files x86);
/// riêng Brave còn tra registry App Paths\brave.exe (HKCU rồi HKLM) làm PHƯƠNG ÁN CUỐI khi đường dẫn cố
/// định không có. Gộp bộ dò của BrowserLauncher (Scrape/Search) và UpdateProductSettings — registry chỉ
/// bổ sung fallback, không đổi kết quả khi đã tìm thấy theo đường dẫn cố định.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserLocator : IBrowserLocator
{
    public string? DetectExe(BrowserKind kind)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();

        if (kind == BrowserKind.Brave)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            candidates.Add(Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            if (!string.IsNullOrWhiteSpace(pf))
                candidates.Add(Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            if (!string.IsNullOrWhiteSpace(pfx86))
                candidates.Add(Path.Combine(pfx86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));

            // Fallback: App Paths\brave.exe (HKCU rồi HKLM) — chỉ dùng khi các đường dẫn cố định trên đều thiếu.
            foreach (var (root, sub) in new[]
            {
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
                (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
            })
            {
                try
                {
                    using var key = root.OpenSubKey(sub);
                    var value = key?.GetValue(string.Empty)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        candidates.Add(value);
                }
                catch { }
            }
        }
        else
        {
            // Edge: thứ tự gốc là x86 TRƯỚC (khớp BrowserLauncher.Detect).
            candidates.Add(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
            candidates.Add(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe");
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public string? DetectUserData(BrowserKind kind)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = kind == BrowserKind.Brave
            ? Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")
            : Path.Combine(local, "Microsoft", "Edge", "User Data");
        return Directory.Exists(Path.Combine(path, "Default")) ? path : null;
    }
}
