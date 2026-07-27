namespace Shopee.Core.Coordination;

// ── DTO truyền tin giữa client và Hub (dùng chung cho cả 2 phía) ───────────────

/// <summary>Acc Shopee đang được một máy DÙNG (giữ qua hub để chống dùng trùng xuyên máy).</summary>
public sealed class AccountLease
{
    public string AccountId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTimeOffset HeartbeatAt { get; set; }
}

/// <summary>Một máy đang/đã online (nhịp sống) — cho bảng trạng thái.</summary>
public sealed class MachinePresence
{
    public string MachineId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }
    public string? AppVersion { get; set; }
    /// <summary>Trần cửa sổ Brave máy này tự báo lên (0 = chưa báo/không rõ). Hub dùng để chia quỹ khi giao việc.</summary>
    public int MaxBrave { get; set; }
    /// <summary>Dòng trạng thái lệnh update app cho máy này (⏳ đã ra lệnh / 🔄 đang khởi động lại / ✓ đã lên bản…).
    /// Rỗng = chưa từng ra lệnh. Field mới: client cũ parse /fleet bỏ qua field lạ → an toàn.</summary>
    public string UpdateStatus { get; set; } = "";
    /// <summary>Thời điểm operator ra lệnh update (còn hiệu lực). null = không có lệnh đang chờ.</summary>
    public DateTimeOffset? UpdateRequestedAt { get; set; }
    /// <summary>Chế độ app của máy: "Full" | "Workspace" | "Shopee" (rỗng = client cũ chưa báo → Hub suy "Workspace").</summary>
    public string Mode { get; set; } = "";
    /// <summary>Loại SUẤT làm việc: <see cref="MachineSlots.Workspace"/> | <see cref="MachineSlots.Orders"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Id PC THẬT (gộp 2 suất của cùng một máy). Suất workspace: = <see cref="MachineId"/>.</summary>
    public string HostId { get; set; } = "";
}

/// <summary>Mục manifest của một file dùng chung trên Hub.</summary>
public sealed class FileManifestEntry
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset Mtime { get; set; }
}

/// <summary>Ảnh chụp toàn cảnh cho bảng trạng thái (1 lần gọi /fleet).</summary>
public sealed class FleetSnapshot
{
    public List<LeaseRecord> Leases { get; set; } = [];
    public List<AccountLease> AccountLeases { get; set; } = [];
    public List<WorkLedgerRecord> Ledger { get; set; } = [];
    public List<MachinePresence> Machines { get; set; } = [];

    // ── Giao việc (Hub đẩy việc cho client) ──
    public List<MachineRoleInfo> Roles { get; set; } = [];
    public List<Assignment> Assignments { get; set; } = [];
    /// <summary>Việc GIÁN ĐOẠN (failed/canceled, chưa xong) — operator bấm ▶ Tiếp tục. Field mới: client cũ bỏ qua.</summary>
    public List<Assignment> Interrupted { get; set; } = [];
}

// ── Giao việc (Hub chủ động giao việc cho client) ─────────────────────────────

/// <summary>Vai trò máy = loại việc máy nhận tự động từ Hub. Lưu chuỗi để DTO/JSON gọn.</summary>
public static class MachineRoles
{
    public const string Off = "off";
    public const string Scrape = "scrape";
    public const string Import = "import";
    public const string Update = "update";
    public const string All = "all";

    /// <summary>Op "search" KHÔNG có vai trò tự động — luôn giao TAY (ghim máy) từ bảng điều phối Search.</summary>
    public const string Search = "search";

    /// <summary>Vai trò phụ trách op này (rewrite gộp vào nhóm Update).</summary>
    public static string ForOp(string op) => op switch
    {
        "scrape" => Scrape,
        "import" => Import,
        "update" or "rewrite" => Update,
        _ => op,
    };

    /// <summary>Máy vai trò <paramref name="role"/> có nhận op <paramref name="op"/> không (All nhận hết).</summary>
    public static bool Handles(string role, string op) =>
        role == All || (role != Off && role == ForOp(op));
}

