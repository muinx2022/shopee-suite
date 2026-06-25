using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Shopee.Core.BigSeller;

namespace UpdateProduct;

internal sealed class BigSellerImportToStoreRunner : IAsyncDisposable
{
    private const string ImportButtonSelector =
        "td[colid='col_10'] a.action_btn[title='Import to Stores'], " +
        ".vxe-body--column.col_10 a.action_btn[title='Import to Stores'], " +
        "a.action_btn[title='Import to Stores'], " +
        "a.action_btn:has-text('Import to Stores'):visible, " +
        "button:has-text('Import to Stores'):visible";

    private readonly BigSellerWorkflowSettings _settings;
    private readonly Action<string> _log;
    private readonly WorkflowPauseToken? _pauseToken;
    private readonly int _laneIndex;
    private readonly int _laneCount;
    private readonly ClaimStore? _claim;
    private readonly bool _exportCookie;
    private long _lastTokenWriteBackTick;   // throttle ghi-ngược muc_token định kỳ trong lúc chạy
    // SONG SONG (Crawl List): SP lane NÀY đã import xong / bỏ qua → không chọn lại; + đếm lỗi để bỏ SP hỏng.
    // Chống-trùng GIỮA các lane do _claim (ConcurrentDictionary dùng chung) lo; _doneKeys chỉ là cục bộ lane.
    private readonly HashSet<string> _doneKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _failCounts = new(StringComparer.Ordinal);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private Process? _braveProcess;

    public BigSellerImportToStoreRunner(
        BigSellerWorkflowSettings settings,
        Action<string> log,
        WorkflowPauseToken? pauseToken = null,
        int laneIndex = 0,
        int laneCount = 1,
        ClaimStore? claim = null,
        bool exportCookie = true)
    {
        _settings = settings;
        _log = log;
        _pauseToken = pauseToken;
        _laneIndex = Math.Max(0, laneIndex);
        _laneCount = Math.Max(1, laneCount);
        _claim = claim;
        _exportCookie = exportCookie;
    }

    /// <summary>
    /// Ghi NGƯỢC muc_token (server vừa xoay) ra file ĐỊNH KỲ trong lúc chạy — chỉ lane 0
    /// (<see cref="_exportCookie"/>), throttle 90s. Bù đúng cái Update trước đây thiếu so với Scrape (chỉ
    /// export đầu/cuối) → file thiu giữa chừng → import token cũ → BigSeller đá phiên. Engine cookie chung ở Core.
    /// </summary>
    private async Task MaybeWriteBackBigSellerTokenAsync(CancellationToken ct)
    {
        if (!_exportCookie || string.IsNullOrWhiteSpace(_settings.BigSellerCookieFile))
            return;
        var now = Environment.TickCount64;
        if (now - _lastTokenWriteBackTick < 90_000)
            return;
        _lastTokenWriteBackTick = now;
        try
        {
            await BigSellerCookieEngine.WriteBackLiveTokenAsync(
                _settings.DebugPort, _settings.BigSellerCookieFile!, _log, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.BravePath))
            throw new FileNotFoundException($"Khong tim thay Brave: {_settings.BravePath}");

        StartBraveForBigSeller();
        _log($"Da goi Brave PID={_braveProcess?.Id.ToString() ?? "unknown"}, cho CDP port {_settings.DebugPort}...");
        if (!await new CdpClient(_settings.DebugPort).WaitForReadyAsync(
                attempts: 30,
                delayMs: 500,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"CDP port {_settings.DebugPort} khong san sang. Hay dong Brave BigSeller profile cu roi chay lai.");

        // Profile BigSeller là persistent — nếu còn phiên sống thì phiên trong profile
        // luôn "tươi" hơn file tĩnh; ghi đè bằng file cũ sẽ làm văng phiên -> chỉ import khi mất phiên.
        // Có token chưa đủ (token có thể đã bị server thu hồi) — probe trang app để chắc chắn.
        var crawlUrl = BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl);
        var hasLiveSession = false;
        try
        {
            hasLiveSession = BigSellerCookieImporter.HasAuthCookie(
                await BigSellerCookieImporter.GetBigSellerCookiesAsync(_settings.DebugPort).ConfigureAwait(false));
        }
        catch
        {
            // khong doc duoc cookie -> coi nhu chua dang nhap, import tu file nhu cu
        }

        if (hasLiveSession &&
            await BigSellerCookieImporter.ProbeLoggedInAsync(
                _settings.DebugPort, crawlUrl, _log, cancellationToken).ConfigureAwait(false) == false)
        {
            hasLiveSession = false;
            _log("Token BigSeller trong profile da bi server thu hoi — nap lai cookie tu file account.");
        }

        if (hasLiveSession)
        {
            _log("Profile da dang nhap BigSeller — giu phien hien tai, khong ghi de cookie tu file.");
            // Chỉ lane 0 (profile base) ghi cookie ra file → tránh các lane phụ đá token (rotation-war).
            if (_exportCookie)
                await BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
                    _settings.DebugPort, _settings.BigSellerCookieFile, _log).ConfigureAwait(false);
        }
        else
        {
            _log("CDP da san sang, dang import cookie BigSeller...");
            await BigSellerCookieImporter.ImportFromFileAsync(
                _settings.DebugPort,
                _settings.BigSellerCookieFile ?? "",
                _log,
                reloadBigSellerTabs: false,
                navigateUrl: crawlUrl,
                cancellationToken).ConfigureAwait(false);
            _log("Da xu ly cookie BigSeller.");

            if (await BigSellerCookieImporter.ProbeLoggedInAsync(
                    _settings.DebugPort, crawlUrl, _log, cancellationToken).ConfigureAwait(false) == false)
                _log("Cookie tu file account cung da het han — mo tab Account, bam Open BigSeller, login lai roi bam Save & close.");
        }

