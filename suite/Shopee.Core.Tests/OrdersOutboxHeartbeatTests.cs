using Shopee.Core.Coordination;

namespace Shopee.Core.Tests;

/// <summary>
/// Phía CLIENT của backlog "chờ đẩy" trên nhịp sống (H1.4). Bất biến: máy KHÔNG có module Đơn hàng thì nhịp
/// KHÔNG mang field này (Hub hiện "—"), và một con số phụ không bao giờ được làm chết nhịp sống.
/// <para><see cref="IDisposable"/> để trả hook static về null sau mỗi ca — nó là trạng thái toàn process.</para>
/// </summary>
public sealed class OrdersOutboxHeartbeatTests : IDisposable
{
    public OrdersOutboxHeartbeatTests() => OrdersSlotHeartbeat.OutboxPendingProvider = null;

    public void Dispose() => OrdersSlotHeartbeat.OutboxPendingProvider = null;

    /// <summary>Chế độ KHÔNG có module Đơn hàng (Workspace) / module init hỏng → không ai rót hook → không báo.</summary>
    [Fact]
    public void KhongCoModuleDonHang_KhongBaoSoTon()
    {
        Assert.Null(OrdersSlotHeartbeat.OutboxPendingProvider);
        Assert.Null(OrdersSlotHeartbeat.TonChoDay());
    }

    /// <summary>Có module → báo đúng số; 0 vẫn là 0 (không được rơi về "không báo").</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    public void CoModuleDonHang_BaoDungSo(int ton)
    {
        OrdersSlotHeartbeat.OutboxPendingProvider = () => ton;
        Assert.Equal(ton, OrdersSlotHeartbeat.TonChoDay());
    }

    /// <summary>Hook ném (DB module đang khoá…) → coi như KHÔNG báo, KHÔNG cho lỗi thoát ra làm hỏng nhịp sống.</summary>
    [Fact]
    public void HookNem_CoiNhuKhongBao_KhongLamChetNhip()
    {
        OrdersSlotHeartbeat.OutboxPendingProvider = () => throw new InvalidOperationException("DB bận");
        Assert.Null(OrdersSlotHeartbeat.TonChoDay());
    }

    /// <summary>Số âm (không nên có) kẹp về 0 — Hub không phải lưu giá trị vô nghĩa.</summary>
    [Fact]
    public void SoAm_KepVeKhong()
    {
        OrdersSlotHeartbeat.OutboxPendingProvider = () => -5;
        Assert.Equal(0, OrdersSlotHeartbeat.TonChoDay());
    }

    /// <summary>
    /// HỢP ĐỒNG với hub CŨ + suất WORKSPACE: dựng <see cref="MachineHeartbeatRequest"/> theo ĐÚNG cách hai đường
    /// đó dựng (không truyền tham số mới) thì field mới là <c>null</c> — nghĩa là client mới không đổi hành vi của
    /// suất workspace, và client cũ (biên dịch với 7 tham số) vẫn hợp lệ.
    /// </summary>
    [Fact]
    public void DungNhipKieuCu_FieldMoiLaNull()
    {
        var workspace = new MachineHeartbeatRequest("m1", "PC-01", "1.7.19", 6, "Full", MachineSlots.Workspace, "m1");
        var clientCu = new MachineHeartbeatRequest("m1", "PC-01", "1.6.0", 4);

        Assert.Null(workspace.OutboxPending);
        Assert.Null(clientCu.OutboxPending);
    }
}
