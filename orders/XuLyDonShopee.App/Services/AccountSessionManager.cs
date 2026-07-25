using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Quản lý các <see cref="IAccountSession"/> đang chạy — MỖI TÀI KHOẢN MỘT PHIÊN ĐỘC LẬP, song song.
/// Thay cho cờ <c>IsBusy</c> toàn cục (khóa mọi tài khoản) trước đây: giờ mỗi tài khoản mở/dừng riêng.
/// <para>
/// Factory phiên tách được để test (ctor nhận <c>Func&lt;long, IAccountSession&gt;</c>): mặc định tạo
/// <see cref="AccountSession"/> thật; test truyền stub (không cần Brave).
/// </para>
/// </summary>
public class AccountSessionManager
{
    private readonly ConcurrentDictionary<long, IAccountSession> _sessions = new();
    private readonly Func<long, IAccountSession> _factory;
    private readonly object _gate = new();

    // ===== Hàng đợi 1-phiên-cầu-nối-một-lúc (Lỗi 2: cổng bridge 47821 cố định + KillBrowsersOnProfile giết chéo
    // trình duyệt của phiên khác) =====
    // Chỉ CHO PHÉP một phiên cầu nối CHIẾM SLOT (Opening/Running/Stopping) cùng lúc. Start khi slot đang bận →
    // account vào hàng đợi FIFO (State=Queued, "Chờ đến lượt"); khi phiên trước về Stopped THẬT SỰ (vòng nền tự đặt
    // sau khi tháo dỡ — xem AccountSession.StopAsync/Lỗi 5) thì tự start account kế. Thao tác dưới _gate.
    private readonly List<long> _queue = new();

    /// <summary>Trạng thái ĐANG CHIẾM SLOT cầu nối (đã mở/đang mở/đang tháo dỡ trình duyệt) — Queued KHÔNG tính
    /// (chưa mở trình duyệt).</summary>
    private static bool OccupiesSlot(SessionState s)
        => s is SessionState.Opening or SessionState.Running or SessionState.Stopping;

    /// <summary>Có phiên nào đang chiếm slot cầu nối không (giữ dưới _gate).</summary>
    private bool AnyOccupyingSlot()
        => _sessions.Values.Any(x => OccupiesSlot(x.State));

    /// <summary>Phát khi bất kỳ phiên nào đổi trạng thái — VM/UI nghe để cập nhật (marshal về UI thread).</summary>
    public event Action? Changed;

    /// <summary>Chuyển tiếp sự kiện "đã lưu cookie" của các phiên (kèm accountId) cho VM làm mới danh sách.</summary>
    public event Action<long>? CookieSaved;

    /// <summary>Ctor thật: tạo <see cref="AccountSession"/> chạy qua cầu nối extension (không dùng proxy).</summary>
    public AccountSessionManager(AppServices services)
    {
        _factory = id => new AccountSession(id, services);
    }

    /// <summary>Ctor test: cho phép thay factory phiên bằng stub.</summary>
    public AccountSessionManager(Func<long, IAccountSession> sessionFactory)
    {
        _factory = sessionFactory;
    }

    /// <summary>
    /// Bắt đầu (hoặc lấy) phiên cho tài khoản <paramref name="id"/>. Idempotent: gọi nhiều lần cùng id
    /// KHÔNG mở trùng — trả về đúng phiên đang có. KHÔNG khóa các tài khoản khác.
    /// </summary>
    public IAccountSession Start(long id)
    {
        IAccountSession session;
        bool launchNow = false;
        lock (_gate)
        {
            session = GetOrCreate(id);

            // Đã chạy/đang mở → idempotent no-op. Đã xếp hàng (Queued hoặc có trong _queue) → giữ nguyên.
            if (session.State is SessionState.Opening or SessionState.Running)
            {
                // đang chạy — không làm gì
            }
            else if (session.State == SessionState.Queued || _queue.Contains(id))
            {
                // đã ở hàng đợi — không làm gì
            }
            else if (AnyOccupyingSlot())
            {
                // Có phiên KHÁC (hoặc chính phiên này đang Stopping) đang chiếm slot → xếp hàng FIFO.
                if (!_queue.Contains(id))
                {
                    _queue.Add(id);
                }
                session.MarkQueued(); // Stopping/Opening/Running → MarkQueued tự bỏ qua (không hạ cấp)
            }
            else
            {
                launchNow = true; // slot trống (Stopped/Error, không ai chiếm) → chạy ngay
            }
        }

        // StartAsync tự idempotent; gọi NGOÀI _gate (StartAsync phát Changed đồng bộ → OnSessionChanged tái nhập lock).
        if (launchNow)
        {
            _ = session.StartAsync();
        }
        return session;
    }

    /// <summary>Lấy phiên trong dict hoặc tạo mới (đăng ký sự kiện MỘT LẦN). Gọi DƯỚI <see cref="_gate"/>.</summary>
    private IAccountSession GetOrCreate(long id)
    {
        if (_sessions.TryGetValue(id, out var existing))
        {
            return existing;
        }
        var created = _factory(id);
        created.Changed += () => OnSessionChanged(created);
        created.CookieSaved += accId => CookieSaved?.Invoke(accId);
        _sessions[id] = created;
        return created;
    }

