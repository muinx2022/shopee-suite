using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Services;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Cảnh báo "máy client rơi offline khi đang giữ việc" (H1.3) — lõi thuần <see cref="MachineOfflineWatch"/>.
/// Bất biến chính: MỖI episode offline ra ĐÚNG 1 tin, máy nối lại ra ĐÚNG 1 tin nữa, quét thêm bao nhiêu lượt
/// cũng không sinh tin lặp.
/// </summary>
public sealed class MachineOfflineWatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 10, 0, 0, TimeSpan.FromHours(7));
    private static readonly TimeSpan Nguong = TimeSpan.FromMinutes(10);

    private static MachinePresence May(string id, string host, DateTimeOffset lastSeen) =>
        new() { MachineId = id, Hostname = host, LastSeen = lastSeen };

    private static Assignment Viec(string status, string claimedBy = "", string? target = null,
        string lastError = "", DateTimeOffset updatedAt = default) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"), Status = status, ClaimedByMachineId = claimedBy,
            TargetMachineId = target, LastError = lastError, UpdatedAt = updatedAt,
        };

    // ===== 1. CA CHÍNH: mất nhịp → 1 tin; quét lại → im; nối lại → 1 tin "trở lại"; quét lại → im =====
    [Fact]
    public void MayGiuViecMatNhip_DungMotTin_NoiLaiThemDungMotTin()
    {
        var daBao = new Dictionary<string, int>(StringComparer.Ordinal);
        var chet = new FleetSnapshot
        {
            Machines = [May("m1", "PC-01", Now.AddMinutes(-30))],
            Assignments = [Viec(AssignmentStatus.Running, claimedBy: "m1")],
        };

        var lan1 = MachineOfflineWatch.Quet(chet, Now, Nguong, daBao);
        var tin = Assert.Single(lan1);
        Assert.Equal(LoaiTinMay.MatNhip, tin.Loai);
        Assert.Equal("PC-01", tin.Hostname);
        Assert.Equal(1, tin.SoViec);
        Assert.Equal(30, (int)tin.MatNhip.TotalMinutes);

        // Quét thêm 3 lượt nữa trong CÙNG episode → không tin nào (đây là chốt chống spam).
        Assert.Empty(MachineOfflineWatch.Quet(chet, Now.AddMinutes(1), Nguong, daBao));
        Assert.Empty(MachineOfflineWatch.Quet(chet, Now.AddMinutes(2), Nguong, daBao));
        Assert.Empty(MachineOfflineWatch.Quet(chet, Now.AddMinutes(3), Nguong, daBao));

        // Máy nối lại (nhịp tươi) → đúng 1 tin "trở lại", nhắc lại số việc của tin đi trước.
        var song = new FleetSnapshot
        {
            Machines = [May("m1", "PC-01", Now.AddSeconds(-5))],
            Assignments = [Viec(AssignmentStatus.Running, claimedBy: "m1")],
        };
        var tinVe = Assert.Single(MachineOfflineWatch.Quet(song, Now, Nguong, daBao));
        Assert.Equal(LoaiTinMay.TroLai, tinVe.Loai);
        Assert.Equal(1, tinVe.SoViec);

        // Và các lượt sau đó im hẳn.
        Assert.Empty(MachineOfflineWatch.Quet(song, Now, Nguong, daBao));
        Assert.Empty(daBao);
    }

    // ===== 2. Máy tắt mà KHÔNG ôm việc gì = chuyện thường (hết ca) → không báo =====
    [Fact]
    public void MayOfflineKhongGiuViec_KhongBaoGi()
    {
        var daBao = new Dictionary<string, int>(StringComparer.Ordinal);
        var snap = new FleetSnapshot { Machines = [May("m1", "PC-01", Now.AddHours(-3))] };

        Assert.Empty(MachineOfflineWatch.Quet(snap, Now, Nguong, daBao));
        Assert.Empty(daBao);
    }

    // ===== 3. Chưa quá ngưỡng thì chưa báo (biên đóng/mở) =====
    [Fact]
    public void ChuaQuaNguong_ChuaBao_QuaNguongMoiBao()
    {
        var daBao = new Dictionary<string, int>(StringComparer.Ordinal);
        FleetSnapshot Snap(int phutMatNhip) => new()
        {
            Machines = [May("m1", "PC-01", Now.AddMinutes(-phutMatNhip))],
            Assignments = [Viec(AssignmentStatus.Queued, target: "m1")],
        };

        Assert.Empty(MachineOfflineWatch.Quet(Snap(10), Now, Nguong, daBao));   // đúng ngưỡng → CHƯA báo
        Assert.Single(MachineOfflineWatch.Quet(Snap(11), Now, Nguong, daBao));  // quá ngưỡng → báo
    }

    // ===== 4. "Đang giữ việc" phải tính CẢ việc vừa bị sweep đánh rụng vì máy hết nhịp =====
    // Đây là lý do tồn tại của nhánh đó: hub tự đánh 'failed' việc 'running' của máy mất nhịp sau 5', mà ngưỡng
    // cảnh báo mặc định là 10' → chỉ đếm queued/running thì ca "máy chết giữa lúc đang chạy" ra 0 việc.
    [Fact]
    public void ViecVuaRungViMatNhip_VanTinhLaDangGiuViec()
    {
        var daBao = new Dictionary<string, int>(StringComparer.Ordinal);
        var snap = new FleetSnapshot
        {
            Machines = [May("m1", "PC-01", Now.AddMinutes(-12))],
            Interrupted =
            [
                Viec(AssignmentStatus.Failed, claimedBy: "m1",
                    lastError: HubDatabase.StaleSweepError, updatedAt: Now.AddMinutes(-7)),
            ],
        };

        var tin = Assert.Single(MachineOfflineWatch.Quet(snap, Now, Nguong, daBao));
        Assert.Equal(1, tin.SoViec);
    }

    [Fact]
    public void ViecRungQuaCu_KhongTinh_VaViecHongViLyDoKhac_CungKhongTinh()
    {
        var quaCu = new FleetSnapshot
        {
            Machines = [May("m1", "PC-01", Now.AddDays(-3))],
            Interrupted =
            [
                Viec(AssignmentStatus.Failed, claimedBy: "m1", lastError: HubDatabase.StaleSweepError,
                    updatedAt: Now - MachineOfflineWatch.TuoiViecRungToiDa.Add(TimeSpan.FromMinutes(1))),
            ],
        };
        var loiKhac = new FleetSnapshot
        {
            Machines = [May("m1", "PC-01", Now.AddMinutes(-12))],
            Interrupted =
            [
                Viec(AssignmentStatus.Failed, claimedBy: "m1", lastError: "shop không có dòng nào",
                    updatedAt: Now.AddMinutes(-7)),
            ],
        };

        Assert.Empty(MachineOfflineWatch.Quet(quaCu, Now, Nguong, new Dictionary<string, int>(StringComparer.Ordinal)));
        Assert.Empty(MachineOfflineWatch.Quet(loiKhac, Now, Nguong, new Dictionary<string, int>(StringComparer.Ordinal)));
    }

    // ===== 5. Máy bị XOÁ khỏi fleet giữa episode → dọn tập nhưng KHÔNG bắn "đã trở lại" =====
    [Fact]
    public void MayBienMatKhoiBang_KhongBanTinTroLai()
    {
        var daBao = new Dictionary<string, int>(StringComparer.Ordinal);
        var chet = new FleetSnapshot
        {
            Machines = [May("m1", "PC-01", Now.AddMinutes(-30))],
            Leases = [new LeaseRecord { MachineId = "m1" }],
        };
        Assert.Single(MachineOfflineWatch.Quet(chet, Now, Nguong, daBao));

        // Admin bấm "Xoá & chặn" → máy không còn trong bảng.
        Assert.Empty(MachineOfflineWatch.Quet(new FleetSnapshot(), Now, Nguong, daBao));
        Assert.Empty(daBao);
    }

    // ===== 6. Đếm việc: gộp việc đang mở + lease + việc vừa rụng, KHÔNG đếm việc của máy khác =====
    [Fact]
    public void SoViecDangGiu_GopBaNguon_ChiCuaDungMayDo()
    {
        var snap = new FleetSnapshot
        {
            Machines = [May("m1", "PC-01", Now), May("m2", "PC-02", Now)],
            Assignments =
            [
                Viec(AssignmentStatus.Running, claimedBy: "m1"),
                Viec(AssignmentStatus.Queued, target: "m1"),
                Viec(AssignmentStatus.Done, claimedBy: "m1"),      // đã xong → không tính
                Viec(AssignmentStatus.Running, claimedBy: "m2"),   // của máy khác → không tính
            ],
            Leases = [new LeaseRecord { MachineId = "m1" }, new LeaseRecord { MachineId = "m2" }],
            Interrupted =
            [
                Viec(AssignmentStatus.Failed, claimedBy: "m1", lastError: HubDatabase.StaleSweepError,
                    updatedAt: Now.AddMinutes(-1)),
            ],
        };

        Assert.Equal(4, MachineOfflineWatch.SoViecDangGiu(snap, "m1", Now));   // 2 việc mở + 1 lease + 1 việc rụng
        Assert.Equal(2, MachineOfflineWatch.SoViecDangGiu(snap, "m2", Now));
        Assert.Equal(0, MachineOfflineWatch.SoViecDangGiu(snap, "", Now));
    }

    // ===== 7. Ngưỡng người dùng nhập: kẹp trong [min,max], rác → mặc định =====
    [Theory]
    [InlineData("15", 15)]
    [InlineData("0", MachineOfflineWatchService.NguongMinPhut)]
    [InlineData("-5", MachineOfflineWatchService.NguongMinPhut)]
    [InlineData("99999", MachineOfflineWatchService.NguongMaxPhut)]
    [InlineData("", MachineOfflineWatchService.NguongMacDinhPhut)]
    [InlineData(null, MachineOfflineWatchService.NguongMacDinhPhut)]
    [InlineData("mười", MachineOfflineWatchService.NguongMacDinhPhut)]
    public void KepNguong_KepVaLuiVeMacDinh(string? raw, int mong)
        => Assert.Equal(mong, MachineOfflineWatchService.KepNguong(raw));

    // ===== 8. Nội dung 2 tin: đủ máy · thời lượng · số việc, và không in "null" khi thiếu tên =====
    [Fact]
    public void TinNhan_DuThongTinNguoiTrucCan()
    {
        var luc = new DateTime(2026, 8, 6, 10, 30, 0);
        var mat = OrderNotifyService.TaoTinNhanMayMatNhip("PC-01", 3, TimeSpan.FromMinutes(95), luc);
        Assert.Contains("PC-01", mat, StringComparison.Ordinal);
        Assert.Contains("1 giờ 35 phút", mat, StringComparison.Ordinal);
        Assert.Contains("3 việc", mat, StringComparison.Ordinal);
        Assert.Contains("06/08/2026 10:30", mat, StringComparison.Ordinal);

        var ve = OrderNotifyService.TaoTinNhanMayTroLai("PC-01", 3, luc);
        Assert.Contains("đã nối lại", ve, StringComparison.Ordinal);
        Assert.Contains("3 việc", ve, StringComparison.Ordinal);

        // Thiếu tên máy / thời lượng bé → vẫn ra tin đọc được, không có chữ "null".
        var thieu = OrderNotifyService.TaoTinNhanMayMatNhip("", 0, TimeSpan.FromSeconds(20), luc);
        Assert.Contains("?", thieu, StringComparison.Ordinal);
        Assert.Contains("<1 phút", thieu, StringComparison.Ordinal);
        Assert.DoesNotContain("null", thieu, StringComparison.OrdinalIgnoreCase);
    }
}
