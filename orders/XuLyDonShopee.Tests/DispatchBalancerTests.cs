using Shopee.Hub.Web.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Luật chia việc của trang Giao việc (<c>/dispatch</c> trên hub web) — <see cref="DispatchBalancer.Balance"/>.
/// Điểm mấu chốt: <b>một tài khoản BigSeller chỉ chạy trên MỘT máy</b> nên đơn vị chia là NHÓM ACC, không phải
/// từng shop: cả nhóm về cùng máy và chỉ trừ ĐÚNG 1 khung Brave. Thứ tự ưu tiên máy đích:
/// máy đang giữ acc (khoá 1-acc-1-máy) → máy "nhà" (affinity) → máy online còn quỹ nhiều nhất.
/// Lõi thuần BCL nên test được thẳng, không cần dựng hub/DB.
/// </summary>
public class DispatchBalancerTests
{
    private static readonly Dictionary<string, string> Khong = new(StringComparer.Ordinal);

    private static DispatchTarget Shop(string acct, string shop) =>
        new(acct, shop, "sheet-" + shop, "Shop " + shop, "Kho " + acct);

    private static MachineBudget May(string id, string host, int free, bool online = true) =>
        new(id, host, online, free, Running: 0);

    private static Dictionary<string, string> Map(params (string Acct, string May)[] items) =>
        items.ToDictionary(x => x.Acct, x => x.May, StringComparer.Ordinal);

