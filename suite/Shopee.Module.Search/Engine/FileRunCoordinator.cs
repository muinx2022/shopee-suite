using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Shopee.Core.Accounts;

namespace ShopeeStatApp.Services;

/// <summary>
/// Drives the parallel "search by category link" run: a pool of <see cref="SearchSession"/> lanes
/// pulls SELECTED links from a shared queue. Each link = 1 lane = 1 account, crawled in
/// "categoryFromLink" mode (mở category → mọi sub-category → lọc Nơi Bán + Bán chạy → cào). When a
/// link's account hits captcha/network errors it is rested and the lane borrows a different free
/// account to retry the SAME link. No account runs two lanes at once. All callbacks fire on
/// background threads — the UI marshals them onto the UI thread. Events keyed by the LINK string so
/// the UI can show 1 tab per link.
/// </summary>
public sealed class FileRunCoordinator
{
    public sealed record LinkItem(int Index, string Link, string SourceFile);

    private readonly AppSettingsService _appSettings;
    private readonly SearchTaskStore _taskStore;
    private readonly List<InstanceConfig> _accounts;   // nhóm tk của lượt chạy; BÙ TK có thể thêm vào (dưới _accLock)
    private readonly ConcurrentQueue<LinkItem> _linkQueue;
    private readonly int _laneCount;
    private readonly string _region;
    private readonly bool _resume;

    // Live sessions, for browser teardown on Stop / form close.
    private readonly object _sesLock = new();
    private readonly List<SearchSession> _sessions = [];

    // Account pool guarded by _accLock.
    private readonly object _accLock = new();
    private readonly HashSet<string> _busy = [];
    private readonly Dictionary<string, long> _restUntilTick = [];
    private readonly HashSet<string> _errored = new(StringComparer.OrdinalIgnoreCase);
    private const long RestMillis = 60_000;

    // BÙ TK THAY THẾ: khi mọi tk trong nhóm đã thử/lỗi (captcha), xin 1 tk RẢNH từ kho chung (đã khóa lease
    // xuyên máy) rồi thêm vào nhóm → link không "hết account" phải chạy lại. null = không bù (giữ hành vi cũ).
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<InstanceConfig?>>? _acquireReplacement;
    private readonly SemaphoreSlim _topUpGate = new(1, 1);
    private long _noReplUntilTick;   // Environment.TickCount64: backoff sau khi kho hết tk dư (khỏi dội Hub)

    // Nghỉ ngẫu nhiên giữa các link để giả lập người dùng + tránh Shopee soi traffic.
    private const int LinkRestMinMs = 8_000;
    private const int LinkRestMaxMs = 18_000;
    private static int RandomRest(int minMs, int maxMs) => Random.Shared.Next(minMs, maxMs + 1);

    // Per-link cancellation (✕ trên tab link đang chạy) + danh sách link bị bỏ qua (✕ khi còn chờ).
    private readonly object _linkCtsLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _linkCts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedLinks = new(StringComparer.Ordinal);

    // Events (key = link string).
    public event Action<string, string>? LinkStatus;                 // link, status/log message
    public event Action<string, ProductResult>? LinkProduct;         // link, product
    public event Action<string, string, string>? LinkAssigned;       // link, accountName, label
    public event Action<string, bool>? LinkConnection;               // link, connected
    public event Action<string>? LinkFinished;                       // link
    public event Action? AccountsChanged;
    public event Action<string>? AccountUsed;
    public event Action<string>? AccountLoggedIn;
    public event Action<string, string>? AccountErrored;

    /// <summary>Persist a finished link's products to Excel (linkLabel, products). Off the UI thread.</summary>
    public Func<string, IReadOnlyList<ProductResult>, Task>? SaveLinkExcel;

