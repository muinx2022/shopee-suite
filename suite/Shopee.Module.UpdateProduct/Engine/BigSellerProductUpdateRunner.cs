using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Coordination;
using Shopee.Core.Progress;

namespace UpdateProduct;

/// <summary>
/// Cập nhật sản phẩm trên BigSeller bằng C# + Playwright (thay cho main.py Python).
/// Quét trang Listing (bsStatus=1), mở từng sản phẩm vào tab edit, đối chiếu workbook theo
/// Shopee item id, rồi điền tên/SKU/giá/tồn/brand/cân nặng/ảnh/video + mô tả AI và lưu.
/// Selector giữ nguyên verbatim từ bản Python.
/// </summary>
internal sealed partial class BigSellerProductUpdateRunner : BigSellerBraveRunner
{
    // ── CONFIG (từ main.py CONFIG) ──
    // Tồn kho / cân nặng / kênh vận chuyển KHÔNG còn là hằng ở đây: đọc từ cấu hình per-shop do HUB đặt
    // (_settings.UpdateStockValue/UpdateWeightValue/UpdateShippingChannel — đã hợp lệ hoá, rỗng đã thay bằng
    // hằng BigSellerShop.DefaultUpdate*). Xem BigSellerShop để biết giá trị mặc định 30069/500/"Nhanh".
    private string StockValue => _settings.UpdateStockValue;
    private string WeightValue => _settings.UpdateWeightValue;
    private string ShippingChannel => _settings.UpdateShippingChannel;
    private const int MaxProductNameChars = 120;   // Shopee giới hạn tên SP 120 ký tự (BigSeller báo lỗi nếu vượt)
    private const int MaxDescriptionChars = 3000;
    private const int TrimmedDescriptionMaxChars = 2900;
    private const int TargetDescriptionMinChars = 2700;
    private const string ListingUrl = "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";

    private IBrowserContext? _context;

    private IReadOnlyDictionary<string, WorkbookRecord> _records = new Dictionary<string, WorkbookRecord>();

    /// <summary>Bắn (rowIndex, rowIndex) mỗi khi 1 dòng sheet vừa UPDATE XONG (lưu thật, result="ok") →
    /// caller đẩy lên ledger Hub để Thống kê biết "shop này đã update những dòng nào".</summary>
    public event Action<int, int>? RowsDone;

    // SP đã xử lý / bỏ qua (gồm cả "không có trong sheet") → đánh dấu để vòng quét sau KHÔNG mở/không xử lý lại.
    // KHÔNG còn xóa dòng nào trên BigSeller → "skip" là cách duy nhất để tiến tới dòng kế.
    private readonly HashSet<string> _skippedRowKeys = new();
    private readonly HashSet<string> _skippedEditIds = new();
    private readonly Dictionary<string, int> _failCounts = new();
    // True nếu ProcessProduct fail do LỖI TẠM (AI rỗng/mạng) → caller RETRY, KHÔNG xóa dòng (tránh mất SP).
    private bool _lastProcessTransient;
    // True nếu phát hiện Material Center ĐẦY giữa lúc xử lý SP (toast/popup lúc upload ảnh/video/save) → thoát SP sớm,
    // dồn về HandleMediaEmergencyAsync (pause-all + dọn toàn cục) thay vì fail lẻ tẻ → SP bị "fail 2 lần → bỏ oan".
    // Reset đầu mỗi ProcessProductAsync.
    private bool _mediaFullDetected;
    // Đã dump 1 toast lỗi CHƯA-nhận-diện chưa (throttle 1 lần/lane để khỏi spam khi lỗi lặp).
    private bool _errorToastDumped;
    // Số SP LIÊN TIẾP (per-lane) mà import ảnh fail dù KHÔNG bắt được toast/popup đầy → ngưỡng 2 = NGHI kho đầy
    // (detection trượt) → chủ động RequestCleanup. Reset khi ảnh lên OK hoặc sau khi kho vừa được dọn (Generation đổi).
    private int _imageUploadFailStreak;
    // Generation của coordinator lần cuối lane này thấy (biết kho vừa dọn sạch giữa 2 vòng — chỉ đồng bộ, không hành động thêm).
    private int _lastSeenMediaGen;
    // Đã log chẩn đoán "listing 0 dòng" chưa (log 1 lần/đợt-trống để khỏi spam mỗi vòng chờ).
    private bool _emptyListingDiagLogged;

    // ── Bộ đếm tổng kết lane (soi "chạy hàng giờ mà Thống kê 0 dòng"): update OK / bỏ qua / không-trong-sheet /
    //    số dòng THỰC BÁO lên ledger. _reportedRows CHỈ tăng khi thực bắn RowsDone (LineIndex>0) → nếu OK cao mà
    //    _reportedRows=0 thì lỗi ở tầng ledger/Hub, không phải ở đây. ──
    private int _okCount;
    private int _skipCount;
    private int _notInSheetCount;
    private int _reportedRows;
    // Nguyên nhân THẬT khiến dòng thành "terminal" (lỗi edit không phục hồi / click bị chặn) → đính vào
    // LaneAbortedException thay message cũ đổ oan Shopee/captcha (làm user tưởng dính captcha Shopee).
    private string? _lastTerminalReason;

