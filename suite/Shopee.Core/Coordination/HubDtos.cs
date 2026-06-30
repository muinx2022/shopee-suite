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
    public string Op { get; set; } = "";                 // scrape | import | update | rewrite
    /// <summary>Ghim cứng vào 1 máy; null = để Hub định tuyến theo vai trò.</summary>
    public string? TargetMachineId { get; set; }
    public bool Pinned { get; set; }
    public string Status { get; set; } = "queued";       // queued | running | done | failed | canceled
    public string ClaimedByMachineId { get; set; } = "";
    public string ClaimedByHostname { get; set; } = "";
    public string LastError { get; set; } = "";
    /// <summary>Khoảng dòng Hub đặt cho client chạy (ghi đè cấu hình client lượt này). 0 = dùng cấu hình client.</summary>
    public int StartRow { get; set; }
    public int EndRow { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string CoordId => $"{BigsellerId}__{ShopId}__{Op}";
}

public sealed record SetRoleRequest(string MachineId, string Role);
public sealed record CreateAssignmentRequest(
    string BigsellerId, string ShopId, string Sheet, string Op, string? TargetMachineId, bool Pinned,
    int StartRow = 0, int EndRow = 0);
public sealed record ClaimAssignmentsRequest(string MachineId, string Role, int Max);
public sealed record AssignmentStatusRequest(string Id, string MachineId, string Status, string? Error);
public sealed record CancelAssignmentRequest(string Id);

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

public sealed record MachineHeartbeatRequest(string MachineId, string Hostname, string? AppVersion);
/// <summary>Client báo Hub "tôi rời đi" (bấm Ngắt kết nối) → Hub xoá khỏi danh sách máy ngay.</summary>
public sealed record MachineLeaveRequest(string MachineId);

public sealed record FilePutResponse(bool Ok, int Version, string? Conflict);
