using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    // ===== Khóa chạy tài khoản XUYÊN MÁY (chống 2 máy cùng chạy 1 subaccount: tranh đơn "chuẩn bị hàng" +
    // đăng nhập song song một tài khoản Shopee) =====
    // 1 = vòng chạy này ĐANG giữ khóa → đường dọn dẹp PHẢI nhả. Interlocked (không phải bool) để nhả ĐÚNG MỘT
    // lần dù nhiều lối ra cùng gọi tới. Nhả sót = tài khoản bị coi là "đang chạy ở máy X" tới khi lease hết hạn
    // (~5') → máy khác không chạy được.
    private int _dangGiuKhoa;

    // Hậu xử lý một lượt cầu nối (ghi DB → GSheet/hub → notify → dọn đơn kết thúc). Sống cùng vòng đời phiên vì
    // nó giữ shop-context của lượt đang chạy + cờ chống spam log GSheet. Xem OrderPersistPipeline.
    private readonly OrderPersistPipeline _persist;

    public AccountSession(
        long accountId,
        AppServices services)
    {
        _accountId = accountId;
        _services = services;
        _logLabel = $"TK {accountId}";
        _persist = new OrderPersistPipeline(accountId, services);
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
            StatusText = State == SessionState.Queued
                ? "Tài khoản này đang chờ đến lượt, chưa thể tải lại phiếu."
                : "Hãy bấm Chạy tài khoản rồi mới tải lại phiếu.";
            _services.Log.Append(_logLabel,
                $"Tải lại phiếu đơn {orderSn}: phiên chưa chạy hoặc chưa tới lượt.");
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
            log("Lỗi khi tải lại phiếu: " + ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// PURE — câu báo khi tài khoản đang được MÁY KHÁC chạy: có tên máy thì nói TÊN (người dùng biết chỗ nào đang
    /// chạy để tắt), hub không nói được là máy nào thì "máy khác". Dùng cho cả nhật ký lẫn dòng trạng thái phiên.
    /// </summary>
    internal static string CauBiTuChoiKhoa(string? holderMachine)
        => string.IsNullOrWhiteSpace(holderMachine)
            ? "Tài khoản đang chạy ở máy khác — bỏ qua lượt này."
            : $"Tài khoản đang chạy ở máy {holderMachine} — bỏ qua lượt này.";

    /// <summary>
    /// XIN khóa chạy tài khoản (hook <see cref="AppServices.AcquireAccountLease"/>) — gọi TRƯỚC khi mở trình duyệt
    /// và trước khi chuyển sang <see cref="SessionState.Running"/>. Trả <c>true</c> = được phép chạy:
    /// <list type="bullet">
    /// <item>hook chưa rót (app chạy độc lập / chưa có hub) → chạy như trước, không log gì thêm;</item>
    /// <item>hook trả <c>Ok</c> → đánh cờ <see cref="_dangGiuKhoa"/> để đường dọn dẹp nhả khóa.</item>
    /// </list>
    /// Trả <c>false</c> = MÁY KHÁC đang chạy tài khoản này → phiên BỎ QUA lượt (không xếp hàng chờ, không thử lại),
    /// ghi nhật ký + dòng trạng thái theo <see cref="CauBiTuChoiKhoa"/>.
    /// </summary>
    internal async Task<bool> XinKhoaChayAsync(string login, Action<string> log, CancellationToken ct)
    {
        var acquire = _services.AcquireAccountLease;
        if (acquire is null)
        {
            return true; // không có hub → hành vi y như trước
        }

        var kq = await acquire(login, ct).ConfigureAwait(false);
        if (!kq.Ok)
        {
            var msg = CauBiTuChoiKhoa(kq.HolderMachine);
            StatusText = msg;
            log(msg);
            return false;
        }

        Interlocked.Exchange(ref _dangGiuKhoa, 1); // đã giữ → finally của vòng chạy PHẢI nhả
        return true;
    }

    /// <summary>
    /// NHẢ khóa chạy tài khoản (hook <see cref="AppServices.ReleaseAccountLease"/>) — gọi ở MỌI lối ra của vòng
    /// chạy (xong / lỗi / hủy). ĐÚNG MỘT lần nhờ <see cref="_dangGiuKhoa"/>: chưa từng giành được (hook null / bị
    /// từ chối) hoặc đã nhả rồi → không gọi hook. Nuốt mọi lỗi: nhả hỏng KHÔNG được phá đường dọn dẹp của phiên
    /// (lease tự hết hạn ~5' phía hub).
    /// </summary>
    internal async Task NhaKhoaChayAsync(string login)
    {
        if (Interlocked.Exchange(ref _dangGiuKhoa, 0) != 1)
        {
            return; // chưa từng giành được / đã nhả
        }

        var release = _services.ReleaseAccountLease;
        if (release is null)
        {
            return;
        }

        try { await release(login).ConfigureAwait(false); }
        catch (Exception ex) { _services.Log.Append(_logLabel, "Nhả khóa tài khoản lỗi: " + ex.ToString()); }
    }

    /// <summary>
    /// <b>GĐ4 — Luồng chạy nền của nút "▶ Chạy" (đường CẦU NỐI extension, chạy LIÊN TỤC).</b> Mỗi chu kỳ:
    /// <see cref="OrdersBridgeSession.RunAllShopsAsync"/> (login Playwright → đóng → clean+extension → SSO picker →
    /// LẶP mọi shop: đọc đơn → callback <see cref="OrderPersistPipeline.PersistSyncedOrdersAsync"/> lưu DB/GSheet/hub → nếu có đơn chờ
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
            // KHÓA CHẠY XUYÊN MÁY: xin TRƯỚC khi đụng trình duyệt và trước khi chuyển sang Running. Máy khác đang
            // chạy tài khoản này → bỏ qua lượt (không xếp hàng chờ) và kết thúc ÊM như người dùng bấm Dừng
            // (finally đặt Stopped). Không có hub → XinKhoaChayAsync trả true (degrade như một máy).
            if (!await XinKhoaChayAsync(acc.Email, log, ct).ConfigureAwait(false))
            {
                return;
            }

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
                    _persist.SetShopContext(shopId, shopLogin);
                    return _persist.PersistSyncedOrdersAsync(shopId, orders, log, c);
                };

            var hardCap = DateTime.UtcNow.AddHours(12);
            SetStatus(SessionState.Running, "Đang chạy (cầu nối extension) — đăng nhập + duyệt mọi shop...");

            while (!ct.IsCancellationRequested && DateTime.UtcNow < hardCap)
            {
                var bridge = new OrdersBridgeSession(userDataDir, browserChoice, log, invoiceDir, province, syncCallback,
                    finalDoneSns: () => _services.Orders.GetOrderSnsWithFinalAmount(_accountId),
                    // Tab "Kết quả": lưu danh sách shop + tăng đếm mỗi đơn arrange theo (tài khoản, shop, ngày yyyy-MM-dd giờ địa phương).
                    onShopListRead: shops =>
                    {
                        _services.Results.UpsertShops(_accountId, shops);
                        // Báo tab "Kết quả" dựng lưới NGAY. Thiếu dòng này thì màn đang mở giữ nguyên kết quả
                        // rỗng của lần nạp đầu (mở app + chọn tài khoản TRƯỚC khi phiên đọc được shop).
                        _services.RaiseShopListChanged(_accountId);
                    },
                    onOrderPrepared: (shopLogin, orderSn) =>
                    {
                        // Đếm CỤC BỘ (dự phòng khi mất hub) — giữ nguyên như cũ.
                        _services.Results.IncrementPrepared(
                            _accountId, shopLogin, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                        // Đếm CHUNG toàn hệ thống: đánh dấu CHÍNH ĐƠN đó đã chuẩn bị hàng lúc nào → lượt đẩy hub kế
                        // mang lên, hub đếm từ bảng đơn (mỗi đơn đúng 1 dòng) nên nhiều máy cùng chạy vẫn ra số thật.
                        // Mã đơn rỗng (extension không trả được) → bỏ qua, vẫn +1 đếm cục bộ như cũ.
                        if (!string.IsNullOrWhiteSpace(orderSn))
                        {
                            _services.Orders.MarkPrepared(_accountId, orderSn, DateTime.UtcNow);
                        }
                        // Báo tab "Kết quả" đang mở tự nạp lại → số nhảy NGAY sau mỗi đơn, không phải đợi
                        // đổi tài khoản / đổi ngày mới thấy.
                        _services.RaisePrepareCountChanged(_accountId);
                    },
                    // Cột tiến độ tab "Kết quả": chấm tròn + vòng quay chạy theo shop mà vòng này đang check tới.
                    onShopCheckStarted: shopLabel => _services.RaiseShopCheckChanged(_accountId, shopLabel, checking: true),
                    onShopCheckFinished: shopLabel => _services.RaiseShopCheckChanged(_accountId, shopLabel, checking: false),
                    // Bước CUỐI flow shop — check đơn trả hàng: mốc "số yêu cầu" nhớ THEO SHOP (account_shops), mã
                    // yêu cầu ghi vào chính đơn (orders.return_request_code) rồi cờ DB lo đẩy GSheet/hub lượt kế.
                    returnCountLast: shopLabel => _services.Results.GetReturnCount(_accountId, shopLabel),
                    saveReturnCount: (shopLabel, so) => _services.Results.SetReturnCount(_accountId, shopLabel, so),
                    // Ghi kho mã + vào đơn, notify phần Hub không tự biết — xem OrderPersistPipeline.LuuMaTraHang.
                    saveReturnCodes: cap => _persist.LuuMaTraHang(cap, log, ct));
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
                else if (result.PickupAddressFailed)
                {
                    // Vòng ĐÃ dừng trong bridge (không in phiếu nào cho shop lỗi, bỏ luôn shop còn lại). Ở đây chỉ
                    // báo người trực — gửi được hay không KHÔNG ảnh hưởng việc dừng.
                    log("⛔ " + result.Error + " Sửa địa chỉ trên Shopee rồi chạy lại — sẽ thử lại sau khi nghỉ.");
                    _persist.StartCanhBaoDiaChiInBackground(result.PickupFailedShop, province, log, ct);
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
            ResetBridgeState();

            // Nhả khóa chạy tài khoản trên MỌI lối ra (xong / lỗi / hủy) — đúng MỘT lần, và không nhả khi chưa
            // từng giành được. Nhả TRƯỚC khi đặt Stopped: manager thấy Stopped là start ngay account kế trong hàng
            // đợi, khóa phải trống trước lúc đó (bấm Dừng rồi Chạy lại cùng tài khoản cũng vậy).
            await NhaKhoaChayAsync(acc.Email).ConfigureAwait(false);

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

    private void ResetBridgeState()
    {
        _readyForActions = false;
        _bridge = null;
    }

    private void SetError(string message)
    {
        ResetBridgeState();
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
