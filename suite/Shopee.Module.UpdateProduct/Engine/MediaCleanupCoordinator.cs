namespace UpdateProduct;

/// <summary>
/// Điều phối dọn Material Center (thư viện ảnh) DÙNG CHUNG cho MỌI lane của một lượt chạy Update (1 account).
/// Hai vai:
///  (1) ĐẾM số lần bắt-đầu-sửa TOÀN ACCOUNT: quota Material Center là per-account (mọi lane chung 1 kho server-side)
///      nên đếm PER-LANE (mỗi runner một cleaner) là LỆCH — 5 lane phải ~50 lần bắt-đầu-sửa mới đủ ngưỡng 10 đầu tiên,
///      lại còn reset về 0 mỗi khi lane chết→supervisor dựng runner mới. Đặt bộ đếm ở đây (thuộc facade, SỐNG xuyên
///      qua lane-restart) → đúng nhịp "10 SP mỗi wipe" tính trên cả account.
///  (2) CỔNG khẩn cấp pause-all: khi kho đầy (toast/popup) HOẶC đủ ngưỡng, MỘT lane nhận vai thợ dọn và ĐÓNG cổng;
///      các lane khác đậu lại (WaitWhileClosedAsync) tới khi dọn xong rồi cùng quét lại Listing. Trước đây mỗi lane
///      tự dọn tại chỗ trong khi lane khác vẫn chạy → save fail hàng loạt → SP bị "fail 2 lần → bỏ oan".
/// Thread-safe: lock cho cổng, Interlocked/Volatile cho các bộ đếm, TaskCompletionSource(RunContinuationsAsynchronously)
/// để mở cổng không chạy continuation dưới lock.
/// </summary>
internal sealed class MediaCleanupCoordinator
{
    // Giữ nhịp "10 SP mỗi wipe" như bản Python 1-worker (DELETE_IMAGES_AFTER) — nhưng đếm ĐÚNG phạm vi account.
    private const int CleanupThreshold = 10;

    private readonly object _lock = new();
    // != null = cổng ĐANG ĐÓNG (một lane đang dọn) → lane khác đậu; == null = cổng mở.
    private TaskCompletionSource? _closedGate;

    private int _editStartCount;   // số lần bắt-đầu-sửa TOÀN ACCOUNT từ lần dọn gần nhất
    private int _cleanupPending;   // 0/1 — đã có yêu cầu dọn (đủ ngưỡng HOẶC media-đầy) chờ xử lý ở ranh giới vòng lặp
    private int _registered;       // số lane ĐANG SỐNG (RegisterLane/dispose) — mốc cho barrier
    private int _parked;           // số lane ĐANG ĐẬU ở cổng (chờ dọn xong)
    private int _generation;       // +1 mỗi lần dọn xong — lane dùng để biết kho vừa được dọn sạch

    /// <summary>Số lần dọn đã hoàn tất (mỗi <see cref="EndCleanup"/> +1). Lane so sánh để biết kho vừa được làm sạch.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Đã có yêu cầu dọn đang chờ (đủ ngưỡng 10 toàn account HOẶC phát hiện kho đầy).</summary>
    public bool CleanupPending => Volatile.Read(ref _cleanupPending) == 1;

    /// <summary>Runner gọi đầu <c>RunAsync</c>, dispose ở finally. Đếm lane sống cho barrier; lane restart → đăng ký lại.</summary>
    public IDisposable RegisterLane()
    {
        Interlocked.Increment(ref _registered);
        return new LaneRegistration(this);
    }

    /// <summary>Đánh dấu vừa BẮT ĐẦU sửa 1 SP (đếm TOÀN account). Đủ ngưỡng → đặt CleanupPending (KHÔNG wipe ngay giữa
    /// lúc đang sửa — lane sẽ xử lý ở ranh giới vòng lặp qua HandleMediaEmergency).</summary>
    public void RecordEditStart(Action<string> log)
    {
        var n = Interlocked.Increment(ref _editStartCount);
        log($"✏ Bắt đầu sửa SP {n}/{CleanupThreshold} (toàn account, mọi lane).");
        if (n >= CleanupThreshold) RequestCleanup();
    }

    /// <summary>Đường media-đầy gọi: đặt cờ dọn không cần chạm ngưỡng đếm.</summary>
    public void RequestCleanup() => Interlocked.Exchange(ref _cleanupPending, 1);

    /// <summary>Lane đầu tiên nhận vai thợ dọn: ĐÓNG cổng, trả true. Đang có lane khác dọn (cổng đóng) → false.</summary>
    public bool TryBeginCleanup()
    {
        lock (_lock)
        {
            if (_closedGate is not null) return false;
            _closedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <summary>PHẢI gọi trong finally của thợ dọn: MỞ cổng, +Generation, reset bộ đếm về 0, xoá CleanupPending —
    /// không bao giờ để lane khác kẹt vĩnh viễn ở cổng.</summary>
    public void EndCleanup()
    {
        TaskCompletionSource? tcs;
        lock (_lock)
        {
            tcs = _closedGate;
            _closedGate = null;
            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _editStartCount, 0);
            Interlocked.Exchange(ref _cleanupPending, 0);
        }
        tcs?.TrySetResult();   // mở cổng NGOÀI lock (continuation của lane đậu không chạy dưới lock)
    }

    /// <summary>Cổng mở → về ngay (rẻ). Cổng đóng → đậu (tăng parked cho barrier) tới khi thợ dọn mở cổng, rồi
    /// re-check (đề phòng vừa mở lại đóng cho đợt dọn kế). Tôn trọng ct: Stop giữa lúc đậu → OCE thoát sạch.</summary>
    public async Task WaitWhileClosedAsync(CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource? tcs;
            lock (_lock) { tcs = _closedGate; }
            if (tcs is null) return;

            Interlocked.Increment(ref _parked);
            try { await tcs.Task.WaitAsync(ct).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _parked); }
        }
    }

    /// <summary>Thợ dọn chờ các lane KHÁC đậu hết trước khi dọn: poll 500ms tới khi parked ≥ registered−1 hoặc hết
    /// timeout. Lane đang dở AI call có thể lâu → cap rồi cứ dọn (best-effort; hành vi cũ còn dọn ngay không chờ ai).</summary>
    public async Task WaitForOthersParkedAsync(int timeoutMs, CancellationToken ct)
    {
        var deadline = timeoutMs;
        while (deadline > 0)
        {
            ct.ThrowIfCancellationRequested();
            // Thợ dọn KHÔNG tự đậu (đang chạy) → mốc là mọi lane KHÁC (registered−1). Single-lane: registered−1=0 → về ngay.
            if (Volatile.Read(ref _parked) >= Volatile.Read(ref _registered) - 1) return;
            await Task.Delay(500, ct).ConfigureAwait(false);
            deadline -= 500;
        }
    }

    private sealed class LaneRegistration(MediaCleanupCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            // Idempotent: lane restart dispose đúng 1 lần; barrier không đợi lane đã chết.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._registered);
        }
    }
}
