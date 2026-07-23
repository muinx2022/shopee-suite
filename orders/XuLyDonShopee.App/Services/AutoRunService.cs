using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Bộ "Chạy tự động theo lô": một vòng chạy NỀN lặp liên tục cho tới khi <see cref="StopAsync"/>.
/// <para>
/// Mỗi lượt: đọc lại snapshot TẤT CẢ tài khoản + cấu hình (người dùng có thể thêm/xóa/đổi giữa chừng) →
/// chia thành các lô N tài khoản → mỗi lô chạy SONG SONG per-account (mở phiên nếu chưa mở → chờ sẵn sàng →
/// Xử lý đơn nếu bật → Sync nếu bật → Kiểm tra LUÔN) → ĐÓNG các phiên do CHÍNH scheduler mở trong lô → nghỉ
/// M phút → lô kế. Hết lô cuối → nghỉ M phút → lặp lại từ snapshot mới.
/// </para>
/// <para>
/// <b>Chỉ dùng API CÔNG KHAI</b> của <see cref="AccountSessionManager"/> (<c>IsRunning/Start/Get/Stop</c>) —
/// KHÔNG phụ thuộc ViewModel. <b>Chỉ đóng phiên do MÌNH mở</b>: đọc <c>IsRunning</c> TRƯỚC khi Start; phiên
/// người dùng đã tự mở thì giữ nguyên. <b>Dừng cắt ở mọi điểm chờ</b>: <see cref="CancellationToken"/> xuyên
/// mọi <c>Task.Delay</c>/poll; các hành động phiên (Sync/Kiểm tra/Xử lý) KHÔNG nhận ct nên khi Dừng ta ĐÓNG
/// phiên (kill Brave) để chúng trả về sớm.
/// </para>
/// </summary>
public sealed class AutoRunService
{
    /// <summary>Nhãn nguồn log cho các thông báo cấp scheduler (không thuộc một shop cụ thể).</summary>
    private const string LogSource = "Chạy tự động";

    /// <summary>Thời hạn chờ một phiên "sẵn sàng thao tác" sau khi mở (đăng nhập + đọc số đơn lần đầu).</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Thời hạn chờ các phiên của lô đóng sạch (Brave chết) trước khi sang bước nghỉ.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(30);

    private readonly AppServices _services;
    private readonly object _gate = new();

    /// <summary>Các tài khoản mà CHÍNH scheduler mở (dùng làm "set" thread-safe) — để chỉ đóng phiên do mình
    /// mở. Thêm khi mở phiên mới trong lô, gỡ khi đã đóng.</summary>
    private readonly ConcurrentDictionary<long, byte> _openedByMe = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private volatile bool _isRunning;
    private volatile string? _currentPhase;

    public AutoRunService(AppServices services) => _services = services;

    /// <summary>Phát khi <see cref="IsRunning"/> / <see cref="CurrentPhase"/> đổi — VM nghe (marshal về UI thread).</summary>
    public event Action? Changed;

    /// <summary>Vòng chạy tự động đang hoạt động.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Dòng mô tả tiến trình hiện tại ("Lô 2/5 — đang xử lý...", "Nghỉ 15' trước lô kế", ...); null = chưa có.</summary>
    public string? CurrentPhase => _currentPhase;