    public FileRunCoordinator(
        AppSettingsService appSettings,
        SearchTaskStore taskStore,
        IReadOnlyList<InstanceConfig> accounts,
        IEnumerable<LinkItem> links,
        int laneCount,
        string region,
        bool resume = false,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<InstanceConfig?>>? acquireReplacement = null)
    {
        _appSettings = appSettings;
        _taskStore = taskStore;
        _accounts = accounts.ToList();
        _linkQueue = new ConcurrentQueue<LinkItem>(links);
        _laneCount = Math.Max(1, laneCount);
        _region = region ?? "";
        _resume = resume;
        _acquireReplacement = acquireReplacement;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        ShopeeAccountUsage.Shared.BeginRun();   // bật cột "Tình trạng" (Đang/Đã/Chưa dùng)
        try
        {
            var workers = new List<Task>();
            for (var lane = 1; lane <= _laneCount; lane++)
            {
                var laneId = lane;
                workers.Add(Task.Run(() => WorkerLoopAsync(laneId, ct), ct));
            }
            await Task.WhenAll(workers);
        }
        finally { ShopeeAccountUsage.Shared.EndRun(); }
    }

    /// <summary>Best-effort synchronous kill of every lane's Brave window (Stop / form close).</summary>
    public void KillAllBrowsers()
    {
        lock (_sesLock)
            foreach (var s in _sessions)
                try { s.KillBrowser(); } catch { }
    }

    /// <summary>"✕" trên tab 1 link đang CHẠY: hủy đúng link đó (kill browser của lane đang xử lý nó).</summary>
    public void StopLink(string link)
    {
        lock (_linkCtsLock)
        {
            _skippedLinks.Add(link);
            if (_linkCts.TryGetValue(link, out var cts)) { try { cts.Cancel(); } catch { } }
        }
    }

    private bool IsSkipped(string link)
    {
        lock (_linkCtsLock) return _skippedLinks.Contains(link);
    }

