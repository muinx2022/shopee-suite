using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>Loại tin của cảnh báo máy: máy vừa rơi mất nhịp, hoặc máy đã nối lại.</summary>
internal enum LoaiTinMay
{
    MatNhip,
    TroLai,
}

/// <summary>Một tin cần bắn về webhook. <see cref="SoViec"/> = số việc máy đang giữ LÚC PHÁT HIỆN (tin "trở lại"
/// nhắc lại đúng con số của tin đi trước để người đọc nối được hai tin).</summary>
internal sealed record TinMayOffline(
    LoaiTinMay Loai, string MachineId, string Hostname, int SoViec, TimeSpan MatNhip);

/// <summary>
/// LÕI THUẦN của cảnh báo "máy rơi offline khi đang giữ việc" (H1.3) — tách khỏi <see cref="MachineOfflineWatchService"/>
/// để test được không cần dựng host/webhook.
/// <para>Chống spam bằng <c>daBao</c>: mỗi EPISODE offline chỉ ra đúng 1 tin; máy nối lại ra đúng 1 tin "trở lại"
/// rồi xoá khỏi tập. Tập này nằm trong bộ nhớ service → hub restart là mất, và lượt quét đầu sau restart CÓ THỂ
/// báo lại một lần cho máy vẫn đang offline-giữ-việc. Chấp nhận có chủ ý (đổi lại: không phải thêm bảng DB).</para>
/// <para>MỌI so sánh thời gian ở đây đều là ĐỒNG HỒ HUB với mốc do HUB ghi (<c>machines.last_seen</c>,
/// <c>assignments.updated_at</c>) — không có phép so mốc chéo máy nào.</para>
/// </summary>
internal static class MachineOfflineWatch
{
    /// <summary>Trần tuổi của "việc vừa rụng vì máy mất nhịp" còn được tính là máy ĐANG GIỮ việc. Không có trần
    /// này thì một máy tắt hẳn từ mấy hôm trước, còn sót việc gián đoạn trong cửa sổ 7 ngày của
    /// <c>ListInterrupted</c>, sẽ bị báo lại mỗi lần hub khởi động.</summary>
    public static readonly TimeSpan TuoiViecRungToiDa = TimeSpan.FromHours(2);

    /// <summary>
    /// Số việc máy <paramref name="machineId"/> đang GIỮ, gồm 3 nguồn:
    /// <list type="number">
    /// <item>việc hub-giao đang MỞ (queued/running) máy đã claim hoặc được ghim vào — đúng con số cột "Việc đang
    /// giữ" của trang /machines;</item>
    /// <item>khoá việc (lease) còn trong snapshot;</item>
    /// <item>việc VỪA bị <c>SweepStaleLocked</c> đánh rụng vì máy hết nhịp (<c>last_error</c> =
    /// <see cref="HubDatabase.StaleSweepError"/>), trong vòng <see cref="TuoiViecRungToiDa"/>.</item>
    /// </list>
    /// Mục (3) là BẮT BUỘC, không phải phòng xa: hub tự đánh 'failed' việc 'running' của máy mất nhịp sau
    /// <c>StaleRunning</c> = 5 phút, và <c>ActiveLeases</c> cũng lọc bỏ lease cũ hơn 5 phút. Với ngưỡng cảnh báo
    /// mặc định 10 phút, nếu chỉ đếm (1)+(2) thì ĐÚNG ca cần báo nhất — máy chết giữa lúc đang chạy việc — lại ra
    /// 0 việc và không bao giờ có tin nào.
    /// </summary>
    public static int SoViecDangGiu(FleetSnapshot f, string machineId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(machineId)) return 0;

        var moOrChayThat = f.Assignments.Count(a =>
            a.Status is AssignmentStatus.Queued or AssignmentStatus.Running
            && (a.ClaimedByMachineId == machineId || a.TargetMachineId == machineId));

        var khoa = f.Leases.Count(l => l.MachineId == machineId);

        var vuaRung = f.Interrupted.Count(a =>
            (a.ClaimedByMachineId == machineId || a.TargetMachineId == machineId)
            && string.Equals(a.LastError, HubDatabase.StaleSweepError, StringComparison.Ordinal)
            && (now - a.UpdatedAt) <= TuoiViecRungToiDa);

        return moOrChayThat + khoa + vuaRung;
    }

    /// <summary>
    /// Quét một lượt. <paramref name="daBao"/> = tập máy ĐANG trong episode đã báo (machineId → số việc lúc báo);
    /// hàm này TỰ CẬP NHẬT tập đó (thêm khi báo mất nhịp, gỡ khi báo trở lại / khi máy biến mất khỏi bảng).
    /// Trả danh sách tin cần gửi — rỗng là bình thường (không có gì đổi).
    /// </summary>
    /// <param name="nguong">Mất nhịp quá ngưỡng này mới coi là offline.</param>
    public static List<TinMayOffline> Quet(
        FleetSnapshot f, DateTimeOffset now, TimeSpan nguong, Dictionary<string, int> daBao)
    {
        var tin = new List<TinMayOffline>();

        foreach (var m in f.Machines)
        {
            var matNhip = now - m.LastSeen;
            var host = string.IsNullOrWhiteSpace(m.Hostname) ? m.MachineId : m.Hostname;

            if (matNhip > nguong)
            {
                if (daBao.ContainsKey(m.MachineId)) continue;   // episode này đã báo rồi → im
                var soViec = SoViecDangGiu(f, m.MachineId, now);
                if (soViec <= 0) continue;                      // máy tắt mà không giữ việc gì → không phải sự cố
                daBao[m.MachineId] = soViec;
                tin.Add(new TinMayOffline(LoaiTinMay.MatNhip, m.MachineId, host, soViec, matNhip));
            }
            else if (daBao.Remove(m.MachineId, out var soViecCu))
            {
                tin.Add(new TinMayOffline(LoaiTinMay.TroLai, m.MachineId, host, soViecCu, matNhip));
            }
        }

        // Máy bị xoá khỏi fleet (admin bấm "Xoá & chặn" / client "Ngắt kết nối") khi đang trong episode: dọn tập
        // cho sạch nhưng KHÔNG bắn "đã trở lại" — nó không trở lại, nó biến mất.
        if (daBao.Count > 0)
        {
            var conSong = f.Machines.Select(m => m.MachineId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in daBao.Keys.Where(id => !conSong.Contains(id)).ToList()) daBao.Remove(id);
        }

        return tin;
    }
}
