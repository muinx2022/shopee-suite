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
    int ItemIdColumn = 5, int ProductNameColumn = 6, int RewrittenNameColumn = 7);

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
        };
    }

    // ── Update product name (C# + OpenAI) ──────────────────────────────────────
    public async Task RunNameRewriteAsync(UpdateProductContext ctx, CancellationToken ct)
    {
        var wf = BuildWorkflow(ctx);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ProductNameRewriteRunner();
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
            await runner.RunAsync(ct);
            return;
        }
        Log?.Invoke($"▶ Import SONG SONG {n} lane (mỗi lane 1 Brave/profile/port riêng).");
        await RunLanesAsync(wf, n, ct, async (laneWf, lane, count, claim, export, c) =>
        {
            await using var runner = new BigSellerImportToStoreRunner(laneWf, m => Log?.Invoke($"[L{lane}] {m}"), _pause, lane, count, claim, export);
            await runner.RunAsync(c);
        });
    }

    // ── Update product (C# + Playwright) — N lane song song nếu UpdateMaxProcess > 1 ──
    public async Task RunUpdateAsync(UpdateProductContext ctx, CancellationToken ct)
    {
        var wf = BuildWorkflow(ctx);
        var n = Math.Clamp(ctx.UpdateMaxProcess, 1, 10);
        if (n == 1)
        {
            await using var runner = new BigSellerProductUpdateRunner(wf, m => Log?.Invoke(m), _pause);
            await runner.RunAsync(ct);
            return;
        }
        Log?.Invoke($"▶ Update SONG SONG {n} lane (claim chống trùng SP; mỗi lane 1 Brave/profile/port riêng).");
        await RunLanesAsync(wf, n, ct, async (laneWf, lane, count, claim, export, c) =>
        {
            await using var runner = new BigSellerProductUpdateRunner(laneWf, m => Log?.Invoke($"[L{lane}] {m}"), _pause, claim, export);
            await runner.RunAsync(c);
        });
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
                    try { await runLane(captured, lane, n, claim, export, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Log?.Invoke($"[L{lane}] ✖ lỗi lane: {ex.Message}"); }
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
