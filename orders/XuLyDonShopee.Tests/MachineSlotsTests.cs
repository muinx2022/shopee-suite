using Shopee.Core.Coordination;

namespace XuLyDonShopee.Tests;

/// <summary>
/// SUẤT LÀM VIỆC (slot) của một PC dưới mắt Hub: mỗi máy có suất <b>Workspace</b> (việc BigSeller) và suất
/// <b>Đơn hàng</b> (việc Shopee); chế độ Full chiếm cả hai.
/// <list type="bullet">
/// <item><b>BẤT BIẾN</b>: suất Workspace GIỮ NGUYÊN id máy (không hậu tố) — đổi là đứt liên kết
/// ledger/account_home/assignments/leases/search_products/machine_roles.</item>
/// <item><b>Tương thích ngược</b>: hub deploy TRƯỚC, client release SAU ⇒ heartbeat của client CŨ không có
/// Mode/Kind/HostId, hub phải tự suy ra (workspace · host = chính id · Workspace).</item>
/// </list>
/// Thuần hàm tĩnh — không cần hub, không cần DB.
/// </summary>
public class MachineSlotsTests
{
    private const string Host = "3f2a91c84b7d4e5f8a1b2c3d4e5f6071";

    // ===== 1. Sinh id suất: workspace GIỮ NGUYÊN, đơn hàng có hậu tố =====
    [Fact]
    public void SlotId_Workspace_GiuNguyenIdMay()
    {
        Assert.Equal(Host, MachineSlots.SlotId(Host, MachineSlots.Workspace));
    }

    [Fact]
    public void SlotId_Orders_ThemHauTo()
    {
        Assert.Equal(Host + ":orders", MachineSlots.SlotId(Host, MachineSlots.Orders));
    }

    /// <summary>Loại suất lạ (gõ sai / bản cũ) KHÔNG được đẻ ra hậu tố mới — rơi về suất workspace.</summary>
    [Fact]
    public void SlotId_LoaiSuatLa_RoiVeWorkspace()
    {
        Assert.Equal(Host, MachineSlots.SlotId(Host, "search"));
        Assert.Equal(Host, MachineSlots.SlotId(Host, ""));
    }

    // ===== 2. HostOf đảo ngược đúng cả 2 chiều =====
    [Fact]
    public void HostOf_DaoNguocDungCaHaiChieu()
    {
        Assert.Equal(Host, MachineSlots.HostOf(MachineSlots.SlotId(Host, MachineSlots.Orders)));
        Assert.Equal(Host, MachineSlots.HostOf(MachineSlots.SlotId(Host, MachineSlots.Workspace)));
    }

    /// <summary>Id chứa ":orders" ở GIỮA (không phải đuôi) KHÔNG bị cắt nhầm — cắt theo đuôi, không theo "chứa".</summary>
    [Fact]
    public void HostOf_ChuoiOrdersONguyenGiua_KhongBiCatNham()
    {
        const string weird = "may:orders-01";
        Assert.Equal(weird, MachineSlots.HostOf(weird));
        Assert.False(MachineSlots.IsOrdersSlot(weird));

        // Có ":orders" ở giữa VÀ ở đuôi → chỉ cắt phần đuôi.
        Assert.Equal(weird, MachineSlots.HostOf(weird + ":orders"));
    }

    [Fact]
    public void IsOrdersSlot_ChiDungVoiDuoiOrders()
    {
        Assert.True(MachineSlots.IsOrdersSlot(Host + ":orders"));
        Assert.False(MachineSlots.IsOrdersSlot(Host));
        Assert.False(MachineSlots.IsOrdersSlot(Host + ":Orders"));   // phân biệt hoa/thường (Ordinal)
        Assert.False(MachineSlots.IsOrdersSlot(null));
    }

    // ===== 3. Suy diễn tương thích ngược: client CŨ không gửi Kind/HostId/Mode =====
    [Fact]
    public void ClientCu_ThieuBaField_RaWorkspace_HostLaChinhId_ModeWorkspace()
    {
        Assert.Equal(MachineSlots.Workspace, MachineSlots.NormalizeKind("", Host));
        Assert.Equal(Host, MachineSlots.NormalizeHostId("", Host));
        Assert.Equal("Workspace", MachineSlots.NormalizeMode(""));
        // null (JSON thiếu field hẳn) cũng phải ra đúng như vậy.
        Assert.Equal(MachineSlots.Workspace, MachineSlots.NormalizeKind(null, Host));
        Assert.Equal(Host, MachineSlots.NormalizeHostId(null, Host));
        Assert.Equal("Workspace", MachineSlots.NormalizeMode(null));
        Assert.Equal("Workspace", MachineSlots.NormalizeMode("   "));
    }

    [Fact]
    public void ClientMoi_GuiDuField_GiuNguyen()
    {
        var slot = MachineSlots.SlotId(Host, MachineSlots.Orders);
        Assert.Equal(MachineSlots.Orders, MachineSlots.NormalizeKind(MachineSlots.Orders, slot));
        Assert.Equal(Host, MachineSlots.NormalizeHostId(Host, slot));
        Assert.Equal("Full", MachineSlots.NormalizeMode("Full"));
        Assert.Equal("Shopee", MachineSlots.NormalizeMode("Shopee"));
    }

    /// <summary>Kind rỗng mà id CÓ hậu tố (bản ghi cũ trong DB sau khi thêm cột, hoặc client gửi thiếu) → vẫn
    /// nhận ra là suất đơn hàng, KHÔNG hoá thành workspace.</summary>
    [Fact]
    public void KindRong_NhungIdCoHauTo_VanRaOrders()
    {
        Assert.Equal(MachineSlots.Orders, MachineSlots.NormalizeKind("", Host + ":orders"));
        Assert.Equal(Host, MachineSlots.NormalizeHostId("", Host + ":orders"));
    }

    /// <summary>Kind lạ (bản client tương lai / rác) → coi là workspace, không đẻ loại suất mới ngoài 2 loại.</summary>
    [Fact]
    public void KindLa_CoiLaWorkspace()
    {
        Assert.Equal(MachineSlots.Workspace, MachineSlots.NormalizeKind("search", Host));
    }
}
