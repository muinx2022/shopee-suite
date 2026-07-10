using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Playwright;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;

namespace UpdateProduct;

/// <summary>Lỗi khiến một lane Update phải DỪNG bất thường (tab đóng, listing lỗi liên tục, captcha…)
/// — ném ra để supervisor <c>RunLanesAsync</c> KHỞI ĐỘNG LẠI lane thay vì để nó "nghỉ hưu" âm thầm.
/// Trước đây các nhánh lỗi dùng <c>break</c> (return bình thường) nên supervisor tưởng "hết việc" →
/// lane mất hẳn → worker rụng dần 5→1→0.</summary>
internal sealed class LaneAbortedException(string reason) : Exception(reason);

/// <summary>Một dòng workbook đã map sang dữ liệu update (khớp theo Shopee item id).</summary>
internal sealed record WorkbookRecord(string Link, string Sku, string ProductName, string Price, int LineIndex);

/// <summary>
/// Cache DÙNG CHUNG dữ liệu shop (dòng workbook → <see cref="WorkbookRecord"/>, khóa = Shopee item id) cho
/// TẤT CẢ lane update. Nạp MỘT LẦN trước khi mở Brave rồi chia sẻ (immutable) → thay vì mỗi lane tự đọc
/// workbook (N lần đọc + N lần khóa file + N lần parse). Đây là "cache chung từ dòng đến dòng" trước khi
/// từng lane tìm item id trên Listing và sửa.
/// </summary>
internal sealed class WorkbookRecordCache
{
    public IReadOnlyDictionary<string, WorkbookRecord> Records { get; }

    private WorkbookRecordCache(IReadOnlyDictionary<string, WorkbookRecord> records) => Records = records;

    /// <summary>Nạp + log (dùng ở tầng điều phối, nạp 1 lần cho mọi lane).</summary>
    public static async Task<WorkbookRecordCache> LoadAsync(BigSellerWorkflowSettings settings, Action<string> log, CancellationToken ct)
    {
        var (map, emptyRewriteRows) = await LoadRecordMapAsync(settings, ct).ConfigureAwait(false);
        if (emptyRewriteRows.Count > 0)
        {
            var preview = string.Join(", ", emptyRewriteRows.Take(10));
            log($"⚠ BỎ QUA {emptyRewriteRows.Count} dòng có cột G (Tên đã sửa) TRỐNG (vd dòng {preview}) — " +
                "chạy \"Update tên SP (AI)\" để điền cột G nếu muốn update các dòng này.");
        }
        log($"📒 Workbook (cache chung mọi lane): {map.Count} dòng (khớp theo Shopee item id).");
        return new WorkbookRecordCache(map);
    }

