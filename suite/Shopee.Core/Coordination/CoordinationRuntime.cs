namespace Shopee.Core.Coordination;

/// <summary>
/// Khởi tạo + giữ các thành phần điều phối phía client cho cả app dùng chung. Gọi
/// <see cref="InitForCurrentMode"/> lúc App khởi động — nó chọn SUẤT làm việc theo chế độ app (xem
/// <see cref="MachineSlots"/>). Nếu máy này là Hub → client tự trỏ về localhost; nếu là client thường → dùng
/// URL/token đã cấu hình. Chưa cấu hình → để NoOp (app chạy như cũ).
/// </summary>
public static class CoordinationRuntime
{
    public static HubClient? Client { get; private set; }
    public static HttpCoordinationHub? Hub { get; private set; }
    public static HubConfigSync? ConfigSync { get; private set; }

    /// <summary>Nhịp sống của SUẤT ĐƠN HÀNG (chế độ Shopee + nhánh đơn hàng của Full). null = chế độ này không
    /// chiếm suất đơn hàng / chưa cấu hình Hub.</summary>
    public static OrdersSlotHeartbeat? OrdersSlot { get; private set; }

    /// <summary>true nếu máy này đã kết nối tới một Hub (hub-mode tại chỗ hoặc client).</summary>
    public static bool Active => Client is not null;

    /// <summary>Bật → các lượt scrape/update tới sẽ CHẠY ĐÈ khoá của máy khác (cho tới khi tắt).
    /// Van thoát khi chắc chắn máy kia đã dừng nhưng khoá còn sót.</summary>
    public static bool ForceNextRun { get; set; }

    /// <summary>
    /// Khởi tạo điều phối theo CHẾ ĐỘ app hiện hành — MỘT chỗ duy nhất quyết suất nào chạy (xem
    /// <see cref="MachineSlots"/>), dùng cả lúc khởi động lẫn lúc <see cref="Reconnect"/>:
    /// <list type="bullet">
    /// <item><b>Workspace</b>: chỉ suất workspace (<see cref="InitFromConfig"/> — heartbeat + poll + lease).</item>
    /// <item><b>Shopee</b>: chỉ suất đơn hàng (client đẩy đơn/phiếu + <see cref="InitOrdersSlot"/>).</item>
    /// <item><b>Full</b>: CẢ HAI suất, trong CÙNG một process.</item>
    /// </list>
    /// </summary>
    public static void InitForCurrentMode()
    {
        var mode = Infrastructure.AppModeStore.Shared.Current;
        if (Infrastructure.AppModeStore.ShowsWorkspace(mode)) InitFromConfig();
        else InitClientOnlyFromConfig();
        if (Infrastructure.AppModeStore.ShowsShopee(mode)) InitOrdersSlot();
    }

