using Microsoft.Playwright;

namespace UpdateProduct;

/// <summary>Phần BigSellerProductUpdateRunner: chuỗi 14 bước điền form MỘT sản phẩm trên tab edit
/// (<c>ProcessProductAsync</c>) — tách khỏi file chính, đợt D pure move.</summary>
internal sealed partial class BigSellerProductUpdateRunner
{
    // ── process one product ──
    private async Task<bool> ProcessProductAsync(IPage page, WorkbookRecord rec, Func<Task>? onSaved, CancellationToken ct)
    {
        _lastProcessTransient = false;
        _mediaFullDetected = false;   // reset cờ kho-đầy cho SP này (upload ảnh/video/save sẽ bật khi gặp tín hiệu đầy)
        await StepAsync($"Xử lý SKU {rec.Sku}");

        await StepAsync("Sửa tên sản phẩm");
        // [1] name — CẮT ≤120 ký tự (giới hạn Shopee, tránh BigSeller báo lỗi), giữ SKU ở cuối.
        // fill fail KHÔNG làm rớt cả SP (giữ tên cũ, vẫn lưu phần còn lại như Python).
        var nameToFill = BigSellerText.TruncateProductNamePreservingSku(rec.ProductName, rec.Sku, MaxProductNameChars);
        var rawLen = (rec.ProductName ?? "").Trim().Length;
        if (rawLen > MaxProductNameChars)
            _log($"  ✂ Tên dài {rawLen} ký tự → cắt còn {nameToFill.Length} (≤{MaxProductNameChars}, giữ SKU).");
        if (!await FillProductNameAsync(page, nameToFill, ct))
            _log("  ⚠ Không điền được tên SP — giữ tên cũ, tiếp tục xử lý.");

        // THỨ TỰ MỚI: radio + import ảnh đẩy lên NGAY SAU đổi tên. Import ảnh là bước NỔ toast kho-đầy ("add from
        // computer → chọn ảnh → OK → toast") → đặt đầu để bắt tín hiệu đầy trong vài giây ĐẦU của SP rồi dừng sang
        // nhánh dọn media (media_full → pause-all → wipe), KHỎI tốn công MD5/SKU/giá/tồn/video/AI cho SP chắc chắn
        // không lưu nổi (trước đây ảnh áp chót → làm gần hết việc mới biết kho đầy). Bước Lưu có TIÊN QUYẾT ảnh-đã-lên.

        // [2] radio "Tải lên hình ảnh" / "Upload Image" — tick để hiện khối upload ảnh (div.spc_box).
        // Trước đây lọc-text-VN cứng nên bản EN ("Upload Image") KHÔNG khớp → radio không tick → không chọn được ảnh.
        try
        {
            var r = page.Locator(UploadImageRadioWrapper).Filter(new() { HasTextRegex = UploadImageRadioText }).First;
            if (await r.CountAsync() == 0) r = page.Locator(UploadImageRadioByValue).First;   // fallback độc-lập-ngôn-ngữ
            if (await r.CountAsync() > 0 && await r.IsVisibleAsync())
            {
                await r.ScrollIntoViewIfNeededAsync();
                await r.ClickAsync();
                // chờ khối upload (spc_box) hiện sau khi sizeChartContent bỏ display:none
                try { await page.Locator(ImageGalleryBox).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 4000 }); } catch { /* chờ best-effort */ }
            }
        }
        catch (Exception ex) { _log($"  ↳ Tick radio 'Tải lên hình ảnh' lỗi: {ex.Message}"); }

        // [3] image — bước Lưu TIÊN QUYẾT ảnh-đã-lên (SaveWithImageRetry không bấm Lưu khi ảnh chưa lên) → ảnh KHÔNG
        // lên = save không bao giờ được bấm → đi tiếp chỉ ĐỐT AI vô ích. Ảnh fail thì THOÁT SỚM để SP thử lại.
        var imagePath = _settings.ImagePath;
        var hasImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
        if (hasImage)
        {
            await StepAsync("Import ảnh");
            var imgOk = false;
            try { imgOk = await UploadImageWithRetryAsync(page, imagePath!, 3, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log($"  ↳ Import ảnh lỗi: {ex.Message}"); }

            if (!imgOk)
            {
                if (_mediaFullDetected) return false;   // toast/modal đã bắt được → "media_full" lo phần dọn (pause-all)
                // Ảnh fail mà KHÔNG bắt được tín hiệu đầy (toast trượt): đếm streak per-lane. 2 SP liên tiếp ảnh-fail =
                // NGHI kho đầy → chủ động RequestCleanup + coi như media_full (không ăn fail-strike; HandleMediaEmergency
                // chạy ở ranh giới vòng lặp). Dưới ngưỡng: chỉ thoát sớm (SP thử lại), TUYỆT ĐỐI không đi tiếp AI/Lưu.
                _imageUploadFailStreak++;
                _log($"⚠ Import ảnh CHƯA lên sau 3 lượt ({_imageUploadFailStreak} SP liên tiếp) — bỏ bước AI/Lưu, SP sẽ thử lại.");
                if (_imageUploadFailStreak >= 2)
                {
                    _log($"⚠ Upload ảnh fail {_imageUploadFailStreak} SP liên tiếp — NGHI kho media đầy (không bắt được toast/popup) → chủ động dọn.");
                    _mediaCoord?.RequestCleanup();
                    _mediaFullDetected = true;
                }
                return false;
            }
            _imageUploadFailStreak = 0;   // ảnh lên OK → reset streak
        }

        await StepAsync("Đồng bộ ảnh (MD5)");
        // [4] md5 sync — MD5 cũng ĐẨY ảnh vào Material Center; toast "kho đầy" bật NGAY tại đây và tự ẩn ~3s → bắt
        // tại NGUỒN (tới lúc save là mất dấu). Dính tín hiệu → thoát SP sớm (khỏi tốn SKU/giá/AI/save).
        try
        {
            var md5 = page.Locator(Md5Button).First;
            if (await md5.IsVisibleAsync())
            {
                await md5.ScrollIntoViewIfNeededAsync();
                await md5.ClickAsync();
                if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page))
                { _mediaFullDetected = true; await DismissStorageNagAsync(page); return false; }   // return KHÔNG bị catch nuốt
                try { await page.Locator(Md5CompleteStatus).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 }); } catch { /* chờ best-effort */ }
                await CloseVisibleAntModalAsync(page, 5000);
            }
        }
        catch (Exception ex) { _log($"  ↳ Đồng bộ ảnh (MD5) lỗi: {ex.Message}"); }
        // Check LẦN NỮA sau block md5, NGOÀI try để catch không nuốt mất đường return (1 query DOM, rẻ).
        if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page))
        { _mediaFullDetected = true; await DismissStorageNagAsync(page); return false; }

        await StepAsync("Điền SKU + thương hiệu");
        // [5] parent SKU
        try
        {
            var s = page.Locator(ParentSkuInput);
            if (await s.IsVisibleAsync())
            {
                await s.FillAsync(rec.Sku);
                await s.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            }
        }
        catch (Exception ex) { _log($"  ↳ Điền SKU cha lỗi: {ex.Message}"); }

        // [6] brand
        try { await SelectNoBrandAsync(page, ct); }
        catch (Exception ex) { _log($"  ↳ Chọn thương hiệu 'No Brand' lỗi: {ex.Message}"); }

        // [7] variation SKUs
        await ForEachVisibleAsync(page.Locator(VariationSkuInputs), async el =>
        {
            await el.ScrollIntoViewIfNeededAsync();
            await el.FillAsync(rec.Sku);
            await el.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            await el.EvaluateAsync("el => el.blur()");
        });

        await StepAsync("Cập nhật tồn kho + giá");
        // [8] stock — đặt TẤT CẢ biến thể về StockValue (kể cả ô đang = 0, không còn giữ nguyên ô hết hàng).
        await ForEachVisibleAsync(page.Locator(VariationStockInputs), async el =>
        {
            await el.ScrollIntoViewIfNeededAsync();
            await el.FillAsync(StockValue);
            await el.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            await el.EvaluateAsync("el => el.blur()");
        });

        // [9] price
        var newPrice = ParsePrice(rec.Price);
        await ForEachVisibleAsync(page.Locator(VariationPriceInputs), async el =>
        {
            await el.ScrollIntoViewIfNeededAsync();
            await el.FillAsync(newPrice);
            await el.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            await el.EvaluateAsync("el => el.blur()");
        });

        await StepAsync("Vận chuyển + cân nặng");
        // [10] shipping "Nhanh"
        try
        {
            var ship = page.Locator(ShippingFastWrapper).Filter(new() { HasTextString = "Nhanh" }).First;
            if (await ship.IsVisibleAsync())
            {
                await ship.ScrollIntoViewIfNeededAsync();
                if (await ship.Locator(ShippingCheckedMark).CountAsync() == 0) await ship.ClickAsync();
            }
        }
        catch (Exception ex) { _log($"  ↳ Chọn vận chuyển 'Nhanh' lỗi: {ex.Message}"); }

        // [11] weight
        try
        {
            var w = page.Locator(WeightInput);
            if (await w.IsVisibleAsync())
            {
                await w.ScrollIntoViewIfNeededAsync();
                await w.FillAsync(WeightValue);
                await w.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
                await w.EvaluateAsync("el => el.blur()");
            }
        }
        catch (Exception ex) { _log($"  ↳ Điền cân nặng lỗi: {ex.Message}"); }

        // video discovery
        var videoPath = ResolveVideoPath(rec.Sku);

        // [12] upload video (non-fatal, 3 attempts)
        if (videoPath != null)
        {
            await StepAsync("Upload video");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { if (await UploadVideoAsync(page, videoPath, ct)) break; }
                catch (Exception ex) { _log($"  ↳ Upload video lượt {attempt + 1}/3 lỗi: {ex.Message}"); }
                await DelayAsync(3000, ct);
            }
        }
        if (_mediaFullDetected) return false;   // kho đầy phát hiện lúc upload video → AI/lưu vô ích, thoát sớm cho rẻ

        await StepAsync("Tạo mô tả AI");
        // [13] AI description — rỗng sau retry = LỖI TẠM → retry vòng sau, KHÔNG xóa dòng (tránh mất SP).
        var aiContent = await GenerateDescriptionAsync(rec.ProductName ?? "", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(aiContent)) { _lastProcessTransient = true; return false; }
        if (!await UpdateDescriptionAsync(page, aiContent, ct)) return false;

        await StepAsync("Lưu sản phẩm");
        // [14] save
        return await SaveWithImageRetryAsync(page, imagePath, 3, onSaved, ct).ConfigureAwait(false);
    }
}
