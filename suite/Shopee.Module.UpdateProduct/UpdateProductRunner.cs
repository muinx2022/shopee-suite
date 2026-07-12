using Shopee.Core.Progress;
using UpdateProduct;

namespace Shopee.Modules.UpdateProduct;

/// <summary>Đầu vào cho một lần chạy: lấy từ BigSeller dùng chung (account/workbook/cookie/shop/sheet)
/// + các tham số riêng của update-product (dòng, ảnh/video, crawl url, max process, AI).</summary>
public sealed record UpdateProductContext(
    string AccountId, string Email, string WorkbookPath, string CookieFile,
    string ShopId, string ShopName, string DataSheet,
    string OpenAiModel, string OpenAiApiKeyFile, int OpenAiBatchSize, string GlobalOpenAiKeyFile,
    int StartRow, int EndRow,
    string ImagePath, string VideoFolder, string CrawlUrl, bool ImportFromClaimedTab,
    int ImportMaxProcess, int UpdateMaxProcess, int ListingReloadSeconds,
    string OpenAiApiKey = "",
    int LinkColumn = 1, int PriceColumn = 3, int SkuColumn = 4,
    int ItemIdColumn = 5, int ProductNameColumn = 6, int RewrittenNameColumn = 7,
    string Password = "",
    bool UseHubData = false);

/// <summary>
/// Facade công khai bọc engine update-product. 3 workflow (TẤT CẢ C#, không còn Python):
///  - Import to store (Playwright + Brave qua BigSeller).
///  - Update product (Playwright + Brave).
///  - Update product name (C# gọi OpenAI, không trình duyệt).
/// Dữ liệu account/workbook/cookie/shop/sheet lấy từ BigSeller dùng chung; chạy 1 lane (đúng,
/// chưa song song hoá để tránh tranh profile/port — sẽ bổ sung sau).
/// </summary>
public sealed class UpdateProductRunner
{
    public event Action<string>? Log;

    /// <summary>Bắn (rowIndex, rowIndex) mỗi dòng sheet vừa xử lý xong (import/update/rewrite) → caller đẩy lên
    /// ledger Hub cho Thống kê. Gom từ 3 runner con.</summary>
    public event Action<int, int>? RowsCompleted;

    private ProductNameRewriteRunner? _nameRunner;
    private readonly WorkflowPauseToken _pause = new();

    public void Pause() => _pause.Pause();
    public void Resume() => _pause.Resume();

    private BigSellerWorkflowSettings BuildWorkflow(UpdateProductContext ctx)
    {
        var file = new UpdateProductSettingsFile
        {
            BraveExe = "",                       // Normalize sẽ tự dò Brave
            OpenAiApiKeyFile = ctx.GlobalOpenAiKeyFile ?? "",
            ActiveAccountId = ctx.AccountId,
            ActiveShopId = ctx.ShopId,
            Accounts =
            [
                new BigSellerAccountConfig
                {
                    Id = ctx.AccountId,
                    Email = ctx.Email,
                    Password = ctx.Password,
                    WorkbookPath = ctx.WorkbookPath,
                    BigSellerCookieFile = ctx.CookieFile,
                    Shops =
                    [
                        new ShopConfig
                        {
                            Id = ctx.ShopId,
                            Name = ctx.ShopName,
                            ShopeeDataSheet = ctx.DataSheet,
                            BigSellerImagePath = ctx.ImagePath,
                            BigSellerVideoFolder = ctx.VideoFolder,
                            BigSellerCrawlUrl = ctx.CrawlUrl,
                            BigSellerImportFromClaimedTab = ctx.ImportFromClaimedTab,
                            BigSellerStartRow = Math.Max(2, ctx.StartRow),
                            BigSellerEndRow = Math.Max(0, ctx.EndRow),
                            BigSellerImportMaxProcess = Math.Clamp(ctx.ImportMaxProcess, 1, 10),
                            BigSellerUpdateMaxProcess = Math.Clamp(ctx.UpdateMaxProcess, 1, 10),
                            BigSellerListingReloadSeconds = Math.Clamp(ctx.ListingReloadSeconds, 3, 600),
                            OpenAiModel = ctx.OpenAiModel,
                            OpenAiApiKeyFile = ctx.OpenAiApiKeyFile,
                            OpenAiBatchSize = Math.Clamp(ctx.OpenAiBatchSize <= 0 ? 40 : ctx.OpenAiBatchSize, 1, 500),
                        },
                    ],
                },
            ],
        };
        // Key OpenAI truyền THẲNG (không qua biến môi trường process-wide → con process Brave/Playwright
        // không kế thừa key trong env) + ánh xạ cột Excel theo cấu hình của shop.
        return BigSellerContextFactory.Build(file) with
        {
            OpenAiApiKey = ctx.OpenAiApiKey ?? "",
            LinkColumn = ctx.LinkColumn,
            PriceColumn = ctx.PriceColumn,
            SkuColumn = ctx.SkuColumn,
            ItemIdColumn = ctx.ItemIdColumn,
            ProductNameColumn = ctx.ProductNameColumn,
            RewrittenNameColumn = ctx.RewrittenNameColumn,
            UseHubData = ctx.UseHubData,   // hub-mode: runner đọc/ghi dòng qua HubClient thay vì mở WorkbookPath
        };
    }

