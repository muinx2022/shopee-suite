using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Playwright;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;

namespace UpdateProduct;

internal sealed class BigSellerImportToStoreRunner : BigSellerBraveRunner
{
    private readonly int _laneIndex;
    private readonly int _laneCount;

    // Tập item id CẦN import = các dòng StartRow→EndRow của sheet shop (nạp 1 lần trước vòng lặp).
    private HashSet<string> _importIds = new(StringComparer.Ordinal);
    // Item id ĐÃ gửi import (chốt chặn: bỏ qua ở các lượt quét sau → không import trùng + đảm bảo kết thúc,
    // độc lập với việc SP có rời tab hay không).
    private readonly HashSet<string> _importedIds = new(StringComparer.Ordinal);
    // item id → dòng sheet (dòng đầu gặp id đó) — để khi import xong 1 id, báo ledger đúng DÒNG nào đã import.
    private Dictionary<string, int> _rowByImportId = new(StringComparer.Ordinal);

    /// <summary>Bắn (rowIndex, rowIndex) mỗi khi 1 dòng sheet vừa được IMPORT XONG (item id gửi lên store) →
    /// caller đẩy lên ledger Hub để Thống kê biết "shop này đã import những dòng nào".</summary>
    public event Action<int, int>? RowsDone;

    // ── Bắc cầu DOM ↔ item id qua API (BigSeller KHÔNG render source id ra HTML) ──
    // Crawl list nạp rows từ API /product/crawl/pageList.json; mỗi row có mainImage + crawlUrl. Ta bắt response
    // đó, trích shopee id từ crawlUrl (i.<shop>.<id> / product/<shop>/<id>), map theo KHOÁ ẢNH (đuôi mainImage)
    // vì DOM chỉ có <img src> để nhận diện dòng — ô .vxe-cell--label giờ là email, link tiêu đề không có href.
    private readonly object _apiLock = new();
    private readonly Dictionary<string, string> _apiImageKeyToShopeeId = new(StringComparer.Ordinal);
    private IPage? _capturePage;

