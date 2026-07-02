using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Playwright;
using Shopee.Core.Ai;
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
internal sealed class BigSellerProductUpdateRunner : IAsyncDisposable
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

    // ── SELECTORS ──
    // BigSeller đã đổi bảng listing sang vxe-table (dòng = tr.vxe-body--row, khóa dòng = thuộc tính rowid,
    // nút Delete title="Delete"). Giữ kèm selector ant-table cũ để vẫn chạy nếu trang nào còn dùng bảng cũ.
    private const string ListingRows = "tr.vxe-body--row, tbody.ant-table-tbody tr";
    private const string ListingEditButton = "a.action_btn.addEditProduct";   // (vxe vẫn giữ class này)
    private const string ListingReadySelector = "tr.vxe-body--row, tbody.ant-table-tbody tr, a.action_btn.addEditProduct, .ant-empty, .ant-table-placeholder, .vxe-table--empty-block, .vxe-table--empty-placeholder";
    private const string ListingRowKeyAttr = "rowid";   // vxe: <tr rowid="..."> (cũ: data-row-key)
    private const string DeleteBtn1 = "a.action_btn[title='Xóa'], a.action_btn[title='Delete']";
    private const string DeleteBtn2 = "a[title='Xóa'], a[title='Delete']";
    private const string DeleteBtn3 = "a.action_btn:has(span.bsicon_trash_2)";
    private const string DeleteConfirmPrimary = ".ant-modal-confirm-btns button.ant-btn-primary";
    private const string BlockingModalVisible = "div.ant-modal-wrap:visible";
    private static readonly string[] DismissBtns =
    {
        ".ant-modal-confirm-btns button:not(.ant-btn-primary)", ".ant-modal-close",
        ".ant-modal-footer button:not(.ant-btn-primary)", ".ant-modal-confirm-btns button", ".ant-modal-footer button",
    };

    private const string SourceLinkInput = "input[autoid='product_source_link_text']";
    private const string SourceLinkCopyButton = "div.com_input_box:has(input[autoid='product_source_link_text']) button";
    private const string ProductNameInput = "input[autoid='product_name_text']";

    private const string Md5Button = "span.sell_md5";
    private const string Md5CompleteStatus = "div.ant-modal:visible div.complete_Status";
    private const string CloseModalAny = "div.ant-modal:visible";
    private static readonly string[] CloseModalSels =
    {
        "div.ant-modal:visible:has(div.complete_Status) .ant-modal-footer button.ant-btn",
        "div.ant-modal:visible:has(div.complete_Status) button.ant-modal-close",
        "div.ant-modal:visible .ant-modal-footer button", "div.ant-modal:visible button.ant-btn",
        "div.ant-modal:visible button.ant-modal-close", "div.ant-modal:visible .ant-modal-close",
        "div.ant-modal:visible .ant-modal-close-x",
    };

    private const string UploadImageRadioWrapper = "label.ant-radio-wrapper";
    private const string ParentSkuInput = "input[autoid='parent_sku_text']";
    private const string VariationSkuInputs = "input[autoid^='variation_sku_text_']";
    private const string VariationStockInputs = "input[autoid^='variation_stock_text_']";
    private const string VariationPriceInputs = "input[autoid^='variation_price_text_']";
    private const string ShippingFastWrapper = "label.ant-checkbox-wrapper";
    private const string ShippingCheckedMark = ".ant-checkbox-checked";
    private const string WeightInput = "input[autoid='weight_text']";

    private const string BrandBoxXPath1 = "//div[contains(@class, 'page_edit_item')][.//*[contains(normalize-space(), 'Thương hiệu') or contains(normalize-space(), 'Brand')]]//div[contains(@class, 'ant-select-selection')]";
    private const string BrandBoxXPath2 = "//div[contains(@class, 'ant-form-item')][.//*[contains(normalize-space(), 'Thương hiệu') or contains(normalize-space(), 'Brand')] or .//div[contains(@class, 'ant-form-explain') and contains(normalize-space(), 'Brand cannot be empty')]]//div[contains(@class, 'ant-select-selection')]";
    private const string BrandBoxCss = "div.ant-form-item:has(div.ant-form-explain:has-text('Brand cannot be empty')) div.ant-select-selection";
    private const string BrandSelectedValue = ".ant-select-selection-selected-value";
    private const string BrandSearchInput1 = ".ant-select-open input.ant-select-search__field";
    private const string BrandSearchInput2 = "input.ant-select-search__field:visible";
    private const string BrandDropdownReady = ".ant-select-dropdown:not(.ant-select-dropdown-hidden), div.option";
    private static readonly string[] BrandOptions =
    {
        ".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-dropdown-menu-item",
        ".ant-select-dropdown:not(.ant-select-dropdown-hidden) [role='option']", "div.option:visible",
    };
    private static readonly Regex NoBrandRegex = new(@"^\s*No\s*brand\s*$", RegexOptions.IgnoreCase);

    private const string ImageUploadedImg = "div.supp_size_chat div.page_edit_img_item.comm_img_module img[src]";
    private const string ImageGalleryBox = "div.spc_box";
    private const string ImageUploadMenuItem = "div.spc_box ul.spc_cho li";

    private const string AddVideoButton = "button:has-text('Thêm video')";
    private const string UploadLocalVideoOpt = "li[autoid='upload_local_video_option']";
    private const string VideoBoxes = "div.pro_vid_box div.page_edit_img_item.comm_img_module";
    private const string VideoSuccessSignal = "span.top_status.bk_green:has-text('Tải lên thành công')";
    private static readonly string[] VideoErrSels =
        { ".ant-message-error", ".ant-notification-notice-message", ".ant-notification-notice-description", ".ant-message-notice-content", ".toast", ".el-message--error" };

    private const string DescriptionTextarea = "textarea[autoid='product_description_text']";
    private const string DescriptionCountBox = "span.count_box";

    private const string SaveButtonWrapper = "div[autoid='save_and_publish_button']";
    private const string SaveOption = "li[autoid='save_and_publish_option']";
    private const string ConfirmPrimaryBtn = "div.ant-modal-confirm-btns button.ant-btn-primary";
    private static readonly string[] SaveErrSels =
        { "div.ant-message", "div.ant-message-notice", "div.ant-message-notice-content", "div.ant-notification", "div.ant-modal-root", "div[role='alert']", "body" };
    private const string SuccessTitle = ".ant-modal-confirm-title, .ant-modal-title";
    private const string SuccessBody = ".ant-modal-body, .ant-modal-confirm-content";
    private const string SuccessClose = ".ant-modal:visible button";

    private static readonly Regex EditIdRegex = new(@"/edit/(\d+)\.htm", RegexOptions.IgnoreCase);

    private readonly BigSellerWorkflowSettings _settings;
    private readonly Action<string> _log;
    private readonly WorkflowPauseToken? _pauseToken;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private Process? _braveProcess;

    private IReadOnlyDictionary<string, WorkbookRecord> _records = new Dictionary<string, WorkbookRecord>();
    // SP đã xử lý / bỏ qua (gồm cả "không có trong sheet") → đánh dấu để vòng quét sau KHÔNG mở/không xử lý lại.
    // KHÔNG còn xóa dòng nào trên BigSeller → "skip" là cách duy nhất để tiến tới dòng kế.
    private readonly HashSet<string> _skippedRowKeys = new();
    private readonly HashSet<string> _skippedEditIds = new();
    private readonly Dictionary<string, int> _failCounts = new();
    // True nếu ProcessProduct fail do LỖI TẠM (AI rỗng/mạng) → caller RETRY, KHÔNG xóa dòng (tránh mất SP).
    private bool _lastProcessTransient;
    // Đã log chẩn đoán "listing 0 dòng" chưa (log 1 lần/đợt-trống để khỏi spam mỗi vòng chờ).
    private bool _emptyListingDiagLogged;

    private readonly ClaimStore? _claim;
    private readonly bool _exportCookie;
    // Cache dữ liệu shop DÙNG CHUNG mọi lane (nạp 1 lần ở tầng điều phối). null = lane tự đọc workbook (đường 1-lane cũ).
    private readonly WorkbookRecordCache? _sharedRecords;
    private long _lastTokenWriteBackTick;   // throttle ghi-ngược muc_token định kỳ trong lúc chạy

    public BigSellerProductUpdateRunner(
        BigSellerWorkflowSettings settings, Action<string> log, WorkflowPauseToken? pauseToken = null,
        ClaimStore? claim = null, bool exportCookie = true, WorkbookRecordCache? sharedRecords = null)
    {
        _settings = settings;
        _log = log;
        _pauseToken = pauseToken;
        _claim = claim;
        _exportCookie = exportCookie;
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

        StartBrave();
        _log($"Đã gọi Brave PID={_braveProcess?.Id.ToString() ?? "?"}, chờ CDP port {_settings.DebugPort}...");
        if (!await new CdpClient(_settings.DebugPort).WaitForReadyAsync(90, 500, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"CDP port {_settings.DebugPort} không sẵn sàng. Đóng Brave BigSeller cũ rồi chạy lại.");

        await EnsureCookieAsync(ct).ConfigureAwait(false);

        _playwright = await Playwright.CreateAsync();
        _log($"Kết nối CDP port {_settings.DebugPort}...");
        for (var attempt = 0; attempt < 8 && _browser is null; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { _browser = await _playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{_settings.DebugPort}", new() { Timeout = 30000 }); }
            catch { await DelayAsync(3000, ct); }
        }
        if (_browser is null)
            throw new InvalidOperationException("Không kết nối được Brave qua CDP.");

        _context = _browser.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Brave chưa có browser context.");

        var page = PickListingPage(_context)
            ?? throw new InvalidOperationException("Không tìm thấy tab BigSeller.");
        await page.BringToFrontAsync();
        if (!await GoToListingPageAsync(page, false))
            throw new InvalidOperationException("Không mở được trang Listing.");

        _log(new string('=', 50));
        _log("BẮT ĐẦU UPDATE PRODUCT (C#)");
        _log(new string('=', 50));

        await OuterLoopAsync(page, ct).ConfigureAwait(false);
    }

    // ── cookie (mirror import runner) ──
    private async Task EnsureCookieAsync(CancellationToken ct)
    {
        var hasLiveSession = false;
        try
        {
            hasLiveSession = BigSellerCookieImporter.HasAuthCookie(
                await BigSellerCookieImporter.GetBigSellerCookiesAsync(_settings.DebugPort).ConfigureAwait(false));
        }
        catch { }

        if (hasLiveSession &&
            await BigSellerCookieImporter.ProbeLoggedInAsync(_settings.DebugPort, ListingUrl, _log, ct).ConfigureAwait(false) == false)
        {
            hasLiveSession = false;
            _log("Token BigSeller trong profile đã bị thu hồi — nạp lại cookie từ file account.");
        }

        if (hasLiveSession)
        {
            _log("Profile đã đăng nhập BigSeller — giữ phiên hiện tại.");
            // Chỉ lane 0 ghi cookie ra file (tránh các lane phụ đá token nhau — rotation-war).
            if (_exportCookie)
                await BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
                    _settings.DebugPort, _settings.BigSellerCookieFile, _log).ConfigureAwait(false);
        }
        else
        {
            _log("Đang import cookie BigSeller từ file...");
            await BigSellerCookieImporter.ImportFromFileAsync(
                _settings.DebugPort, _settings.BigSellerCookieFile ?? "", _log,
                reloadBigSellerTabs: false, navigateUrl: ListingUrl, ct).ConfigureAwait(false);
            if (await BigSellerCookieImporter.ProbeLoggedInAsync(_settings.DebugPort, ListingUrl, _log, ct).ConfigureAwait(false) == false)
                _log("Cookie từ file cũng hết hạn — mở tab Account, login lại rồi Save & close.");
        }
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

                if (terminal) throw new LaneAbortedException("Shopee chặn (captcha) hoặc lỗi edit không phục hồi");

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
                        return;
                    case "exhausted":   // Có dòng nhưng trang này hết dòng lane này xử lý được (đã xong/đã skip/lane khác giữ).
                        emptyStreak = 0;
                        if (await ClickNextListingPageAsync(page, ct).ConfigureAwait(false)) break;
                        _log("✔ Hết trang cuối — không còn item id cần update trên mọi trang. Lane kết thúc.");
                        return;
                    case "retry":
                        emptyStreak = 0;
                        await DelayAsync(1200, ct);
                        break;
                    default:   // ok / deleted / skipped → đã tiến 1 dòng, quét tiếp trang hiện tại.
                        emptyStreak = 0;
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
    }

    // ── phân trang Listing ──
    // Bấm "Next Page" (li.next_item) trên thanh phân trang BigSeller (giống bảng Crawl). Trang cuối →
    // li.next_item.disabled → KHÔNG có :not(.disabled) → trả false (báo caller: hết trang → kết thúc lane).
    // Chờ nhãn "X / Y" (li.now_page_item) ĐỔI rồi mới cho quét → tránh quét nhầm DOM trang cũ (nhảy sót trang).
    private const string PaginationNowPage = ".pagination li.now_page_item";
    private async Task<bool> ClickNextListingPageAsync(IPage page, CancellationToken ct)
    {
        try
        {
            string before = "";
            try { before = (await page.Locator(PaginationNowPage).First.InnerTextAsync(new() { Timeout = 1500 })).Trim(); } catch { }

            var clicked = await page.EvaluateAsync<bool>(
                @"() => {
                    const next = document.querySelector('.pagination li.next_item:not(.disabled)');
                    if (!next) return false;
                    const action = next.querySelector('a.paging_action, a, button') || next;
                    action.click();
                    return true;
                }");
            if (!clicked) return false;   // trang cuối (next_item.disabled) → hết trang

            // Chờ nhãn trang "X / Y" ĐỔI = xác nhận trang ĐÃ sang thật. Nếu bấm được nhưng nhãn KHÔNG đổi trong
            // ~10s ⇒ trang không lật (glitch) → trả FALSE (caller kết thúc lane) thay vì lặp bấm-Next vô tận.
            var changed = false;
            for (var i = 0; i < 40; i++)
            {
                ct.ThrowIfCancellationRequested();
                string now = "";
                try { now = (await page.Locator(PaginationNowPage).First.InnerTextAsync(new() { Timeout = 500 })).Trim(); } catch { }
                if (!string.IsNullOrEmpty(now) && !string.Equals(now, before, StringComparison.Ordinal)) { changed = true; _log($"→ Sang trang Listing: {before} → {now}."); break; }
                await DelayAsync(250, ct);
            }
            if (!changed)
            {
                _log($"  (bấm Next nhưng trang không lật sau ~10s — coi như hết trang: '{before}')");
                return false;
            }
            try { await page.WaitForSelectorAsync(ListingReadySelector, new() { State = WaitForSelectorState.Visible, Timeout = 10000 }); } catch { }
            await DelayAsync(600, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log($"  (lỗi chuyển trang Listing, coi như chưa sang được: {ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// Ghi NGƯỢC muc_token (server vừa xoay) từ browser ra file ĐỊNH KỲ trong lúc chạy — chỉ lane 0
    /// (<see cref="_exportCookie"/>) để tránh rotation-war, throttle 90s. Đây là điều Scrape làm sau MỖI link
    /// mà Update trước đây THIẾU (chỉ export lúc đầu + lúc đóng) → file thiu giữa chừng → lane khác / lần chạy
    /// sau import token CŨ → BigSeller đá phiên ("log in first"). Dùng engine cookie DÙNG CHUNG ở Core.
    /// </summary>
    private async Task MaybeWriteBackBigSellerTokenAsync(CancellationToken ct)
    {
        if (!_exportCookie || string.IsNullOrWhiteSpace(_settings.BigSellerCookieFile))
            return;
        var now = Environment.TickCount64;
        if (now - _lastTokenWriteBackTick < 90_000)
            return;
        _lastTokenWriteBackTick = now;
        try
        {
            await BigSellerCookieEngine.WriteBackLiveTokenAsync(
                _settings.DebugPort, _settings.BigSellerCookieFile!, _log, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

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
            // NHẢ claim rowKey khi dòng CHƯA xong-hẳn: "retry" (lỗi tạm) HOẶC terminal (lane sắp chết/restart).
            // Nếu KHÔNG nhả ở terminal → dòng bị "claim mồ côi" (ClaimStore chung không hết-hạn) → sau restart
            // không lane nào claim lại được → bỏ sót SP mà vẫn báo Hub "completed". Giữ claim CHỈ khi ok/deleted/skipped.
            if (_claim is not null && (res.terminal || res.result == "retry")) _claim.Release(rowKey);
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
            var editBtns = await page.Locator(ListingEditButton).CountAsync().ConfigureAwait(false);
            var empty = await page.Locator(".ant-empty, .ant-table-placeholder").CountAsync().ConfigureAwait(false);
            _log($"⚠ Không thấy dòng SP để update. URL={page.Url}");
            _log($"   chẩn đoán: ant-table={ant} dòng · vxe-table={vxe} dòng · nút Edit={editBtns} · bảng-rỗng={empty}.");
            if (vxe > 0 && ant == 0)
                _log("   → BigSeller đã đổi bảng listing sang vxe-table; cần đổi selector dòng/edit (báo mình để sửa).");
            else if (empty > 0 || (ant == 0 && vxe == 0 && editBtns == 0))
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
                keepClaim = true; return ("skipped", false);
            }
            if (_skippedEditIds.Contains(actualEditId))
            {
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                keepClaim = true; return ("skipped", false);
            }

            // SONG SONG — claim TẦNG 2 theo edit-id thật: phòng 2 dòng draft khác nhau cùng trỏ 1 SP
            // → lane khác đang sửa đúng SP này thì bỏ qua (đóng tab ở finally, KHÔNG xóa).
            if (_claim is not null && !_claim.TryClaim($"edit:{actualEditId}"))
            {
                // Nạp rowKey vào skipped để vòng sau XÓA dòng draft trùng này — nếu không, dòng vẫn nằm
                // ở listing, lane cứ bám row #0 quét lại mỗi vòng → không tiến/không kết thúc (treo).
                if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
                keepClaim = true; return ("skipped", false);   // claim tầng-2 do LANE KHÁC giữ → KHÔNG nhả hộ (editClaimKey vẫn null)
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
                keepClaim = true; return ("skipped", false);
            }

            var ok = await ProcessProductAsync(editPage, record!, ct).ConfigureAwait(false);
            if (!ok)
            {
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
                keepClaim = true; return ("skipped", false);
            }

            _skippedEditIds.Add(actualEditId);
            if (!string.IsNullOrEmpty(rowKey)) _skippedRowKeys.Add(rowKey);
            _log($"✅ HOÀN TẤT XỬ LÝ SKU: {record!.Sku}");
            await OverlayAsync($"✅ Hoàn tất SKU {record!.Sku}");
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
                if (getTotal() >= 9) { keepEditOpen = true; return (null, true); }
                await DismissBlockingModalAsync(page);
                if (getStreak() >= 3) { await GoToListingPageAsync(page, true); setStreak(0); }
                return ("retry", false);
            }
            _log($"  ↳ Lỗi không phục hồi: {msg}");
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
    private async Task<bool> ProcessProductAsync(IPage page, WorkbookRecord rec, CancellationToken ct)
    {
        _lastProcessTransient = false;
        await StepAsync($"Xử lý SKU {rec.Sku}");

        await StepAsync("Sửa tên sản phẩm");
        // [1] name — CẮT ≤120 ký tự (giới hạn Shopee, tránh BigSeller báo lỗi), giữ SKU ở cuối.
        // fill fail KHÔNG làm rớt cả SP (giữ tên cũ, vẫn lưu phần còn lại như Python).
        var nameToFill = TruncateProductNamePreservingSku(rec.ProductName, rec.Sku, MaxProductNameChars);
        var rawLen = (rec.ProductName ?? "").Trim().Length;
        if (rawLen > MaxProductNameChars)
            _log($"  ✂ Tên dài {rawLen} ký tự → cắt còn {nameToFill.Length} (≤{MaxProductNameChars}, giữ SKU).");
        if (!await FillProductNameAsync(page, nameToFill, ct))
            _log("  ⚠ Không điền được tên SP — giữ tên cũ, tiếp tục xử lý.");

        await StepAsync("Đồng bộ ảnh (MD5)");
        // [2] md5 sync
        try
        {
            var md5 = page.Locator(Md5Button).First;
            if (await md5.IsVisibleAsync())
            {
                await md5.ScrollIntoViewIfNeededAsync();
                await md5.ClickAsync();
                try { await page.Locator(Md5CompleteStatus).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 }); } catch { }
                await CloseVisibleAntModalAsync(page, 5000);
            }
        }
        catch { }

        // [3] radio "Tải lên hình ảnh"
        try
        {
            var r = page.Locator(UploadImageRadioWrapper).Filter(new() { HasTextString = "Tải lên hình ảnh" }).First;
            if (await r.IsVisibleAsync()) await r.ClickAsync();
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
        // [7] stock (skip if 0)
        await ForEachVisibleAsync(page.Locator(VariationStockInputs), async el =>
        {
            var cur = ParseDigits(await el.InputValueAsync());
            if (cur == 0) return;
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

        // [11] image (non-fatal)
        var imagePath = _settings.ImagePath;
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            await StepAsync("Import ảnh");
            try { await UploadImageWithRetryAsync(page, imagePath, 3, ct); } catch { }
        }

        await StepAsync("Tạo mô tả AI");
        // [12.1] AI description — rỗng sau retry = LỖI TẠM → retry vòng sau, KHÔNG xóa dòng (tránh mất SP).
        var aiContent = await GenerateDescriptionAsync(rec.ProductName ?? "", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(aiContent)) { _lastProcessTransient = true; return false; }
        if (!await UpdateDescriptionAsync(page, aiContent, ct)) return false;

        await StepAsync("Lưu sản phẩm");
        // [12.2] save
        return await SaveWithImageRetryAsync(page, imagePath, 3, ct).ConfigureAwait(false);
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
                await wrapper.HoverAsync();
                await DelayAsync(1000, ct);

                var opt = page.Locator(SaveOption);
                if (await opt.IsVisibleAsync()) await opt.ClickAsync(new() { Force = true });
                else await wrapper.Locator("button").First.ClickAsync();

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
                var title = Normalize(await SafeInnerTextAsync(page.Locator(SuccessTitle).First));
                var body = Normalize(await SafeInnerTextAsync(page.Locator(SuccessBody).First));
                var close = Normalize(await SafeInnerTextAsync(page.Locator(SuccessClose).First));
                if (title.Contains("thao tac thanh cong") || Regex.IsMatch(title, @"^\s*successfully\s*$", RegexOptions.IgnoreCase) ||
                    body.Contains("de trinh") || body.Contains("thao tac thanh cong") ||
                    Regex.IsMatch(body, "pending by shopee", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(body, @"publishing\s*/\s*failed\s*/\s*active", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(close, "close this page", RegexOptions.IgnoreCase))
                    return true;
            }
            catch { }
            await DelayAsync(step, CancellationToken.None);
            deadline -= step;
        }
        return false;
    }

    // ── helpers ──
    private async Task<string> DraftRowKeyAsync(ILocator row)
    {
        var key = await row.GetAttributeAsync(ListingRowKeyAttr);
        if (string.IsNullOrEmpty(key)) key = await row.GetAttributeAsync("data-row-key");   // bảng ant cũ
        if (!string.IsNullOrEmpty(key)) return $"key:{key}";
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

    private static async Task<string> SafeInnerTextAsync(ILocator loc)
    {
        try { return await loc.CountAsync() > 0 ? await loc.InnerTextAsync(new() { Timeout = 500 }) : ""; }
        catch { return ""; }
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
    private static string Normalize(string? s)
    {
        s ??= "";
        s = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (c == 'đ' || c == 'Đ') { sb.Append('d'); continue; }
            sb.Append(char.ToLowerInvariant(c));
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static int ParseDigits(string? s)
    {
        var digits = new string((s ?? "").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : 0;
    }

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

    // Cắt tên SP về tối đa maxLength ký tự, ƯU TIÊN giữ SKU ở cuối (bỏ bớt từ ở thân). Parity name-rewrite.
    private static string TruncateProductNamePreservingSku(string? productName, string? sku, int maxLength)
    {
        var name = (productName ?? "").Trim();
        sku = (sku ?? "").Trim();
        if (name.Length <= maxLength) return name;

        if (!string.IsNullOrWhiteSpace(sku) && name.EndsWith(sku, StringComparison.Ordinal))
        {
            var body = name[..^sku.Length].Trim();
            var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 0)
            {
                var candidate = $"{string.Join(" ", words)} {sku}".Trim();
                if (candidate.Length <= maxLength) return candidate;
                words.RemoveAt(words.Count - 1);
            }
            return sku.Length <= maxLength ? sku : sku[..maxLength].Trim();
        }

        var allWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (allWords.Count > 0)
        {
            var candidate = string.Join(" ", allWords).Trim();
            if (candidate.Length <= maxLength) return candidate;
            allWords.RemoveAt(allWords.Count - 1);
        }
        return name[..Math.Min(maxLength, name.Length)].Trim();
    }

    private void StartBrave()
    {
        Directory.CreateDirectory(_settings.ProfileDir);
        var args = string.Join(" ", new[]
        {
            $"--remote-debugging-port={_settings.DebugPort}",
            $"--user-data-dir=\"{_settings.ProfileDir}\"",
            "--no-first-run", "--no-default-browser-check", "--no-session-restore",
            "--restore-last-session=false", "--disable-session-crashed-bubble",
            "--start-maximized", "--window-size=1920,1080",
            "--disable-gpu", "--disable-dev-shm-usage", "--disable-software-rasterizer",
            Shopee.Core.Browser.BraveCachePolicy.DiskLimitArgString,
            $"\"{ListingUrl}\"",
        });
        // Đăng ký profile vào "fleet" TRƯỚC khi phóng → trình dọn Brave mồ côi (BraveFleet) CHỪA cửa sổ
        // update này. Thiếu bước này = Brave bị quét-giết như mồ côi giữa chừng (xem chú thích ở import).
        BraveFleet.RegisterActiveProfile(_settings.ProfileDir);

        _log("Mở Brave BigSeller profile...");
        _braveProcess = Process.Start(new ProcessStartInfo
        {
            FileName = _settings.BravePath, Arguments = args, UseShellExecute = false,
        });
    }

    private Task WaitIfNotPausedAsync(CancellationToken ct) =>
        _pauseToken?.WaitWhileRunningAsync(ct) ?? Task.CompletedTask;

    private Task DelayAsync(int ms, CancellationToken ct) =>
        _pauseToken?.DelayAsync(ms, ct) ?? Task.Delay(ms, ct);

    public async ValueTask DisposeAsync()
    {
        // Lưu cookie CHỈ lane 0 (tránh lane phụ đá token) + TIMEOUT (Brave có thể treo → không để chặn việc kill).
        if (_exportCookie && _braveProcess is { HasExited: false })
        {
            try
            {
                await Task.WhenAny(
                    BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
                        _settings.DebugPort, _settings.BigSellerCookieFile, _log, verifySessionAlive: true),
                    Task.Delay(6000)).ConfigureAwait(false);
            }
            catch { }
        }

        // KILL Brave NGAY (đóng profile + giải phóng RAM) — KHÔNG phụ thuộc dispose browser/playwright (có thể treo).
        if (_braveProcess is not null)
        {
            try { if (!_braveProcess.HasExited) _braveProcess.Kill(entireProcessTree: true); } catch { }
            try { _braveProcess.Dispose(); } catch { }
            _braveProcess = null;
        }
        // Fallback: Brave hay fork browser thật rồi để stub thoát ngay → _braveProcess.HasExited=true,
        // Kill ở trên no-op, browser thật thành orphan (giữ profile lock + RAM). Diệt theo --user-data-dir.
        try { BraveProcessReaper.KillByUserDataDir(_settings.ProfileDir, _log); } catch { }

        // Gỡ đăng ký SAU khi đã giết → còn sót tiến trình nào thì lần sweep kế dọn nốt.
        BraveFleet.UnregisterActiveProfile(_settings.ProfileDir);

        if (_browser is not null)
        {
            try { await Task.WhenAny(_browser.DisposeAsync().AsTask(), Task.Delay(3000)).ConfigureAwait(false); } catch { }
            _browser = null;
        }
        try { _playwright?.Dispose(); } catch { }
        _playwright = null;
    }
}
