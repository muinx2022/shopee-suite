using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.Search;
using Shopee.Suite.Modules.UpdateProduct;
using Shopee.Suite.Services;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Worker phía CLIENT: định kỳ XIN việc Hub giao cho máy này (đúng vai trò) rồi TỰ chạy bằng đúng các
/// entry-point fire-and-forget của Workspace (<see cref="ScrapeViewModel.RunSingleAsync"/> /
/// <see cref="UpdateProductViewModel.RunImportSingleAsync"/> …, gọi với silent:true để KHÔNG bao giờ mở
/// modal) — KHÔNG viết lại engine. Khoá lease/account-lease bên trong runner vẫn là lưới an toàn cuối.
/// Chạy trên MỌI máy có vai trò ≠ Tắt (kể cả máy Hub vì Hub cũng có thể là worker).
/// </summary>
public sealed class AssignmentWorker : IDisposable
{
    private readonly ScrapeViewModel _scrape;
    private readonly UpdateProductViewModel _update;
    private readonly SearchViewModel _search;
    private readonly UiThread.UiTimer _timer;            // claim/launch/reconcile (UI thread)
    private readonly System.Threading.Timer _heartbeat;  // nhịp 'running' (luồng NỀN, không bị UI làm nghẽn)
    private readonly Dictionary<string, InFlight> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _liveIds = new(StringComparer.Ordinal);
    // Số lần đã claim-nhưng-chưa-chạy-được của 1 việc (theo id, sống qua các lần re-queue) → thử lại có mức trần.
    private readonly ConcurrentDictionary<string, int> _launchAttempts = new(StringComparer.Ordinal);
    private bool _ticking;
    // Đã gọi resume-mine (tự nhận lại việc dở của máy này lúc khởi động) chưa — chỉ chạy MỘT LẦN mỗi process.
    private bool _resumedMine;
    // Đang chuẩn bị TẮT máy để cập nhật app → Tick NGỪNG nhận việc mới VÀ NGỪNG reconcile (khỏi kết luận
    // 'failed' oan cho việc mình vừa chủ động 'requeue' + dừng trong PrepareForShutdownAsync).
    private bool _shuttingDown;

    private const int MaxConcurrent = 4;
    private const int GraceTicks = 6;   // ~60s: chưa thấy chạy trong ngần này coi như không khởi động được
    // Tiền-kiểm CHƯA đạt (client mới chưa kịp kéo tk/workbook…) coi là lỗi TẠM THỜI: trả việc về hàng đợi thử
    // lại ngần này nhịp (~10s/nhịp) trước khi báo 'failed' thật → không mất việc ghim do trễ đồng bộ.
    private const int MaxLaunchAttempts = 6;

    /// <summary>Người ngồi máy bấm "Tạm dừng nhận việc" → ngừng xin việc mới (việc đang chạy vẫn xong).</summary>
    public bool Paused { get; set; }

    /// <summary>Tự kéo cấu hình/cookie MỚI trước import/update và đẩy cấu hình/cookie ĐÃ ĐỔI sau scrape (bàn
    /// giao xuyên máy). Mặc định TẮT để không đụng cấu hình máy đang chạy 1 máy; bật từ bảng điều phối khi tách
    /// vai trò qua nhiều máy. (Workbook KHÔNG còn sync — kho SP đã sang Postgres; chỉ còn cấu hình + cookie.)</summary>
    public bool AutoSyncHandoff { get; set; }

    private sealed class InFlight
    {
        public required Assignment A;
        public bool SeenRunning;
        public int IdleTicks;
        /// <summary>Số cửa sổ Brave đã CẤP cho việc này (để tính quỹ; 0 = việc không dùng trình duyệt, vd rewrite).</summary>
        public int GrantedBraves;
    }

