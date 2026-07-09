using Microsoft.AspNetCore.Components;
using Shopee.Core.Coordination;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Components;

/// <summary>Base cho các trang bám snapshot fleet: tự subscribe FleetStateService.Changed → cập nhật
/// <see cref="Snap"/> → gọi <see cref="OnFleetTick"/> → StateHasChanged, và tự unsubscribe khi dispose
/// (pattern này trước bị chép tay ~7 trang). Trang override OnInitialized thì PHẢI gọi base.OnInitialized().
/// Tên property là FleetState (không phải Fleet) để khỏi trùng tên class trang Fleet.razor.</summary>
public abstract class FleetPageBase : ComponentBase, IDisposable
{
    [Inject] protected FleetStateService FleetState { get; set; } = default!;

    /// <summary>Snapshot fleet mới nhất (cập nhật mỗi ~2s trước khi OnFleetTick chạy).</summary>
    protected FleetSnapshot Snap { get; private set; } = new();

    protected override void OnInitialized()
    {
        FleetState.Changed += OnChanged;
        Snap = FleetState.Snapshot;
    }

    private void OnChanged() => InvokeAsync(() =>
    {
        Snap = FleetState.Snapshot;
        if (!ShouldTickRender()) return;   // trang tạm dừng vẽ theo nhịp (Snap vẫn được cập nhật ở trên)
        OnFleetTick();
        StateHasChanged();
    });

    /// <summary>Trang trả false để BỎ QUA cả OnFleetTick lẫn StateHasChanged của nhịp fleet — dùng khi đang mở
    /// UI nặng không muốn diff lại mỗi 2s (vd modal heatmap 20k ô). Render do trang tự gọi không bị ảnh hưởng.</summary>
    protected virtual bool ShouldTickRender() => true;

    /// <summary>Hook mỗi nhịp cập nhật fleet (sau khi Snap đã mới) — trang override để tính lại state dẫn xuất.</summary>
    protected virtual void OnFleetTick() { }

    public virtual void Dispose() => FleetState.Changed -= OnChanged;
}