    private readonly ClaimStore? _claim;
    // Điều phối dọn Material Center DÙNG CHUNG mọi lane (đếm bắt-đầu-sửa TOÀN account + cổng pause-all khi kho đầy).
    // Facade truyền ở cả đường 1-lane lẫn đa-lane; null = đường trực tiếp/test → giữ hành vi cũ (cleaner tự đếm per-lane).
    private readonly MediaCleanupCoordinator? _mediaCoord;
    // Cache dữ liệu shop DÙNG CHUNG mọi lane (nạp 1 lần ở tầng điều phối). null = lane tự đọc workbook (đường 1-lane cũ).
    private readonly WorkbookRecordCache? _sharedRecords;
    // RESUME: tiến độ update đã lưu (itemId Shopee → tên đã điền lúc save) nạp 1 lần ở facade, chia CHUNG mọi lane
    // (nhiều lane chung 1 store). SP có key khớp & doneName == tên hiện tại → BỎ QUA (không sửa lại) — bền qua
    // kill/restart. Rỗng = chạy mới / không có tiến độ. MarkDone lúc save gọi thẳng OpProgressStore.Shared (thread-safe).
    private readonly IReadOnlyDictionary<string, string?> _updateDone;
    // Dọn Material Center (thư viện ảnh) — tách sang class riêng; khởi tạo sau khi có browser context.
    private BigSellerMaterialCenterCleaner? _mediaCleaner;

    protected override string StartUrl => ListingUrl;

    public BigSellerProductUpdateRunner(
        BigSellerWorkflowSettings settings, Action<string> log, WorkflowPauseToken? pauseToken = null,
        ClaimStore? claim = null, MediaCleanupCoordinator? mediaCoord = null,
        bool exportCookie = true, WorkbookRecordCache? sharedRecords = null,
        IReadOnlyDictionary<string, string?>? updateDone = null)
        : base(settings, log, pauseToken, exportCookie)
    {
        _claim = claim;
        _mediaCoord = mediaCoord;
        _sharedRecords = sharedRecords;
        _updateDone = updateDone ?? new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!File.Exists(_settings.BravePath))
            throw new FileNotFoundException($"Khong tim thay Brave: {_settings.BravePath}");

        // Cache chung có sẵn → dùng luôn (không đọc lại workbook); không có → tự đọc (đường 1-lane).
        if (_sharedRecords is not null)
        {
            _records = _sharedRecords.Records;
            _log($"📒 Workbook (dùng cache chung): {_records.Count} dòng (khớp theo Shopee item id).");
        }
        else
        {
            await LoadWorkbookRecordsAsync(ct).ConfigureAwait(false);
            _log($"📒 Workbook: {_records.Count} dòng (khớp theo Shopee item id).");
        }

        // MAP RỖNG (đường 1-lane tự nạp; đa-lane đã chặn ở facade) → mọi SP trên Listing đều "not_in_xlsx" → BỎ QUA
        // hết, chạy hàng giờ vô ích rồi vẫn báo "✓ xong". DỪNG NGAY trước StartBrave, đừng phóng trình duyệt.
        if (_records.Count == 0)
        {
            _log($"⚠ KHÔNG có dòng nào đủ điều kiện update trong sheet '{_settings.DataSheet}' (cột 'Tên đã sửa' trống hết " +
                 "hoặc khoảng dòng không có SP) — mọi SP trên Listing sẽ chỉ bị BỎ QUA nên DỪNG NGAY, không mở Brave. " +
                 "→ Chạy 'Update tên SP (AI)' để điền cột G trước, rồi chạy lại Update.");
            return;
        }

        // Đăng ký lane với coordinator dọn-kho (đếm lane sống cho barrier pause-all). Lane chết→restart thì runner mới
        // đăng ký lại. null-safe: đường không có coordinator thì using-null là no-op.
        using var _laneReg = _mediaCoord?.RegisterLane();

        StartBrave();
        _log($"Đã gọi Brave PID={_braveProcess?.Id.ToString() ?? "?"}, chờ CDP port {_settings.DebugPort}...");
        await EnsureCdpReadyAsync(90,
            $"CDP port {_settings.DebugPort} không sẵn sàng. Đóng Brave BigSeller cũ rồi chạy lại.", ct)
            .ConfigureAwait(false);

        await EnsureCookieAsync(ct).ConfigureAwait(false);

        _log($"Kết nối CDP port {_settings.DebugPort}...");
        await ConnectBrowserAsync(ct).ConfigureAwait(false);

        _context = _browser!.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Brave chưa có browser context.");
        // Cài "máy ghi" toast NGAY khi có context: toast media-đầy sống ~3s, mọi điểm check của ta đều có thể trễ hơn
        // (đợi ảnh 5s / MD5 complete 10s) → ngó-đúng-khoảnh-khắc MISS (bug prod v1.0.11: kho đầy, toast hiện, worker
        // vẫn import 3 attempt, không pause-all). AddInitScript áp cho MỌI tab edit mở sau → observer ghi toast vào
        // buffer, các điểm check hiện có đọc buffer thay vì phải bắt đúng lúc toast còn hiện.
        await BigSellerMaterialCenterCleaner.InstallToastRecorderAsync(_context).ConfigureAwait(false);
        _mediaCleaner = new BigSellerMaterialCenterCleaner(_context, _claim, _log, DelayAsync, OverlayAsync);

        var page = PickListingPage(_context)
            ?? throw new InvalidOperationException("Không tìm thấy tab BigSeller.");
        await page.BringToFrontAsync();
        await BigSellerAutoLogin.EnsureFreshSessionAsync(   // Phase 4b: đầu phiên tự mint token tươi (mỗi máy tự login)
            page, _settings.AccountId, _settings.Email, _settings.Password,
            _settings.BigSellerCookieFile, _settings.DebugPort, _exportCookie, _log, ct).ConfigureAwait(false);
        if (!await GoToListingPageAsync(page, false))
            throw new InvalidOperationException("Không mở được trang Listing.");

