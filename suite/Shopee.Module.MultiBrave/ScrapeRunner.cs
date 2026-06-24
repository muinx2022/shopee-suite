using System.Collections.Concurrent;
using OpenMultiBraveLauncherV3;
using Shopee.Core.Browser;

namespace Shopee.Modules.MultiBrave;

/// <summary>Mô tả một account Shopee + phần dòng được giao, để engine v31 mở Brave và scrape.</summary>
public sealed record ScrapeAccountSpec(
    string Id, string Label, string ShopeeAccountLogin, bool OpenWithShopeeAccount,
    string KiotProxyKey, string Region, string ProxyType, string ManualProxy, bool RequireProxy,
    string Sheet, int StartRow, int EndRow, string ShopeeProfileDir = "");

/// <summary>Kho tk Shopee DÙNG CHUNG cho auto-run: worker mượn 1 tk (nghỉ lâu nhất) TRƯỚC mỗi khối,
/// chạy xong TRẢ về kho → xoay vòng toàn kho, cho tk nghỉ luân phiên. Nhiều job BigSeller chia sẻ cùng
/// kho mà không tk nào bị 2 cửa sổ dùng một lúc (mượn = lấy khỏi kho). "Số process" = số worker (cửa sổ)
/// chạy song song, KHÔNG còn = số tk dùng.</summary>
public interface IScrapeAccountPool
{
    /// <summary>Mượn 1 tk nghỉ lâu nhất. Chờ nếu chưa có tk rảnh; null khi kho cạn hẳn (hết tk khả dụng).</summary>
    Task<ScrapeAccountSpec?> BorrowAsync(CancellationToken ct);
    /// <summary>Chạy ổn → trả tk về kho (đẩy xuống cuối hàng nghỉ).</summary>
    void Release(ScrapeAccountSpec spec);
    /// <summary>Lỗi proxy/tạm → cho tk nghỉ rồi tự quay lại kho. Trả (giây nghỉ, có "để dành" không) để log.</summary>
    AccountCooldown Cooldown(ScrapeAccountSpec spec);
    /// <summary>Captcha → loại tk khỏi kho lượt này (xử lý sau).</summary>
    void Quarantine(ScrapeAccountSpec spec);
}

public readonly record struct AccountCooldown(int Seconds, bool SetAside);

/// <summary>
/// Facade công khai bọc engine scrape v31 (BraveInstanceSession + LauncherRunnerLoop +
/// ExtensionRunnerAutomation — tất cả là internal). Dữ liệu workbook/video đọc native (không Python).
/// </summary>
public sealed class ScrapeRunner
{
    private readonly string _braveExe;
    private readonly string _sourceUserData;
    private readonly string _workbookPath;
    private readonly string _bigSellerAccountName;
    private readonly string _bigSellerKiotKey;
    private readonly string _bigSellerRegion;
    private readonly string _bigSellerProxyType;
    private readonly ConcurrentDictionary<string, BraveInstanceSession> _sessions = new();

    /// <summary>(key, dòng log). key = account.Id (manual) hoặc "P{slot}" (auto).</summary>
    public event Action<string, string>? InstanceLog;
    /// <summary>(key, trạng thái).</summary>
    public event Action<string, string>? InstanceStatus;
    /// <summary>(key, tên account, khối dòng) — auto: khi 1 slot nhận tk + khối mới.</summary>
    public event Action<string, string, string>? SlotAssigned;
    /// <summary>(accountId, tên account, lý do) — tk dính captcha/proxy lỗi, bị loại khỏi vòng xoay.</summary>
    public event Action<string, string, string>? AccountErrored;
    /// <summary>(from, to) — khoảng dòng vừa cào XONG (để lưu tiến độ resume). Báo theo từng chunk.</summary>
    public event Action<int, int>? RowsCompleted;
    /// <summary>(lý do) — tk BigSeller mất phiên ("log in first"). Toàn bộ job tk này bị dừng; cần đăng nhập lại BigSeller.</summary>
    public event Action<string>? BigSellerNeedLogin;