    // ===== 1. Hai shop CÙNG acc phải về CÙNG một máy (dù có 2 máy rảnh ngang nhau) =====
    [Fact]
    public void HaiShopCungAcc_LuonVeCungMotMay()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1"), Shop("a", "s2") },
            new[] { May("m1", "PC-01", 5), May("m2", "PC-02", 5) },
            Khong, Khong);

        var chon = Assert.Single(plan.ByMachine);
        Assert.Equal(2, chon.Value.Count);
        Assert.Empty(plan.Skipped);
    }

    // ===== 2. Acc đang BỊ GIỮ → luôn ra máy đang giữ, kể cả máy kia rảnh hơn nhiều =====
    [Fact]
    public void AccDangBiGiu_RaDungMayDangGiu_DuMayKhacRanhHon()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1") },
            new[] { May("m1", "PC-01", 1), May("m2", "PC-02", 9) },
            Map(("a", "m1")), Khong);

        Assert.Equal(new[] { "m1" }, plan.ByMachine.Keys);
        Assert.Empty(plan.Skipped);
    }

    // ===== 2b. Máy đang giữ acc HẾT quỹ → VẪN xếp vào đó (client tự xếp hàng) + cảnh báo =====
    [Fact]
    public void MayDangGiuHetQuy_VanXepVaoDo_NhungCoCanhBao()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1") },
            new[] { May("m1", "PC-01", 0), May("m2", "PC-02", 9) },
            Map(("a", "m1")), Khong);

        Assert.Equal(new[] { "m1" }, plan.ByMachine.Keys);
        Assert.Contains("hết quỹ Brave", Assert.Single(plan.Skipped));
    }

    // ===== 2c. Máy đang giữ acc đã TẮT → BỎ QUA cả nhóm (KHÔNG đẩy sang máy khác: giao vào sẽ nằm chờ mãi) =====
    [Fact]
    public void MayDangGiuAccOffline_BoQuaCaNhom_KhongDaySangMayKhac()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1"), Shop("a", "s2") },
            new[] { May("m1", "PC-01", 5, online: false), May("m2", "PC-02", 9) },
            Map(("a", "m1")), Khong);

        Assert.Empty(plan.ByMachine);
        var ly = Assert.Single(plan.Skipped);
        Assert.Contains("Kho a (2 shop)", ly);
        Assert.Contains("PC-01", ly);
    }

    // ===== 3. Có máy "nhà" (affinity), không bị giữ → ra máy nhà khi còn online + còn quỹ =====
    [Fact]
    public void CoMayNhaConQuy_RaMayNha_DuMayKhacRanhHon()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1") },
            new[] { May("m1", "PC-01", 9), May("m2", "PC-02", 1) },
            Khong, Map(("a", "m2")));

        Assert.Equal(new[] { "m2" }, plan.ByMachine.Keys);
    }

    // ===== 4. Máy nhà OFFLINE → rơi xuống máy online còn quỹ NHIỀU NHẤT (nhà chỉ là cố vấn, không phải khoá) =====
    [Fact]
    public void MayNhaOffline_RoiXuongMayOnlineQuyNhieuNhat()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1") },
            new[] { May("m1", "PC-01", 3), May("m2", "PC-02", 9, online: false), May("m3", "PC-03", 7) },
            Khong, Map(("a", "m2")));

        Assert.Equal(new[] { "m3" }, plan.ByMachine.Keys);
        Assert.Empty(plan.Skipped);
    }

    // ===== 5. Không máy nào online → không giao gì, và phải nói RÕ lý do cho operator =====
    [Fact]
    public void KhongMayNaoOnline_KhongGiaoGi_CoLyDoDocDuoc()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1") },
            new[] { May("m1", "PC-01", 5, online: false) },
            Khong, Khong);

        Assert.Empty(plan.ByMachine);
        Assert.Contains("không máy nào online còn quỹ", Assert.Single(plan.Skipped));
    }

    // ===== 5b. Máy online nhưng HẾT quỹ (và acc không bị giữ) → cũng bỏ qua, cùng lý do =====
    [Fact]
    public void MayOnlineNhungHetQuy_BoQua()
    {
        var plan = DispatchBalancer.Balance(
            new[] { Shop("a", "s1") },
            new[] { May("m1", "PC-01", 0) },
            Khong, Khong);

        Assert.Empty(plan.ByMachine);
        Assert.Single(plan.Skipped);
    }

    // ===== 6. BẪY CHÍNH: trừ quỹ theo NHÓM ACC, không theo shop — 2 acc × 3 shop vào lọt máy Free = 2 =====
    [Fact]
    public void TruQuyTheoNhomAcc_KhongTheoShop()
    {
        var targets = new[]
        {
            Shop("a", "s1"), Shop("a", "s2"), Shop("a", "s3"),
            Shop("b", "s4"), Shop("b", "s5"), Shop("b", "s6"),
        };

        var plan = DispatchBalancer.Balance(targets, new[] { May("m1", "PC-01", 2) }, Khong, Khong);

        Assert.Equal(6, Assert.Single(plan.ByMachine).Value.Count);   // cả 6 shop, chỉ tốn 2 khung
        Assert.Empty(plan.Skipped);
    }

    // ===== 6b. …và khi hết quỹ thật (acc thứ 3 trên máy Free = 2) thì acc thừa mới bị bỏ qua =====
    [Fact]
    public void HetQuyThat_AccThuBaBiBoQua()
    {
        var targets = new[] { Shop("a", "s1"), Shop("b", "s2"), Shop("c", "s3") };

        var plan = DispatchBalancer.Balance(targets, new[] { May("m1", "PC-01", 2) }, Khong, Khong);

        Assert.Equal(2, Assert.Single(plan.ByMachine).Value.Count);
        Assert.Contains("Kho c (1 shop)", Assert.Single(plan.Skipped));
    }

    // ===== 7. Hoà quỹ → tie-break theo MachineId ordinal ⇒ kết quả TẤT ĐỊNH (xem trước phải khớp lúc giao thật) =====
    [Fact]
    public void HoaQuy_TieBreakTheoMachineIdOrdinal()
    {
        var machines = new[] { May("m2", "PC-02", 4), May("m1", "PC-01", 4) };

        var plan = DispatchBalancer.Balance(new[] { Shop("a", "s1") }, machines, Khong, Khong);

        Assert.Equal(new[] { "m1" }, plan.ByMachine.Keys);
    }

    // ===== 8. Chia đều: mỗi nhóm acc trừ 1 khung nên nhóm sau nhảy sang máy còn nhiều quỹ hơn =====
    [Fact]
    public void NhieuAcc_TraiDeuTheoQuyConLai()
    {
        var targets = new[] { Shop("a", "s1"), Shop("b", "s2"), Shop("c", "s3") };
        var machines = new[] { May("m1", "PC-01", 2), May("m2", "PC-02", 2) };

        var plan = DispatchBalancer.Balance(targets, machines, Khong, Khong);

        // a → m1 (hoà 2-2, ordinal nhỏ hơn) · b → m2 (m1 còn 1, m2 còn 2) · c → m1 (hoà 1-1)
        Assert.Equal(2, plan.ByMachine["m1"].Count);
        Assert.Single(plan.ByMachine["m2"]);
        Assert.Empty(plan.Skipped);
    }
}
