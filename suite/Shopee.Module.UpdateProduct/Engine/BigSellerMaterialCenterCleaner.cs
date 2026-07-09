using Microsoft.Playwright;

namespace UpdateProduct;

/// <summary>
/// Dọn "thư viện ảnh" (Material Center) của BigSeller định kỳ trong lúc Update sản phẩm — tách khỏi
/// <see cref="BigSellerProductUpdateRunner"/> (port image_manager.py). Mỗi lần VÀO sửa 1 SP BigSeller đã đẩy
/// ảnh vào Material Center (kể cả khi lưu fail); không dọn thì đầy quota → popup cảnh báo dung lượng CHẶN
/// upload ảnh các SP sau. Đếm theo số lần BẮT ĐẦU sửa SP (vào trang edit và thực sự điền/sửa), PER-LANE
/// (mỗi runner 1 cleaner), nhưng mọi lane DÙNG CHUNG 1 account (Material Center server-side chung)
/// nên khoá qua <see cref="ClaimStore"/> để mỗi lần chỉ 1 lane wipe — lane khác bỏ qua vì 1 wipe đã dọn
/// sạch cho cả account. Nhận sẵn browser context (mở tab Material Center), ClaimStore, log + delegate delay
/// (tôn trọng pause) + overlay từ runner để GIỮ NGUYÊN hành vi.
/// </summary>
internal sealed class BigSellerMaterialCenterCleaner
{
    internal const string MaterialCenterUrl = "https://www.bigseller.com/web/product/materialCenter/index.htm";
    // Sau mỗi X lần BẮT ĐẦU sửa SP (vào trang edit + thực sự điền/sửa, kể cả lưu fail) thì xóa sạch Material
    // Center (thư viện ảnh) — mirror main.py DELETE_IMAGES_AFTER.
    private const int DeleteMediaAfterUpdates = 10;

    // ── Material Center (dọn media định kỳ — selector verbatim từ image_manager.py) ──
    // BigSeller render 2 ngôn ngữ (VN/EN). ƯU TIÊN selector CẤU TRÚC (class/icon, độc-lập-ngôn-ngữ) rồi mới
    // tới text 2 thứ tiếng. Đã đối chiếu HTML EN: Select All = label.bs-antd-check-all; Bulk Delete =
    // button:has(i.bsicon_trash_2); nút xác nhận trong modal bs-micro = button.bs-micro-btn-dangerous.
    // (Lọc-text-VN cũ "Chọn tất cả"/"Xóa hàng loạt"/"Xóa" KHÔNG khớp EN → nếu thiếu fallback cấu trúc thì
    // dọn media im lặng thất bại → kho ảnh đầy quota → popup "Media Center space is insufficient" chặn upload.)
    private static readonly string[] MaterialPopupCloseSels =
    {
        "button.ant-modal-close",
        "button[aria-label='Close']",
        ".bs-micro-modal-close",
        "div.ant-modal-footer button.ant-btn:has-text('Hủy')",
        "div.ant-modal-footer button.ant-btn:has-text('Cancel')",
    };
    private static readonly string[] MaterialSelectAllSels =
    {
        // KHÔNG dùng '.bs-antd-check-all' TRẦN: popup "sắp hết hạn dung lượng" cũng có label.bs-antd-check-all
        // ("Don't remind me again") → dễ tick nhầm. Bắt buộc scope trong action row, hoặc khớp text "Select All".
        "section.material_action_row label.bs-antd-check-all",
        "div.material_batch_actions label.bs-antd-check-all",
        "section.material_action_row label.bs-micro-checkbox-wrapper:has-text('Select All')",
        "section.material_action_row label.bs-micro-checkbox-wrapper:has-text('Chọn tất cả')",
        "label.bs-micro-checkbox-wrapper:has-text('Select All')",
        "label.bs-micro-checkbox-wrapper:has-text('Chọn tất cả')",
        "label.ant-checkbox-wrapper:has-text('Chọn tất cả')",
    };
    private static readonly string[] MaterialDeleteBatchSels =
    {
        "section.material_action_row button:has(i.bsicon_trash_2)",
        "button:has(i.bsicon_trash_2)",
        "section.material_action_row button:has-text('Xóa hàng loạt')",
        "section.material_action_row button:has-text('Bulk Delete')",
        "section.material_action_row button:has-text('Batch Delete')",
        "button:has-text('Xóa hàng loạt')",
        "button:has-text('Bulk Delete')",
        "button.ant-btn-success:has-text('Xóa')",
        ".ant-btn-success",
    };
    private static readonly string[] MaterialEmptySels =
    {
        "section.material_state_panel .bs-micro-empty",
        ".bs-micro-empty",
        ".bs-micro-empty-description",
        "div.page_list_empty",
        ".page_list_empty",
    };
    private static readonly string[] MaterialDeleteConfirmSels =
    {
        ".bs-micro-modal-confirm-btns button.bs-micro-btn-dangerous",
        ".bs-micro-modal-confirm-btns button.bs-micro-btn-primary",
        ".bs-micro-modal-confirm-btns button:has-text('Xóa')",
        ".bs-micro-modal-confirm-btns button:has-text('Delete')",
        ".bs-micro-modal-confirm-btns button:has-text('Confirm')",
        ".bs-micro-modal-confirm button:has-text('Xóa')",
        "button.ant-btn-primary:has-text('Xác nhận')",
        "button.ant-btn-primary:has-text('Confirm')",
        "button.ant-btn-primary:has-text('OK')",
        "button.ant-btn-primary:has-text('Delete')",
        "button:has-text('Xác nhận')",
        ".ant-modal button.ant-btn-primary",
    };

