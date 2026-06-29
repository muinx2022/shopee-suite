using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;

namespace Shopee.Suite.Modules.Fleet;

/// <summary>1 dòng trên bảng trạng thái: máy nào đang/đã làm gì với shop nào.</summary>
public sealed class FleetRow
{
    public string MachineName { get; init; } = "";
    public string AccountLabel { get; init; } = "";
    public string ShopName { get; init; } = "";
    public string Op { get; init; } = "";
    public string State { get; init; } = "";
    public string Updated { get; init; } = "";
    public bool Running { get; init; }
}

/// <summary>
/// "Trạng thái máy" — bảng tổng hợp từ Hub: máy nào đang scrape/import/update shop nào, máy nào đã
/// xong. Đọc <see cref="HttpCoordinationHub.CurrentFleet"/>, làm mới khi hub poll (event Changed).
/// </summary>
public sealed partial class FleetViewModel : ObservableObject
{
    public ObservableCollection<FleetRow> Rows { get; } = [];

    [ObservableProperty] private string _status = "Máy này chưa bật đồng bộ Hub.";
    [ObservableProperty] private string _machines = "";

    /// <summary>Bật → các lượt scrape/update tới chạy ĐÈ khoá máy khác (van thoát khi khoá sót).</summary>
    public bool ForceNextRun
    {
        get => CoordinationRuntime.ForceNextRun;
        set { CoordinationRuntime.ForceNextRun = value; OnPropertyChanged(); }
    }

    public FleetViewModel()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is not null)
        {
            hub.Changed += OnHubChanged;
            Refresh();
        }
    }

    private void OnHubChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Refresh();
        else d.BeginInvoke(Refresh);
    }

    [RelayCommand]
    private void Refresh()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null) { Status = "Máy này chưa bật đồng bộ Hub (Cài đặt → Đồng bộ nhiều máy)."; return; }

        var f = hub.CurrentFleet;
        Rows.Clear();

        foreach (var l in f.Leases)
            Rows.Add(MakeRow(l.BigsellerId, l.ShopId, l.Op, l.Hostname, "⏳ đang chạy", l.HeartbeatAt, true));

        var leasedKeys = f.Leases.Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var g in f.Ledger.Where(g => !leasedKeys.Contains(g.Key) && g.Status is not ("idle" or "")))
            Rows.Add(MakeRow(g.BigsellerId, g.ShopId, g.Op, g.LastHostname, StateIcon(g.Status), g.UpdatedAt, false));

        // Danh sách máy đang kết nối: CHỈ máy Hub được xem (theo yêu cầu). Action thì mọi máy đều thấy.
        if (HubServerConfigStore.Shared.Current.Enabled)
            Machines = f.Machines.Count == 0 ? "🖥 (chưa có máy nào kết nối)"
                : "Máy đang kết nối:   " + string.Join("        ", f.Machines.Select(m => $"🖥 {m.Hostname} · {Ago(m.LastSeen)}"));
        else
            Machines = "(danh sách máy đang kết nối chỉ hiển thị trên máy Hub)";
        Status = $"{f.Leases.Count} việc đang chạy · cập nhật {DateTimeOffset.Now:HH:mm:ss}";
    }

    private static FleetRow MakeRow(string bsId, string shopId, string op, string host, string state, DateTimeOffset at, bool running)
    {
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(a => a.Id == bsId);
        var shop = acct?.Shops.FirstOrDefault(s => s.Id == shopId);
        return new FleetRow
        {
            MachineName = string.IsNullOrWhiteSpace(host) ? "?" : host,
            AccountLabel = AcctName(acct, bsId),
            ShopName = shop?.Name is { Length: > 0 } n ? n : (shop?.ShopeeDataSheet is { Length: > 0 } sh ? sh : Short(shopId)),
            Op = OpVi(op),
            State = state,
            Updated = Ago(at),
            Running = running,
        };
    }

    private static string AcctName(BigSellerAccount? a, string id) =>
        a is null ? Short(id)
        : !string.IsNullOrWhiteSpace(a.Label) ? a.Label
        : !string.IsNullOrWhiteSpace(a.Email) ? a.Email
        : Short(id);

    private static string OpVi(string op) => op switch
    {
        "scrape" => "Scrape",
        "import" => "Import",
        "update" => "Update",
        "rewrite" => "Tên SP",
        _ => op,
    };

    private static string StateIcon(string status) => status switch
    {
        "completed" => "✓ xong",
        "stopped" => "■ dừng dở",
        "running" => "⏳ đang chạy",
        _ => status,
    };

    private static string Ago(DateTimeOffset at)
    {
        if (at == default || at == DateTimeOffset.MinValue) return "";
        var s = (DateTimeOffset.Now - at).TotalSeconds;
        if (s < 0) s = 0;
        return s < 60 ? $"{(int)s}s trước" : s < 3600 ? $"{(int)(s / 60)} phút trước" : $"{(int)(s / 3600)} giờ trước";
    }

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "?" : id[..Math.Min(8, id.Length)];
}
