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

    // Toast "kho đầy" (ant-message error góc trên) — KHÁC popup modal .bs-micro-modal ở IsMediaFullPopupAsync:
    // BigSeller báo đầy qua toast, cấu trúc DOM thật (user chụp production): div.ant-message
    // .ant-message-custom-content.ant-message-error > span. Selector/text nhận diện CHỈ ở file này.
    private const string ErrorToast = "div.ant-message .ant-message-custom-content.ant-message-error";

    public static async Task<bool> IsMediaInsufficientToastAsync(IPage page)
    {
        try
        {
            var loc = page.Locator(ErrorToast);
            var n = await loc.CountAsync();
            for (var i = 0; i < n; i++)
            {
                var el = loc.Nth(i);
                if (!await el.IsVisibleAsync()) continue;
                var t = BigSellerSaveSuccessHelper.Normalize(await el.InnerTextAsync());
                // Tầng 1 — mẫu ĐÃ XÁC NHẬN từ DOM production (user chụp 2026-07-11):
                //  EN nguyên văn: "The Media Center space is insufficient, please delete images or recharge in Gallery."
                //  VN nguyên văn: "Dung lượng lưu trữ của Trung tâm Media không đủ, vui lòng xóa tư liệu trong Trung
                //                  tâm Media hoặc nạp thêm dung lượng và thử lại."
                if (t.Contains("media center") && t.Contains("insufficient")) return true;
                if (t.Contains("trung tam media") && t.Contains("khong du")) return true;
                // Tầng 2 — fallback tổ hợp rộng, đề phòng BigSeller đổi wording (mẫu mới sẽ lộ qua DumpErrorToastOnceAsync).
                var mediaWord = t.Contains("media") || t.Contains("thu vien") || t.Contains("gallery");
                var fullWord = t.Contains("khong du") || t.Contains("da day") || t.Contains("het dung luong") || t.Contains("insufficient");
                if (mediaWord && fullWord) return true;
            }
        }
        catch { }
        return false;
    }

    // Tín hiệu kho đầy TỔNG HỢP: modal (class-based, độc lập ngôn ngữ) HOẶC toast ant-message. Gọi ở upload ảnh/video.
    public static async Task<bool> IsMediaInsufficientSignalAsync(IPage page)
        => await IsMediaFullPopupAsync(page) || await IsMediaInsufficientToastAsync(page);

    // Fail nhưng KHÔNG khớp tín hiệu đầy → log NGUYÊN VĂN toast lỗi (1 lần/lane qua claimLogSlot) để bổ sung bộ nhận
    // diện: BigSeller đổi wording (đặc biệt bản ngôn ngữ mới) thì có ngay mẫu thật để thêm vào file này, khỏi ngồi canh.
    public static async Task DumpErrorToastOnceAsync(IPage page, Action<string> log, Func<bool> claimLogSlot)
    {
        try
        {
            var texts = await page.Locator("div.ant-message .ant-message-custom-content").AllInnerTextsAsync();
            var joined = string.Join(" | ", texts.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0));
            if (joined.Length == 0) return;
            if (!claimLogSlot()) return;   // claim slot CHỈ khi có text để log → không phí "1 lần/lane" lúc không có toast
            log("⚠ toast lỗi CHƯA nhận diện (gửi dòng log này để bổ sung bộ nhận diện media-đầy): " + joined);
        }
        catch { }
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
            // Mạng chậm → grid media render TRỄ; trước đây các check 'trống' ăn nhầm trạng thái loading/skeleton →
            // thoát sớm oan ("hết file để xóa" trong khi kho còn media). Chờ list SẴN SÀNG (action row hiện HOẶC
            // đếm được item) tối đa 30s, mỗi nhịp đóng popup che; hết giờ vẫn KHÔNG return, chỉ cảnh báo rồi vào vòng.
            if (!await WaitMaterialListReadyAsync(mediaPage, 30000, ct).ConfigureAwait(false))
                _log("⚠ Trang Material Center tải chậm/không render sau 30s — vẫn thử tiếp.");

            var disabledDeleteCount = 0;
            var emptyStreak = 0;   // số lần LIÊN TIẾP nghi 'trống' + 0 checkbox item (cần 2 để chốt) — reset khi thấy item.

            // Chống-oan 2 lớp cho MỌI dấu hiệu 'trống' (dùng chung cho cả 3 đường kết luận-trống bên dưới):
            //  • Lớp 1 — veto theo item: CountItemCheckboxesAsync > 0 nghĩa là list CÒN item (chỉ đang tải/ẩn nút)
            //    → KHÔNG kết luận trống: log chẩn đoán + chờ rồi retry.
            //  • Lớp 2 — xác nhận 2 lần: 0 checkbox → tăng emptyStreak; lần đầu reload + chờ sẵn-sàng rút gọn để
            //    vòng sau soi lại; đủ 2 lần LIÊN TIẾP mới chốt trống. Trả "confirmed" (caller return) / "retry" (continue).
            async Task<string> HandleSuspectedEmptyAsync(IPage page, string reason)
            {
                var count = await CountItemCheckboxesAsync(page).ConfigureAwait(false);
                if (count > 0)
                {
                    emptyStreak = 0;
                    _log($"⚠ Thấy dấu 'trống' ({reason}) nhưng còn {count} checkbox item — list có thể đang tải, thử lại.");
                    await _delay(2000, ct);
                    return "retry";
                }
                if (++emptyStreak >= 2)
                {
                    _log("✅ Material Center trống (xác nhận 2 lần) — không còn media để xóa.");
                    return "confirmed";
                }
                // Lần đầu nghi trống: mạng chậm có thể khiến grid chưa render → reload + chờ sẵn-sàng rút gọn rồi soi lại.
                _log($"… Nghi Material Center trống ({reason}) — reload xác nhận lần 2.");
                try { await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }); } catch { }
                await WaitMaterialListReadyAsync(page, 8000, ct).ConfigureAwait(false);
                await CloseMaterialPopupAsync(page);
                return "retry";
            }

            for (var loop = 1; loop <= 50; loop++)   // cap 50 vòng chống lặp vô hạn (parity Python)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await CloseMaterialPopupAsync(mediaPage);

                    // Đường (1): selector 'trống' xuất hiện → qua chống-oan 2 lớp (không return thẳng như trước).
                    if (await IsMaterialEmptyAsync(mediaPage))
                    {
                        if ((await HandleSuspectedEmptyAsync(mediaPage, "selector 'trống' xuất hiện")) == "confirmed") return;
                        continue;
                    }

                    var selectResult = await EnsureMaterialSelectAllAsync(mediaPage, ct).ConfigureAwait(false);
                    // Đường (2): click 'Chọn tất cả' không tick được → CHỈ nghi trống khi 0 checkbox (qua chống-oan 2 lớp).
                    if (selectResult == "empty")
                    {
                        if ((await HandleSuspectedEmptyAsync(mediaPage, "click 'Chọn tất cả' không tick được")) == "confirmed") return;
                        continue;
                    }
                    if (selectResult == "missing")
                    {
                        await CloseMaterialPopupAsync(mediaPage);
                        await _delay(2000, ct);
                        continue;
                    }

                    var deleteBtn = await WaitMaterialDeleteEnabledAsync(mediaPage, 8000).ConfigureAwait(false);
                    if (deleteBtn is null)
                    {
                        // Đường (3): nút xóa disabled → có thể list rỗng THẬT hoặc đang tải (chưa có item để bật nút).
                        if (await IsMaterialEmptyAsync(mediaPage))
                        {
                            if ((await HandleSuspectedEmptyAsync(mediaPage, "nút xóa disabled + có dấu 'trống'")) == "confirmed") return;
                            continue;
                        }
                        if (++disabledDeleteCount >= 3)
                        {
                            if ((await HandleSuspectedEmptyAsync(mediaPage, "nút xóa disabled ≥3 lần")) == "confirmed") return;
                            disabledDeleteCount = 0;   // chưa chốt trống (còn item) → đếm lại chuỗi disabled
                            continue;
                        }
                        await _delay(2000, ct);
                        continue;
                    }
                    disabledDeleteCount = 0;
                    emptyStreak = 0;   // có nút xóa enabled = list CÒN item → reset chuỗi nghi 'trống'.

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
        // Popup "Guide: switch the language" cũng hiện ở Material Center và CHẶN click 'Chọn tất cả' → đóng/né NGAY
        // đầu (helper check nhanh-rẻ, thoát ngay khi không có popup).
        await BigSellerCrawlHelper.DismissLanguageGuideAsync(page, _log, CancellationToken.None);
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

    // Đếm SỐ ITEM THẬT trong grid Material Center = số checkbox VISIBLE (offsetParent !== null), TRỪ: (a) checkbox
    // 'Chọn tất cả' trong action row (section.material_action_row / div.material_batch_actions), (b) checkbox trong
    // popup (.bs-micro-modal / .ant-modal, vd "Don't remind me again"). >0 = list CÒN item; ==0 = trống HOẶC đang
    // tải (chưa render). Best-effort: lỗi → -1 (không biết) để caller đừng vội kết luận trống.
    private static async Task<int> CountItemCheckboxesAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int>(
                @"() => {
                    let n = 0;
                    for (const cb of document.querySelectorAll('input[type=""checkbox""]')) {
                        if (cb.offsetParent === null) continue;                                     // ẩn / không hiển thị
                        if (cb.closest('.material_action_row, .material_batch_actions')) continue;   // 'Chọn tất cả'
                        if (cb.closest('.bs-micro-modal, .ant-modal')) continue;                     // popup che
                        n++;
                    }
                    return n;
                }");
        }
        catch { return -1; }
    }

    // Chờ trang Material Center SẴN SÀNG trước khi tin các dấu hiệu 'trống': mạng chậm → grid render trễ, skeleton
    // dễ bị đọc nhầm là rỗng. Poll mỗi ~1s tới timeoutMs, mỗi nhịp đóng popup che; thoát sớm khi (a) action row hiện
    // (= list đã render) HOẶC (b) đếm được >0 item. Trả true nếu thấy sẵn sàng, false nếu hết giờ (caller KHÔNG return).
    private async Task<bool> WaitMaterialListReadyAsync(IPage page, int timeoutMs, CancellationToken ct)
    {
        var deadline = timeoutMs;
        while (deadline > 0)
        {
            ct.ThrowIfCancellationRequested();
            await CloseMaterialPopupAsync(page);
            try
            {
                var actionRow = page.Locator("section.material_action_row, div.material_batch_actions").First;
                if (await actionRow.CountAsync() > 0 && await actionRow.IsVisibleAsync()) return true;
            }
            catch { }
            if (await CountItemCheckboxesAsync(page) > 0) return true;
            await _delay(1000, ct);
            deadline -= 1000;
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
            _log("… Click 'Chọn tất cả' nhưng checkbox vẫn un-checked → nghi hết media (caller sẽ xác nhận).");
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