    // BigSeller có 2 popup dung-lượng (đều .bs-micro-modal, đều có nút "Expand Space" = bs-micro-btn-primary =
    // nâng cấp/mua thêm → TUYỆT ĐỐI KHÔNG bấm). Nhận diện & đóng bằng CLASS nên độc-lập-ngôn-ngữ (VN/EN như nhau):
    //  • "Insufficient media storage space" (.space_insufficient_modal_*): kho ĐẦY → CHẶN upload → cần DỌN kho.
    //  • "Media storage space is about to expire" (.space_recharge_modal_*): chỉ NHẮC gia hạn (chưa đầy) → chỉ ĐÓNG.
    // Đóng bằng X (bs-micro-modal-close) hoặc Cancel (bs-micro-btn-default); Expand Space là bs-micro-btn-primary → né.
    private const string MediaFullTitle = ".space_insufficient_modal_title, .space_insufficient_modal_desc";
    private static readonly string[] StorageNagDismissSels =
    {
        ".bs-micro-modal:has(.space_insufficient_modal_title) button.bs-micro-modal-close",
        ".bs-micro-modal:has(.space_recharge_modal_title) button.bs-micro-modal-close",
        ".space_insufficient_modal_actions button.bs-micro-btn-default",
        ".space_recharge_modal_actions button.bs-micro-btn-default",
        ".bs-micro-modal:has(.space_insufficient_modal_title) button[aria-label='Close']",
        ".bs-micro-modal:has(.space_recharge_modal_title) button[aria-label='Close']",
    };

    private readonly IBrowserContext? _context;
    private readonly ClaimStore? _claim;
    private readonly Action<string> _log;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly Func<string, Task> _overlay;

    // Số lần BẮT ĐẦU sửa SP (per-lane) từ lần dọn media gần nhất → đạt DeleteMediaAfterUpdates thì xóa Material Center.
    private int _editStartCount;

    public BigSellerMaterialCenterCleaner(
        IBrowserContext context, ClaimStore? claim, Action<string> log,
        Func<int, CancellationToken, Task> delay, Func<string, Task> overlay)
    {
        _context = context;
        _claim = claim;
        _log = log;
        _delay = delay;
        _overlay = overlay;
    }

    // ── dọn media định kỳ (parity main.py _record_success → image_manager.delete_all_images) ──
    // Đếm theo số lần BẮT ĐẦU sửa SP (KHÔNG phải số lần lưu thành công): mỗi lần VÀO sửa 1 SP BigSeller đã đẩy
    // ảnh vào "thư viện" (Material Center) kể cả khi lưu fail; không dọn thì đầy quota → popup cảnh báo dung
    // lượng CHẶN upload ảnh các SP sau. Đếm PER-LANE, nhưng mọi lane DÙNG CHUNG 1 account (Material Center
    // server-side chung) nên khoá qua ClaimStore để mỗi lần chỉ 1 lane wipe — lane khác bỏ qua vì 1 wipe đã
    // dọn sạch cho cả account.

