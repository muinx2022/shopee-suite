using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Helper dò phần tử trên trang Playwright theo <b>hiển thị</b> (<c>getClientRects</c>) + text — dùng chung cho
/// mọi bước của luồng đăng nhập (Shopee, subaccount, Microsoft, Outlook). Không chứa nghiệp vụ: chỉ "tìm cái
/// đang hiện" và "có đang hiện không"; mọi hàm nuốt lỗi từng selector (selector có thể không hợp lệ trên trang
/// hiện tại) và chỉ ném khi bị HỦY.
/// </summary>
internal static class LoginPageProbe
{
    /// <summary>True nếu có ÍT NHẤT một phần tử khớp một trong <paramref name="selectors"/> đang HIỂN THỊ
    /// (kiểm bằng <c>getClientRects</c> có kích thước &gt; 0 — KHÔNG dùng offsetParent). Một lượt quét,
    /// không poll (caller tự lặp nếu cần).</summary>
    internal static async Task<bool> IsAnyVisibleByClientRectsAsync(IPage page, string[] selectors, CancellationToken ct)
    {
        foreach (var sel in selectors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var visible = await page.EvaluateAsync<bool>(
                    @"(sel) => { for (const el of document.querySelectorAll(sel)) { const rs = el.getClientRects();"
                    + " for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } } return false; }",
                    sel).ConfigureAwait(false);
                if (visible)
                {
                    return true;
                }
            }
            catch { /* selector không dùng được trên trang này — thử selector kế */ }
        }