    public ScrapeRunner(string workbookPath, string videoOutputDir, string? braveExe = null, string sourceUserData = "", string bigSellerAccountName = "",
        string bigSellerKiotKey = "", string bigSellerRegion = "random", string bigSellerProxyType = "http")
    {
        // Workbook giữ PER-INSTANCE (mang qua InstanceConfig) để chạy song song nhiều BigSeller mỗi
        // workbook khác nhau. VideoOutputDir dùng chung mọi BigSeller nên vẫn để static.
        _workbookPath = workbookPath;
        _bigSellerAccountName = bigSellerAccountName;
        // Proxy RIÊNG của tk BigSeller (nếu có key) → mỗi instance đẩy bigseller.com qua IP này (split-tunnel).
        _bigSellerKiotKey = bigSellerKiotKey ?? "";
        _bigSellerRegion = string.IsNullOrWhiteSpace(bigSellerRegion) ? "random" : bigSellerRegion;
        _bigSellerProxyType = string.IsNullOrWhiteSpace(bigSellerProxyType) ? "http" : bigSellerProxyType;
        if (!string.IsNullOrWhiteSpace(videoOutputDir)) ScrapeNativeSettings.VideoOutputDir = videoOutputDir;
        _braveExe = braveExe ?? BrowserLauncher.Detect(BrowserKind.Brave)
            ?? throw new FileNotFoundException("Không tìm thấy brave.exe. Hãy cài Brave Browser.");
        _sourceUserData = sourceUserData;
    }