    // Đọc workbook → map item id → record. Khóa file khi đọc (chung file giữa nhiều account): serialize với
    // lúc "Update tên SP" đang GHI cột G → tránh đọc-khi-đang-ghi (IOException/đọc lệch). Thuần, không log.
    internal static async Task<(Dictionary<string, WorkbookRecord> map, List<int> emptyRewriteRows)> LoadRecordMapAsync(
        BigSellerWorkflowSettings settings, CancellationToken ct)
    {
        // Cột bắt buộc: "Tên đã sửa" (tên để update) + ít nhất 1 trong (Item ID / Link) để khớp dòng.
        // 0 = "không dùng" → fail rõ ràng thay vì đẩy 0 vào ClosedXML (Cell(0) ném lỗi).
        if (settings.RewrittenNameColumn <= 0)
            throw new InvalidOperationException("Chưa map cột 'Tên đã sửa' cho shop (mục BigSeller → Ánh xạ cột).");
        if (settings.ItemIdColumn <= 0 && settings.LinkColumn <= 0)
            throw new InvalidOperationException("Cần map ít nhất 'Item ID' hoặc 'Link' để khớp dòng (mục BigSeller → Ánh xạ cột).");

        using var _ = await WorkbookFileLockHandle.AcquireAsync(settings.WorkbookPath, ct).ConfigureAwait(false);
        var map = new Dictionary<string, WorkbookRecord>();
        using var wb = new XLWorkbook(settings.WorkbookPath);
        var ws = string.IsNullOrWhiteSpace(settings.DataSheet)
            ? wb.Worksheets.First()
            : wb.Worksheet(settings.DataSheet);
        var start = Math.Max(2, settings.StartRow);
        var last = ws.LastRowUsed()?.RowNumber() ?? 0;
        var end = settings.EndRow > 0 ? Math.Min(settings.EndRow, last) : last;

        var emptyRewriteRows = new List<int>();   // dòng có SP để update nhưng cột G (Tên đã sửa) còn trống
        for (var r = start; r <= end; r++)
        {
            var row = ws.Row(r);
            // Cột = 0 ("không dùng") → đọc rỗng, KHÔNG gọi Cell(0) (ClosedXML 1-based, Cell(0) ném lỗi).
            var link = settings.LinkColumn > 0 ? row.Cell(settings.LinkColumn).GetString().Trim() : "";
            var price = settings.PriceColumn > 0 ? row.Cell(settings.PriceColumn).GetString().Trim() : "";
            var sku = settings.SkuColumn > 0 ? row.Cell(settings.SkuColumn).GetString().Trim() : "";
            var colE = settings.ItemIdColumn > 0 ? row.Cell(settings.ItemIdColumn).GetString().Trim() : "";
            var rewritten = row.Cell(settings.RewrittenNameColumn).GetString().Trim();   // Tên đã sửa (đã validate > 0)

            var rowId = !string.IsNullOrWhiteSpace(colE) ? colE : (BigSellerCrawlHelper.ExtractShopeeId(link) ?? "");
            if (string.IsNullOrWhiteSpace(rowId)) continue;

            // Cột G trống → BỎ QUA riêng dòng đó (không update tên gốc cột F), vẫn chạy tiếp các dòng khác.
            if (string.IsNullOrWhiteSpace(rewritten)) { emptyRewriteRows.Add(r); continue; }
            map[rowId] = new WorkbookRecord(link, sku, rewritten, price, r);
        }

        return (map, emptyRewriteRows);
    }
}

/// <summary>
/// Cập nhật sản phẩm trên BigSeller bằng C# + Playwright (thay cho main.py Python).
/// Quét trang Listing (bsStatus=1), mở từng sản phẩm vào tab edit, đối chiếu workbook theo
/// Shopee item id, rồi điền tên/SKU/giá/tồn/brand/cân nặng/ảnh/video + mô tả AI và lưu.
/// Selector giữ nguyên verbatim từ bản Python.
/// </summary>
internal sealed partial class BigSellerProductUpdateRunner : BigSellerBraveRunner
{
    // ── CONFIG (từ main.py CONFIG) ──
    private const string StockValue = "30069";
    private const string WeightValue = "500";
    private const int MaxProductNameChars = 120;   // Shopee giới hạn tên SP 120 ký tự (BigSeller báo lỗi nếu vượt)
    private const int MaxDescriptionChars = 3000;
    private const int TrimmedDescriptionMaxChars = 2900;
    private const int TargetDescriptionMinChars = 2700;
    private const string ListingUrl = "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";