        _log(new string('=', 50));
        _log("BẮT ĐẦU UPDATE PRODUCT (C#)");
        // Giá trị điền form của lượt này (cấu hình per-shop trên Hub; rỗng đã được thay bằng mặc định) — 1 dòng
        // để chẩn đoán "sao SP ra tồn kho/cân nặng/kênh khác mong đợi" mà không phải đoán.
        _log($"Giá trị điền form: tồn kho={StockValue} · cân nặng={WeightValue}g · vận chuyển='{ShippingChannel}'.");
        _log(new string('=', 50));

        await OuterLoopAsync(page, ct).ConfigureAwait(false);
    }

    // ── workbook (đường 1-lane: tự đọc; đa-lane dùng WorkbookRecordCache chung ở tầng điều phối) ──
    private async Task LoadWorkbookRecordsAsync(CancellationToken ct)
    {
        var (map, emptyRewriteRows) = await WorkbookRecordCache.LoadRecordMapAsync(_settings, ct).ConfigureAwait(false);
        if (emptyRewriteRows.Count > 0)
        {
            var preview = string.Join(", ", emptyRewriteRows.Take(10));
            _log($"⚠ BỎ QUA {emptyRewriteRows.Count} dòng có cột G (Tên đã sửa) TRỐNG (vd dòng {preview}) — " +
                 "chạy \"Update tên SP (AI)\" để điền cột G nếu muốn update các dòng này.");
        }
        _records = map;
    }

    // ── outer loop ──
    // PHÂN TRANG: xử lý hết dòng-cần-update trên trang hiện tại (mỗi vòng 1 dòng, claim chống trùng đa-lane)
    // rồi bấm "Next Page" sang trang kế — GIỮ vị trí trang (KHÔNG reload về trang 1). Vì KHÔNG xóa dòng nào,
    // reload-về-trang-1 mỗi vòng (bản cũ) = mọi dòng trang 1 bị 'skip' → 'exhausted' → reload → KẸT trang 1
    // vĩnh viễn, không lane nào sang được trang 2 (đúng lỗi báo). Tới TRANG CUỐI mà không còn item id cần
    // update ⇒ RETURN (lane kết thúc) → RunOneWorkflowAsync PublishCompletion("completed") ⇒ báo Hub finished.
    private async Task OuterLoopAsync(IPage page, CancellationToken ct)
    {
        var listingErrorStreak = 0;
        var clickBlockedStreak = 0;
        var clickBlockedTotal = 0;
        var emptyStreak = 0;   // số lần liên tiếp trang hiển thị 0 dòng (phân biệt "đang tải" với "rỗng thật")
        var emptyWaitSeconds = Math.Max(3, _settings.ListingReloadSeconds);

        while (!ct.IsCancellationRequested)
        {
            await WaitIfNotPausedAsync(ct).ConfigureAwait(false);
            // Có lane đang dọn Material Center → ĐẬU tại đây tới khi dọn xong (mọi lane quay lại quét Listing cùng lúc,
            // GIỮ vị trí trang hiện tại). Cổng mở thì về ngay (rẻ). Qua cổng rồi đồng bộ Generation đã thấy (kho vừa
            // được dọn sạch — không cần làm gì thêm vì đếm đã ở coordinator).
            if (_mediaCoord != null)
            {
                await _mediaCoord.WaitWhileClosedAsync(ct).ConfigureAwait(false);
                var gen = _mediaCoord.Generation;
                // Generation đổi = kho vừa được dọn sạch → reset streak ảnh-fail (đếm lại từ đầu, kho đã có chỗ).
                if (gen != _lastSeenMediaGen) { _lastSeenMediaGen = gen; _imageUploadFailStreak = 0; }
            }
            // Tab/Brave đóng, listing lỗi liên tục, captcha… = THOÁT BẤT THƯỜNG → ném LaneAbortedException
            // để supervisor RunLanesAsync KHỞI ĐỘNG LẠI lane. KHÔNG dùng break (return bình thường) vì
            // supervisor coi return bình thường là "hết việc" → lane nghỉ hưu vĩnh viễn → 5→1→0.
            if (page.IsClosed) throw new LaneAbortedException("trang/tab BigSeller đã đóng");

            try
            {
                // forceReload:false → GIỮ vị trí trang phân trang hiện tại (chỉ chờ bảng sẵn sàng, KHÔNG về trang 1).
                if (!await GoToListingPageAsync(page, false))
                {
                    listingErrorStreak++;
                    await DelayAsync(Math.Min(5 + listingErrorStreak, 15) * 1000, ct);
                    if (listingErrorStreak >= 5)
                        throw new LaneAbortedException($"mở trang Listing thất bại {listingErrorStreak} lần liên tục");
                    continue;
                }
                listingErrorStreak = 0;

                var (result, terminal) = await RunFirstListingRowAsync(page, ct, () => clickBlockedStreak,
                    s => clickBlockedStreak = s, () => clickBlockedTotal, t => clickBlockedTotal = t).ConfigureAwait(false);

                // Message cũ đổ oan Shopee/captcha → user tưởng dính captcha Shopee trong khi thực tế là lỗi
                // edit/modal. Đính nguyên nhân THẬT (_lastTerminalReason) do RunListingRowAsync ghi.
                if (terminal) throw new LaneAbortedException("lỗi edit không phục hồi: " + (_lastTerminalReason ?? "không rõ nguyên nhân"));

                switch (result)
                {
                    case null:   // 0 dòng trên trang: có thể đang tải, có thể rỗng thật.
                        emptyStreak++;
                        if (emptyStreak < 2)
                        {
                            // Chưa chắc rỗng thật → chờ rồi QUÉT LẠI CHÍNH trang này (KHÔNG sang trang, tránh bỏ sót trang đang tải).
                            await DelayAsync(emptyWaitSeconds * 1000, ct);
                            break;
                        }
                        // Rỗng 2 lần liên tiếp → trang này rỗng thật → sang trang kế nếu còn; hết trang ⇒ kết thúc.
                        emptyStreak = 0;
                        if (await ClickNextListingPageAsync(page, ct).ConfigureAwait(false)) break;
                        _log("✔ Listing rỗng / hết trang cuối — không còn item id cần update. Lane kết thúc.");
                        LogSummary();
                        return;
                    case "media_full":   // Toast/popup báo kho đầy → dừng-toàn-cục + dọn; vòng sau quét lại Listing
                        emptyStreak = 0;   // GIỮ vị trí trang (KHÔNG reload về trang 1 — bug kẹt trang 1 cũ).
                        await HandleMediaEmergencyAsync(page, "toast/popup báo đầy", ct).ConfigureAwait(false);
                        break;
                    case "exhausted":   // Có dòng nhưng trang này hết dòng lane này xử lý được (đã xong/đã skip/lane khác giữ).
                        emptyStreak = 0;
                        if (await ClickNextListingPageAsync(page, ct).ConfigureAwait(false)) break;
                        _log("✔ Hết trang cuối — không còn item id cần update trên mọi trang. Lane kết thúc.");
                        LogSummary();
                        return;
                    case "retry":
                        emptyStreak = 0;
                        // SP có thể ĐÃ bắt đầu sửa rồi mới fail tạm → vẫn check ngưỡng dọn media (đếm đã diễn ra lúc bắt đầu sửa).
                        await MaybeClearMediaAfterEditsAsync(page, ct).ConfigureAwait(false);
                        await DelayAsync(1200, ct);
                        break;
                    default:   // ok / deleted / skipped → đã tiến 1 dòng, quét tiếp trang hiện tại.
                        emptyStreak = 0;
                        // Đếm đã diễn ra lúc BẮT ĐẦU sửa (RecordEditStart trong RunListingRowAsync) → ở đây chỉ
                        // check-ngưỡng-và-dọn; no-op rẻ khi chưa đủ 10 lần bắt đầu sửa.
                        await MaybeClearMediaAfterEditsAsync(page, ct).ConfigureAwait(false);
                        await DelayAsync(800, ct);
                        await MaybeWriteBackBigSellerTokenAsync(ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (LaneAbortedException) { throw; }   // thoát bất thường → để supervisor restart, KHÔNG nuốt vào catch dưới
            catch (Exception ex)
            {
                listingErrorStreak++;
                if ((ex.Message ?? "").Contains("closed", StringComparison.OrdinalIgnoreCase))
                    throw new LaneAbortedException("Brave/tab đã đóng: " + ex.Message);
                await DelayAsync(Math.Min(5 + listingErrorStreak, 15) * 1000, ct);
                try { await GoToListingPageAsync(page, false); } catch { }
                if (listingErrorStreak >= 5)
                    throw new LaneAbortedException($"lỗi listing {listingErrorStreak} lần liên tục: " + ex.Message);
            }
        }
        // Thoát while VÌ CANCEL (Stop) — thoát bình thường → tổng kết. (Đường LaneAbortedException ném ra ngoài,
        // KHÔNG qua đây, cố ý: thoát bất thường supervisor sẽ restart, đừng log tổng kết nửa vời mỗi lần restart.)
        LogSummary();
    }

    // Tổng kết lane khi thoát BÌNH THƯỜNG (hết trang / bị Stop): OK cao mà "đã báo Thống kê"=0 ⇒ ngờ ledger/Hub;
    // "bỏ qua" cao với "không-trong-sheet" cao ⇒ map thiếu dòng (chạy nhầm sheet / chưa điền cột G), KHÔNG phải Hub.
    private void LogSummary() =>
        _log($"Σ lane: update OK {_okCount} · bỏ qua {_skipCount} (không-trong-sheet {_notInSheetCount}) · đã báo Thống kê {_reportedRows} dòng.");

    // RESUME: ghi tiến độ update (itemId → tên vừa điền) NGAY sau save thành công (bền với kill) — nguồn CHÍNH phía
    // client để lượt sau bỏ qua SP đã xong. Hub-mode: báo server (mark-updated) để record-map lượt sau lọc bớt —
    // best-effort, lỗi mạng KHÔNG làm hỏng lượt chạy (store local vẫn đủ). itemId rỗng (không xảy ra ở nhánh
    // needs_update — luôn có shopeeId) → bỏ qua an toàn.
    private void MarkUpdateProgress(string itemId, string productName)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        try
        {
            OpProgressStore.Shared.MarkDone(_settings.AccountId, _settings.DataSheet, AssignmentOps.Update,
                new[] { new KeyValuePair<string, string?>(itemId, productName) });
        }
        catch { }
        if (_settings.UseHubData) _ = MarkUpdatedHubAsync(itemId);
    }

    // Khung chung ở base (BigSellerBraveRunner.MarkStoreProgressHubAsync) — best-effort, nuốt lỗi mạng.
    private Task MarkUpdatedHubAsync(string itemId) => MarkStoreProgressHubAsync(
        (client, items, ct) => client.MarkProductUpdatedAsync(_settings.AccountId, _settings.DataSheet, items, ct),
        new[] { itemId }, "updated", "store local");

    // ── phân trang Listing → uỷ quyền BigSellerCrawlHelper.ClickNextCrawlPageAsync (bản chung Crawl + Listing) ──
    // Nút Next bảng Listing dùng li.next_item (trang cuối → li.next_item.disabled → không khớp :not(.disabled)
    // → trả false = hết trang → kết thúc lane). Truyền PaginationNowPage để helper CHỜ nhãn "X / Y" ĐỔI rồi mới
    // cho quét (tránh quét nhầm DOM trang cũ / nhảy sót trang) + ListingReadySelector để chờ bảng listing sẵn sàng.
    private const string ListingNextPageSelector = ".pagination li.next_item:not(.disabled)";
    private const string PaginationNowPage = ".pagination li.now_page_item";

    private Task<bool> ClickNextListingPageAsync(IPage page, CancellationToken ct) =>
        BigSellerCrawlHelper.ClickNextCrawlPageAsync(
            page, _log, ListingNextPageSelector, PaginationNowPage, ListingReadySelector, DelayAsync, ct);

    // ── dọn media định kỳ ──
    // Đường CŨ (không coordinator): cleaner tự đếm per-lane + wipe khi đủ 10. Đường coordinator: bộ đếm ở
    // coordinator (toàn account), ở đây chỉ kiểm cờ CleanupPending (đủ 10 toàn account HOẶC media-đầy) rồi vào
    // quy trình dọn-toàn-cục (pause-all). Gọi từ OuterLoop sau khi tab edit đã đóng — KHÔNG chạy giữa lúc đang sửa.
    private async Task MaybeClearMediaAfterEditsAsync(IPage listingPage, CancellationToken ct)
    {
        if (_mediaCoord == null) { await _mediaCleaner!.MaybeClearMediaAsync(listingPage, ct).ConfigureAwait(false); return; }
        if (_mediaCoord.CleanupPending) await HandleMediaEmergencyAsync(listingPage, "đủ 10 SP (toàn account)", ct).ConfigureAwait(false);
    }

    // Kho ĐẦY (toast/popup) hoặc đủ ngưỡng 10 SP toàn account → DỪNG TOÀN BỘ lane, dọn 1 lần, xong thì mọi lane quay
    // lại quét Listing. Trước đây mỗi lane tự dọn tại chỗ trong khi lane khác vẫn chạy → save fail hàng loạt → SP bị
    // "fail 2 lần → bỏ oan". Dồn về đây (pause-all) để không lane nào upload/lưu trong lúc kho đang bị dọn.
    private async Task HandleMediaEmergencyAsync(IPage listingPage, string reason, CancellationToken ct)
    {
        _mediaFullDetected = false;   // đã tiếp nhận tín hiệu → xoá cờ kẻo vòng sau tưởng còn đầy
        if (_mediaCoord == null) { await RunMediaCleanupLockedAsync(ct).ConfigureAwait(false); return; }   // fallback đường cũ

        try
        {
            if (_mediaCoord.TryBeginCleanup())   // lane NÀY nhận vai thợ dọn (đóng cổng)
            {
                _log($"⛔ Media Center đầy/{reason} — TẠM DỪNG toàn bộ lane, dọn kho…");
                try
                {
                    // Chờ các lane khác đậu hết (best-effort, cap 180s: lane đang dở AI call/save có thể lâu) rồi mới
                    // dọn — truyền _log để khoảng chờ có nhịp tiến độ, không im lặng như treo (user từng bấm dừng oan).
                    await _mediaCoord.WaitForOthersParkedAsync(180_000, ct, _log).ConfigureAwait(false);
                    _log($"🧹 Bắt đầu dọn Material Center ({_mediaCoord.Parked + 1}/{_mediaCoord.Registered} lane đã dừng)…");
                    await RunMediaCleanupLockedAsync(ct).ConfigureAwait(false);
                }
                finally { _mediaCoord.EndCleanup(); }   // BẮT BUỘC mở cổng lại — không để lane khác kẹt vĩnh viễn
                _log("▶ Dọn kho xong — các lane chạy lại từ Listing.");
            }
            else   // lane khác đang làm thợ dọn → mình chỉ đậu chờ cổng mở
            {
                _log("⏸ Lane tạm dừng — chờ lane khác dọn Media Center…");
                await _mediaCoord.WaitWhileClosedAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try { if (!listingPage.IsClosed) await listingPage.BringToFrontAsync(); } catch { }
        }
    }

    private Task<bool> RunMediaCleanupLockedAsync(CancellationToken ct)
        => _mediaCleaner!.RunMediaCleanupLockedAsync(ct);

    private Task<bool> DismissStorageNagAsync(IPage page)
        => _mediaCleaner!.DismissStorageNagAsync(page);

    // result string ("ok"/"deleted"/"retry"/"skipped"/"exhausted"/null), terminal flag
    private async Task<(string? result, bool terminal)> RunFirstListingRowAsync(
        IPage page, CancellationToken ct,
        Func<int> getStreak, Action<int> setStreak, Func<int> getTotal, Action<int> setTotal)
    {
        var rows = page.Locator(ListingRows);
        var count = await rows.CountAsync();
        if (count == 0)
        {
            await LogEmptyListingDiagnosticsAsync(page).ConfigureAwait(false);
            return (null, false);
        }
        _emptyListingDiagLogged = false;   // có dòng trở lại → cho phép log lại nếu sau này lại trống

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows.Nth(i);
            try { await row.WaitForAsync(new() { Timeout = 15000 }); } catch { continue; }

            var editLink = row.Locator(ListingEditButton).First;
            if (await editLink.CountAsync() == 0) continue;

            var rowKey = await DraftRowKeyAsync(row);
            var editId = await row.GetAttributeAsync(ListingRowKeyAttr) ?? "";

            // Dòng đã xử lý/bỏ qua trước đó → BỎ QUA, KHÔNG xóa (yêu cầu: không xóa dòng nào trên BigSeller).
            if (_skippedRowKeys.Contains(rowKey) || (!string.IsNullOrEmpty(editId) && _skippedEditIds.Contains(editId)))
                continue;

            // SONG SONG: giành quyền xử lý dòng này; lane khác đang giữ → bỏ qua (không mở/không xóa).
            if (_claim is not null && !_claim.TryClaim(rowKey)) continue;

            var res = await RunListingRowAsync(page, row, editLink, rowKey, editId, ct,
                getStreak, setStreak, getTotal, setTotal).ConfigureAwait(false);
            // NHẢ claim rowKey khi dòng CHƯA xong-hẳn: "retry" (lỗi tạm) / terminal (lane sắp chết/restart) /
            // "media_full" (làm lại sau khi dọn kho) / "failed" (lane này bỏ cuộc sau fail 2 lần — để lane khác thử).
            // Nếu KHÔNG nhả → dòng bị "claim mồ côi" (ClaimStore chung không hết-hạn) → sau restart không lane nào
            // claim lại được → bỏ sót SP mà vẫn báo Hub "completed". Giữ claim (rowKey) CHỈ khi ok/deleted/skipped.
            if (_claim is not null && (res.terminal || res.result == "retry" || res.result == "media_full" || res.result == "failed")) _claim.Release(rowKey);
            return res;
        }
        return ("exhausted", false);
    }

    // Khi không thấy dòng SP nào để update: log RÕ vì sao (trang rỗng/sai status, hay BigSeller đã đổi bảng
    // sang vxe-table khiến selector ant-table cũ khớp 0 dòng). Log 1 lần/đợt-trống để khỏi spam mỗi vòng chờ.
    private async Task LogEmptyListingDiagnosticsAsync(IPage page)
    {
        if (_emptyListingDiagLogged) return;
        _emptyListingDiagLogged = true;
        try
        {
            var ant = await page.Locator("tbody.ant-table-tbody tr").CountAsync().ConfigureAwait(false);
            var vxe = await page.Locator("tr.vxe-body--row").CountAsync().ConfigureAwait(false);
            var native = await page.Locator("tr.product_native_row").CountAsync().ConfigureAwait(false);
            var editBtns = await page.Locator(ListingEditButton).CountAsync().ConfigureAwait(false);
            var empty = await page.Locator(".ant-empty, .ant-table-placeholder").CountAsync().ConfigureAwait(false);
            _log($"⚠ Không thấy dòng SP để update. URL={page.Url}");
            _log($"   chẩn đoán: ant-table={ant} · vxe-table={vxe} · product_native={native} · nút Edit={editBtns} · bảng-rỗng={empty}.");
            if (native > 0 && ant == 0 && vxe == 0)
                _log("   → BigSeller đã đổi sang bảng Vue product_native_row nhưng selector dòng không khớp — báo mình để chỉnh.");
            else if (vxe > 0 && ant == 0)
                _log("   → BigSeller đã đổi bảng listing sang vxe-table; cần đổi selector dòng/edit (báo mình để sửa).");
            else if (empty > 0 || (ant == 0 && vxe == 0 && native == 0 && editBtns == 0))
                _log("   → Listing đang TRỐNG thật: kiểm tra đúng tài khoản/shop, SP đã import vào Shopee chưa, và bộ lọc bsStatus.");
            else
                _log("   → Có nút Edit nhưng không khớp selector dòng — báo mình kèm dòng log này để chỉnh selector.");
        }
        catch (Exception ex) { _log($"   (không đọc được chẩn đoán listing: {ex.Message})"); }
    }

    private async Task<(string? result, bool terminal)> RunListingRowAsync(
        IPage page, ILocator row, ILocator editLink, string rowKey, string editId, CancellationToken ct,
        Func<int> getStreak, Action<int> setStreak, Func<int> getTotal, Action<int> setTotal)
    {
        IPage? editPage = null;
        var keepEditOpen = false;
        string? editClaimKey = null;   // "edit:{id}" nếu lane NÀY đã claim tầng-2 (để nhả nếu không giữ)
        var keepClaim = false;         // true = giữ claim (ok/deleted/skipped: đừng cho lane khác mở lại); false = nhả (retry/terminal/lỗi → cho restart/lane khác làm lại)
        try
        {
            var newPage = await _context!.RunAndWaitForPageAsync(async () =>
            {
                try { await editLink.ClickAsync(new() { Timeout = 10000 }); }
                catch (Exception ex) when ((ex.Message ?? "").Contains("intercept", StringComparison.OrdinalIgnoreCase)
                                          || (ex.Message ?? "").Contains("timeout", StringComparison.OrdinalIgnoreCase))
                {
                    await DismissBlockingModalAsync(page);
                    await editLink.ClickAsync(new() { Timeout = 10000 });
                }
            });
            editPage = newPage;
            await editPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 30000 });
            await DelayAsync(2000, ct);

            var actualEditId = ExtractEditId(editPage.Url);
            // "skipped" phải nạp rowKey vào skipped → vòng sau KHÔNG chọn lại dòng này (tránh treo bám row #0).
            if (string.IsNullOrEmpty(actualEditId))
            {
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                _skipCount++; keepClaim = true; return ("skipped", false);
            }
            if (_skippedEditIds.Contains(actualEditId))
            {
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                _skipCount++; keepClaim = true; return ("skipped", false);
            }

            // SONG SONG — claim TẦNG 2 theo edit-id thật: phòng 2 dòng draft khác nhau cùng trỏ 1 SP
            // → lane khác đang sửa đúng SP này thì bỏ qua (đóng tab ở finally, KHÔNG xóa).
            if (_claim is not null && !_claim.TryClaim($"edit:{actualEditId}"))
            {
                // Nạp rowKey vào skipped để vòng sau XÓA dòng draft trùng này — nếu không, dòng vẫn nằm
                // ở listing, lane cứ bám row #0 quét lại mỗi vòng → không tiến/không kết thúc (treo).
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                _skipCount++; keepClaim = true; return ("skipped", false);   // claim tầng-2 do LANE KHÁC giữ → KHÔNG nhả hộ (editClaimKey vẫn null)
            }
            editClaimKey = $"edit:{actualEditId}";   // lane NÀY vừa claim tầng-2 → nhớ để nhả nếu không giữ (retry/terminal/lỗi)

            var (status, record, itemId) = await InspectEditPageAsync(editPage, ct).ConfigureAwait(false);
            if (status != "needs_update")
            {
                // KHÔNG xóa item trên BigSeller cho BẤT KỲ trạng thái nào (not_in_xlsx / blocked / missing…).
                // Chỉ GIỮ NGUYÊN + đánh dấu để vòng quét sau bỏ qua (khỏi mở lại / khỏi treo ở dòng này).
                _log($"  ↳ {status} → giữ nguyên trên BigSeller (KHÔNG xóa), bỏ qua dòng.");
                if (!string.IsNullOrEmpty(actualEditId)) _skippedEditIds.Add(actualEditId);
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                // "not_in_xlsx" = SP trên Listing KHÔNG có trong map → đây là loại "bỏ qua" cần soi riêng: nếu số này
                // ≈ tổng SP mà OK=0 thì đang chạy nhầm sheet / map thiếu, KHÔNG phải Hub nuốt event.
                if (status == "not_in_xlsx") _notInSheetCount++;
                _skipCount++; keepClaim = true; return ("skipped", false);
            }

            // RESUME (tiến độ đã lưu): SP đã update ĐÚNG tên hiện tại ở lượt trước → BỎ QUA, KHÔNG mở/sửa lại (bền
            // qua kill/restart). Excel-mode: _updateDone local là chốt duy nhất. Hub-mode: record-map server đã lọc
            // dòng này (double protection) nhưng vẫn check phòng lỗi mạng lúc mark-updated lượt trước. Xử lý như
            // nhánh 'skipped' (giữ claim + chốt MarkDone để lane restart không mở lại) — không đếm là "bắt đầu sửa".
            if (_updateDone.TryGetValue(itemId, out var doneName) && doneName == record!.ProductName)
            {
                _log("  ⏭ đã update trước đó (tiến độ đã lưu) — bỏ qua, không sửa lại.");
                _skippedEditIds.Add(actualEditId);
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                _claim?.MarkDone(editClaimKey); _claim?.MarkDone(rowKey);
                _skipCount++; keepClaim = true; return ("skipped", false);
            }

            // ĐÂY là điểm "bắt đầu sửa" — SP chỉ bị mở rồi skip ở các nhánh trên (không có edit-id / đã skip /
            // lane khác giữ / not_in_xlsx / thiếu tên / đã-update-trước-đó) KHÔNG đếm; chỉ đếm khi thực sự vào điền/sửa SP.
            // Coordinator đếm TOÀN account (quota Material Center per-account; đếm per-lane ở cleaner là LỆCH —
            // 5 lane phải ~50 lần mới đủ 10, lại reset khi lane restart). null → giữ đường cũ per-lane.
            if (_mediaCoord != null) _mediaCoord.RecordEditStart(_log);
            else _mediaCleaner!.RecordEditStart();
            var reported = false;
            Func<Task> onSaved = () =>
            {
                // Bắn RowsDone ĐÚNG thời điểm đóng tab sau save thành công (yêu cầu thiết kế): helper là nơi duy nhất
                // quyết định "thành công"; tầng này chỉ ánh xạ sang dòng sheet. LineIndex luôn >0 (record từ workbook).
                if (record!.LineIndex > 0)
                {
                    RowsDone?.Invoke(record.LineIndex, record.LineIndex); _reportedRows++; reported = true;
                    MarkUpdateProgress(itemId, record.ProductName);   // RESUME: chốt tiến độ update (bền với kill)
                }
                else _log("  ⚠ LineIndex=0 — update xong nhưng KHÔNG báo được lên Thống kê (dòng này sẽ thiếu trên Hub).");
                return Task.CompletedTask;
            };
            var ok = await ProcessProductAsync(editPage, record!, onSaved, ct).ConfigureAwait(false);
            if (!ok)
            {
                // Kho ĐẦY giữa chừng → KHÔNG đếm fail / KHÔNG add skip-set: SP này sẽ được làm lại SAU khi dọn kho
                // (pause-all). keepClaim vẫn false → finally nhả claim tầng-2 cho lane/vòng sau claim lại.
                if (_mediaFullDetected) return ("media_full", false);
                // Lỗi TẠM (AI rỗng/mạng) → để dòng lại, thử vòng sau, TUYỆT ĐỐI KHÔNG xóa (tránh mất SP). NHẢ claim (finally).
                if (_lastProcessTransient) { _log("  ↳ lỗi tạm (AI) → để lại dòng, thử lại sau."); return ("retry", false); }
                var failKey = $"shopee:{record!.LineIndex}/edit:{actualEditId}/row:{rowKey}";
                var fails = _failCounts.TryGetValue(failKey, out var c) ? c + 1 : 1;
                _failCounts[failKey] = fails;
                if (fails < 2) return ("retry", false);   // NHẢ claim (finally) → thử lại vòng sau
                // 2 lần fail (không phải lỗi tạm) → lane NÀY bỏ cuộc. skip-set PER-LANE chặn lane này chọn lại dòng
                // (khỏi lặp vô hạn), nhưng NHẢ khóa (keepClaim=false → finally nhả edit-id; "failed" → nhả rowKey ở
                // RunFirstListingRow) để LANE KHÁC có 2 lượt thử riêng → tổng tự chặn ở 2×N lane, KHÔNG khóa oan SP
                // (bug prod: kho media đầy → "fail 2 lần" khóa vĩnh viễn dù dọn xong lane khác sửa được).
                _log("  ↳ fail 2 lần → lane này bỏ qua, NHẢ khóa cho lane khác thử (không xóa trên BigSeller).");
                _skippedEditIds.Add(actualEditId);
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                _skipCount++; keepClaim = false; return ("failed", false);
            }

            _skippedEditIds.Add(actualEditId);
            if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
            _log($"✅ HOÀN TẤT XỬ LÝ SKU: {record!.Sku}");
            await OverlayAsync($"✅ Hoàn tất SKU {record!.Sku}");
            _okCount++;
            // Helper PHẢI đã gọi onSaved lúc đóng tab; nếu CHƯA (đường success nào đó không qua helper) vẫn báo để Hub
            // không thiếu dòng — cờ reported chống bắn đúp.
            if (!reported && record!.LineIndex > 0) { RowsDone?.Invoke(record.LineIndex, record.LineIndex); _reportedRows++; MarkUpdateProgress(itemId, record.ProductName); _log("  ⚠ báo Thống kê qua fallback (onSaved không được gọi trong helper — soi lại luồng save)."); }
            // THÀNH CÔNG → chốt cả khóa edit-id lẫn khóa dòng-draft vào mảng-2 (_done): khóa VĨNH VIỄN trong lượt
            // chạy, không lane nào (kể cả lane này sau restart) mở lại. keepClaim=true nên finally không nhả.
            _claim?.MarkDone(editClaimKey); _claim?.MarkDone(rowKey);
            keepClaim = true; return ("ok", false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("intercepts pointer events", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ant-modal", StringComparison.OrdinalIgnoreCase))
            {
                setStreak(getStreak() + 1);
                setTotal(getTotal() + 1);
                if (getTotal() >= 9) { _lastTerminalReason = "click bị modal chặn 9 lần liên tục (popup/modal lạ trên trang?)"; keepEditOpen = true; return (null, true); }
                await DismissBlockingModalAsync(page);
                if (getStreak() >= 3) { await GoToListingPageAsync(page, true); setStreak(0); }
                return ("retry", false);
            }
            _log($"  ↳ Lỗi không phục hồi: {msg}");
            _lastTerminalReason = msg;
            keepEditOpen = true;
            return (null, true);
        }
        finally
        {
            // NHẢ claim tầng-2 khi dòng CHƯA xong-hẳn (retry / terminal / exception ném ra) → để restart hoặc
            // lane khác claim lại & làm nốt. Không nhả = "claim mồ côi" khóa vĩnh viễn (ClaimStore chung không
            // hết-hạn) → bỏ sót SP mà vẫn báo Hub "completed". Giữ claim CHỈ khi ok/deleted/skipped (keepClaim).
            if (!keepClaim && editClaimKey is not null) _claim?.Release(editClaimKey);
            if (!keepEditOpen && editPage is not null)
            {
                await ClosePageAcceptingDialogAsync(editPage);
                try { await page.BringToFrontAsync(); } catch { }
            }
        }
    }

    // ── inspect ──
    // Trả THÊM itemId (Shopee id trích từ link nguồn) để caller đối chiếu tiến độ update đã lưu (_updateDone) —
    // "" ở các nhánh chưa/không trích được id.
    private async Task<(string status, WorkbookRecord? record, string itemId)> InspectEditPageAsync(IPage editPage, CancellationToken ct)
    {
        // wait_for_edit_page_ready
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await editPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 12000 });
                await editPage.WaitForSelectorAsync(SourceLinkInput, new() { State = WaitForSelectorState.Visible, Timeout = 12000 });
                break;
            }
            catch
            {
                if (attempt == 1) break;
                try { await editPage.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }); } catch { }
                await DelayAsync(3000, ct);
            }
        }

        string inputVal = "";
        try { inputVal = await editPage.Locator(SourceLinkInput).InputValueAsync(new() { Timeout = 3000 }); } catch { }

        string clip = "";
        try
        {
            var copyBtn = editPage.Locator(SourceLinkCopyButton).First;
            await _context!.GrantPermissionsAsync(new[] { "clipboard-read", "clipboard-write" },
                new() { Origin = "https://www.bigseller.com" });
            await copyBtn.ClickAsync(new() { Timeout = 5000 });
            await DelayAsync(1000, ct);
            clip = await editPage.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        }
        catch { }

        string? shopeeId = null;
        var sourceUrl = "";
        foreach (var url in new[] { inputVal, clip })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (url.Contains("/verify/captcha") || url.Contains("/verify/traffic"))
                return ("shopee_blocked", null, "");
            if (shopeeId is null) { shopeeId = BigSellerCrawlHelper.ExtractShopeeId(url); if (shopeeId is not null) sourceUrl = url; }
        }

        if (string.IsNullOrEmpty(shopeeId))
        {
            _log($"   ⚠ KHÔNG trích được item id từ link nguồn: '{(string.IsNullOrWhiteSpace(inputVal) ? clip : inputVal)}'");
            return ("missing_shopee_id", null, "");
        }
        // LOG để soi: item id của SP đang edit + có trong sheet (dùng để scrape) không + tên sheet/số dòng + link.
        // Item id "KHÔNG" trong sheet = SP này không đến từ sheet đang chạy (sheet khác / lần scrape trước / thêm tay).
        var inSheet = _records.ContainsKey(shopeeId);
        _log($"   item id = {shopeeId} · trong sheet '{_settings.DataSheet}' ({_records.Count} dòng): {(inSheet ? "CÓ" : "KHÔNG")} · link: {sourceUrl}");
        if (!_records.TryGetValue(shopeeId, out var rec)) return ("not_in_xlsx", null, shopeeId);
        if (string.IsNullOrWhiteSpace(rec.ProductName)) return ("missing_product_name", null, shopeeId);
        return ("needs_update", rec, shopeeId);
    }

}
