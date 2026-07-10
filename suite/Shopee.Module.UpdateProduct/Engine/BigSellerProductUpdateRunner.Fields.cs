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
        // Video KHÔNG lên sau 60s — nếu do kho đầy thì bật cờ để dồn về HandleMediaEmergencyAsync. Video vốn non-fatal
        // nên KHÔNG return sớm giữa vòng: ProcessProductAsync check _mediaFullDetected NGAY SAU bước video.
        if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page)) _mediaFullDetected = true;
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
        var lastStep = "khởi tạo";   // bước gần nhất trước khi lỗi → in ở attempt cuối để biết KẸT ở đâu (catch nuốt = mù)
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (await IsImageUploadedAsync(page)) return true;
                lastStep = "chờ spc_box";
                var box = page.Locator(ImageGalleryBox).First;
                await box.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                await box.ScrollIntoViewIfNeededAsync();
                await box.ClickAsync();
                lastStep = "mở menu upload";
                var menuItem = page.Locator(ImageUploadMenuItem).First;
                await menuItem.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                lastStep = "file chooser";
                var fc = await page.RunAndWaitForFileChooserAsync(async () => await menuItem.ClickAsync(), new() { Timeout = 5000 });
                await fc.SetFilesAsync(imagePath);
                // Toast "kho đầy" bật gần như TỨC THÌ sau khi chọn file → check sớm sau ~0.5s, khỏi đốt 5s chờ ảnh-hiện
                // rồi mới biết (fast-path; toast bật trễ hơn thì nhánh fail-sau-timeout vẫn bắt qua buffer máy ghi).
                await DelayAsync(500, ct);
                if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page))
                {
                    _log("⚠ Media Center đầy (toast ngay sau khi chọn ảnh) — dừng SP này, chuyển sang quy trình dọn toàn cục.");
                    await DismissStorageNagAsync(page);
                    _mediaFullDetected = true;
                    return false;
                }
                lastStep = "chờ ảnh hiện sau upload";
                var uploaded = page.Locator(ImageUploadedImg).First;
                await uploaded.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                var src = await uploaded.GetAttributeAsync("src");
                if (!string.IsNullOrWhiteSpace(src)) return true;
            }
            catch { }

            // Kho ĐẦY (toast ant-message HOẶC popup modal) → KHÔNG dọn tại chỗ nữa: dọn cục bộ trong khi các lane
            // khác vẫn chạy = save fail hàng loạt → SP bị "fail 2 lần → bỏ oan". Dừng SP này, bật cờ để
            // HandleMediaEmergencyAsync xử lý pause-all + dọn toàn cục.
            if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page))
            {
                _log("⚠ Media Center đầy khi upload ảnh — dừng SP này, chuyển sang quy trình dọn toàn cục.");
                await DismissStorageNagAsync(page);
                _mediaFullDetected = true;
                return false;
            }
            // Fail nhưng KHÔNG khớp tín hiệu đầy: ở attempt CUỐI, log BƯỚC KẸT gần nhất (khỏi mù "im lặng") + dump toast
            // lỗi CHƯA nhận diện (1 lần/lane) để bổ sung bộ nhận diện — lần đầu gặp wording mới, log tự khai nguyên văn.
            if (attempt == maxAttempts - 1)
            {
                _log($"⚠ Upload ảnh thất bại sau {maxAttempts} lượt — kẹt ở bước: {lastStep}.");
                await BigSellerMaterialCenterCleaner.DumpErrorToastOnceAsync(page, _log, () =>
                {
                    if (_errorToastDumped) return false;
                    _errorToastDumped = true;
                    return true;
                });
            }
            await DismissStorageNagAsync(page);   // đóng popup "sắp hết hạn dung lượng" nếu có (né Expand Space) — như cũ
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
        var cfg = await HubAiConfig.GetAsync(ct).ConfigureAwait(false);
        // Parity Python: temperature 0.6 + ràng buộc độ dài trong user prompt (tránh vượt 3000 bị cắt cứng/reject).
        var userPrompt =
            $"Tên sản phẩm: {productName}\n" +
            $"Giới hạn bắt buộc: TỐI ĐA {MaxDescriptionChars} ký tự, nên trong khoảng {TargetDescriptionMinChars}–{TrimmedDescriptionMaxChars} ký tự.";
        // Backoff & timeout RIÊNG của call site này (khác NameRewrite): chờ phẳng 1500ms×lần cho mọi lỗi tạm,
        // và timeout HttpClient (OperationCanceledException dù ct chưa hủy) ném NGAY không thử lại — nên KHÔNG
        // dùng AiChat.ExecuteWithRetryAsync (mặc định 2000/15000ms×lần + coi timeout là lỗi tạm để retry).
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
                _log($"✖ Lỗi AI không thể phục hồi ({ex.StatusCode}) — dừng. Kiểm tra OpenAI API key/quota/model trên Hub (trang Cấu hình AI).");
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