    /// <summary>
    /// Bắt đầu vòng chạy tự động (idempotent: đang chạy → no-op). Vòng lặp chạy dưới <see cref="Task.Run"/> +
    /// <see cref="CancellationTokenSource"/>; đọc cấu hình mới nhất từ DB ở đầu mỗi lượt.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _isRunning = true;
            _currentPhase = "Đang khởi động...";
            _loopTask = Task.Run(() => RunLoopAsync(ct));
        }

        RaiseChanged();
    }

    /// <summary>
    /// Dừng vòng chạy: hủy token (cắt mọi điểm chờ), ĐÓNG ngay các phiên do mình mở (kill Brave → hành động
    /// đang chạy trả về sớm) rồi CHỜ vòng lặp thoát sạch. Idempotent: chưa chạy → no-op.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loopTask;
        }

        if (cts is null)
        {
            return; // chưa từng Start / đã dừng
        }

        _currentPhase = "Đang dừng...";
        RaiseChanged();

        try { cts.Cancel(); } catch { /* bỏ qua */ }

        // Đóng ngay các phiên do mình mở để mọi hành động đang chạy (không nhận ct) trả về sớm → vòng thoát nhanh.
        CloseAllOpenedByMe();

        if (loop is not null)
        {
            // KHÔNG chờ vô hạn (mẫu AccountSession.StopAsync): loop tự thoát NỀN sau khi hành động hiện tại
            // xong (ct đã hủy → kiểm ct trước hành động kế → thoát). Nhờ đó app phản hồi Dừng / thoát ngay,
            // KHÔNG treo UI thread khi shutdown gọi StopAsync đồng bộ.
            try { await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false); }
            catch { /* vòng đã tự nuốt; phòng hờ */ }
        }

        lock (_gate)
        {
            _cts = null;
            _loopTask = null;
        }

        _isRunning = false;
        _currentPhase = "Đã dừng.";
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>Đặt <see cref="CurrentPhase"/> + phát <see cref="Changed"/> (bỏ qua nếu không đổi).</summary>
    private void SetPhase(string? phase)
    {
        if (_currentPhase == phase)
        {
            return;
        }

        _currentPhase = phase;
        RaiseChanged();
    }

    /// <summary>Vòng lặp ngoài: lặp liên tục tới khi hủy. Mỗi lượt đọc snapshot mới rồi chạy từng lô.</summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        _services.Log.Append(LogSource, "Bắt đầu chạy tự động.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var cfg = _services.Settings.GetAutoRunSettings();

                // Snapshot TẤT CẢ tài khoản đầu mỗi lượt (đón thêm/xóa/đổi giữa chừng).
                var accounts = _services.Accounts.GetAll()
                    .Select(a => (a.Id, a.Email))
                    .ToList();

                if (accounts.Count == 0)
                {
                    SetPhase($"Chưa có tài khoản nào — nghỉ {cfg.GapMinutes}' rồi kiểm lại.");
                    _services.Log.Append(LogSource, "Chưa có tài khoản nào để chạy — nghỉ rồi kiểm lại.");
                    await DelayMinutesAsync(cfg.GapMinutes, ct).ConfigureAwait(false);
                    continue;
                }

                var batches = AutoRunBatcher.Split(accounts, cfg.BatchSize);
                for (var i = 0; i < batches.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = batches[i];
                    SetPhase($"Lô {i + 1}/{batches.Count} — đang xử lý {batch.Count} tài khoản.");
                    _services.Log.Append(LogSource,
                        $"Lô {i + 1}/{batches.Count}: {string.Join(", ", batch.Select(b => b.Email))}");

                    await RunBatchAsync(batch, cfg, ct).ConfigureAwait(false);

                    ct.ThrowIfCancellationRequested();
                    SetPhase($"Nghỉ {cfg.GapMinutes}' trước lô kế.");
                    await DelayMinutesAsync(cfg.GapMinutes, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dừng theo yêu cầu — thoát sạch.
        }
        catch (Exception ex)
        {
            _services.Log.Append(LogSource, $"Vòng chạy tự động lỗi: {ex.Message}");
        }
        finally
        {
            // Backstop: đóng mọi phiên do mình mở còn sót (dừng giữa lô / lỗi).
            CloseAllOpenedByMe();
            _isRunning = false;
            _currentPhase = ct.IsCancellationRequested ? "Đã dừng." : "Đã dừng (kết thúc).";
            _services.Log.Append(LogSource, "Đã dừng chạy tự động.");
            RaiseChanged();
        }
    }

    /// <summary>
    /// Chạy một lô: các tài khoản chạy SONG SONG, mỗi tài khoản ĐỘC LẬP LỖI. Xong hết → ĐÓNG các phiên do
    /// CHÍNH scheduler mở trong lô (phiên người dùng tự mở giữ nguyên) rồi chờ đóng sạch.
    /// </summary>
    private async Task RunBatchAsync(
        IReadOnlyList<(long Id, string Email)> batch, AutoRunSettings cfg, CancellationToken ct)
    {
        var runs = batch.Select(acc => RunAccountAsync(acc.Id, acc.Email, cfg, ct));
        await Task.WhenAll(runs).ConfigureAwait(false);

        // Phiên do MÌNH mở trong lô này (đọc từ _openedByMe — phiên người dùng tự mở không có trong đó).
        var mine = batch.Where(b => _openedByMe.ContainsKey(b.Id)).ToList();

        // PHÁT HIỆN "TK chưa xác nhận" TRƯỚC khi đóng: soi trang hiện tại của từng phiên do mình mở; còn kẹt
        // ở verify/login/captcha → đánh dấu; đã đăng nhập (LoggedIn) → gỡ cờ. Best-effort, KHÔNG chặn đóng phiên
        // (bọc try/catch bao trùm: mọi lỗi bất ngờ vẫn để vòng đóng phiên bên dưới chạy).
        try
        {
            await ProbeUnverifiedBeforeCloseAsync(mine).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Append(LogSource, $"Phát hiện TK chưa xác nhận gặp lỗi (bỏ qua): {ex.Message}");
        }

        // Đóng phiên do MÌNH mở.
        foreach (var (id, _) in mine)
        {
            _services.Sessions.Stop(id);
            _openedByMe.TryRemove(id, out _);
        }

        await WaitSessionsClosedAsync(mine.Select(m => m.Id).ToList(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cuối lô, TRƯỚC khi đóng các phiên do autorun mở: soi trạng thái trang hiện tại của từng phiên
    /// (<see cref="IAccountSession.ProbePageStateAsync"/> — read-only). Kết quả:
    /// <list type="bullet">
    /// <item>Verify / LoginForm / Captcha (<see cref="LaTrangThaiChuaXacNhan"/>) → đánh dấu
    /// <see cref="AccountRepository.MarkVerifyFailed"/> + log per-acc rõ.</item>
    /// <item>LoggedIn → <see cref="AccountRepository.ClearVerifyFailed"/> (phiên tốt thì gỡ nhãn cũ nếu có).</item>
    /// <item>Unknown / null (không kết luận được / đang navigating) → KHÔNG đổi cờ.</item>
    /// </list>
    /// Toàn khối best-effort try/catch per-acc: lỗi probe KHÔNG chặn việc đóng phiên. Có bất kỳ thay đổi cờ nào
    /// → phát <see cref="AppServices.RaiseAccountsChanged"/> một lần để màn Tài khoản làm mới nhãn/bộ lọc. Kèm
    /// một dòng log tổng cấp scheduler liệt kê các TK vừa bị đánh dấu trong lô.
    /// </summary>
    private async Task ProbeUnverifiedBeforeCloseAsync(IReadOnlyList<(long Id, string Email)> sessions)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        var newlyFailed = new List<string>();
        var anyChanged = false;

        foreach (var (id, email) in sessions)
        {
            try
            {
                var session = _services.Sessions.Get(id);
                if (session is null)
                {
                    continue;
                }

                var state = await session.ProbePageStateAsync().ConfigureAwait(false);
                if (state is null)
                {
                    continue; // đang navigating / lỗi / phiên đã đóng → không kết luận
                }

                if (LaTrangThaiChuaXacNhan(state.Value))
                {
                    _services.Accounts.MarkVerifyFailed(id, DateTime.UtcNow);
                    newlyFailed.Add(email);
                    anyChanged = true;
                    _services.Log.Append(email,
                        $"KHÔNG tự xác minh được — phiên vẫn ở trang {MoTaTrang(state.Value)} khi kết thúc lượt. Đánh dấu: TK chưa xác nhận.");
                }
                else if (state.Value == ShopeePageState.LoggedIn)
                {
                    // Phiên tốt → gỡ nhãn cũ nếu có (ClearVerifyFailed trả >0 khi thực sự gỡ một cờ đang bật).
                    if (_services.Accounts.ClearVerifyFailed(id) > 0)
                    {
                        anyChanged = true;
                    }
                }
                // Unknown → KHÔNG đổi cờ (không kết luận bừa).
            }
            catch
            {
                // Lỗi probe/ghi DB của MỘT tài khoản KHÔNG chặn việc đóng phiên hay các tài khoản khác.
            }
        }

        if (newlyFailed.Count > 0)
        {
            _services.Log.Append(LogSource,
                $"Lô này có {newlyFailed.Count} TK chưa xác nhận: {string.Join(", ", newlyFailed)}");
        }

        if (anyChanged)
        {
            _services.RaiseAccountsChanged(); // màn Tài khoản làm mới nhãn đỏ + bộ lọc
        }
    }

    /// <summary>
    /// Phân loại trạng thái trang cuối lượt thành "TK chưa xác nhận được" (hàm THUẦN, test được): phiên còn kẹt
    /// ở <see cref="ShopeePageState.Verify"/> / <see cref="ShopeePageState.LoginForm"/> /
    /// <see cref="ShopeePageState.Captcha"/> = chưa đăng nhập được → <c>true</c>.
    /// <see cref="ShopeePageState.LoggedIn"/> / <see cref="ShopeePageState.Unknown"/> → <c>false</c> (không
    /// kết luận bừa: Unknown = trang trắng/đang load).
    /// </summary>
    public static bool LaTrangThaiChuaXacNhan(ShopeePageState state)
        => state is ShopeePageState.Verify or ShopeePageState.LoginForm or ShopeePageState.Captcha;

    /// <summary>Mô tả NGẮN trang chặn để ghi vào log per-acc.</summary>
    private static string MoTaTrang(ShopeePageState state) => state switch
    {
        ShopeePageState.Verify => "xác minh (verify)",
        ShopeePageState.LoginForm => "đăng nhập (login)",
        ShopeePageState.Captcha => "captcha",
        _ => state.ToString(),
    };

    /// <summary>
    /// Một tài khoản trong lô. CHỈ xử lý phiên do CHÍNH autorun mở: nếu tài khoản ĐANG có phiên (người dùng tự
    /// mở) → BỎ QUA lượt này (tránh giẫm thao tác tay VÀ tránh chạy hành động dài trên phiên có ct riêng —
    /// autorun không cắt được). Ngược lại: tự mở phiên (đánh dấu "do mình mở" để đóng sau) → chờ sẵn sàng (tối
    /// đa 5') → chạy các hành động theo thứ tự (Xử lý → Sync → Kiểm tra) theo cờ cấu hình. Bọc try/catch riêng:
    /// một tài khoản lỗi/timeout KHÔNG phá lô; hủy (OCE) thoát êm.
    /// </summary>
    private async Task RunAccountAsync(long id, string email, AutoRunSettings cfg, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Đọc trạng thái TRƯỚC khi làm gì: đang chạy (Opening/Running) = phiên NGƯỜI DÙNG tự mở → BỎ QUA
            // lượt này (giữ nguyên phiên tay; hành động của họ có ct riêng, autorun không cắt được nên không
            // đụng vào). Lượt sau, nếu người dùng đã đóng phiên, autorun sẽ tự mở lại + xử lý.
            if (_services.Sessions.IsRunning(id))
            {
                _services.Log.Append(email,
                    "Chạy tự động: bỏ qua lượt này — đang có phiên bạn tự mở (tránh giẫm thao tác tay; sẽ xử lý ở lượt sau khi tự động mở phiên).");
                return;
            }

            // Chưa chạy = autorun tự mở → đánh dấu "do mình mở" để đóng sau lô.
            var session = _services.Sessions.Start(id);
            _openedByMe.TryAdd(id, 0);
            _services.Log.Append(email, "Chạy tự động: mở phiên...");

            var ready = await WaitForReadyAsync(session, ReadyTimeout, ct).ConfigureAwait(false);
            if (ready != ReadyResult.Ready)
            {
                var why = ready == ReadyResult.Ended
                    ? (session.LastError ?? session.StatusText ?? "phiên đã dừng")
                    : "chưa sẵn sàng sau 5 phút (có thể cần đăng nhập tay)";
                _services.Log.Append(email, $"Chạy tự động: bỏ qua lượt này — {why}.");
                return;
            }

            foreach (var kind in AutoRunPlan.ActionsFor(cfg.DoProcess, cfg.DoSync))
            {
                ct.ThrowIfCancellationRequested();
                switch (kind)
                {
                    case AutoRunActionKind.Process:
                        await session.ProcessOrdersAsync().ConfigureAwait(false);
                        break;
                    case AutoRunActionKind.Sync:
                        await session.SyncOrdersAsync().ConfigureAwait(false);
                        break;
                    case AutoRunActionKind.Check:
                        await session.CheckOrdersAsync().ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dừng — thoát êm (không phá WhenAll của lô).
        }
        catch (Exception ex)
        {
            // Một tài khoản lỗi KHÔNG phá lô — nuốt + log theo email của shop đó.
            _services.Log.Append(email, $"Chạy tự động lỗi: {ex.Message}");
        }
    }

    /// <summary>Kết quả chờ phiên "sẵn sàng thao tác".</summary>
    private enum ReadyResult
    {
        /// <summary>Sẵn sàng (Running + đã đăng nhập + đọc số đơn lần đầu).</summary>
        Ready,

        /// <summary>Phiên kết thúc giữa chừng (Stopped/Error).</summary>
        Ended,

        /// <summary>Quá thời hạn chờ.</summary>
        Timeout
    }

    /// <summary>
    /// Chờ tới khi <paramref name="session"/> "sẵn sàng thao tác" (State==Running &amp;&amp;
    /// <see cref="IAccountSession.ReadyForActions"/>), poll mỗi giây, tối đa <paramref name="timeout"/>. Phiên
    /// chuyển Stopped/Error → trả <see cref="ReadyResult.Ended"/> ngay. Hủy → ném OCE (cắt điểm chờ này).
    /// </summary>
    private static async Task<ReadyResult> WaitForReadyAsync(
        IAccountSession session, TimeSpan timeout, CancellationToken ct, int pollMs = 1000)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var state = session.State;
            if (state is SessionState.Stopped or SessionState.Error)
            {
                return ReadyResult.Ended;
            }

            if (state == SessionState.Running && session.ReadyForActions)
            {
                return ReadyResult.Ready;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return ReadyResult.Timeout;
            }

            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Chờ các phiên <paramref name="ids"/> đóng sạch (không còn <c>IsRunning</c>), tối đa
    /// <see cref="CloseTimeout"/>. Hủy → trả về SỚM (KHÔNG ném) để bước dọn lô hoàn tất; vòng ngoài sẽ tự thoát.
    /// </summary>
    private async Task WaitSessionsClosedAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var deadline = DateTime.UtcNow + CloseTimeout;
        while (ids.Any(id => _services.Sessions.IsRunning(id)))
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline)
            {
                return;
            }

            try { await Task.Delay(300, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Đóng (Stop) mọi phiên còn đánh dấu "do mình mở" và gỡ khỏi set. Best-effort, không chờ.</summary>
    private void CloseAllOpenedByMe()
    {
        foreach (var id in _openedByMe.Keys.ToList())
        {
            _services.Sessions.Stop(id);
            _openedByMe.TryRemove(id, out _);
        }
    }

    /// <summary>Nghỉ <paramref name="minutes"/> phút (≥1), hủy được qua <paramref name="ct"/>.</summary>
    private static Task DelayMinutesAsync(int minutes, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMinutes(minutes < 1 ? 1 : minutes), ct);
}
