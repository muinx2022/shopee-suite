namespace Shopee.Core.Coordination;

/// <summary>Câu trả lời của Hub cho một lượt hỏi (POST xin login / GET đọc trạng thái), rút về 3 field THUẦN BCL.
/// Bản sao gọn của <c>BigSellerReloginResponse</c> (HubDtos): lớp điều phối dưới đây CỐ Ý không tham chiếu
/// HubClient/DTO để LINK được nguyên file vào project test (khuôn <see cref="MachineSlots"/>/<see cref="OpLanes"/>);
/// chỗ nối dây (<see cref="CoordinationRuntime"/>) map DTO → kiểu này.</summary>
public sealed record HubReloginState(bool Accepted, string Status, string Message);

/// <summary>
/// Client gặp "BigSeller đòi mã verify / mất phiên" → NHỜ Hub đăng nhập lại acc đó (hub có mật khẩu + giải captcha
/// + tự đọc mã OTP từ hòm thư), theo dõi tới khi hub xong rồi KÉO COOKIE MỚI về máy này — không ai phải gõ mã ở
/// client. Thuần logic: mọi việc đụng mạng/đĩa đi qua 3 hàm truyền vào (xin login / đọc trạng thái / kéo cookie).
/// <para>Luật: mỗi acc chỉ MỘT lời nhờ đang treo (<see cref="IsRelogging"/> để <c>AssignmentWorker</c> khỏi giao
/// việc cho acc đang chờ); hub trả Accepted=false KHÔNG phải lỗi (đã có phiên khác lo / hub đang bận acc khác) —
/// cứ chờ, hỏi lại nhịp sau. Quá <see cref="MaxWait"/> chưa xong thì buông để không kẹt vĩnh viễn.</para>
/// </summary>
public sealed class BigSellerReloginCoordinator : IDisposable
{
    // Nguyên văn LoginState.Status của hub (server/Shopee.Hub.Web/Services/BigSellerLoginService.cs).
    public const string StatusIdle = "idle";
    public const string StatusRunning = "running";
    public const string StatusNeedsOtp = "needsOtp";
    public const string StatusSuccess = "success";
    public const string StatusFailed = "failed";

    /// <summary>Trần chờ 1 acc: hub giữ browser chờ OTP tối đa 10' nên quá ngần này coi như hỏng — buông ra để
    /// việc của acc không bị treo "đang đăng nhập lại" vĩnh viễn.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>Nhịp hỏi trạng thái hub. Vòng chỉ tốn 1 request/acc đang chờ; danh sách rỗng thì không làm gì.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly Func<string, CancellationToken, Task<HubReloginState?>> _ask;
    private readonly Func<string, CancellationToken, Task<HubReloginState?>> _poll;
    private readonly Func<CancellationToken, Task> _pullCookies;
    private readonly Func<DateTimeOffset> _now;

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private System.Threading.Timer? _timer;
    private int _ticking;

    /// <summary>Dòng chữ báo tiến trình cho lớp trên hiển thị: (accountId, dòng chữ). Lớp này KHÔNG tự biết UI.</summary>
    public Action<string, string>? Log;

    private sealed class Pending
    {
        public required string AccountId;
        public required DateTimeOffset Since;
        /// <summary>Hub đã NHẬN lời nhờ (có phiên login cho acc này) → chuyển sang theo dõi trạng thái. false =
        /// chưa hỏi được / hub đang bận acc khác → nhịp sau XIN LẠI.</summary>
        public bool Asked;
        public bool OtpLogged;      // đã báo "hub đang chờ mã OTP" (chỉ 1 lần)
        public bool WaitLogged;     // đã báo "hub đang bận acc khác" (chỉ 1 lần)
    }

