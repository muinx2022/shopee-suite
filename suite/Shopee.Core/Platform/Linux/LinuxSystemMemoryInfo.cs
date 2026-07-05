using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Linux;

/// <summary>Bộ nhớ vật lý qua /proc/meminfo (MemTotal / MemAvailable, đơn vị kB → byte).</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemMemoryInfo : ISystemMemoryInfo
{
    public ulong TotalPhysicalBytes() => ReadMeminfoBytes("MemTotal:");
    public ulong AvailablePhysicalBytes() => ReadMeminfoBytes("MemAvailable:");

    private static ulong ReadMeminfoBytes(string key)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
                // Ví dụ: "MemTotal:       16384000 kB"
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && ulong.TryParse(parts[1], out var kb))
                    return kb * 1024UL;
                return 0;
            }
        }
        catch { }
        return 0;
    }
}