        return false;
    }

    /// <summary>True nếu <paramref name="el"/> đang HIỂN THỊ (getClientRects có kích thước &gt; 0). Dùng cho
    /// element handle đơn (kể cả trong iframe — eval chạy trong document của frame đó).</summary>
    internal static async Task<bool> IsElementVisibleByClientRectsAsync(IElementHandle el)
    {
        try
        {
            return await el.EvaluateAsync<bool>(
                "(node) => { const rs = node.getClientRects(); for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } return false; }")
                .ConfigureAwait(false);
        }
        catch { return false; }
    }

    /// <summary>True nếu <paramref name="selector"/> có phần tử ĐANG HIỂN THỊ (dùng cho error box Microsoft).</summary>
    internal static async Task<bool> IsSelectorVisibleAsync(IPage page, string selector)
    {
        try
        {
            var el = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
            return el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false);
        }
        catch { return false; }
    }

    /// <summary>Đọc text các <c>div[role='alert']</c> của trang (nối bằng " | "). Lỗi → chuỗi rỗng.</summary>
    internal static async Task<string> ReadAlertTextAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>(
                "() => Array.from(document.querySelectorAll(\"div[role='alert']\")).map(a => a.innerText || '').join(' | ')")
                .ConfigureAwait(false);
        }
        catch { return string.Empty; }
    }

    /// <summary>Dò phần tử ĐẦU TIÊN đang HIỂN THỊ (getClientRects) khớp một trong <paramref name="selectors"/>,
    /// poll tới hết <paramref name="timeoutMs"/>. Giống <see cref="FindFirstVisibleAsync"/> nhưng kiểm hiển
    /// thị bằng getClientRects (không offsetParent) — dùng cho form Microsoft/Outlook.</summary>
    internal static async Task<IElementHandle?> FindFirstVisibleByRectsAsync(
        IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sel in selectors)
            {
                try
                {
                    var el = await page.QuerySelectorAsync(sel).ConfigureAwait(false);
                    if (el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                    {
                        return el;
                    }
                }
                catch { /* selector không dùng được — thử selector kế */ }
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>Dò phần tử ĐẦU TIÊN đang HIỂN THỊ khớp selector VÀ có innerText khớp <paramref name="textRegex"/>
    /// (vi/en), poll tới hết <paramref name="timeoutMs"/>. Duyệt theo thứ tự selector (ưu tiên phần tử
    /// clickable trước). Chỉ quét frame chính.</summary>
    internal static async Task<IElementHandle?> FindVisibleByTextAsync(
        IPage page, string[] selectors, Regex textRegex, CancellationToken ct, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sel in selectors)
            {
                IReadOnlyList<IElementHandle> els;
                try { els = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                catch { continue; }

                foreach (var el in els)
                {
                    try
                    {
                        if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                        {
                            continue;
                        }

                        var txt = await el.InnerTextAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(txt) && textRegex.IsMatch(txt))
                        {
                            return el;
                        }
                    }
                    catch { /* detached / lỗi đọc — bỏ qua phần tử này */ }
                }
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>Như <see cref="FindVisibleByTextInFramesAsync"/> nhưng so khớp KHÔNG PHÂN BIỆT DẤU: chuẩn hóa
    /// InnerText qua <see cref="LoginParsers.NormalizeForMatch"/> (FormD + bỏ dấu + đ→d + lower) rồi kiểm CHỨA một
    /// trong <paramref name="normalizedNeedles"/> (phải ĐÃ ở dạng không dấu, chữ thường). TRỊ lỗi: text tiếng Việt
    /// trên trang MS ở dạng tổ hợp dấu (NFD) khác literal regex dựng sẵn (NFC) → Regex.IsMatch trượt dù mắt
    /// thấy giống. VD "Các cách khác để đăng nhập" NFD KHÔNG khớp regex "cách khác..." NFC.</summary>
    internal static async Task<IElementHandle?> FindByNormalizedTextInFramesAsync(
        IPage page, string[] selectors, string[] normalizedNeedles, CancellationToken ct, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                foreach (var sel in selectors)
                {
                    IReadOnlyList<IElementHandle> els;
                    try { els = await frame.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                    catch { continue; }

                    foreach (var el in els)
                    {
                        try
                        {
                            if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                            {
                                continue;
                            }

                            var txt = LoginParsers.NormalizeForMatch(await el.InnerTextAsync().ConfigureAwait(false));
                            if (txt.Length > 0 && Array.Exists(normalizedNeedles, n => txt.Contains(n, StringComparison.Ordinal)))
                            {
                                return el;
                            }
                        }
                        catch { /* detached — bỏ qua */ }
                    }
                }
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>Như <see cref="FindVisibleByTextAsync"/> nhưng quét MỌI frame của trang (thân mail HTML của
    /// Outlook thường nằm trong iframe reading-pane).</summary>
    internal static async Task<IElementHandle?> FindVisibleByTextInFramesAsync(
        IPage page, string[] selectors, Regex textRegex, CancellationToken ct, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                foreach (var sel in selectors)
                {
                    IReadOnlyList<IElementHandle> els;
                    try { els = await frame.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                    catch { continue; }

                    foreach (var el in els)
                    {
                        try
                        {
                            if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                            {
                                continue;
                            }

                            var txt = await el.InnerTextAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(txt) && textRegex.IsMatch(txt))
                            {
                                return el;
                            }
                        }
                        catch { /* detached — bỏ qua */ }
                    }
                }
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>
    /// Dò phần tử đầu tiên <b>đang hiển thị</b> khớp một trong <paramref name="selectors"/> (thử lần
    /// lượt), poll tới khi hết <paramref name="timeoutMs"/>. Trả <c>null</c> nếu không thấy. Nuốt lỗi
    /// từng selector (selector có thể không hợp lệ trên trang hiện tại).
    /// </summary>
    internal static async Task<IElementHandle?> FindFirstVisibleAsync(
        IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sel in selectors)
            {
                try
                {
                    var el = await page.QuerySelectorAsync(sel).ConfigureAwait(false);
                    if (el is not null && await el.IsVisibleAsync().ConfigureAwait(false))
                    {
                        return el;
                    }
                }
                catch
                {
                    // Selector không dùng được trên trang này — thử selector kế.
                }
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>True nếu tại điểm (x,y) của viewport, phần tử nhận sự kiện chính là el / con của el /
    /// tổ tiên của el (elementFromPoint trả node TRÊN CÙNG — bị phần tử khác đè thì trả phần tử đè).</summary>
    internal static async Task<bool> IsPointOnElementAsync(IElementHandle el, double x, double y)
    {
        try
        {
            return await el.EvaluateAsync<bool>(
                "(node, pt) => { const hit = document.elementFromPoint(pt.x, pt.y);" +
                " return !!hit && (node === hit || node.contains(hit) || hit.contains(node)); }",
                new { x, y }).ConfigureAwait(false);
        }
        catch { return false; }
    }

    /// <summary>
    /// Phần tử có <b>bounding box</b> không (đang hiển thị), <b>nuốt lỗi</b> handle DETACHED (Vue vẽ lại
    /// form sau khi map/modal re-render khiến <c>BoundingBoxAsync</c> ném) → <c>false</c> graceful, KHÔNG
    /// để exception rò lên catch ngoài cùng của <c>SetPickupAddressAsync</c> (lỗi handle biến
    /// thành "không click được", modal vẫn được Hủy).
    /// </summary>
    internal static async Task<bool> HasBoundingBoxAsync(IElementHandle el)
    {
        try { return await el.BoundingBoxAsync().ConfigureAwait(false) is not null; }
        catch { return false; }
    }
}