    // ── Manual: mỗi account 1 khối cố định (spec.StartRow..EndRow), tối đa N đồng thời ──
    public async Task RunAsync(
        IReadOnlyList<ScrapeAccountSpec> specs, string? bigSellerCookieFile, int maxConcurrent, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrent));
        // CTS phạm vi 1 job tk BigSeller: 1 lane thấy "log in first" → cancel để dừng mọi lane cùng tk.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var jct = jobCts.Token;
        var tasks = specs.Select(async (spec, index) =>
        {
            // Giãn khởi động lane đầu (tối đa maxConcurrent cái vào ngay) để không phóng Brave dồn cục.
            if (index > 0 && index < maxConcurrent)
            {
                try { await Task.Delay(index * LaunchStaggerMs, jct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            try { await gate.WaitAsync(jct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                var res = await RunChunkAsync(spec, spec.Id, spec.StartRow, spec.EndRow, bigSellerCookieFile, jct).ConfigureAwait(false);
                if (res.NeedLogin)
                {
                    InstanceLog?.Invoke(spec.Id, "⛔ " + res.Reason + " — DỪNG toàn bộ job tk BigSeller này.");
                    BigSellerNeedLogin?.Invoke(res.Reason);
                    try { jobCts.Cancel(); } catch { }
                }
                else if (res.Errored) AccountErrored?.Invoke(spec.Id, spec.Label, res.Reason);
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    // ── Auto (mô hình tiến trình động + vá): KHÔNG chia hết khối từ đầu. Có 1 đường biên (frontier)
    //    = dòng kế tiếp của tiến trình; worker rảnh thì lấy khối kế `[frontier, frontier+N]` và đẩy
    //    frontier lên (khối rộng N+1 dòng, nối tiếp — vd N=10: 1–11, 12–22, 23–33…). Khi 1 tk dừng
    //    GIỮA CHỪNG, phần còn lại thành việc VÁ (ưu tiên worker rảnh nhặt ngay), KHÔNG tính vào frontier.
    //    MỖI khối mượn 1 tk MỚI (nghỉ lâu nhất) từ KHO CHUNG `pool` rồi trả lại → xoay vòng toàn kho,
    //    cho tk nghỉ. Tk lỗi: CAPTCHA → pool.Quarantine (loại khỏi kho); PROXY/lỗi khác → pool.Cooldown
    //    (nghỉ rồi tự quay lại) → khối dở vá bằng tk khác. workerCount = số cửa sổ Brave song song. ──
    public async Task RunAutoAsync(
        IScrapeAccountPool pool, int workerCount, IReadOnlyList<(int from, int to)> segments,
        int rowsPerChunk, string? bigSellerCookieFile, CancellationToken ct)
    {
        if (segments.Count == 0 || workerCount <= 0) return;
        var per = Math.Max(1, rowsPerChunk);
        var work = new WorkAllocator(segments, per);

        var workers = Math.Max(1, workerCount);
        // CTS phạm vi 1 job tk BigSeller: nếu 1 worker thấy "log in first" (mất phiên) → cancel để DỪNG
        // mọi worker cùng tk (vá/retry vô nghĩa khi token đã chết). Linked với ct ngoài (nút Dừng).
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Đếm login-first LIÊN TIẾP toàn job (reset khi có chunk chạy được) — chỉ dừng job khi vượt ngưỡng.
        var needLoginStreak = new int[1];
        var tasks = Enumerable.Range(1, workers)
            .Select(slot => AutoWorkerAsync(slot, work, pool, bigSellerCookieFile, jobCts, needLoginStreak))
            .ToArray();
        await Task.WhenAll(tasks);

        // Run dừng mà vẫn còn dòng (vd mọi tk dính captcha) → báo RÕ toàn bộ đoạn chưa chạy để chạy lại.
        if (!ct.IsCancellationRequested)
        {
            var remaining = work.Remaining();
            if (remaining.Count > 0)
            {
                var ranges = string.Join(", ", remaining.OrderBy(r => r.from).Select(r => $"{r.from}–{r.to}"));
                InstanceLog?.Invoke("Auto", $"⚠ CÒN DÒNG CHƯA CHẠY (hết tk khả dụng / captcha): {ranges}. Xử lý captcha/tk rồi chạy lại từ các dòng này.");
            }
        }
    }

    // 3 lần liên tiếp KHÔNG tiến được dòng nào tại cùng 1 dòng bắt đầu → coi dòng đó hỏng, BỎ QUA
    // (cứu phần còn lại của khối). 1-2 lần có thể do captcha/proxy nhất thời nên vẫn retry.
    private const int MaxStallRetries = 3;

    // "Log in BigSeller first" từ 1 instance KHÔNG chắc tk BigSeller mất phiên TOÀN CỤC — thường chỉ 1
    // proxy bị BigSeller từ chối (token vẫn sống ở IP khác; kiểm chứng: đóng mẻ xong mở BigSeller tay vẫn
    // còn đăng nhập). CHỈ dừng cả job khi login-first xảy ra LIÊN TIẾP tới ngưỡng này (token thật sự chết →
    // re-import cũng vô ích). Dưới ngưỡng: trả phần dở thành vá + thử lại bằng tk/proxy khác, KHÔNG nuke mẻ.
    private const int MaxNeedLoginBeforeStop = 5;

    // Giãn khởi động giữa các lane: phóng tất cả Brave cùng lúc (thundering herd) làm nghẽn CPU,
    // chậm mở CDP port và khiến service worker MV3 dựng đồng loạt bị throttle → probe timeout.
    // Lệch mỗi lane ~1.5s khi BẮT ĐẦU (chỉ 1 lần lúc ramp-up; sau đó vẫn giữ đủ N lane song song).
    private const int LaunchStaggerMs = 1500;

    // CỔNG WARMUP: số instance được "dựng service worker (cold-start)" ĐỒNG THỜI. Đây là nút thắt thật
    // trên máy yếu — nhiều SW MV3 cold-start cùng lúc → tranh CPU → SW không lên → reopen/reload. Giới
    // hạn ở đây (mặc định 3) để chỉ vài cái dựng SW một lúc; cái nào SW lên thì THẢ cổng cho cái kế →
    // tổng số Brave chạy vẫn cao (30-50) mà SW vẫn lên ổn định, chỉ chậm lúc ramp. Chỉnh được từ ngoài.
    public static int WarmupSlots { get; set; } = 3;
    private static readonly SemaphoreSlim WarmupGate = new(Math.Max(1, WarmupSlots), Math.Max(1, WarmupSlots));

    private async Task AutoWorkerAsync(
        int slot, WorkAllocator work, IScrapeAccountPool pool, string? cookieFile,
        CancellationTokenSource jobCts, int[] needLoginStreak)
    {
        var ct = jobCts.Token;
        var key = "P" + slot;

        // Giãn lần phóng Brave ĐẦU TIÊN của mỗi lane để không dồn cục lúc bắt đầu.
        if (slot > 1)
        {
            try { await Task.Delay((slot - 1) * LaunchStaggerMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Lấy việc: ƯU TIÊN vá (phần dở của tk lỗi), rồi tới khối kế của tiến trình (frontier).
                var item = work.TryTake();
                if (item is null)
                {
                    if (work.AllDone()) break;                           // hết việc thật sự
                    await Task.Delay(500, ct).ConfigureAwait(false);     // worker khác đang chạy, có thể sinh vá
                    continue;
                }
                var (from, to, stall, isPatch) = item.Value;

                try
                {
                    // Mượn 1 tk MỚI (nghỉ lâu nhất) từ kho chung cho khối này → xoay vòng toàn kho, cho tk nghỉ.
                    var spec = await pool.BorrowAsync(ct).ConfigureAwait(false);
                    if (spec is null)
                    {
                        // Kho cạn hẳn (mọi tk captcha/lỗi/disabled) → trả việc lại, dừng worker.
                        work.AddPatch(from, to, stall);
                        InstanceStatus?.Invoke(key, "Hết tài khoản");
                        InstanceLog?.Invoke(key, $"⚠ Hết tk khả dụng (captcha/lỗi) — còn dòng {from}–{to} CHƯA chạy (xử lý rồi chạy lại).");
                        break;
                    }

                    SlotAssigned?.Invoke(key, spec.Label, $"{from}–{to}");
                    InstanceLog?.Invoke(key, $"→ tk {spec.Label}, dòng {from}–{to}{(isPatch ? " [vá]" : "")}");
                    var res = await RunChunkAsync(spec, key, from, to, cookieFile, ct, needLoginStreak).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) { pool.Release(spec); break; }

                    // Có chunk KHÔNG báo login-first → token tk BigSeller vẫn dùng được → reset chuỗi đếm.
                    if (!res.NeedLogin) Interlocked.Exchange(ref needLoginStreak[0], 0);

                    // "Log in first" từ 1 instance: thường chỉ 1 proxy bị BigSeller TỪ CHỐI, token tk BigSeller
                    // VẪN sống (kiểm chứng: đóng mẻ xong mở BigSeller tay vẫn còn đăng nhập). Nên KHÔNG nuke cả
                    // job ngay — trả phần dở thành vá + thử lại bằng tk/proxy khác (mở lại sẽ re-import token còn
                    // sống + lấy proxy mới). CHỈ dừng khi login-first LIÊN TIẾP tới ngưỡng (token thật sự chết).
                    if (res.NeedLogin)
                    {
                        var lastDoneNl = res.LastCompletedRow;
                        if (lastDoneNl >= from) RowsCompleted?.Invoke(from, Math.Min(lastDoneNl, to));
                        var nlFrom = Math.Max(from, lastDoneNl + 1);
                        if (nlFrom <= to) work.AddPatch(nlFrom, to, stall);

                        var streak = Interlocked.Increment(ref needLoginStreak[0]);
                        if (streak >= MaxNeedLoginBeforeStop)
                        {
                            pool.Release(spec);
                            InstanceLog?.Invoke(key, $"⛔ {res.Reason} — {streak} lần LIÊN TIẾP → DỪNG job (BigSeller thực sự mất phiên, cần đăng nhập lại).");
                            BigSellerNeedLogin?.Invoke(res.Reason);
                            try { jobCts.Cancel(); } catch { }
                            break;
                        }
                        pool.Cooldown(spec);   // cho tk Shopee nghỉ; phần dở vá bằng tk khác
                        InstanceLog?.Invoke(key,
                            $"⚠ {res.Reason} (lần {streak}/{MaxNeedLoginBeforeStop}) — có thể do proxy bị từ chối, token còn sống → " +
                            $"VÁ phần dở bằng tk khác (KHÔNG dừng cả mẻ).");
                        continue;
                    }

                    // Phân loại tk lỗi: CAPTCHA → loại khỏi kho (xử lý sau); PROXY/lỗi khác → cho nghỉ rồi
                    // tự quay lại kho; không lỗi → trả về kho ngay (phần dở sẽ vá bằng tk khác đang rảnh).
                    if (res.Errored)
                    {
                        if (res.IsCaptcha)
                        {
                            AccountErrored?.Invoke(spec.Id, spec.Label, res.Reason);
                            pool.Quarantine(spec);
                            InstanceStatus?.Invoke(key, "🚫 Captcha");
                            InstanceLog?.Invoke(key, $"🚫 {spec.Label}: captcha → loại khỏi kho, dùng tk khác.");
                        }
                        else
                        {
                            // Lỗi khác (proxy/đứt/tạm) → cho tk nghỉ. Lần 1 nghỉ ngắn (15s); lỗi ≥2 lần liên
                            // tiếp → nghỉ dài (90s) "để dành". Phần dở vá bằng tk khác đang rảnh (không chờ tk này).
                            var cd = pool.Cooldown(spec);
                            InstanceLog?.Invoke(key, $"⏸ {spec.Label}: {res.Reason} → cho tk nghỉ {cd.Seconds}s, vá phần dở bằng tk khác"
                                + (cd.SetAside ? " (lỗi 2 lần — nghỉ dài 90s)." : "."));
                        }
                    }
                    else pool.Release(spec);

                    // Chỉ XONG khi scrape tới `to`. Dừng giữa chừng (lỗi HAY không) → phần còn lại
                    // [lastDone+1, to] thành việc VÁ; không mất dòng, không chạy lại dòng đã xong.
                    var lastDone = res.LastCompletedRow;
                    // Báo phần đã cào xong của chunk này để lưu tiến độ (resume).
                    if (lastDone >= from) RowsCompleted?.Invoke(from, Math.Min(lastDone, to));
                    if (lastDone >= to) { InstanceLog?.Invoke(key, $"✓ Xong dòng {from}–{to} (tk {spec.Label})."); continue; }

                    var nextFrom = Math.Max(from, lastDone + 1);
                    var progressed = lastDone >= from;
                    var nextStall = progressed ? 0 : stall + 1;
                    if (nextStall >= MaxStallRetries)
                    {
                        InstanceLog?.Invoke(key,
                            $"⛔ Dòng {nextFrom} kẹt {MaxStallRetries} lần liên tiếp " +
                            $"({(string.IsNullOrWhiteSpace(res.Reason) ? "không tiến" : res.Reason)}) → BỎ QUA dòng {nextFrom}.");
                        nextFrom += 1;
                        nextStall = 0;
                    }
                    if (nextFrom <= to)
                    {
                        work.AddPatch(nextFrom, to, nextStall);
                        InstanceLog?.Invoke(key,
                            $"↻ Còn dòng {nextFrom}–{to} (đã xong tới {lastDone}) → VÁ bằng tk khác " +
                            (res.Errored ? $"[{res.Reason}]." : "[dừng giữa chừng]."));
                    }
                    else InstanceLog?.Invoke(key, $"✓ Xong dòng {from}–{to} (sau khi bỏ dòng kẹt).");
                }
                finally { work.Done(); } // khớp với inFlight++ trong TryTake
            }
            InstanceStatus?.Invoke(key, ct.IsCancellationRequested ? "Đã dừng" : "Xong");
        }
        catch (OperationCanceledException) { InstanceStatus?.Invoke(key, "Đã dừng"); }
        catch (Exception ex) { InstanceLog?.Invoke(key, "✘ " + ex.Message); }
    }

    private readonly record struct ChunkResult(bool Errored, string Reason, int LastCompletedRow, bool IsCaptcha, bool NeedLogin = false);

    /// <summary>Dòng cuối ĐÃ scrape xong của 1 chunk (đọc từ cfg do engine ghi per-row). Chưa xong
    /// dòng nào → from-1. Kẹp trong [from-1, to].</summary>
    private static int LastDoneOf(InstanceConfig cfg, int from, int to)
    {
        var last = cfg.LastCompletedRow ?? (from - 1);
        if (last < from - 1) last = from - 1;
        return Math.Min(last, to);
    }

    /// <summary>Chạy 1 khối dòng với 1 account (mở Brave → đăng nhập → extension → đóng). Đọc
    /// cfg.RunnerPhase/LastRunnerMessage sau khi xong để biết captcha/proxy lỗi.</summary>
    private async Task<ChunkResult> RunChunkAsync(
        ScrapeAccountSpec spec, string key, int from, int to, string? cookieFile, CancellationToken ct,
        int[]? needLoginStreak = null)
    {
        BraveInstanceSession? session = null;
        var cfg = BuildConfig(spec, from, to);
        cfg.WorkbookPath = _workbookPath;   // per-instance workbook (chạy song song nhiều BigSeller)
        cfg.BigSellerAccountName = _bigSellerAccountName;   // để overlay hiện "Bigseller Account: …"
        var port = PortAllocator.Shared.AllocateInstancePort();
        try
        {
            session = new BraveInstanceSession(port, line => InstanceLog?.Invoke(key, line));
            // Cổng warmup dùng CHUNG mọi instance: giới hạn số SW cold-start đồng thời.
            session.WarmupAcquire = WarmupGate.WaitAsync;
            session.WarmupRelease = () => WarmupGate.Release();
            _sessions[key] = session;
            session.StatusChanged += () => InstanceStatus?.Invoke(key, session!.StatusText);
            // Báo tiến độ theo TỪNG DÒNG (live) — engine cập nhật cfg.LastCompletedRow + bắn ExtensionProgressSynced
            // mỗi khi xong 1 link. Nhờ vậy Thống kê thấy ngay (kể cả khi đang chạy / khi dừng giữa chừng),
            // không phải đợi xong cả khối.
            var reportedRow = from - 1;
            session.ExtensionProgressSynced += () =>
            {
                var lc = cfg.LastCompletedRow ?? 0;
                if (lc > reportedRow && lc <= to)
                {
                    var lo = reportedRow + 1;
                    reportedRow = lc;
                    RowsCompleted?.Invoke(lo, lc);
                    // Cào được 1 DÒNG = tk BigSeller CÒN SỐNG → reset chuỗi đếm login-first. Nhờ vậy vài
                    // instance proxy xấu báo login-first KHÔNG dồn bộ đếm tới ngưỡng khi các instance khác
                    // vẫn cào ngon → job KHÔNG tự đóng. Chỉ dừng nếu THỰC SỰ không cào được gì + login-first dồn.
                    if (needLoginStreak is not null) Interlocked.Exchange(ref needLoginStreak[0], 0);
                }
            };
            if (!string.IsNullOrWhiteSpace(cookieFile)) session.SetBigSellerCookieFile(cookieFile);
            // Proxy riêng tk BigSeller (nếu có) → engine phân giải IP mỗi lần mở Brave + split-tunnel bigseller.com.
            session.SetBigSellerProxy(_bigSellerKiotKey, _bigSellerRegion, _bigSellerProxyType);
            // Import session Shopee đã đăng nhập (profile Edge của tk) → khỏi login form → tránh captcha.
            session.SetShopeeSessionProfileDir(spec.ShopeeProfileDir);
            session.ApplyConfig(cfg);

            InstanceStatus?.Invoke(key, "Mở Brave…");
            await session.StartAsync(_braveExe, _sourceUserData).ConfigureAwait(false);

            InstanceStatus?.Invoke(key, $"Scrape dòng {from}–{(to <= 0 ? "hết" : to.ToString())}…");
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Bỏ qua RunnerLoopEnded phát ra GIỮA lúc watchdog/proxy đang relaunch+resume cùng profile
            // (IsRunnerResuming); chỉ kết thúc khi runner thật sự xong — giống ShopeeWorkspaceControl v31.
            void OnEnded(string _) { if (session!.IsRunnerResuming) return; done.TrySetResult(); }
            session.RunnerLoopEnded += OnEnded;
            try
            {
                await session.ResumeContinueAsync(
                    _braveExe, _sourceUserData, preferSuggestedResume: true, retryExtensionStart: true, ct)
                    .ConfigureAwait(false);
                using (ct.Register(() => done.TrySetResult()))
                    await done.Task.ConfigureAwait(false);
            }
            finally { session.RunnerLoopEnded -= OnEnded; }

            var lastDone = LastDoneOf(cfg, from, to);
            if (ct.IsCancellationRequested) return new ChunkResult(false, "", lastDone, false);
            return Classify(cfg) with { LastCompletedRow = lastDone };
        }
        catch (OperationCanceledException) { InstanceStatus?.Invoke(key, "Đã dừng"); return new ChunkResult(false, "", LastDoneOf(cfg, from, to), false); }
        catch (Exception ex) { InstanceLog?.Invoke(key, "✘ " + ex.Message); return new ChunkResult(true, ex.Message, LastDoneOf(cfg, from, to), false); }
        finally
        {
            try { if (session is not null) await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { session?.Dispose(); } catch { }
            _sessions.TryRemove(key, out _);
            // Trả port về pool — nếu thiếu dòng này, mỗi chunk rò 1 port → auto run dài cạn pool (600 port) rồi chết.
            PortAllocator.Shared.Release(port);
        }
    }

    /// <summary>Phân loại kết quả từ InstanceConfig sau khi runner kết thúc.</summary>
    private static ChunkResult Classify(InstanceConfig cfg)
    {
        var phase = cfg.RunnerPhase ?? "";
        var msg = cfg.LastRunnerMessage ?? "";
        var captcha = cfg.CaptchaError
            || msg.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("verify", StringComparison.OrdinalIgnoreCase);
        var proxy = msg.Contains("proxy", StringComparison.OrdinalIgnoreCase);

        // LastCompletedRow=0 là placeholder — caller ghi đè bằng `with { LastCompletedRow = … }`.
        if (string.Equals(phase, "needlogin", StringComparison.OrdinalIgnoreCase))
            return new ChunkResult(true, string.IsNullOrWhiteSpace(msg) ? "BigSeller mất đăng nhập — cần đăng nhập lại" : msg, 0, false, NeedLogin: true);
        if (captcha) return new ChunkResult(true, "Captcha/verify", 0, true);   // IsCaptcha=true → quarantine
        if (proxy) return new ChunkResult(true, "Lỗi proxy", 0, false);
        if (string.Equals(phase, "error", StringComparison.OrdinalIgnoreCase))
            return new ChunkResult(true, string.IsNullOrWhiteSpace(msg) ? "Lỗi" : msg, 0, false);
        if (string.Equals(phase, "paused", StringComparison.OrdinalIgnoreCase))
            return new ChunkResult(true, string.IsNullOrWhiteSpace(msg) ? "Tạm dừng" : msg, 0, false);
        return new ChunkResult(false, "", 0, false);
    }

    private static InstanceConfig BuildConfig(ScrapeAccountSpec spec, int from, int to)
    {
        var cfg = new InstanceConfig
        {
            Id = spec.Id,
            Label = spec.Label,
            ShopeeAccountLogin = spec.ShopeeAccountLogin,
            OpenWithShopeeAccount = spec.OpenWithShopeeAccount,
            KiotProxyKey = spec.KiotProxyKey,
            Region = string.IsNullOrWhiteSpace(spec.Region) ? "random" : spec.Region,
            ProxyType = string.IsNullOrWhiteSpace(spec.ProxyType) ? "http" : spec.ProxyType,
            ManualProxy = spec.ManualProxy,
            RequireProxy = spec.RequireProxy,
            DataSheet = spec.Sheet,
            StartRow = from,
            EndRow = to,
            NextRunRow = from,
            AutoCloseProfileOnFinish = true,
        };
        cfg.EnsureProfileRelativePath();
        return cfg;
    }

    /// <summary>Dừng mọi phiên đang chạy (đóng Brave + lưu tiến độ).</summary>
    public async Task StopAllAsync()
    {
        foreach (var s in _sessions.Values.ToArray())
        {
            try { await s.StopRunningWorkAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Đưa cửa sổ Brave của 1 process (key = "P{slot}") lên trước toàn bộ — gọi khi click dòng tiến trình.</summary>
    public void BringInstanceToFront(string key)
    {
        if (_sessions.TryGetValue(key, out var session)) session.BringWindowToFront();
    }

    /// <summary>Cấp việc động cho auto-run: đường biên (frontier) cho TIẾN TRÌNH + hàng VÁ cho phần
    /// dở của tk lỗi (ưu tiên vá trước). Đếm việc đang chạy (inFlight) để biết khi nào THỰC SỰ hết.</summary>
    /// <summary>
    /// Cấp dòng theo DANH SÁCH KHOẢNG còn cần chạy (segments) — reset = [(start,total)]; resume = các
    /// khoảng còn thiếu. Mỗi segment được cắt thành khối rộng N+1 dòng (per+1) theo thứ tự. Vẫn hỗ trợ
    /// VÁ (patch) cho phần dở của tk lỗi/dừng giữa chừng.
    /// </summary>
    private sealed class WorkAllocator
    {
        private readonly object _lock = new();
        private readonly Queue<(int from, int to, int stall)> _patches = new();
        private readonly List<(int from, int to)> _segments;
        private readonly int _per;
        private int _segIdx;
        private int _cursor;     // dòng kế trong segment hiện tại
        private int _inFlight;

        public WorkAllocator(IEnumerable<(int from, int to)> segments, int per)
        {
            _per = Math.Max(1, per);
            _segments = segments.Where(s => s.to >= s.from).OrderBy(s => s.from).ToList();
            _segIdx = 0;
            _cursor = _segments.Count > 0 ? _segments[0].from : int.MaxValue;
        }

        /// <summary>Lấy việc kế: ưu tiên VÁ, rồi tới khối kế trong các segment (rộng N+1 dòng).
        /// null = hết việc khả dĩ ngay lúc này. inFlight++ khi trả việc.</summary>
        public (int from, int to, int stall, bool isPatch)? TryTake()
        {
            lock (_lock)
            {
                if (_patches.Count > 0)
                {
                    var p = _patches.Dequeue();
                    _inFlight++;
                    return (p.from, p.to, p.stall, true);
                }
                while (_segIdx < _segments.Count)
                {
                    var seg = _segments[_segIdx];
                    if (_cursor > seg.to)
                    {
                        _segIdx++;
                        if (_segIdx < _segments.Count) _cursor = _segments[_segIdx].from;
                        continue;
                    }
                    var from = _cursor;
                    var to = Math.Min(seg.to, from + _per);
                    _cursor = to + 1;
                    _inFlight++;
                    return (from, to, 0, false);
                }
                return null;
            }
        }

        public void AddPatch(int from, int to, int stall) { lock (_lock) _patches.Enqueue((from, to, stall)); }

        public void Done() { lock (_lock) _inFlight--; }

        /// <summary>Hết việc THẬT: hết segment + không còn vá + không worker nào đang chạy.</summary>
        public bool AllDone() { lock (_lock) return _segIdx >= _segments.Count && _patches.Count == 0 && _inFlight == 0; }

        /// <summary>Các đoạn dòng CHƯA chạy (phần còn lại của các segment + các đoạn vá) — để báo
        /// cho người dùng khi run dừng sớm (vd hết tk do captcha).</summary>
        public List<(int from, int to)> Remaining()
        {
            lock (_lock)
            {
                var list = new List<(int, int)>();
                for (var i = _segIdx; i < _segments.Count; i++)
                {
                    var seg = _segments[i];
                    var from = i == _segIdx ? Math.Max(_cursor, seg.from) : seg.from;
                    if (from <= seg.to) list.Add((from, seg.to));
                }
                foreach (var p in _patches) list.Add((p.from, p.to));
                return list;
            }
        }
    }
}