    private async Task WorkerLoopAsync(int laneId, CancellationToken ct)
    {
        var session = new SearchSession(laneId, _appSettings, _taskStore);
        session.AccountStateChanged += () => AccountsChanged?.Invoke();
        session.AccountLoggedIn += id => AccountLoggedIn?.Invoke(id);
        lock (_sesLock) _sessions.Add(session);

        try
        {
            while (!ct.IsCancellationRequested && _linkQueue.TryDequeue(out var item))
            {
                if (IsSkipped(item.Link)) continue;

                using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_linkCtsLock) _linkCts[item.Link] = linkCts;
                // Gắn log/sản phẩm của session vào ĐÚNG link đang xử lý trên lane này.
                void OnLog(string m) => LinkStatus?.Invoke(item.Link, m);
                void OnProduct(ProductResult p) => LinkProduct?.Invoke(item.Link, p);
                void OnConn(bool c) => LinkConnection?.Invoke(item.Link, c);
                session.Log += OnLog;
                session.ProductFound += OnProduct;
                session.ConnectionChanged += OnConn;
                try
                {
                    await ProcessLinkAsync(session, laneId, item, linkCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    LinkStatus?.Invoke(item.Link, "Đã dừng link (chưa kết thúc).");
                }
                finally
                {
                    session.Log -= OnLog;
                    session.ProductFound -= OnProduct;
                    session.ConnectionChanged -= OnConn;
                    lock (_linkCtsLock) _linkCts.Remove(item.Link);
                    LinkFinished?.Invoke(item.Link);
                }

                if (!ct.IsCancellationRequested && !_linkQueue.IsEmpty)
                {
                    var rest = RandomRest(LinkRestMinMs, LinkRestMaxMs);
                    await Task.Delay(rest, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await session.DisposeAsync(); } catch { }
        }
    }

    private async Task ProcessLinkAsync(SearchSession session, int laneId, LinkItem item, CancellationToken ct)
    {
        var triedForLink = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reconnectStreak = 0;
        const int MaxAutoReconnects = 3;

        // Resume: tiếp tục đúng task dang dở của link này (giữ checkpoint danh mục/trang + SP đã cào).
        // Run thường: task mới (taskId=0). taskId tái dùng qua các lần đổi account để không mất tiến độ.
        long taskId = 0;
        var resumeCatIndex = 1;
        var resumePage = 1;
        if (_resume)
        {
            taskId = _taskStore.GetResumableTaskId(item.Link);
            if (taskId > 0 && _taskStore.GetTask(taskId) is { } rec)
            {
                resumeCatIndex = Math.Max(1, rec.ResumeCategoryIndex);
                resumePage = Math.Max(1, rec.CurrentPage);
                LinkStatus?.Invoke(item.Link, $"⏯ Tiếp tục từ danh mục #{resumeCatIndex}, trang {resumePage} (đã có {rec.ProductCount} SP).");
            }
        }

        InstanceConfig? account = null;
        try
        {
            var resolved = false;
            while (!resolved)
            {
                ct.ThrowIfCancellationRequested();
                if (account is null)
                {
                    account = await BorrowAccountAsync(triedForLink, ct);
                    if (account is null)
                    {
                        LinkStatus?.Invoke(item.Link, "Lỗi: hết account khả dụng.");
                        LinkAssigned?.Invoke(item.Link, "(hết account)", "");
                        return;
                    }
                    LinkAssigned?.Invoke(item.Link, account.DisplayName, "");
                }

                LinkStatus?.Invoke(item.Link, $"Đang crawl — \"{account.DisplayName}\"…");

                var cfg = new SearchConfig
                {
                    Mode = "categoryFromLink",
                    ProductLink = item.Link,
                    Keyword = item.Link,
                    RegionFilterText = _region,
                    MinPriceVnd = 0,
                    MinMonthlySold = 0,
                    CheckVariantStock = false,
                    ResumeCategoryIndex = resumeCatIndex,
                    ResumePage = resumePage,
                };

                SearchRunOutcome outcome;
                List<ProductResult> results;
                try
                {
                    outcome = await session.RunAsync(account, cfg, ct, taskId);
                    results = session.Results.ToList();
                    // Tái dùng cùng task + checkpoint cho lần thử kế (đổi account) → không cào lại từ đầu.
                    taskId = session.TaskId;
                    resumeCatIndex = Math.Max(1, session.LastCategoryIndex);
                    resumePage = Math.Max(1, session.LastPage);
                }
                catch (OperationCanceledException) { throw; }
                finally
                {
                    try { await session.CloseBrowserAsync(); } catch { }
                }

                if (outcome == SearchRunOutcome.Cancelled) return;

                if (outcome == SearchRunOutcome.Completed)
                {
                    if (results.Count > 0)
                    {
                        try { _taskStore.SaveShopProducts(CatId(item.Link), CatLabel(item.Link), item.Link, results); }
                        catch (Exception ex) { LinkStatus?.Invoke(item.Link, "Lưu CSDL lỗi: " + ex.Message); }
                        await SaveLinkOnceAsync(item.Link, results);
                    }
                    LinkStatus?.Invoke(item.Link, $"✔ Xong ({results.Count} sản phẩm).");
                    SetDone(item);
                    resolved = true;
                }
                else if (outcome == SearchRunOutcome.Reconnect)
                {
                    reconnectStreak++;
                    if (reconnectStreak <= MaxAutoReconnects)
                        LinkStatus?.Invoke(item.Link, $"Kết nối lại — \"{account.DisplayName}\" (lần {reconnectStreak}).");
                    else
                    {
                        reconnectStreak = 0;
                        triedForLink.Add(account.Id);
                        ReleaseAccount(account, rest: false);
                        account = null;
                        LinkStatus?.Invoke(item.Link, "Kết nối lại nhiều lần không được — đổi account.");
                    }
                }
                else if (outcome is SearchRunOutcome.CaptchaOrVerify or SearchRunOutcome.NetworkError)
                {
                    reconnectStreak = 0;
                    triedForLink.Add(account.Id);
                    if (outcome == SearchRunOutcome.CaptchaOrVerify)
                    {
                        MarkErrored(account, "Verify/captcha");
                        ReleaseAccount(account, rest: false);
                        LinkStatus?.Invoke(item.Link, $"\"{account.DisplayName}\" bị verify/captcha — đổi account, thử lại.");
                    }
                    else
                    {
                        ReleaseAccount(account, rest: true);
                        LinkStatus?.Invoke(item.Link, $"Lỗi ({outcome}) — đổi account, thử lại.");
                    }
                    account = null;
                }
                else // Error (link chết / không crawl được) → KHÔNG đổi account, bỏ qua link.
                {
                    var reason = string.IsNullOrWhiteSpace(session.LastError) ? outcome.ToString() : session.LastError!;
                    LinkStatus?.Invoke(item.Link, $"Lỗi: {reason} — bỏ qua link.");
                    resolved = true;
                }
            }
        }
        finally
        {
            if (account is not null) ReleaseAccount(account, rest: false);
        }
    }

    private void SetDone(LinkItem item)
    {
        try { new LinkFileStore(item.SourceFile).MarkStatus(item.Index, LinkFileStore.Processed); } catch { }
    }

    private async Task SaveLinkOnceAsync(string link, IReadOnlyList<ProductResult> results)
    {
        if (SaveLinkExcel is null) return;
        try { await SaveLinkExcel(CatLabel(link), results); }
        catch (Exception ex) { LinkStatus?.Invoke(link, "Lỗi lưu Excel: " + ex.Message); }
    }

    // ── Account pool ────────────────────────────────────────────────────────────
    private async Task<InstanceConfig?> BorrowAccountAsync(HashSet<string> tried, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            InstanceConfig? pick = null;
            bool noCandidates;
            lock (_accLock)
            {
                var candidates = _accounts.Where(a => !tried.Contains(a.Id) && !_errored.Contains(a.Id)).ToList();
                noCandidates = candidates.Count == 0;
                if (!noCandidates)
                {
                    var now = Environment.TickCount64;
                    // Chọn NGẪU NHIÊN trong nhóm khả dụng (free + hết nghỉ + KHÔNG bị module khác giữ chỗ) →
                    // không nện mãi mấy acc đầu, và 2 module (Scrape/Search) không mở cùng 1 tk Shopee.
                    var eligible = candidates
                        .Where(a => !_busy.Contains(a.Id) && !IsResting(a.Id, now) && !ShopeeAccountUsage.Shared.IsReserved(a.Id))
                        .ToList();
                    pick = eligible.Count > 0 ? eligible[Random.Shared.Next(eligible.Count)] : null;
                    // Giành quyền NGAY trong _accLock (TryReserve nguyên tử) — nếu module khác vừa giành mất thì bỏ, chờ lượt sau.
                    if (pick is not null && !ShopeeAccountUsage.Shared.TryReserve(pick.Id))
                        pick = null;
                    if (pick is not null)
                        _busy.Add(pick.Id);
                }
            }
            if (pick is not null)
            {
                AccountUsed?.Invoke(pick.Id);
                ShopeeAccountUsage.Shared.MarkInUse(pick.Id);
                return pick;
            }
            // Hết sạch ứng viên (mọi tk đã thử-cho-link-này / lỗi) → xin 1 tk THAY THẾ từ kho (khóa lease xuyên máy).
            if (noCandidates)
            {
                if (await TryTopUpAsync(ct).ConfigureAwait(false)) continue;   // đã bù → vòng lại chọn tk mới
                return null;   // không bù được (kho hết tk dư) → hết account thật
            }
            await Task.Delay(500, ct);
        }
        return null;
    }

    /// <summary>Xin 1 tk THAY THẾ từ kho chung (đã khóa lease) và thêm vào nhóm. true = đã bù (dùng chung cho
    /// mọi lane); false = kho hết tk dư (có backoff để khỏi dội Hub). Nối tiếp 1-tại-1-thời-điểm.</summary>
    private async Task<bool> TryTopUpAsync(CancellationToken ct)
    {
        if (_acquireReplacement is null) return false;
        if (Environment.TickCount64 < Interlocked.Read(ref _noReplUntilTick)) return false;   // backoff sau khi hết tk dư
        await _topUpGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyCollection<string> excludeIds;
            lock (_accLock) excludeIds = _accounts.Select(a => a.Id).ToArray();   // khỏi bù trùng tk đã có trong nhóm
            var repl = await _acquireReplacement(excludeIds, ct).ConfigureAwait(false);
            if (repl is null)
            {
                Interlocked.Exchange(ref _noReplUntilTick, Environment.TickCount64 + 20_000);   // kho hết tk dư → nghỉ 20s
                return false;
            }
            lock (_accLock)
            {
                if (_accounts.Any(a => a.Id == repl.Id)) return true;   // đã có (đề phòng trùng) → coi như thành công
                _accounts.Add(repl);
                _errored.Remove(repl.Id);   // tk mới toanh → đảm bảo không kẹt trong danh sách lỗi
            }
            AccountsChanged?.Invoke();
            return true;
        }
        finally { _topUpGate.Release(); }
    }