    /// <summary>Đánh dấu vừa BẮT ĐẦU sửa 1 SP (đã vào trang edit + thực sự điền/sửa) → tăng bộ đếm. Gọi TRƯỚC
    /// khi lưu để đếm cả SP lưu fail (ảnh đã bị đẩy vào Material Center rồi).</summary>
    public void RecordEditStart()
    {
        _editStartCount++;
        _log($"✏ Bắt đầu sửa SP {_editStartCount}/{DeleteMediaAfterUpdates} (từ lần dọn media trước).");
    }

    /// <summary>Đạt ngưỡng BẮT ĐẦU sửa DeleteMediaAfterUpdates SP thì xóa sạch Material Center; chưa đủ thì
    /// no-op rẻ (chỉ so bộ đếm). Gọi từ OuterLoopAsync sau khi tab edit đã đóng — KHÔNG chạy giữa lúc đang sửa.</summary>
    public async Task MaybeClearMediaAsync(IPage listingPage, CancellationToken ct)
    {
        if (_editStartCount < DeleteMediaAfterUpdates) return;
        _editStartCount = 0;

        _log(new string('=', 50));
        _log($"ĐÃ BẮT ĐẦU SỬA {DeleteMediaAfterUpdates} SP → XÓA THƯ VIỆN ẢNH (Material Center)");
        _log(new string('=', 50));
        try { await RunMediaCleanupLockedAsync(ct).ConfigureAwait(false); }
        finally { try { if (!listingPage.IsClosed) await listingPage.BringToFrontAsync(); } catch { } }
    }

