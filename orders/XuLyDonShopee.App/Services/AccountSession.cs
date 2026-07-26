using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Một phiên mở trang bán hàng CHẠY NỀN ĐỘC LẬP cho một tài khoản (mỗi tài khoản một Brave/profile/CDP
/// port/proxy/theo-dõi-đơn riêng) — nhờ đó mở được nhiều shop song song. Kế thừa
/// <see cref="ObservableObject"/> để trạng thái quan sát được.
/// <para>
/// Toàn bộ luồng <b>bê nguyên</b> từ <c>AccountsViewModel.OpenSellerAsync</c> cũ (chọn proxy → chuẩn bị
/// trình duyệt → mở → tự đăng nhập kiểu người → vòng poll bắt cookie + theo dõi đơn theo chu kỳ cấu hình → bắt-cookie-chốt),
/// CHỈ khác: <b>bỏ mọi hộp thoại modal</b> (15 phiên = 15 modal) → thay bằng trạng thái/log per-account; và
/// việc cập nhật danh sách UI được <b>marshal về UI thread</b> ở ViewModel qua sự kiện (session chỉ ghi DB
/// trên thread nền — SQLite an toàn — rồi phát <see cref="CookieSaved"/>).
/// </para>
/// </summary>
public partial class AccountSession : ObservableObject, IAccountSession
{
    private readonly long _accountId;
    private readonly AppServices _services;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    // GĐ4: phiên cầu nối đang chạy (nút "▶ Chạy" dùng đường cầu nối extension). Giữ để Dừng kill trình duyệt sạch.
    private volatile OrdersBridgeSession? _bridge;

    // Cờ "SẴN SÀNG THAO TÁC" TƯỜNG MINH: chỉ true SAU khi (của lần mở/relaunch HIỆN TẠI) đã tự-đăng-nhập
    // xong VÀ đọc được số "Chờ Lấy Hàng" lần đầu — điểm CHẮC CHẮN trang chủ đã đăng nhập & ổn định, luồng
    // chuột tự-đăng-nhập đã xong. Nút Sync/Kiểm tra (AccountsViewModel) CHỜ cờ này rồi mới điều hướng để
    // KHÔNG giẫm lên login. Đặt LẠI false ở đầu MỖI vòng mở/relaunch + khi phát hiện đổi proxy + khi
    // Stopped/Error → kín mọi ca restart/relaunch/đang-login, KHÔNG bị "sẵn sàng ảo" do số đơn cũ còn sót
    // (ToShipCount không reset khi relaunch). volatile: đọc từ UI thread trong lúc phiên chạy nền ghi.
    private volatile bool _readyForActions;

    // Nhãn tài khoản gắn vào mỗi dòng log (phân biệt nguồn khi nhiều phiên chạy song song). Mặc định
    // "TK {id}" để log phát TRƯỚC khi đọc được email (chọn proxy, chuẩn bị trình duyệt) vẫn có nhãn;
    // RunAsync cập nhật thành email khi đã đọc tài khoản.
    // volatile: đảm bảo thread khác (UI thread, thread sync) luôn thấy giá trị mới nhất khi nhiều phiên chạy song song.
    private volatile string _logLabel;

    // ===== Mô hình 1 subaccount = nhiều shop =====
    // Shop ĐANG xử lý trong vòng lặp shop (đặt trước khi chạy flow của shop, XÓA sau ở finally). SyncOrdersAsync
    // gắn shop_id này vào đơn khi upsert; PushOrdersToGsheetAsync lọc đơn theo shop + lấy Tên Shop = tên đăng nhập.
    // volatile: RunAsync (thread nền) đặt, lượt đẩy GSheet nền đọc (nhưng đã CHỤP giá trị lúc kích hoạt để tránh đua).
    private volatile string? _currentShopId;
    private volatile string? _currentShopLogin;

    // Cờ chống spam log "chưa cấu hình GSheet": phiên chạy cả buổi, mỗi shop một lượt đẩy sheet → chỉ báo 1 dòng
    // cho cả phiên là đủ để người dùng thấy máy đang KHÔNG ghi sheet. volatile: lượt đẩy chạy trên thread nền.
    private volatile bool _daBaoThieuGsheetUrl;

    public AccountSession(
        long accountId,
        AppServices services)
    {
        _accountId = accountId;
        _services = services;
        _logLabel = $"TK {accountId}";
    }

    public long AccountId => _accountId;

    [ObservableProperty]
    private SessionState _state = SessionState.Stopped;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private int? _toShipCount;

    [ObservableProperty]
    private string? _lastError;

    // Bất kỳ thay đổi quan sát được nào → phát Changed để manager/VM cập nhật UI. Event Changed CỐ Ý bắn
    // từ thread nền: manager dùng ConcurrentDictionary (thread-safe) và VM tự marshal về UI thread khi đụng
    // ObservableCollection. Riêng PropertyChanged (cho binding trực tiếp) được marshal ở OnPropertyChanged.
    partial void OnStateChanged(SessionState value) => Changed?.Invoke();
    partial void OnStatusTextChanged(string? value) => Changed?.Invoke();
    partial void OnToShipCountChanged(int? value) => Changed?.Invoke();
    partial void OnLastErrorChanged(string? value) => Changed?.Invoke();

    /// <summary>
    /// Marshal thông báo <b>PropertyChanged</b> về UI thread. Phiên chạy nền (RunAsync) set
    /// State/StatusText/ToShipCount trên thread nền; nếu UI (Plan B) bind TRỰC TIẾP vào phiên thì Avalonia
    /// cập nhật binding phải trên UI thread — nếu bắn từ nền sẽ ném "Call from invalid thread". Chạy ngay
    /// nếu đã ở UI thread; ngược lại <c>Dispatcher.UIThread.Post</c>.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess())
        {
            base.OnPropertyChanged(e);
        }
        else
        {
            ui.Post(() => base.OnPropertyChanged(e));
        }
    }

    public Process? BraveProcess => _bridge?.Process;

    /// <summary>
    /// True khi phiên đã "sẵn sàng thao tác" (của lần mở HIỆN TẠI đã đăng nhập xong + đọc số đơn lần đầu) —
    /// VM chờ cờ này rồi mới chạy Sync/Kiểm tra để không giẫm luồng tự-đăng-nhập. Xem <see cref="_readyForActions"/>.
    /// </summary>
    public bool ReadyForActions => _readyForActions;

    /// <summary>True khi vòng lặp shop (mô hình 1 subaccount = nhiều shop) đang chạy — VM dùng để BỎ QUA thao tác
    /// tay (Sync/Kiểm tra) tránh giẫm luồng. Mô hình cầu nối: vòng lặp shop nằm TRONG
    /// <see cref="OrdersBridgeSession"/> (không ở AccountSession) nên LUÔN false — thao tác tay định tuyến qua cầu nối.</summary>
    public bool IsShopLoopRunning => false;

    public event Action? Changed;
    public event Action<long>? CookieSaved;

    public Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            // Idempotent: đang chuẩn bị / đang chạy / ĐANG DỪNG (vòng nền cũ chưa tháo dỡ xong) → bỏ qua để KHÔNG
            // mở phiên MỚI cùng hồ sơ + cổng 47821 khi phiên cũ còn sống (Lỗi 5). Chỉ Queued/Stopped/Error mới launch.
            if (State is SessionState.Opening or SessionState.Running or SessionState.Stopping)
            {
                return Task.CompletedTask;
            }

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            LastError = null;
            _readyForActions = false; // phiên mới khởi động → CHƯA sẵn sàng (chờ login + đọc số lần đầu)
            State = SessionState.Opening;
            // GĐ4: nút "▶ Chạy" nay dùng ĐƯỜNG CẦU NỐI (login Playwright → clean+extension → mọi shop → sync DB →
            // nghỉ interval → lặp). Luồng Playwright cũ (RunAsync) GIỮ làm đường lui nhưng KHÔNG còn được gọi từ đây.
            _runTask = Task.Run(() => RunBridgeContinuousAsync(ct));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Đánh dấu phiên đang <see cref="SessionState.Queued"/> (CHỜ TỚI LƯỢT — do <see cref="AccountSessionManager"/>
    /// gọi khi Start lúc đã có phiên khác chiếm cầu nối). KHÔNG mở trình duyệt; chỉ đổi trạng thái + StatusText hiển
    /// thị. Bỏ qua nếu đang chuẩn bị/đang chạy/đang dừng (không hạ cấp một phiên đang hoạt động về hàng đợi).
    /// </summary>
    public void MarkQueued()
    {
        lock (_lifecycleLock)
        {
            if (State is SessionState.Opening or SessionState.Running or SessionState.Stopping)
            {
                return; // đang hoạt động → không hạ về hàng đợi
            }
            StatusText = "Chờ đến lượt (đã có tài khoản khác đang chạy)...";
            State = SessionState.Queued;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? run;
        lock (_lifecycleLock)
        {
            // Phiên đang CHỜ TỚI LƯỢT (chưa mở trình duyệt) → chỉ hạ về Stopped, không có gì để tháo dỡ.
            // (Manager tự rút khỏi hàng đợi; OnSessionChanged gỡ khỏi dict khi thấy Stopped.)
            if (State == SessionState.Queued)
            {
                StatusText = null;
                State = SessionState.Stopped;
                return;
            }

            cts = _cts;
            run = _runTask;
        }

        // Phản hồi cho người dùng; GIỮ State (Running/Opening) để nút "Mở" còn khóa tới khi vòng nền dừng thật (Lỗi 2).
        // Ghi LOG ngay khi bấm Dừng (kể cả lúc đang NGHỈ giữa chu kỳ, Brave đã đóng) để panel nhật ký có phản hồi.
        var wasActive = State is SessionState.Opening or SessionState.Running or SessionState.Stopping;
        if (wasActive)
        {
            StatusText = "Đang dừng...";
            _services.Log.Append(_logLabel, "Đang dừng phiên (hủy vòng lặp + đóng trình duyệt)...");
        }

        try { cts?.Cancel(); } catch { /* bỏ qua */ }

        // Kill trình duyệt SẠCH cầu nối NGAY (trước khi chờ) để vòng nền unblock nhanh, không đợi hết 8s.
        try
        {
            var bp = _bridge?.Process;
            if (bp is { HasExited: false })
            {
                bp.Kill(entireProcessTree: true);
            }
        }
        catch { /* bỏ qua */ }

        if (run is not null)
        {
            // Chờ vòng nền thoát & dispose trong ~8s. KHÔNG ép Stopped sau đó (Lỗi 5): nếu vòng CHƯA xong (bước
            // login Playwright có thể sống quá 8s) mà ép Stopped thì OnSessionChanged gỡ phiên khỏi dict → user
            // bấm Chạy lại tạo phiên MỚI cùng hồ sơ + cùng cổng 47821 trong khi phiên cũ còn tháo dỡ → Error.
            Task done;
            try { done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false); }
            catch { done = run; /* WhenAny không ném; phòng hờ */ }

            if (!ReferenceEquals(done, run))
            {
                // Quá 8s mà vòng nền CHƯA hoàn tất → giữ trạng thái "Đang dừng…" (Stopping): nút Chạy còn khóa,
                // manager giữ phiên trong dict + KHÔNG start phiên kế cho tới khi vòng nền tự đặt Stopped (finally
                // của RunBridgeContinuousAsync). ct đã hủy + trình duyệt đã kill nên vòng sẽ tự kết thúc.
                lock (_lifecycleLock)
                {
                    if (State is not (SessionState.Stopped or SessionState.Error))
                    {
                        StatusText = "Đang dừng… (đang tháo dỡ trình duyệt/đăng nhập — sẽ xong sau giây lát)";
                        State = SessionState.Stopping;
                    }
                }
                return; // CHƯA dừng hẳn — không log "Đã dừng phiên."
            }
        }

        // Vòng nền đã hoàn tất (finally của nó đã đặt Stopped/Error). Chỉ log tổng kết.
        if (wasActive)
        {
            _services.Log.Append(_logLabel, "Đã dừng phiên.");
        }
    }

    /// <summary>
    /// Gỡ cờ "TK chưa xác nhận" cho tài khoản này khi phiên vừa đăng nhập được (đọc số "Chờ Lấy Hàng" lần
    /// đầu). Chỉ log + phát <see cref="AppServices.RaiseAccountsChanged"/> khi THỰC SỰ có cờ được gỡ
    /// (<see cref="AccountRepository.ClearVerifyFailed"/> trả &gt;0) để không làm mới UI thừa mỗi lần mở phiên.
    /// Best-effort: mọi lỗi bị nuốt (KHÔNG phá luồng theo dõi đơn).
    /// </summary>
    private void TryClearVerifyFailedAfterLogin()
    {
        try
        {
            if (_services.Accounts.ClearVerifyFailed(_accountId) > 0)
            {
                _services.Log.Append(_logLabel, "Đã xác minh được — gỡ nhãn TK chưa xác nhận.");
                _services.RaiseAccountsChanged();
            }
        }
        catch { /* best-effort — không phá luồng */ }
    }

    /// <summary>Kết quả một lượt <see cref="PersistSyncedOrdersAsync"/> — số đơn thêm mới / cập nhật / bỏ qua (ngoài theo dõi).</summary>
    public readonly record struct PersistOrdersResult(int Inserted, int Updated, int BoQua);

    /// <summary>
    /// <b>Phần LƯU của một lượt sync</b> — tách khỏi <see cref="SyncOrdersAsync"/> (Playwright) để DÙNG CHUNG cho
    /// callback CẦU NỐI (extension đọc đơn, GĐ4). Thao tác THUẦN trên DTO <paramref name="orders"/> + DB/GSheet/hub,
    /// KHÔNG đụng trình duyệt: lọc "chỉ giữ đơn Chuẩn bị hàng"/đã-theo-dõi → detect "Đã bán" (đọc status CŨ trước
    /// upsert) → <see cref="OrderRepository.UpsertMany"/> gắn <paramref name="shopId"/> → đánh cờ sold ngay cho
    /// nhóm không +1 → phát <see cref="AppServices.RaiseOrdersChanged"/> → đẩy GSheet/hub/hub-slip/sold/notify chạy
    /// NỀN (fire-and-forget; hook chưa rót → im lặng bên trong). Trả về số đơn thêm/cập nhật/bỏ qua để caller tổng kết.
    /// </summary>
    public Task<PersistOrdersResult> PersistSyncedOrdersAsync(
        string? shopId, IReadOnlyList<SyncedOrder> orders, Action<string> log, CancellationToken tok)
    {
        // Lọc đơn được LƯU: đơn ĐÃ theo dõi (mã đã có trong DB) LUÔN cập nhật; đơn MỚI chỉ nhận khi Chuẩn bị hàng.
        // (Filter này ĐỒNG THỜI chặn đơn đã-bị-dọn được insert lại → không lặp ghi-xóa.)
        var existing = _services.Orders.GetOrderSns(_accountId);
        var toUpsert = orders
            .Where(o => existing.Contains(o.OrderSn) || ShopeeShippingNav.LaChuanBiHang(o.Status))
            .ToList();

        // "Đã bán" theo SKU: đọc trạng thái CŨ TRƯỚC khi UpsertMany ghi đè (tuần tự nên tương đương cùng transaction).
        var soldDetect = _services.Orders.DetectNewlyDelivered(_accountId, toUpsert);

        // Upsert theo (account_id, order_sn), gắn shopId + shopLogin (tên shop, cho cột "Shop" màn Đơn hàng) của lượt
        // này. insertedOrders = đơn VỪA thêm mới (để notify).
        var (inserted, updated, insertedOrders) = _services.Orders.UpsertMany(_accountId, toUpsert, DateTime.UtcNow, shopId, _currentShopLogin);

        // Đánh cờ NGAY cho nhóm KHÔNG cần +1 (grandfather + đã-giao-không-SKU). Nhóm CÓ SKU đánh cờ SAU khi hub +1 OK.
        if (soldDetect.ImmediateMarkOrderSns.Count > 0)
        {
            _services.Orders.MarkSoldCounted(_accountId, soldDetect.ImmediateMarkOrderSns, DateTime.UtcNow);
        }

        // Vừa ghi đơn → phát tín hiệu để màn "Đơn hàng" đang mở tự nạp lại.
        _services.RaiseOrdersChanged();

        // Đẩy GSheet/hub/sold/notify chạy NỀN (chỉ đụng DB + file + HTTP, KHÔNG trình duyệt).
        // Hub: đẩy ĐƠN rồi mới đẩy PHIẾU (StartHubPushInBackground tự nối phiếu SAU khi đơn lên hub — xem lý do ở đó,
        // tránh đua với reset hub_synced_at khi mã vận đơn vừa xuất hiện).
        StartGsheetPushInBackground(log, tok);
        StartHubPushInBackground(log, tok);
        StartSoldCountInBackground(soldDetect.SkusToIncrement, soldDetect.PendingMarkOrderSns, log, tok);
        if (insertedOrders.Count > 0)
        {
            StartNotifyInBackground(insertedOrders, log, tok);
        }

        return Task.FromResult(new PersistOrdersResult(inserted, updated, orders.Count - toUpsert.Count));
    }

    /// <summary>
    /// <b>Tải LẠI phiếu MỘT đơn (nút "Tải phiếu" màn Đơn hàng):</b> ĐỊNH TUYẾN qua PHIÊN CẦU NỐI extension đang
    /// chạy (nút "▶ Chạy" nay dùng đường cầu nối — <see cref="RunBridgeContinuousAsync"/> giữ tham chiếu
    /// <see cref="_bridge"/>) rồi gọi <see cref="OrdersBridgeSession.RedownloadSlipAsync"/> (extension về danh
    /// sách "Tất cả", định vị card theo mã, bấm In phiếu giao, tải PDF, C# lưu file). Lưu được → phát
    /// <see cref="AppServices.RaiseOrdersChanged"/> (cột Phiếu hết đỏ). Graceful — KHÔNG ném:
    /// <list type="bullet">
    /// <item>Phiên chưa chạy (không có cầu nối) → <c>false</c> + báo "Hãy bấm Chạy tài khoản rồi mới tải lại phiếu".</item>
    /// <item>Extension chưa/không còn kết nối (<see cref="InvalidOperationException"/> từ SendAsync) → <c>false</c> +
    /// báo "extension chưa kết nối" (fail-fast, KHÔNG chờ timeout).</item>
    /// <item>Không thấy đơn trong shop đang mở / hủy / lỗi khác → <c>false</c> + StatusText/log.</item>
    /// </list>
    /// Ràng buộc mô hình cầu nối (user đã chốt): chỉ tải lại được phiếu của đơn thuộc SHOP mà phiên đang mở tab.
    /// </summary>
    public async Task<bool> RedownloadSlipAsync(string orderSn)
    {
        if (string.IsNullOrWhiteSpace(orderSn))
        {
            return false;
        }

        // Chụp phiên cầu nối + token dưới lock.
        OrdersBridgeSession? bridge;
        CancellationToken tok;
        try
        {
            lock (_lifecycleLock)
            {
                bridge = _bridge;
                tok = _cts?.Token ?? default;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        // Phiên chưa chạy (không có cầu nối đang mở) → hướng dẫn bấm Chạy trước. State phải Running (cầu nối đã lên).
        if (bridge is null || State != SessionState.Running)
        {
            StatusText = "Hãy bấm Chạy tài khoản rồi mới tải lại phiếu.";
            _services.Log.Append(_logLabel,
                $"Tải lại phiếu đơn {orderSn}: phiên chưa chạy — hãy bấm Chạy tài khoản trước.");
            return false;
        }

        StatusText = $"Đang tải lại phiếu đơn {orderSn}...";
        var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));
        try
        {
            var ok = await bridge.RedownloadSlipAsync(orderSn, tok).ConfigureAwait(false);
            if (ok)
            {
                StatusText = $"Đã tải lại phiếu đơn {orderSn}.";
                _services.RaiseOrdersChanged();
                return true;
            }

            StatusText = $"Chưa tải được phiếu đơn {orderSn} — xem nhật ký.";
            return false;
        }
        catch (OperationCanceledException)
        {
            return false; // dừng chủ động
        }
        catch (InvalidOperationException ex)
        {
            // Extension chưa/không còn kết nối (SendAsync fail-fast) hoặc cầu nối chưa khởi động.
            StatusText = $"Tải lại phiếu đơn {orderSn}: extension chưa kết nối — thử lại sau khi phiên ổn định.";
            log("Tải lại phiếu (cầu nối) lỗi: " + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            StatusText = $"Tải lại phiếu đơn {orderSn} gặp lỗi — xem nhật ký.";
            log("Lỗi khi tải lại phiếu: " + ex.Message);
            return false;
        }
    }

    /// <summary>Giới hạn kích thước file phiếu đính kèm (5MB) — PDF phiếu giao thường ~100–300KB.</summary>
    private const long MaxSlipBytes = 5 * 1024 * 1024;

    /// <summary>Cờ CHỐNG CHỒNG lượt đẩy GSheet trên CÙNG phiên (0 = rảnh, 1 = đang đẩy). Bấm Sync liên tiếp
    /// trong lúc lượt đẩy nền trước chưa xong → bỏ qua lượt đẩy mới (Interlocked, thread-safe).</summary>
    private int _gsheetPushing;

    /// <summary>
    /// Kích hoạt đẩy GSheet CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết. KHÔNG await trong luồng sync
    /// vì push chỉ đụng DB + file + HTTP, không đụng trình duyệt → chạy
    /// song song được với nhịp đọc "Chờ Lấy Hàng"/Xử lý đơn. Cờ <see cref="_gsheetPushing"/> chống 2 lượt đẩy
    /// chồng nhau (bấm Sync liên tiếp): lượt trước còn chạy → bỏ qua, log 1 dòng (lượt sync sau tự đẩy phần
    /// thiếu nhờ cờ DB). <paramref name="ct"/> là token phiên → dừng phiên thì lượt đẩy tự hủy.
    /// <see cref="PushOrdersToGsheetAsync"/> đã tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartGsheetPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _gsheetPushing, 1, 0) != 0)
        {
            log("GSheet: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        // CHỤP shop hiện tại NGAY (mô hình nhiều-shop): task nền chạy sau khi vòng lặp đã XÓA _currentShopId/Login
        // → phải truyền giá trị đã chụp, KHÔNG đọc field trong task. Null (chưa vào loop) → đẩy như cũ theo account.
        var shopId = _currentShopId;
        var shopLogin = _currentShopLogin;

        _ = Task.Run(async () =>
        {
            try { await PushOrdersToGsheetAsync(shopId, shopLogin, log, ct).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _gsheetPushing, 0); }
        }, CancellationToken.None);
    }

    /// <summary>Kích thước LÔ tối đa mỗi lần đẩy đơn lên hub — chia nhỏ để không nghẽn tunnel; timeout 5' của
    /// <c>_bulkHttp</c> phía hub-client đủ rộng cho một lô.</summary>
    public const int HubPushBatchSize = 200;

    /// <summary>Cờ CHỐNG CHỒNG lượt đẩy hub trên CÙNG phiên (0 = rảnh, 1 = đang đẩy) — y <see cref="_gsheetPushing"/>.</summary>
    private int _hubPushing;

    /// <summary>
    /// Kích hoạt đẩy đơn lên HUB đơn hàng CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartGsheetPushInBackground"/>. Cờ <see cref="_hubPushing"/> (Interlocked) chống 2 lượt đẩy
    /// chồng nhau: lượt trước còn chạy → bỏ qua, log 1 dòng (lượt sync sau tự đẩy phần thiếu nhờ cờ DB
    /// <c>hub_synced_at</c>). <paramref name="ct"/> là token phiên → dừng phiên thì lượt đẩy tự hủy.
    /// <see cref="PushOrdersToHubAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartHubPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _hubPushing, 1, 0) != 0)
        {
            log("Hub: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await PushOrdersToHubAsync(log, ct).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _hubPushing, 0); }
            // PHIẾU đẩy SAU khi ĐƠN đã lên hub (hub_synced_at set) — KHÔNG chạy song song với đẩy đơn: khi mã vận đơn
            // vừa xuất hiện, UpsertMany RESET hub_synced_at về NULL để re-push đơn; nếu đẩy phiếu song song, nó đọc
            // GetForHubSlipPush (đòi đơn ĐÃ hub-synced) TRÚNG lúc hub_synced_at đang NULL → bỏ sót phiếu. Tuần tự thì
            // tới lượt phiếu, đơn đã re-push xong (hub_synced_at set lại) → phiếu khớp đơn trên hub.
            StartHubSlipPushInBackground(log, ct);
        }, CancellationToken.None);
    }

    /// <summary>Cờ CHỐNG CHỒNG lượt +1 "Đã bán" theo SKU trên CÙNG phiên (0 = rảnh, 1 = đang +1) — y <see cref="_hubPushing"/>.</summary>
    private int _soldCounting;

    /// <summary>
    /// Kích hoạt +1 "Đã bán" theo SKU lên HUB CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartHubPushInBackground"/>. <paramref name="skus"/> = SKU các đơn VỪA chuyển sang đã-giao trong
    /// lượt này (có SKU); <paramref name="orderSns"/> = mã đơn tương ứng để đánh cờ SAU khi hub +1 OK. Không có SKU
    /// nào → return ngay (không chiếm cờ). Cờ <see cref="_soldCounting"/> (Interlocked) chống 2 lượt chồng nhau.
    /// <paramref name="ct"/> là token phiên → dừng phiên thì lượt +1 tự hủy. <see cref="IncrementSoldBySkuAsync"/>
    /// tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartSoldCountInBackground(
        IReadOnlyList<string> skus, IReadOnlyList<string> orderSns, Action<string> log, CancellationToken ct)
    {
        if (skus is null || skus.Count == 0)
        {
            return; // không có đơn chuyển-sang-đã-giao có SKU → không +1 (grandfather đã đánh cờ ở luồng chính)
        }
        if (Interlocked.CompareExchange(ref _soldCounting, 1, 0) != 0)
        {
            log("Đã bán: lượt +1 trước còn đang chạy — bỏ qua (lượt sync sau tự đếm phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await IncrementSoldBySkuAsync(skus, orderSns, log, ct).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _soldCounting, 0); }
        }, CancellationToken.None);
    }

    /// <summary>
    /// +1 "Đã bán" theo SKU lên HUB qua hook <see cref="AppServices.IncrementSoldBySku"/> (do shell suite rót), rồi
    /// CHỈ đánh cờ <c>sold_counted_at</c> cho <paramref name="orderSns"/> khi hub +1 OK (ưu tiên KHÔNG mất đếm nếu
    /// hub lỗi). <b>Không bao giờ ném</b>: hủy CHỦ ĐỘNG → thôi; lỗi khác → log. Hook null (app Đơn hàng chạy độc
    /// lập / hub chưa cấu hình) → return im lặng (đơn CHƯA đánh cờ → lượt sync sau thử lại).
    /// </summary>
    private async Task IncrementSoldBySkuAsync(
        IReadOnlyList<string> skus, IReadOnlyList<string> orderSns, Action<string> log, CancellationToken ct)
    {
        var inc = _services.IncrementSoldBySku;
        if (inc is null)
        {
            return; // hub tắt / app Đơn hàng chạy độc lập → im lặng, KHÔNG đánh cờ (lượt sau thử lại)
        }

        try
        {
            var ok = await inc(skus, ct).ConfigureAwait(false);
            if (ok)
            {
                // Hub +1 OK → đánh cờ để không +1 lại lượt sau. (Rủi ro hiếm: +1 xong mà đánh cờ lỗi/crash →
                // lượt sau đếm lại 1 lần — chấp nhận, ưu tiên không mất đếm.)
                _services.Orders.MarkSoldCounted(_accountId, orderSns, DateTime.UtcNow);
                var preview = string.Join(", ", skus.Take(20));
                log($"+{skus.Count} Đã bán theo SKU: {preview}{(skus.Count > 20 ? " …" : string.Empty)}");
            }
            else
            {
                log("Đã bán: hub chưa nhận (+1 hoãn) — lượt sync sau thử lại.");
            }
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động (dừng phiên) — thôi; đơn CHƯA đánh cờ, lượt sau thử lại.
        }
        catch (Exception ex)
        {
            log("Đã bán: lỗi — " + ex.Message);
        }
    }

    /// <summary>
    /// Đẩy các đơn CHƯA đẩy hub của tài khoản này lên HUB đơn hàng qua hook <see cref="AppServices.PushOrdersToHub"/>
    /// (do shell suite rót). <b>Không bao giờ ném</b> (sync DB đã xong — lỗi hub chỉ ghi log): hủy CHỦ ĐỘNG → thôi;
    /// lỗi khác → log "Hub: lỗi — ...". Hook null (app Đơn hàng chạy độc lập / hub chưa cấu hình) → return im lặng
    /// (không đổi hành vi cũ, KHÔNG đụng DB). Không có đơn chờ → return. Logic chia lô + đánh dấu tách sang hàm thuần
    /// <see cref="PushPendingToHubAsync"/> (test được, không đụng trình duyệt).
    /// </summary>
    private async Task PushOrdersToHubAsync(Action<string> log, CancellationToken ct)
    {
        var push = _services.PushOrdersToHub;
        if (push is null)
        {
            return; // hub tắt / app Đơn hàng chạy độc lập → im lặng, không đụng DB
        }

        try
        {
            var pending = _services.Orders.GetForHubPush(_accountId);
            if (pending.Count == 0)
            {
                return;
            }

            var marked = await PushPendingToHubAsync(
                _accountId,
                pending,
                push,
                sns => _services.Orders.MarkHubSynced(_accountId, sns, DateTime.UtcNow),
                HubPushBatchSize,
                ct).ConfigureAwait(false);

            if (marked > 0)
            {
                log($"Hub: đã đẩy {marked}/{pending.Count} đơn lên hub.");
            }
            else
            {
                // KHÔNG im lặng: hook trả false ngay lô đầu (hub offline/lỗi) trước đây không để lại dấu vết nào
                // → máy chạy cả buổi mà không đơn nào lên hub vẫn trông như bình thường.
                log($"Hub: đẩy 0/{pending.Count} đơn — hub không phản hồi, sẽ thử lại lượt sau.");
            }
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động (dừng phiên) — thôi.
        }
        catch (Exception ex)
        {
            // Lỗi đẩy hub KHÔNG phá lượt sync (đã ghi DB) — chỉ log; đơn CHƯA đánh dấu → lượt sau đẩy lại.
            log("Hub: lỗi — " + ex.Message);
        }
    }

    /// <summary>
    /// LÕI THUẦN (không đụng trình duyệt/DB trực tiếp → test được) của việc đẩy đơn lên hub: chia
    /// <paramref name="pending"/> thành các LÔ ≤ <paramref name="batchSize"/> rồi đẩy TUẦN TỰ qua
    /// <paramref name="push"/> (đúng chữ ký hook <see cref="AppServices.PushOrdersToHub"/>). Mỗi lô trả
    /// <c>true</c> → gọi <paramref name="markSynced"/> cho đúng các mã đơn của lô (đánh dấu đã đẩy, chống đẩy
    /// trùng lượt sau); trả <c>false</c> → DỪNG các lô còn lại (giữ đơn CHƯA đánh dấu để lượt sync sau đẩy lại —
    /// thà đẩy lặp, hub idempotent, còn hơn mất đơn). <paramref name="push"/> null (hook chưa rót) hoặc
    /// <paramref name="pending"/> rỗng → không làm gì, trả 0. Trả về SỐ đơn đã đánh dấu thành công.
    /// <paramref name="ct"/> hủy → <see cref="OperationCanceledException"/> cho XUYÊN (caller phân biệt hủy chủ động).
    /// </summary>
    public static async Task<int> PushPendingToHubAsync(
        long accountId,
        IReadOnlyList<SyncedOrder> pending,
        Func<long, IReadOnlyList<SyncedOrder>, CancellationToken, Task<bool>>? push,
        Action<IReadOnlyList<string>> markSynced,
        int batchSize,
        CancellationToken ct)
    {
        if (push is null || pending is null || pending.Count == 0)
        {
            return 0;
        }

        var marked = 0;
        for (var i = 0; i < pending.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var count = Math.Min(batchSize, pending.Count - i);
            var batch = new List<SyncedOrder>(count);
            for (var j = 0; j < count; j++)
            {
                batch.Add(pending[i + j]);
            }

            var ok = await push(accountId, batch, ct).ConfigureAwait(false);
            if (!ok)
            {
                break; // hub offline / hook trả false → dừng các lô sau, lượt sync sau tự đẩy lại
            }

            var sns = new List<string>(batch.Count);
            foreach (var o in batch)
            {
                sns.Add(o.OrderSn);
            }
            markSynced(sns);
            marked += batch.Count;
        }
        return marked;
    }

    /// <summary>Kích thước LÔ tối đa mỗi lần đẩy PHIẾU lên hub — lô ≤5 PDF ~1,5MB qua tunnel (trần hub 5MB/phiếu).</summary>
    public const int HubSlipPushBatchSize = 5;

    /// <summary>Cờ CHỐNG CHỒNG lượt đẩy PHIẾU hub trên CÙNG phiên (0 = rảnh, 1 = đang đẩy) — y <see cref="_hubPushing"/>.</summary>
    private int _hubSlipPushing;

    /// <summary>
    /// Kích hoạt đẩy FILE PHIẾU lên HUB CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartHubPushInBackground"/>. Cờ <see cref="_hubSlipPushing"/> (Interlocked) chống 2 lượt đẩy chồng
    /// nhau: lượt trước còn chạy → bỏ qua, log 1 dòng (lượt sync sau tự đẩy phần thiếu nhờ cờ DB
    /// <c>hub_slip_synced_at</c>). <paramref name="ct"/> là token phiên → dừng phiên thì lượt đẩy tự hủy.
    /// <see cref="PushSlipsToHubAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartHubSlipPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _hubSlipPushing, 1, 0) != 0)
        {
            log("Hub phiếu: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await PushSlipsToHubAsync(log, ct).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _hubSlipPushing, 0); }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Đẩy FILE PHIẾU của các đơn ĐÃ lên hub nhưng CHƯA đẩy phiếu (<see cref="OrdersRepository.GetForHubSlipPush"/>)
    /// lên HUB qua hook <see cref="AppServices.PushOrderSlipsToHub"/> (do shell suite rót). Với từng đơn: đọc file
    /// <c>&lt;invoiceDir&gt;/&lt;SanitizeFileName(sn)&gt;.pdf</c> qua kiểm magic sẵn có (<see cref="TryReadSlipBase64"/>) —
    /// file THIẾU/hỏng → bỏ qua im lặng (khi file có, lượt sau tự đẩy). Chia lô ≤ <see cref="HubSlipPushBatchSize"/>,
    /// gọi hook; danh sách <c>order_sn</c> hub báo ĐÃ LƯU → <see cref="OrdersRepository.MarkHubSlipSynced"/> đúng các
    /// đơn đó; hook trả null (hub lỗi cả lô) → DỪNG các lô sau (lượt sau thử lại). Log 1 dòng khi đẩy được ≥1 phiếu.
    /// <b>Không bao giờ ném</b>: hủy CHỦ ĐỘNG → thôi; lỗi khác → log. Hook null / không có đơn chờ → return im lặng.
    /// </summary>
    private async Task PushSlipsToHubAsync(Action<string> log, CancellationToken ct)
    {
        var push = _services.PushOrderSlipsToHub;
        if (push is null)
        {
            return; // hub tắt / app Đơn hàng chạy độc lập → im lặng, không đụng DB
        }

        try
        {
            var pending = _services.Orders.GetForHubSlipPush(_accountId);
            if (pending.Count == 0)
            {
                return;
            }

            // Đọc file phiếu local hợp lệ (tồn tại + ≤5MB + magic %PDF-) → (order_sn, base64). File thiếu → bỏ qua.
            var invoiceDir = _services.Settings.GetInvoiceFolder();
            var ready = new List<(string OrderSn, string FileBase64)>();
            foreach (var (sn, _) in pending)
            {
                var path = Path.Combine(invoiceDir, ShopeeShippingNav.SanitizeFileName(sn) + ".pdf");
                if (TryReadSlipBase64(path, log, out var b64) && b64 is not null)
                {
                    ready.Add((sn, b64));
                }
            }
            if (ready.Count == 0)
            {
                return; // chưa có file phiếu local nào hợp lệ → lượt sau (khi tải-lại-phiếu xong) tự đẩy
            }

            var pushed = 0;
            for (var i = 0; i < ready.Count; i += HubSlipPushBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var count = Math.Min(HubSlipPushBatchSize, ready.Count - i);
                var batch = ready.GetRange(i, count);

                var saved = await push(_accountId, batch, ct).ConfigureAwait(false);
                if (saved is null)
                {
                    break; // hub lỗi cả lô (offline / route chưa có) → dừng, lượt sync sau tự đẩy lại
                }
                if (saved.Count > 0)
                {
                    _services.Orders.MarkHubSlipSynced(_accountId, saved, DateTime.UtcNow);
                    pushed += saved.Count;
                }
            }

            if (pushed > 0)
            {
                log($"Hub phiếu: đã đẩy {pushed} file.");
            }
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động (dừng phiên) — thôi.
        }
        catch (Exception ex)
        {
            // Lỗi đẩy phiếu KHÔNG phá lượt sync (đã ghi DB) — chỉ log; đơn CHƯA đánh dấu → lượt sau đẩy lại.
            log("Hub phiếu: lỗi — " + ex.Message);
        }
    }

    /// <summary>
    /// Kích hoạt báo "đơn MỚI" (Slack/Discord/Telegram) CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết —
    /// y pattern <see cref="StartGsheetPushInBackground"/>. URL webhook chưa cấu hình → return im lặng (không
    /// đổi hành vi cũ). Tên shop = <see cref="Account.Email"/> (tên đăng nhập người dùng nhập, như GSheet).
    /// Dựng tin nhắn qua <see cref="OrderNotifyService.TaoTinNhanDonMoi"/> rồi gửi qua
    /// <see cref="OrderNotifyService.SendAsync"/>; thành công → log 1 dòng. Mọi exception NUỐT + log (KHÔNG phá
    /// sync — sync DB đã xong). <paramref name="ct"/> là token phiên → dừng phiên thì lượt gửi tự hủy.
    /// </summary>
    private void StartNotifyInBackground(IReadOnlyList<SyncedOrder> insertedOrders, Action<string> log, CancellationToken ct)
    {
        var url = _services.Settings.GetNotifyWebhookUrl();
        if (string.IsNullOrWhiteSpace(url) || insertedOrders is null || insertedOrders.Count == 0)
        {
            return; // người dùng chưa dùng tính năng / không có đơn mới → im lặng
        }

        // Tên shop = tên đăng nhập tài khoản (như GSheet); fallback "TK {id}" nếu chưa đọc được email.
        var tenShop = _services.Accounts.GetById(_accountId)?.Email;
        if (string.IsNullOrWhiteSpace(tenShop))
        {
            tenShop = $"TK {_accountId}";
        }
        var luc = DateTime.Now;

        _ = Task.Run(async () =>
        {
            try
            {
                var text = OrderNotifyService.TaoTinNhanDonMoi(tenShop, insertedOrders, luc);
                var ok = await _services.Notify.SendAsync(url, text, log, ct).ConfigureAwait(false);
                if (ok)
                {
                    var kenh = OrderNotifyService.NhanDienKenh(url);
                    log($"Notify: đã báo {insertedOrders.Count} đơn mới ({kenh}).");
                }
            }
            catch (OperationCanceledException)
            {
                // Hủy chủ động (dừng phiên) — thôi.
            }
            catch (Exception ex)
            {
                // Lỗi báo đơn KHÔNG phá lượt sync (đã báo thành công) — chỉ ghi log.
                log("Notify: lỗi — " + ex.Message);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Đẩy các đơn của tài khoản này (kèm file phiếu PDF base64) lên Google Sheet qua Apps Script Web App, RỒI
    /// DỌN đơn KẾT THÚC (Đã giao / Đã hủy) khỏi app (chính sách "app chỉ giữ đơn Chuẩn bị hàng"). Gọi CHẠY NỀN
    /// (qua <see cref="StartGsheetPushInBackground"/>) SAU khi Sync đã ghi đơn vào DB + tổng kết.
    /// <b>Không bao giờ ném</b> (sync DB đã xong — lỗi GSheet chỉ ghi log): hủy chủ động → thôi; lỗi khác → log.
    /// <para>
    /// <b>URL chưa cấu hình</b> KHÔNG return sớm nữa: người dùng không dùng sheet thì coi như MỌI đơn đã "settled
    /// GSheet" nhưng vẫn phải DỌN đơn kết thúc. Chỉ đính kèm file khi phiếu tồn tại + đúng magic <c>%PDF-</c> và
    /// đơn chưa có link. Đơn đã ghi sheet mà không có gì mới → bỏ qua (không đẩy trùng) và coi là settled.
    /// </para>
    /// <para>
    /// <b>DỌN vòng đời:</b> đơn kết thúc chỉ bị XÓA khi (a) đã settled GSheet, (b) nếu Đã giao có SKU thì "Đã bán"
    /// đã đếm (<c>sold_counted_at</c>), (c) nếu hub bật thì đã đẩy hub (<c>hub_synced_at</c>) — xem
    /// <see cref="NenXoaDonKetThuc"/>. Nghi ngờ thì GIỮ (đơn thừa vô hại, đơn mất là mất dữ liệu); lượt sync sau
    /// tự đẩy + dọn tiếp. Xóa xong phát <see cref="AppServices.RaiseOrdersChanged"/> để lưới Đơn hàng vẽ lại.
    /// </para>
    /// </summary>
    private async Task PushOrdersToGsheetAsync(string? shopId, string? shopLogin, Action<string> log, CancellationToken ct)
    {
        try
        {
            // Đọc pending TRƯỚC nhánh check URL — cần cho bước DỌN kể cả khi người dùng không dùng GSheet. Mô hình
            // nhiều-shop: lọc theo shopId (chỉ đơn của shop hiện tại) — null (chưa vào loop) → mọi đơn của account.
            var pending = _services.Orders.GetForGsheetPush(_accountId, shopId);
            if (pending.Count == 0)
            {
                return; // không có đơn nào → không ghi, không dọn
            }

            var url = _services.Settings.GetGsheetWebAppUrl();
            // Hub đơn hàng đang bật? (hook đã rót — CÙNG điều kiện PushOrdersToHubAsync dùng để quyết đẩy hub.)
            var hubHookActive = _services.PushOrdersToHub is not null;

            // Cờ per-đơn: đơn đã "settled" với GSheet = đã ghi xong / không cần ghi / hủy-chưa-vận-đơn / URL trống.
            // Chỉ đơn settled mới đủ điều kiện dọn. Mã đơn so khớp Ordinal.
            var settled = new HashSet<string>(StringComparer.Ordinal);

            if (string.IsNullOrWhiteSpace(url))
            {
                // KHÔNG im lặng: máy chưa điền URL Web App thì cả buổi không ghi được dòng nào mà không ai hay
                // (sự cố thật). Chỉ log MỘT lần mỗi phiên — mỗi shop một dòng sẽ rất ồn.
                if (!_daBaoThieuGsheetUrl)
                {
                    _daBaoThieuGsheetUrl = true;
                    log($"GSheet: chưa cấu hình Web App URL — bỏ qua ghi sheet ({pending.Count} đơn chờ). Điền ở Cài đặt hoặc trên Hub.");
                }

                // Người dùng chưa dùng GSheet → coi MỌI đơn đã settled (không có nghĩa vụ ghi sheet); KHÔNG return,
                // vẫn xuống bước dọn đơn kết thúc.
                foreach (var p in pending)
                {
                    settled.Add(p.OrderSn);
                }
            }
            else
            {
                // Tên shop (cột E) = TÊN ĐĂNG NHẬP của SHOP hiện tại (mô hình nhiều-shop: vd "alina99.store"), lấy từ
                // bảng /portal/shop khi vào loop. Fallback về Account.Email khi CHƯA vào loop (shopLogin null/rỗng —
                // giữ hành vi cũ cho các đường không qua vòng lặp shop).
                var tenShop = string.IsNullOrWhiteSpace(shopLogin)
                    ? _services.Accounts.GetById(_accountId)?.Email
                    : shopLogin;

                var invoiceDir = _services.Settings.GetInvoiceFolder();
                // Đọc thời điểm MỘT LẦN cho cả lượt (ngày ghi cột + tab tự động theo tháng) — lượt vắt qua nửa
                // đêm cuối tháng vẫn nhất quán một tab.
                var now = DateTime.Now;
                var ngay = now.ToString("dd/MM/yyyy");

                // Tab đích của đơn MỚI: override ở Cài đặt (có giá trị) hoặc tự động "Tháng MM-yyyy" (override trống).
                // Đơn ĐÃ nhớ tab (p.GsheetTab) LUÔN về đúng tab cũ, bất kể override/tháng hiện tại.
                var overrideTab = _services.Settings.GetGsheetTabName();     // "" = tự động
                var autoTab = GsheetTabName.ForMonth(now);
                var defaultTab = string.IsNullOrEmpty(overrideTab) ? autoTab : overrideTab;

                // Gộp rows theo tab đích (PushAsync nhận MỘT tab/lượt). Thứ tự đơn trong mỗi nhóm giữ nguyên
                // (List theo thứ tự duyệt pending). Thường 1–2 nhóm (tab tháng hiện tại + tab đã nhớ của đơn cũ).
                var rowsByTab = new Dictionary<string, List<GsheetOrderRow>>(StringComparer.Ordinal);
                // Nhớ trạng thái hủy + đã-có-vận-đơn + đã-có-ước-tính VỪA tính của từng đơn được gửi → dùng cho
                // MarkGsheetSynced (ghi cờ gsheet_da_huy / gsheet_da_co_van_don / gsheet_da_co_uoc_tinh).
                var daHuyByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coVanDonByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coUocTinhByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var p in pending)
                {
                    var daHuy = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason);
                    var coVanDon = !string.IsNullOrWhiteSpace(p.TrackingNumber);

                    // BỎ QUA đơn HỦY mà CHƯA từng có vận đơn: đơn hủy trước khi vào pipeline giao không thuộc sổ
                    // theo dõi → không ghi (tránh spam dòng đỏ vô nghĩa). By design → coi là settled (được dọn).
                    // Đơn CHƯA hủy (đang chuẩn bị) vẫn ghi dù chưa có vận đơn (dòng TRẮNG), cột B tự điền sau.
                    if (daHuy && !coVanDon)
                    {
                        settled.Add(p.OrderSn);
                        continue;
                    }

                    string? fileName = null;
                    string? fileBase64 = null;

                    // Chỉ đính kèm file khi đơn CHƯA có link (FileUrl trống) — tránh upload lại phiếu đã có.
                    if (string.IsNullOrEmpty(p.FileUrl))
                    {
                        var safeName = ShopeeShippingNav.SanitizeFileName(p.OrderSn);
                        var path = Path.Combine(invoiceDir, safeName + ".pdf");
                        if (TryReadSlipBase64(path, log, out var b64))
                        {
                            fileName = safeName + ".pdf";
                            fileBase64 = b64;
                        }
                    }

                    // CHỌN GỬI khi thỏa ÍT NHẤT một điều kiện: (a) đơn mới với sheet; (b) có file phiếu để bổ sung
                    // link (fileBase64 chỉ set khi FileUrl null); (c) trạng thái hủy đổi so với lần đẩy trước (hoặc
                    // chưa từng đẩy) → sheet cần đổi màu; (d) vận đơn VỪA xuất hiện (đã ghi dòng lúc chưa có vận đơn,
                    // giờ có) → gửi lại để điền cột B; (e) số ước tính VỪA xuất hiện (đã ghi dòng lúc chưa mở trang
                    // chi tiết nên ô tiền còn TRỐNG, giờ có ước tính) → gửi lại để điền cột tiền.
                    // Không thỏa → bỏ qua (đã ghi đủ, không đẩy trùng) → settled.
                    var coFileBoSung = fileBase64 is not null;
                    var huyDoi = p.GsheetDaHuy is null || daHuy != (p.GsheetDaHuy == 1);
                    var vanDonMoi = coVanDon && p.GsheetDaCoVanDon != 1;
                    var coUocTinh = p.FinalAmount is not null;
                    var uocTinhMoi = coUocTinh && p.GsheetDaCoUocTinh != 1;
                    if (!(!p.DaGhiSheet || coFileBoSung || huyDoi || vanDonMoi || uocTinhMoi))
                    {
                        settled.Add(p.OrderSn);
                        continue;
                    }

                    daHuyByMaDon[p.OrderSn] = daHuy;
                    coVanDonByMaDon[p.OrderSn] = coVanDon;
                    coUocTinhByMaDon[p.OrderSn] = coUocTinh;

                    // Tab đích: tab đã nhớ của đơn (đẩy lại về đúng chỗ cũ) hoặc tab mặc định cho đơn mới.
                    var tab = string.IsNullOrEmpty(p.GsheetTab) ? defaultTab : p.GsheetTab;
                    if (!rowsByTab.TryGetValue(tab, out var tabRows))
                    {
                        tabRows = new List<GsheetOrderRow>();
                        rowsByTab[tab] = tabRows;
                    }
                    tabRows.Add(new GsheetOrderRow(
                        MaDon: p.OrderSn,
                        MaVanDon: p.TrackingNumber,
                        TenShop: tenShop,
                        // Tiền bán = "Ước tính" (số tiền cuối cùng đọc ở trang chi tiết); chưa có thì để TRỐNG
                        // (đơn hủy → tổng tiền, vì đơn hủy không bao giờ có ước tính) — xem GsheetMoney.Chon.
                        DoanhThu: GsheetMoney.Chon(p.FinalAmount, p.TotalPrice, daHuy),
                        Ngay: ngay,
                        Sku: p.Sku,
                        FileName: fileName,
                        FileBase64: fileBase64,
                        DaHuy: daHuy));
                }

                if (rowsByTab.Count == 0)
                {
                    log("GSheet: không có đơn mới cần ghi.");
                }
                else
                {
                    // PushAsync có thể ném (lỗi mạng/lô) → đơn ĐỊNH-GỬI (trong rowsByTab) coi CHƯA settled → GIỮ
                    // lại, lượt sync sau tự đẩy lại. Đơn settled-by-design ở trên VẪN được dọn. OCE (hủy) cho xuyên.
                    // Đẩy LẦN LƯỢT từng tab (thường 1–2). Một nhóm ném lỗi → catch dưới log + DỪNG các nhóm sau
                    // (mạng đang hỏng); đơn các nhóm đã gửi trước đó vẫn settled, các nhóm sau giữ chưa settled.
                    try
                    {
                        int added = 0, updated = 0, withFile = 0, errors = 0;
                        string? firstError = null;
                        foreach (var nhom in rowsByTab)
                        {
                            var tabName = nhom.Key;
                            var results = await _services.GsheetSync.PushAsync(url, tabName, nhom.Value, log, ct).ConfigureAwait(false);

                            foreach (var r in results)
                            {
                                if (r.Ok)
                                {
                                    var daHuy = daHuyByMaDon.TryGetValue(r.MaDon, out var dh) && dh;
                                    var coVanDon = coVanDonByMaDon.TryGetValue(r.MaDon, out var cv) && cv;
                                    var coUocTinh = coUocTinhByMaDon.TryGetValue(r.MaDon, out var cu) && cu;
                                    _services.Orders.MarkGsheetSynced(_accountId, r.MaDon, r.FileUrl, daHuy, coVanDon, coUocTinh, tabName, DateTime.UtcNow);
                                    settled.Add(r.MaDon); // gửi thành công → settled (đủ điều kiện dọn nếu kết thúc)
                                    if (r.Added) { added++; } else { updated++; }
                                    if (!string.IsNullOrEmpty(r.FileUrl)) { withFile++; }
                                }
                                else
                                {
                                    errors++;
                                    firstError ??= $"{r.MaDon}: {r.Error}";
                                }
                            }
                        }

                        var summary = $"GSheet: thêm {added} dòng mới, bổ sung {updated}, kèm {withFile} file phiếu.";
                        if (errors > 0)
                        {
                            summary += $" Lỗi {errors} đơn (vd {firstError}).";
                        }
                        log(summary);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // hủy chủ động → bỏ qua cả bước dọn (lượt sau làm lại)
                    }
                    catch (Exception ex)
                    {
                        // Lỗi đẩy GSheet (mạng/lô) → đơn định-gửi giữ CHƯA settled; vẫn xuống dọn đơn settled-by-design.
                        log("GSheet: lỗi — " + ex.Message);
                    }
                }
            }

            // ===== DỌN đơn KẾT THÚC (Đã giao / Đã hủy) đã hoàn tất mọi nghĩa vụ khỏi app =====
            // Thư mục phiếu để kiểm "còn phiếu local chưa đẩy hub" (giữ đơn tới khi phiếu lên hub). Đọc 1 lần.
            var slipDir = _services.Settings.GetInvoiceFolder();
            var deletable = new List<string>();
            var terminalChuaXong = 0;
            foreach (var p in pending)
            {
                var terminal = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason)
                    || ShopeeShippingNav.LaDaGiaoDaBan(p.Status);
                if (!terminal)
                {
                    continue; // đơn trung gian (Chuẩn bị hàng / Đang giao / Chờ xác nhận…) → GIỮ, theo dõi tiếp
                }
                // Còn phiếu local HỢP LỆ chưa đẩy hub (hub bật) → GIỮ đơn để lượt sau đẩy phiếu xong mới dọn.
                var coPhieuLocalChuaDayHub = hubHookActive && !p.DaDayPhieuHub
                    && SlipFileIsValidPdf(Path.Combine(slipDir, ShopeeShippingNav.SanitizeFileName(p.OrderSn) + ".pdf"));
                if (NenXoaDonKetThuc(p, settled.Contains(p.OrderSn), hubHookActive, coPhieuLocalChuaDayHub))
                {
                    deletable.Add(p.OrderSn);
                }
                else
                {
                    terminalChuaXong++;
                }
            }

            if (deletable.Count > 0)
            {
                var n = _services.Orders.DeleteOrders(_accountId, deletable);
                _services.RaiseOrdersChanged(); // lưới Đơn hàng đang mở tự vẽ lại
                log($"Dọn: đã lưu sheet & xóa {n} đơn kết thúc (Đã giao/Đã hủy) khỏi app.");
            }
            if (terminalChuaXong > 0)
            {
                log($"Dọn: {terminalChuaXong} đơn kết thúc chờ lượt sau (GSheet/hub/đếm chưa xong).");
            }
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động — thôi (sync DB đã xong; lượt sync sau tự đẩy + dọn lại nhờ cờ DB).
        }
        catch (Exception ex)
        {
            // Lỗi bất ngờ KHÔNG phá lượt sync (đã báo thành công) — chỉ ghi log.
            log("GSheet: lỗi — " + ex.Message);
        }
    }

    /// <summary>
    /// HÀM THUẦN (test được) quyết định một đơn KẾT THÚC có được XÓA khỏi app chưa. Trả true khi:
    /// <list type="bullet">
    /// <item>đơn KẾT THÚC — <c>LaDonHuy</c> (Đã hủy) hoặc <c>LaDaGiaoDaBan</c> (Đã giao); VÀ</item>
    /// <item><paramref name="gsheetSettled"/> — đã ghi sheet xong / không cần ghi / URL trống; VÀ</item>
    /// <item>KHÔNG (Đã giao + có SKU + chưa đếm "Đã bán") — nghĩa là đếm sold còn NULL thì GIỮ để lượt sau +1
    /// (xóa sớm là mất đếm); VÀ</item>
    /// <item>KHÔNG (hub bật + chưa đẩy hub) — hub đang nhận đơn mà đơn chưa <c>hub_synced_at</c> thì GIỮ, kẻo
    /// hub mất đơn.</item>
    /// <item>KHÔNG <paramref name="coPhieuLocalChuaDayHub"/> — còn file phiếu local HỢP LỆ chưa đẩy lên hub (hub
    /// đang bật) thì GIỮ, đợi phiếu lên hub xong (đẩy xong lượt sau mới dọn).</item>
    /// </list>
    /// Đơn trung gian (chưa kết thúc) hoặc chưa settled → false (GIỮ). Nghi ngờ thì GIỮ — đơn thừa vô hại.
    /// <paramref name="coPhieuLocalChuaDayHub"/> do caller tính: hub bật + <c>!p.DaDayPhieuHub</c> + file phiếu
    /// local hợp lệ tồn tại. File local KHÔNG tồn tại → false (không giữ vì phiếu, như cũ).
    /// </summary>
    internal static bool NenXoaDonKetThuc(GsheetPendingOrder p, bool gsheetSettled, bool hubHookActive, bool coPhieuLocalChuaDayHub)
    {
        var terminal = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason)
            || ShopeeShippingNav.LaDaGiaoDaBan(p.Status);
        return terminal
            && gsheetSettled
            && (!ShopeeShippingNav.LaDaGiaoDaBan(p.Status) || string.IsNullOrWhiteSpace(p.Sku) || p.DaDemDaBan)
            && (!hubHookActive || p.DaDayHub)
            && !coPhieuLocalChuaDayHub;
    }

    /// <summary>
    /// Đọc file phiếu <paramref name="path"/> thành base64 nếu HỢP LỆ: tồn tại, ≤ 5MB, và 5 byte đầu là
    /// <c>%PDF-</c> (kiểm magic — bài học cũ: đừng tin đuôi file, GET lại phiếu có thể ra HTML 200-OK). File
    /// quá lớn → log 1 dòng + bỏ qua. Mọi lỗi đọc → false. Trả true + base64 khi hợp lệ.
    /// </summary>
    private static bool TryReadSlipBase64(string path, Action<string> log, out string? base64)
    {
        base64 = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length > MaxSlipBytes)
            {
                log($"GSheet: file phiếu quá lớn (>{MaxSlipBytes / (1024 * 1024)}MB), bỏ qua: {Path.GetFileName(path)}");
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            if (!BytesLookPdf(bytes))
            {
                return false; // không phải PDF thật → không gửi rác
            }

            base64 = Convert.ToBase64String(bytes);
            return true;
        }
        catch
        {
            return false; // lỗi đọc file → bỏ qua, không phá luồng
        }
    }

    /// <summary>True nếu 5 byte đầu là magic <c>%PDF-</c> — nhận đúng file PDF thật, tránh coi HTML/redirect
    /// (GET lại phiếu có thể ra HTML 200-OK) là phiếu. Dùng chung cho <see cref="TryReadSlipBase64"/> và
    /// <see cref="SlipFileIsValidPdf"/>.</summary>
    private static bool BytesLookPdf(ReadOnlySpan<byte> b)
        => b.Length >= 5 && b[0] == (byte)'%' && b[1] == (byte)'P'
           && b[2] == (byte)'D' && b[3] == (byte)'F' && b[4] == (byte)'-';

    /// <summary>
    /// True nếu file phiếu <paramref name="path"/> TỒN TẠI và là PDF thật (5 byte đầu <c>%PDF-</c>). Đọc TỐI ĐA
    /// 5 byte đầu (nhẹ, gọi được cho mỗi dòng lưới) — KHÔNG áp trần dung lượng (chỉ kiểm tồn tại + magic, đúng
    /// định nghĩa "có phiếu"). Mọi lỗi IO → <c>false</c>. Dùng cho <see cref="ThieuPhieu"/> (tự động khi sync) và
    /// <c>OrderRowViewModel.HasSlipFile</c> (nút "Tải phiếu").
    /// </summary>
    internal static bool SlipFileIsValidPdf(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[5];
            var n = fs.Read(head);
            return BytesLookPdf(head[..Math.Max(0, n)]);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// PURE — True khi đơn <b>THIẾU PHIẾU</b> (cần tải lại): trạng thái là "Chuẩn bị hàng"
    /// (<see cref="ShopeeShippingNav.LaChuanBiHang"/>) VÀ ĐÃ có mã vận đơn (<paramref name="trackingNumber"/>
    /// khác rỗng — tức arrange đã xong, phiếu đáng lẽ phải có) VÀ file <paramref name="pdfPath"/> KHÔNG tồn tại
    /// hoặc KHÔNG phải PDF thật (<see cref="SlipFileIsValidPdf"/>). Đơn CHƯA có vận đơn KHÔNG tính (phiếu sẽ
    /// được tạo ở bước Xử lý đơn). Dùng chung cho luồng tự-động-khi-sync và hiển thị nút "Tải phiếu".
    /// </summary>
    internal static bool ThieuPhieu(string? status, string? trackingNumber, string pdfPath)
        => ShopeeShippingNav.LaChuanBiHang(status)
           && !string.IsNullOrWhiteSpace(trackingNumber)
           && !SlipFileIsValidPdf(pdfPath);

    /// <summary>
    /// <b>GĐ4 — Luồng chạy nền của nút "▶ Chạy" (đường CẦU NỐI extension, chạy LIÊN TỤC).</b> Mỗi chu kỳ:
    /// <see cref="OrdersBridgeSession.RunAllShopsAsync"/> (login Playwright → đóng → clean+extension → SSO picker →
    /// LẶP mọi shop: đọc đơn → callback <see cref="PersistSyncedOrdersAsync"/> lưu DB/GSheet/hub → nếu có đơn chờ
    /// thì Chuẩn bị hàng + in phiếu + revert địa chỉ → đóng tab shop) → nghỉ <c>GetOrderIntervalMinutes()</c> → lặp.
    /// Dừng (ct hủy) đóng cả trình duyệt điều khiển (finally trong bridge) lẫn trình duyệt sạch (Stop kill _bridge.Process).
    /// Cap cứng 12h. KHÔNG dùng proxy (bridge mở sạch, không CDP).
    /// </summary>
    private async Task RunBridgeContinuousAsync(CancellationToken ct)
    {
        var acc = _services.Accounts.GetById(_accountId);
        if (!string.IsNullOrWhiteSpace(acc?.Email))
        {
            _logLabel = acc!.Email;
        }
        var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));

        if (acc is null)
        {
            SetError("Không đọc được tài khoản (đã bị xóa?).");
            return;
        }

        try
        {
            var baseDir = Path.GetDirectoryName(_services.Database.Path) ?? ".";
            var browserChoice = _services.Settings.GetBrowserChoice();
            var browserKind = BrowserLocator.ResolveBrowserKind(browserChoice);
            var userDataDir = BrowserProfilePaths.ForAccount(baseDir, _accountId, browserKind);
            Directory.CreateDirectory(userDataDir);

            var invoiceDir = _services.Settings.GetInvoiceFolder();
            var province = string.IsNullOrWhiteSpace(acc.PickupAddress)
                ? AccountsViewModel.DefaultPickupAddress
                : acc.PickupAddress!;
            var intervalMin = Math.Max(1, _services.Settings.GetOrderIntervalMinutes());
            var login = new OrdersLoginParams(acc.Email, acc.Password, acc.VerifyEmail, acc.VerifyEmailPassword);

            // Callback lưu DB/GSheet/hub cho MỖI shop (thao tác thuần DTO — dùng chung với đường Playwright).
            Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task> syncCallback =
                (shopId, shopLogin, orders, c) =>
                {
                    // Rót shop-context để GSheet lấy đúng Tên Shop + cleanup/GSheet scope theo shop (giống vòng Playwright).
                    _currentShopId = shopId;
                    _currentShopLogin = string.IsNullOrWhiteSpace(shopLogin) ? null : shopLogin;
                    return PersistSyncedOrdersAsync(shopId, orders, log, c);
                };

            var hardCap = DateTime.UtcNow.AddHours(12);
            SetStatus(SessionState.Running, "Đang chạy (cầu nối extension) — đăng nhập + duyệt mọi shop...");

            while (!ct.IsCancellationRequested && DateTime.UtcNow < hardCap)
            {
                var bridge = new OrdersBridgeSession(userDataDir, browserChoice, log, invoiceDir, province, syncCallback,
                    finalDoneSns: () => _services.Orders.GetOrderSnsWithFinalAmount(_accountId),
                    // Tab "Kết quả": lưu danh sách shop + tăng đếm mỗi đơn arrange theo (tài khoản, shop, ngày yyyy-MM-dd giờ địa phương).
                    onShopListRead: shops => _services.Results.UpsertShops(_accountId, shops),
                    onOrderPrepared: shopLogin => _services.Results.IncrementPrepared(
                        _accountId, shopLogin, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
                _bridge = bridge;
                OrdersBridgeRunResult result;
                try
                {
                    result = await bridge.RunAllShopsAsync(login, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Đóng cửa sổ sạch của vòng này + giải phóng cổng cầu nối (vòng sau mở phiên mới).
                    try { var p = bridge.Process; if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
                    try { bridge.Dispose(); } catch { /* bỏ qua */ }
                    _bridge = null;
                }

                _readyForActions = true; // đã chạy xong ít nhất 1 vòng → nút phụ thuộc phiên mở

                if (result.Captcha)
                {
                    log("Gặp captcha/verify — dừng vòng này, sẽ thử lại sau khi nghỉ.");
                }
                else if (result.Error is not null)
                {
                    log("Vòng cầu nối chưa trọn: " + result.Error);
                }
                else
                {
                    SetStatus(SessionState.Running,
                        $"Vòng xong: {result.ShopsDone}/{result.ShopCount} shop, {result.TotalOrders} đơn, {result.TotalSlips} phiếu — nghỉ {intervalMin}'.");
                }

                // Nghỉ interval trước chu kỳ kế (hủy giữa chừng → thoát vòng).
                try { await Task.Delay(TimeSpan.FromMinutes(intervalMin), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException)
        {
            // Dừng chủ động — không phải lỗi.
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            _readyForActions = false;
            // Chốt chặn: kill trình duyệt sạch nếu còn (vòng bị ngắt giữa chừng), giải phóng cổng.
            try { var p = _bridge?.Process; if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
            try { _bridge?.Dispose(); } catch { /* bỏ qua */ }
            _bridge = null;

            lock (_lifecycleLock)
            {
                if (State != SessionState.Error)
                {
                    State = SessionState.Stopped;
                }
            }
        }
    }

    private void SetStatus(SessionState state, string text)
    {
        StatusText = text;
        State = state;
        _services.Log.Append(_logLabel, text);
    }

    private void SetError(string message)
    {
        _readyForActions = false; // lỗi → không còn sẵn sàng (nút Sync/Kiểm tra sẽ tự mở/khởi động lại phiên)
        LastError = message;
        StatusText = message;
        State = SessionState.Error;
        _services.Log.Append(_logLabel, "LỖI: " + message);
    }

    /// <summary>
    /// Ghi cookie JSON vào ĐÚNG tài khoản của phiên (thread nền — SQLite an toàn) rồi phát
    /// <see cref="CookieSaved"/> để VM làm mới danh sách trên UI thread. Trả true nếu đã ghi.
    /// </summary>
    private bool TrySaveCookie(string cookieJson)
    {
        if (CookieJson.Deserialize(cookieJson).Count == 0)
        {
            return false; // JSON không chứa cookie nào
        }

        var acc = _services.Accounts.GetById(_accountId);
        if (acc is null)
        {
            return false; // tài khoản đã bị xóa
        }

        acc.Cookie = cookieJson;
        _services.Accounts.Update(acc);

        // VM nghe sự kiện này để dựng lại danh sách (instance trong Accounts có cookie mới) trên UI thread.
        CookieSaved?.Invoke(_accountId);
        return true;
    }
}
