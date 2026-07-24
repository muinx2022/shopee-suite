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
        var hub = new HttpCoordinationHub(client, machine.MachineId);

        Client = client;
        ConfigSync = new HubConfigSync(client);
        Hub = hub;
        Coordination.Hub = hub;                 // gán vào locator để các điểm chạy (scrape/update) dùng
        // Ledger→tiến độ local (resume xuyên máy) do poller tự fold ở lần Hub trả lời ĐẦU TIÊN — tránh race
        // lúc máy-Hub mới bật (server localhost chưa kịp nghe). Xem HttpCoordinationHub.PollAsync.
    }

    /// <summary>
    /// Khởi tạo CHỈ <see cref="Client"/> (đẩy đơn/phiếu/+1-đã-bán lên Hub) mà KHÔNG dựng
    /// <see cref="HttpCoordinationHub"/> — nên KHÔNG có poller/heartbeat, KHÔNG đăng ký máy (machine.json) hay
    /// giành lease. Dùng cho chế độ <b>Shopee</b> (chỉ module đơn hàng): cần đẩy đơn lên Hub nhưng KHÔNG tham gia
    /// fleet điều phối (tránh tranh danh tính máy + lease với bản Workspace chạy song song). <see cref="Hub"/> giữ
    /// null → mọi tính năng fleet (chỉ dựng ở chế độ Workspace) không đụng tới. Chưa cấu hình → để NoOp.
    /// </summary>
    public static void InitClientOnlyFromConfig()
    {
        HubClientConfig clientCfg;
        var srv = HubServerConfigStore.Shared.Current;
        if (srv.Enabled && !string.IsNullOrWhiteSpace(srv.ApiToken))
        {
            clientCfg = new HubClientConfig { Enabled = true, BaseUrl = $"http://localhost:{srv.Port}", ApiToken = srv.ApiToken };
        }
        else
        {
            clientCfg = HubClientConfigStore.Shared.Current;
        }
        if (!clientCfg.IsConfigured) return;

        var machine = MachineIdentity.Shared;
        Client = new HubClient(clientCfg, machine.MachineId);
        ConfigSync = new HubConfigSync(Client);
        // KHÔNG dựng HttpCoordinationHub → không heartbeat/không đăng ký máy. Hub giữ null.
    }

    /// <summary>
    /// Áp dụng lại cấu hình client→Hub NGAY (sau khi người dùng lưu ở Cài đặt) mà KHÔNG cần khởi động lại app.
    /// Gỡ kết nối cũ rồi dựng lại từ config hiện tại. Trả về true nếu đã có client (đã cấu hình hợp lệ).
    /// Các điểm dùng (AssignmentWorker/Scrape/Update/Fleet) đều đọc <see cref="Hub"/>/<see cref="Client"/>
    /// tươi mỗi lần nên việc tráo singleton này an toàn.
    /// </summary>
    public static bool Reconnect()
    {
        Client = null;
        Hub = null;
        ConfigSync = null;
        Coordination.Hub = NoOpCoordinationHub.Instance;   // về trạng thái "tắt" trước khi dựng lại
        InitFromConfig();
        return Active;
    }
}