    // ── Update product name (C# + OpenAI) ──────────────────────────────────────
    public async Task RunNameRewriteAsync(UpdateProductContext ctx, CancellationToken ct)
    {
        var wf = BuildWorkflow(ctx);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ProductNameRewriteRunner();
        runner.RowsDone += (f, t) => RowsCompleted?.Invoke(f, t);
        _nameRunner = runner;
        runner.Start(wf, m => Log?.Invoke(m), () => done.TrySetResult());
        using (ct.Register(() => { try { runner.Stop(m => Log?.Invoke(m)); } catch { } done.TrySetResult(); }))
            await done.Task;
        _nameRunner = null;
    }

    // ── Import to store (Playwright) — N lane song song nếu ImportMaxProcess > 1 ──
    public async Task RunImportAsync(UpdateProductContext ctx, CancellationToken ct)
    {
        var wf = BuildWorkflow(ctx);
        var n = Math.Clamp(ctx.ImportMaxProcess, 1, 10);
        if (n == 1)
        {
            await using var runner = new BigSellerImportToStoreRunner(wf, m => Log?.Invoke(m), _pause, 0, 1);
            runner.RowsDone += (f, t) => RowsCompleted?.Invoke(f, t);
            await runner.RunAsync(ct);
            return;
        }
        Log?.Invoke($"▶ Import SONG SONG {n} lane (mỗi lane 1 Brave/profile/port riêng).");
        await RunLanesAsync(wf, n, ct, async (laneWf, lane, count, claim, export, c) =>
        {
            await using var runner = new BigSellerImportToStoreRunner(laneWf, m => Log?.Invoke($"[L{lane}] {m}"), _pause, lane, count, claim, export);
            runner.RowsDone += (f, t) => RowsCompleted?.Invoke(f, t);
            await runner.RunAsync(c);
        });
    }

    // ── Update product (C# + Playwright) — N lane song song nếu UpdateMaxProcess > 1 ──
    public async Task RunUpdateAsync(UpdateProductContext ctx, CancellationToken ct)
    {
        var wf = BuildWorkflow(ctx);
        var n = Math.Clamp(ctx.UpdateMaxProcess, 1, 10);
        // CACHE CHUNG dữ liệu shop (dòng → record, khớp theo item id): nạp MỘT LẦN trước mọi lane rồi chia sẻ
        // (immutable) → mỗi lane chỉ tìm id trên Listing rồi sửa, KHỎI đọc-lại/parse-lại workbook N lần.
        // Nạp trước khi mở Brave → workbook lỗi/thiếu cột báo ngay (chưa tốn công phóng trình duyệt).
        var records = await WorkbookRecordCache.LoadAsync(wf, m => Log?.Invoke(m), ct).ConfigureAwait(false);
        // MAP RỖNG = 0 dòng đủ điều kiện update (cột "Tên đã sửa" trống hết / khoảng dòng không có SP). Nếu vẫn mở
        // Brave thì MỌI SP trên Listing đều "not_in_xlsx" → bị BỎ QUA → chạy HÀNG GIỜ vô ích rồi vẫn báo "✓ xong".
        // → DỪNG NGAY tại facade (trước khi spawn lane/mở Brave nào). Return sạch = lane "hết việc", KHÔNG restart.
        if (records.Records.Count == 0)
        {
            Log?.Invoke($"⚠ KHÔNG có dòng nào đủ điều kiện update trong sheet '{wf.DataSheet}' (cột 'Tên đã sửa' trống hết " +
                "hoặc khoảng dòng không có SP) — mọi SP trên Listing sẽ chỉ bị BỎ QUA nên DỪNG NGAY, không mở Brave. " +
                "→ Chạy 'Update tên SP (AI)' để điền cột G trước, rồi chạy lại Update.");
            return;
        }
        // RESUME: tiến độ update đã lưu (itemId → tên đã điền) — đọc 1 LẦN ở facade, chia CHUNG mọi lane (nhiều lane
        // chung 1 store). Runner bỏ qua SP đã update ĐÚNG tên hiện tại; MarkDone lúc save gọi thẳng OpProgressStore
        // singleton (thread-safe) nên snapshot này chỉ để SKIP, không cần refresh trong lượt.
        var updateDone = OpProgressStore.Shared.GetDone(wf.AccountId, wf.DataSheet, "update");
        // Điều phối dọn Material Center DÙNG CHUNG mọi lane (1 account): đếm bắt-đầu-sửa TOÀN account (quota kho
        // per-account, đếm per-lane là lệch) + cổng pause-all khi kho đầy. Sống ở facade → xuyên qua lane-restart.
        var mediaCoord = new MediaCleanupCoordinator();
        if (n == 1)
        {
            await using var runner = new BigSellerProductUpdateRunner(wf, m => Log?.Invoke(m), _pause, mediaCoord: mediaCoord, sharedRecords: records, updateDone: updateDone);
            runner.RowsDone += (f, t) => RowsCompleted?.Invoke(f, t);
            await runner.RunAsync(ct);
            return;
        }
        Log?.Invoke($"▶ Update SONG SONG {n} lane (claim chống trùng SP; mỗi lane 1 Brave/profile/port riêng).");
        await RunLanesAsync(wf, n, ct, async (laneWf, lane, count, claim, export, c) =>
        {
            await using var runner = new BigSellerProductUpdateRunner(laneWf, m => Log?.Invoke($"[L{lane}] {m}"), _pause, claim, mediaCoord, export, records, updateDone);
            runner.RowsDone += (f, t) => RowsCompleted?.Invoke(f, t);
            await runner.RunAsync(c);
        });
    }