    /// <param name="ask">POST xin hub bắt đầu login acc này. null = chưa hỏi được (mất mạng / hub cũ).</param>
    /// <param name="poll">GET trạng thái phiên login của acc. null = chưa hỏi được.</param>
    /// <param name="pullCookies">Kéo cookie mới từ kho hub về máy này (HubConfigSync.PullCookiesIfNewerAsync).</param>
    /// <param name="now">Đồng hồ (test tiêm được để kiểm trần chờ). null = giờ hệ thống.</param>
    public BigSellerReloginCoordinator(
        Func<string, CancellationToken, Task<HubReloginState?>> ask,
        Func<string, CancellationToken, Task<HubReloginState?>> poll,
        Func<CancellationToken, Task> pullCookies,
        Func<DateTimeOffset>? now = null)
    {
        _ask = ask;
        _poll = poll;
        _pullCookies = pullCookies;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Bật vòng theo dõi định kỳ (<see cref="PollInterval"/>). Gọi MỘT LẦN ở chỗ nối dây; test gọi thẳng
    /// <see cref="TickAsync"/> nên không cần timer. Nhịp mà danh sách chờ RỖNG thì không đụng hub (thoát ngay).</summary>
    public void Start()
    {
        _timer ??= new System.Threading.Timer(
            async _ => { try { await TickAsync().ConfigureAwait(false); } catch { } },
            null, PollInterval, PollInterval);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Acc này đang chờ Hub đăng nhập lại? (AssignmentWorker dùng để không giao việc mới cho nó.)</summary>
    public bool IsRelogging(string accountId)
    {
        lock (_gate) return _pending.ContainsKey(accountId);
    }

    /// <summary>Số acc đang chờ (chẩn đoán + test).</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>Nhờ Hub đăng nhập lại acc này. Acc đã trong danh sách chờ → KHÔNG hỏi lại (nhiều lane/nhiều lần
    /// phát hiện mất phiên chỉ tốn MỘT request). Fire-and-forget: lỗi mạng nuốt hết, vòng theo dõi xin lại.</summary>
    public void Request(string accountId, string reason)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        lock (_gate)
        {
            if (_pending.ContainsKey(accountId)) return;
            _pending[accountId] = new Pending { AccountId = accountId, Since = _now() };
        }
        Say(accountId, $"⏳ Đã nhờ Hub đăng nhập lại BigSeller ({reason}) — chờ cookie mới về.");
        _ = AskAsync(accountId, CancellationToken.None);
    }

    /// <summary>Một vòng theo dõi: xin lại cho acc hub chưa nhận, đọc trạng thái acc đã nhận, kéo cookie khi xong.
    /// Danh sách rỗng → không làm gì. Chống chạy chồng (nhịp trước còn dở thì bỏ nhịp này).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            List<Pending> jobs;
            lock (_gate) jobs = _pending.Values.ToList();
            foreach (var p in jobs)
            {
                if (_now() - p.Since > MaxWait)
                {
                    Drop(p, $"⌛ Quá {MaxWait.TotalMinutes:0} phút chưa thấy Hub đăng nhập lại xong — thôi chờ, cần xử lý tay trên hub.");
                    continue;
                }
                if (!p.Asked) { await AskAsync(p.AccountId, ct).ConfigureAwait(false); continue; }

                HubReloginState? st;
                try { st = await _poll(p.AccountId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { st = null; }
                if (st is null) continue;   // chưa hỏi được → giữ nguyên, thử lại nhịp sau

                switch (st.Status)
                {
                    case StatusSuccess:
                        // Kéo cookie TRƯỚC khi bỏ khỏi danh sách: acc còn "đang relogin" tới lúc cookie đã nằm trên
                        // đĩa, kẻo AssignmentWorker giao lại việc ngay và nó chạy bằng cookie chết.
                        try { await _pullCookies(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { }
                        Drop(p, "✅ Hub đã đăng nhập lại BigSeller — đã kéo cookie mới về, việc sẽ chạy lại.");
                        break;
                    case StatusFailed:
                        Drop(p, $"⛔ Hub đăng nhập lại BigSeller KHÔNG được: {st.Message} — cần xử lý tay trên hub.");
                        break;
                    case StatusNeedsOtp:
                        if (!p.OtpLogged) { p.OtpLogged = true; Say(p.AccountId, "⏳ Hub đang chờ mã OTP — vào hub nhập mã (hub không tự đọc được)."); }
                        break;
                    case StatusIdle:
                        // Hub không còn phiên nào cho acc này (hub khởi động lại / lúc xin thì hub đang bận acc
                        // khác) → xin lại ở nhịp sau.
                        p.Asked = false;
                        break;
                    // StatusRunning: đang chạy → chờ tiếp, không log lại.
                }
            }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>Xin hub bắt đầu login. Accepted=true (hub vừa mở phiên) hoặc trạng thái running/needsOtp (đã có
    /// phiên lo) → chuyển sang theo dõi; failed → hub từ chối hẳn (acc không có / thiếu mật khẩu) → buông.</summary>
    private async Task AskAsync(string accountId, CancellationToken ct)
    {
        HubReloginState? st;
        try { st = await _ask(accountId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { st = null; }
        if (st is null) return;   // chưa hỏi được → vòng theo dõi xin lại

        // Quyết định dưới lock, LOG ngoài lock (callback của lớp trên không chạy trong lock của mình).
        string? line = null;
        lock (_gate)
        {
            if (!_pending.TryGetValue(accountId, out var p)) return;
            if (st.Status == StatusFailed)
            {
                _pending.Remove(accountId);
                line = $"⛔ Hub không đăng nhập lại được: {st.Message}";
            }
            else if (st.Accepted || st.Status is StatusRunning or StatusNeedsOtp) p.Asked = true;
            // Hub bận acc khác (Accepted=false, chưa có phiên cho acc này) → giữ chỗ, nhịp sau xin lại.
            else if (!p.WaitLogged)
            {
                p.WaitLogged = true;
                line = "⏳ " + (string.IsNullOrWhiteSpace(st.Message) ? "Hub chưa nhận lời nhờ — sẽ xin lại." : st.Message);
            }
        }
        if (line is not null) Say(accountId, line);
    }

    private void Drop(Pending p, string line)
    {
        lock (_gate) _pending.Remove(p.AccountId);
        Say(p.AccountId, line);
    }

    private void Say(string accountId, string line)
    {
        try { Log?.Invoke(accountId, line); } catch { }
    }
}
