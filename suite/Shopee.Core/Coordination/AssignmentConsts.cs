namespace Shopee.Core.Coordination;

// ── Hằng chuỗi của hợp đồng GIAO VIỆC — dùng chung client (suite) + hub ────────
// File này được LINK vào server\Shopee.Hub.Web\Shopee.Hub.Web.csproj (khuôn của HubDtos.cs) để hai phía
// KHÔNG bao giờ lệch nhau. MỌI giá trị ở đây vừa là hợp đồng WIRE (client cũ vẫn đang gửi/nhận) vừa là DỮ
// LIỆU ĐANG SỐNG trong hub.db production (cột assignments.op/status, ledger.status, leases.status) và trong
// file tiến độ %AppData%\ShopeeSuite\shared\*.json của từng máy: đổi MỘT BYTE = dòng cũ không còn khớp,
// hỏng âm thầm (việc kẹt 'queued', ledger tưởng chưa xong, resume bỏ sót). CHỈ THÊM, KHÔNG SỬA giá trị.

/// <summary>Op của một việc Hub giao (<see cref="Assignment.Op"/>) — cũng là phần đuôi của khoá lease/ledger
/// <c>{bigsellerId}__{shopId}__{op}</c> (xem <see cref="Assignment.CoordId"/>).</summary>
public static class AssignmentOps
{
    public const string Scrape = "scrape";
    public const string Import = "import";
    public const string Update = "update";
    public const string Rewrite = "rewrite";
    /// <summary>Search KHÔNG ghi ledger và KHÔNG đụng cookie BigSeller (không tính "bận theo acc") — nhiều
    /// nhánh phải tách riêng op này, xem <c>HubDatabase.OpFamily</c> + <c>AssignmentWorker.ReconcileInflightAsync</c>.</summary>
    public const string Search = "search";

    /// <summary>KHÔNG phải op assignment: nhãn dòng PHIÊN ĐƠN HÀNG trên trang Giao việc (dòng đó không có
    /// assignment, huỷ bằng lệnh tới máy). Xem <c>DispatchViewLogic.OrdersOperation</c>.</summary>
    public const string Orders = "orders";
}

/// <summary>Trạng thái một việc Hub giao (<see cref="Assignment.Status"/>). Vòng đời: hub tạo
/// <see cref="Queued"/> → máy claim thành <see cref="Running"/> → client báo <see cref="Done"/>/<see cref="Failed"/>;
/// operator huỷ = <see cref="Canceled"/>.</summary>
public static class AssignmentStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    /// <summary>KHÔNG lưu vào DB: giá trị client GỬI ở POST /assignments/status để TRẢ việc về hàng chờ
    /// (running → queued + bỏ claim) khi lỗi chỉ là TẠM THỜI. Xem <c>HubDatabase.UpdateAssignmentStatus</c>.</summary>
    public const string Requeue = "requeue";
}

/// <summary>Trạng thái sổ hoàn thành (<see cref="WorkLedgerRecord.Status"/>). CŨNG là value space của tiến độ
/// LOCAL trên client (<c>ScrapeProgress.Status</c> / <c>OpProgress.Status</c>) vì client đẩy thẳng chuỗi đó
/// sang <see cref="ICoordinationHub.PublishCompletion"/> — hai bên lệch nhau là ledger sai.</summary>
public static class LedgerStatus
{
    /// <summary>Chưa chạy. Hub nhận giá trị này ở /ledger/set = XOÁ bản ghi + tiến độ dòng (reset).</summary>
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Completed = "completed";
}

/// <summary>Trạng thái khoá một đơn vị việc (<see cref="LeaseRecord.Status"/>). <see cref="Running"/> và
/// <see cref="Finishing"/> đều tính là "còn giữ" khi xét acc bận / chủ sở hữu acc.</summary>
public static class LeaseStatus
{
    public const string Running = "running";
    public const string Finishing = "finishing";
    public const string Released = "released";
}
