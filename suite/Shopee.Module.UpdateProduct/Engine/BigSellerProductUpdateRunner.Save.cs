using Microsoft.Playwright;

namespace UpdateProduct;

// Partial của BigSellerProductUpdateRunner: luồng Lưu (Save & Publish) + phát hiện lỗi/thành công — A2, pure move.
internal sealed partial class BigSellerProductUpdateRunner
{
    // ── [12.2] save ──
    private async Task<bool> SaveWithImageRetryAsync(IPage page, string? imagePath, int maxAttempts, Func<Task>? onSaved, CancellationToken ct)
    {
        // Tab đóng TRƯỚC khi kịp submit save = chưa lưu gì → KHÔNG được báo dòng oan (đó là thất bại thật).
        if (page.IsClosed) { _log("  ↳ Lưu: tab đã đóng trước khi kịp bấm Lưu → coi là CHƯA lưu (không báo dòng oan)."); return false; }
        var wrapper = page.Locator(SaveButtonWrapper);
        if (!await wrapper.IsVisibleAsync()) { _log("  ↳ Lưu: KHÔNG thấy nút 'Save & Publish' (form chưa sẵn sàng?) → bỏ lưu, SP thử lại."); return false; }
        var hasImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Attempt TRƯỚC đã submit save; tab tự đóng giữa chừng = dấu hiệu BigSeller đóng sau khi lưu OK (nó
            // hay tự đóng tab rất nhanh). Bug cũ nuốt exception 3 vòng rồi trả false oan → Hub nhận 0 dòng; nay
            // xác nhận qua context còn sống (helper) thay vì coi tab-đóng là thất bại.
            if (page.IsClosed)
                return await BigSellerSaveSuccessHelper.ConfirmSavedThenCloseAsync(page, _context!, 5000, _log, DelayAsync, onSaved, ct);
            try
            {
                if (hasImage && !await IsImageUploadedAsync(page))
                {
                    if (!await UploadImageWithRetryAsync(page, imagePath!, 1, ct))
                    {
                        if (attempt == maxAttempts - 1)
                        {
                            // Ảnh không lên sau hết lượt → KHÔNG bấm Lưu (lưu thiếu ảnh vô nghĩa). Log rõ + check kho
                            // đầy (cùng ngữ nghĩa các điểm check khác) kẻo im lặng "▶ Lưu" rồi return false.
                            _log($"⚠ Ảnh KHÔNG upload được sau {maxAttempts} lượt → KHÔNG bấm Lưu, SP sẽ được thử lại.");
                            if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page))
                            { _mediaFullDetected = true; _log("⚠ Media Center đầy (phát hiện ở bước Lưu)."); }
                            else
                                await BigSellerMaterialCenterCleaner.DumpErrorToastOnceAsync(page, _log, () =>
                                { if (_errorToastDumped) return false; _errorToastDumped = true; return true; });
                            return false;
                        }
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
                    try { await btn.ClickAsync(); } catch { /* click mở dropdown best-effort — kết quả kiểm ngay bằng opt.IsVisibleAsync bên dưới */ }
                    try { await opt.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3000 }); } catch { /* chờ option best-effort — nhánh dưới tự fallback nút thường */ }
                }

                if (await opt.IsVisibleAsync())
                {
                    _log("  ↳ Lưu: bấm option 'Save & Publish' (dropdown).");
                    await opt.ClickAsync(new() { Force = true });   // chọn option = lưu
                }
                else
                {
                    _log("  ↳ Lưu: option dropdown KHÔNG hiện → bấm nút thường (nếu là bản dropdown thì click này chỉ mở menu — soi dump timeout).");
                    await btn.ClickAsync();   // fallback: bản nút-thường cũ → bấm là lưu
                }

                var err = await DetectSaveErrorAsync(page, 4000);
                if (err is not null) _log($"  ↳ BigSeller báo lỗi khi lưu: {err} → xử lý & thử lại.");
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
                    if (err2 is not null) _log($"  ↳ BigSeller báo lỗi khi lưu: {err2} → xử lý & thử lại.");
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

                if (await BigSellerSaveSuccessHelper.ConfirmSavedThenCloseAsync(page, _context!, 60000, _log, DelayAsync, onSaved, ct)) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log($"  ↳ Lưu attempt {attempt + 1} lỗi: {ex.Message}"); }
            await DelayAsync(2500, ct);
        }
        _log($"⚠ Lưu thất bại sau {maxAttempts} lượt — không xác nhận được đã lưu, SP sẽ thử lại vòng sau.");
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
}
