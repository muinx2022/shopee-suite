using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace UpdateProduct;

/// <summary>Phần BigSellerProductUpdateRunner: helper thao tác trang Listing (khoá dòng draft, xoá dòng,
/// chọn/mở đúng tab Listing) + nhóm string utils (giá, edit-id, cắt mô tả) — tách khỏi file chính,
/// đợt D pure move.</summary>
internal sealed partial class BigSellerProductUpdateRunner
{
    // ── helpers ──
    private async Task<string> DraftRowKeyAsync(ILocator row)
    {
        var key = await row.GetAttributeAsync(ListingRowKeyAttr);
        if (string.IsNullOrEmpty(key)) key = await row.GetAttributeAsync("data-row-key");   // bảng ant cũ
        if (!string.IsNullOrEmpty(key)) return $"key:{key}";
        // Bảng Vue mới (tr.product_native_row) KHÔNG có rowid. Lưu ý: name của checkbox là "seed+index"
        // sinh client-side → KHÁC nhau giữa các Brave/lane + đổi mỗi lần render ⇒ KHÔNG dùng làm khóa lock.
        // Dùng hash ảnh SP (cf.shopee.vn/file/<hash>): GIỐNG nhau trên mọi lane + ổn định qua reload + ~1:1
        // mỗi listing ⇒ pre-filter tốt nhất lấy được từ DOM dòng. (Lock CHỐNG TRÙNG THẬT vẫn là edit:{id} sau
        // khi mở tab — xem RunListingRowAsync; id sản phẩm KHÔNG có sẵn trong DOM/network của trang listing.)
        try
        {
            var img = row.Locator("img[src*='/file/']").First;
            if (await img.CountAsync() > 0)
            {
                var src = await img.GetAttributeAsync("src") ?? "";
                var m = Regex.Match(src, @"/file/([^/?#]+)");
                if (m.Success) return $"img:{m.Groups[1].Value}";
            }
        }
        catch { }
        try
        {
            var txt = (await row.InnerTextAsync()) ?? "";
            txt = Regex.Replace(txt, @"\s+", " ").Trim();
            if (txt.Length > 200) txt = txt[..200];
            return $"txt:{txt}";
        }
        catch { return "txt:"; }
    }

    // KHÔNG còn được gọi: theo yêu cầu, KHÔNG xóa dòng nào trên BigSeller. Giữ lại để bật lại nhanh nếu cần.
#pragma warning disable IDE0051 // private member chưa dùng (cố ý)
    private async Task DeleteListingRowAsync(ILocator row)
    {
        try
        {
            ILocator? btn = null;
            foreach (var sel in new[] { DeleteBtn1, DeleteBtn2, DeleteBtn3 })
            {
                var loc = row.Locator(sel).First;
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync()) { btn = loc; break; }
            }
            if (btn is null) return;
            await btn.ClickAsync();
            await DelayAsync(500, CancellationToken.None);

            var confirm = row.Page;
            var confirmBtns = confirm.Locator(".ant-modal-confirm-btns button").Filter(new() { HasTextString = "Xóa" }).First;
            if (await confirmBtns.CountAsync() > 0) await confirmBtns.ClickAsync();
            else { var p = confirm.Locator(DeleteConfirmPrimary).First; if (await p.CountAsync() > 0) await p.ClickAsync(); }
            await DelayAsync(2000, CancellationToken.None);
        }
        catch { }
    }
#pragma warning restore IDE0051

    private IPage? PickListingPage(IBrowserContext context)
    {
        foreach (var p in context.Pages)
            if (IsDraftPage(p.Url)) return p;
        foreach (var p in context.Pages)
            if ((p.Url ?? "").Contains("bigseller.com", StringComparison.OrdinalIgnoreCase)) return p;
        return context.Pages.FirstOrDefault();
    }

    private static bool IsDraftPage(string? url)
    {
        var u = (url ?? "").Replace(" ", "").ToLowerInvariant();
        return u.Contains("bigseller.com/web/listing/shopee/") && !u.Contains("/edit/") && u.Contains("bsstatus=1");
    }

    private async Task<bool> GoToListingPageAsync(IPage page, bool forceReload)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (IsDraftPage(page.Url) && !forceReload)
                {
                    try { await page.WaitForSelectorAsync(ListingReadySelector, new() { State = WaitForSelectorState.Visible, Timeout = 3000 }); return true; } catch { }
                }

                if (IsDraftPage(page.Url) && forceReload)
                    await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                else
                    await page.GotoAsync(ListingUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

                await DelayAsync(1500, CancellationToken.None);
                await page.WaitForSelectorAsync(ListingReadySelector, new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                await DelayAsync(1000, CancellationToken.None);
                // Dọn ngay popup hướng dẫn đổi ngôn ngữ khi vừa vào Listing (khỏi đợi tới lúc click Edit bị chặn).
                await BigSellerCrawlHelper.DismissLanguageGuideAsync(page, _log, CancellationToken.None);
                return true;
            }
            catch
            {
                try { await BigSellerCrawlHelper.StopPageLoadingAsync(page); } catch { }
                await DelayAsync(3000, CancellationToken.None);
            }
        }
        return false;
    }

    // ── string utils ──
    // Bỏ dấu tiếng Việt: impl đã DỜI sang BigSellerSaveSuccessHelper (nơi duy nhất nhận diện success); giữ wrapper
    // vì DetectSaveErrorAsync (Save.cs) còn gọi Normalize với chữ ký cũ.
    private static string Normalize(string? s) => BigSellerSaveSuccessHelper.Normalize(s);

    private static string ParsePrice(string? s)
    {
        var cleaned = new string((s ?? "").Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (string.IsNullOrEmpty(cleaned)) return "0";
        if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
        return new string(cleaned.Where(char.IsDigit).ToArray());
    }

    private static string? ExtractEditId(string? url)
    {
        var m = EditIdRegex.Match(url ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string TrimDescriptionForShopee(string? content)
    {
        var text = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (text.Length <= MaxDescriptionChars) return text;

        var targetMax = Math.Min(TrimmedDescriptionMaxChars, MaxDescriptionChars);
        var clipped = text[..targetMax].TrimEnd();
        var lowerBound = Math.Min(TargetDescriptionMinChars, Math.Max(0, targetMax - 220));

        foreach (var sep in new[] { "\n\n", "\n", ". ", "! ", "? " })
        {
            var pos = clipped.LastIndexOf(sep, StringComparison.Ordinal);
            if (pos >= lowerBound)
            {
                var end = pos + (sep is ". " or "! " or "? " ? 1 : 0);
                return clipped[..end].Trim();
            }
        }
        var sp = clipped.LastIndexOf(' ');
        if (sp >= lowerBound) return clipped[..sp].Trim();
        return clipped.Trim();
    }
}
