using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace UpdateProduct;

internal static class BigSellerCrawlHelper
{
    public const string CrawlUrl = "https://www.bigseller.com/web/crawl/index.htm";

    /// <summary>Trích item id Shopee từ URL nguồn (i.&lt;shop&gt;.&lt;id&gt; / product/&lt;shop&gt;/&lt;id&gt;). Trang verify
    /// (captcha/traffic) → null. Dùng chung cho Update (khớp dòng) lẫn Import (nạp tập item id từ sheet).</summary>
    internal static string? ExtractShopeeId(string? url)
    {
        url ??= "";
        if (url.Contains("/verify/captcha") || url.Contains("/verify/traffic")) return null;
        foreach (var pat in new[] { @"i\.\d+\.(\d+)", @"product/\d+/(\d+)", @"i\.\d+\.(\d+)\?" })
        {
            var m = Regex.Match(url, pat);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    public static bool IsCrawlPage(IPage page, string? targetUrl = null) =>
        IsTargetCrawlUrl(page.Url ?? "", ResolveCrawlUrl(targetUrl));

    public static async Task<bool> IsCrawlPageReadyAsync(IPage page, int timeoutMs = 8000, string? targetUrl = null)
    {
        if (!IsCrawlPage(page, targetUrl))
            return false;

        const string readySelector =
            "#activeTab, " +
            ".tabs_module, " +
            "button:has-text('Import to Stores'), " +
            "a.action_btn[title='Import to Stores'], " +
            "tbody.ant-table-tbody tr, " +
            ".ant-empty, " +
            ".ant-table-placeholder";

        try
        {
            await page.WaitForSelectorAsync(readySelector, new() { State = WaitForSelectorState.Attached, Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task StopPageLoadingAsync(IPage page)
    {
        try { await page.EvaluateAsync("() => window.stop()"); } catch { }
    }

    public static async Task<bool> NavigateToCrawlUrlAsync(IPage page, string? targetUrl = null, Action<string>? log = null)
    {
        var crawlUrl = ResolveCrawlUrl(targetUrl);
        await StopPageLoadingAsync(page);
        try
        {
            await page.GotoAsync(crawlUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 12000 });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Commit navigation lỗi, thử JS: {ex.Message}");
        }

        try
        {
            await page.EvaluateAsync("url => { window.location.href = url; }", crawlUrl);
            await page.WaitForURLAsync("**/web/crawl/index.htm**", new() { WaitUntil = WaitUntilState.Commit, Timeout = 12000 });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"JS navigation lỗi: {ex.Message}");
        }

        try
        {
            await page.GotoAsync(crawlUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 12000 });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"DOM navigation lỗi: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> GoToCrawlPageAsync(IPage page, bool forceReload = false, string? targetUrl = null, Action<string>? log = null)
    {
        if (IsCrawlPage(page, targetUrl) && !forceReload)
        {
            if (await IsCrawlPageReadyAsync(page, 3000, targetUrl))
                return true;
            log?.Invoke("Đang ở Crawl List nhưng nội dung chưa sẵn sàng, reload...");
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            log?.Invoke("Chuyển về trang Crawl List...");
            try
            {
                if (!await NavigateToCrawlUrlAsync(page, targetUrl, log))
                    throw new TimeoutException("Không mở được Crawl List.");

                if (await IsCrawlPageReadyAsync(page, 10000, targetUrl))
                {
                    await Task.Delay(2000);
                    return true;
                }

                throw new TimeoutException("Crawl List mở nhưng bảng chưa sẵn sàng.");
            }
            catch (Exception ex)
            {
                if (IsCrawlPage(page, targetUrl) && await IsCrawlPageReadyAsync(page, 3000, targetUrl))
                {
                    log?.Invoke($"Crawl List đã mở, bỏ qua lỗi phụ: {ex.Message}");
                    await Task.Delay(2000);
                    return true;
                }

                log?.Invoke($"Chuyển Crawl List lỗi {attempt}/3: {ex.Message}");
                if (attempt < 3)
                {
                    await StopPageLoadingAsync(page);
                    await Task.Delay(3000);
                }
                else
                {
                    log?.Invoke($"Không mở được Crawl List. URL: {page.Url}");
                    return false;
                }
            }
        }

        return false;
    }

    public static async Task<bool> SelectClaimedTabAsync(IPage page, Action<string>? log = null) =>
        await SelectClaimedTabByTextAsync(page, log);

    public static async Task<bool> SelectClaimedTabByTextAsync(IPage page, Action<string>? log = null)
    {
        try
        {
            var result = await page.EvaluateAsync<string>(
                @"() => {
                    const normalize = v => (v || '')
                        .normalize('NFD')
                        .replace(/[̀-ͯ]/g, '')
                        .replace(/[đĐdd]/g, 'd')
                        .replace(/\s+/g, ' ')
                        .trim()
                        .toLowerCase();

                    const selectors = [
                        '#activeTab li', '#activeTab a', '#activeTab > div', '#activeTab > span',
                        '.tabs_module li', '.tabs_module a', '.tabs_module > div', '.tabs_module > span',
                        '.ant-tabs-tab', '[class*=""tab-nav""] li', '[class*=""tab-nav""] a',
                        '[class*=""tab-list""] li', '[class*=""tab-list""] a', '[class*=""tab-item""]',
                    ];
                    const seen = new Set();
                    const items = selectors
                        .flatMap(s => Array.from(document.querySelectorAll(s)))
                        .filter(el => { if (seen.has(el)) return false; seen.add(el); return true; });

                    if (items.length === 0) return 'no-items';

                    let el = items.find(item => normalize(item.textContent).includes('da nhan'));
                    if (!el && items.length >= 3) el = items[2];
                    if (!el) return 'not-found:' + items.length;

                    const isActive = el.classList.contains('active') ||
                                     el.classList.contains('ant-tabs-tab-active') ||
                                     el.getAttribute('aria-selected') === 'true';
                    if (isActive) return 'already-active';

                    (el.querySelector('a, button') || el).click();
                    return 'clicked';
                }");

            switch (result)
            {
                case "clicked":
                log?.Invoke("Đã chọn tab Đã nhận.");
                    await Task.Delay(1500);
                    await WaitForCrawlListContentAsync(page, 8000);
                    return true;
                case "already-active":
                log?.Invoke("Tab Đã nhận đã được chọn sẵn.");
                    return true;
                case "no-items":
                log?.Invoke("Không tìm thấy container tab trên trang.");
                    return false;
                default:
                log?.Invoke($"Không tìm thấy tab Đã nhận ({result}).");
                    return false;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Chọn tab Đã nhận lỗi: {ex.Message}");
            return false;
        }
    }

    /// <summary>Tab "Đã nhận" có ĐANG là tab active không (dùng cùng cách nhận diện với
    /// <see cref="SelectClaimedTabByTextAsync"/>). Dùng làm chốt chặn: reload do BigSeller tự đổi ngôn ngữ hay
    /// đưa trang về tab "new" — không xác nhận đúng tab thì KHÔNG quét/import (tránh import nhầm tab).</summary>
    public static async Task<bool> IsClaimedTabActiveAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                @"() => {
                    const normalize = v => (v || '')
                        .normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[đĐdd]/g, 'd')
                        .replace(/\s+/g, ' ').trim().toLowerCase();
                    const selectors = [
                        '#activeTab li', '#activeTab a', '#activeTab > div', '#activeTab > span',
                        '.tabs_module li', '.tabs_module a', '.tabs_module > div', '.tabs_module > span',
                        '.ant-tabs-tab', '[class*=""tab-nav""] li', '[class*=""tab-nav""] a',
                        '[class*=""tab-list""] li', '[class*=""tab-list""] a', '[class*=""tab-item""]',
                    ];
                    const seen = new Set();
                    const items = selectors
                        .flatMap(s => Array.from(document.querySelectorAll(s)))
                        .filter(el => { if (seen.has(el)) return false; seen.add(el); return true; });
                    let el = items.find(i => normalize(i.textContent).includes('da nhan'));
                    if (!el && items.length >= 3) el = items[2];
                    if (!el) return false;
                    return el.classList.contains('active') ||
                           el.classList.contains('ant-tabs-tab-active') ||
                           el.getAttribute('aria-selected') === 'true';
                }");
        }
        catch { return false; }
    }

    public static async Task WaitForCrawlListContentAsync(IPage page, int timeoutMs = 8000)
    {
        const string selector =
            "td[colid='col_10'] a.action_btn[title='Import to Stores'], " +
            ".vxe-body--column.col_10 a.action_btn[title='Import to Stores'], " +
            "a.action_btn[title='Import to Stores'], " +
            "a.action_btn:has-text('Import to Stores'), " +
            "button:has-text('Import to Stores'), " +
            "tbody.ant-table-tbody tr, " +
            ".vxe-table--body tr, " +
            ".ant-empty, " +
            ".ant-table-placeholder";

        try { await page.WaitForSelectorAsync(selector, new() { State = WaitForSelectorState.Attached, Timeout = timeoutMs }); }
        catch { }
    }

    // Selector nút "Trang kế" mặc định (Crawl List): BigSeller dùng Ant Design → .ant-pagination-next
    // (li[aria-disabled] ở trang cuối); giữ luôn .next_item cũ để tương thích.
    public const string DefaultNextPageSelector =
        ".pagination .next_item:not(.disabled), li.next_item:not(.disabled), " +
        "li.ant-pagination-next:not(.ant-pagination-disabled), .ant-pagination-next:not(.ant-pagination-disabled)";

    /// <summary>
    /// Bấm "Trang kế" trên thanh phân trang BigSeller — DÙNG CHUNG cho Crawl List (Import) lẫn Listing (Update).
    /// <paramref name="nextSelector"/> chọn nút Next chưa-disabled (trang cuối → không khớp → trả false = hết trang).
    /// Nếu truyền <paramref name="nowPageSelector"/> (nhãn "X / Y") → CHỜ nhãn ĐỔI mới coi là đã lật (bản Listing:
    /// chống kẹt/nhảy sót trang); null → chỉ chờ 1.5s cố định (bản Crawl). Sau khi lật: chờ
    /// <paramref name="readySelector"/> visible nếu có, không thì chờ nội dung crawl-list. <paramref name="delay"/>
    /// (nếu có) = hàm chờ có-nhận-pause của caller; null → Task.Delay thường.
    /// </summary>
    public static async Task<bool> ClickNextCrawlPageAsync(
        IPage page, Action<string>? log = null,
        string nextSelector = DefaultNextPageSelector,
        string? nowPageSelector = null, string? readySelector = null,
        Func<int, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        Task Wait(int ms) => delay is not null ? delay(ms, ct) : Task.Delay(ms, ct);
        try
        {
            // (Listing) đọc nhãn "X / Y" TRƯỚC để lát so sánh, xác nhận đã lật trang thật.
            string before = "";
            if (nowPageSelector is not null)
                try { before = (await page.Locator(nowPageSelector).First.InnerTextAsync(new() { Timeout = 1500 })).Trim(); } catch { }

            var clicked = await page.EvaluateAsync<bool>(
                @"(sel) => {
                    const next = document.querySelector(sel);
                    if (!next) return false;
                    if (next.getAttribute('aria-disabled') === 'true') return false;
                    const action = next.querySelector('a.paging_action, a, button') || next;
                    action.click();
                    return true;
                }", nextSelector);

            if (!clicked)
            {
                if (nowPageSelector is null) log?.Invoke("Không còn trang tiếp theo.");   // (Crawl) log; Listing im lặng như bản cũ
                return false;
            }

            if (nowPageSelector is not null)
            {
                // Chờ nhãn trang "X / Y" ĐỔI = xác nhận trang ĐÃ sang thật. Nếu bấm được nhưng nhãn KHÔNG đổi trong
                // ~10s ⇒ trang không lật (glitch) → trả FALSE (caller kết thúc lane) thay vì lặp bấm-Next vô tận.
                var changed = false;
                for (var i = 0; i < 40; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string now = "";
                    try { now = (await page.Locator(nowPageSelector).First.InnerTextAsync(new() { Timeout = 500 })).Trim(); } catch { }
                    if (!string.IsNullOrEmpty(now) && !string.Equals(now, before, StringComparison.Ordinal)) { changed = true; log?.Invoke($"→ Sang trang Listing: {before} → {now}."); break; }
                    await Wait(250);
                }
                if (!changed)
                {
                    log?.Invoke($"  (bấm Next nhưng trang không lật sau ~10s — coi như hết trang: '{before}')");
                    return false;
                }
            }
            else
            {
                log?.Invoke("Chuyển sang trang tiếp theo...");
                await Task.Delay(1500);
            }

            if (readySelector is not null)
            {
                try { await page.WaitForSelectorAsync(readySelector, new() { State = WaitForSelectorState.Visible, Timeout = 10000 }); } catch { }
                await Wait(600);
            }
            else
            {
                await WaitForCrawlListContentAsync(page, 10000);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke(nowPageSelector is not null
                ? $"  (lỗi chuyển trang Listing, coi như chưa sang được: {ex.Message})"
                : $"Chuyển trang tiếp theo lỗi: {ex.Message}");
            return false;
        }
    }

    /// <summary>Gỡ lớp phủ HƯỚNG DẪN của BigSeller (vd <c>.language_switch_guide_mask</c> — overlay nhắc đổi
    /// ngôn ngữ) đè lên trang và CHẶN click nút ("…intercepts pointer events"). Xoá hẳn các lớp mask này.
    /// Best-effort; trả số phần tử đã gỡ.</summary>
    public static async Task<int> DismissGuideMasksAsync(IPage page, Action<string>? log = null)
    {
        try
        {
            var removed = await page.EvaluateAsync<int>(
                @"() => {
                    let n = 0;
                    document.querySelectorAll('.language_switch_guide_mask, .language_switch_guide, [class*=""guide_mask""]')
                        .forEach(el => { try { el.remove(); n++; } catch (e) {} });
                    return n;
                }");
            if (removed > 0)
                log?.Invoke($"Đã gỡ {removed} lớp phủ hướng dẫn BigSeller (che nút bấm).");
            return removed;
        }
        catch { return 0; }
    }

    /// <summary>Đóng popup hướng dẫn đổi ngôn ngữ của BigSeller ("Guide: Click here to switch the language…"
    /// + menu ngôn ngữ mở sẵn) — popup này KHÔNG phải ant-modal nên DismissBlockingModal thường không đóng được,
    /// chặn click Edit trên Listing → vòng update kẹt "nhấp nháy". Ưu tiên CHỌN HẲN "Tiếng Việt" khi menu đang
    /// hiện (BigSeller nhớ lựa chọn → lần sau không hiện nữa; selector của ta hỗ trợ VN/EN); không thấy menu thì
    /// đóng nút X của guide, cuối cùng ESC. Best-effort, không ném. Trả true nếu có thao tác gì đó.</summary>
    public static async Task<bool> DismissLanguageGuideAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        // 1. Check nhanh: guide có ĐANG hiện không. Đường nóng gọi thường xuyên nên phải RẺ — chưa thấy thì
        //    thoát NGAY (khỏi chạy JS quét toàn DOM khi không có popup).
        try
        {
            var guide = page.Locator("text=switch the language").First;
            if (await guide.CountAsync() == 0 || !await guide.IsVisibleAsync())
                return false;
        }
        catch { return false; }

        // 2. Guide đang hiện → thử CHỌN "Tiếng Việt" (BigSeller nhớ lựa chọn → lần sau không hiện lại). Click
        //    element lá đúng text 'Tiếng Việt' đang visible → bubble lên parent handler đổi ngôn ngữ.
        try
        {
            var pickedVn = await page.EvaluateAsync<bool>(
                @"() => {
                    const items = Array.from(document.querySelectorAll('li,a,span,div'))
                        .filter(el => el.childElementCount === 0 && (el.textContent || '').trim() === 'Tiếng Việt'
                                   && el.offsetParent !== null);
                    if (items.length) { items[0].click(); return true; }
                    return false;
                }");
            if (pickedVn)
            {
                log?.Invoke("🌐 Popup chọn ngôn ngữ BigSeller → đã chọn Tiếng Việt.");
                await Task.Delay(1500, ct);   // đổi ngôn ngữ có thể reload nội dung
                return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 3. Không có mục ngôn ngữ để chọn → đóng nút X của guide: leo tổ tiên của node chứa text tới container,
        //    tìm phần tử close bên trong; không được thì click mọi lá visible có text '×'/'✕' gần đó.
        try
        {
            var closed = await page.EvaluateAsync<bool>(
                @"() => {
                    const nodes = Array.from(document.querySelectorAll('*'))
                        .filter(el => el.childElementCount === 0 && (el.textContent || '').includes('switch the language'));
                    for (const n of nodes) {
                        let box = n;
                        for (let i = 0; i < 6 && box; i++) {
                            const close = box.querySelector('[class*=""close"" i], .anticon-close, svg');
                            if (close) { close.click(); return true; }
                            box = box.parentElement;
                        }
                    }
                    const marks = Array.from(document.querySelectorAll('*'))
                        .filter(el => el.childElementCount === 0 && el.offsetParent !== null
                                   && ['×', '✕'].includes((el.textContent || '').trim()));
                    if (marks.length) { marks[0].click(); return true; }
                    return false;
                }");
            if (closed)
            {
                log?.Invoke("🌐 Đã đóng popup hướng dẫn đổi ngôn ngữ BigSeller (nút X).");
                await Task.Delay(300, ct);
                return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 4. Chốt hạ: ESC.
        try { await page.Keyboard.PressAsync("Escape"); return true; } catch { }
        return false;
    }

    /// <summary>"Có hiển thị trong vòng timeout không" — thay cho IsVisibleAsync(Timeout) đã obsolete:
    /// chờ tới khi phần tử visible (true) hoặc hết giờ (false). Giữ nguyên ngữ nghĩa "đợi rồi trả bool".</summary>
    private static async Task<bool> IsVisibleWithinAsync(ILocator loc, float timeoutMs)
    {
        try { await loc.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeoutMs }); return true; }
        catch { return false; }
    }

    public static async Task DismissPostImportDialogsAsync(IPage page, Action<string>? log = null)
    {
        try
        {
            var draftLink = page.Locator("a.has_underline:has-text('Hộp nháp')").First;
            if (await IsVisibleWithinAsync(draftLink, 3000))
            {
                log?.Invoke("Bỏ qua link Hộp nháp, dùng hộp thoại...");
                await page.Keyboard.PressAsync("Escape");
                await Task.Delay(500);
            }
        }
        catch { }

        for (var i = 0; i < 3; i++)
        {
            var closeBtn = page.Locator(".ant-modal-wrap:visible .ant-modal-close").Last;
            if (await closeBtn.CountAsync() == 0)
                break;

            try
            {
                if (await IsVisibleWithinAsync(closeBtn, 1000))
                {
                    await closeBtn.ClickAsync(new() { Timeout = 3000 });
                    await Task.Delay(400);
                }
                else break;
            }
            catch
            {
                break;
            }
        }

        try { await page.Keyboard.PressAsync("Escape"); } catch { }
        await Task.Delay(500);
    }

    public static async Task<bool> DeleteBrokenRowAsync(IPage page, ILocator rowElem, Action<string>? log = null)
    {
        try
        {
            var deleteBtn = rowElem.Locator("a[title='Xóa']");
            if (await deleteBtn.CountAsync() == 0)
            {
                log?.Invoke("Không tìm thấy nút Xóa");
                return false;
            }

            await deleteBtn.ClickAsync();
            var confirmBtn = page.Locator(".ant-modal-confirm-btns button.ant-btn-primary");
            await confirmBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3000 });
            await confirmBtn.ClickAsync();
            await Task.Delay(2000);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Lỗi khi xóa dòng: {ex.Message}");
            return false;
        }
    }

    public static string ResolveCrawlUrl(string? targetUrl) =>
        string.IsNullOrWhiteSpace(targetUrl) ? CrawlUrl : targetUrl.Trim();

    private static bool IsTargetCrawlUrl(string currentUrl, string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl))
            return false;

        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current))
            return currentUrl.Contains("bigseller.com/web/crawl/index.htm", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target))
            return currentUrl.Contains(targetUrl, StringComparison.OrdinalIgnoreCase);

        return current.Host.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.AbsolutePath, target.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }
}