/// <summary>Vai trò đã gán cho 1 máy (Hub lưu; client đọc để biết mình nhận loại việc nào).</summary>
public sealed class MachineRoleInfo
{
    public string MachineId { get; set; } = "";
    public string Role { get; set; } = MachineRoles.Off;
}

/// <summary>Một việc Hub giao: (tài khoản BigSeller + shop + op) → máy đích hoặc theo vai trò.</summary>
public sealed class Assignment
{
    public string Id { get; set; } = "";
    public string BigsellerId { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string Op { get; set; } = "";                 // scrape | import | update | rewrite | search
    /// <summary>Ghim cứng vào 1 máy; null = để Hub định tuyến theo vai trò.</summary>
    public string? TargetMachineId { get; set; }
    /// <summary>Dữ liệu kèm theo việc (JSON). Hiện chỉ op "search" dùng: <see cref="SearchJobPayload"/>
    /// (danh sách link của khối + số acc khóa + lane + khu vực). Các op khác để rỗng.</summary>
    public string Payload { get; set; } = "";
    public bool Pinned { get; set; }
    public string Status { get; set; } = "queued";       // queued | running | done | failed | canceled
    /// <summary>Operator đã bỏ khỏi danh sách gián đoạn (hub-side; client không dùng) — ẩn khỏi ▶ Tiếp tục; status giữ nguyên.</summary>
    public bool Dismissed { get; set; }
    public string ClaimedByMachineId { get; set; } = "";
    public string ClaimedByHostname { get; set; } = "";
    public string LastError { get; set; } = "";
    /// <summary>Khoảng dòng Hub đặt cho client chạy (ghi đè cấu hình client lượt này). 0 = dùng cấu hình client.</summary>
    public int StartRow { get; set; }
    public int EndRow { get; set; }
    /// <summary>Tham số chạy Hub đặt cho lượt này (ghi đè cấu hình client). 0 = dùng cấu hình client.</summary>
    public int Processes { get; set; }
    public int FrameSize { get; set; }
    public int ReloadSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string CoordId => $"{BigsellerId}__{ShopId}__{Op}";
}

public sealed record SetRoleRequest(string MachineId, string Role);
// Processes/FrameSize/ReloadSeconds đặt SAU Payload (không phải "trước Payload") vì các call-site hiện có
// truyền StartRow/EndRow/Payload THEO VỊ TRÍ (Fleet.razor, FleetViewModel, SearchBoardService…) — chèn param
// int vào giữa sẽ nuốt nhầm đối số Payload (string) → vỡ build. Sau Payload thì mọi call-site cũ giữ nguyên.
public sealed record CreateAssignmentRequest(
    string BigsellerId, string ShopId, string Sheet, string Op, string? TargetMachineId, bool Pinned,
    int StartRow = 0, int EndRow = 0, string Payload = "",
    int Processes = 0, int FrameSize = 0, int ReloadSeconds = 0);

/// <summary>Dữ liệu việc Import Hub giao (ghi vào <see cref="Assignment.Payload"/> cho op "import"):
/// cờ import từ tab "Đã nhận" (Claimed) thay vì danh sách crawl. Payload rỗng = client dùng cấu hình của nó.</summary>
public sealed class ImportJobPayload
{
    public bool FromClaimedTab { get; set; }
}

/// <summary>Dữ liệu việc Search Hub giao cho 1 client: chạy đúng khối link này, khóa tối đa
/// <see cref="AccountsPerClient"/> tài khoản Shopee (qua account-lease) để máy khác không đụng.</summary>
public sealed class SearchJobPayload
{
    public List<string> Links { get; set; } = [];
    public int AccountsPerClient { get; set; }
    public int Lanes { get; set; } = 3;
    public string? Region { get; set; }
    public string? SourceFile { get; set; }
}

// ── Gom kết quả Search về Hub (client đẩy sản phẩm cào được → Hub gộp) ─────────
/// <summary>1 sản phẩm client gửi lên Hub: <see cref="ItemId"/> để dedup (khoá chính), <see cref="Json"/>
/// là toàn bộ ProductResult serialize (Hub CHỈ lưu blob, không cần biết model engine).</summary>
public sealed record SearchProductItem(long ItemId, string Json);
public sealed record SearchProductsPushRequest(string MachineId, string SourceFile, List<SearchProductItem> Products);

// ── Log tập trung (client gửi 1 dòng lên Hub → tab Log xem log nhiều máy) ──────
public sealed record AppendLogRequest(string MachineId, string Hostname, string Level, string Text);
public sealed class LogEntry
{
    public long Id { get; set; }
    public string MachineId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTimeOffset Ts { get; set; }
    public string Level { get; set; } = "info";   // info | ok | warn | error
    public string Text { get; set; } = "";
}

// ── Client báo acc Shopee dính captcha/lỗi về Hub (Hub xem + quyết giữ/xóa) ────
/// <summary>Client báo 1 acc Shopee bị captcha/lỗi. Status: "captcha" (vừa dính, đang tự sửa) | "failed"
/// (client không sửa được → Hub quyết). Sửa được thì client gọi clear (gỡ báo).</summary>
public sealed record AccountErrorRequest(
    string AccountId, string MachineId, string Hostname, string Reason, string? CaptchaUrl, string Status);
public sealed class AccountError
{
    public string AccountId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? CaptchaUrl { get; set; }
    public string Status { get; set; } = "captcha";
    public DateTimeOffset ReportedAt { get; set; }
}
public sealed record ClearAccountErrorRequest(string AccountId);
public sealed record ClaimAssignmentsRequest(string MachineId, string Role, int Max);
public sealed record AssignmentStatusRequest(string Id, string MachineId, string Status, string? Error);
public sealed record CancelAssignmentRequest(string Id);
/// <summary>Operator bấm ▶ Tiếp tục 1 việc đã dừng/huỷ → Hub đưa về 'queued' (giữ nguyên tham số).</summary>
public sealed record ResumeAssignmentRequest(string Id);
/// <summary>Client khởi động lại → xin nhận lại việc đang dở của CHÍNH máy mình (nhả lease chết + việc về 'queued').</summary>
public sealed record ResumeMineRequest(string MachineId);
/// <summary>Kết quả POST /assignments/resume: Error null = tiếp tục OK; ngược lại là lý do từ chối (tiếng Việt).</summary>
public sealed record ResumeAssignmentResponse(string? Error);
/// <summary>Kết quả POST /assignments/resume-mine: số việc máy này được đưa lại về 'queued'.</summary>
public sealed record ResumeMineResponse(int Requeued);
/// <summary>Hub đặt TAY trạng thái sổ hoàn thành cho 1 (shop+op): completed = ✓ xong; stopped = ■ dừng;
/// idle = chưa chạy (XOÁ bản ghi + tiến độ dòng → scrape giao lại + chạy lại từ đầu).</summary>
public sealed record SetLedgerStatusRequest(
    string Key, string BigsellerId, string ShopId, string Sheet, string Op, string Status);

// ── Request / Response ────────────────────────────────────────────────────────

public sealed record LeaseAcquireRequest(
    string Key, string BigsellerId, string ShopId, string Sheet, string Op,
    string MachineId, string Hostname, bool Force);

public sealed record LeaseAcquireResponse(bool Granted, string? BlockedByHostname);

public sealed record LeaseHeartbeatRequest(string Key, string MachineId);

public sealed record LeaseReleaseRequest(string Key, string MachineId);

public sealed record AccountReserveRequest(List<string> AccountIds, string MachineId, string Hostname);

public sealed record AccountReserveResponse(List<string> Granted, List<string> Blocked);

public sealed record AccountReleaseRequest(List<string> AccountIds, string MachineId);

/// <summary>Client ghi "nhà" (home machine) = máy này cho các tk đã được lease cấp (affinity tk↔máy, Scrape).</summary>
public sealed record SetAccountHomeRequest(List<string> AccountIds, string MachineId, string Hostname);
/// <summary>Một dòng "nhà" tk: tk <see cref="AccountId"/> thuộc máy <see cref="MachineId"/>. <see cref="Binding"/>
/// = máy nhà CÒN online (last_seen trong ngưỡng HomeTakeoverAfter) ⇒ máy khác phải tránh; false = tk tự do.</summary>
public sealed record AccountHomeItem(string AccountId, string MachineId, string Hostname, bool Binding);

/// <summary>Nhịp sống 1 SUẤT làm việc. <paramref name="MachineId"/> = id SUẤT (suất workspace GIỮ NGUYÊN id máy,
/// suất đơn hàng có hậu tố — xem <see cref="MachineSlots"/>). 3 field cuối có MẶC ĐỊNH RỖNG để client CŨ (không
/// gửi) vẫn hợp lệ; Hub suy ra qua <c>MachineSlots.Normalize*</c>.</summary>
public sealed record MachineHeartbeatRequest(
    string MachineId, string Hostname, string? AppVersion, int MaxBrave = 0,
    string Mode = "", string Kind = "", string HostId = "");
/// <summary>Phản hồi heartbeat: kênh Hub đẩy lệnh xuống client. <see cref="UpdateRequestedAt"/> null/rỗng = không có
/// lệnh; có giá trị = chuỗi ISO lúc operator ra lệnh update, client dùng làm ID dedup (chỉ update 1 lần/lệnh).
/// Là class để sau này thêm lệnh khác chỉ cần thêm field (client cũ bỏ qua field lạ).</summary>
public sealed class MachineHeartbeatResponse
{
    public string? UpdateRequestedAt { get; set; }

