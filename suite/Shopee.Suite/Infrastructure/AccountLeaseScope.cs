using Shopee.Core.Accounts;
using Shopee.Core.Coordination;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Gói VÒNG ĐỜI "khóa tk Shopee xuyên máy" (account-lease) dùng chung cho Scrape + Search — trước đây là 2 bản
/// mirror gần y hệt trong <c>ScrapeViewModel.RunOneJobAsync</c> ⇄ <c>SearchViewModel.RunAssignmentAsync</c>:
///  • theo dõi danh sách tk đang giữ lease Hub (<c>_hubLeased</c>) dưới MỘT khóa (idiom snapshot-under-lock),
///  • heartbeat nền 60s (chống lease hết hạn 5' giữa chunk dài — không lệ thuộc tốc độ chạy),
///  • BÙ TK thay thế khi 1 tk dính captcha (<see cref="AccountReplenisher"/>),
///  • <see cref="DisposeAsync"/> nhả ĐÚNG THỨ TỰ: dừng heartbeat → UnmarkHubLeased → ReleaseAccountsAsync (Hub)
///    → ReleaseReservation (giữ-chỗ cục bộ).
///
/// Hai module GIÀNH tk theo 2 cách KHÁC nhau nên phần giành được tham số hoá, KHÔNG ép chung:
///  • <b>Scrape (đóng khung)</b>: <c>s.ClaimFrame</c> đã <c>TryReserve</c> CẢ khung cục bộ. Dùng
///    <see cref="ForFrame"/> (giữ giữ-chỗ cục bộ tới lúc nhả) rồi <see cref="ReserveHubAsync"/> reserve Hub CẢ
///    khung 1 LẦN (bulk). Tk BÙ giữ luôn giữ-chỗ cục bộ (nhả ở Dispose).
///  • <b>Search (per-account)</b>: <see cref="AcquirePerAccountAsync"/> giành TỪNG tk (TryReserve tạm →
///    ReserveAccounts Hub từng cái → nhả giữ-chỗ cục bộ NGAY để lane borrow lại). KHÔNG giữ giữ-chỗ cục bộ ở
///    scope; tk BÙ cũng nhả giữ-chỗ cục bộ ngay.
///
/// <c>BeginRun</c>/<c>EndRun</c> vẫn do caller quản (bọc quanh cả lượt chạy, không thuộc scope này).
/// </summary>
public sealed class AccountLeaseScope : IAsyncDisposable
{
    private readonly HttpCoordinationHub? _hub;   // null = chạy như 1 máy (chỉ giữ-chỗ cục bộ, không lease Hub)
    private readonly object _lock = new();        // reservedLock: 2 list dưới đây bị BÙ TK thêm từ luồng khác
    private readonly List<string> _hubLeased = []; // tk Hub cấp lease → heartbeat + nhả + UnmarkHubLeased
    private readonly List<string> _localReserved = []; // tk giữ-chỗ cục bộ cần ReleaseReservation ở Dispose (Scrape)
    private readonly bool _holdsLocalReservation;
    private System.Threading.Timer? _heartbeat;
    private int _disposed;

    private AccountLeaseScope(HttpCoordinationHub? hub, bool holdsLocalReservation)
    {
        _hub = hub;
        _holdsLocalReservation = holdsLocalReservation;
    }

    // ── Scrape: đóng khung (ClaimFrame đã TryReserve cục bộ) ──────────────────────────────────────────
    /// <summary>Tạo scope cho 1 KHUNG tk đã được giữ-chỗ cục bộ (qua ClaimFrame). GIỮ giữ-chỗ cục bộ của CẢ
    /// khung tới lúc <see cref="DisposeAsync"/> nhả. Tạo NGAY (kể cả khi <paramref name="hub"/> null / offline)
    /// để mọi lối ra đều nhả giữ-chỗ cục bộ. Chưa đụng Hub — gọi <see cref="ReserveHubAsync"/> sau nếu có Hub.</summary>
    public static AccountLeaseScope ForFrame(HttpCoordinationHub? hub, IEnumerable<string> claimedFrameIds)
    {
        var scope = new AccountLeaseScope(hub, holdsLocalReservation: true);
        scope._localReserved.AddRange(claimedFrameIds);
        return scope;
    }

    /// <summary>Reserve Hub CẢ khung 1 LẦN: tk nào MÁY KHÁC đang giữ → không được cấp. Ghi tập ĐƯỢC CẤP vào sổ
    /// lease Hub (heartbeat + nhả), đánh dấu per-máy (MarkHubLeased) rồi bật heartbeat nền. Trả tập được cấp để
    /// caller lọc khung. Chỉ gọi khi có Hub (caller đã kiểm).</summary>
    public async Task<HashSet<string>> ReserveHubAsync(IReadOnlyCollection<string> frameIds)
    {
        var granted = await _hub!.ReserveAccountsAsync(frameIds).ConfigureAwait(false);
        lock (_lock) _hubLeased.AddRange(frameIds.Where(granted.Contains));
        ShopeeAccountUsage.Shared.MarkHubLeased(granted);   // dấu per-máy → module khác không cướp lease các tk này
        StartHeartbeat();
        return granted;
    }