    /// <summary>
    /// Dừng phiên của một tài khoản (nếu có). KHÔNG gỡ khỏi dictionary ngay: giữ <see cref="IsRunning"/>
    /// = true tới khi Brave chết THẬT (RunAsync finally đặt State→Stopped SAU khi dispose kill Brave). Nhờ
    /// đó nút "Mở" còn khóa trong lúc dừng → không mở lại vào CÙNG hồ sơ đang bị khóa (tránh Error khóa hồ
    /// sơ). Việc gỡ khỏi dictionary do <see cref="OnSessionChanged"/> làm (gỡ theo VALUE — xem Lỗi 1).
    /// </summary>
    public void Stop(long id)
    {
        IAccountSession? session;
        lock (_gate)
        {
            _queue.Remove(id); // rút khỏi hàng đợi nếu đang chờ tới lượt (Stop một account đang xếp hàng)
            _sessions.TryGetValue(id, out session);
        }

        if (session is not null)
        {
            // Queued → StopAsync hạ về Stopped ngay (OnSessionChanged gỡ khỏi dict). Đang chạy → tháo dỡ nền.
            _ = session.StopAsync(); // fire-and-forget: UI không phải chờ kill Brave (State→Stopped sẽ tự gỡ)
        }
    }

    /// <summary>Dừng TẤT CẢ phiên (dùng khi thoát app) — chờ kill hết Brave để không mồ côi.</summary>
    public async Task StopAllAsync()
    {
        List<IAccountSession> all;
        lock (_gate)
        {
            all = _sessions.Values.ToList();
            _sessions.Clear();
            _queue.Clear();
        }

        await Task.WhenAll(all.Select(SafeStopAsync)).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private static async Task SafeStopAsync(IAccountSession session)
    {
        try { await session.StopAsync().ConfigureAwait(false); }
        catch { /* bỏ qua khi thoát */ }
    }

    /// <summary>True nếu tài khoản có phiên ĐANG chuẩn bị/đang chạy (dùng để khóa nút theo TỪNG tài khoản).</summary>
    public bool IsRunning(long id)
        => _sessions.TryGetValue(id, out var s) && IsActiveState(s.State);

    /// <summary>Lấy phiên của một tài khoản (hoặc null nếu không có) — VM đọc trạng thái để hiển thị.</summary>
    public IAccountSession? Get(long id)
        => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>Các phiên đang chạy (đang chuẩn bị/đang chạy).</summary>
    public IReadOnlyCollection<IAccountSession> Active
        => _sessions.Values.Where(s => IsActiveState(s.State)).ToList();

    // "Đang hoạt động" cho khoá nút theo TỪNG tài khoản (IsRunning) + đếm Active: gồm cả Queued (đã bấm Chạy, chờ
    // tới lượt) và Stopping (đang tháo dỡ) — để nút Chạy khoá + nút Dừng còn bật (rút khỏi hàng / dừng hẳn).
    private static bool IsActiveState(SessionState state)
        => state is SessionState.Opening or SessionState.Running or SessionState.Queued or SessionState.Stopping;

    private void OnSessionChanged(IAccountSession session)
    {
        // Phiên đã kết thúc bình thường (đóng cửa sổ / dừng) → dọn khỏi danh sách. Phiên lỗi (Error) giữ
        // lại để còn hiển thị lỗi; người dùng bấm "Mở" lại sẽ chạy lại phiên đó.
        //
        // GỠ THEO (KEY, VALUE) — KHÔNG theo key đơn thuần: chỉ xóa khi ĐÚNG instance vừa phát event. Nếu
        // phiên cũ (A) phát Stopped TRỄ trong khi id đã được Start lại thành phiên mới (B) đang chạy, gỡ
        // theo key sẽ xóa NHẦM B (B mồ côi). Gỡ theo value: dict[id] == A mới xóa; == B thì bỏ qua (Lỗi 1).
        IAccountSession? toStart = null;
        lock (_gate)
        {
            if (session.State == SessionState.Stopped)
            {
                ((ICollection<KeyValuePair<long, IAccountSession>>)_sessions)
                    .Remove(new KeyValuePair<long, IAccountSession>(session.AccountId, session));
            }

            // Slot cầu nối vừa trống + còn account chờ tới lượt → lấy account đầu hàng đợi ra chạy (Lỗi 2).
            // KHÔNG rút khỏi _queue theo sự kiện Stopped: account có thể vừa bị Stop (đã rút ở Stop) hoặc là chính
            // account đang chờ để chạy lại (giữ trong hàng để nhánh này start). Chỉ Stop() mới rút khỏi hàng.
            if (_queue.Count > 0 && !AnyOccupyingSlot())
            {
                var nextId = _queue[0];
                _queue.RemoveAt(0);
                // Phiên có thể đã bị gỡ khỏi dict (Queued rồi Stopped, hoặc vừa Stopped ở nhánh trên) → tạo mới.
                toStart = GetOrCreate(nextId);
            }
        }

        // Start NGOÀI _gate (StartAsync phát Changed đồng bộ → OnSessionChanged tái nhập lock — lock re-entrant nhưng
        // vẫn tránh giữ lock qua lời gọi ngoài). Slot sẽ do phiên này chiếm (Opening) nên vòng sau không dequeue thêm.
        if (toStart is not null)
        {
            _ = toStart.StartAsync();
        }

        Changed?.Invoke();
    }
}
