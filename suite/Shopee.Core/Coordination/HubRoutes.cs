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
    /// <summary>Affinity tk↔máy (Scrape): POST ghi "nhà" máy này cho các tk; GET đọc danh sách home + cờ binding.</summary>
    public const string AccountsHome = "/accounts/home";

    // ── Sổ hoàn thành (ledger) ──
    public const string Ledger = "/ledger";
    public const string LedgerSet = "/ledger/set";
    /// <summary>POST: mở lại các dòng ĐÃ BỎ QUA của 1 việc — bỏ chúng khỏi vùng phủ + xoá sổ + status về
    /// stopped. Route MỚI (08/2026): client cũ không gọi nên hub mới deploy trước là vô hại.</summary>
    public const string LedgerReopenSkipped = "/ledger/reopen-skipped";

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

    /// <summary>POST = client NHỜ hub đăng nhập lại 1 acc BigSeller (gặp verify code / mất phiên) — hub tự login
    /// (captcha AI + tự đọc mã OTP từ hòm thư) rồi ghi cookie vào kho, client kéo về. GET <c>?accountId=</c> = đọc
    /// trạng thái phiên login đó. Như <see cref="BigSellerUpsert"/>, CỐ Ý nằm ngoài tiền tố <c>config/</c> nên
    /// không dính chặn <c>AllowClientConfigPush</c>. Mật khẩu KHÔNG đi qua dây — client chỉ gửi AccountId.</summary>
    public const string BigSellerRelogin = "/bigseller/relogin";

    // ── Cấu hình DÙNG CHUNG của module Đơn hàng (khối GSheet) ──
    /// <summary>GET = client kéo cấu hình GSheet dùng chung về; POST = client đẩy bản vừa sửa ở Cài đặt lên.
    /// CỐ Ý đặt NGOÀI tiền tố <c>config/</c> (đường file) để không dính chặn <c>AllowClientConfigPush</c> —
    /// đây là đường hợp lệ để client góp cấu hình, như <see cref="BigSellerUpsert"/>.</summary>
    public const string OrdersConfig = "/orders-config";

    // ── Gương danh bạ tài khoản Đơn hàng + lệnh hub → suất đơn hàng ──
    /// <summary>Client đẩy GƯƠNG danh bạ tài khoản Đơn hàng của máy mình (login + shop con + trạng thái phiên +
    /// 3 ô đăng nhập từ 11/08/2026; KHÔNG cookie). Hub thay TOÀN BỘ danh bạ của đúng máy đó — trừ 3 ô đăng nhập,
    /// nơi ô rỗng không xoá giá trị đang giữ. Như <see cref="BigSellerUpsert"/>, CỐ Ý nằm ngoài tiền tố
    /// <c>config/</c> nên không dính chặn <c>AllowClientConfigPush</c>.</summary>
    public const string OrdersAccounts = "/orders/accounts";

    /// <summary>GET: máy MỚI kéo DANH BẠ sub-acc Đơn hàng GỘP TỪ MỌI MÁY (login + shop con + 3 ô đăng nhập;
    /// KHÔNG cookie). Máy mới tạo sẵn bản ghi tài khoản dùng được ngay; ô nào chưa máy nào nhập thì về rỗng và
    /// người dùng tự nhập. CỐ Ý nằm ngoài tiền tố <c>config/</c> nên không dính chặn
    /// <c>AllowClientConfigPush</c>.</summary>
    public const string OrdersAccountsDirectory = "/orders/accounts/directory";

    /// <summary>Client báo kết quả thực thi một lệnh hub giao (lệnh ĐI trong phản hồi heartbeat, không có route
    /// riêng để lấy).</summary>
    public const string OrdersCommandsAck = "/orders/commands/ack";

    // ── Nghiệp vụ đơn hàng ── (prefix /api BẮT BUỘC: tránh AmbiguousMatchException với trang Blazor /shops, /orders)
    public const string Shops = "/api/shops";
    public const string Orders = "/api/orders";
    public const string OrdersStats = "/api/orders/stats";
    public const string OrdersPush = "/api/orders/push";
    /// <summary>Client đẩy file phiếu PDF (base64, lô ≤5) của các đơn ĐÃ lên hub → hub lưu đĩa + đặt slip_at.</summary>
    public const string OrdersSlip = "/api/orders/slip";
    /// <summary>Client báo sự kiện lỗi app (vd. không đặt được địa chỉ) → Hub quyết định gửi webhook lỗi app.</summary>
    public const string OrdersAppAlert = "/api/orders/app-alert";
    /// <summary>POST upsert / POST dismiss / GET ?accountLogin= — banner lỗi địa chỉ bền, khóa theo tài khoản+shop.</summary>
    public const string OrdersPickupAlerts = "/api/orders/pickup-alerts";
    public const string OrdersPickupAlertsUpsert = "/api/orders/pickup-alerts/upsert";
    public const string OrdersPickupAlertsDismiss = "/api/orders/pickup-alerts/dismiss";
    /// <summary>GET <c>?day=yyyy-MM-dd</c> → số đơn ĐÃ "chuẩn bị hàng" theo shop trong ngày đó, hub đếm THẲNG từ
    /// bảng đơn (mỗi đơn 1 dòng) nên là số CHUNG toàn hệ thống, không cộng trùng dù nhiều máy cùng chạy.</summary>
    public const string PrepareStats = "/prepare-stats";

    // ── File-sync ──
    public const string Manifest = "/manifest";

    /// <summary>Tiền tố endpoint file — nối tên đã encode: <c>HubRoutes.Files + EncodePath(name)</c>.</summary>
    public const string Files = "/files/";
}