    private const string DescriptionSystemPrompt =
        "Bạn là chuyên gia SEO TMĐT chuyên viết mô tả sản phẩm GIÀY – DÉP NỮ để đăng Shopee, Lazada, Tiki, Ozon.\n\n" +
        "NHIỆM VỤ:\nViết MỘT bài mô tả sản phẩm duy nhất, sẵn sàng đăng bán.\n\n" +
        "YÊU CẦU BẮT BUỘC:\n" +
        "- ĐỘ DÀI: cố gắng trong khoảng 2800–2900 ký tự.\n" +
        "- TUYỆT ĐỐI KHÔNG VƯỢT 3000 ký tự vì Shopee sẽ báo lỗi.\n" +
        "- Chuẩn SEO theo hành vi tìm kiếm người mua giày nữ online.\n" +
        "- Lặp tự nhiên từ khóa chính và biến thể liên quan đến giày nữ, không spam.\n" +
        "- Văn phong chuyên nghiệp, dễ đọc, tập trung lợi ích người dùng nữ.\n\n" +
        "CẤU TRÚC:\n" +
        "- Mở bài: giới thiệu sản phẩm, nêu từ khóa chính.\n" +
        "- Thân bài: thiết kế, chất liệu, đế, form, cảm giác mang, tính ứng dụng.\n" +
        "- Kết bài: gợi ý phối đồ, đối tượng phù hợp, kêu gọi mua.\n\n" +
        "QUY ĐỊNH:\n- Không chèn tiêu đề thừa.\n- Không ghi \"Thông số\", \"Cam kết\", \"Chính sách\".\n- Không giải thích SEO.\n\n" +
        "HASHTAG:\n- Đặt NGAY SAU đoạn mô tả cuối cùng.\n- Viết liền, không tiêu đề.\n- Đúng ngành giày nữ, có mã sản phẩm.\n- CHÍNH XÁC 18 hashtag.\n\n" +
        "NGUYÊN TẮC CUỐI:\n- Nếu cần điều chỉnh, chỉ thay đổi độ dài câu để nằm trong khoảng 2800–2900 ký tự.\n- Tuyệt đối không thêm hoặc bớt hashtag.";

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
    // Dọn Material Center (thư viện ảnh) — tách sang class riêng; khởi tạo sau khi có browser context.
    private BigSellerMaterialCenterCleaner? _mediaCleaner;

    protected override string StartUrl => ListingUrl;

    public BigSellerProductUpdateRunner(
        BigSellerWorkflowSettings settings, Action<string> log, WorkflowPauseToken? pauseToken = null,
        ClaimStore? claim = null, MediaCleanupCoordinator? mediaCoord = null,
        bool exportCookie = true, WorkbookRecordCache? sharedRecords = null)
        : base(settings, log, pauseToken, exportCookie)
    {
        _claim = claim;
        _mediaCoord = mediaCoord;
        _sharedRecords = sharedRecords;
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
                if (gen != _lastSeenMediaGen) _lastSeenMediaGen = gen;
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
                    // Chờ các lane khác đậu hết (best-effort, cap 180s: lane đang dở AI call có thể lâu) rồi mới dọn.
                    await _mediaCoord.WaitForOthersParkedAsync(180_000, ct).ConfigureAwait(false);
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
            // "media_full" (làm lại sau khi dọn kho). Nếu KHÔNG nhả → dòng bị "claim mồ côi" (ClaimStore chung không
            // hết-hạn) → sau restart không lane nào claim lại được → bỏ sót SP mà vẫn báo Hub "completed". Giữ claim
            // CHỈ khi ok/deleted/skipped.
            if (_claim is not null && (res.terminal || res.result == "retry" || res.result == "media_full")) _claim.Release(rowKey);
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

            var (status, record) = await InspectEditPageAsync(editPage, ct).ConfigureAwait(false);
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

            // ĐÂY là điểm "bắt đầu sửa" — SP chỉ bị mở rồi skip ở các nhánh trên (không có edit-id / đã skip /
            // lane khác giữ / not_in_xlsx / thiếu tên) KHÔNG đếm; chỉ đếm khi thực sự vào điền/sửa SP.
            // Coordinator đếm TOÀN account (quota Material Center per-account; đếm per-lane ở cleaner là LỆCH —
            // 5 lane phải ~50 lần mới đủ 10, lại reset khi lane restart). null → giữ đường cũ per-lane.
            if (_mediaCoord != null) _mediaCoord.RecordEditStart(_log);
            else _mediaCleaner!.RecordEditStart();
            var reported = false;
            Func<Task> onSaved = () =>
            {
                // Bắn RowsDone ĐÚNG thời điểm đóng tab sau save thành công (yêu cầu thiết kế): helper là nơi duy nhất
                // quyết định "thành công"; tầng này chỉ ánh xạ sang dòng sheet. LineIndex luôn >0 (record từ workbook).
                if (record!.LineIndex > 0) { RowsDone?.Invoke(record.LineIndex, record.LineIndex); _reportedRows++; reported = true; }
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
                // 2 lần fail (không phải lỗi tạm) → GIỮ NGUYÊN, KHÔNG xóa; chỉ bỏ qua dòng (giữ claim để khỏi mở lại).
                _log("  ↳ fail 2 lần → giữ nguyên trên BigSeller (KHÔNG xóa), bỏ qua dòng.");
                _skippedEditIds.Add(actualEditId);
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                _skipCount++; keepClaim = true; return ("skipped", false);
            }

            _skippedEditIds.Add(actualEditId);
            if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
            _log($"✅ HOÀN TẤT XỬ LÝ SKU: {record!.Sku}");
            await OverlayAsync($"✅ Hoàn tất SKU {record!.Sku}");
            _okCount++;
            // Helper PHẢI đã gọi onSaved lúc đóng tab; nếu CHƯA (đường success nào đó không qua helper) vẫn báo để Hub
            // không thiếu dòng — cờ reported chống bắn đúp.
            if (!reported && record!.LineIndex > 0) { RowsDone?.Invoke(record.LineIndex, record.LineIndex); _reportedRows++; _log("  ⚠ báo Thống kê qua fallback (onSaved không được gọi trong helper — soi lại luồng save)."); }
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
    private async Task<(string status, WorkbookRecord? record)> InspectEditPageAsync(IPage editPage, CancellationToken ct)
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
                return ("shopee_blocked", null);
            if (shopeeId is null) { shopeeId = BigSellerCrawlHelper.ExtractShopeeId(url); if (shopeeId is not null) sourceUrl = url; }
        }

