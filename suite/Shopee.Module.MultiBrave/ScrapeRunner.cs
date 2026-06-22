using System.Collections.Concurrent;
using OpenMultiBraveLauncherV3;
using Shopee.Core.Browser;

namespace Shopee.Modules.MultiBrave;

/// <summary>Mô tả một account Shopee + phần dòng được giao, để engine v31 mở Brave và scrape.</summary>
public sealed record ScrapeAccountSpec(
    string Id, string Label, string ShopeeAccountLogin, bool OpenWithShopeeAccount,
    string KiotProxyKey, string Region, string ProxyType, string ManualProxy, bool RequireProxy,
    string Sheet, int StartRow, int EndRow, string ShopeeProfileDir = "");

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

    public ScrapeRunner(string workbookPath, string videoOutputDir, string? braveExe = null, string sourceUserData = "", string bigSellerAccountName = "")
    {
        // Workbook giữ PER-INSTANCE (mang qua InstanceConfig) để chạy song song nhiều BigSeller mỗi
        // workbook khác nhau. VideoOutputDir dùng chung mọi BigSeller nên vẫn để static.
        _workbookPath = workbookPath;
        _bigSellerAccountName = bigSellerAccountName;
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
        var tasks = specs.Select(async (spec, index) =>
        {
            // Giãn khởi động lane đầu (tối đa maxConcurrent cái vào ngay) để không phóng Brave dồn cục.
            if (index > 0 && index < maxConcurrent)
            {
                try { await Task.Delay(index * LaunchStaggerMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var res = await RunChunkAsync(spec, spec.Id, spec.StartRow, spec.EndRow, bigSellerCookieFile, ct).ConfigureAwait(false);
                if (res.Errored) AccountErrored?.Invoke(spec.Id, spec.Label, res.Reason);
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    // ── Auto (mô hình tiến trình động + vá): KHÔNG chia hết khối từ đầu. Có 1 đường biên (frontier)
    //    = dòng kế tiếp của tiến trình; N process rảnh thì lấy khối kế `[frontier, frontier+N]` và
    //    đẩy frontier lên (khối rộng N+1 dòng, nối tiếp — vd N=10: 1–11, 12–22, 23–33…). Khi 1 tk
    //    dừng GIỮA CHỪNG, phần còn lại thành việc VÁ (ưu tiên process rảnh nhặt ngay), KHÔNG tính
    //    vào frontier. Tk lỗi: CAPTCHA → quarantine (để riêng xử lý sau); PROXY/lỗi khác → cooldown
    //    90s rồi tự quay lại vòng. Nhờ vậy luôn còn tk nhận phần dở, không mất dòng. ──
    public async Task RunAutoAsync(
        IReadOnlyList<ScrapeAccountSpec> pool, IReadOnlyList<(int from, int to)> segments, int rowsPerChunk,
        int totalRows, int numProcesses, string? bigSellerCookieFile, CancellationToken ct,
        Func<Task<ScrapeAccountSpec?>>? borrowReplacement = null)
    {
        if (pool.Count == 0 || segments.Count == 0) return;
        var per = Math.Max(1, rowsPerChunk);
        var work = new WorkAllocator(segments, per);
        var rotation = new AccountRotation(pool);

        var workers = Math.Max(1, Math.Min(numProcesses, pool.Count));
        var tasks = Enumerable.Range(1, workers)
            .Select(slot => AutoWorkerAsync(slot, work, rotation, bigSellerCookieFile, borrowReplacement, ct))
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

    // Giãn khởi động giữa các lane: phóng tất cả Brave cùng lúc (thundering herd) làm nghẽn CPU,
    // chậm mở CDP port và khiến service worker MV3 dựng đồng loạt bị throttle → probe timeout.
    // Lệch mỗi lane ~1.5s khi BẮT ĐẦU (chỉ 1 lần lúc ramp-up; sau đó vẫn giữ đủ N lane song song).
    private const int LaunchStaggerMs = 1500;

    private async Task AutoWorkerAsync(
        int slot, WorkAllocator work, AccountRotation rotation, string? cookieFile,
        Func<Task<ScrapeAccountSpec?>>? borrowReplacement, CancellationToken ct)
    {
        // Mượn 1 tk bù từ kho còn lại rồi thêm vào vòng (khi tk cũ captcha/lỗi nhiều) — giữ đủ tk chạy.
        async Task RefillAsync(string reason)
        {
            if (borrowReplacement is null) return;
            var repl = await borrowReplacement().ConfigureAwait(false);
            if (repl is not null && rotation.Add(repl))
                InstanceLog?.Invoke("P" + slot, $"➕ Mượn bù tk \"{repl.Label}\" từ kho ({reason}).");
        }

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
                    var spec = await AcquireAsync(rotation, borrowReplacement, ct).ConfigureAwait(false);
                    if (spec is null)
                    {
                        // Hết tk khả dụng trong vòng VÀ kho cũng hết → trả việc lại, dừng worker.
                        work.AddPatch(from, to, stall);
                        InstanceStatus?.Invoke(key, "Hết tài khoản");
                        InstanceLog?.Invoke(key, $"⚠ Hết tk khả dụng (captcha/lỗi) — còn dòng {from}–{to} CHƯA chạy (xử lý rồi chạy lại).");
                        break;
                    }

                    SlotAssigned?.Invoke(key, spec.Label, $"{from}–{to}");
                    InstanceLog?.Invoke(key, $"→ tk {spec.Label}, dòng {from}–{to}{(isPatch ? " [vá]" : "")}");
                    var res = await RunChunkAsync(spec, key, from, to, cookieFile, ct).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) { rotation.Release(spec); break; }

                    // Phân loại tk lỗi: CAPTCHA → quarantine (để riêng xử lý sau); PROXY/lỗi khác →
                    // cooldown (tự quay lại vòng); không lỗi → trả về vòng ngay.
                    if (res.Errored)
                    {
                        if (res.IsCaptcha)
                        {
                            // Captcha → tách hẳn (xử lý sau) + MƯỢN BÙ tk khác để giữ đủ tk chạy.
                            AccountErrored?.Invoke(spec.Id, spec.Label, res.Reason);
                            rotation.Quarantine(spec);
                            InstanceLog?.Invoke(key, $"🚫 {spec.Label}: captcha → tách ra xử lý sau.");
                            await RefillAsync($"thay {spec.Label} dính captcha").ConfigureAwait(false);
                        }
                        else
                        {
                            // Lỗi khác (proxy/đứt/tạm) → cooldown rồi tự quay lại. Nếu đã lỗi ≥2 lần liên tiếp
                            // (nhiều khả năng proxy) → để dành tk này, MƯỢN tk khác chạy trước, lúc khác quay lại.
                            var setAside = rotation.Cooldown(spec);
                            InstanceLog?.Invoke(key, $"⏸ {spec.Label}: {res.Reason} → nghỉ {AccountRotation.CooldownSeconds}s rồi thử lại"
                                + (setAside ? " (đã lỗi 2 lần — để dành, mượn tk khác chạy trước)." : "."));
                            if (setAside) await RefillAsync($"thay {spec.Label} lỗi 2 lần").ConfigureAwait(false);
                        }
                    }
                    else rotation.Release(spec);

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

    private static async Task<ScrapeAccountSpec?> AcquireAsync(
        AccountRotation rotation, Func<Task<ScrapeAccountSpec?>>? borrowReplacement, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var s = rotation.TryAcquire();
            if (s is not null) return s;
            if (rotation.AllQuarantined)
            {
                // Mọi tk trong vòng đã captcha → thử MƯỢN BÙ từ kho; hết kho thật sự thì mới dừng.
                var extra = borrowReplacement is null ? null : await borrowReplacement().ConfigureAwait(false);
                if (extra is not null && rotation.Add(extra)) continue;
                return null;
            }
            await Task.Delay(800, ct).ConfigureAwait(false); // tk khác đang bận / cooldown → chờ quay lại
        }
        return null;
    }

    private readonly record struct ChunkResult(bool Errored, string Reason, int LastCompletedRow, bool IsCaptcha);

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
        ScrapeAccountSpec spec, string key, int from, int to, string? cookieFile, CancellationToken ct)
    {
        BraveInstanceSession? session = null;
        var cfg = BuildConfig(spec, from, to);
        cfg.WorkbookPath = _workbookPath;   // per-instance workbook (chạy song song nhiều BigSeller)
        cfg.BigSellerAccountName = _bigSellerAccountName;   // để overlay hiện "Bigseller Account: …"
        var port = PortAllocator.Shared.AllocateInstancePort();
        try
        {
            session = new BraveInstanceSession(port, line => InstanceLog?.Invoke(key, line));
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
                }
            };
            if (!string.IsNullOrWhiteSpace(cookieFile)) session.SetBigSellerCookieFile(cookieFile);
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

    /// <summary>Xoay vòng tk (round-robin): CAPTCHA → quarantine (tách hẳn, xử lý sau); PROXY/lỗi
    /// khác → cooldown rồi TỰ quay lại vòng. Lỗi ≥2 lần liên tiếp → báo "để dành" (caller mượn tk khác
    /// chạy trước, tk này quay lại sau cooldown). Hỗ trợ THÊM tk mượn bù vào vòng khi tk cũ hỏng.</summary>
    private sealed class AccountRotation
    {
        public const int CooldownSeconds = 90;
        private const int SetAsideAfterFails = 2;   // lỗi liên tiếp tới ngưỡng này → mượn tk khác chạy trước

        private readonly List<ScrapeAccountSpec> _pool;
        private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
        private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);            // captcha → xử lý sau
        private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal); // proxy/lỗi → tạm nghỉ
        private readonly Dictionary<string, int> _fail = new(StringComparer.Ordinal);           // lỗi liên tiếp (reset khi chạy được)
        private readonly object _lock = new();
        private int _cursor;

        public AccountRotation(IReadOnlyList<ScrapeAccountSpec> pool) => _pool = pool.ToList();

        /// <summary>Thêm tk MƯỢN BÙ vào vòng xoay (khi tk cũ dính captcha/lỗi). Bỏ qua nếu đã có.</summary>
        public bool Add(ScrapeAccountSpec spec)
        {
            lock (_lock)
            {
                if (_pool.Any(p => p.Id == spec.Id)) return false;
                _pool.Add(spec);
                _quarantined.Remove(spec.Id); _cooldown.Remove(spec.Id); _fail.Remove(spec.Id);
                return true;
            }
        }

        /// <summary>true nếu MỌI tk trong vòng đều quarantine (captcha) — không tk nào tự quay lại nữa.</summary>
        public bool AllQuarantined
        {
            get { lock (_lock) return _pool.Count > 0 && _pool.All(p => _quarantined.Contains(p.Id)); }
        }

        public ScrapeAccountSpec? TryAcquire()
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                for (var i = 0; i < _pool.Count; i++)
                {
                    var idx = (_cursor + i) % _pool.Count;
                    var s = _pool[idx];
                    if (_busy.Contains(s.Id) || _quarantined.Contains(s.Id)) continue;
                    if (_cooldown.TryGetValue(s.Id, out var until) && until > now) continue; // còn trong cooldown
                    _cooldown.Remove(s.Id);
                    _busy.Add(s.Id);
                    _cursor = idx + 1;
                    return s;
                }
            }
            return null;
        }

        /// <summary>Chạy được → trả về vòng + reset đếm lỗi.</summary>
        public void Release(ScrapeAccountSpec spec) { lock (_lock) { _busy.Remove(spec.Id); _fail.Remove(spec.Id); } }

        /// <summary>Captcha: tách hẳn tk khỏi run (để riêng xử lý sau), KHÔNG tự quay lại.</summary>
        public void Quarantine(ScrapeAccountSpec spec)
        {
            lock (_lock) { _busy.Remove(spec.Id); _cooldown.Remove(spec.Id); _quarantined.Add(spec.Id); }
        }

        /// <summary>Proxy/lỗi khác: tạm nghỉ rồi tự quay lại vòng. Trả về true nếu đã lỗi ≥2 lần liên tiếp
        /// (caller nên mượn tk khác chạy trước, tk này quay lại sau).</summary>
        public bool Cooldown(ScrapeAccountSpec spec)
        {
            lock (_lock)
            {
                _busy.Remove(spec.Id);
                _cooldown[spec.Id] = DateTimeOffset.UtcNow.AddSeconds(CooldownSeconds);
                var n = _fail[spec.Id] = _fail.GetValueOrDefault(spec.Id) + 1;
                return n >= SetAsideAfterFails;
            }
        }
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
