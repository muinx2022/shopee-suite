using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;

namespace UpdateProduct;

internal sealed class BigSellerImportToStoreRunner : IAsyncDisposable
{
    private readonly BigSellerWorkflowSettings _settings;
    private readonly Action<string> _log;
    private readonly WorkflowPauseToken? _pauseToken;
    private readonly int _laneIndex;
    private readonly int _laneCount;
    private readonly bool _exportCookie;
    private long _lastTokenWriteBackTick;   // throttle ghi-ngược muc_token định kỳ trong lúc chạy
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
        _ = claim;   // (cũ: chống trùng đa-lane) — import giờ 1 process/1 lô nên không dùng nữa
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

                // ── IMPORT CẢ LÔ — dùng CHUNG cho Crawl List & tab "Đã nhận" (tab "Đã nhận" CHỈ khác ở bước
                //    click tab, đã làm trong SelectSourceTabIfNeededAsync ở trên): CHỌN TẤT CẢ → nút "Import to
                //    Stores" trên THANH CÔNG CỤ → chọn shop → reload. Chu kỳ reload = "Reload (s)". 1 process. ──
                int rowCount;
                try { rowCount = await GetBodyRowCountAsync(page); }
                catch (Exception ex) when (IsTransientNavigationError(ex))
                {
                    _log($"Trang đang reload, thử lại: {ex.Message}");
                    await DelayAsync(3000, cancellationToken);
                    continue;
                }
                consecutiveErrors = 0;   // đọc được bảng → tab còn sống

                var cycleSec = Math.Max(3, _settings.ListingReloadSeconds);
                if (rowCount == 0)
                {
                    _log($"Danh sách trống — đợi {cycleSec}s rồi reload (chờ SP mới, cho tới khi bấm Dừng)…");
                    await DelayAsync(TimeSpan.FromSeconds(cycleSec), cancellationToken);
                    await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log);
                    await SelectSourceTabIfNeededAsync(page, cancellationToken);
                    continue;
                }

                _log($"Có {rowCount} SP — Chọn tất cả → Import to Stores (cả lô)…");

                // committed = true NGAY TRƯỚC khi bấm Import trong modal: lỗi điều hướng SAU đó = đã gửi lên server.
                var committed = false;
                try
                {
                    await ClickSelectAllAsync(page);
                    await DelayAsync(500, cancellationToken);
                    await ClickToolbarImportAsync(page);

                    var modal = page.Locator(".ant-modal-content:visible").Last;
                    await modal.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });
                    await SelectImportShopAndConfirmAsync(modal, _settings.ShopName, () => committed = true);

                    importCount++;
                    _log($"Đã Import to Stores cả lô (#{importCount}) — {rowCount} SP.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (committed && IsTransientNavigationError(ex))
                    {
                        importCount++;
                        _log($"Import cả lô đã gửi (#{importCount}) — trang điều hướng làm mất context.");
                    }
                    else
                    {
                        _log($"Import cả lô lỗi (giữ phiên, thử lại lượt sau): {ex.Message}");
                    }
                }

                await DelayAsync(2000, cancellationToken);
                await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);
                _log($"Reload màn import sau {cycleSec}s…");
                await DelayAsync(TimeSpan.FromSeconds(cycleSec), cancellationToken);
                await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log);
                await SelectSourceTabIfNeededAsync(page, cancellationToken);
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

    // ── Helper cho IMPORT CẢ LÔ ─────────────────────────────────────────────────
    // Số dòng dữ liệu đang hiển thị trong bảng (có dữ liệu mới chọn-tất-cả + import).
    private static Task<int> GetBodyRowCountAsync(IPage page) =>
        page.EvaluateAsync<int>(
            @"() => Array.from(document.querySelectorAll('tr.vxe-body--row'))
                .filter(row => { const r = row.getBoundingClientRect(); return r.width > 0 && r.height > 0; }).length");

    // Tick 'Chọn tất cả' ở header (chỉ tick khi CHƯA chọn hết — tránh bấm thành BỎ chọn).
    private static Task ClickSelectAllAsync(IPage page) =>
        page.EvaluateAsync(
            @"() => {
                const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
                // vxe fixed-column nhân đôi cột checkbox (1 bản 'fixed--hidden') → CHỈ lấy cái đang HIỂN THỊ,
                // tránh click bản ẩn = no-op = chọn-tất-cả thất bại = import lô trên 0 dòng.
                const cells = Array.from(document.querySelectorAll(
                    ""th.col--checkbox .vxe-cell--checkbox, .vxe-header--column.col--checkbox .vxe-cell--checkbox""));
                const head = cells.find(vis) || cells[0];
                if (!head) throw new Error('Khong tim thay checkbox Chon tat ca');
                const icon = head.querySelector('.vxe-checkbox--icon');
                const checked = icon && icon.classList.contains('vxe-icon-checkbox-checked');
                if (!checked) head.click();
            }");

    // Bấm 'Import to Stores' trên THANH CÔNG CỤ (cả lô) — KHÔNG phải nút từng dòng (a.action_btn) / nút trong modal.
    // Gỡ lớp phủ hướng dẫn (vd .language_switch_guide_mask) hay CHẶN click TRƯỚC; nếu click thật vẫn bị overlay
    // chặn (intercepts pointer events) → gỡ lại + click bằng JS (gọi thẳng handler, bỏ qua pointer-events).
    private async Task ClickToolbarImportAsync(IPage page)
    {
        await BigSellerCrawlHelper.DismissGuideMasksAsync(page, _log);
        var btn = page.Locator(
            ".bs-antd-button.mode-sucess button:has-text('Import to Stores'), " +
            ".rows .lt button:has-text('Import to Stores')").First;
        try
        {
            await btn.ClickAsync(new() { Timeout = 10000 });
        }
        catch
        {
            await BigSellerCrawlHelper.DismissGuideMasksAsync(page, _log);
            var ok = await page.EvaluateAsync<bool>(
                @"() => {
                    const b = Array.from(document.querySelectorAll(
                        '.bs-antd-button.mode-sucess button, .rows .lt button, button'))
                        .find(x => (x.textContent || '').includes('Import to Stores'));
                    if (!b) return false;
                    b.click();
                    return true;
                }");
            if (!ok) throw;
            _log("Đã bấm Import to Stores bằng JS (lớp phủ hướng dẫn chặn click thường).");
        }
    }

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

        // Đăng ký profile vào "fleet" TRƯỚC khi phóng → trình dọn Brave mồ côi (BraveFleet, chạy nền ~4'
        // trong tiến trình app) sẽ CHỪA cửa sổ import này. Thiếu bước này = Brave import (profile nằm trong
        // persistent-data nhưng không ai nhận) bị coi là mồ côi và bị giết giữa chừng, còn vòng lặp "lì đòn"
        // thì cứ mở lại Brave rồi lại bị giết — đúng triệu chứng "tắt Brave nhưng script vẫn chạy".
        BraveFleet.RegisterActiveProfile(_settings.ProfileDir);

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

        // Gỡ đăng ký SAU khi đã giết → nếu còn sót tiến trình nào, lần sweep kế dọn nốt (không rò qua các lượt).
        BraveFleet.UnregisterActiveProfile(_settings.ProfileDir);

        if (_browser is not null)
        {
            try { await Task.WhenAny(_browser.DisposeAsync().AsTask(), Task.Delay(3000)).ConfigureAwait(false); } catch { }
            _browser = null;
        }
        try { _playwright?.Dispose(); } catch { }
        _playwright = null;
    }
}
