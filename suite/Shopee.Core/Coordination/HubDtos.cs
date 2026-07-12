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

public sealed record MachineHeartbeatRequest(string MachineId, string Hostname, string? AppVersion, int MaxBrave = 0);
/// <summary>Client báo Hub "tôi rời đi" (bấm Ngắt kết nối) → Hub xoá khỏi danh sách máy ngay.</summary>
public sealed record MachineLeaveRequest(string MachineId);

public sealed record FilePutResponse(bool Ok, int Version, string? Conflict);

/// <summary>Kết quả upsert acc BigSeller từ client lên hub (POST /bigseller/upsert): số acc mới thêm, số acc đã
/// có được cập nhật (field chung hoặc shop), tổng số shop mới thêm. Hub KHÔNG bao giờ xoá acc/shop.</summary>
public sealed record BigSellerUpsertResult(int Added, int Updated, int ShopsAdded);