        if (string.IsNullOrEmpty(shopeeId))
        {
            _log($"   ⚠ KHÔNG trích được item id từ link nguồn: '{(string.IsNullOrWhiteSpace(inputVal) ? clip : inputVal)}'");
            return ("missing_shopee_id", null);
        }
        // LOG để soi: item id của SP đang edit + có trong sheet (dùng để scrape) không + tên sheet/số dòng + link.
        // Item id "KHÔNG" trong sheet = SP này không đến từ sheet đang chạy (sheet khác / lần scrape trước / thêm tay).
        var inSheet = _records.ContainsKey(shopeeId);
        _log($"   item id = {shopeeId} · trong sheet '{_settings.DataSheet}' ({_records.Count} dòng): {(inSheet ? "CÓ" : "KHÔNG")} · link: {sourceUrl}");
        if (!_records.TryGetValue(shopeeId, out var rec)) return ("not_in_xlsx", null);
        if (string.IsNullOrWhiteSpace(rec.ProductName)) return ("missing_product_name", null);
        return ("needs_update", rec);
    }

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

        await StepAsync("Đồng bộ ảnh (MD5)");
        // [2] md5 sync — MD5 + import ảnh là 2 hành động ĐẨY ảnh vào Material Center; toast "kho đầy" bật NGAY tại đây
        // và tự ẩn sau ~3s → phải bắt tại NGUỒN (tới lúc save là mất dấu). Dính tín hiệu → thoát SP sớm (khỏi tốn AI/save).
        try
        {
            var md5 = page.Locator(Md5Button).First;
            if (await md5.IsVisibleAsync())
            {
                await md5.ScrollIntoViewIfNeededAsync();
                await md5.ClickAsync();
                if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page))
                { _mediaFullDetected = true; await DismissStorageNagAsync(page); return false; }   // return KHÔNG bị catch nuốt
                try { await page.Locator(Md5CompleteStatus).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 }); } catch { }
                await CloseVisibleAntModalAsync(page, 5000);
            }
        }
        catch { }
        // Check LẦN NỮA sau block md5, NGOÀI try để catch { } không nuốt mất đường return (1 query DOM, rẻ).
        if (await BigSellerMaterialCenterCleaner.IsMediaInsufficientSignalAsync(page))
        { _mediaFullDetected = true; await DismissStorageNagAsync(page); return false; }

        // [3] radio "Tải lên hình ảnh" / "Upload Image" — tick để hiện khối upload ảnh (div.spc_box).
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
                try { await page.Locator(ImageGalleryBox).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 4000 }); } catch { }
            }
        }
        catch { }

        await StepAsync("Điền SKU + thương hiệu");
        // [4] parent SKU
        try
        {
            var s = page.Locator(ParentSkuInput);
            if (await s.IsVisibleAsync())
            {
                await s.FillAsync(rec.Sku);
                await s.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            }
        }
        catch { }

        // [5] brand
        try { await SelectNoBrandAsync(page, ct); } catch { }

        // [6] variation SKUs
        await ForEachVisibleAsync(page.Locator(VariationSkuInputs), async el =>
        {
            await el.ScrollIntoViewIfNeededAsync();
            await el.FillAsync(rec.Sku);
            await el.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            await el.EvaluateAsync("el => el.blur()");
        });

        await StepAsync("Cập nhật tồn kho + giá");
        // [7] stock — đặt TẤT CẢ biến thể về StockValue (kể cả ô đang = 0, không còn giữ nguyên ô hết hàng).
        await ForEachVisibleAsync(page.Locator(VariationStockInputs), async el =>
        {
            await el.ScrollIntoViewIfNeededAsync();
            await el.FillAsync(StockValue);
            await el.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            await el.EvaluateAsync("el => el.blur()");
        });

        // [8] price
        var newPrice = ParsePrice(rec.Price);
        await ForEachVisibleAsync(page.Locator(VariationPriceInputs), async el =>
        {
            await el.ScrollIntoViewIfNeededAsync();
            await el.FillAsync(newPrice);
            await el.EvaluateAsync("el => el.dispatchEvent(new Event('input', {bubbles:true}))");
            await el.EvaluateAsync("el => el.blur()");
        });

        await StepAsync("Vận chuyển + cân nặng");
        // [9] shipping "Nhanh"
        try
        {
            var ship = page.Locator(ShippingFastWrapper).Filter(new() { HasTextString = "Nhanh" }).First;
            if (await ship.IsVisibleAsync())
            {
                await ship.ScrollIntoViewIfNeededAsync();
                if (await ship.Locator(ShippingCheckedMark).CountAsync() == 0) await ship.ClickAsync();
            }
        }
        catch { }

        // [10] weight
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
        catch { }

        // video discovery
        var videoPath = ResolveVideoPath(rec.Sku);

        // [10.5] upload video (non-fatal, 3 attempts)
        if (videoPath != null)
        {
            await StepAsync("Upload video");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { if (await UploadVideoAsync(page, videoPath, ct)) break; } catch { }
                await DelayAsync(3000, ct);
            }
        }
        if (_mediaFullDetected) return false;   // kho đầy phát hiện lúc upload video → AI/ảnh/lưu vô ích, thoát sớm cho rẻ

        // [11] image (non-fatal)
        var imagePath = _settings.ImagePath;
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            await StepAsync("Import ảnh");
            try { await UploadImageWithRetryAsync(page, imagePath, 3, ct); } catch { }
        }
        if (_mediaFullDetected) return false;   // kho đầy lúc upload ảnh → thoát sớm; HandleMediaEmergencyAsync sẽ dọn

        await StepAsync("Tạo mô tả AI");
        // [12.1] AI description — rỗng sau retry = LỖI TẠM → retry vòng sau, KHÔNG xóa dòng (tránh mất SP).
        var aiContent = await GenerateDescriptionAsync(rec.ProductName ?? "", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(aiContent)) { _lastProcessTransient = true; return false; }
        if (!await UpdateDescriptionAsync(page, aiContent, ct)) return false;

        await StepAsync("Lưu sản phẩm");
        // [12.2] save
        return await SaveWithImageRetryAsync(page, imagePath, 3, onSaved, ct).ConfigureAwait(false);
    }

    // ── overlay tiến độ ngay trên trang Brave để THEO DÕI log (best-effort: lỗi overlay KHÔNG bao giờ
    // chặn luồng cập nhật). Phát lên MỌI tab đang mở trong context → nhìn tab nào cũng thấy. ──
    private const string OverlayJs = @"(line) => {
  let box = document.getElementById('__ssyncOverlay');
  if (!box) {
    box = document.createElement('div');
    box.id = '__ssyncOverlay';
    box.style.cssText = 'position:fixed;z-index:2147483647;right:10px;bottom:10px;width:430px;max-height:260px;overflow:hidden;background:rgba(17,17,17,.86);color:#7CFC7C;font:12px/1.5 Consolas,Menlo,monospace;padding:8px 10px;border-radius:10px;box-shadow:0 4px 16px rgba(0,0,0,.55);pointer-events:none;white-space:pre-wrap;word-break:break-word';
    const t = document.createElement('div');
    t.textContent = '● ShopeeSuite — tiến độ Update';
    t.style.cssText = 'color:#67d3ff;font-weight:700;margin-bottom:5px';
    box.appendChild(t);
    const b = document.createElement('div'); b.id = '__ssyncOverlayBody'; box.appendChild(b);
    (document.body || document.documentElement).appendChild(box);
  }
  const body = document.getElementById('__ssyncOverlayBody');
  const r = document.createElement('div');
  const n = new Date();
  const p = x => String(x).padStart(2, '0');
  r.textContent = '[' + p(n.getHours()) + ':' + p(n.getMinutes()) + ':' + p(n.getSeconds()) + '] ' + line;
  body.appendChild(r);
  while (body.childNodes.length > 12) body.removeChild(body.firstChild);
}";

    /// <summary>Đẩy 1 dòng lên overlay của MỌI trang đang mở (listing + edit). Lỗi → bỏ qua.</summary>
    private async Task OverlayAsync(string line)
    {
        var ctx = _context;
        if (ctx is null) return;
        foreach (var pg in ctx.Pages)
        {
            if (pg.IsClosed) continue;
            try { await pg.EvaluateAsync(OverlayJs, line); } catch { /* overlay best-effort */ }
        }
    }

    /// <summary>Báo bắt đầu một bước: ghi log app ("▶ …") + hiện trên overlay Brave.</summary>
    private async Task StepAsync(string text)
    {
        _log("  ▶ " + text);
        await OverlayAsync("▶ " + text);
    }

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

    private async Task DismissBlockingModalAsync(IPage page)
    {
        try
        {
            // Popup hướng dẫn đổi ngôn ngữ (KHÔNG phải ant-modal) cũng chặn click Edit → thử chọn Tiếng Việt/đóng trước.
            if (await BigSellerCrawlHelper.DismissLanguageGuideAsync(page, _log, CancellationToken.None)) return;
            if (await page.Locator(BlockingModalVisible).CountAsync() > 0)
            {
                foreach (var sel in DismissBtns)
                {
                    var loc = page.Locator(sel).First;
                    if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync()) { await loc.ClickAsync(); return; }
                }
            }
            await page.Keyboard.PressAsync("Escape");
        }
        catch { }
    }

    private async Task CloseVisibleAntModalAsync(IPage page, int timeoutMs)
    {
        var deadline = timeoutMs;
        while (deadline > 0)
        {
            if (await page.Locator(CloseModalAny).CountAsync() == 0) return;
            var clicked = false;
            foreach (var sel in CloseModalSels)
            {
                var loc = page.Locator(sel);
                var n = await loc.CountAsync();
                for (var i = n - 1; i >= 0; i--)
                {
                    var el = loc.Nth(i);
                    if (!await el.IsVisibleAsync()) continue;
                    try { await el.ClickAsync(); clicked = true; } catch { }
                    break;
                }
                if (clicked) break;
            }
            if (!clicked) { try { await page.Keyboard.PressAsync("Escape"); } catch { } }
            await page.WaitForTimeoutAsync(300);
            deadline -= 300;
        }
    }

    private static async Task ClosePageAcceptingDialogAsync(IPage page)
    {
        try
        {
            page.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch { } };
            await page.CloseAsync(new() { RunBeforeUnload = true });
        }
        catch { try { await page.CloseAsync(); } catch { } }
    }

    private async Task ForEachVisibleAsync(ILocator locator, Func<ILocator, Task> action)
    {
        var n = await locator.CountAsync();
        for (var i = 0; i < n; i++)
        {
            var el = locator.Nth(i);
            try { if (await el.IsVisibleAsync()) await action(el); } catch { }
        }
    }

    private static async Task<ILocator?> FirstVisibleAsync(params ILocator[] locators)
    {
        foreach (var loc in locators)
        {
            try
            {
                var n = await loc.CountAsync();
                for (var i = 0; i < n; i++)
                {
                    var el = loc.Nth(i);
                    if (await el.IsVisibleAsync()) return el;
                }
            }
            catch { }
        }
        return null;
    }

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
