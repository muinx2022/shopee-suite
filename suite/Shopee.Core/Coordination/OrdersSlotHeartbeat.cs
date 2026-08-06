namespace Shopee.Core.Coordination;

/// <summary>
/// Nhịp sống của SUẤT ĐƠN HÀNG (xem <see cref="MachineSlots"/>): đăng ký máy trên Hub + nhận lệnh update app,
/// cho chế độ <b>Shopee</b> (chỉ đơn hàng) và nhánh đơn hàng của chế độ <b>Full</b>.
/// <para>CỐ Ý chỉ có heartbeat: KHÔNG poll /fleet, KHÔNG claim assignment, KHÔNG giành lease BigSeller — suất
/// này không làm việc BigSeller (Hub cũng chặn cứng ở <c>ClaimNext</c>/<c>CreateAssignment</c>). Việc đẩy đơn/
/// phiếu + khoá tài khoản đơn hàng vẫn đi đường cũ qua <see cref="CoordinationRuntime.Client"/>.</para>
/// <para>Dùng CHUNG <see cref="HubClient"/> với suất workspace (chế độ Full chạy 2 suất trong 1 process — KHÔNG
/// dựng thêm <see cref="HttpCoordinationHub"/> hay HttpClient thứ hai). Hệ quả: header <c>X-Machine-Id</c> của
/// request vẫn là id PC (id suất nằm trong THÂN request) — chặn máy (revoke) phía Hub xét theo header nên chặn
/// một suất là chặn cả process, đúng ý "1 process 1 máy vật lý".</para>
/// </summary>
public sealed class OrdersSlotHeartbeat : IUpdateAckSink, IDisposable
{
    private readonly HubClient _client;
    private readonly string _hostId;
    private readonly string _slotId;
    private readonly Timer _timer;

    /// <summary>Tên hiển thị máy này, đọc LIVE (y <see cref="HttpCoordinationHub"/>) → đổi tên trong Settings có
    /// hiệu lực ngay lượt gửi kế tiếp.</summary>
    private static string Host => MachineIdentity.Shared.DisplayName;

    /// <summary>Id suất này gửi lên Hub (<c>&lt;id-máy&gt;:orders</c>) — cũng là khoá dòng máy trên Hub.</summary>
    public string MachineId => _slotId;

    /// <summary>Hook LỆNH Hub giao cho suất đơn hàng (▶ Chạy / ✖ Dừng một tài khoản) — suite gán lúc khởi động
    /// (về <c>OrdersModuleHost</c>); bắn từ <see cref="BeatAsync"/> khi phản hồi heartbeat mang lệnh. Handler
    /// TỰ dedup theo <see cref="OrdersCommandDto.Id"/>, TỰ ack và KHÔNG được block nhịp — y khuôn
    /// <see cref="HttpCoordinationHub.UpdateRequested"/> (static vì nhịp chạy NỀN, không có context để bám).</summary>
    public static Action<IReadOnlyList<OrdersCommandDto>>? CommandsReceived { get; set; }

    /// <summary>
    /// Nguồn số "hàng còn chờ đẩy" của module Đơn hàng cho mỗi nhịp (xem <see cref="MachinePresence.OutboxPending"/>).
    /// Khuôn giống <c>BraveFleet.MaxConcurrentWindows</c> của suất workspace, chỉ khác là phải qua HOOK: module Đơn
    /// hàng nằm ngoài <c>Shopee.Core</c> nên ở đây không gọi thẳng được — shell suite rót vào lúc khởi động
    /// (<c>OrdersModuleHost</c>).
    /// <para><c>null</c> (chưa ai rót — chế độ không có module Đơn hàng, hoặc module init hỏng) ⇒ nhịp KHÔNG mang
    /// field này ⇒ Hub lưu NULL ⇒ bảng /machines hiện "—". Hàm ném lỗi cũng coi như không báo: một con số phụ
    /// KHÔNG được phép làm chết nhịp sống.</para>
    /// </summary>
    public static Func<int>? OutboxPendingProvider { get; set; }

    public OrdersSlotHeartbeat(HubClient client, string hostId)
    {
        _client = client;
        _hostId = hostId;
        _slotId = MachineSlots.SlotId(hostId, MachineSlots.Orders);
        _timer = new Timer(_ => _ = BeatAsync(), null, TimeSpan.Zero, HttpCoordinationHub.PollEvery);
    }

    public void Dispose() => _timer.Dispose();

    private async Task BeatAsync()
    {
        try
        {
            // MaxBrave = 0 ("chưa báo"): quỹ Brave của máy do suất WORKSPACE báo, chia quỹ cho máy Full là việc
            // của đợt sau — suất này không tự khai trần để Hub khỏi cộng nhầm thành 2 máy 2 quỹ.
            var resp = await _client.MachineHeartbeatAsync(new MachineHeartbeatRequest(
                _slotId, Host, Infrastructure.AppInfo.Version, 0,
                Infrastructure.AppModeStore.Shared.Current.ToString(), MachineSlots.Orders, _hostId,
                TonChoDay()));
            // Hub đẩy lệnh update xuống suất này? Bắn CHUNG hook của đường workspace (handler tự dedup + chạy
            // nền) → dùng lại y bộ xử lý update sẵn có, ack quay về đúng machine_id của suất đơn hàng.
            var reqAt = resp?.UpdateRequestedAt;
            if (!string.IsNullOrEmpty(reqAt))
                try { HttpCoordinationHub.UpdateRequested?.Invoke(this, reqAt); } catch { }
            // Lệnh chạy/dừng tài khoản Đơn hàng: Hub đánh dấu 'sent' ngay khi trả về nên nhịp sau KHÔNG nhận lại —
            // nhưng mạng chập chờn vẫn có thể làm lượt này lặp, handler phải tự dedup theo Id (xem CommandsReceived).
            var cmds = resp?.OrdersCommands;
            if (cmds is { Count: > 0 })
                try { CommandsReceived?.Invoke(cmds); } catch { }
        }
        catch { /* offline: bỏ nhịp này, lượt sau bù */ }
    }

    /// <summary>Số hàng chờ đẩy để gửi kèm nhịp: chưa ai rót hook / hook ném → <c>null</c> ("máy không báo").
    /// Số âm (không nên có) kẹp về 0 để Hub khỏi lưu giá trị vô nghĩa.</summary>
    internal static int? TonChoDay()
    {
        if (OutboxPendingProvider is not { } lay) return null;
        try { return Math.Max(0, lay()); }
        catch { return null; }
    }

    /// <summary>Ack tiến trình/kết quả tự-update app cho SUẤT NÀY. true = gửi được; false = offline/lỗi mạng
    /// (Hub giữ cờ → nhịp sau lệnh lại về).</summary>
    public async Task<bool> TryAckUpdateAsync(string status)
    {
        try { await _client.AckUpdateAsync(new UpdateAckRequest(_slotId, status)); return true; }
        catch { return false; }
    }
}
