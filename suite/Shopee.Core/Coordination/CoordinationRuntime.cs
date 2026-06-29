namespace Shopee.Core.Coordination;

/// <summary>
/// Khởi tạo + giữ các thành phần điều phối phía client cho cả app dùng chung. Gọi
/// <see cref="InitFromConfig"/> lúc App khởi động. Nếu máy này là Hub → client tự trỏ về localhost;
/// nếu là client thường → dùng URL/token đã cấu hình. Chưa cấu hình → để NoOp (app chạy như cũ).
/// </summary>
public static class CoordinationRuntime
{
    public static HubClient? Client { get; private set; }
    public static HttpCoordinationHub? Hub { get; private set; }
    public static HubConfigSync? ConfigSync { get; private set; }

    /// <summary>true nếu máy này đã kết nối tới một Hub (hub-mode tại chỗ hoặc client).</summary>
    public static bool Active => Client is not null;

    /// <summary>Bật → các lượt scrape/update tới sẽ CHẠY ĐÈ khoá của máy khác (cho tới khi tắt).
    /// Van thoát khi chắc chắn máy kia đã dừng nhưng khoá còn sót.</summary>
    public static bool ForceNextRun { get; set; }

    public static void InitFromConfig()
    {
        HubClientConfig clientCfg;
        var srv = HubServerConfigStore.Shared.Current;
        if (srv.Enabled && !string.IsNullOrWhiteSpace(srv.ApiToken))
        {
            // Máy này là Hub → client trỏ thẳng localhost (nhanh, khỏi vòng qua tunnel).
            clientCfg = new HubClientConfig { Enabled = true, BaseUrl = $"http://localhost:{srv.Port}", ApiToken = srv.ApiToken };
        }
        else
        {
            clientCfg = HubClientConfigStore.Shared.Current;
        }
        if (!clientCfg.IsConfigured) return;

        var machine = MachineIdentity.Shared;
        var client = new HubClient(clientCfg, machine.MachineId);
        var hub = new HttpCoordinationHub(client, machine.MachineId, machine.Hostname);

        Client = client;
        ConfigSync = new HubConfigSync(client);
        Hub = hub;
        Coordination.Hub = hub;                 // gán vào locator để các điểm chạy (scrape/update) dùng
        _ = hub.SyncIntoProgressAsync();        // fold ledger → tiến độ local (resume xuyên máy)
    }
}
