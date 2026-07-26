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
    // gắn shop_id này vào đơn khi upsert; HubOutbox.PushOrdersToGsheetAsync lọc đơn theo shop + lấy Tên Shop = tên đăng nhập.
    // volatile: RunAsync (thread nền) đặt, lượt đẩy GSheet nền đọc (nhưng đã CHỤP giá trị lúc kích hoạt để tránh đua).
    private volatile string? _currentShopId;
    private volatile string? _currentShopLogin;

    // Cờ chống spam log "chưa cấu hình GSheet": phiên chạy cả buổi, mỗi shop một lượt đẩy sheet → chỉ báo 1 dòng
    // cho cả phiên là đủ để người dùng thấy máy đang KHÔNG ghi sheet. volatile: lượt đẩy chạy trên thread nền.
    private volatile bool _daBaoThieuGsheetUrl;

    /// <summary>Cờ "được phép báo THIẾU URL Web App lần này" truyền cho
    /// <see cref="HubOutbox.PushOrdersToGsheetAsync"/>: trả true ĐÚNG một lần cho mỗi phiên (xem
    /// <see cref="_daBaoThieuGsheetUrl"/>), các lần sau false.</summary>
    private bool NenBaoThieuGsheetUrl()
    {
        if (_daBaoThieuGsheetUrl)
        {
            return false;
        }
        _daBaoThieuGsheetUrl = true;
        return true;
    }

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

    /// <summary>
    /// Kích hoạt đẩy GSheet CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết. KHÔNG await trong luồng sync
    /// vì push chỉ đụng DB + file + HTTP, không đụng trình duyệt → chạy
    /// song song được với nhịp đọc "Chờ Lấy Hàng"/Xử lý đơn. <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống
    /// 2 lượt đẩy chồng nhau — cả khi lượt kia do <see cref="HubOutboxWorker"/> kích hoạt: lượt trước còn chạy →
    /// bỏ qua, log 1 dòng (lượt sau tự đẩy phần thiếu nhờ cờ DB). <paramref name="ct"/> là token phiên → dừng
    /// phiên thì lượt đẩy tự hủy (worker sẽ nhặt lại phần còn tồn).
    /// <see cref="HubOutbox.PushOrdersToGsheetAsync"/> đã tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartGsheetPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (!PushGate.TryEnter(_accountId, PushKind.Gsheet))
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
            try
            {
                await HubOutbox.PushOrdersToGsheetAsync(
                    _accountId, _services, shopId, shopLogin,
                    NenBaoThieuGsheetUrl, imLangKhiKhongCoDonMoi: false, log, ct).ConfigureAwait(false);
            }
            finally { PushGate.Exit(_accountId, PushKind.Gsheet); }
        }, CancellationToken.None);
    }

    /// <summary>Kích thước LÔ tối đa mỗi lần đẩy đơn lên hub — chia nhỏ để không nghẽn tunnel; timeout 5' của
    /// <c>_bulkHttp</c> phía hub-client đủ rộng cho một lô.</summary>
    public const int HubPushBatchSize = 200;

    /// <summary>
    /// Kích hoạt đẩy đơn lên HUB đơn hàng CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartGsheetPushInBackground"/>. <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống 2 lượt đẩy
    /// chồng nhau — cả khi lượt kia do <see cref="HubOutboxWorker"/> kích hoạt: lượt trước còn chạy → bỏ qua, log
    /// 1 dòng (lượt sau tự đẩy phần thiếu nhờ cờ DB <c>hub_synced_at</c>). <paramref name="ct"/> là token phiên →
    /// dừng phiên thì lượt đẩy tự hủy (worker sẽ nhặt lại phần còn tồn).
    /// <see cref="HubOutbox.PushOrdersToHubAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartHubPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (!PushGate.TryEnter(_accountId, PushKind.Hub))
        {
            log("Hub: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await HubOutbox.PushOrdersToHubAsync(_accountId, _services, log, ct).ConfigureAwait(false); }
            finally { PushGate.Exit(_accountId, PushKind.Hub); }
            // PHIẾU đẩy SAU khi ĐƠN đã lên hub (hub_synced_at set) — KHÔNG chạy song song với đẩy đơn: khi mã vận đơn
            // vừa xuất hiện, UpsertMany RESET hub_synced_at về NULL để re-push đơn; nếu đẩy phiếu song song, nó đọc
            // GetForHubSlipPush (đòi đơn ĐÃ hub-synced) TRÚNG lúc hub_synced_at đang NULL → bỏ sót phiếu. Tuần tự thì
            // tới lượt phiếu, đơn đã re-push xong (hub_synced_at set lại) → phiếu khớp đơn trên hub.
            StartHubSlipPushInBackground(log, ct);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Kích hoạt +1 "Đã bán" theo SKU lên HUB CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartHubPushInBackground"/>. <paramref name="skus"/> = SKU các đơn VỪA chuyển sang đã-giao trong
    /// lượt này (có SKU); <paramref name="orderSns"/> = mã đơn tương ứng để đánh cờ SAU khi hub +1 OK. Không có SKU
    /// nào → return ngay (không chiếm chỗ ở gate). <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống 2 lượt chồng
    /// nhau — ĐẶC BIỆT quan trọng với loại này: phiên và <see cref="HubOutboxWorker"/> cùng +1 một đơn = <b>+2</b>
    /// sai số liệu kho. <paramref name="ct"/> là token phiên → dừng phiên thì lượt +1 tự hủy (worker đếm bù sau).
    /// <see cref="HubOutbox.IncrementSoldBySkuAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartSoldCountInBackground(
        IReadOnlyList<string> skus, IReadOnlyList<string> orderSns, Action<string> log, CancellationToken ct)
    {
        if (skus is null || skus.Count == 0)
        {
            return; // không có đơn chuyển-sang-đã-giao có SKU → không +1 (grandfather đã đánh cờ ở luồng chính)
        }
        if (!PushGate.TryEnter(_accountId, PushKind.SoldCount))
        {
            log("Đã bán: lượt +1 trước còn đang chạy — bỏ qua (lượt sync sau tự đếm phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await HubOutbox.IncrementSoldBySkuAsync(_accountId, _services, skus, orderSns, log, ct).ConfigureAwait(false); }
            finally { PushGate.Exit(_accountId, PushKind.SoldCount); }
        }, CancellationToken.None);
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

    /// <summary>
    /// Kích hoạt đẩy FILE PHIẾU lên HUB CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartHubPushInBackground"/>. <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống 2 lượt đẩy chồng
    /// nhau — cả khi lượt kia do <see cref="HubOutboxWorker"/> kích hoạt: lượt trước còn chạy → bỏ qua, log 1 dòng
    /// (lượt sau tự đẩy phần thiếu nhờ cờ DB <c>hub_slip_synced_at</c>). <paramref name="ct"/> là token phiên →
    /// dừng phiên thì lượt đẩy tự hủy (worker sẽ nhặt lại phần còn tồn).
    /// <see cref="HubOutbox.PushSlipsToHubAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartHubSlipPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (!PushGate.TryEnter(_accountId, PushKind.HubSlip))
        {
            log("Hub phiếu: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await HubOutbox.PushSlipsToHubAsync(_accountId, _services, log, ct).ConfigureAwait(false); }
            finally { PushGate.Exit(_accountId, PushKind.HubSlip); }
        }, CancellationToken.None);
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
    /// <para><c>internal</c> (không private) vì các thân đẩy đã dời sang <see cref="HubOutbox"/> — vẫn cùng
    /// assembly, không lộ ra ngoài module.</para>
    /// </summary>
    internal static bool TryReadSlipBase64(string path, Action<string> log, out string? base64)
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
                    onOrderPrepared: shopLogin =>
                    {
                        _services.Results.IncrementPrepared(
                            _accountId, shopLogin, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                        // Báo tab "Kết quả" đang mở tự nạp lại → số nhảy NGAY sau mỗi đơn, không phải đợi
                        // đổi tài khoản / đổi ngày mới thấy.
                        _services.RaisePrepareCountChanged(_accountId);
                    },
                    // Cột tiến độ tab "Kết quả": chấm tròn + vòng quay chạy theo shop mà vòng này đang check tới.
                    onShopCheckStarted: shopLabel => _services.RaiseShopCheckChanged(_accountId, shopLabel, checking: true),
                    onShopCheckFinished: shopLabel => _services.RaiseShopCheckChanged(_accountId, shopLabel, checking: false));
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
