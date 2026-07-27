namespace Shopee.Core.Coordination;

/// <summary>
/// SUẤT LÀM VIỆC (slot) của một PC dưới mắt Hub. Mỗi PC có HAI loại suất — <see cref="Workspace"/> (việc
/// BigSeller) và <see cref="Orders"/> (việc Shopee/đơn hàng) — mỗi suất là MỘT client trong bảng máy của Hub.
/// Chế độ app quyết định suất nào chạy: <c>Workspace</c> chiếm suất workspace, <c>Shopee</c> chiếm suất đơn
/// hàng, <c>Full</c> chiếm CẢ HAI (2 dòng máy trên Hub nhưng vẫn 1 process / 1 CPU / 1 quỹ Brave — Hub gộp
/// theo <c>host_id</c>).
/// <para><b>BẤT BIẾN:</b> suất Workspace GIỮ NGUYÊN id máy (KHÔNG hậu tố). Đổi nó là đứt mọi dữ liệu đang khoá
/// theo <c>machine_id</c>: <c>ledger.last_machine_id</c>, <c>account_home.machine_id</c>,
/// <c>assignments.claimed_by</c>/<c>target_machine_id</c>, <c>leases.machine_id</c>,
/// <c>search_products.machine_id</c>, <c>machine_roles</c>. Chỉ suất MỚI (đơn hàng) mang hậu tố.</para>
/// Suất là thứ TÍNH RA lúc chạy từ <c>MachineIdentity.MachineId</c> — KHÔNG lưu vào machine.json.
/// </summary>
public static class MachineSlots
{
    /// <summary>Loại suất "việc BigSeller" (chế độ Workspace/Full). Ghi vào <c>machines.kind</c>.</summary>
    public const string Workspace = "workspace";

    /// <summary>Loại suất "việc đơn hàng Shopee" (chế độ Shopee/Full). Ghi vào <c>machines.kind</c>.</summary>
    public const string Orders = "orders";

    /// <summary>Hậu tố id của suất đơn hàng. Suất Workspace GIỮ NGUYÊN id máy (không hậu tố) — xem BẤT BIẾN
    /// ở chú thích lớp.</summary>
    public const string OrdersSuffix = ":orders";

    /// <summary>Id suất gửi lên Hub: đơn hàng = <c>&lt;id-máy&gt;:orders</c>; còn lại = ĐÚNG id máy.</summary>
    public static string SlotId(string hostId, string kind) =>
        kind == Orders ? hostId + OrdersSuffix : hostId;

    /// <summary>Id PC thật của một suất (đảo ngược <see cref="SlotId"/>). Cắt theo ĐUÔI nên id máy tình cờ
    /// chứa ":orders" ở giữa không bị cắt nhầm.</summary>
    public static string HostOf(string slotId) =>
        IsOrdersSlot(slotId) ? slotId[..^OrdersSuffix.Length] : slotId;

    /// <summary>Id này là suất ĐƠN HÀNG? (chỉ xét đuôi — dùng được cả ở nơi không có bảng <c>machines</c>).</summary>
    public static bool IsOrdersSlot(string? slotId) =>
        slotId is not null && slotId.EndsWith(OrdersSuffix, StringComparison.Ordinal);

    // ── Quy ước SUY DIỄN cho client CŨ (tương thích ngược) ────────────────────────
    // Hub deploy TRƯỚC, client release SAU → trong khoảng giữa mọi client đang chạy là bản CŨ, heartbeat KHÔNG
    // mang Mode/Kind/HostId. Hub tự suy 3 giá trị dưới đây để dòng máy cũ vẫn đúng nghĩa (và KHÔNG đổi machine_id).

    /// <summary>Loại suất: rỗng/không hợp lệ → suy từ ĐUÔI id (client cũ luôn gửi id không hậu tố ⇒ workspace).</summary>
    public static string NormalizeKind(string? kind, string machineId) =>
        kind == Orders || (string.IsNullOrEmpty(kind) && IsOrdersSlot(machineId)) ? Orders : Workspace;

    /// <summary>Id PC thật: rỗng → suy từ chính id suất (client cũ ⇒ host = machine_id).</summary>
    public static string NormalizeHostId(string? hostId, string machineId) =>
        string.IsNullOrWhiteSpace(hostId) ? HostOf(machineId) : hostId;

    /// <summary>Chế độ app: rỗng → "Workspace" (client cũ chỉ heartbeat ở chế độ Workspace/Full; coi là
    /// Workspace, bản mới sẽ tự khai đúng ngay nhịp đầu).</summary>
    public static string NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? "Workspace" : mode.Trim();
}