    public AssignmentWorker(ScrapeViewModel scrape, UpdateProductViewModel update, SearchViewModel search)
    {
        _scrape = scrape;
        _update = update;
        _search = search;
        _timer = UiThread.Interval(TimeSpan.FromSeconds(10), Tick);
        _timer.Start();
        // Nhịp giữ-sống chạy trên luồng nền → KHÔNG bị log-flood của scrape (10-20 cửa sổ) làm nghẽn, nên
        // SweepStaleLocked (5') không đánh nhầm 'failed' việc đang chạy thật.
        _heartbeat = new System.Threading.Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Dispose() { _timer.Stop(); _heartbeat.Dispose(); }

    /// <summary>
    /// Chuẩn bị TẮT MÁY ÊM trước khi Velopack áp bản mới + khởi động lại: TRẢ mọi việc Hub-giao đang chạy về
    /// HÀNG CHỜ hub (GIỮ nguyên tham số StartRow/EndRow/Processes… vì 'requeue' chỉ đổi trạng thái + xoá claim),
    /// dừng runner local, rồi ĐỢI tất cả dừng hẳn (tối đa <paramref name="timeout"/>). Nhờ đó bản mới khởi động
    /// lại tự claim lại việc còn dở thay vì để nó chết cứng (khoá acc treo tới khi hub sweep 5'). Đặt
    /// <see cref="Paused"/> + cờ _shuttingDown để Tick ngừng nhận việc mới VÀ ngừng reconcile (khỏi báo 'failed'
    /// oan cho việc mình vừa chủ động dừng). Trả về SỐ việc đã trả lại hàng chờ (hub null → 0, vẫn set Paused).
    /// </summary>
    public async Task<int> PrepareForShutdownAsync(TimeSpan timeout)
    {
        Paused = true;
        _shuttingDown = true;
        var hub = CoordinationRuntime.Hub;
        var jobs = _inflight.Values.ToList();
        foreach (var f in jobs)
        {
            // 'requeue' (running → queued + xoá claim) dùng lại đường sẵn có cho "chờ quỹ Brave" — GIỮ tham số
            // việc. hub null → chỉ dừng local (không có gì để trả về hàng chờ).
            if (hub is not null) await hub.ReportAssignmentAsync(f.A.Id, "requeue", "tạm dừng để cập nhật app");
            StopLocal(f.A);
            HubLog.Info($"⏸ Trả về hàng chờ hub: {Describe(f.A)} — cập nhật app");
            _liveIds.TryRemove(f.A.Id, out _);
            _launchAttempts.TryRemove(f.A.Id, out _);
            _inflight.Remove(f.A.Id);
        }
        // Đợi runner dừng HẲN. StopLocal enqueue lên UI thread nên KHÔNG .Wait()/.Result (kẹt UI) — poll
        // IsRunningLocally mỗi ~500ms tới khi hết việc chạy hoặc quá hạn (job kẹt thì hub sweep + resume-mine lo,
        // không được treo nút cập nhật vĩnh viễn).
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && jobs.Any(f => IsRunningLocally(f.A)))
            await Task.Delay(500);
        return hub is null ? 0 : jobs.Count;
    }

