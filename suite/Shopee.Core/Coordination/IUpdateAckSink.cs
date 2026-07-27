namespace Shopee.Core.Coordination;

/// <summary>
/// Đích gửi ACK tiến trình/kết quả tự-update app về Hub. Tách interface vì lệnh update đi tới TỪNG SUẤT làm
/// việc (xem <see cref="MachineSlots"/>) và ack phải mang <c>machine_id</c> của ĐÚNG suất đã nhận lệnh:
/// suất workspace ack qua <see cref="HttpCoordinationHub"/>, suất đơn hàng ack qua
/// <see cref="OrdersSlotHeartbeat"/>. Nhờ vậy bộ xử lý lệnh update phía app (RemoteUpdateService) dùng CHUNG
/// cho cả hai suất, không phải chép logic.
/// </summary>
public interface IUpdateAckSink
{
    /// <summary>Ack 1 trạng thái update. true = gửi được; false = offline/lỗi mạng (Hub GIỮ cờ lệnh → nhịp sau
    /// lệnh lại về, handler dedup xử tiếp).</summary>
    Task<bool> TryAckUpdateAsync(string status);
}