        _log($"Ket noi CDP port {_settings.DebugPort}...");
        await ConnectBrowserAsync(cancellationToken).ConfigureAwait(false);

        var context = _browser!.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Brave chưa có browser context.");

        var page = await FindBigSellerPageAsync(context, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy tab BigSeller.");

        await page.BringToFrontAsync();
        _log($"Crawl URL: {crawlUrl}");
        if (!await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log))
            throw new InvalidOperationException("Không mở được Crawl List.");

        await SelectSourceTabIfNeededAsync(page, cancellationToken);

        _log(new string('=', 50));
        _log("BẮT ĐẦU IMPORT TO STORE");
        _log(new string('=', 50));

        var importCount = 0;
        if (_laneCount > 1)
            _log($"Lane #{_laneIndex + 1}/{_laneCount}: MỖI SP chỉ 1 lane nhận (claim chống trùng) — các lane import SP KHÁC nhau.");

        // Vòng lặp "lì đòn": mọi lỗi KHÔNG phải Stop được ghi log + tự hồi phục tab/CDP (mở lại tab /
        // kết nối lại CDP / khởi động lại Brave) rồi CHẠY TIẾP, thay vì văng ra ngoài làm chết cả phiên
        // ("chạy tẹo rồi dừng"). Chỉ thật sự thoát khi bị Stop, hoặc quá nhiều lỗi liên tiếp không đọc nổi
        // danh sách (coi như hỏng hẳn). Cơ chế chống import-trùng (committed/claim) GIỮ NGUYÊN.
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 8;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await WaitIfNotPausedAsync(cancellationToken).ConfigureAwait(false);
                await MaybeWriteBackBigSellerTokenAsync(cancellationToken).ConfigureAwait(false);
                _log("");