    private void Heartbeat()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;
        foreach (var id in _liveIds.Keys) _ = hub.ReportAssignmentAsync(id, "running");
    }

    private async void Tick()
    {
        if (_ticking) return;
        _ticking = true;
        try { await TickAsync(); } catch { } finally { _ticking = false; }
    }

    private async Task TickAsync()
    {
        // Đang chuẩn bị tắt máy để cập nhật → dừng HẲN vòng lặp: không nhận việc mới, KHÔNG reconcile (việc đang
        // dở đã được PrepareForShutdownAsync chủ động 'requeue' + dừng; reconcile lúc này chỉ đẻ 'failed' oan).
        if (_shuttingDown) return;

        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;

        // Tự NHẬN LẠI việc đang dở SAU KHI KHỞI ĐỘNG LẠI (chạy 1 lần/process): process cũ chết (Velopack áp bản
        // mới / crash / tắt máy) để lại lease MỒ CÔI khoá acc tới 5' + việc còn 'running' của máy này treo trên
        // hub. resume-mine nhả lease chết NGAY + đưa việc ấy về 'queued' → được claim LUÔN trong tick này (đặt
        // TRƯỚC phần claim), khỏi cần người ngồi máy nhớ bấm gì.
        if (!_resumedMine && _inflight.Count == 0)
        {
            // Điều kiện _inflight RỖNG: resume-mine phía server requeue MỌI việc 'running' của máy này + xoá lease
            // của máy — chỉ an toàn khi process này CHƯA chạy gì (mọi thứ 'running' đứng tên máy chắc chắn là mồ côi
            // của process cũ). Đã nhận việc rồi mà retry (vì tick trước rớt mạng đúng 1 request) sẽ requeue nhầm
            // việc ĐANG SỐNG → thôi, coi như đã re-attach.
            var n = await hub.ResumeMineAsync();
            // -1 = chưa gọi được hub (boot đúng lúc rớt mạng/hub chưa lên) → GIỮ cờ để thử lại nhịp sau; chỉ chốt
            // "đã re-attach" khi round-trip thành công (kể cả 0 việc), kẻo mất re-attach cả phiên vì 1 tick xui.
            if (n >= 0) _resumedMine = true;
            if (n > 0) HubLog.Info($"⏯ Nhận lại {n} việc đang dở sau khi khởi động lại");
        }
        else if (!_resumedMine) _resumedMine = true;   // đã có việc trong tay → quá cửa sổ re-attach an toàn

        await ReconcileInflightAsync(hub);

        if (Paused) return;

        var myId = hub.MachineId;
        var role = hub.CurrentFleet.Roles.FirstOrDefault(r => r.MachineId == myId)?.Role ?? MachineRoles.Off;
        // KHÔNG return sớm khi role==Off: việc GHIM TAY (Giao tay) định tuyến theo MÁY, không cần vai trò.
        // Server chỉ trả việc ghim-cho-máy-này (+ việc đúng vai trò nếu có) → role=Off vẫn nhận được việc ghim.

        var free = MaxConcurrent - _inflight.Count;
        if (free <= 0) return;

        // QUỸ BRAVE: tổng cửa sổ các việc HUB-GIAO đang chạy KHÔNG vượt trần MaxConcurrentWindows. Luật: việc cuối
        // được cấp phần CÒN THIẾU (max − đã dùng); hết quỹ thì việc mới NẰM CHỜ ('queued') trên hub tới khi 1 việc
        // xong nhả quỹ. Quỹ CHỈ đếm việc hub-giao (job chạy TAY do user tự chịu; lưới an toàn cuối là gate semaphore
        // trong BraveFleet cho scrape).
        var usedBraves = _inflight.Values.Sum(f => f.GrantedBraves);
        var freeBraves = Shopee.Core.Browser.BraveFleet.MaxConcurrentWindows - usedBraves;
        if (freeBraves <= 0) return;   // hết quỹ → KHÔNG claim việc mới (để việc nằm 'queued' trên hub = phần đợi)

        foreach (var a in await hub.ClaimAssignmentsAsync(role, free))
        {
            if (_inflight.ContainsKey(a.Id)) continue;
            var need = RequiredBraves(a);
            // Cạn quỹ giữa lượt (việc trước vừa lấy hết) mà việc này CẦN Brave → TRẢ VỀ HÀNG ĐỢI, KHÔNG đếm số lần
            // thử: chờ quỹ là chuyện BÌNH THƯỜNG có thể lâu; RequeueOrFailAsync sẽ báo 'failed' oan sau 6 nhịp. Dùng
            // 'requeue' thẳng để giữ ô ở "• đã xếp" cho tới khi có quỹ (tái dùng đường 'requeue' sẵn có, không đổi giao thức).
            if (need > 0 && freeBraves <= 0)
            {
                // "Đang dùng" tính từ freeBraves HIỆN TẠI (không dùng usedBraves chụp trước vòng lặp) — việc vừa
                // được cấp trong CHÍNH tick này cũng phải tính, kẻo message báo "0/10" dù quỹ vừa cạn.
                var inUse = Shopee.Core.Browser.BraveFleet.MaxConcurrentWindows - freeBraves;
                await hub.ReportAssignmentAsync(a.Id, "requeue", $"chờ quỹ Brave ({inUse}/{Shopee.Core.Browser.BraveFleet.MaxConcurrentWindows} đang dùng)");
                continue;
            }
            // Cấp phần còn thiếu cho việc cuối; việc không dùng trình duyệt (need==0) cấp 0 (không trừ quỹ).
            var grant = need == 0 ? 0 : Math.Min(need, freeBraves);
            freeBraves -= grant;
            _inflight[a.Id] = new InFlight { A = a, GrantedBraves = grant };
            await LaunchAsync(hub, a, grant);
        }
    }

    /// <summary>Số cửa sổ Brave 1 việc CẦN để tính quỹ. rewrite/search KHÔNG mở trình duyệt → 0. scrape/import/update:
    /// ưu tiên Processes Hub đặt cho lượt này, else cấu hình client (RunConfig.Processes, mặc định 2). Clamp theo trần
    /// engine: update/import 1..10, scrape 1..64.</summary>
    private static int RequiredBraves(Assignment a)
    {
        if (a.Op is "rewrite" or "search") return 0;
        var n = a.Processes > 0
            ? a.Processes
            : (BigSellerStore.Shared.Accounts.FirstOrDefault(x => x.Id == a.BigsellerId)?.RunConfig?.Processes ?? 2);
        return a.Op == "scrape" ? Math.Clamp(n, 1, 64) : Math.Clamp(n, 1, 10);
    }

    private async Task LaunchAsync(HttpCoordinationHub hub, Assignment a, int grant)
    {
        // Tiền-kiểm KHÔNG mở dialog: thiếu điều kiện (kho tk/Brave/workbook/cookie/sheet). Trước đây báo 'failed'
        // NGAY → việc ghim vào máy mới (chưa kịp đồng bộ) chết ngầm, ô về 'chờ'. Giờ coi là lỗi TẠM THỜI: trả về
        // hàng đợi thử lại vài nhịp, quá trần mới 'failed' (kèm lý do) — xem RequeueOrFailAsync.
        if (!CanLaunch(a, out var problem))
        {
            _inflight.Remove(a.Id);
            await RequeueOrFailAsync(hub, a, problem);
            return;
        }

        // Bàn giao xuyên máy: kéo cấu hình/cookie mới nhất trước khi import/update (workbook không còn sync).
        if (AutoSyncHandoff && a.Op is "import" or "update" or "rewrite")
        {
            try { if (CoordinationRuntime.ConfigSync is { } sync) await sync.PullAccountsAsync(); } catch { }
        }

        // Enqueue (luôn xếp hàng, không chạy inline) → LaunchCore không chạy trong nested modal pump nếu lỡ có dialog đang mở.
        UiThread.Enqueue(() =>
        {
            if (LaunchCore(a, grant)) { _liveIds[a.Id] = 1; _launchAttempts.TryRemove(a.Id, out _); HubLog.Info($"▶ Nhận {Describe(a)}"); }   // đã chạy → xoá bộ đếm thử lại
            else { _inflight.Remove(a.Id); _ = RequeueOrFailAsync(hub, a, "không khởi động được trên máy này"); }
        });
    }

    /// <summary>Việc vừa claim nhưng CHƯA chạy được (thường do client mới chưa kịp đồng bộ tk/workbook) = lỗi
    /// TẠM THỜI: trả về hàng đợi (server: running → queued) để claim lại nhịp sau, GIỮ ô ở "• đã xếp". Quá
    /// <see cref="MaxLaunchAttempts"/> lần vẫn không được → báo 'failed' kèm lý do để operator thấy rõ.</summary>
    private async Task RequeueOrFailAsync(HttpCoordinationHub hub, Assignment a, string problem)
    {
        var n = _launchAttempts.AddOrUpdate(a.Id, 1, (_, v) => v + 1);
        if (n < MaxLaunchAttempts)
        {
            await hub.ReportAssignmentAsync(a.Id, "requeue", problem);
        }
        else
        {
            _launchAttempts.TryRemove(a.Id, out _);
            await hub.ReportAssignmentAsync(a.Id, "failed", $"{problem} (đã thử {n} lần)");
        }
    }

    /// <summary>Tiền-kiểm điều kiện chạy (KHÔNG side-effect mở dialog). false → báo failed ngay.</summary>
    private bool CanLaunch(Assignment a, out string problem)
    {
        problem = "";
        if (a.Op == "search")
        {
            if (_search.PoolCount == 0) { problem = "kho tài khoản Shopee trống"; return false; }
            if (_search.IsRunning) { problem = "máy đang chạy 1 việc Search khác"; return false; }
            if (string.IsNullOrWhiteSpace(a.Payload)) { problem = "thiếu dữ liệu khối link"; return false; }
            return true;
        }
        if (a.Op == "scrape")
        {
            var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
            var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
            if (t is null || shop is null) { problem = "không thấy tài khoản/shop trên máy này"; return false; }
            t.SelectedShop = shop;
            return _scrape.CanDispatchScrape(t, out problem);
        }
        var ut = _update.RunTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
        var ushop = ut?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
        if (ut is null || ushop is null) { problem = "không thấy tài khoản/shop trên máy này"; return false; }
        ut.SelectedShop = ushop;
        return _update.CanDispatchUpdate(ut, a.Op, out problem);
    }

    private bool LaunchCore(Assignment a, int grant)
    {
        switch (a.Op)
        {
            case "scrape":
            {
                var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
                var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
                if (t is null || shop is null) return false;
                t.SelectedShop = shop;
                // Hub đặt khoảng dòng + số cửa sổ (= quỹ cấp) + cỡ khung cho lượt này (0/null = dùng cấu hình client).
                _ = _scrape.RunSingleAsync(t, resume: true, silent: true, a.StartRow, a.EndRow,
                    grant > 0 ? grant : (int?)null, a.FrameSize > 0 ? a.FrameSize : (int?)null);
                return true;
            }
            case "import":
            case "update":
            case "rewrite":
            {
                var t = _update.RunTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
                var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
                if (t is null || shop is null) return false;
                t.SelectedShop = shop;
                // import/update: số lane = quỹ Brave cấp; reload theo Hub đặt (0/null = dùng cấu hình client).
                // rewrite: KHÔNG mở trình duyệt → processes null (quỹ đã cấp grant=0 cho op này).
                if (a.Op == "import") _ = _update.RunImportSingleAsync(t, silent: true, a.StartRow, a.EndRow, ImportFromClaimedTab(a),
                    processes: grant > 0 ? grant : (int?)null, reloadSeconds: a.ReloadSeconds > 0 ? a.ReloadSeconds : (int?)null);
                else if (a.Op == "update") _ = _update.RunUpdateSingleAsync(t, silent: true, a.StartRow, a.EndRow,
                    processes: grant > 0 ? grant : (int?)null, reloadSeconds: a.ReloadSeconds > 0 ? a.ReloadSeconds : (int?)null);
                else _ = _update.RunNameRewriteSingleAsync(t, silent: true, a.StartRow, a.EndRow);
                return true;
            }
            case "search":
            {
                if (_search.IsRunning) return false;   // đang chạy 1 search khác → báo failed NGAY (khỏi kẹt grace 60s)
                var payload = TryParseSearch(a.Payload);
                if (payload is null || payload.Links.Count == 0) return false;
                _ = _search.RunAssignmentAsync(a.Id, payload, CancellationToken.None);
                return true;
            }
            default: return false;
        }
    }

    private static SearchJobPayload? TryParseSearch(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SearchJobPayload>(json); } catch { return null; }
    }

    /// <summary>Mô tả ngắn 1 việc cho dòng log tập trung: "Import · Shop (Tài khoản)" / "Search · file".</summary>
    private static string Describe(Assignment a)
    {
        if (a.Op == MachineRoles.Search)
            return $"Search · {(string.IsNullOrWhiteSpace(a.Sheet) ? "(file Hub giao)" : a.Sheet)}";
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(x => x.Id == a.BigsellerId);
        var shop = acct?.Shops.FirstOrDefault(s => s.Id == a.ShopId);
        var op = a.Op switch { "scrape" => "Scrape", "import" => "Import", "update" => "Update", "rewrite" => "Tên SP", _ => a.Op };
        var acctName = acct?.DisplayName ?? (a.BigsellerId.Length > 8 ? a.BigsellerId[..8] : a.BigsellerId);
        var shopName = shop?.DisplayName ?? (a.ShopId.Length > 8 ? a.ShopId[..8] : a.ShopId);
        return $"{op} · {shopName} ({acctName})";
    }

    /// <summary>Cờ "import từ tab Đã nhận" Hub ghim trong payload việc import: non-null → ĐÈ cấu hình shop lượt
    /// này; null (payload rỗng, vd việc tự-động) → client dùng cấu hình của nó.</summary>
    private static bool? ImportFromClaimedTab(Assignment a)
    {
        if (string.IsNullOrWhiteSpace(a.Payload)) return null;
        try { return JsonSerializer.Deserialize<ImportJobPayload>(a.Payload)?.FromClaimedTab; } catch { return null; }
    }

    private async Task ReconcileInflightAsync(HttpCoordinationHub hub)
    {
        if (_inflight.Count == 0) return;

        // 1) Việc bị HUỶ ở Hub → dừng job local + bỏ khỏi sổ (làm Cancel thực sự có hiệu lực).
        foreach (var f in _inflight.Values.ToList())
        {
            var st = hub.CurrentFleet.Assignments.FirstOrDefault(x => x.Id == f.A.Id)?.Status;
            if (st == "canceled")
            {
                StopLocal(f.A);
                HubLog.Warn($"✖ Huỷ {Describe(f.A)}");
                _liveIds.TryRemove(f.A.Id, out _);
                _launchAttempts.TryRemove(f.A.Id, out _);
                _inflight.Remove(f.A.Id);
            }
        }

        // 2) Phân loại còn chạy / cần kết luận; cập nhật _liveIds cho nhịp giữ-sống nền.
        var toConclude = new List<InFlight>();
        foreach (var f in _inflight.Values)
        {
            if (IsRunningLocally(f.A)) { f.SeenRunning = true; f.IdleTicks = 0; _liveIds[f.A.Id] = 1; continue; }
            _liveIds.TryRemove(f.A.Id, out _);
            f.IdleTicks++;
            // SeenRunning: chờ ~2 nhịp để chắc đã dừng hẳn; chưa từng thấy chạy: chờ hết grace.
            if (f.IdleTicks >= (f.SeenRunning ? 2 : GraceTicks)) toConclude.Add(f);
        }

        // 3) Kết luận dựa trên ledger TƯƠI (round-trip thật) — KHÔNG dùng snapshot poll 12s (tránh báo nhầm).
        foreach (var f in toConclude)
        {
            // Search KHÔNG ghi ledger → kết luận theo OUTCOME client ghi lại (completed/stopped/failed). Không có
            // outcome mà đã từng thấy chạy → coi như xong (lưới an toàn); chưa từng chạy → lỗi.
            if (f.A.Op == "search")
            {
                var outcome = _search.TakeAssignmentOutcome(f.A.Id);
                var searchOk = outcome == "completed" || (outcome is null && f.SeenRunning);
                var searchErr = searchOk ? null : outcome switch
                {
                    "stopped" => "đã dừng dở (khối link chưa xong)",
                    "failed" => "lỗi khi chạy Search",
                    _ => "không khởi động được (kho acc trống / bị máy khác giữ)",
                };
                await hub.ReportAssignmentAsync(f.A.Id, searchOk ? "done" : "failed", searchErr);
                if (searchOk) HubLog.Ok($"✔ Xong {Describe(f.A)}"); else HubLog.Warn($"■ {Describe(f.A)} — {searchErr}");
                _liveIds.TryRemove(f.A.Id, out _);
                _inflight.Remove(f.A.Id);
                continue;
            }
            var status = await hub.FetchLedgerStatusAsync(f.A.CoordId);
            var ok = status == "completed";
            await hub.ReportAssignmentAsync(f.A.Id, ok ? "done" : "failed",
                ok ? null : (f.SeenRunning ? (status ?? "dừng dở") : "không khởi động được (có thể bị chặn khoá)"));
            if (ok) HubLog.Ok($"✔ Xong {Describe(f.A)}");
            else HubLog.Warn($"■ {Describe(f.A)} — {(f.SeenRunning ? (status ?? "dừng dở") : "không khởi động được")}");

            if (AutoSyncHandoff && f.A.Op == "scrape" && ok)
            { try { _ = CoordinationRuntime.ConfigSync?.PushAsync(); } catch { } }

            _liveIds.TryRemove(f.A.Id, out _);
            _inflight.Remove(f.A.Id);
        }
    }

    private void StopLocal(Assignment a)
    {
        UiThread.Enqueue(() =>
        {
            if (a.Op == "search") { _search.StopAssignment(a.Id); return; }
            if (a.Op == "scrape")
            {
                var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
                if (t is not null) _ = _scrape.StopSingleAsync(t);
            }
            else _update.StopSingle(a.BigsellerId);
        });
    }

    private bool IsRunningLocally(Assignment a)
    {
        if (a.Op == "search") return _search.IsRunningAssignment(a.Id);
        if (a.Op == "scrape")
        {
            var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
            var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
            return t is not null && shop is not null && (t.IsShopRunning?.Invoke(shop) ?? false);
        }
        return _update.IsUpdateRunning(a.BigsellerId);
    }
}
