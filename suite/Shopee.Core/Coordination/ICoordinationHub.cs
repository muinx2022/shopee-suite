using Shopee.Core.Scrape;

namespace Shopee.Core.Coordination;

/// <summary>Thao tác có thể khoá/điều phối trên một (tài khoản BigSeller + shop). Tên viết-thường của từng
/// hằng PHẢI khớp <see cref="AssignmentOps"/> — <c>HttpCoordinationHub.OpStr</c> gửi lên Hub bằng
/// <c>ToLowerInvariant()</c>, lệch một chữ là khoá lease/ledger không khớp việc Hub giao.</summary>
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
    public string Status { get; set; } = LeaseStatus.Running;   // xem LeaseStatus
}

/// <summary>Sổ hoàn thành cho một đơn vị việc, có đóng dấu máy thực hiện gần nhất.</summary>
public sealed class WorkLedgerRecord
{
    public string Key { get; set; } = "";
    public string BigsellerId { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string Op { get; set; } = "";

    /// <summary>Khoảng dòng (trong sheet) đã xong. Scrape: dòng đã cào. Import/Update/Rewrite: dòng SP đã
    /// xử lý xong — để Thống kê trên Hub xem "shop này đã import/update được những dòng nào". Fold-về-tiến-độ
    /// scrape CHỈ dùng record op=scrape (xem HttpCoordinationHub.SyncIntoProgressAsync/FoldScrapeLedgerAsync).</summary>
    public List<RowRange> Completed { get; set; } = [];

    /// <summary>Các dòng NẰM TRONG <see cref="Completed"/> mà THỰC TẾ không cào được (kẹt 3 lần / lỗi giữa
    /// khối) — bản xuyên máy của <c>ScrapeProgress.SkippedRows</c>. Không có sổ này thì máy KHÁC fold ledger về
    /// chỉ thấy "✔ Hoàn thành toàn bộ" mà không biết đang thiếu SP nào (báo thành công khi thiếu).
    /// <para>TƯƠNG THÍCH NGƯỢC: client cũ (≤ v1.9.2) KHÔNG gửi field này → JSON thiếu → về danh sách RỖNG, và
    /// server CHỈ UNION (không thay thế) nên sổ đã có trên Hub không bị client cũ xoá trắng. Hub gộp như
    /// <see cref="Completed"/>; <c>/ledger/set</c> idle xoá bản ghi nên sổ chết theo — đúng ý reset.</para></summary>
    public List<int> Skipped { get; set; } = [];

    public int LastRowReached { get; set; }
    public string Status { get; set; } = LedgerStatus.Idle;   // xem LedgerStatus
    public string LastMachineId { get; set; } = "";
    public string LastHostname { get; set; } = "";

    /// <summary>Tập machine_id ĐÃ tham gia việc này. Hub TỰ tích luỹ mỗi lần publish (union LastMachineId) →
    /// Thống kê xem "các máy nào đã cùng scrape/import/update shop này". Client KHÔNG set (Hub tự gom).</summary>
    public List<string> MachineIds { get; set; } = [];

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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

    /// <summary>Báo 1 dòng BỎ QUA (không cào được) lên sổ Hub: vẫn TÍNH VÀO vùng phủ như
    /// <see cref="PublishProgress"/> (không publish thì Hub giao lại dòng hỏng vòng vô tận) NHƯNG kèm tên dòng
    /// vào <see cref="WorkLedgerRecord.Skipped"/> để máy khác biết chỗ này đang thiếu SP.</summary>
    void PublishSkipped(CoordKey key, int row);

    void PublishCompletion(CoordKey key, string status, int lastRow);
    IReadOnlyList<LeaseRecord> ActiveLeases();
}
