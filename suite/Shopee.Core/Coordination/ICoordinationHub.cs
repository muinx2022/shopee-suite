using Shopee.Core.Scrape;

namespace Shopee.Core.Coordination;

/// <summary>Thao tác có thể khoá/điều phối trên một (tài khoản BigSeller + shop).</summary>
public enum CoordOp { Scrape, Import, Update, Rewrite }

/// <summary>
/// Khoá định danh một đơn vị việc: (BigSellerAccount.Id, BigSellerShop.Id, op). Mang kèm Sheet
/// (=ShopeeDataSheet) để nối với tiến độ scrape. <see cref="Id"/> dùng làm khoá file/bản ghi.
/// </summary>
public readonly record struct CoordKey(string BigsellerId, string ShopId, string Sheet, CoordOp Op)
{
    public string Id => $"{BigsellerId}__{ShopId}__{Op.ToString().ToLowerInvariant()}";
}

/// <summary>Kết quả xin khoá: cấp (Granted) / bị máy khác giữ (Blocked) / hub tắt (Off=cấp luôn).</summary>
public sealed record AcquireResult(bool Granted, string? BlockedByHostname, bool Disabled)
{
    public static AcquireResult Ok() => new(true, null, false);
    public static AcquireResult Blocked(string? hostname) => new(false, hostname, false);
    public static AcquireResult Off() => new(true, null, true);
}

/// <summary>Handle của một khoá đang giữ; Dispose = nhả khoá (kèm heartbeat nền lúc giữ).</summary>
public interface ILeaseHandle : IAsyncDisposable
{
    CoordKey Key { get; }
    bool Held { get; }
}

/// <summary>Kết quả gộp của một lần xin khoá: kết quả + handle (null nếu không cấp).</summary>
public sealed record LeaseAttempt(AcquireResult Result, ILeaseHandle? Handle)
{
    public bool Granted => Result.Granted;
}

/// <summary>Bản ghi khoá hiện hành trên Hub (cho bảng trạng thái + chống trùng).</summary>
public sealed class LeaseRecord
{
    public string Key { get; set; } = "";
    public string BigsellerId { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string Op { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
    public string Status { get; set; } = "running";   // running | finishing | released
}

/// <summary>Sổ hoàn thành cho một đơn vị việc, có đóng dấu máy thực hiện gần nhất.</summary>
public sealed class WorkLedgerRecord
{
    public string Key { get; set; } = "";
    public string BigsellerId { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string Op { get; set; } = "";

    /// <summary>Khoảng dòng đã xong (chỉ áp dụng scrape).</summary>
    public List<RowRange> Completed { get; set; } = [];
    public int LastRowReached { get; set; }
    public string Status { get; set; } = "idle";   // idle | running | stopped | completed
    public string LastMachineId { get; set; } = "";
    public string LastHostname { get; set; } = "";
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Kho khoá phân tán (backend swappable: HTTP-Hub hôm nay, cloud-DB sau này).</summary>
public interface ILeaseStore
{
    Task<LeaseAttempt> AcquireAsync(CoordKey key, bool force, CancellationToken ct);
    /// <summary>Nhả khoá mồ côi do CHÍNH máy này giữ (gọi lúc khởi động sau crash).</summary>
    Task ReclaimOwnAsync(CancellationToken ct);
    IReadOnlyList<LeaseRecord> SnapshotActive();
}

/// <summary>Kho sổ hoàn thành; có thể fold ngược vào tiến độ scrape để resume xuyên máy.</summary>
public interface ILedgerStore
{
    void Publish(WorkLedgerRecord record);
    WorkLedgerRecord? Find(CoordKey key);
    IReadOnlyList<WorkLedgerRecord> All();
    void SyncIntoProgress();
}

/// <summary>Dịch vụ đồng bộ cấu hình + file dùng chung với Hub.</summary>
public interface IHubSync
{
    bool Enabled { get; }
    void Start();
    void Stop();
    Task PullNowAsync(CancellationToken ct);
}

/// <summary>
/// Mặt tiền (facade) các điểm chạy (Scrape/Update/Workspace) gọi tới — gói khoá + sổ + trạng thái.
/// Khi hub tắt, mọi AcquireAsync trả Off()=cấp luôn để app chạy single-machine y như cũ.
/// </summary>
public interface ICoordinationHub
{
    bool Enabled { get; }
    event Action? Changed;
    Task<LeaseAttempt> AcquireAsync(CoordKey key, bool force, CancellationToken ct);
    void PublishProgress(CoordKey key, int from, int to);
    void PublishCompletion(CoordKey key, string status, int lastRow);
    IReadOnlyList<LeaseRecord> ActiveLeases();
}