    // ── Xóa media (Material Center) thủ công — nút "Xóa Medias" ở trang cấu hình BigSeller ──
    public async Task RunMediaCleanupAsync(UpdateProductContext ctx, CancellationToken ct)
    {
        var wf = BuildWorkflow(ctx);
        // Profile/port RIÊNG để KHÔNG tranh CDP/profile-lock với lane update đang chạy cùng tk; cookie seed
        // từ file qua EnsureCookieAsync (giữ phiên nếu còn sống, login lại nếu thiu).
        var port = PortAllocator.Shared.AllocateBigSellerPort();
        try
        {
            wf = wf with { ProfileDir = wf.ProfileDir + "-mediaclean", DebugPort = port };
            await using var runner = new BigSellerMediaCleanupRunner(wf, m => Log?.Invoke(m));
            await runner.RunAsync(ct);
        }
        finally { PortAllocator.Shared.Release(port); }
    }

    // Spawn N lane song song: lane 0 dùng profile/port BASE (giữ phiên login); lane phụ có profile
    // "<base>-p{i}" + port cấp riêng (PortAllocator) + seed cookie từ file, CHỈ lane 0 ghi cookie ra file.
    // ClaimStore dùng chung chống 2 lane xử lý trùng 1 SP. Stagger launch để né rate-limit + đua login.
    private async Task RunLanesAsync(
        BigSellerWorkflowSettings wf, int n, CancellationToken ct,
        Func<BigSellerWorkflowSettings, int, int, ClaimStore, bool, CancellationToken, Task> runLane)
    {
        var claim = new ClaimStore();
        var ports = new List<int>();
        var tasks = new List<Task>();
        try
        {
            for (var i = 0; i < n; i++)
            {
                var lane = i;
                var laneWf = wf;
                if (lane > 0)
                {
                    // 1 port/lane: cả Import lẫn Update runner chỉ dùng DebugPort/ProfileDir (ImportDebugPort/
                    // ImportProfileDir không runner nào đọc) → cấp 2 port là phí, cạn pool BigSeller nhanh gấp đôi.
                    var p1 = PortAllocator.Shared.AllocateBigSellerPort();
                    ports.Add(p1);
                    laneWf = wf with
                    {
                        ProfileDir = $"{wf.ProfileDir}-p{lane}", DebugPort = p1,
                        ImportProfileDir = $"{wf.ImportProfileDir}-p{lane}", ImportDebugPort = p1,
                    };
                    try { await Task.Delay(2500 + lane * 1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
                var captured = laneWf;
                var export = lane == 0;
                tasks.Add(Task.Run(async () =>
                {
                    // GIÁM SÁT LANE: lane chết (8 lỗi liên tiếp → throw / Brave crash / exception) thì TỰ KHỞI
                    // ĐỘNG LẠI (mở lại Brave, seed cookie, giữ claim chung) thay vì mất hẳn 1 cửa sổ → trước
                    // đây "chạy 1 lúc rồi worker rụng dần". Chạy được >2' coi như có tiến triển → reset đếm;
                    // chết NHANH liên tục >6 lần → account/mạng hỏng thật → dừng lane + báo (tránh restart-storm).
                    var fails = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        var startTick = Environment.TickCount64;
                        try
                        {
                            await runLane(captured, lane, n, claim, export, ct).ConfigureAwait(false);
                            return;   // lane chạy xong bình thường (hết việc / Stop) — không restart
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex)
                        {
                            if (Environment.TickCount64 - startTick >= 120_000) fails = 0; else fails++;
                            if (fails > 6)
                            {
                                Log?.Invoke($"[L{lane}] ✖ lane chết NHANH liên tục {fails} lần — DỪNG lane. Kiểm tra đăng nhập BigSeller/mạng. Lỗi cuối: {ex.Message}");
                                return;
                            }
                            var delaySec = fails == 0 ? 5 : Math.Min(60, 8 * fails);
                            Log?.Invoke($"[L{lane}] ✖ lane chết (lần {fails}): {ex.Message} — KHỞI ĐỘNG LẠI sau {delaySec}s (giữ phiên + claim).");
                            try { await Task.Delay(delaySec * 1000, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { return; }
                        }
                    }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            foreach (var p in ports) PortAllocator.Shared.Release(p);
        }
    }

    public void Stop()
    {
        try { _nameRunner?.Stop(m => Log?.Invoke(m)); } catch { }
    }
}