                if (!await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log))
                {
                    await DelayAsync(5000, cancellationToken);
                    continue;
                }
                await SelectSourceTabIfNeededAsync(page, cancellationToken);

                // ── TAB "ĐÃ NHẬN" (claimed): theo index + xóa dòng sau import (giữ nguyên cách cũ) ──
                if (_settings.ImportFromClaimedTab)
                {
                    int claimedCount;
                    try { claimedCount = await GetClaimedImportRowCountAsync(page); }
                    catch (Exception ex) when (IsTransientNavigationError(ex))
                    {
                        _log($"Trang đang reload, thử lại: {ex.Message}");
                        await DelayAsync(3000, cancellationToken);
                        continue;
                    }
                    consecutiveErrors = 0;   // đọc được danh sách → tab còn sống
                    _log($"Tab Đã nhận có {claimedCount} sản phẩm.");

                    if (claimedCount == 0 || _laneIndex >= claimedCount)
                    {
                        if (await BigSellerCrawlHelper.ClickNextCrawlPageAsync(page, _log))
                        {
                            await DelayAsync(2500, cancellationToken);
                            continue;
                        }
                        _log($"Hết SP tab Đã nhận. Đợi {_settings.ListingReloadSeconds}s...");
                        await DelayAsync(TimeSpan.FromSeconds(Math.Max(3, _settings.ListingReloadSeconds)), cancellationToken);
                        await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log);
                        await SelectSourceTabIfNeededAsync(page, cancellationToken);
                        continue;
                    }

                    await ImportClaimedRowWithRetryAsync(page, _laneIndex, crawlUrl, cancellationToken);
                    importCount++;
                    _log($"Đã Import to Stores #{importCount}.");
                    await DelayAsync(2000, cancellationToken);
                    await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);
                    if (await RemoveClaimedImportRowAsync(page, _laneIndex))
                        _log("Đã xóa dòng vừa import khỏi bảng hiện tại.");
                    await DelayAsync(1500, cancellationToken);
                    continue;
                }

                // ── CRAWL LIST (mặc định): HÀNG ĐỢI claim theo KEY ổn định → N lane lấy SP KHÁC nhau ──
                List<(string Key, string Name)> rows;
                try { rows = await GetImportableRowsAsync(page); }
                catch (Exception ex) when (IsTransientNavigationError(ex))
                {
                    _log($"Trang đang reload/đổi ngôn ngữ, thử lại: {ex.Message}");
                    await DelayAsync(3000, cancellationToken);
                    continue;
                }
                consecutiveErrors = 0;   // đọc được danh sách → tab còn sống
                _log($"Crawl List có {rows.Count} sản phẩm có nút Import.");

                // Lấy SP ĐẦU TIÊN: chưa lane này làm xong (_doneKeys) & chưa lane nào nhận (TryClaim).
                // TryClaim thành công ở đây = lane này "đặt gạch" SP đó → các lane khác bỏ qua.
                (string Key, string Name)? pick = null;
                foreach (var r in rows)
                {
                    if (string.IsNullOrEmpty(r.Key) || _doneKeys.Contains(r.Key)) continue;
                    if (_claim is not null && !_claim.TryClaim(r.Key)) continue;   // lane khác đang/đã nhận
                    pick = r;
                    break;
                }

                if (pick is null)
                {
                    // Trang HIỆN TẠI hết SP để nhận (đã import & BigSeller gỡ đi / hoặc lane khác đang giữ). SP còn
                    // lại nằm ở TRANG SAU → phải phân trang để tới; TUYỆT ĐỐI không reload về trang 1 ở đây, vì
                    // trang 1 đã cạn còn trang 2+ vẫn còn SP → reload trang 1 rỗng = kẹt = "chạy 1 lúc rồi đơ".
                    // Hết sạch trang mới đợi + về trang 1 (đón SP crawl mới / lane khác nhả claim do lỗi tạm).
                    if (await BigSellerCrawlHelper.ClickNextCrawlPageAsync(page, _log))
                    {
                        await DelayAsync(1000, cancellationToken);
                        continue;
                    }
                    _log($"Lane #{_laneIndex + 1} đã duyệt hết các trang Crawl List. Đợi {_settings.ListingReloadSeconds}s rồi về trang 1…");
                    await DelayAsync(TimeSpan.FromSeconds(Math.Max(3, _settings.ListingReloadSeconds)), cancellationToken);
                    await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log);
                    continue;
                }

                var key = pick.Value.Key;
                var name = string.IsNullOrWhiteSpace(pick.Value.Name) ? "(không tên)" : pick.Value.Name;
                var shortName = name.Length > 50 ? name[..50] + "..." : name;
                _log($"[SP #{importCount + 1}] {shortName}");
                _log("Click Import to Stores...");

                // committed = true NGAY TRƯỚC khi bấm nút "Import to Stores" (mốc commit). Lỗi SAU mốc này
                // (thường do trang điều hướng làm mất context) nghĩa là click ĐÃ gửi lên server → KHÔNG được
                // import lại (sẽ TRÙNG). Lỗi TRƯỚC mốc (mở/chọn modal) thì AN TOÀN để trả claim & thử lại.
                var committed = false;
                try
                {
                    try { await ClickImportRowByKeyAsync(page, key); }
                    catch { await ClickImportRowByKeyAsync(page, key); }

                    var modal = page.Locator(".ant-modal-content:visible").Last;
                    await modal.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });
                    await SelectImportShopAndConfirmAsync(modal, _settings.ShopName, () => committed = true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Đã BẤM Import (committed) RỒI gặp lỗi ĐIỀU HƯỚNG/mất-context = click hầu như chắc chắn ĐÃ
                    // tới server (import xong khiến trang reload) → coi như XONG, KHÔNG làm lại (tránh trùng).
                    if (committed && IsTransientNavigationError(ex))
                    {
                        _doneKeys.Add(key);
                        importCount++;
                        consecutiveErrors = 0;
                        _log($"Import đã gửi (#{importCount}) — trang điều hướng làm mất context, không làm lại tránh trùng.");
                        await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);
                    }
                    else
                    {
                        // Lỗi TRƯỚC khi gửi (chưa mở/chọn được modal, nút chưa kịp bấm, timeout không-điều-hướng)
                        // → SP CHƯA import → TRẢ claim cho lane/loop sau thử. Quá 3 lần (lane này) → bỏ hẳn SP.
                        _claim?.Release(key);
                        var fails = (_failCounts.TryGetValue(key, out var f) ? f : 0) + 1;
                        _failCounts[key] = fails;
                        if (fails >= 3) { _doneKeys.Add(key); _log($"Bỏ qua SP lỗi {fails} lần: {shortName} ({ex.Message})"); }
                        else _log($"Import lỗi (lần {fails}/3), để SP lại thử sau: {ex.Message}");
                    }
                    if (IsTransientNavigationError(ex)) await DelayAsync(3000, cancellationToken);
                    else await DelayAsync(1500, cancellationToken);
                    await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log);
                    await SelectSourceTabIfNeededAsync(page, cancellationToken);
                    continue;
                }

                _doneKeys.Add(key);   // xong → giữ claim + đánh dấu (phòng SP chưa kịp bị gỡ thì lane này vẫn bỏ qua)
                importCount++;
                _log($"Đã Import to Stores (#{importCount}) - không vào Hộp nháp.");

                await DelayAsync(1500, cancellationToken);
                await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);
                // Ở LẠI trang hiện tại import SP kế — KHÔNG reload về trang 1. SP vừa import đã bị BigSeller gỡ
                // (và đã vào done/claim) nên vòng sau quét lại tự lấy SP tiếp trên trang này; trang này hết thì
                // nhánh pick==null sẽ sang trang sau. Tránh kẹt trang 1 + đỡ churn full-reload mỗi lần import.
                await DelayAsync(700, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }   // Stop / cancel → thoát sạch
            catch (Exception ex)
            {
                consecutiveErrors++;
                _log($"⚠ Lỗi vòng lặp ({consecutiveErrors}/{maxConsecutiveErrors}) — giữ phiên, thử hồi phục rồi chạy tiếp: {ex.Message}");
                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _log($"✖ Quá {maxConsecutiveErrors} lỗi liên tiếp không đọc nổi danh sách — dừng phiên. Lỗi cuối: {ex.Message}");
                    throw;
                }
                try { page = await RecoverPageAsync(page, crawlUrl, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception rex) { _log($"  (chưa hồi phục được tab/CDP lần này: {rex.Message})"); }
                await DelayAsync(3000, cancellationToken);
            }
        }
    }

    // Kết nối Playwright sang Brave qua CDP (tạo Playwright nếu chưa có; dọn browser cũ nếu đang rớt).
    private async Task ConnectBrowserAsync(CancellationToken ct)
    {
        _playwright ??= await Playwright.CreateAsync();
        if (_browser is not null)
        {
            try { await _browser.DisposeAsync(); } catch { }
            _browser = null;
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _browser = await _playwright.Chromium.ConnectOverCDPAsync(
                    $"http://127.0.0.1:{_settings.DebugPort}",
                    new() { Timeout = 30000 });
                return;
            }
            catch
            {
                await DelayAsync(3000, ct);
            }
        }
        throw new InvalidOperationException(
            "Không kết nối được Brave qua CDP. Kiểm tra BigSeller profile đã đăng nhập chưa.");
    }

    // Brave chết hẳn (tab crash/OOM/đóng) → khởi động lại Brave cùng profile (phiên login bền trên đĩa),
    // chờ CDP rồi kết nối lại. Dùng khi vòng lặp gặp lỗi mà tiến trình Brave đã thoát.
    private async Task RestartBrowserAsync(CancellationToken ct)
    {
        if (_browser is not null)
        {
            try { await _browser.DisposeAsync(); } catch { }
            _browser = null;
        }
        if (_braveProcess is not null)
        {
            try { if (!_braveProcess.HasExited) _braveProcess.Kill(entireProcessTree: true); } catch { }
            try { _braveProcess.Dispose(); } catch { }
            _braveProcess = null;
        }

        StartBraveForBigSeller();
        _log($"  Đã khởi động lại Brave PID={_braveProcess?.Id.ToString() ?? "unknown"}, chờ CDP port {_settings.DebugPort}…");
        if (!await new CdpClient(_settings.DebugPort).WaitForReadyAsync(
                attempts: 30, delayMs: 500, cancellationToken: ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"CDP port {_settings.DebugPort} không sẵn sàng sau khi khởi động lại Brave.");

        await ConnectBrowserAsync(ct).ConfigureAwait(false);
    }

    // Hồi phục sau lỗi vòng lặp: Brave chết → khởi động lại; rớt CDP → kết nối lại; tab chết → mở/tìm lại
    // tab BigSeller. Trả về 1 page sống đang ở Crawl List để vòng lặp chạy tiếp.
    private async Task<IPage> RecoverPageAsync(IPage current, string crawlUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_braveProcess is null || _braveProcess.HasExited)
        {
            _log("  Brave đã thoát — khởi động lại…");
            await RestartBrowserAsync(ct).ConfigureAwait(false);
        }
        else if (_browser is null || !_browser.IsConnected)
        {
            _log("  Mất kết nối CDP — kết nối lại…");
            await ConnectBrowserAsync(ct).ConfigureAwait(false);
        }

        var context = _browser!.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Brave chưa có context sau khi hồi phục.");
        var page = await FindBigSellerPageAsync(context, ct).ConfigureAwait(false);
        if (page is null || page.IsClosed)
            page = await context.NewPageAsync().ConfigureAwait(false);

        await page.BringToFrontAsync().ConfigureAwait(false);
        await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log).ConfigureAwait(false);
        await SelectSourceTabIfNeededAsync(page, ct).ConfigureAwait(false);
        _log("  ✓ Đã hồi phục tab BigSeller — chạy tiếp.");
        return page;
    }

    private async Task SelectSourceTabIfNeededAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_settings.ImportFromClaimedTab)
            return;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = await BigSellerCrawlHelper.SelectClaimedTabByTextAsync(page, _log);
            if (ok) return;

            _log($"Tab Đã nhận chưa chọn được (lần {attempt}/3), chờ trang load...");
            await DelayAsync(2000, cancellationToken);

            if (attempt == 2)
            {
                _log("Reload trang để thử lại chọn tab...");
                await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true,
                    targetUrl: BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl), log: _log);
                await DelayAsync(2000, cancellationToken);
            }
        }

        _log("Cảnh báo: Không chọn được tab Đã nhận, tiếp tục vòng lặp kế tiếp...");
    }

    private async Task<string> ImportClaimedRowWithRetryAsync(
        IPage page,
        int currentIndex,
        string crawlUrl,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 1)
                {
                    _log($"Thử lại import dòng #{currentIndex + 1} lần {attempt}/3...");
                    await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);
                    await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log);
                    await SelectSourceTabIfNeededAsync(page, cancellationToken);
                    await DelayAsync(1000, cancellationToken);
                }

                var claimedProductName = await ClickClaimedImportRowAsync(page, currentIndex);
                if (string.IsNullOrWhiteSpace(claimedProductName))
                    claimedProductName = $"Dòng {currentIndex + 1}";

                var claimedShortName = claimedProductName.Length > 50 ? claimedProductName[..50] + "..." : claimedProductName;
                _log($"[SP #{currentIndex + 1}] {claimedShortName}");

                var claimedModal = page.Locator(".ant-modal-content:visible").Last;
                await claimedModal.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
                await SelectImportShopAndConfirmAsync(claimedModal, _settings.ShopName);
                return claimedProductName;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log($"Import dòng #{currentIndex + 1} lỗi lần {attempt}/3: {ex.Message}");
                if (attempt < 3)
                {
                    if (IsTransientNavigationError(ex))
                    {
                        await DelayAsync(3000, cancellationToken);
                        await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log);
                        await SelectSourceTabIfNeededAsync(page, cancellationToken);
                    }
                    else
                    {
                        await DelayAsync(1500, cancellationToken);
                    }
                }
            }
        }

        throw lastError ?? new InvalidOperationException($"Không import được dòng #{currentIndex + 1} sau 3 lần thử.");
    }

    private static bool IsTransientNavigationError(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("most likely because of a navigation", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase);
    }

    // onCommitting: bắn NGAY TRƯỚC khi bấm nút "Import to Stores" (mốc commit) — caller dùng để biết
    // lỗi xảy ra TRƯỚC hay SAU khi click (sau = đã gửi lên server → không import lại tránh trùng).
    private async Task SelectImportShopAndConfirmAsync(ILocator modal, string shopName, Action? onCommitting = null)
    {
        _log($"Chon shop import: {shopName}");
        var selectedLabel = await modal.EvaluateAsync<string>(
            @"(root, targetShop) => {
                const normalize = value => (value || '')
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .replace(/\u0111/g, 'd')
                    .replace(/\u0110/g, 'd')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                const compact = value => normalize(value).replace(/[^a-z0-9]/g, '');
                const target = normalize(targetShop);
                const targetCompact = compact(targetShop);
                const labelText = label => (label.textContent || '').replace(/\s+/g, ' ').trim();
                const isVisible = el => {
                    const rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                };
                const labels = Array.from(root.querySelectorAll(
                    '.cont_btm.btmOut label.ant-checkbox-wrapper, .btmOut label.ant-checkbox-wrapper, label.ant-checkbox-wrapper'))
                    .filter(label => isVisible(label) && label.querySelector('input[type=checkbox]'));
                const stores = labels.filter(label => !normalize(labelText(label)).includes('select all'));
                const targetLabel = stores.find(label => {
                    const text = labelText(label);
                    const normalized = normalize(text);
                    const compacted = compact(text);
                    return normalized === target ||
                        normalized.includes(target) ||
                        compacted === targetCompact ||
                        compacted.includes(targetCompact);
                });
                if (!targetLabel) {
                    const available = stores.map(labelText).filter(Boolean).join(' | ');
                    throw new Error(`Khong tim thay shop import: ${targetShop}. Available: ${available}`);
                }
                const isChecked = label => {
                    const input = label.querySelector('input[type=checkbox]');
                    const box = label.querySelector('.ant-checkbox');
                    return !!(input?.checked || box?.classList.contains('ant-checkbox-checked') || label.classList.contains('ant-checkbox-wrapper-checked'));
                };
                const clickLabel = label => {
                    const target = label.querySelector('.ant-checkbox-inner') ||
                        label.querySelector('.ant-checkbox') ||
                        label.querySelector('input[type=checkbox]') ||
                        label;
                    target.click();
                };
                for (const label of stores) {
                    if (label === targetLabel) continue;
                    if (isChecked(label)) clickLabel(label);
                }
                if (!isChecked(targetLabel)) clickLabel(targetLabel);
                return labelText(targetLabel) || targetShop;
            }",
            shopName);

        await DelayAsync(500, CancellationToken.None);
        var isChecked = await IsImportShopCheckedAsync(modal, shopName);
        if (!isChecked)
            throw new InvalidOperationException($"Khong chon duoc shop import: {shopName}");

        _log($"Da chon shop import: {selectedLabel}");
        var importButton = modal.Locator("button.ant-btn-primary:has-text('Import to Stores'), button:has-text('Import to Stores')").First;
        onCommitting?.Invoke();   // sắp click Import → từ đây mọi lỗi coi như "đã gửi" (tránh import lại)
        await importButton.ClickAsync(new() { Force = true, Timeout = 10000 });
    }
    private static Task<bool> IsImportShopCheckedAsync(ILocator modal, string shopName) =>
        modal.EvaluateAsync<bool>(
            @"(root, targetShop) => {
                const normalize = value => (value || '')
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .replace(/\u0111/g, 'd')
                    .replace(/\u0110/g, 'd')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                const compact = value => normalize(value).replace(/[^a-z0-9]/g, '');
                const target = normalize(targetShop);
                const targetCompact = compact(targetShop);
                const labelText = label => (label.textContent || '').replace(/\s+/g, ' ').trim();
                const labels = Array.from(root.querySelectorAll(
                    '.cont_btm.btmOut label.ant-checkbox-wrapper, .btmOut label.ant-checkbox-wrapper, label.ant-checkbox-wrapper'))
                    .filter(label => label.querySelector('input[type=checkbox]'));
                const label = labels.find(label => {
                    const text = labelText(label);
                    const normalized = normalize(text);
                    const compacted = compact(text);
                    return !normalized.includes('select all') &&
                        (normalized === target || normalized.includes(target) || compacted === targetCompact || compacted.includes(targetCompact));
                });
                if (!label) return false;
                const input = label.querySelector('input[type=checkbox]');
                const box = label.querySelector('.ant-checkbox');
                return !!(input?.checked || box?.classList.contains('ant-checkbox-checked') || label.classList.contains('ant-checkbox-wrapper-checked'));
            }",
            shopName);

    // Danh sách SP có nút Import (Crawl List) kèm KEY để claim & click theo key (KHÔNG theo index — index
    // đổi liên tục giữa các lane/reload). Mỗi lane là 1 Brave RIÊNG nên key phải suy từ NỘI DUNG (giống nhau
    // ở mọi lane cho cùng 1 SP): gộp href-thật + src ảnh + tên. Bỏ href placeholder (#, javascript:) vì
    // BigSeller hay render link tên SP dạng placeholder → nếu dùng sẽ MỌI dòng cùng 1 key (đứng sau 1 SP).
    private static async Task<List<(string Key, string Name)>> GetImportableRowsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("() => {" + RowJsHelpers +
            " return JSON.stringify(importableRows().map(r => ({ key: keyOf(r), name: nameOf(r) }))); }");
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
                list.Add((
                    el.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
                    el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        }
        catch (JsonException) { /* payload lỗi 1 lần (đang điều hướng) → coi như rỗng, vòng sau thử lại */ }
        return list;
    }

    // Click nút Import của ĐÚNG dòng có key tương ứng (key tính y hệt GetImportableRowsAsync nhờ dùng chung
    // RowJsHelpers). Có 2 dòng cùng key (SP trùng nội dung) thì click dòng đầu — chấp nhận (hiếm).
    private static Task ClickImportRowByKeyAsync(IPage page, string key) =>
        page.EvaluateAsync(
            "key => {" + RowJsHelpers + @"
                const row = importableRows().find(r => keyOf(r) === key);
                if (!row) throw new Error('Khong tim thay dong import voi key: ' + key);
                const button = findButton(row);
                if (!button) throw new Error('Khong tim thay nut Import to Stores trong dong');
                button.scrollIntoView({ block: 'center', inline: 'center' });
                button.click();
            }",
            key);

    // Helper JS DÙNG CHUNG (quét/click cùng cách tính key → luôn khớp). isVisible/findButton/nameOf/keyOf
    // + importableRows(). keyOf gộp [href thật, src ảnh, tên] để duy nhất & ổn định & non-empty.
    private const string RowJsHelpers = @"
        const isVisible = el => {
            const rect = el.getBoundingClientRect();
            const style = window.getComputedStyle(el);
            return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
        };
        const findButton = row => row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores'], .vxe-body--column.col_10 a.action_btn[title='Import to Stores'], a.action_btn[title='Import to Stores']"");
        const nameOf = row => (
            row.querySelector(""td[colid='col_3'] .list_tit_link"")?.textContent ||
            row.querySelector(""td[colid='col_3']"")?.textContent || '').replace(/\s+/g, ' ').trim();
        const realHref = h => (h && !/^\s*(#|javascript:)/i.test(h)) ? h.trim() : '';
        const keyOf = row => {
            const titleA = row.querySelector(""td[colid='col_3'] .list_tit_link, td[colid='col_3'] a"");
            let href = titleA ? realHref(titleA.getAttribute('href')) : '';
            if (!href) { href = Array.from(row.querySelectorAll('a')).map(a => realHref(a.getAttribute('href'))).find(Boolean) || ''; }
            const img = row.querySelector('img');
            const src = img ? (img.getAttribute('src') || '') : '';
            const name = nameOf(row);
            return [href, src, name].filter(Boolean).join('|') || (row.textContent || '').replace(/\s+/g, ' ').trim();
        };
        const importableRows = () => Array.from(document.querySelectorAll('tr.vxe-body--row'))
            .filter(row => isVisible(row) && findButton(row) && isVisible(findButton(row)));
    ";

    private static Task<int> GetClaimedImportRowCountAsync(IPage page) =>
        page.EvaluateAsync<int>(
            @"() => Array.from(document.querySelectorAll('tr.vxe-body--row'))
                .filter(row => {
                    const rect = row.getBoundingClientRect();
                    return rect.width > 0 &&
                        rect.height > 0 &&
                        row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                }).length");

    private static Task<string> ClickClaimedImportRowAsync(IPage page, int rowIndex) =>
        page.EvaluateAsync<string>(
            @"index => {
                const rows = Array.from(document.querySelectorAll('tr.vxe-body--row'))
                    .filter(row => {
                        const rect = row.getBoundingClientRect();
                        return rect.width > 0 &&
                            rect.height > 0 &&
                            row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                    });
                const row = rows[index];
                if (!row) throw new Error(`Khong tim thay dong import index ${index}`);

                const name = (
                    row.querySelector(""td[colid='col_3'] .list_tit_link"")?.textContent ||
                    row.querySelector(""td[colid='col_3']"")?.textContent ||
                    row.textContent ||
                    ''
                ).replace(/\s+/g, ' ').trim();

                const button = row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                if (!button) throw new Error('Khong tim thay nut Import to Stores trong dong');
                button.click();
                return name;
            }",
            rowIndex);

    private static Task<bool> RemoveClaimedImportRowAsync(IPage page, int rowIndex) =>
        page.EvaluateAsync<bool>(
            @"index => {
                const rows = Array.from(document.querySelectorAll('tr.vxe-body--row'))
                    .filter(row => {
                        const rect = row.getBoundingClientRect();
                        return rect.width > 0 &&
                            rect.height > 0 &&
                            row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                    });
                const row = rows[index];
                if (!row) return false;
                row.remove();
                return true;
            }",
            rowIndex);

    private void StartBraveForBigSeller()
    {
        Directory.CreateDirectory(_settings.ProfileDir);
        ClearSessionTabs(_settings.ProfileDir);

        var args = string.Join(" ", [
            $"--remote-debugging-port={_settings.DebugPort}",
            $"--user-data-dir=\"{_settings.ProfileDir}\"",
            "--no-first-run",
            "--no-default-browser-check",
            "--no-session-restore",
            "--restore-last-session=false",
            "--disable-session-crashed-bubble",
            "--start-maximized",
            "--window-size=1920,1080",
            "--disable-gpu",
            "--disable-dev-shm-usage",
            "--disable-software-rasterizer",
            $"\"{BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl)}\"",
        ]);

        _log("Mở Brave BigSeller profile...");
        _braveProcess = Process.Start(new ProcessStartInfo
        {
            FileName = _settings.BravePath,
            Arguments = args,
            UseShellExecute = false,
        });
    }

    private static void ClearSessionTabs(string profileDir)
    {
        var profilePath = new DirectoryInfo(profileDir);
        if (!profilePath.Exists)
            return;

        var patterns = new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs" };
        var dirs = new List<DirectoryInfo> { profilePath };
        dirs.AddRange(profilePath.GetDirectories("Profile *"));
        var defaultDir = Path.Combine(profilePath.FullName, "Default");
        if (Directory.Exists(defaultDir))
            dirs.Add(new DirectoryInfo(defaultDir));

        foreach (var dir in dirs)
        {
            foreach (var pattern in patterns)
            {
                foreach (var file in dir.GetFiles(pattern))
                {
                    try { file.Delete(); } catch { }
                }

                var sessions = Path.Combine(dir.FullName, "Sessions");
                if (!Directory.Exists(sessions))
                    continue;

                foreach (var file in Directory.GetFiles(sessions, pattern))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
    }

    private Task<IPage?> FindBigSellerPageAsync(IBrowserContext context, CancellationToken ct)
    {
        foreach (var p in context.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (BigSellerCrawlHelper.IsCrawlPage(p, _settings.CrawlUrl))
                return Task.FromResult<IPage?>(p);
        }

        foreach (var p in context.Pages)
        {
            if ((p.Url ?? "").Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IPage?>(p);
        }

        return Task.FromResult<IPage?>(context.Pages.FirstOrDefault());
    }

    private Task WaitIfNotPausedAsync(CancellationToken cancellationToken) =>
        _pauseToken?.WaitWhileRunningAsync(cancellationToken) ?? Task.CompletedTask;

    private Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        _pauseToken?.DelayAsync(milliseconds, cancellationToken) ?? Task.Delay(milliseconds, cancellationToken);

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        _pauseToken?.DelayAsync(delay, cancellationToken) ?? Task.Delay(delay, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Lưu token CHỈ lane 0 (tránh lane phụ đá token) + TIMEOUT — Brave treo không được chặn việc kill.
        if (_exportCookie && _braveProcess is { HasExited: false })
        {
            try
            {
                await Task.WhenAny(
                    BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
                        _settings.DebugPort, _settings.BigSellerCookieFile, _log, verifySessionAlive: true),
                    Task.Delay(6000)).ConfigureAwait(false);
            }
            catch { }
        }

        // KILL Brave NGAY (đóng profile + giải phóng RAM) — không phụ thuộc dispose browser/playwright.
        if (_braveProcess is not null)
        {
            try { if (!_braveProcess.HasExited) _braveProcess.Kill(entireProcessTree: true); } catch { }
            try { _braveProcess.Dispose(); } catch { }
            _braveProcess = null;
        }

        if (_browser is not null)
        {
            try { await Task.WhenAny(_browser.DisposeAsync().AsTask(), Task.Delay(3000)).ConfigureAwait(false); } catch { }
            _browser = null;
        }
        try { _playwright?.Dispose(); } catch { }
        _playwright = null;
    }
}
