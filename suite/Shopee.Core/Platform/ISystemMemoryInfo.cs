namespace Shopee.Core.Platform;

/// <summary>Bộ nhớ vật lý hệ thống. Windows = GlobalMemoryStatusEx; Linux (GĐ3) = /proc/meminfo.</summary>
public interface ISystemMemoryInfo
{
    ulong TotalPhysicalBytes();
    ulong AvailablePhysicalBytes();
}
