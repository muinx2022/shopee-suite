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

    // ── Kho gộp kết quả Search ──
    public const string SearchProducts = "/search-products";
    public const string SearchProductsCount = "/search-products/count";
    public const string SearchProductsClear = "/search-products/clear";

    // ── Log tập trung ──
    public const string Logs = "/logs";
    public const string LogsClear = "/logs/clear";

    // ── File-sync ──
    public const string Manifest = "/manifest";

    /// <summary>Tiền tố endpoint file — nối tên đã encode: <c>HubRoutes.Files + EncodePath(name)</c>.</summary>
    public const string Files = "/files/";
}