    private bool IsResting(string accountId, long now) =>
        _restUntilTick.TryGetValue(accountId, out var until) && now < until;

    private void MarkErrored(InstanceConfig account, string reason)
    {
        lock (_accLock) _errored.Add(account.Id);
        ShopeeAccountUsage.Shared.MarkCaptcha(account.Id);   // cột "Tình trạng" → "⚠ Captcha"
        AccountErrored?.Invoke(account.Id, reason);
    }

    private void ReleaseAccount(InstanceConfig account, bool rest)
    {
        lock (_accLock)
        {
            _busy.Remove(account.Id);
            if (rest)
                _restUntilTick[account.Id] = Environment.TickCount64 + RestMillis;
        }
        ShopeeAccountUsage.Shared.MarkReleased(account.Id);
        ShopeeAccountUsage.Shared.ReleaseReservation(account.Id);   // nhả giữ-chỗ → Scrape mượn được tk này
    }

    // ── Link helpers ────────────────────────────────────────────────────────────
    private static readonly Regex CatRx = new(@"-cat\.([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Mã danh mục cuối trong link (vd .../-cat.11035567.11035568 → 11035568). 0 nếu không có.</summary>
    public static long CatId(string link)
    {
        var m = CatRx.Match(link ?? "");
        if (!m.Success) return 0;
        var last = m.Groups[1].Value.Split('.').LastOrDefault(x => x.Length > 0) ?? "";
        return long.TryParse(last, out var v) ? v : 0;
    }

    /// <summary>Nhãn ngắn cho link (tên danh mục lấy từ slug) để hiển thị/đặt tên file Excel.</summary>
    public static string CatLabel(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return "category";
        try
        {
            var path = new Uri(link).AbsolutePath.Trim('/');
            var seg = path.Split('/').LastOrDefault() ?? path;
            var dash = seg.IndexOf("-cat.", StringComparison.OrdinalIgnoreCase);
            var name = dash > 0 ? seg[..dash] : seg;
            return string.IsNullOrWhiteSpace(name) ? "category" : Uri.UnescapeDataString(name);
        }
        catch { return "category"; }
    }

    private static readonly Regex IdRx = new(
        @"/product/(\d+)/(\d+)|-i\.(\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>shopId từ link sản phẩm (giữ tương thích cho LoadFileLinks/RescanShop). 0 nếu là link category.</summary>
    public static long ParseShopId(string link)
    {
        var m = IdRx.Match(link ?? "");
        if (!m.Success) return 0;
        var shop = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
        return long.TryParse(shop, out var s) ? s : 0;
    }
}
