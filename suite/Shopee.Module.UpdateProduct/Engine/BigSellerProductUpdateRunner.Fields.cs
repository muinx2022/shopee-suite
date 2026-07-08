using Microsoft.Playwright;
using Shopee.Core.Ai;

namespace UpdateProduct;

// Partial của BigSellerProductUpdateRunner: sửa field trên form edit (tên/brand/video/ảnh/mô tả AI) — A2, pure move.
internal sealed partial class BigSellerProductUpdateRunner
{
    // ── [1] fill name ──
    private async Task<bool> FillProductNameAsync(IPage page, string name, CancellationToken ct)
    {
        var target = (name ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await page.EvaluateAsync("window.scrollTo(0, 0)");
                await page.WaitForSelectorAsync(ProductNameInput, new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
                var input = page.Locator(ProductNameInput).First;
                await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

                if ((await input.InputValueAsync()).Trim() == target) return true;

                await input.ScrollIntoViewIfNeededAsync();
                await input.ClickAsync(new() { Timeout = 15000 });
                await input.FillAsync(target, new() { Timeout = 15000 });
                await input.EvaluateAsync("el => el.dispatchEvent(new Event('input', { bubbles: true }))");
                await page.Keyboard.PressAsync("Space");
                await page.Keyboard.PressAsync("Backspace");
                await DelayAsync(300, ct);
                if ((await input.InputValueAsync()).Trim() == target) return true;
            }
            catch
            {
                await DelayAsync(2000, ct);
                try { await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }); } catch { }
                await DelayAsync(2000, ct);
            }
        }
        return false;
    }

    // ── [5] brand ──
    private async Task SelectNoBrandAsync(IPage page, CancellationToken ct)
    {
        var box = await FirstVisibleAsync(page.Locator(BrandBoxXPath1), page.Locator(BrandBoxXPath2), page.Locator(BrandBoxCss));
        if (box is null) return;

        if (await IsNoBrandSelectedAsync(box)) return;

        await box.ScrollIntoViewIfNeededAsync();
        await box.ClickAsync(new() { Force = true });
        await DelayAsync(400, ct);

        var search = await FirstVisibleAsync(page.Locator(BrandSearchInput1), page.Locator(BrandSearchInput2));
        if (search is not null)
        {
            try { await search.FillAsync("No brand"); }
            catch
            {
                await page.Keyboard.PressAsync("Control+A");
                await page.Keyboard.PressAsync("Backspace");
                await search.PressSequentiallyAsync("No brand", new() { Delay = 30 });
            }
        }

        try { await page.WaitForSelectorAsync(BrandDropdownReady, new() { State = WaitForSelectorState.Visible, Timeout = 10000 }); } catch { }

        var clicked = false;
        foreach (var sel in BrandOptions)
        {
            var opts = page.Locator(sel);
            var n = await opts.CountAsync();
            for (var i = 0; i < n; i++)
            {
                var opt = opts.Nth(i);
                if (!await opt.IsVisibleAsync()) continue;
                var txt = (await opt.InnerTextAsync()) ?? "";
                if (!NoBrandRegex.IsMatch(txt.Trim())) continue;
                await opt.ClickAsync(new() { Force = true });
                clicked = true;
                break;
            }
            if (clicked) break;
        }
        if (!clicked) await page.Keyboard.PressAsync("Enter");

        // verify ≤5s
        for (var i = 0; i < 20; i++)
        {
            if (await IsNoBrandSelectedAsync(box)) return;
            await DelayAsync(250, ct);
        }
    }

    private static async Task<bool> IsNoBrandSelectedAsync(ILocator box)
    {
        string val;
        try { val = await box.Locator(BrandSelectedValue).First.InnerTextAsync(new() { Timeout = 1000 }); }
        catch { try { val = await box.InnerTextAsync(); } catch { val = ""; } }
        return Normalize(val).Replace(" ", "") == "nobrand";
    }

    // ── video ──
    private string? ResolveVideoPath(string sku)
    {
        var folder = _settings.VideoFolder;
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var candidate = Path.Combine(folder, sku + ".mp4");
        if (!File.Exists(candidate)) return null;
        var dur = Mp4Duration.TryGetSeconds(candidate);
        if (dur != null && dur >= 60) return null; // bỏ video dài
        return candidate;
    }

    private async Task<bool> UploadVideoAsync(IPage page, string videoPath, CancellationToken ct)
    {
        if (!File.Exists(videoPath)) return false;
        var dur = Mp4Duration.TryGetSeconds(videoPath);
        if (dur != null && dur >= 60) return false;

        var opt = await OpenLocalVideoUploadOptionAsync(page, ct);
        if (opt is null) return false;

        var fc = await page.RunAndWaitForFileChooserAsync(async () => await opt.ClickAsync(), new() { Timeout = 10000 });
        await fc.SetFilesAsync(videoPath);

        for (var i = 0; i < 60; i++)
        {
            var ok = page.Locator(VideoSuccessSignal).First;
            if (await ok.CountAsync() > 0 && await ok.IsVisibleAsync()) return true;
            if (await DetectVideoUploadErrorAsync(page)) return false;
            await DelayAsync(1000, ct);
        }
        return false;
    }

    private async Task<ILocator?> OpenLocalVideoUploadOptionAsync(IPage page, CancellationToken ct)
    {
        await page.Keyboard.PressAsync("Escape");
        await DelayAsync(500, ct);
        var addBtn = page.Locator(AddVideoButton).First;
        if (await addBtn.CountAsync() == 0) return null;
        await addBtn.ScrollIntoViewIfNeededAsync();
        await DelayAsync(500, ct);

        for (var i = 0; i < 3; i++)
        {
            try { await addBtn.HoverAsync(); } catch { }
            await DelayAsync(1000, ct);
            var opt = page.Locator(UploadLocalVideoOpt).First;
            if (await opt.CountAsync() > 0)
            {
                try { await opt.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 }); return opt; } catch { }
            }
            try { await addBtn.ClickAsync(new() { Force = true }); } catch { }
            await DelayAsync(1000, ct);
        }
        return null;
    }

    private async Task<bool> DetectVideoUploadErrorAsync(IPage page)
    {
        foreach (var sel in VideoErrSels)
        {
            try
            {
                var loc = page.Locator(sel);
                var n = await loc.CountAsync();
                for (var i = 0; i < n; i++)
                {
                    var txt = Normalize(await loc.Nth(i).InnerTextAsync());
                    foreach (var kw in new[] { "that bai", "khong thanh cong", "loi", "fail", "failed", "error", "qua", "khong ho tro" })
                        if (txt.Contains(kw)) return true;
                }
            }
            catch { }
        }
        return false;
    }

    // ── image ──
    private async Task<bool> UploadImageWithRetryAsync(IPage page, string imagePath, int maxAttempts, CancellationToken ct)
    {
        var cleanedForFull = false;   // chỉ dọn kho 1 lần/lượt upload (dọn xong không lên được nữa thì thôi)
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (await IsImageUploadedAsync(page)) return true;
                var box = page.Locator(ImageGalleryBox).First;
                await box.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                await box.ScrollIntoViewIfNeededAsync();
                await box.ClickAsync();
                var menuItem = page.Locator(ImageUploadMenuItem).First;
                await menuItem.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                var fc = await page.RunAndWaitForFileChooserAsync(async () => await menuItem.ClickAsync(), new() { Timeout = 5000 });
                await fc.SetFilesAsync(imagePath);
                var uploaded = page.Locator(ImageUploadedImg).First;
                await uploaded.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                var src = await uploaded.GetAttributeAsync("src");
                if (!string.IsNullOrWhiteSpace(src)) return true;
            }
            catch { }

            // Đóng mọi popup dung-lượng đang chặn upload (đầy / sắp hết hạn) bằng X/Cancel (KHÔNG bấm Expand Space).
            // Riêng popup ĐẦY thì còn dọn Material Center 1 lần → vòng sau upload lại (đã có chỗ). Phá thế kẹt
            // "đầy → không lưu được → không đủ 10 SP để auto-dọn".
            var mediaFull = await IsMediaFullPopupAsync(page);
            if (await DismissStorageNagAsync(page) && mediaFull)
            {
                if (!cleanedForFull)
                {
                    cleanedForFull = true;
                    _log("⚠ Media Center đầy khi upload ảnh → dọn kho rồi thử lại.");
                    if (!await RunMediaCleanupLockedAsync(ct)) await DelayAsync(8000, ct);   // lane khác đang dọn → chờ
                    try { await page.BringToFrontAsync(); } catch { }
                }
            }
            await DelayAsync(2500, ct);
        }
        return false;
    }

    private static async Task<bool> IsImageUploadedAsync(IPage page)
    {
        try
        {
            var img = page.Locator(ImageUploadedImg).First;
            if (await img.CountAsync() == 0 || !await img.IsVisibleAsync()) return false;
            return !string.IsNullOrWhiteSpace(await img.GetAttributeAsync("src"));
        }
        catch { return false; }
    }

    // ── AI description ──
    private async Task<string> GenerateDescriptionAsync(string productName, CancellationToken ct)
    {
        var cfg = AiConfigStore.Shared.Current;
        // Parity Python: temperature 0.6 + ràng buộc độ dài trong user prompt (tránh vượt 3000 bị cắt cứng/reject).
        var userPrompt =
            $"Tên sản phẩm: {productName}\n" +
            $"Giới hạn bắt buộc: TỐI ĐA {MaxDescriptionChars} ký tự, nên trong khoảng {TargetDescriptionMinChars}–{TrimmedDescriptionMaxChars} ký tự.";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                _log($"🤖 Đang tạo mô tả AI cho: {productName}" + (attempt > 1 ? $" (lần {attempt})" : ""));
                var content = TrimDescriptionForShopee(
                    await AiChat.CompleteAsync(cfg, cfg.EffectiveDescriptionPrompt, userPrompt, ct, 0.6, 4096).ConfigureAwait(false));
                if (!string.IsNullOrWhiteSpace(content))
                {
                    _log($"✅ Đã tạo mô tả: {content.Length} ký tự");
                    return content;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (AiHttpException ex) when (ex.IsPermanent)
            {
                // Key sai / hết quota / model sai → lỗi cấu hình: retry vô ích. Ném ra để DỪNG hẳn run
                // (báo lỗi rõ) thay vì coi là "lỗi tạm" → lặp mở/đóng tab + đập endpoint AI vô hạn.
                _log($"✖ Lỗi AI không thể phục hồi ({ex.StatusCode}) — dừng. Kiểm tra OpenAI API key/quota/model trong Cài đặt.");
                throw;
            }
            catch (Exception ex) { _log($"⚠ Lỗi tạo mô tả AI (lần {attempt}): {ex.Message}"); }
            if (attempt < 3) await DelayAsync(1500 * attempt, ct);
        }
        return "";   // 3 lần vẫn rỗng → coi là LỖI TẠM (transient), KHÔNG xóa dòng (xử lý ở ProcessProduct).
    }

    private async Task<bool> UpdateDescriptionAsync(IPage page, string aiContent, CancellationToken ct)
    {
        aiContent = TrimDescriptionForShopee(aiContent);
        var ta = page.Locator(DescriptionTextarea);
        if (!await ta.IsVisibleAsync()) return false;
        await ta.ScrollIntoViewIfNeededAsync();
        await ta.ClickAsync();
        await ta.EvaluateAsync("el => el.value = ''");
        await ta.FillAsync("");
        await ta.FillAsync(aiContent);
        await ta.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
        await ta.EvaluateAsync("el => el.blur()");
        await DelayAsync(1000, ct);

        try
        {
            var cb = page.Locator(DescriptionCountBox).First;
            var txt = await cb.InnerTextAsync();
            var first = txt.Split('/')[0];
            if (int.TryParse(new string(first.Where(char.IsDigit).ToArray()), out var cnt) && cnt > MaxDescriptionChars)
                return false;
        }
        catch { }
        return true;
    }
}