    protected override string StartUrl => BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl);

    // Import xoá các file session-tab của profile TRƯỚC khi phóng Brave (tránh Brave khôi phục tab cũ).
    protected override void PrepareProfileBeforeLaunch() =>
        BraveCachePolicy.PrepareProfileForLaunch(_settings.ProfileDir, ProfileLaunchPrep.ClearSessionRestore);

    public BigSellerImportToStoreRunner(
        BigSellerWorkflowSettings settings,
        Action<string> log,
        WorkflowPauseToken? pauseToken = null,
        int laneIndex = 0,
        int laneCount = 1,
        ClaimStore? claim = null,
        bool exportCookie = true)
        : base(settings, log, pauseToken, exportCookie)
    {
        _laneIndex = Math.Max(0, laneIndex);
        _laneCount = Math.Max(1, laneCount);
        _ = claim;   // (cũ: chống trùng đa-lane) — import giờ 1 process/1 lô nên không dùng nữa
    }

    /// <summary>Phase 4 — ĐẦU PHIÊN: đảm bảo có token BigSeller TƯƠI (tự mint) cho máy này. Ủy thác cho
    /// <see cref="BigSellerAutoLogin.EnsureFreshSessionAsync"/> (dùng chung với Update/Scrape).</summary>
    private Task EnsureFreshLoginAsync(IPage page, CancellationToken ct)
        => BigSellerAutoLogin.EnsureFreshSessionAsync(
            page, _settings.AccountId, _settings.Email, _settings.Password,
            _settings.BigSellerCookieFile, _settings.DebugPort, _exportCookie, _log, ct);

    // Nạp tập item id cần import từ sheet của shop, khoảng dòng StartRow→EndRow (giống Update, nhưng KHÔNG
    // đòi cột "Tên đã sửa" vì import không cần). Khoá file khi đọc để serialize với lúc "Update tên SP" đang ghi.
    private async Task LoadImportItemIdSetAsync(CancellationToken ct)
    {
        if (_settings.ItemIdColumn <= 0 && _settings.LinkColumn <= 0)
            throw new InvalidOperationException("Cần map ít nhất 'Item ID' hoặc 'Link' để lấy item id import (mục BigSeller → Ánh xạ cột).");

        using var _ = await WorkbookFileLockHandle.AcquireAsync(_settings.WorkbookPath, ct).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rowById = new Dictionary<string, int>(StringComparer.Ordinal);
        using var wb = new XLWorkbook(_settings.WorkbookPath);
        var ws = string.IsNullOrWhiteSpace(_settings.DataSheet)
            ? wb.Worksheets.First()
            : wb.Worksheet(_settings.DataSheet);
        var start = Math.Max(2, _settings.StartRow);
        var last = ws.LastRowUsed()?.RowNumber() ?? 0;
        var end = _settings.EndRow > 0 ? Math.Min(_settings.EndRow, last) : last;

        for (var r = start; r <= end; r++)
        {
            var row = ws.Row(r);
            var colE = _settings.ItemIdColumn > 0 ? row.Cell(_settings.ItemIdColumn).GetString().Trim() : "";
            var link = _settings.LinkColumn > 0 ? row.Cell(_settings.LinkColumn).GetString().Trim() : "";
            var id = !string.IsNullOrWhiteSpace(colE) ? colE : (BigSellerCrawlHelper.ExtractShopeeId(link) ?? "");
            if (!string.IsNullOrWhiteSpace(id)) { ids.Add(id); rowById.TryAdd(id, r); }
        }

        _importIds = ids;
        _rowByImportId = rowById;
        _log($"Nạp {ids.Count} item id từ sheet '{_settings.DataSheet}' (dòng {start}→{(end >= start ? end : start)}).");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.BravePath))
            throw new FileNotFoundException($"Khong tim thay Brave: {_settings.BravePath}");

        // Cache item id của sheet (dòng StartRow→EndRow) TRƯỚC khi mở Brave — import CHỈ tick đúng các id này.
        await LoadImportItemIdSetAsync(cancellationToken).ConfigureAwait(false);
        if (_importIds.Count == 0)
        {
            _log("Sheet không có item id nào trong khoảng dòng đã đặt — không có gì để import. Kết thúc.");
            return;   // → tầng trên báo Hub "completed" (không có việc để làm)
        }

        StartBrave();
        _log($"Da goi Brave PID={_braveProcess?.Id.ToString() ?? "unknown"}, cho CDP port {_settings.DebugPort}...");
        await EnsureCdpReadyAsync(30,
            $"CDP port {_settings.DebugPort} khong san sang. Hay dong Brave BigSeller profile cu roi chay lai.",
            cancellationToken).ConfigureAwait(false);

        // Profile BigSeller là persistent — nếu còn phiên sống thì phiên trong profile luôn "tươi" hơn file tĩnh;
        // ghi đè bằng file cũ sẽ làm văng phiên -> chỉ import khi mất phiên (logic chung ở EnsureCookieAsync).
        var crawlUrl = BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl);
        await EnsureCookieAsync(cancellationToken).ConfigureAwait(false);

        _log($"Ket noi CDP port {_settings.DebugPort}...");
        await ConnectBrowserAsync(cancellationToken).ConfigureAwait(false);

        var context = _browser!.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Brave chưa có browser context.");

        var page = await FindBigSellerPageAsync(context, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy tab BigSeller.");
        AttachApiCapture(page);   // bắt pageList.json để đọc item id (DOM không có id)

        await page.BringToFrontAsync();
        await EnsureFreshLoginAsync(page, cancellationToken).ConfigureAwait(false);   // Phase 4: đầu phiên tự mint token tươi
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
        var tabSkips = 0;            // số lượt liên tiếp bị đưa về SAI tab (reload đổi ngôn ngữ) — quá ngưỡng thì dừng
        string? abortReason = null;  // lý do dừng "cứng" (thoát while rồi ném để tầng trên báo lỗi, không nuốt)
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

                // ── IMPORT THEO ITEM ID — tick từng dòng có item id ∈ sheet (dòng StartRow→EndRow) rồi bấm
                //    "Import to Stores" (thanh công cụ) → chọn shop → reload về trang 1 → quét lại. Hết trang mà
                //    không còn item id cần import ⇒ RETURN (kết thúc) → tầng trên báo Hub "completed"/"done".
                //    _importedIds chống import-trùng + đảm bảo kết thúc (độc lập tab Unclaimed/Claimed/All). ──
                await BigSellerCrawlHelper.WaitForCrawlListContentAsync(page);   // chờ có dòng HOẶC trạng thái rỗng
                // Đưa/GIỮ đúng tab Đã nhận SAU khi trang đã load. BigSeller hay TỰ đổi ngôn ngữ → reload → về tab
                // "new"; nếu không ở đúng tab thì BỎ lượt quét (đừng import nhầm từ tab "new"). Chốt lại NGAY trước
                // khi quét vì reload có thể xảy ra trong lúc chờ nội dung/API.
                var onClaimed = await SelectSourceTabIfNeededAsync(page, cancellationToken);
                await WaitForApiCoverageAsync(page, cancellationToken);          // chờ đọc xong pageList.json của trang này
                if (_settings.ImportFromClaimedTab
                    && (!onClaimed || !await BigSellerCrawlHelper.IsClaimedTabActiveAsync(page)))
                {
                    if (++tabSkips >= 12)
                    {
                        abortReason = "Tab 'Đã nhận' liên tục bị đưa về 'new' (BigSeller tự đổi ngôn ngữ khi reload?). "
                            + "Hãy đặt ngôn ngữ BigSeller = Tiếng Việt cho profile này rồi chạy lại.";
                        break;
                    }
                    _log($"Chưa ở tab Đã nhận (reload đổi ngôn ngữ?) — bỏ lượt {tabSkips}/12, thử lại.");
                    await DelayAsync(2000, cancellationToken);
                    continue;
                }
                tabSkips = 0;
                int rowCount;
                string[] checkedIds;
                try
                {
                    rowCount = await GetBodyRowCountAsync(page);
                    checkedIds = await CheckMatchingRowsOnPageAsync(page);
                }
                catch (Exception ex) when (IsTransientNavigationError(ex))
                {
                    _log($"Trang đang reload, thử lại: {ex.Message}");
                    await DelayAsync(3000, cancellationToken);
                    continue;
                }
                consecutiveErrors = 0;   // đọc được bảng → tab còn sống

                var cycleSec = Math.Max(3, _settings.ListingReloadSeconds);

                if (checkedIds.Length == 0)
                {
                    // Trang hiện tại không còn item id cần import → sang trang kế (KHÔNG reload để giữ vị trí trang).
                    if (await BigSellerCrawlHelper.ClickNextCrawlPageAsync(page, _log))
                        continue;

                    int cacheCount; lock (_apiLock) cacheCount = _apiImageKeyToShopeeId.Count;
                    if (cacheCount == 0)
                        _log("⚠ Không đọc được item id nào từ API crawl (pageList.json) — BigSeller đổi endpoint/response? Không import được gì.");
                    _log($"✔ Hết trang — không còn item id cần import ({_importedIds.Count} SP đã gửi). Kết thúc.");
                    return;   // → RunOneWorkflowAsync: "✔ xong" + PublishCompletion("completed") ⇒ báo Hub finished
                }

                _log($"Trang có {rowCount} SP — {checkedIds.Length} SP khớp item id → Import to Stores…");

                // committed = true NGAY TRƯỚC khi bấm Import trong modal: lỗi điều hướng SAU đó = đã gửi lên server.
                var committed = false;
                try
                {
                    await ClickToolbarImportAsync(page);

                    var modal = page.Locator(".ant-modal-content:visible").Last;
                    await modal.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });
                    await SelectImportShopAndConfirmAsync(modal, _settings.ShopName, () => committed = true);

                    importCount++;
                    _log($"Đã Import {checkedIds.Length} SP theo item id (#{importCount}).");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (committed && IsTransientNavigationError(ex))
                    {
                        importCount++;
                        _log($"Import lô đã gửi (#{importCount}) — trang điều hướng làm mất context.");
                    }
                    else
                    {
                        _log($"Import lô lỗi (giữ phiên, thử lại lượt sau): {ex.Message}");
                    }
                }

                // Đã gửi lên server (committed) → đánh dấu để KHÔNG import lại (dù SP còn hiện trong danh sách hay không).
                if (committed)
                    foreach (var id in checkedIds)
                    {
                        _importedIds.Add(id);
                        // Báo ledger đúng DÒNG sheet của id vừa import (nếu biết) → Thống kê "đã import dòng nào".
                        if (_rowByImportId.TryGetValue(id, out var row)) RowsDone?.Invoke(row, row);
                    }

                // Đã gom ĐỦ mọi item id cần import → DỪNG NGAY, khỏi lật hết tab (tab "Đã nhận" có thể tới ~75
                // trang). _importedIds ⊆ _importIds nên đủ số là đủ tất cả.
                if (_importedIds.Count >= _importIds.Count)
                {
                    _log($"✔ Đã import đủ {_importedIds.Count}/{_importIds.Count} SP — dừng (không lật thêm trang).");
                    return;
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

        // Thoát while do dừng "cứng" (vd tab Đã nhận cứ bị reload về 'new') → ném để tầng trên báo LỖI thật,
        // không lẫn vào nhánh "hoàn tất" (return) — tránh false-success che lỗi.
        if (abortReason is not null)
            throw new InvalidOperationException(abortReason);
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
        // Brave fork browser thật rồi stub thoát → Kill no-op, browser cũ giữ profile lock. Phải diệt
        // orphan theo --user-data-dir TRƯỚC khi mở lại cùng profile, kẻo instance mới đụng lock/port cũ.
        try { BraveProcessReaper.KillByUserDataDir(_settings.ProfileDir, _log); } catch { }

        StartBrave();
        _log($"  Đã khởi động lại Brave PID={_braveProcess?.Id.ToString() ?? "unknown"}, chờ CDP port {_settings.DebugPort}…");
        await EnsureCdpReadyAsync(30,
            $"CDP port {_settings.DebugPort} không sẵn sàng sau khi khởi động lại Brave.", ct).ConfigureAwait(false);

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
        AttachApiCapture(page);   // tab mới sau hồi phục → gắn lại listener pageList.json

        await page.BringToFrontAsync().ConfigureAwait(false);
        await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log).ConfigureAwait(false);
        await SelectSourceTabIfNeededAsync(page, ct).ConfigureAwait(false);
        _log("  ✓ Đã hồi phục tab BigSeller — chạy tiếp.");
        return page;
    }

    /// <summary>Đưa trang về tab "Đã nhận" (khi bật cờ). Trả về true nếu đã/đang ở đúng tab; false nếu 3 lần
    /// vẫn không được. KHÔNG cần → true luôn (tab "new" mặc định).</summary>
    private async Task<bool> SelectSourceTabIfNeededAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_settings.ImportFromClaimedTab)
            return true;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Gỡ guide "đổi ngôn ngữ" TRƯỚC khi click tab — chính nó hay gây reload đưa trang về tab "new".
            await BigSellerCrawlHelper.DismissGuideMasksAsync(page, _log);
            // Popup hướng dẫn đổi ngôn ngữ (KHÔNG phải mask/ant-modal) cũng chặn click — thử chọn Tiếng Việt/đóng.
            await BigSellerCrawlHelper.DismissLanguageGuideAsync(page, _log, cancellationToken);
            var ok = await BigSellerCrawlHelper.SelectClaimedTabByTextAsync(page, _log);
            if (ok) return true;

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

        _log("Cảnh báo: Không chọn được tab Đã nhận.");
        return false;
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

    // ── Bắc cầu DOM ↔ item id qua API pageList.json ─────────────────────────────
    // BigSeller KHÔNG render source item id ra HTML (ô .vxe-cell--label là email, link tiêu đề không href).
    // Nên bắt response API danh sách, trích id từ crawlUrl, và nhận diện dòng DOM qua ẢNH (mainImage ↔ <img src>).
    private void AttachApiCapture(IPage page)
    {
        if (ReferenceEquals(_capturePage, page)) return;
        if (_capturePage is not null) { try { _capturePage.Response -= OnCrawlResponse; } catch { } }
        _capturePage = page;
        page.Response += OnCrawlResponse;
    }

    private void OnCrawlResponse(object? sender, IResponse response)
    {
        if (response.Url.Contains("/product/crawl/pageList.json", StringComparison.OrdinalIgnoreCase))
            _ = CaptureCrawlRowsAsync(response);
    }

    // Đọc rows từ 1 response pageList.json → map ẢNH(đuôi mainImage) ↔ shopee id(từ crawlUrl). Gộp dồn (ảnh là
    // duy nhất theo SP nên gộp nhiều trang vẫn đúng); best-effort, nuốt lỗi để không đụng vòng lặp import.
    private async Task CaptureCrawlRowsAsync(IResponse response)
    {
        try
        {
            var text = await response.TextAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            JsonElement rows;
            if (!((data.TryGetProperty("page", out var pageEl) && pageEl.TryGetProperty("rows", out rows) && rows.ValueKind == JsonValueKind.Array)
                  || (data.TryGetProperty("rows", out rows) && rows.ValueKind == JsonValueKind.Array)))
                return;
            var added = 0;
            lock (_apiLock)
            {
                foreach (var r in rows.EnumerateArray())
                {
                    var img = r.TryGetProperty("mainImage", out var mi) ? mi.GetString() : null;
                    var url = r.TryGetProperty("crawlUrl", out var cu) ? cu.GetString() : null;
                    var id = BigSellerCrawlHelper.ExtractShopeeId(url);
                    var key = ImgKey(img);
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(id)) { _apiImageKeyToShopeeId[key] = id!; added++; }
                }
            }
            if (added > 0) _log($"Đọc {added} item id từ API crawl (cache {_apiImageKeyToShopeeId.Count}).");
        }
        catch { /* best-effort */ }
    }

    // Khoá ảnh = đoạn cuối URL ảnh (bỏ query) — cầu nối vì cả JSON (mainImage) lẫn DOM (<img src>) đều có.
    private static string ImgKey(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var noQuery = url.Split('?')[0].TrimEnd('/');
        var idx = noQuery.LastIndexOf('/');
        return idx >= 0 && idx < noQuery.Length - 1 ? noQuery[(idx + 1)..] : noQuery;
    }

    // Chờ tới khi cache API đã có ít nhất 1 ảnh của trang đang hiển thị (response pageList.json xử lý xong), để
    // không kết luận nhầm "0 khớp" chỉ vì listener chạy sau. Trang rỗng thật → thoát ngay.
    private async Task WaitForApiCoverageAsync(IPage page, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)   // ~6s
        {
            ct.ThrowIfCancellationRequested();
            string[] keys;
            try { keys = await GetVisibleImageKeysAsync(page); } catch { return; }
            if (keys.Length == 0) return;
            bool covered;
            lock (_apiLock) covered = keys.Any(k => _apiImageKeyToShopeeId.ContainsKey(k));
            if (covered) return;
            await DelayAsync(300, ct);
        }
    }

    private static Task<string[]> GetVisibleImageKeysAsync(IPage page) =>
        page.EvaluateAsync<string[]>(
            @"() => {
                const key = src => { if (!src) return ''; const u = src.split('?')[0].replace(/\/+$/, ''); const p = u.split('/'); return p[p.length - 1] || ''; };
                const set = new Set();
                for (const tr of Array.from(document.querySelectorAll('tr.vxe-body--row'))) {
                    const img = tr.querySelector('img');
                    if (img) { const k = key(img.getAttribute('src')); if (k) set.add(k); }
                }
                return Array.from(set);
            }");

    // Tick từng dòng có item id ∈ tập cần import (và CHƯA import), nhận diện dòng qua KHOÁ ẢNH lấy từ API.
    // vxe fixed-column có thể render 2 bản CÙNG rowid: bản body chứa <img>, bản fixed-left chứa checkbox HIỂN
    // THỊ → gom theo rowid rồi ghép. Trả về mảng shopee id vừa tick (lô import).
    private async Task<string[]> CheckMatchingRowsOnPageAsync(IPage page)
    {
        Dictionary<string, string> toTick;
        lock (_apiLock)
        {
            toTick = _apiImageKeyToShopeeId
                .Where(kv => _importIds.Contains(kv.Value) && !_importedIds.Contains(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
        if (toTick.Count == 0) return Array.Empty<string>();

        var clicked = await page.EvaluateAsync<string[]>(
            @"(map) => {
                const key = src => { if (!src) return ''; const u = src.split('?')[0].replace(/\/+$/, ''); const p = u.split('/'); return p[p.length - 1] || ''; };
                const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
                const byId = new Map();   // rowid -> { key, checkboxCell, icon }
                for (const tr of Array.from(document.querySelectorAll('tr.vxe-body--row'))) {
                    const rowid = tr.getAttribute('rowid');
                    if (!rowid) continue;
                    let e = byId.get(rowid);
                    if (!e) { e = { key: '', checkboxCell: null, icon: null }; byId.set(rowid, e); }
                    if (!e.key) {
                        const img = tr.querySelector('img');
                        if (img) { const k = key(img.getAttribute('src')); if (k && map[k]) e.key = k; }
                    }
                    if (!e.checkboxCell) {
                        const cb = Array.from(tr.querySelectorAll('.vxe-cell--checkbox')).find(vis);
                        if (cb) { e.checkboxCell = cb; e.icon = cb.querySelector('.vxe-checkbox--icon'); }
                    }
                }
                const clicked = [];
                for (const e of byId.values()) {
                    if (!e.key || !map[e.key] || !e.checkboxCell) continue;
                    const checked = e.icon && e.icon.classList.contains('vxe-icon-checkbox-checked');
                    if (!checked) e.checkboxCell.click();
                    clicked.push(map[e.key]);
                }
                return clicked;
            }",
            toTick);
        return clicked ?? Array.Empty<string>();
    }

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
            // Popup hướng dẫn đổi ngôn ngữ (KHÔNG phải mask/ant-modal) cũng chặn click — thử chọn Tiếng Việt/đóng.
            await BigSellerCrawlHelper.DismissLanguageGuideAsync(page, _log, CancellationToken.None);
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

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        _pauseToken?.DelayAsync(delay, cancellationToken) ?? Task.Delay(delay, cancellationToken);

    // Detach listener API pageList.json trước khi base kill Brave (phần dọn Brave chung ở BigSellerBraveRunner).
    protected override void OnBeforeDispose()
    {
        if (_capturePage is not null) { try { _capturePage.Response -= OnCrawlResponse; } catch { } _capturePage = null; }
    }
}