    public static void InitFromConfig()
    {
        var clientCfg = ResolveClientConfig();
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
    /// <see cref="HttpCoordinationHub"/> — nên KHÔNG poll /fleet, KHÔNG claim việc BigSeller, KHÔNG giành lease
    /// việc. Dùng cho chế độ <b>Shopee</b> (chỉ module đơn hàng): cần đẩy đơn lên Hub nhưng KHÔNG tham gia điều
    /// phối BigSeller (tránh tranh việc + lease với bản Workspace chạy song song trên cùng PC). <see cref="Hub"/>
    /// giữ null → mọi tính năng fleet (chỉ dựng ở chế độ Workspace) không đụng tới. Việc ĐĂNG KÝ MÁY của chế độ
    /// này đi qua <see cref="InitOrdersSlot"/> bằng id SUẤT ĐƠN HÀNG (danh tính RIÊNG, không đụng suất workspace).
    /// Chưa cấu hình → để NoOp.
    /// </summary>
    public static void InitClientOnlyFromConfig()
    {
        var clientCfg = ResolveClientConfig();
        if (!clientCfg.IsConfigured) return;

        var machine = MachineIdentity.Shared;
        Client = new HubClient(clientCfg, machine.MachineId);
        ConfigSync = new HubConfigSync(Client);
        // KHÔNG dựng HttpCoordinationHub → không poll /fleet, không claim việc, không lease BigSeller. Hub giữ
        // null. Đăng ký máy (nếu chế độ có đơn hàng) do InitOrdersSlot lo, bằng id SUẤT ĐƠN HÀNG.
    }

    /// <summary>
    /// Bật nhịp sống của SUẤT ĐƠN HÀNG (<c>&lt;id-máy&gt;:orders</c>): đăng ký máy trên Hub + nhận lệnh update app.
    /// Cần <see cref="Client"/> đã dựng (một trong hai đường init ở trên) — chưa cấu hình Hub → NoOp. Gọi lại khi
    /// đang chạy thì dọn bản cũ trước (chống rò timer, y <see cref="Reconnect"/>).
    /// </summary>
    public static void InitOrdersSlot()
    {
        OrdersSlot?.Dispose();
        OrdersSlot = null;
        if (Client is not { } client) return;
        OrdersSlot = new OrdersSlotHeartbeat(client, MachineIdentity.Shared.MachineId);
    }

    /// <summary>Cấu hình client→Hub đang dùng: máy này là Hub → trỏ thẳng localhost (nhanh, khỏi vòng qua
    /// tunnel); ngược lại dùng cấu hình client đã lưu.</summary>
    private static HubClientConfig ResolveClientConfig()
    {
        var srv = HubServerConfigStore.Shared.Current;
        return srv.Enabled && !string.IsNullOrWhiteSpace(srv.ApiToken)
            ? new HubClientConfig { Enabled = true, BaseUrl = $"http://localhost:{srv.Port}", ApiToken = srv.ApiToken }
            : HubClientConfigStore.Shared.Current;
    }

    /// <summary>
    /// Áp dụng lại cấu hình client→Hub NGAY (sau khi người dùng lưu ở Cài đặt) mà KHÔNG cần khởi động lại app.
    /// Gỡ kết nối cũ rồi dựng lại từ config hiện tại. Trả về true nếu đã có client (đã cấu hình hợp lệ).
    /// Các điểm dùng (AssignmentWorker/Scrape/Update/Fleet) đều đọc <see cref="Hub"/>/<see cref="Client"/>
    /// tươi mỗi lần nên việc tráo singleton này an toàn.
    /// </summary>
    public static bool Reconnect()
    {
        // Giữ tham chiếu bản CŨ để DISPOSE sau khi tráo singleton: HttpCoordinationHub có Timer _poller 12s;
        // chỉ gán = null thì instance cũ sống mãi (heartbeat tiếp sau khi ngắt; đổi URL/token → 2 poller song
        // song). HubClient cũng giữ 2 HttpClient → dispose để giải phóng. Dispose SAU khi đã trỏ singleton về
        // bản mới: lệnh đang bay trên client cũ (nếu có) chỉ ném ObjectDisposedException, đã bọc try/catch ở nơi gọi.
        var oldHub = Hub;
        var oldClient = Client;
        var oldOrders = OrdersSlot;   // suất đơn hàng cũng có Timer → phải dispose, kẻo 2 nhịp song song
        Client = null;
        Hub = null;
        ConfigSync = null;
        OrdersSlot = null;
        Coordination.Hub = NoOpCoordinationHub.Instance;   // về trạng thái "tắt" trước khi dựng lại
        oldHub?.Dispose();     // dừng poller 12s của instance cũ
        oldOrders?.Dispose();  // dừng nhịp suất đơn hàng của instance cũ
        oldClient?.Dispose();  // giải phóng 2 HttpClient của client cũ
        // Dựng lại theo ĐÚNG chế độ app (không mặc định InitFromConfig): chế độ Shopee mà dựng suất workspace là
        // máy đơn-hàng-thuần tự nhận việc BigSeller + tranh lease với bản Workspace chạy song song.
        InitForCurrentMode();
        return Active;
    }
}
