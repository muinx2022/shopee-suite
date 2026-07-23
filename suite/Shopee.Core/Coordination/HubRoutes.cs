namespace Shopee.Core.Coordination;

/// <summary>
/// Bảng hằng đường dẫn (route) của API Hub — nguồn sự thật cho <see cref="HubClient"/> (phía client) và,
/// khi không vướng file WIP, cho ClientApiEndpoints (phía server) để 2 đầu KHÔNG lệch literal. Toàn bộ là
/// path tương đối gốc "/". Endpoint có tham số động (file theo tên, log theo query) chỉ để phần TĨNH ở đây,
/// caller tự nối phần động.
/// </summary>
public static class HubRoutes
{
    public const string Health = "/health";

    // ── Máy ──
    public const string MachineLeave = "/machines/leave";
    public const string MachineHeartbeat = "/machines/heartbeat";
    public const string MachineUpdateAck = "/machines/update-ack";
    public const string Fleet = "/fleet";
    public const string Roles = "/roles";

    // ── Khoá việc (lease) ──
    public const string LeasesAcquire = "/leases/acquire";
    public const string LeasesHeartbeat = "/leases/heartbeat";
    public const string LeasesRelease = "/leases/release";

    // ── Khoá tài khoản ──
    public const string AccountsReserve = "/accounts/reserve";
    public const string AccountsRelease = "/accounts/release";
    public const string AccountsHeartbeat = "/accounts/heartbeat";
    public const string AccountsErrored = "/accounts/errored";
    public const string AccountsErroredClear = "/accounts/errored/clear";

    // ── Sổ hoàn thành (ledger) ──
    public const string Ledger = "/ledger";
    public const string LedgerSet = "/ledger/set";

    // ── Vai trò máy + giao việc ──
    public const string Assignments = "/assignments";
    public const string AssignmentsClaim = "/assignments/claim";
    public const string AssignmentsStatus = "/assignments/status";
    public const string AssignmentsCancel = "/assignments/cancel";
    public const string AssignmentsResume = "/assignments/resume";
    public const string AssignmentsResumeMine = "/assignments/resume-mine";

    // ── Kho gộp kết quả Search ──
    public const string SearchProducts = "/search-products";
    public const string SearchProductsCount = "/search-products/count";
    public const string SearchProductsClear = "/search-products/clear";

    // ── Log tập trung ──
    public const string Logs = "/logs";
    public const string LogsClear = "/logs/clear";

    // ── Kho sản phẩm (Postgres — thay dần workbook Excel) ──
    public const string ProductsSheets = "/products/sheets";
    public const string ProductsLinks = "/products/links";
    public const string ProductsRecordMap = "/products/record-map";
    public const string ProductsImportIds = "/products/import-ids";
    public const string ProductsRewritePending = "/products/rewrite-pending";
    public const string ProductsRewritten = "/products/rewritten";
    public const string ProductsAppend = "/products/rows/append";
    public const string ProductsImportXlsx = "/products/import-xlsx";
    public const string ProductsExportXlsx = "/products/export-xlsx";
    // ── RESUME per-SP (tiến độ Import/Update bền xuyên kill) ──
    public const string ProductsMarkImported = "/products/mark-imported";
    public const string ProductsMarkUpdated = "/products/mark-updated";
    public const string ProductsResetStoreProgress = "/products/reset-store-progress";
    // ── Trang "📦 Dữ liệu" (mọi shop) — client desktop thao tác qua các route này ──
    public const string ProductsAllData = "/products/all-data";
    public const string ProductsMarkSold = "/products/mark-sold";
    /// <summary>+1 "Đã bán" theo SKU khớp tuyệt đối (mọi shop) — module Đơn hàng gọi khi đơn chuyển sang đã-giao.</summary>
    public const string ProductsMarkSoldBySku = "/products/mark-sold-by-sku";
    public const string ProductsResetSold = "/products/reset-sold";
    public const string ProductsRegenSkus = "/products/regen-skus";
    public const string ProductsDeleteRows = "/products/rows/delete";
    public const string ProductsUpdateRow = "/products/rows/update";
    public const string ProductsInsertRow = "/products/rows/insert";
    public const string ProductsSkuExists = "/products/sku-exists";

    // ── Cấu hình BigSeller (client → hub upsert) ──
    /// <summary>Client đẩy (upsert) acc/shop BigSeller của máy mình lên hub — client giờ là nguồn phát sinh
    /// acc/shop; hub gộp KHÔNG XÓA (kẻo lượt pull mirror-xoá acc client vừa thêm).</summary>
    public const string BigSellerUpsert = "/bigseller/upsert";

    // ── Nghiệp vụ đơn hàng ── (prefix /api BẮT BUỘC: tránh AmbiguousMatchException với trang Blazor /shops, /orders)
    public const string Shops = "/api/shops";
    public const string Orders = "/api/orders";
    public const string OrdersPush = "/api/orders/push";
    /// <summary>Client đẩy file phiếu PDF (base64, lô ≤5) của các đơn ĐÃ lên hub → hub lưu đĩa + đặt slip_at.</summary>
    public const string OrdersSlip = "/api/orders/slip";

    // ── File-sync ──
    public const string Manifest = "/manifest";

    /// <summary>Tiền tố endpoint file — nối tên đã encode: <c>HubRoutes.Files + EncodePath(name)</c>.</summary>
    public const string Files = "/files/";
}