    // ── Search: giành TỪNG tk ────────────────────────────────────────────────────────────────────────
    /// <summary>Giành ĐÚNG <paramref name="want"/> tk từ <paramref name="candidateIds"/> theo cơ chế per-account
    /// (khớp lease cục bộ + Hub từng cái, chống 2 module cùng máy xóa nhầm 1 dòng lease machine-scoped):
    ///  (1) né tk module khác cùng máy đang giữ (IsReserved / IsHubLeased);
    ///  (2) TryReserve cục bộ (chốt atomic tạm) rồi ReserveAccounts Hub;
    ///  (3) grant → MarkHubLeased TRƯỚC khi nhả chốt tạm (luôn ≥1 dấu suốt → module khác không cướp), rồi nhả
    ///      giữ-chỗ cục bộ để lane borrow lại kiểu per-borrow.
    /// Scope KHÔNG giữ giữ-chỗ cục bộ (đã nhả trong lúc giành). Trả về scope + danh sách tk giành được (để
    /// caller dựng specs). Chỉ gọi khi có Hub.</summary>
    public static async Task<(AccountLeaseScope Scope, List<string> Acquired)> AcquirePerAccountAsync(
        HttpCoordinationHub hub, IEnumerable<string> candidateIds, int want)
    {
        var scope = new AccountLeaseScope(hub, holdsLocalReservation: false);
        var acquired = new List<string>();
        foreach (var id in candidateIds)
        {
            if (acquired.Count >= want) break;
            if (ShopeeAccountUsage.Shared.IsHubLeased(id) || ShopeeAccountUsage.Shared.IsReserved(id)) continue;
            if (!ShopeeAccountUsage.Shared.TryReserve(id)) continue;
            HashSet<string> g;
            try { g = await hub.ReserveAccountsAsync(new[] { id }).ConfigureAwait(false); }
            catch { g = new HashSet<string>(StringComparer.Ordinal) { id }; }   // Hub lỗi → degrade như 1 máy
            if (g.Contains(id))
            {
                ShopeeAccountUsage.Shared.MarkHubLeased(new[] { id });   // dấu per-máy TRƯỚC khi nhả chốt tạm
                acquired.Add(id);
            }
            ShopeeAccountUsage.Shared.ReleaseReservation(id);   // nhả chốt tạm → lane per-borrow TryReserve lại
        }
        lock (scope._lock) scope._hubLeased.AddRange(acquired);
        if (acquired.Count > 0) scope.StartHeartbeat();
        return (scope, acquired);
    }

    // ── Bù tk thay thế ───────────────────────────────────────────────────────────────────────────────
    /// <summary>Xin 1 tk RẢNH từ kho chung (đã khóa lease xuyên máy qua <see cref="AccountReplenisher"/>) để bù
    /// khi 1 tk dính captcha/lỗi → job KHÔNG cạn khung phải chạy lại. Ghi vào sổ lease Hub (heartbeat + nhả ở
    /// Dispose). Scrape: giữ luôn giữ-chỗ cục bộ (nhả ở Dispose); Search: nhả giữ-chỗ cục bộ NGAY để lane borrow
    /// như tk nhóm ban đầu. null = kho hết tk dư (caller giữ hành vi cũ).</summary>
    public async Task<ShopeeAccount?> AcquireReplacementAsync(IReadOnlyCollection<string> excludeIds, CancellationToken ct)
    {
        var repl = await AccountReplenisher.TryAcquireSpareAsync(excludeIds, _hub, ct).ConfigureAwait(false);
        if (repl is null) return null;
        if (_holdsLocalReservation)
        {
            lock (_lock)
            {
                _localReserved.Add(repl.Id);                       // nhả giữ-chỗ cục bộ ở Dispose
                if (_hub is not null) _hubLeased.Add(repl.Id);     // heartbeat + nhả lease Hub ở Dispose
            }
        }
        else
        {
            lock (_lock) _hubLeased.Add(repl.Id);                  // lease Hub → heartbeat + nhả ở Dispose
            ShopeeAccountUsage.Shared.ReleaseReservation(repl.Id); // nhả giữ-chỗ cục bộ → lane borrow bình thường
        }
        return repl;
    }

    // Heartbeat account-lease theo TIMER nền → khỏi hết hạn 5' giữa chunk dài. Snapshot dưới khóa vì tk BÙ có
    // thể được thêm vào _hubLeased trong lúc chạy.
    private void StartHeartbeat()
    {
        _heartbeat = new System.Threading.Timer(_ =>
        {
            List<string> snap; lock (_lock) snap = _hubLeased.ToList();
            if (snap.Count > 0) { try { _ = _hub!.HeartbeatAccountsAsync(snap); } catch { } }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>Nhả toàn bộ lease đã giữ ĐÚNG THỨ TỰ (idempotent). Snapshot dưới khóa (khung/nhóm ban đầu + tk
    /// BÙ) → không rò. ReleaseAccountsAsync với list rỗng là no-op nên khỏi cần guard count.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_heartbeat is not null) { try { await _heartbeat.DisposeAsync().ConfigureAwait(false); } catch { } }
        List<string> hubToRelease, localToRelease;
        lock (_lock) { hubToRelease = _hubLeased.ToList(); localToRelease = _localReserved.ToList(); }
        ShopeeAccountUsage.Shared.UnmarkHubLeased(hubToRelease);   // gỡ dấu per-máy TRƯỚC/CÙNG khi nhả lease Hub
        if (_hub is not null) { try { await _hub.ReleaseAccountsAsync(hubToRelease).ConfigureAwait(false); } catch { } }
        if (_holdsLocalReservation) ShopeeAccountUsage.Shared.ReleaseReservation(localToRelease);
    }
}