    /// <summary>Lệnh Hub giao cho SUẤT ĐƠN HÀNG này (▶ Chạy / ✖ Dừng một tài khoản). Rỗng = không có lệnh.
    /// Field MỚI: client cũ bỏ qua field lạ khi parse → hub deploy trước, client release sau vẫn an toàn.</summary>
    public List<OrdersCommandDto> OrdersCommands { get; set; } = [];
}
/// <summary>Client báo tiến trình/kết quả tự-update app về Hub. Status: "checking" | "restarting" | "already-latest"
/// | "unsupported" | "failed: &lt;lý do&gt;" — Hub map thành dòng trạng thái + clear cờ khi terminal.</summary>
public sealed record UpdateAckRequest(string MachineId, string Status);
/// <summary>Client báo Hub "tôi rời đi" (bấm Ngắt kết nối) → Hub xoá khỏi danh sách máy ngay.</summary>
public sealed record MachineLeaveRequest(string MachineId);

// ── GƯƠNG danh bạ tài khoản Đơn hàng (client đẩy lên; Hub KHÔNG sở hữu) ───────
// Tài khoản Đơn hàng (Email/Password/Cookie/ProxyKey/PickupAddress…) nằm trong CSDL cục bộ của TỪNG máy. Hub chỉ
// giữ một BẢN GƯƠNG để trang điều phối biết "máy này có những tài khoản nào" mà ra lệnh — TUYỆT ĐỐI không nhận
// mật khẩu / cookie / mật khẩu hòm thư (đó là lý do chọn mô hình gương thay vì đưa acc lên hub làm nguồn sự thật).

/// <summary>Một shop con của tài khoản Đơn hàng trong gương. <see cref="Login"/> = <c>shop_login</c> phía client —
/// CŨNG là <c>shops.username</c> trên hub (client đẩy đơn theo khoá này) nên hub tra được số đơn của shop;
/// <see cref="Name"/> chỉ để hiển thị.</summary>
public sealed record OrdersShopItem(string Login, string Name);

/// <summary>Một tài khoản Đơn hàng trên MỘT máy (gương). KHOÁ là <see cref="Login"/> (email đăng nhập), KHÔNG
/// phải Id cục bộ: mỗi máy tự tạo bản ghi tài khoản nên Id của CÙNG một tài khoản LỆCH giữa các máy.
/// <see cref="SessionState"/> nhận giá trị trong <see cref="OrdersSessionStates"/>.</summary>
public sealed record OrdersAccountItem(
    string Login,
    string SessionState,
    List<OrdersShopItem> Shops,
    bool VerifyFailed,
    DateTimeOffset? LastSyncAt);

/// <summary>Client đẩy TOÀN BỘ danh bạ tài khoản Đơn hàng của CHÍNH MÁY MÌNH lên hub. <see cref="MachineId"/> =
/// id SUẤT ĐƠN HÀNG (<c>&lt;id-máy&gt;:orders</c>, xem <see cref="MachineSlots"/>). Hợp đồng: hub THAY TOÀN BỘ
/// danh bạ của máy đó (client là nguồn sự thật cho danh sách của chính nó), KHÔNG đụng máy khác.</summary>
public sealed record OrdersAccountsPushRequest(string MachineId, string Hostname, List<OrdersAccountItem> Accounts);

/// <summary>Trạng thái phiên tài khoản Đơn hàng trong gương (chuỗi để DTO/JSON gọn, y khuôn
/// <see cref="MachineRoles"/>). Rỗng = không có phiên / đã dừng / lỗi.</summary>
public static class OrdersSessionStates
{
    public const string Idle = "";
    public const string Queued = "queued";
    public const string Opening = "opening";
    public const string Running = "running";
    public const string Stopping = "stopping";
}

// ── Lệnh Hub → SUẤT ĐƠN HÀNG (đi trong phản hồi heartbeat + ack) ──────────────

/// <summary>Hành động hub ra lệnh cho một tài khoản Đơn hàng trên một máy.</summary>
public static class OrdersCommandActions
{
    public const string Run = "run";
    public const string Stop = "stop";
    /// <summary>Đồng bộ đơn MỘT lượt. CHƯA hỗ trợ ở bản này (client không có điểm vào cấp service — phiên là một
    /// vòng liên tục login→mọi shop→sync), client ack 'failed' nếu nhận được.</summary>
    public const string SyncOnce = "sync-once";
    /// <summary>Đăng nhập lại / kiểm tra tài khoản. CHƯA hỗ trợ ở bản này — xem <see cref="SyncOnce"/>.</summary>
    public const string Relogin = "relogin";
}

/// <summary>Vòng đời một lệnh: hub tạo 'pending' → nhịp heartbeat lấy đi thành 'sent' → client ack 'done'/'failed'.
/// 'sent' quá lâu không ack → hub tự quy về 'failed' (client không phản hồi), không kẹt vĩnh viễn.</summary>
public static class OrdersCommandStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>Một lệnh hub gửi xuống suất đơn hàng trong phản hồi heartbeat. <see cref="Id"/> là khoá DEDUP:
/// nhịp có thể lặp khi mạng chập chờn, client PHẢI thực thi mỗi Id đúng một lần (chạy lại 'run' giữa chừng một
/// phiên đang chạy là mở lại trình duyệt — hỏng thật).</summary>
public sealed record OrdersCommandDto(string Id, string Login, string Action);

/// <summary>Client báo kết quả thực thi một lệnh về hub. <see cref="Status"/> ∈
/// <see cref="OrdersCommandStatuses.Done"/> | <see cref="OrdersCommandStatuses.Failed"/>.</summary>
public sealed record OrdersCommandAckRequest(string Id, string Status, string? Error);

public sealed record FilePutResponse(bool Ok, int Version, string? Conflict);

/// <summary>Kết quả upsert acc BigSeller từ client lên hub (POST /bigseller/upsert): số acc mới thêm, số acc đã
/// có được cập nhật (field chung hoặc shop), tổng số shop mới thêm. Hub KHÔNG bao giờ xoá acc/shop.</summary>
public sealed record BigSellerUpsertResult(int Added, int Updated, int ShopsAdded);

/// <summary>Client nhờ Hub đăng nhập lại 1 acc BigSeller (gặp verify/mất phiên). <see cref="MachineId"/> để Hub
/// ghi log biết máy nào xin. CỐ Ý không có mật khẩu: credential nằm SẴN trên hub, đừng đẩy qua dây.</summary>
public sealed record BigSellerReloginRequest(string AccountId, string MachineId);

/// <summary>Trạng thái phiên login trên Hub. <see cref="Status"/> = idle|running|needsOtp|success|failed
/// (nguyên văn LoginState.Status). <see cref="Accepted"/> = Hub vừa BẮT ĐẦU phiên mới cho lượt xin này
/// (false = đã có phiên đang chạy → client cứ chờ phiên đó).</summary>
public sealed record BigSellerReloginResponse(bool Accepted, string Status, string Message);
