namespace Shopee.Core.Infrastructure;

/// <summary>
/// Cấp phát cổng loopback cho trình duyệt. Một lớp, HAI lối dùng (trước đây là ba bản chép tay: Core +
/// MultiBrave + UpdateProduct):
/// <list type="bullet">
/// <item><b>Ephemeral (static <see cref="Reserve"/>/<see cref="Release"/>)</b> — hỏi OS một cổng trống bất kỳ và
/// GIỮ CHỖ tới khi được trả lại. Khi nhiều luồng mở trình duyệt song song, 2 lần dò ephemeral gần như cùng lúc
/// có thể nhận trùng cổng (listener dò đã đóng ngay) → 2 cửa sổ tranh cùng cổng debug. HashSet reserved bảo đảm
/// mỗi luồng 1 cổng. Dùng bởi <c>BrowserLauncher</c> và module Search.</item>
/// <item><b>Pool theo DẢI (thể hiện: <see cref="Allocate"/>/<see cref="Free"/>)</b> — hàng đợi cổng liên tiếp
/// <c>[basePort + PortOffset .. +count)</c> của một <see cref="AppSession"/>, cấp theo lượt và trả về pool khi
/// xong. Dùng bởi Scrape (9330, 600 cổng) và Update Product (10000, 400 cổng), mỗi module một thể hiện.</item>
/// </list>
/// </summary>
public sealed class PortAllocator
{
    // ── Lối 1: ephemeral, dùng chung toàn process (static) ───────────────────────
    private static readonly object _ephemeralLock = new();
    private static readonly HashSet<int> _reserved = [];

    /// <summary>Xin OS một cổng loopback trống và GIỮ CHỖ (không luồng nào khác nhận lại) tới khi <see cref="Release"/>.</summary>
    public static int Reserve()
    {
        lock (_ephemeralLock)
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

    /// <summary>Nhả chỗ giữ của <see cref="Reserve"/>.</summary>
    public static void Release(int port)
    {
        if (port <= 0) return;
        lock (_ephemeralLock) _reserved.Remove(port);
    }

    // ── Lối 2: pool theo dải cổng của một phiên (thể hiện) ───────────────────────
    private readonly object _lock = new();
    private readonly AppSession _session;
    private readonly int _basePort;
    private readonly int _count;
    private readonly string _label;
    private readonly Queue<int> _ports;
    private readonly HashSet<int> _leased = [];

    /// <summary>Pool cổng <c>[basePort + session.PortOffset .. +count)</c>. <paramref name="label"/> chỉ dùng cho
    /// thông điệp lỗi khi hết cổng.</summary>
    public PortAllocator(int basePort, int count, AppSession session, string label)
    {
        _basePort = basePort;
        _count = count;
        _session = session;
        _label = label;
        _ports = CreatePortQueue(basePort + session.PortOffset, count);
    }

    /// <summary>Mượn một cổng còn trống trong dải; ném khi hết cổng.</summary>
    public int Allocate()
    {
        lock (_lock)
        {
            // Port BẬN TẠM (TIME_WAIT ngay sau khi đóng Brave) phải QUAY LẠI pool. Trước đây `continue` làm
            // port đã dequeue biến mất hẳn: không re-enqueue, cũng không vào _leased nên Free() (chỉ trả
            // port có trong _leased) không cứu được → pool teo dần sau mỗi lượt đóng/mở Brave, chạy auto dài
            // là "Khong con port" dù thực tế port đã rảnh từ lâu.
            List<int>? busy = null;
            var checkedCount = _ports.Count;   // quét tối đa 1 vòng ⇒ mọi port đều bận thì thoát, không lặp vô hạn
            try
            {
                while (checkedCount-- > 0 && _ports.Count > 0)
                {
                    var port = _ports.Dequeue();
                    if (_leased.Contains(port))
                        continue;   // đang cho mượn (lọt trùng vào queue) → bỏ, Free() sẽ đưa lại vào pool
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
                // (Vị trí "cuối queue" không bền: Free() dùng EnqueueSorted sắp lại toàn bộ, xấu nhất chỉ là
                // thử lại một port còn TIME_WAIT rồi gom lại tiếp — vô hại.)
                if (busy is not null)
                    foreach (var p in busy)
                        _ports.Enqueue(p);
            }
        }

        throw new InvalidOperationException($"Khong con port trong cho {_label}.");
    }

    /// <summary>Trả cổng đã mượn về pool (bỏ qua nếu không phải cổng đang mượn / ngoài dải).</summary>
    public void Free(int port)
    {
        lock (_lock)
        {
            if (!_leased.Remove(port))
                return;

            if (IsInRange(port))
                EnqueueSorted(_ports, port);
        }
    }

    private static Queue<int> CreatePortQueue(int start, int count)
    {
        var queue = new Queue<int>(count);
        for (var port = start; port < start + count; port++)
            queue.Enqueue(port);
        return queue;
    }

    private bool IsInRange(int port)
    {
        var start = _basePort + _session.PortOffset;
        return port >= start && port < start + _count;
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
