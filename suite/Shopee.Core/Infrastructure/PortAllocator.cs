namespace Shopee.Core.Infrastructure;

/// <summary>
/// Cấp phát cổng loopback trống và GIỮ CHỖ tới khi được trả lại. Khi nhiều luồng mở trình
/// duyệt song song, 2 lần dò ephemeral gần như cùng lúc có thể nhận trùng cổng (listener dò
/// đã đóng ngay) → 2 cửa sổ tranh cùng cổng debug. HashSet reserved bảo đảm mỗi luồng 1 cổng.
/// </summary>
public static class PortAllocator
{
    private static readonly object _lock = new();
    private static readonly HashSet<int> _reserved = [];

    public static int Reserve()
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                l.Start();
                var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                if (_reserved.Add(port))
                    return port;
            }
            throw new InvalidOperationException("Không cấp phát được cổng CDP trống.");
        }
    }

    public static void Release(int port)
    {
        if (port <= 0) return;
        lock (_lock) _reserved.Remove(port);
    }
}
