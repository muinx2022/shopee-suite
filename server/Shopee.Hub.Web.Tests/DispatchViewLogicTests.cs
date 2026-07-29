using Shopee.Core.Coordination;
using Shopee.Hub.Web.Components;

namespace Shopee.Hub.Web.Tests;

public sealed class DispatchViewLogicTests
{
    [Theory]
    [InlineData("machines", 3)]
    [InlineData("running", 5)]
    [InlineData("queued", 7)]
    [InlineData("interrupted", 11)]
    [InlineData("unknown", 0)]
    public void KpiCount_UsesTheMatchingProjection(string key, int expected)
        => Assert.Equal(expected, DispatchViewLogic.KpiCount(key, 3, 5, 7, 11));

    [Theory]
    [InlineData("machines", true, "🖥 Máy online (3)")]
    [InlineData("running", true, "▶ Việc đang chạy (5)")]
    [InlineData("queued", true, "⏳ Việc chờ (7)")]
    [InlineData("interrupted", true, "⚠ Việc gián đoạn (11)")]
    [InlineData("unknown", false, "")]
    public void KpiMetadata_RecognizesKeysAndBuildsTitles(string key, bool known, string title)
    {
        Assert.Equal(known, DispatchViewLogic.IsKpiKey(key));
        Assert.Equal(title, DispatchViewLogic.KpiPanelTitle(key, 3, 5, 7, 11));
    }

    [Fact]
    public void AdvanceEmptyKpiPanel_ClosesOnlyAfterSecondEmptyFleetTick()
    {
        var first = DispatchViewLogic.AdvanceEmptyKpiPanel("running", 0, 0);
        var second = DispatchViewLogic.AdvanceEmptyKpiPanel("running", 0, first.EmptyTicks);
        var populated = DispatchViewLogic.AdvanceEmptyKpiPanel("running", 1, first.EmptyTicks);

        Assert.False(first.Close);
        Assert.Equal(1, first.EmptyTicks);
        Assert.True(second.Close);
        Assert.Equal(0, second.EmptyTicks);
        Assert.False(populated.Close);
        Assert.Equal(0, populated.EmptyTicks);
    }

    [Theory]
    [InlineData(OrdersSessionStates.Running, "run", "▶ Đang chạy")]
    [InlineData(OrdersSessionStates.Opening, "run", "▶ Đang mở")]
    [InlineData(OrdersSessionStates.Queued, "queued", "⏱ Chờ đến lượt")]
    [InlineData(OrdersSessionStates.Stopping, "warn", "■ Đang dừng")]
    [InlineData("unknown", "idle", "— Dừng")]
    public void OrdersSessionPresentation_MapsStateConsistently(string state, string css, string label)
    {
        Assert.Equal(css, DispatchViewLogic.OrdersStateCss(state));
        Assert.Equal(label, DispatchViewLogic.OrdersStateLabel(state));
    }

    [Fact]
    public void SinceText_UsesUpdateLanguageForOrdersMirror()
    {
        var work = new DispatchWorkItem("key", "", DispatchViewLogic.OrdersOperation, "—", "login", "PC-01",
            DateTimeOffset.Now.AddMinutes(-1), "—", false);

        Assert.StartsWith("cập nhật ", DispatchViewLogic.SinceText(work, running: true));
        Assert.Equal("▶ Chạy", DispatchViewLogic.OrdersActionLabel(OrdersCommandActions.Run));
        Assert.Equal("✖ Dừng", DispatchViewLogic.OrdersActionLabel(OrdersCommandActions.Stop));
    }
}
