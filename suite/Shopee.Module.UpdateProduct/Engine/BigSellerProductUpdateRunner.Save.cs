using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace UpdateProduct;

// Partial của BigSellerProductUpdateRunner: luồng Lưu (Save & Publish) + phát hiện lỗi/thành công — A2, pure move.
internal sealed partial class BigSellerProductUpdateRunner
{
    // ── [12.2] save ──
    private async Task<bool> SaveWithImageRetryAsync(IPage page, string? imagePath, int maxAttempts, CancellationToken ct)
    {
        var wrapper = page.Locator(SaveButtonWrapper);
        if (!await wrapper.IsVisibleAsync()) return false;
        var hasImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (hasImage && !await IsImageUploadedAsync(page))
                {
                    if (!await UploadImageWithRetryAsync(page, imagePath!, 1, ct))
                    {
                        if (attempt == maxAttempts - 1) return false;
                        await DelayAsync(2500, ct);
                        continue;
                    }
                }

                await wrapper.ScrollIntoViewIfNeededAsync();

                // "Save & Publish" (bản EN) giờ là DROPDOWN: nút chính là ant-dropdown-trigger nên bấm nút
                // CHỈ mở menu, KHÔNG lưu — phải bấm đúng <li autoid='save_and_publish_option'>. Bản cũ (VN)
                // là nút thường → bấm nút là lưu luôn. Vì hover có thể KHÔNG mở menu (trigger='click'), ta:
                // hover thử → nếu option chưa hiện mà đây là dropdown thì click nút để mở → chờ option → bấm.
                var opt = page.Locator(SaveOption);
                var btn = wrapper.Locator("button").First;

                await btn.HoverAsync();
                await DelayAsync(500, ct);
                if (!await opt.IsVisibleAsync() && await wrapper.Locator(".ant-dropdown-trigger").CountAsync() > 0)
                {
                    try { await btn.ClickAsync(); } catch { }   // click nút = mở dropdown (chưa lưu)
                    try { await opt.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3000 }); } catch { }
                }

                if (await opt.IsVisibleAsync()) await opt.ClickAsync(new() { Force = true });   // chọn option = lưu
                else await btn.ClickAsync();   // fallback: bản nút-thường cũ → bấm là lưu

                var err = await DetectSaveErrorAsync(page, 4000);
                if (err == "brand") { await SelectNoBrandAsync(page, ct); await DelayAsync(2500, ct); continue; }
                if (err == "other")
                {
                    if (hasImage) await UploadImageWithRetryAsync(page, imagePath!, 1, ct);
                    await DelayAsync(2500, ct);
                    continue;
                }

                try
                {
                    var confirm = page.Locator(ConfirmPrimaryBtn);
                    await confirm.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                    await confirm.ClickAsync();
                    var err2 = await DetectSaveErrorAsync(page, 4000);
                    if (err2 == "brand") { await SelectNoBrandAsync(page, ct); await DelayAsync(2500, ct); continue; }
                    if (err2 == "other") { if (hasImage) await UploadImageWithRetryAsync(page, imagePath!, 1, ct); await DelayAsync(2500, ct); continue; }
                }
                catch
                {
                    if (hasImage && (!await IsImageUploadedAsync(page) || await page.Locator(ImageUploadMenuItem).First.IsVisibleAsync()))
                    {
                        await UploadImageWithRetryAsync(page, imagePath!, 1, ct);
                        await DelayAsync(2500, ct);
                        continue;
                    }
                }

                if (await WaitForSaveSuccessAsync(page, 60000))
                {
                    await ClosePageAcceptingDialogAsync(page);
                    return true;
                }
            }
            catch { }
            await DelayAsync(2500, ct);
        }
        return false;
    }

    private async Task<string?> DetectSaveErrorAsync(IPage page, int timeoutMs)
    {
        var deadline = timeoutMs;
        var step = 400;
        while (deadline > 0)
        {
            foreach (var sel in SaveErrSels)
            {
                try
                {
                    var loc = page.Locator(sel).First;
                    if (await loc.CountAsync() == 0) continue;
                    var txt = Normalize(await loc.InnerTextAsync());
                    if (txt.Contains("brand cannot be empty")) return "brand";
                    if (txt.Contains("bieu do kich co khong duoc de trong")) return "other";
                }
                catch { }
            }
            await DelayAsync(step, CancellationToken.None);
            deadline -= step;
        }
        return null;
    }

    private async Task<bool> WaitForSaveSuccessAsync(IPage page, int timeoutMs)
    {
        var deadline = timeoutMs;
        var step = 250;
        while (deadline > 0)
        {
            try
            {
                // Gộp text MỌI modal đang hiện (thường chỉ 1 = modal thành công) → khớp tín hiệu thành công song ngữ.
                var texts = await page.Locator(VisibleModal).AllInnerTextsAsync();
                var m = Normalize(string.Join(" \n ", texts));
                if (m.Contains("thao tac thanh cong") || m.Contains("successfully") ||
                    m.Contains("de trinh") || m.Contains("pending by shopee") ||
                    Regex.IsMatch(m, @"publishing\s*/\s*failed\s*/\s*active") ||
                    m.Contains("close this page") || m.Contains("dong trang nay") ||
                    m.Contains("create next product") || m.Contains("tao san pham tiep"))
                    return true;
            }
            catch { }
            await DelayAsync(step, CancellationToken.None);
            deadline -= step;
        }
        return false;
    }
}
