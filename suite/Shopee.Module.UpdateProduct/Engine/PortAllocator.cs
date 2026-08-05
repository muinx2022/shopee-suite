using System.Collections.Generic;

namespace UpdateProduct;

internal sealed class PortAllocator
{
    private const int BigSellerBasePort = 10000;

    private readonly object _lock = new();
    private readonly Queue<int> _bigSellerPorts;
    private readonly HashSet<int> _leased = [];

    public static PortAllocator Shared { get; } = new();

    public PortAllocator()
    {
        _bigSellerPorts = CreatePortQueue(BigSellerBasePort + AppSession.PortOffset, 400);
    }

    public int AllocateBigSellerPort() => Allocate(_bigSellerPorts, "BigSeller");

    public void Release(int port)
    {
        lock (_lock)
        {
            if (!_leased.Remove(port))
                return;

            if (IsInRange(port, BigSellerBasePort, 400))
                EnqueueSorted(_bigSellerPorts, port);
        }
    }

    private int Allocate(Queue<int> queue, string label)
    {
        lock (_lock)
        {
            // Port BẬN TẠM (TIME_WAIT ngay sau khi đóng Brave) phải QUAY LẠI pool. Trước đây `continue` làm
            // port đã dequeue biến mất hẳn: không re-enqueue, cũng không vào _leased nên Release() (chỉ trả
            // port có trong _leased) không cứu được → pool teo dần sau mỗi lượt đóng/mở Brave, chạy auto dài
            // là "Khong con port" dù thực tế port đã rảnh từ lâu.
            List<int>? busy = null;
            var checkedCount = queue.Count;   // quét tối đa 1 vòng ⇒ mọi port đều bận thì thoát, không lặp vô hạn
            try
            {
                while (checkedCount-- > 0 && queue.Count > 0)
                {
                    var port = queue.Dequeue();
                    if (_leased.Contains(port))
                        continue;   // đang cho mượn (lọt trùng vào queue) → bỏ, Release() sẽ đưa lại vào pool
                    if (!AppSession.IsPortFree(port))
                    {
                        (busy ??= []).Add(port);
                        continue;
                    }

                    _leased.Add(port);
                    return port;
                }
            }
            finally
            {
                // Trả lại hàng đợi (kể cả khi đã return ở trên) — port bận không được biến mất khỏi pool.
                // (Vị trí "cuối queue" không bền: Release() dùng EnqueueSorted sắp lại toàn bộ, xấu nhất chỉ là
                // thử lại một port còn TIME_WAIT rồi gom lại tiếp — vô hại.)
                if (busy is not null)
                    foreach (var p in busy)
                        queue.Enqueue(p);
            }
        }

        throw new InvalidOperationException($"Khong con port trong cho {label}.");
    }

    private static Queue<int> CreatePortQueue(int start, int count)
    {
        var queue = new Queue<int>(count);
        for (var port = start; port < start + count; port++)
            queue.Enqueue(port);
        return queue;
    }

    private static bool IsInRange(int port, int basePort, int count)
    {
        var start = basePort + AppSession.PortOffset;
        return port >= start && port < start + count;
    }

    private static void EnqueueSorted(Queue<int> queue, int port)
    {
        var ports = queue.ToList();
        ports.Add(port);
        ports.Sort();
        queue.Clear();
        foreach (var item in ports)
            queue.Enqueue(item);
    }
}
