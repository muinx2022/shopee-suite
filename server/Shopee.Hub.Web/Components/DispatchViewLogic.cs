using Shopee.Core.Coordination;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Components;

internal sealed record DispatchKpiCard(string Key, string Icon, string Tone, string Label);

internal sealed record DispatchWorkItem(
    string Key, string AsnId, string Op, string ShopName, string AcctName, string Host,
    DateTimeOffset Since, string Rows, bool Manual, string MachineId = "", string Login = "");

internal static class DispatchViewLogic
{
    public const string OrdersOperation = "orders";

    public static readonly IReadOnlyList<DispatchKpiCard> KpiCards =
    [
        new("machines", "machines", "green", "Máy online"),
        new("running", "run", "blue", "Việc đang chạy"),
        new("queued", "clock", "amber", "Việc chờ"),
        new("interrupted", "alert", "red", "Việc gián đoạn"),
    ];

    public static bool IsKpiKey(string key) => KpiCards.Any(card => card.Key == key);

    public static int KpiCount(string key, int machines, int running, int queued, int interrupted) => key switch
    {
        "machines" => machines,
        "running" => running,
        "queued" => queued,
        "interrupted" => interrupted,
        _ => 0,
    };

    public static string KpiPanelTitle(string key, int machines, int running, int queued, int interrupted) => key switch
    {
        "machines" => $"🖥 Máy online ({machines})",
        "running" => $"▶ Việc đang chạy ({running})",
        "queued" => $"⏳ Việc chờ ({queued})",
        "interrupted" => $"⚠ Việc gián đoạn ({interrupted})",
        _ => "",
    };

    public static string SinceText(DispatchWorkItem work, bool running)
    {
        var ago = FleetStateService.Ago(work.Since);
        if (ago.Length == 0) return "—";
        return work.Op == OrdersOperation ? $"cập nhật {ago}" : $"{(running ? "chạy từ" : "xếp lúc")} {ago}";
    }

    public static (bool Close, int EmptyTicks) AdvanceEmptyKpiPanel(string selectedKey, int count, int emptyTicks)
    {
        if (selectedKey.Length == 0 || count > 0) return (false, 0);
        var next = emptyTicks + 1;
        return (next >= 2, next >= 2 ? 0 : next);
    }

    public static string OrdersStateCss(string state) => state switch
    {
        OrdersSessionStates.Running or OrdersSessionStates.Opening => "run",
        OrdersSessionStates.Queued => "queued",
        OrdersSessionStates.Stopping => "warn",
        _ => "idle",
    };

    public static string OrdersStateLabel(string state) => state switch
    {
        OrdersSessionStates.Running => "▶ Đang chạy",
        OrdersSessionStates.Opening => "▶ Đang mở",
        OrdersSessionStates.Queued => "⏱ Chờ đến lượt",
        OrdersSessionStates.Stopping => "■ Đang dừng",
        _ => "— Dừng",
    };

    public static string OrdersActionLabel(string action) => action switch
    {
        OrdersCommandActions.Run => "▶ Chạy",
        OrdersCommandActions.Stop => "✖ Dừng",
        _ => action,
    };
}
