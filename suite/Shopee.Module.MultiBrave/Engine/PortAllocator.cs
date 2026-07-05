using System.Collections.Generic;

namespace OpenMultiBraveLauncherV3;

internal sealed class PortAllocator
{
    private const int InstanceBasePort = 9330;

    private readonly object _lock = new();
    private readonly Queue<int> _instancePorts;
    private readonly HashSet<int> _leased = [];

    public static PortAllocator Shared { get; } = new();

    public PortAllocator()
    {
        _instancePorts = CreatePortQueue(InstanceBasePort + AppSession.PortOffset, 600);
    }

    public int AllocateInstancePort() => Allocate(_instancePorts, "Shopee instance");

    public void Release(int port)
    {
        lock (_lock)
        {
            if (!_leased.Remove(port))
                return;

            if (IsInRange(port, InstanceBasePort, 600))
                EnqueueSorted(_instancePorts, port);
        }
    }

    private int Allocate(Queue<int> queue, string label)
    {
        lock (_lock)
        {
            var checkedCount = queue.Count;
            while (checkedCount-- > 0 && queue.Count > 0)
            {
                var port = queue.Dequeue();
                if (_leased.Contains(port) || !AppSession.IsPortFree(port))
                    continue;

                _leased.Add(port);
                return port;
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