    // Dọn Material Center có KHÓA chống trùng đa-lane (mọi lane chung 1 account → 1 wipe dọn cho cả account).
    // Trả về true nếu CHÍNH lane này đã chạy wipe; false nếu lane khác đang giữ khóa (caller nên chờ nó xong).
    public async Task<bool> RunMediaCleanupLockedAsync(CancellationToken ct)
    {
        const string mediaLock = "media-cleanup";
        if (_claim is not null && !_claim.TryClaim(mediaLock))
        {
            _log("  ↳ Lane khác đang dọn Material Center — bỏ qua (1 wipe đã dọn chung cho cả account).");
            return false;
        }
        try
        {
            await _overlay("🗑️ Dọn Material Center…");
            await DeleteAllMediaAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally { _claim?.Release(mediaLock); }
    }

    // Popup "Insufficient media storage space" đang hiện? (chặn upload ảnh)
    public static async Task<bool> IsMediaFullPopupAsync(IPage page)
    {
        try
        {
            var el = page.Locator(MediaFullTitle).First;
            return await el.CountAsync() > 0 && await el.IsVisibleAsync();
        }
        catch { return false; }
    }

    // Đóng popup dung-lượng (đầy HOẶC sắp hết hạn) bằng X/Cancel — KHÔNG bao giờ bấm "Expand Space". True nếu có đóng.
    public async Task<bool> DismissStorageNagAsync(IPage page)
    {
        foreach (var sel in StorageNagDismissSels)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync(new() { Force = true, Timeout = 2000 });
                    await page.WaitForTimeoutAsync(600);
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>Xóa TẤT CẢ media trong BigSeller Material Center (port image_manager.delete_all_images).
    /// Mở tab Material Center rồi lặp: đóng popup → "Chọn tất cả" → "Xóa hàng loạt" → xác nhận, tới khi trống
    /// hoặc nút xóa disabled nhiều lần. Best-effort: nuốt mọi lỗi (trừ hủy), KHÔNG bao giờ ném ra chặn vòng update.</summary>
    private async Task DeleteAllMediaAsync(CancellationToken ct)
    {
        if (_context is null) return;
        IPage? mediaPage = null;
        try
        {
            mediaPage = await _context.NewPageAsync();
            await mediaPage.GotoAsync(MaterialCenterUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await mediaPage.BringToFrontAsync();
            _log("📂 Đã mở Material Center");
            await _delay(3000, ct);
            await CloseMaterialPopupAsync(mediaPage);
            await _delay(1000, ct);

            var disabledDeleteCount = 0;
            for (var loop = 1; loop <= 50; loop++)   // cap 50 vòng chống lặp vô hạn (parity Python)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await CloseMaterialPopupAsync(mediaPage);

                    if (await IsMaterialEmptyAsync(mediaPage)) { _log("✅ Material Center trống — không còn media để xóa."); return; }

                    var selectResult = await EnsureMaterialSelectAllAsync(mediaPage, ct).ConfigureAwait(false);
                    if (selectResult == "empty") return;
                    if (selectResult == "missing")
                    {
                        await CloseMaterialPopupAsync(mediaPage);
                        await _delay(2000, ct);
                        continue;
                    }

                    var deleteBtn = await WaitMaterialDeleteEnabledAsync(mediaPage, 8000).ConfigureAwait(false);
                    if (deleteBtn is null)
                    {
                        if (await IsMaterialEmptyAsync(mediaPage)) { _log("✅ Material Center trống — không còn media để xóa."); return; }
                        if (++disabledDeleteCount >= 3) { _log("✅ Nút xóa vẫn disabled sau nhiều lần chọn tất cả → coi như đã hết media."); return; }
                        await _delay(2000, ct);
                        continue;
                    }
                    disabledDeleteCount = 0;

                    await deleteBtn.ScrollIntoViewIfNeededAsync();
                    await _delay(500, ct);
                    await deleteBtn.ClickAsync(new() { Force = true });
                    await _delay(1500, ct);

                    await ConfirmMaterialDeleteAsync(mediaPage, ct).ConfigureAwait(false);
                    await _delay(3000, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log($"⚠ Lỗi khi dọn Material Center (vòng {loop}): {ex.Message}");
                    await CloseMaterialPopupAsync(mediaPage);
                    await _delay(2000, ct);
                }
            }
            _log("✅ Kết thúc dọn Material Center (đạt trần 50 vòng).");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log($"❌ Lỗi dọn Material Center: {ex.Message}"); }
        finally
        {
            if (mediaPage is not null) { try { await mediaPage.CloseAsync(); } catch { } }
        }
    }

    private async Task CloseMaterialPopupAsync(IPage page)
    {
        // Popup dung-lượng (đầy/sắp hết hạn) chặn trang Material Center → đóng bằng X/Cancel trước (né "Expand Space");
        // cũng để "Don't remind me again" của nó không nhiễu FindMaterialSelectAllAsync (cùng class bs-antd-check-all).
        if (await DismissStorageNagAsync(page)) return;
        foreach (var sel in MaterialPopupCloseSels)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync(new() { Force = true, Timeout = 2000 });
                    await page.WaitForTimeoutAsync(800);
                    return;
                }
            }
            catch { }
        }
    }

    private static async Task<bool> IsMaterialEmptyAsync(IPage page)
    {
        foreach (var sel in MaterialEmptySels)
        {
            try
            {
                var el = page.Locator(sel).First;
                if (await el.CountAsync() > 0 && await el.IsVisibleAsync()) return true;
            }
            catch { }
        }
        return false;
    }

    private static async Task<ILocator?> FindMaterialSelectAllAsync(IPage page)
    {
        foreach (var sel in MaterialSelectAllSels)
        {
            try
            {
                var loc = page.Locator(sel).First;
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync()) return loc;
            }
            catch { }
        }
        return null;
    }

    private static async Task<bool> IsMaterialSelectAllCheckedAsync(ILocator selectAll)
    {
        try
        {
            var checkbox = selectAll.Locator("input[type='checkbox']").First;
            if (await checkbox.CountAsync() > 0) return await checkbox.IsCheckedAsync();
        }
        catch { }
        try { return ((await selectAll.GetAttributeAsync("class")) ?? "").ToLowerInvariant().Contains("checked"); }
        catch { return false; }
    }

    private static async Task<ILocator?> FindMaterialDeleteButtonAsync(IPage page)
    {
        foreach (var sel in MaterialDeleteBatchSels)
        {
            try
            {
                var loc = page.Locator(sel).First;
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync()) return loc;
            }
            catch { }
        }
        return null;
    }

    private static async Task<bool> IsMaterialButtonEnabledAsync(ILocator button)
    {
        try { if (!await button.IsEnabledAsync()) return false; } catch { }
        try { if (await button.GetAttributeAsync("disabled") is not null) return false; } catch { }
        try { if (string.Equals(await button.GetAttributeAsync("aria-disabled"), "true", StringComparison.OrdinalIgnoreCase)) return false; } catch { }
        try { if (((await button.GetAttributeAsync("class")) ?? "").ToLowerInvariant().Contains("disabled")) return false; } catch { }
        return true;
    }

    private static async Task<ILocator?> WaitMaterialDeleteEnabledAsync(IPage page, int timeoutMs)
    {
        var deadline = timeoutMs;
        ILocator? last = null;
        while (deadline > 0)
        {
            last = await FindMaterialDeleteButtonAsync(page);
            if (last is not null && await IsMaterialButtonEnabledAsync(last)) return last;
            await page.WaitForTimeoutAsync(300);
            deadline -= 300;
        }
        return last is not null && await IsMaterialButtonEnabledAsync(last) ? last : null;
    }

    // Trả về "checked" / "empty" (click nhưng không tick được → coi như hết media) / "missing" / "unknown".
    private async Task<string> EnsureMaterialSelectAllAsync(IPage page, CancellationToken ct)
    {
        var selectAll = await FindMaterialSelectAllAsync(page);
        if (selectAll is null) return "missing";
        if (await IsMaterialSelectAllCheckedAsync(selectAll)) { _log("☑️ 'Chọn tất cả' đã được chọn."); return "checked"; }

        _log("☑️ Click 'Chọn tất cả'.");
        var targets = new[]
        {
            selectAll.Locator("span.bs-micro-checkbox-inner").First,
            selectAll.Locator("input[type='checkbox']").First,
            selectAll,
        };
        foreach (var target in targets)
        {
            try
            {
                if (await target.CountAsync() == 0 || !await target.IsVisibleAsync()) continue;
                await target.ScrollIntoViewIfNeededAsync();
                await _delay(300, ct);
                await target.ClickAsync(new() { Force = true, Timeout = 3000 });
                for (var i = 0; i < 12; i++)
                {
                    await _delay(250, ct);
                    var refreshed = await FindMaterialSelectAllAsync(page);
                    if (refreshed is not null && await IsMaterialSelectAllCheckedAsync(refreshed)) return "checked";
                    if (await WaitMaterialDeleteEnabledAsync(page, 250) is not null) return "checked";
                }
            }
            catch { }
        }

        // Fallback: click qua JS (mirror _ensure_select_all_selected của Python).
        try
        {
            var changed = await page.EvaluateAsync<bool>(
                @"() => {
                    const label = Array.from(document.querySelectorAll('label'))
                        .find(el => { const t = el.textContent || ''; if (t.includes('Chọn tất cả') || t.includes('Select All')) return true; return el.classList.contains('bs-antd-check-all') && !!el.closest('.material_action_row, .material_batch_actions'); });
                    if (!label) { return false; }
                    const input = label.querySelector('input[type=""checkbox""]');
                    if (input && !input.checked) {
                        input.click();
                        input.dispatchEvent(new Event('input', { bubbles: true }));
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                        return true;
                    }
                    label.click();
                    return true;
                }");
            if (changed)
            {
                for (var i = 0; i < 16; i++)
                {
                    await _delay(250, ct);
                    var refreshed = await FindMaterialSelectAllAsync(page);
                    if (refreshed is not null && await IsMaterialSelectAllCheckedAsync(refreshed)) return "checked";
                    if (await WaitMaterialDeleteEnabledAsync(page, 250) is not null) return "checked";
                }
            }
        }
        catch { }

        var final = await FindMaterialSelectAllAsync(page);
        if (final is not null && !await IsMaterialSelectAllCheckedAsync(final))
        {
            _log("✅ Click 'Chọn tất cả' nhưng checkbox vẫn un-checked → coi như đã hết media.");
            return "empty";
        }
        _log("⚠ Click 'Chọn tất cả' không xác nhận được trạng thái checkbox.");
        return "unknown";
    }

    private async Task ConfirmMaterialDeleteAsync(IPage page, CancellationToken ct)
    {
        await _delay(1000, ct);
        foreach (var sel in MaterialDeleteConfirmSels)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync(new() { Force = true, Timeout = 2000 });
                    _log("✅ Đã xác nhận xóa media.");
                    await _delay(3000, ct);
                    return;
                }
            }
            catch { }
        }
    }
}
