using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="AutoRunPlan.ActionsFor"/>: Kiểm tra LUÔN có mặt và đứng CUỐI; Xử lý/Sync chỉ có
/// khi bật cờ; thứ tự cố định Xử lý → Sync → Kiểm tra.
/// </summary>
public class AutoRunPlanTests
{
    [Fact]
    public void ActionsFor_TatCaTat_ChiKiemTra()
    {
        var actions = AutoRunPlan.ActionsFor(doProcess: false, doSync: false);

        Assert.Equal(new[] { AutoRunActionKind.Check }, actions);
    }

    [Fact]
    public void ActionsFor_BatXuLy_XuLyRoiKiemTra()
    {
        var actions = AutoRunPlan.ActionsFor(doProcess: true, doSync: false);

        Assert.Equal(new[] { AutoRunActionKind.Process, AutoRunActionKind.Check }, actions);
    }

    [Fact]
    public void ActionsFor_BatSync_SyncRoiKiemTra()
    {
        var actions = AutoRunPlan.ActionsFor(doProcess: false, doSync: true);

        Assert.Equal(new[] { AutoRunActionKind.Sync, AutoRunActionKind.Check }, actions);
    }

    [Fact]
    public void ActionsFor_BatCaHai_XuLy_Sync_KiemTra_DungThuTu()
    {
        var actions = AutoRunPlan.ActionsFor(doProcess: true, doSync: true);

        Assert.Equal(
            new[] { AutoRunActionKind.Process, AutoRunActionKind.Sync, AutoRunActionKind.Check },
            actions);
    }

    [Fact]
    public void ActionsFor_MoiCauHinh_KiemTraLuonDungCuoi()
    {
        foreach (var doProcess in new[] { false, true })
        {
            foreach (var doSync in new[] { false, true })
            {
                var actions = AutoRunPlan.ActionsFor(doProcess, doSync);
                Assert.Equal(AutoRunActionKind.Check, actions[^1]); // luôn cuối
                Assert.Contains(AutoRunActionKind.Check, actions);   // luôn có
            }
        }
    }
}
