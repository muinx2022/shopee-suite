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
    private readonly ShopeeLoginService _loginService;
    private readonly IProxyHealthChecker _healthChecker;

    // Round-robin BỀN cho proxy thủ công được CHIA SẺ giữa các phiên (do manager giữ chỉ số) → nhiều tài
    // khoản trải đều trên danh sách proxy thay vì cùng nhận proxy đầu tiên.
    private readonly Func<IReadOnlyList<ProxyEntry>, ProxyEntry?> _nextManualProxy;

    // Cấp/nhả API key KiotProxy từ POOL CHUNG do manager quản (ưu tiên key rảnh; "rảnh/bận" theo phiên đang
    // chạy). Acquire MỘT LẦN khi chọn proxy (giữ key suốt đời phiên, kể cả relaunch đổi IP); Release ở finally
    // NGOÀI cùng khi phiên đóng hẳn → key rảnh lại cho phiên khác. Do manager cấp qua factory (giống _nextManualProxy).
    private readonly Func<long, string?> _acquireKiotKey;
    private readonly Action<long> _releaseKiotKey;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile ILoginSession? _session;

    // Bật trong lúc đang XỬ LÝ ĐƠN (điều hướng kiểu người). Khi bật, vòng RunAsync KHÔNG chạy nhịp đọc đơn
    // (ReadToShipCountAsync có reload trang → sẽ phá thao tác điều hướng đang chạy giữa chừng).
    private volatile bool _navigating;

    // Cờ "SẴN SÀNG THAO TÁC" TƯỜNG MINH: chỉ true SAU khi (của lần mở/relaunch HIỆN TẠI) đã tự-đăng-nhập
    // xong VÀ đọc được số "Chờ Lấy Hàng" lần đầu — điểm CHẮC CHẮN trang chủ đã đăng nhập & ổn định, luồng
    // chuột tự-đăng-nhập đã xong. Nút Sync/Kiểm tra (AccountsViewModel) CHỜ cờ này rồi mới điều hướng để
    // KHÔNG giẫm lên login. Đặt LẠI false ở đầu MỖI vòng mở/relaunch + khi phát hiện đổi proxy + khi
    // Stopped/Error → kín mọi ca restart/relaunch/đang-login, KHÔNG bị "sẵn sàng ảo" do số đơn cũ còn sót
    // (ToShipCount không reset khi relaunch). volatile: đọc từ UI thread trong lúc phiên chạy nền ghi.
    private volatile bool _readyForActions;

    // Proxy đang NƯỚNG vào Brave (đặt lúc launch qua --proxy-server) — watchdog kiểm cái này còn sống không.
    private volatile ProxyEntry? _currentProxy;

    // Client nguồn KiotProxy của phiên (null nếu phiên KHÔNG dùng KiotProxy: proxy thủ công / IP máy) →
    // watchdog CHỈ chạy khi client này != null.
    private volatile IKiotProxyClient? _kiotClient;

    // Chờ trước lần kiểm lại (xác nhận proxy chết lần 2) để chống false-negative khi mạng chập chờn.
    private const int ProxyRecheckDelayMs = 5000;

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

    // TRUE khi vòng lặp shop của RunAsync đang chạy → nút thủ công (VM) bỏ qua để KHÔNG giẫm luồng. Đặt lại false
    // ở finally của vòng lặp. volatile: UI thread đọc trong lúc phiên chạy nền ghi.
    private volatile bool _shopLoopRunning;

    public AccountSession(
        long accountId,
        AppServices services,
        ShopeeLoginService loginService,
        IProxyHealthChecker healthChecker,
        Func<IReadOnlyList<ProxyEntry>, ProxyEntry?> nextManualProxy,
        Func<long, string?> acquireKiotKey,
        Action<long> releaseKiotKey)
    {
        _accountId = accountId;
        _services = services;
        _loginService = loginService;
        _healthChecker = healthChecker;
        _nextManualProxy = nextManualProxy;
        _acquireKiotKey = acquireKiotKey;
        _releaseKiotKey = releaseKiotKey;
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

    public Process? BraveProcess => _session?.BraveProcess;

    /// <summary>
    /// True khi phiên đã "sẵn sàng thao tác" (của lần mở HIỆN TẠI đã đăng nhập xong + đọc số đơn lần đầu) —
    /// VM chờ cờ này rồi mới chạy Sync/Kiểm tra để không giẫm luồng tự-đăng-nhập. Xem <see cref="_readyForActions"/>.
    /// </summary>
    public bool ReadyForActions => _readyForActions;

    /// <summary>True khi vòng lặp shop (mô hình 1 subaccount = nhiều shop) đang chạy — VM dùng để BỎ QUA thao tác
    /// tay (Sync/Kiểm tra) tránh giẫm luồng. Xem <see cref="_shopLoopRunning"/>.</summary>
    public bool IsShopLoopRunning => _shopLoopRunning;

    public event Action? Changed;
    public event Action<long>? CookieSaved;

    public Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            // Idempotent: đang chuẩn bị / đang chạy → bỏ qua (không mở trùng cùng một tài khoản).
            if (State is SessionState.Opening or SessionState.Running)
            {
                return Task.CompletedTask;
            }

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            LastError = null;
            _readyForActions = false; // phiên mới khởi động → CHƯA sẵn sàng (chờ login + đọc số lần đầu)
            State = SessionState.Opening;
            _runTask = Task.Run(() => RunAsync(ct));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? run;
        lock (_lifecycleLock)
        {
            cts = _cts;
            run = _runTask;
        }

        // Phản hồi cho người dùng; GIỮ State=Running để nút "Mở" còn khóa tới khi Brave chết thật (Lỗi 2).
        if (State is SessionState.Opening or SessionState.Running)
        {
            StatusText = "Đang dừng...";
        }

        try { cts?.Cancel(); } catch { /* bỏ qua */ }

        if (run is not null)
        {
            // Chờ vòng lặp thoát & dispose (kill Brave) trong ~8s.
            try { await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false); }
            catch { /* bỏ qua */ }
        }

        // Phòng hờ: nếu vì lý do gì Brave còn sống thì kill cả cây tiến trình (không để mồ côi giữ khóa hồ sơ).
        try
        {
            var p = _session?.BraveProcess;
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch { /* bỏ qua */ }

        State = SessionState.Stopped;
    }

    /// <summary>
    /// Đọc trạng thái trang hiện tại (read-only) của phiên đang chạy — dùng cuối lượt autorun để phát hiện
    /// "TK chưa xác nhận". Chụp phiên + token; phiên null / chưa Running / đang <see cref="_navigating"/> → trả
    /// null (không kết luận khi đang giữa thao tác). KHÔNG bật <see cref="_navigating"/> (DetectPageStateAsync
    /// chỉ evaluate JS đọc, không đụng chuột nên không phá thao tác nào). Mọi lỗi → null (best-effort).
    /// </summary>
    public async Task<ShopeePageState?> ProbePageStateAsync()
    {
        var s = _session;
        CancellationToken tok;
        try
        {
            lock (_lifecycleLock)
            {
                tok = _cts?.Token ?? default;
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        // Đang navigating (đọc đơn / xử lý đơn / kiểm proxy) → không kết luận (trang đang chuyển giữa chừng).
        if (s is null || State != SessionState.Running || _navigating)
        {
            return null;
        }

        try
        {
            return await s.DetectPageStateAsync(tok).ConfigureAwait(false);
        }
        catch
        {
            return null; // DetectPageStateAsync vốn không ném, nhưng phòng hờ vẫn nuốt.
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

    /// <summary>
    /// Xử lý đơn: trong phiên đang chạy. <b>ĐẦU LUỒNG — cửa skip:</b> đọc TƯƠI (reload trang chủ) số
    /// "Chờ Lấy Hàng"; nếu ĐỌC ĐƯỢC số VÀ == 0 (<see cref="ShouldSkipProcessing"/>) thì <b>BỎ QUA toàn bộ</b>
    /// (KHÔNG vào Cài đặt vận chuyển, không đặt/đặt-lại địa chỉ), đặt <c>ToShipCount=0</c> + StatusText/log
    /// "Không có đơn Chờ lấy hàng — bỏ qua xử lý" và trả <c>true</c> ("xong-không-có-việc", KHÔNG phải lỗi).
    /// ĐỌC KHÔNG được (null: chưa đăng nhập / lỗi) → KHÔNG skip, làm tiếp như cũ (tránh bỏ sót đơn thật).
    /// Cửa skip này áp cho CẢ nút "Xử lý đơn" thủ công LẪN "Chạy tự động" (AutoRunService gọi cùng hàm này).
    /// <para>
    /// Khi CÓ đơn (skip không kích hoạt): điều hướng KIỂU NGƯỜI tới "Cài Đặt Vận Chuyển" → tab "Địa Chỉ"
    /// (bước 1), rồi <b>đặt địa chỉ lấy hàng</b> theo tỉnh mặc định của tài khoản
    /// (<see cref="AccountsViewModel.DefaultPickupAddress"/> nếu chưa chọn) (bước 2), rồi <b>xử lý LẦN LƯỢT
    /// MỌI đơn</b> cần "Chuẩn bị hàng" (bước 3): lặp <c>ProcessFirstOrderAsync</c> (arrange → CHỜ nút In phiếu
    /// giao tới 5' → lưu phiếu → đóng modal) TỚI KHI hết đơn. ĐƠN LỖI thì ghi log + <b>BỎ QUA, chạy tiếp đơn
    /// kế</b>; chỉ dừng khi lỗi 3 đơn LIÊN TIẾP (chống lặp vô hạn) hoặc chạm chốt chặn 200 đơn. Khi vòng kết
    /// thúc TỰ NHIÊN (hết đơn / chạm chốt chặn), quay lại Cài đặt vận chuyển và <b>đặt lại địa chỉ lấy hàng
    /// về MỘT ĐỊA CHỈ KHÁC</b> (bước 4, best-effort — chỉ ghi log/StatusText, KHÔNG đổi giá trị trả về; nếu
    /// vòng dừng GIỮA CHỪNG vì 3 lỗi liên tiếp thì GIỮ NGUYÊN địa chỉ vì việc còn dở). Bật cờ
    /// <see cref="_navigating"/> bao trùm cả 4 bước để vòng <see cref="RunAsync"/> KHÔNG reload đọc đơn
    /// giữa chừng (phá thao tác). Graceful: phiên chưa chạy / bị hủy / lỗi → false, KHÔNG ném.
    /// </para>
    /// </summary>
    public async Task<bool> ProcessOrdersAsync()
    {
        // Chụp phiên + token dưới lock (nuốt ObjectDisposedException nếu _cts đã dispose).
        var s = _session;
        CancellationToken tok;
        try
        {
            lock (_lifecycleLock)
            {
                tok = _cts?.Token ?? default;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        // _navigating: đang có lượt điều hướng chạy dở (bấm lặp) → bỏ qua, không chạy 2 luồng chuột chồng nhau.
        if (s is null || State != SessionState.Running || _navigating)
        {
            return false;
        }

        _navigating = true;
        try
        {
            // CỬA SKIP (đầu luồng, TRƯỚC khi vào Cài đặt vận chuyển): đọc TƯƠI số "Chờ Lấy Hàng" (reload
            // trang chủ) để phản ánh đúng hiện trạng lúc bấm / lúc lô chạy — ToShipCount hiển thị có thể CŨ
            // (bấm sau khi đơn đã hết, hoặc chưa tới nhịp đọc kế). CHỈ bỏ qua khi ĐỌC ĐƯỢC số VÀ == 0
            // (ShouldSkipProcessing). Đọc KHÔNG được (null: chưa đăng nhập / lỗi) → KHÔNG skip, làm tiếp như
            // cũ (tránh bỏ sót đơn thật). An toàn để await ở đây vì _navigating đang bật (loại trừ nhịp đọc
            // đơn của RunAsync); finally cuối hàm vẫn nhả cờ.
            StatusText = "Đang kiểm tra đơn Chờ lấy hàng...";
            var toShip = await s.ReadToShipCountAsync(reload: true, tok).ConfigureAwait(false);
            if (ShouldSkipProcessing(toShip))
            {
                ToShipCount = 0; // đồng bộ UI: đã xác nhận không còn đơn Chờ lấy hàng
                StatusText = "Không có đơn Chờ lấy hàng — bỏ qua xử lý.";
                _services.Log.Append(_logLabel, "Không có đơn Chờ lấy hàng — bỏ qua xử lý.");
                return true; // "xong, không có việc" (KHÔNG phải lỗi) — KHÔNG vào Cài đặt vận chuyển.
            }

            // Bước 1: mở Cài đặt vận chuyển → tab Địa Chỉ. Kết quả phân biệt bước hỏng để báo StatusText đúng.
            StatusText = "Đang mở Cài đặt vận chuyển → Địa Chỉ (kiểu người)...";
            var nav = await s.OpenShippingAddressSettingsAsync(tok).ConfigureAwait(false);
            if (nav != ShippingNavResult.Ok)
            {
                StatusText = nav switch
                {
                    ShippingNavResult.PageNotOpened =>
                        "Không mở được trang Cài đặt vận chuyển (click không ăn / trang không chuyển) — thao tác tay trong cửa sổ Brave.",
                    ShippingNavResult.AddressTabNotFound =>
                        "Đã mở Cài đặt vận chuyển nhưng không thấy tab \"Địa Chỉ\" — Shopee có thể đã đổi giao diện, thao tác tay trong Brave.",
                    _ => "Không mở được Cài đặt vận chuyển — thao tác tay trong cửa sổ Brave.",
                };
                return false;
            }

            // Bước 2: đặt địa chỉ lấy hàng theo tỉnh mặc định của tài khoản (null/rỗng → mặc định app).
            var acc = _services.Accounts.GetById(_accountId);
            var province = string.IsNullOrWhiteSpace(acc?.PickupAddress)
                ? AccountsViewModel.DefaultPickupAddress
                : acc!.PickupAddress!;

            StatusText = $"Đang chọn địa chỉ lấy hàng ({province})...";
            var pick = await s.SetPickupAddressAsync(province, tok).ConfigureAwait(false);
            StatusText = pick switch
            {
                SetPickupResult.Ok => $"Đã đặt địa chỉ lấy hàng: {province}.",
                SetPickupResult.AddressNotFound =>
                    $"Không thấy địa chỉ ở {province} trong danh sách — kiểm tra tay trong cửa sổ Brave.",
                SetPickupResult.EditModalNotOpened =>
                    $"Mở được danh sách nhưng không sửa được địa chỉ ({province}) — shop có thể bị khóa sửa, kiểm tra tay.",
                SetPickupResult.CheckboxNotFound =>
                    $"Mở được ô Sửa địa chỉ nhưng không thấy mục \"Đặt làm địa chỉ lấy hàng\" — kiểm tra tay trong Brave.",
                SetPickupResult.CheckboxClickFailed =>
                    $"Không tick được \"Đặt làm địa chỉ lấy hàng\" ({province}) — kiểm tra tay trong cửa sổ Brave.",
                SetPickupResult.SaveFailed =>
                    $"Đã tick nhưng chưa Lưu được địa chỉ lấy hàng ({province}) — kiểm tra tay trong cửa sổ Brave.",
                _ => $"Không đặt được địa chỉ lấy hàng ({province}) — kiểm tra tay trong cửa sổ Brave.",
            };
            if (pick != SetPickupResult.Ok)
            {
                // KHÔNG dừng cả luồng: Shopee có thể CHẶN đổi địa chỉ lấy hàng khi shop có đơn "Chờ lấy
                // hàng" (đã arrange, chờ bưu cục) → bước đặt địa chỉ thất bại KHÔNG được chặn việc xử lý đơn.
                // Địa chỉ giữ nguyên hiện tại; vẫn chạy tiếp vòng xử lý đơn bên dưới.
                _services.Log.Append(_logLabel,
                    $"Không đặt được địa chỉ lấy hàng ({province}) — có thể do có đơn Chờ lấy hàng khóa đổi địa chỉ; vẫn tiếp tục xử lý đơn.");
            }

            // Bước 3: xử lý LẦN LƯỢT MỌI đơn — lặp ProcessFirstOrderAsync (mỗi vòng: điều hướng "Tất cả" →
            // quét đơn đầu có "Chuẩn bị hàng" → arrange → In phiếu → đóng modal) TỚI KHI hết đơn (NoOrder).
            // Đơn đã arrange MẤT nút "Chuẩn bị hàng" nên vòng tự dừng khi mọi đơn xử lý xong. ĐƠN LỖI thì GHI
            // LOG + BỎ QUA + chạy tiếp đơn kế (KHÔNG dừng cả vòng) — chỉ dừng khi LỖI 3 ĐƠN LIÊN TIẾP (chống
            // lặp vô hạn). Mọi bước ghi log qua ActivityLog (panel + file) để smoke live thấy rõ.
            var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));

            // Thư mục lưu phiếu: đọc MỘT LẦN từ Cài đặt (config hoặc mặc định cạnh app.db) — chụp vào biến,
            // KHÔNG đọc lại giữa vòng/await. NGUỒN DUY NHẤT, khớp link "In phiếu" ở màn Đơn hàng. Tạo sẵn
            // best-effort (SaveSlipAsync cũng tự tạo + cảnh báo nếu vẫn lỗi).
            var invoiceDir = _services.Settings.GetInvoiceFolder();
            try { Directory.CreateDirectory(invoiceDir); } catch { /* SaveSlipAsync sẽ thử lại + cảnh báo */ }

            const int MaxOrders = 200;              // chốt chặn an toàn (tránh lặp vô hạn nếu 1 đơn kẹt ở "Chuẩn bị hàng")
            var loopRng = new Random();
            int done = 0;                            // số đơn xử lý THÀNH CÔNG
            int failCount = 0;                       // số đơn BỎ QUA vì lỗi
            int consecutiveFails = 0;                // số đơn lỗi LIÊN TIẾP (reset khi có 1 đơn Ok)
            bool stoppedByConsecutiveFails = false;
            ArrangeShipmentResult last = ArrangeShipmentResult.NoOrder;
            while (done < MaxOrders)
            {
                StatusText = $"Đang xử lý đơn thứ {done + failCount + 1}...";
                last = await s.ProcessFirstOrderAsync(invoiceDir, log, tok).ConfigureAwait(false);

                // Quyết định vòng lặp (hàm thuần, test được): Ok → reset chuỗi lỗi; NoOrder → dừng (hết đơn);
                // lỗi khác → tăng chuỗi lỗi, dừng khi đạt 3 liên tiếp.
                var (stop, nextConsecutive) = NextLoopDecision(last, consecutiveFails);
                consecutiveFails = nextConsecutive;

                if (last == ArrangeShipmentResult.Ok)
                {
                    done++;
                }
                else if (last != ArrangeShipmentResult.NoOrder)
                {
                    // Đơn lỗi: ghi log + bỏ qua, chạy tiếp đơn kế.
                    failCount++;
                    log($"Bỏ qua đơn lỗi ({DescribeFailedStep(last)}) — tiếp tục đơn kế.");
                }

                if (stop)
                {
                    stoppedByConsecutiveFails = last != ArrangeShipmentResult.NoOrder;
                    break;
                }

                // Dừng ngẫu nhiên kiểu người giữa các đơn.
                try { await Task.Delay(loopRng.Next(1500, 3500), tok).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }

            // Tổng kết cuối vòng: ghi CẢ StatusText LẪN ActivityLog (StatusText không vào file log → mất dấu vết).
            // Thứ tự nhánh QUAN TRỌNG: nhánh "đạt chốt chặn" (last == Ok) phải đứng TRƯỚC nhánh "failCount > 0"
            // — vì chạm chốt chặn nghĩa là CÒN đơn chưa xử lý, không được để câu "bỏ qua Y đơn lỗi" nuốt mất
            // thông tin đó làm người dùng tưởng đã hết đơn.
            string summary;
            if (stoppedByConsecutiveFails)
            {
                summary = $"Dừng vì lỗi 3 đơn liên tiếp ({DescribeFailedStep(last)}). Đã xử lý {done} đơn, bỏ qua {failCount} đơn lỗi.";
            }
            else if (last == ArrangeShipmentResult.Ok)
            {
                // Chạm chốt chặn MaxOrders (còn đơn chưa xử lý) — nêu RÕ để không tưởng đã hết đơn.
                summary = failCount > 0
                    ? $"Đã xử lý {done} đơn (đạt chốt chặn {MaxOrders}), bỏ qua {failCount} đơn lỗi."
                    : $"Đã xử lý {done} đơn (đạt chốt chặn {MaxOrders}).";
            }
            else if (failCount > 0)
            {
                summary = $"Đã xử lý {done} đơn, bỏ qua {failCount} đơn lỗi.";
            }
            else
            {
                // Còn lại: NoOrder (hết đơn), failCount == 0 — giữ câu cũ.
                summary = done > 0
                    ? $"Đã xử lý xong {done} đơn. Không còn đơn nào cần xử lý."
                    : "Không có đơn nào cần xử lý.";
            }
            StatusText = summary;
            log(summary);

            // Bước 4: sau khi vòng xử lý kết thúc TỰ NHIÊN (hết đơn NoOrder / chạm chốt chặn Ok), quay lại Cài
            // đặt vận chuyển và đặt địa chỉ lấy hàng về MỘT ĐỊA CHỈ KHÁC — KHÔNG giữ nguyên địa chỉ app đã đặt
            // để xử lý. Best-effort: mọi kết cục CHỈ ghi log + StatusText (ghép SAU summary cho khỏi mất tổng
            // kết), KHÔNG đổi giá trị return. Nếu vòng dừng GIỮA CHỪNG vì 3 lỗi liên tiếp → GIỮ NGUYÊN địa chỉ
            // (việc còn dở, người dùng sẽ chạy lại). OCE ném xuyên để catch ngoài cùng dừng sạch.
            if (last is ArrangeShipmentResult.NoOrder or ArrangeShipmentResult.Ok)
            {
                try
                {
                    StatusText = "Đang đặt lại địa chỉ lấy hàng (địa chỉ khác)...";
                    log("Quay lại Cài đặt vận chuyển để đặt địa chỉ lấy hàng về địa chỉ khác...");
                    var nav2 = await s.OpenShippingAddressSettingsAsync(tok).ConfigureAwait(false);
                    if (nav2 != ShippingNavResult.Ok)
                    {
                        StatusText = summary + " Không mở lại được Cài đặt vận chuyển — giữ nguyên địa chỉ lấy hàng.";
                        log("Không mở lại được Cài đặt vận chuyển — giữ nguyên địa chỉ lấy hàng.");
                    }
                    else
                    {
                        var other = await s.SetPickupAddressToOtherAsync(tok).ConfigureAwait(false);
                        string msg = other switch
                        {
                            SetPickupResult.Ok => "Đã đặt địa chỉ lấy hàng về địa chỉ khác.",
                            SetPickupResult.AddressNotFound =>
                                "Shop không có địa chỉ nào khác — giữ nguyên địa chỉ lấy hàng.",
                            _ =>
                                $"Chưa đặt lại được địa chỉ lấy hàng ({DescribeSetPickupStep(other)}) — Shopee có thể khóa đổi địa chỉ khi có đơn Chờ lấy hàng; kiểm tra tay nếu cần.",
                        };
                        StatusText = summary + " " + msg;
                        log(msg);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // hủy chủ động → để catch OCE ngoài cùng dừng sạch
                }
                catch (Exception ex)
                {
                    // Best-effort: lỗi bất ngờ khi đặt lại địa chỉ KHÔNG được phá kết quả vòng xử lý.
                    // Trả StatusText về câu tổng kết (kẻo kẹt ở "Đang đặt lại địa chỉ...").
                    StatusText = summary + " (Lỗi khi đặt lại địa chỉ lấy hàng — xem nhật ký.)";
                    log($"Lỗi khi đặt lại địa chỉ lấy hàng (bỏ qua): {ex.Message}");
                }
            }
            else
            {
                log("Giữ nguyên địa chỉ lấy hàng (vòng dừng giữa chừng).");
            }

            return last is ArrangeShipmentResult.NoOrder or ArrangeShipmentResult.Ok || done > 0;
        }
        catch (OperationCanceledException)
        {
            // Bị dừng chủ động trong lúc điều hướng — không phải lỗi.
            return false;
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// Quyết định vòng lặp xử lý đơn theo kết quả xử lý MỘT đơn (thuần, KHÔNG side-effect → test được):
    /// <list type="bullet">
    /// <item><see cref="ArrangeShipmentResult.Ok"/> → KHÔNG dừng, reset chuỗi lỗi liên tiếp về 0.</item>
    /// <item><see cref="ArrangeShipmentResult.NoOrder"/> → DỪNG (hết đơn), giữ nguyên chuỗi lỗi.</item>
    /// <item>Lỗi khác → tăng chuỗi lỗi liên tiếp; DỪNG khi đạt <paramref name="maxConsecutiveFails"/>.</item>
    /// </list>
    /// Guard 3-liên-tiếp cần vì đơn fail TRƯỚC arrange (PrepareNotFound/ShipModalNotOpened/ConfirmFailed/
    /// Failed) vẫn còn nút "Chuẩn bị hàng" → vòng sau chọn LẠI chính đơn đó; 3 lần không tiến triển = có vấn
    /// đề hệ thống, dừng để người xem. (Đơn fail SAU arrange — PrintFailed/DetailModalNotOpened — mất nút
    /// "Chuẩn bị hàng" nên vòng sau tự sang đơn kế, không lặp.)
    /// </summary>
    public static (bool stop, int consecutive) NextLoopDecision(
        ArrangeShipmentResult last, int consecutiveFails, int maxConsecutiveFails = 3)
    {
        if (last == ArrangeShipmentResult.Ok)
        {
            return (false, 0);                       // 1 đơn Ok → reset chuỗi lỗi
        }
        if (last == ArrangeShipmentResult.NoOrder)
        {
            return (true, consecutiveFails);         // hết đơn → dừng
        }
        int next = consecutiveFails + 1;
        return (next >= maxConsecutiveFails, next);  // lỗi → tăng chuỗi; đủ 3 liên tiếp thì dừng
    }

    /// <summary>
    /// Quyết định BỎ QUA xử lý đơn theo số "Chờ Lấy Hàng" ĐỌC ĐƯỢC (thuần, KHÔNG side-effect → test được).
    /// Dùng ở ĐẦU <see cref="ProcessOrdersAsync"/>: CHỈ bỏ qua khi đọc ĐƯỢC số VÀ số == 0 (chắc chắn không
    /// còn đơn "Chờ Lấy Hàng" → không cần vào Cài đặt vận chuyển). Đọc KHÔNG được
    /// (<paramref name="toShipCount"/> == null: chưa đăng nhập / đọc lỗi) → KHÔNG bỏ qua (làm tiếp như cũ,
    /// tránh bỏ sót đơn thật). Số &gt; 0 → KHÔNG bỏ qua (có đơn cần xử lý).
    /// </summary>
    public static bool ShouldSkipProcessing(int? toShipCount) => toShipCount is 0;

    /// <summary>Mô tả NGẮN bước lỗi của một đơn (dùng cho log "Bỏ qua đơn lỗi (...)" và câu dừng 3-liên-tiếp).</summary>
    private static string DescribeFailedStep(ArrangeShipmentResult r) => r switch
    {
        ArrangeShipmentResult.OrdersPageNotOpened  => "không mở được danh sách đơn",
        ArrangeShipmentResult.PrepareNotFound      => "không bấm được Chuẩn bị hàng",
        ArrangeShipmentResult.ShipModalNotOpened   => "không mở được ô Giao Đơn Hàng",
        ArrangeShipmentResult.ConfirmFailed        => "không Xác nhận được",
        ArrangeShipmentResult.DetailModalNotOpened => "không mở được Thông Tin Chi Tiết",
        ArrangeShipmentResult.PrintFailed          => "không In phiếu giao được",
        _ => "lỗi không xác định",
    };

    /// <summary>Mô tả NGẮN bước hỏng khi đặt LẠI địa chỉ lấy hàng về địa chỉ khác (bước 4, cho log/StatusText).</summary>
    private static string DescribeSetPickupStep(SetPickupResult r) => r switch
    {
        SetPickupResult.EditModalNotOpened  => "không mở được ô Sửa địa chỉ",
        SetPickupResult.CheckboxNotFound    => "không thấy mục Đặt làm địa chỉ lấy hàng",
        SetPickupResult.CheckboxClickFailed => "không tick được",
        SetPickupResult.SaveFailed          => "không Lưu được",
        _ => "lỗi không xác định",
    };

    /// <summary>
    /// Kiểm tra đơn NGAY (thủ công): trong phiên đang chạy, về trang chủ Seller (Goto như người gõ URL —
    /// KHÔNG click máy) rồi đọc lại số "Chờ Lấy Hàng" ngay, cập nhật <see cref="ToShipCount"/> — không đợi
    /// chu kỳ theo dõi (cấu hình). Bật cờ <see cref="_navigating"/> để vòng <see cref="RunAsync"/> KHÔNG reload đọc
    /// đơn giữa chừng và để loại trừ với <see cref="ProcessOrdersAsync"/> (hai thao tác không chạy chồng nhau
    /// trên cùng trang). Graceful: phiên chưa chạy / đang bận / bị hủy / không đọc được → false, KHÔNG ném.
    /// KHÔNG đổi <see cref="ToShipCount"/> khi không đọc được (giữ số cũ).
    /// </summary>
    public async Task<bool> CheckOrdersAsync()
    {
        // Chụp phiên + token dưới lock (nuốt ObjectDisposedException nếu _cts đã dispose).
        var s = _session;
        CancellationToken tok;
        try
        {
            lock (_lifecycleLock)
            {
                tok = _cts?.Token ?? default;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        // _navigating: đang có lượt điều hướng chạy dở (bấm lặp / xử lý đơn) → bỏ qua, không chạy chồng nhau.
        if (s is null || State != SessionState.Running || _navigating)
        {
            return false;
        }

        _navigating = true;
        StatusText = "Đang về trang chủ để kiểm tra đơn...";
        try
        {
            // Về trang chủ (Goto) + đọc lại số ngay (reload:false vì trang vừa load) — gộp trong Core.
            var count = await s.GoHomeAndReadToShipCountAsync(tok).ConfigureAwait(false);
            if (count is int n)
            {
                ToShipCount = n; // VM tự định dạng dòng hiển thị theo số này
                StatusText = $"Đã kiểm tra: Chờ Lấy Hàng = {n}.";
                return true;
            }

            // Bị hủy giữa chừng (người dùng bấm Dừng): Core nuốt OperationCanceledException và trả null —
            // KHÔNG đè thông báo "Đang dừng..." bằng câu báo lỗi gây hiểu lầm.
            if (tok.IsCancellationRequested)
            {
                return false;
            }

            // Không đọc được → GIỮ nguyên số cũ (KHÔNG đổi ToShipCount).
            StatusText = "Không đọc được số đơn — có thể chưa đăng nhập xong, kiểm tra cửa sổ Brave.";
            return false;
        }
        catch (OperationCanceledException)
        {
            // Bị dừng chủ động trong lúc điều hướng — không phải lỗi.
            return false;
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// Sync Đơn hàng: trong phiên đang chạy, vào Quản lý đơn hàng → tab "Tất cả", duyệt MỌI trang danh sách
    /// (Core best-effort — không ném trừ hủy) thu thập thông tin đơn rồi <b>UPSERT về DB</b> (bảng orders,
    /// theo khóa <c>(account_id, order_sn)</c>). Bật cờ <see cref="_navigating"/> suốt lượt để loại trừ với
    /// Xử lý đơn / Kiểm tra / nhịp theo dõi (cấu hình) (không hai luồng chuột trên cùng trang). Ghi log tiến trình
    /// từng trang + tổng kết (thêm mới / cập nhật). Graceful: phiên chưa chạy / đang bận / bị hủy / lỗi →
    /// false + StatusText/log, KHÔNG ném. finally reset <see cref="_navigating"/>.
    /// <para>
    /// <b>Vòng đời đơn (app chỉ giữ đơn Chuẩn bị hàng):</b> quét tab "Tất cả" để DÒ trạng thái theo mã đơn nhưng
    /// chỉ LƯU đơn MỚI khi đang ở Chuẩn bị hàng (<see cref="ShopeeShippingNav.LaChuanBiHang"/>); đơn ĐÃ theo dõi
    /// luôn cập nhật đến trạng thái cuối. Đơn KẾT THÚC (Đã giao / Đã hủy) được DỌN khỏi DB trong
    /// <see cref="PushOrdersToGsheetAsync"/> sau khi GSheet + "Đã bán" + hub đã xong.
    /// </para>
    /// <para>
    /// <b>"Nút Sync bao gồm cả nút Kiểm tra"</b> (người dùng chốt): trước khi tổng kết (vẫn trong cửa sổ
    /// <see cref="_navigating"/>), VỀ TRANG CHỦ đọc số "Chờ Lấy Hàng" tươi (<c>GoHomeAndReadToShipCountAsync</c>
    /// — y hệt nút Kiểm tra; ô số nằm Ở TRANG CHỦ, sau sync trình duyệt đang ở trang danh sách đơn nên đọc tại
    /// chỗ sẽ không thấy) và cập nhật <see cref="ToShipCount"/> — để nút "Xử lý đơn" (phụ thuộc số này) đúng
    /// trạng thái ngay, không phải chờ nhịp đọc định kỳ (có thể xa tới ~10'). Best-effort: đọc lỗi/không được
    /// KHÔNG phá kết quả sync.
    /// </para>
    /// <para>
    /// Đẩy đơn lên Google Sheet (<see cref="PushOrdersToGsheetAsync"/>) được kích hoạt SAU tổng kết và chạy
    /// <b>NỀN</b> qua <see cref="StartGsheetPushInBackground"/> (KHÔNG await trong cửa sổ <see cref="_navigating"/>):
    /// push chỉ đụng DB + file + HTTP, KHÔNG đụng trình duyệt nên chạy song song được với nhịp đọc "Chờ Lấy
    /// Hàng" của <see cref="RunAsync"/> — nhờ đó nút "Xử lý đơn" (phụ thuộc ToShipCount) không bị xám lâu chờ
    /// upload phiếu xong.
    /// </para>
    /// </summary>
    public async Task<bool> SyncOrdersAsync()
    {
        // Chụp phiên + token dưới lock (nuốt ObjectDisposedException nếu _cts đã dispose).
        var s = _session;
        CancellationToken tok;
        try
        {
            lock (_lifecycleLock)
            {
                tok = _cts?.Token ?? default;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        // _navigating: đang có lượt điều hướng chạy dở (bấm lặp / xử lý đơn / kiểm tra) → bỏ qua, không chồng nhau.
        if (s is null || State != SessionState.Running || _navigating)
        {
            return false;
        }

        _navigating = true;
        StatusText = "Đang sync đơn hàng (tab Tất cả)...";
        var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));
        try
        {
            // Tập đơn ĐÃ có "Số tiền cuối cùng" trong DB → Core bỏ qua mở lại chi tiết cho các đơn này (tối ưu:
            // lần đầu lâu, các lần sau nhanh). Đọc từ DB trước mỗi lượt sync để đón số đã lấy ở lượt trước.
            var alreadyHaveFinal = _services.Orders.GetOrderSnsWithFinalAmount(_accountId);

            // Core thu thập (best-effort có log tiến trình từng trang), trả DTO — KHÔNG đụng DB.
            var result = await s.SyncAllOrdersAsync(log, alreadyHaveFinal, tok).ConfigureAwait(false);

            // Lọc đơn được LƯU (chính sách "app chỉ giữ đơn Chuẩn bị hàng"): đơn ĐÃ theo dõi (mã đã có trong DB)
            // LUÔN cập nhật — kể cả khi đã sang Đã giao/Đã hủy (cần cho GSheet + "Đã bán" + dọn vòng đời); đơn MỚI
            // (chưa có trong DB) chỉ nhận khi đang ở Chuẩn bị hàng. Đơn mới KHÁC (Chờ xác nhận / Đang giao / Đã
            // giao…) bị BỎ QUA — sẽ tự vào ở lượt sau khi thành Chuẩn bị hàng.
            // QUAN TRỌNG: filter này ĐỒNG THỜI chặn đơn ĐÃ-BỊ-DỌN (xóa ở PushOrdersToGsheetAsync) được insert lại
            // ở lượt quét sau — nó xuất hiện lại trong tab "Tất cả" với trạng thái KẾT THÚC (không phải Chuẩn bị
            // hàng) nên không lọt qua → KHÔNG lặp vô hạn ghi-xóa.
            var existing = _services.Orders.GetOrderSns(_accountId);
            var toUpsert = result.Orders
                .Where(o => existing.Contains(o.OrderSn) || ShopeeShippingNav.LaChuanBiHang(o.Status))
                .ToList();

            // "Đã bán" theo SKU: phát hiện đơn CHUYỂN sang đã-giao TRƯỚC khi UpsertMany ghi đè cột status (đọc
            // trạng thái CŨ trong DB). Chạy tuần tự trước upsert nên tương đương "cùng transaction" — không có ghi
            // đồng thời (mỗi tài khoản một phiên, đang trong cửa sổ _navigating). No-backfill: đơn đã-giao-sẵn →
            // grandfather (đánh cờ, KHÔNG +1). Chỉ xét đơn ĐƯỢC LƯU (toUpsert) — đơn mới ngoài theo dõi không tính.
            var soldDetect = _services.Orders.DetectNewlyDelivered(_accountId, toUpsert);

            // Lưu về DB (thread nền — SQLite an toàn): upsert theo (account_id, order_sn). insertedOrders =
            // các đơn VỪA thêm mới (đơn cập nhật KHÔNG có) → dùng để báo "đơn mới" qua Slack/Discord/Telegram.
            var (inserted, updated, insertedOrders) = _services.Orders.UpsertMany(_accountId, toUpsert, DateTime.UtcNow, _currentShopId);

            // Đánh cờ NGAY (SAU upsert để dòng mới toanh đã tồn tại) cho nhóm KHÔNG cần +1: đơn grandfather
            // (đã-giao-sẵn) + đơn chuyển-sang-đã-giao nhưng không có SKU. Nhóm CÓ SKU (+1) chỉ đánh cờ SAU khi hub
            // +1 OK (StartSoldCountInBackground) để hub lỗi thì lượt sau thử lại.
            if (soldDetect.ImmediateMarkOrderSns.Count > 0)
            {
                _services.Orders.MarkSoldCounted(_accountId, soldDetect.ImmediateMarkOrderSns, DateTime.UtcNow);
            }

            // Vừa ghi đơn vào DB → phát tín hiệu để màn "Đơn hàng" đang mở TỰ nạp lại (OrdersViewModel nghe
            // rồi marshal về UI thread). CHỈ thêm 1 lời gọi này, KHÔNG đụng luồng xử lý đơn / cửa-skip.
            _services.RaiseOrdersChanged();

            // Tải LẠI phiếu THIẾU (đơn Chuẩn bị hàng + có vận đơn nhưng file phiếu mất/không phải PDF): vẫn TRONG
            // cửa sổ _navigating (thao tác trình duyệt độc quyền, không hai luồng chuột). Best-effort: chốt chặn ≤5
            // đơn/lượt (tránh kéo dài sync — còn thiếu thì lượt sau làm tiếp); mọi lỗi CHỈ log, KHÔNG phá kết quả
            // sync. OCE ném xuyên (dừng chủ động). Lưu được ≥1 phiếu → phát OrdersChanged (cột Phiếu cập nhật).
            try
            {
                var invoiceDir = _services.Settings.GetInvoiceFolder();
                var thieu = _services.Orders.GetOrdersForSlipCheck(_accountId)
                    .Where(o => ThieuPhieu(o.Status, o.TrackingNumber,
                        Path.Combine(invoiceDir, ShopeeShippingNav.SanitizeFileName(o.OrderSn) + ".pdf")))
                    .Select(o => o.OrderSn)
                    .ToList();
                if (thieu.Count > 0)
                {
                    var batch = thieu.Take(5).ToList();
                    if (thieu.Count > batch.Count)
                    {
                        log($"Có {thieu.Count} đơn thiếu phiếu — tải lại {batch.Count} đơn lượt này, còn {thieu.Count - batch.Count} đơn chờ lượt sau.");
                    }
                    StatusText = $"Đang tải lại {batch.Count} phiếu thiếu...";
                    var re = await s.RedownloadSlipsAsync(batch, invoiceDir, log, tok).ConfigureAwait(false);
                    log($"Tải lại phiếu: xong {re}/{batch.Count}.");
                    if (re > 0)
                    {
                        _services.RaiseOrdersChanged();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw; // hủy chủ động → catch ngoài xử như hủy
            }
            catch (Exception ex)
            {
                log("Tải lại phiếu thiếu gặp lỗi (bỏ qua): " + ex.Message);
            }

            // "Nút Sync bao gồm cả nút Kiểm tra" (người dùng chốt 2026-07-20): kết thúc mỗi lượt sync bằng SỐ
            // TƯƠI "Chờ Lấy Hàng" — VỀ TRANG CHỦ đọc y hệt nút Kiểm tra (GoHomeAndReadToShipCountAsync): ô số
            // nằm Ở TRANG CHỦ, sau sync trình duyệt đang ở trang danh sách đơn nên ReadToShipCountAsync tại chỗ
            // sẽ không thấy. Phiên mở sẵn lâu có nextOrderCheck xa tới ~10' → trước đây sync xong ToShipCount
            // vẫn là số CŨ, hiển thị "Chờ lấy: N" và quyết định xử-lý-hay-không của Sync trọn gói sai theo;
            // giờ sync nào cũng kết bằng số tươi (phiên cũng đậu lại ở trang chủ như sau Kiểm tra).
            // Đọc ở đây vẫn TRONG cửa sổ _navigating (thao tác trình duyệt độc quyền). Best-effort: lỗi đọc số
            // KHÔNG phá kết quả sync (OCE cho xuyên để dừng sạch).
            int? toShip = null;
            try
            {
                toShip = await s.GoHomeAndReadToShipCountAsync(tok).ConfigureAwait(false);
                if (toShip is int n)
                {
                    ToShipCount = n; // VM/danh sách tài khoản cập nhật "Chờ lấy: N" ngay
                    log($"Chờ Lấy Hàng: {n} đơn.");
                }
                else
                {
                    log("Không đọc được số Chờ Lấy Hàng sau sync (có thể chưa đăng nhập xong) — bỏ qua.");
                }
            }
            catch (OperationCanceledException)
            {
                throw; // hủy chủ động → catch ngoài xử như hủy
            }
            catch (Exception ex)
            {
                log("Không đọc được số Chờ Lấy Hàng sau sync (bỏ qua): " + ex.Message);
            }

            var boQua = result.Orders.Count - toUpsert.Count; // đơn quét thấy nhưng KHÔNG lưu (mới, ngoài Chuẩn bị hàng)
            var summary = $"Sync xong: {result.Orders.Count} đơn / {result.Pages} trang — thêm {inserted} mới, cập nhật {updated}."
                + (boQua > 0 ? $" — bỏ qua {boQua} đơn ngoài theo dõi" : string.Empty)
                + (result.ReachedPageCap ? " (chạm chốt chặn 10 trang)" : string.Empty)
                + (toShip is int t ? $" — Chờ Lấy Hàng: {t}" : string.Empty);
            StatusText = summary;
            log(summary);

            // Đẩy GSheet chạy NỀN (KHÔNG await trong cửa sổ _navigating): push chỉ đụng DB + file + HTTP, KHÔNG
            // đụng trình duyệt → không cần giữ _navigating. Nếu await ở đây thì upload phiếu (mạng, có thể nhiều
            // phút) kéo dài khóa _navigating, hoãn nhịp RunAsync đọc "Chờ Lấy Hàng" → số hiển thị/quyết định
            // của Sync trọn gói treo số cũ rất lâu sau Sync. Chạy nền để nhịp đọc số chạy ngay.
            StartGsheetPushInBackground(log, tok);

            // Đẩy đơn lên HUB đơn hàng chạy NỀN (y pattern GSheet: chỉ đụng DB + HTTP, không đụng trình duyệt).
            // KHÔNG điều kiện insertedOrders: kể cả lượt này không có đơn MỚI vẫn thử đẩy để BÙ backlog những đơn
            // các lượt trước đẩy hụt (hub offline). Hook chưa được rót (app Đơn hàng chạy độc lập / hub tắt) →
            // tự return im lặng bên trong.
            StartHubPushInBackground(log, tok);

            // Đẩy FILE PHIẾU lên HUB chạy NỀN (y pattern hub-push): các đơn ĐÃ lên hub, có vận đơn, CHƯA đẩy phiếu
            // + có file phiếu local hợp lệ → đẩy lô ≤5. Chạy SAU StartHubPushInBackground (phiếu chỉ đẩy được cho
            // đơn đã lên hub). Hook chưa rót (app độc lập / hub tắt) → tự return im lặng bên trong.
            StartHubSlipPushInBackground(log, tok);

            // +1 "Đã bán" theo SKU lên HUB chạy NỀN (y pattern hub-push): các đơn VỪA chuyển sang đã-giao trong lượt
            // này (soldDetect). Chỉ đánh cờ sold_counted_at cho các đơn CÓ SKU SAU khi hub +1 OK. Không có SKU nào
            // cần +1 → tự return bên trong. Hook chưa rót → im lặng (đơn giữ CHƯA đánh cờ, lượt sau thử lại).
            StartSoldCountInBackground(soldDetect.SkusToIncrement, soldDetect.PendingMarkOrderSns, log, tok);

            // Báo "đơn MỚI" (Slack/Discord/Telegram) — CHỈ khi lượt này có đơn INSERT. Chạy NỀN fire-and-forget
            // (y pattern GSheet): thông báo chỉ đụng HTTP nên không cần giữ _navigating, không kéo dài lượt sync;
            // lỗi mạng chỉ log, KHÔNG phá sync. Tin nhắn mang dữ liệu ĐÃ sync (kể cả mã vận đơn nếu có sau bước
            // chuẩn bị hàng — sync là bước cuối của "Sync trọn gói").
            if (insertedOrders.Count > 0)
            {
                StartNotifyInBackground(insertedOrders, log, tok);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            // Bị dừng chủ động trong lúc sync — không phải lỗi.
            return false;
        }
        catch (Exception ex)
        {
            // Lỗi bất ngờ (vd ghi DB) → log + StatusText, KHÔNG ném (không phá phiên).
            StatusText = "Sync đơn hàng gặp lỗi — xem nhật ký.";
            log("Lỗi khi sync đơn hàng: " + ex.Message);
            return false;
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// <b>Tải LẠI phiếu MỘT đơn (nút "Tải phiếu" màn Đơn hàng):</b> trong phiên ĐANG chạy, gọi
    /// <see cref="ILoginSession.RedownloadSlipsAsync"/> cho một mã đơn (về danh sách "Tất cả", định vị card,
    /// bấm In phiếu giao, lưu PDF). Bọc cờ <see cref="_navigating"/> y hệt các lượt điều hướng khác (loại trừ
    /// với sync / xử lý đơn / kiểm tra — không hai luồng chuột trên cùng trang). Graceful: phiên chưa chạy /
    /// đang bận / bị hủy / lỗi → <c>false</c> + StatusText/log, KHÔNG ném. Lưu được → phát
    /// <see cref="AppServices.RaiseOrdersChanged"/> (cột Phiếu cập nhật). finally reset <see cref="_navigating"/>.
    /// </summary>
    public async Task<bool> RedownloadSlipAsync(string orderSn)
    {
        if (string.IsNullOrWhiteSpace(orderSn))
        {
            return false;
        }

        var s = _session;
        CancellationToken tok;
        try
        {
            lock (_lifecycleLock)
            {
                tok = _cts?.Token ?? default;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        // _navigating: đang có lượt điều hướng chạy dở → bỏ qua, không chồng nhau.
        if (s is null || State != SessionState.Running || _navigating)
        {
            return false;
        }

        _navigating = true;
        StatusText = $"Đang tải lại phiếu đơn {orderSn}...";
        var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));
        try
        {
            var invoiceDir = _services.Settings.GetInvoiceFolder();
            var re = await s.RedownloadSlipsAsync(new[] { orderSn }, invoiceDir, log, tok).ConfigureAwait(false);
            if (re > 0)
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
        catch (Exception ex)
        {
            StatusText = $"Tải lại phiếu đơn {orderSn} gặp lỗi — xem nhật ký.";
            log("Lỗi khi tải lại phiếu: " + ex.Message);
            return false;
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// Sync TRỌN GÓI (nút Sync mới của màn Tài khoản): kiểm tra đơn mới → nếu có thì xử lý đơn →
    /// sync đơn hàng. Caller (VM) đã lo mở phiên + chờ sẵn sàng qua RunOrAutoStartAsync. Mỗi bước con tự
    /// quản <see cref="_navigating"/>; bước Kiểm tra bận/fail → dừng chuỗi; riêng Xử lý đơn lỗi giữa chừng
    /// vẫn đi tiếp bước sync (log rõ), KHÔNG ném.
    /// <para>
    /// Thứ tự: <b>Kiểm tra → (nếu ToShipCount &gt; 0) Xử lý đơn → Sync</b>. Xử lý đơn LỖI giữa chừng KHÔNG
    /// chặn việc sync (vẫn lưu đơn về máy). Ghép từ 3 method rời có sẵn
    /// (<see cref="CheckOrdersAsync"/>/<see cref="ProcessOrdersAsync"/>/<see cref="SyncOrdersAsync"/>) —
    /// KHÔNG tái dùng pipeline AutoRun vì AutoRun đi theo thứ tự Process→Sync→Check và có chính sách vòng
    /// đời phiên riêng (bỏ qua phiên người dùng tự mở, tự đóng phiên sau lô).
    /// </para>
    /// </summary>
    public async Task<bool> SyncFullAsync()
    {
        var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));
        log("Sync trọn gói: kiểm tra đơn mới → xử lý (nếu có) → sync đơn hàng.");

        // Bước 1: kiểm tra đơn mới (về trang chủ đọc số "Chờ Lấy Hàng"). false = phiên bận / không đọc được → dừng chuỗi.
        var ok = await CheckOrdersAsync().ConfigureAwait(false);
        if (!ok)
        {
            log("Sync trọn gói: không kiểm tra được (phiên bận?) — dừng.");
            return false;
        }

        // Bước 2: nếu có đơn Chờ Lấy Hàng thì XỬ LÝ trước khi sync. Xử lý lỗi giữa chừng KHÔNG được chặn
        // việc lưu đơn về máy → vẫn đi tiếp sang bước sync (chỉ log rõ). ToShipCount đã được CheckOrdersAsync
        // cập nhật số tươi ngay trước đó.
        if (ToShipCount is > 0)
        {
            log($"Có {ToShipCount} đơn chờ — xử lý đơn trước khi sync.");
            var processed = await ProcessOrdersAsync().ConfigureAwait(false);
            if (!processed)
            {
                log("Xử lý đơn chưa trọn vẹn — vẫn sync đơn hàng.");
            }
        }
        else
        {
            log("Không có đơn chờ — bỏ qua bước xử lý.");
        }

        // Bước 3: sync đơn hàng (bên trong đã tự về trang chủ đọc số + đẩy GSheet chạy nền).
        return await SyncOrdersAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Flow đơn cho MỘT shop trong vòng lặp shop (mô hình 1 subaccount = nhiều shop): BÊ THÂN
    /// <see cref="SyncFullAsync"/> — Kiểm tra → (nếu ToShipCount &gt; 0) Xử lý đơn → Sync. Các hàm con chạy trên
    /// "trang làm việc" (tab shop đang mở qua <c>OpenShopDetailAsync</c>) và TỰ quản <see cref="_navigating"/>.
    /// Đơn được gắn <see cref="_currentShopId"/> khi upsert; đẩy GSheet lấy Tên Shop = <see cref="_currentShopLogin"/>.
    /// </summary>
    private Task<bool> ChayFlowMotShopAsync() => SyncFullAsync();

    /// <summary>
    /// Chờ tối đa <paramref name="ms"/> mili-giây nhưng THỨC NGAY khi phiên đóng cửa sổ (<see cref="ILoginSession.Closed"/>)
    /// hoặc bị hủy (<paramref name="ct"/>). ct-aware: bấm Dừng thoát ngay, KHÔNG đợi hết delay (caller kiểm
    /// <c>ct</c>/<c>IsClosed</c> sau khi trả về). Dùng cho delay 3–5' giữa các shop + chờ 1' khi không đọc được shop.
    /// </summary>
    private static Task InterruptibleDelayAsync(ILoginSession session, int ms, CancellationToken ct)
        => Task.WhenAny(session.Closed, Task.Delay(ms, ct));

    /// <summary>Giới hạn kích thước file phiếu đính kèm (5MB) — PDF phiếu giao thường ~100–300KB.</summary>
    private const long MaxSlipBytes = 5 * 1024 * 1024;

    /// <summary>Cờ CHỐNG CHỒNG lượt đẩy GSheet trên CÙNG phiên (0 = rảnh, 1 = đang đẩy). Bấm Sync liên tiếp
    /// trong lúc lượt đẩy nền trước chưa xong → bỏ qua lượt đẩy mới (Interlocked, thread-safe).</summary>
    private int _gsheetPushing;

    /// <summary>
    /// Kích hoạt đẩy GSheet CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết. KHÔNG await trong luồng sync
    /// (không giữ <see cref="_navigating"/>) vì push chỉ đụng DB + file + HTTP, không đụng trình duyệt → chạy
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
                // Nhớ trạng thái hủy + đã-có-vận-đơn VỪA tính của từng đơn được gửi → dùng cho MarkGsheetSynced
                // (ghi cờ gsheet_da_huy / gsheet_da_co_van_don).
                var daHuyByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coVanDonByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
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
                    // giờ có) → gửi lại để điền cột B. Không thỏa → bỏ qua (đã ghi đủ, không đẩy trùng) → settled.
                    var coFileBoSung = fileBase64 is not null;
                    var huyDoi = p.GsheetDaHuy is null || daHuy != (p.GsheetDaHuy == 1);
                    var vanDonMoi = coVanDon && p.GsheetDaCoVanDon != 1;
                    if (!(!p.DaGhiSheet || coFileBoSung || huyDoi || vanDonMoi))
                    {
                        settled.Add(p.OrderSn);
                        continue;
                    }

                    daHuyByMaDon[p.OrderSn] = daHuy;
                    coVanDonByMaDon[p.OrderSn] = coVanDon;

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
                        DoanhThu: p.TotalPrice,
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
                                    _services.Orders.MarkGsheetSynced(_accountId, r.MaDon, r.FileUrl, daHuy, coVanDon, tabName, DateTime.UtcNow);
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
    /// Chọn proxy theo thứ tự ưu tiên và ĐỒNG THỜI set <see cref="_kiotClient"/> — client nguồn KiotProxy
    /// của phiên để watchdog canh proxy:
    /// (1) POOL KiotProxy CHUNG → cấp key RẢNH cho phiên (<see cref="_acquireKiotKey"/>); phiên GIỮ key này
    ///     suốt đời (kể cả relaunch đổi IP), watchdog BẬT trên key đó,
    /// (2) danh sách proxy thủ công → round-robin BỀN, chia sẻ giữa các phiên (watchdog TẮT: <c>_kiotClient=null</c>),
    /// (3) IP máy (null → watchdog TẮT).
    /// <para>
    /// KHÔNG còn đọc <c>acc.ProxyKey</c> (cơ chế gán-cố-định cũ đã bỏ; giá trị cũ được migrate vào pool chung).
    /// Acquire chỉ gọi ở đây (một lần khi mở phiên) — relaunch đổi IP KHÔNG gọi lại (giữ nguyên key). Nếu pool
    /// rỗng → <see cref="_acquireKiotKey"/> trả null, rơi xuống proxy thủ công / IP máy.
    /// </para>
    /// </summary>
    private async Task<ProxyEntry?> SelectProxyAsync(CancellationToken ct)
    {
        // (1) Pool KiotProxy chung: cấp key rảnh cho phiên (giữ suốt đời phiên). Watchdog BẬT.
        var key = _acquireKiotKey(_accountId);
        if (key is not null)
        {
            _kiotClient = new KiotProxyClient(new[] { key });
            return await ProxySelector.SelectKiotProxyAsync(_kiotClient, _healthChecker, ct).ConfigureAwait(false);
        }

        // (2) Proxy thủ công → round-robin BỀN, chia sẻ giữa các phiên (watchdog TẮT).
        var manual = _services.Proxies.GetAll();
        if (manual.Count > 0)
        {
            _kiotClient = null;                                   // proxy thủ công → không canh
            return _nextManualProxy(manual);                      // round-robin BỀN, KHÔNG kiểm
        }

        // (3) IP máy (null → watchdog TẮT).
        _kiotClient = null;
        return null;
    }

    /// <summary>
    /// Luồng chạy nền của phiên. Bê nguyên logic từ <c>OpenSellerAsync</c>; thay modal bằng trạng thái;
    /// tôn trọng <paramref name="ct"/> để dừng nhanh khi người dùng bấm Dừng / thoát app.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Đọc tài khoản theo Id (KHÔNG đọc form) — dùng cho cả chọn proxy lẫn tự đăng nhập.
            var acc = _services.Accounts.GetById(_accountId);

            // Gắn nhãn email cho log (thay mặc định "TK {id}") để dòng log dễ nhận nguồn.
            if (!string.IsNullOrWhiteSpace(acc?.Email))
            {
                _logLabel = acc!.Email;
            }

            // Hồ sơ persistent riêng cho tài khoản này → mở lại vẫn còn đăng nhập.
            var baseDir = Path.GetDirectoryName(_services.Database.Path) ?? ".";

            // Trình duyệt người dùng chọn ở Cài đặt — đọc TƯƠI khi phiên bắt đầu (đổi trong Cài đặt CHỈ áp cho
            // phiên MỞ SAU khi lưu). Đọc TRƯỚC khi tính hồ sơ + đặt ở scope bao cả vòng relaunch để mọi lần
            // mở lại (kể cả relaunch đổi proxy) đều dùng CÙNG trình duyệt + CÙNG hồ sơ.
            var browserChoice = _services.Settings.GetBrowserChoice();

            // Hồ sơ RIÊNG theo (tài khoản × trình duyệt THỰC được mở): đổi trình duyệt = phiên sạch, phải
            // login lại bằng đúng fingerprint trình duyệt đó. Kind lấy từ cùng nguồn ResolveExecutable mà
            // OpenAsync dùng để launch nên hồ sơ + exe luôn khớp.
            var browserKind = BrowserLocator.ResolveBrowserKind(browserChoice);
            var userDataDir = BrowserProfilePaths.ForAccount(baseDir, _accountId, browserKind);

            // Cờ TOÀN CỤC "Xóa profile và tạo lại" (Cài đặt) BẬT → xóa hồ sơ hiện có của (tài khoản × trình
            // duyệt) rồi tạo lại sạch NGAY khi phiên BẮT ĐẦU (ở đây, TRƯỚC vòng relaunch) — nên đăng nhập lại
            // từ đầu (cookie DB vẫn được luồng login dùng). Chỉ xóa 1 LẦN lúc phiên mở mới; relaunch đổi proxy
            // KHÔNG chạy lại đoạn này nên hồ sơ vừa tạo được giữ. Xóa thất bại (hồ sơ bị khóa) → degrade êm:
            // ProfileJanitor đã retry + trả false, ta chỉ log cảnh báo rồi CHẠY TIẾP với hồ sơ cũ (không chặn sync).
            if (_services.Settings.GetSyncFreshProfile())
            {
                var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));
                if (ProfileJanitor.TryResetDirectory(userDataDir, log))
                {
                    _services.Log.Append(_logLabel, $"Đã xóa và tạo lại hồ sơ trình duyệt: {userDataDir}");
                }
                else
                {
                    _services.Log.Append(_logLabel,
                        $"CẢNH BÁO: không xóa được hồ sơ trình duyệt — chạy tiếp với hồ sơ cũ ({userDataDir}).");
                }
            }

            Directory.CreateDirectory(userDataDir);

            // 1) Chọn proxy theo thứ tự ưu tiên (pool KiotProxy → thủ công → IP máy) + set _kiotClient để
            //    watchdog canh. Acquire key từ pool xảy ra ở đây MỘT LẦN (giữ suốt đời phiên).
            SetStatus(SessionState.Opening, "Đang kiểm tra proxy...");
            _currentProxy = await SelectProxyAsync(ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // 2) Đảm bảo trình duyệt đã cài (tải lần đầu ~150MB) — chạy nền.
            SetStatus(SessionState.Opening, "Đang chuẩn bị trình duyệt...");
            var installCode = await Task.Run(() => _loginService.EnsureBrowserInstalled(browserChoice), ct).ConfigureAwait(false);
            if (installCode != 0)
            {
                SetError("Không cài được trình duyệt. Kiểm tra mạng rồi thử lại.");
                return;
            }

            // Random riêng cho nhịp watchdog: jitter 2–3' (kiểm proxy thường xuyên để proxy chết được phát hiện
            // & đổi trong vài phút — tránh trang bán hàng chết tới ~10' rồi người dùng phải Dừng/mở lại tay).
            var proxyRng = new Random();
            bool firstOpen = true;
            // Captcha-retry: đếm số lần đã đóng phiên + xóa hồ sơ + mở lại vì gặp captcha (tối đa 2 → tổng 3
            // lượt thử). Đặt NGOÀI vòng relaunch để đếm qua các lần mở lại.
            int captchaResets = 0;
            const int MaxCaptchaResets = 2;
            // Chốt chặn TUYỆT ĐỐI: cap an toàn 12h tính từ ĐẦU phiên, áp cho MỌI lần relaunch (KHÔNG reset mỗi
            // lần mở lại). Tín hiệu kết thúc CHÍNH vẫn là "không còn cửa sổ nào".
            var hardCap = DateTime.UtcNow.AddHours(12);

            // Chu kỳ theo dõi đơn (phút): đọc MỘT LẦN từ Cài đặt khi phiên bắt đầu (chụp vào biến — KHÔNG đọc
            // lại giữa await). Đổi trong Cài đặt CHỈ áp cho phiên MỞ SAU khi lưu (phiên đang chạy giữ số cũ,
            // kể cả khi relaunch đổi proxy — đơn giản, chấp nhận). Đã kẹp [1,1440] trong config.
            var orderIntervalMin = _services.Settings.GetOrderIntervalMinutes();

            // ===== VÒNG RELAUNCH NGOÀI =====
            // Mỗi vòng = một lần mở Brave (với _currentProxy hiện tại) + vòng poll bên trong. Khi proxy chết,
            // watchdog đặt relaunchForProxy=true → thoát poll → dispose Brave → quay lại mở LẠI với proxy mới.
            // State GIỮ Running/Opening xuyên suốt, KHÔNG rơi vào finally NGOÀI giữa chừng (nếu State thành
            // Stopped, AccountSessionManager sẽ GỠ phiên khỏi dict).
            while (!ct.IsCancellationRequested)
            {
                bool relaunchForProxy = false;
                // Gặp captcha ở lần mở này → sau khi dispose sẽ xóa hồ sơ + mở lại (đếm bởi captchaResets).
                bool relaunchForCaptcha = false;
                // ĐẦU MỖI vòng mở/relaunch: CHƯA sẵn sàng — phải đăng nhập lại + đọc số lần đầu mới bật lại
                // (kín cả đường proxy-chết-relaunch: ToShipCount giữ số cũ nhưng cờ này về false).
                _readyForActions = false;

                // 3) Mở cửa sổ trình duyệt (profile persistent) tới trang bán hàng.
                SetStatus(SessionState.Opening,
                    firstOpen ? "Đang mở cửa sổ trình duyệt..." : "Đang mở lại trình duyệt với proxy mới...");
                // Lần mở ĐẦU: lỗi → SetError + return ngay (giữ hành vi cũ). ĐƯỜNG RELAUNCH: settle + retry vì hồ
                // sơ persistent vừa dispose có thể chưa nhả khóa hẳn (dù DisposeAsync đã WaitForExit). Phân biệt
                // HỦY bằng ct.IsCancellationRequested (không bằng loại exception) vì OpenAsync bọc MỌI lỗi kể cả
                // OperationCanceledException thành InvalidOperationException.
                const int MaxReopenAttempts = 3;
                ILoginSession? session = null;
                for (int attempt = 1; session is null; attempt++)
                {
                    if (!firstOpen)
                    {
                        // Relaunch: chờ settle để khóa hồ sơ nhả nốt (biên an toàn thêm sau WaitForExit).
                        try { await Task.Delay(proxyRng.Next(800, 1500), ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                    }
                    try
                    {
                        session = await _loginService.OpenAsync(userDataDir, _currentProxy, browserChoice, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); // Dừng → catch NGOÀI xử như HỦY
                        if (firstOpen || attempt >= MaxReopenAttempts) { SetError(ex.Message); return; }
                        SetStatus(SessionState.Opening, $"Mở lại trình duyệt chưa được (thử {attempt}/{MaxReopenAttempts})...");
                        try { await Task.Delay(2000 * attempt, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                    }
                }

                _session = session; // expose BraveProcess cho Plan B (focus cửa sổ)

                try
                {
                    // 4) ĐIỀU PHỐI đăng nhập QUA NỀN TẢNG TÀI KHOẢN PHỤ (mô hình 1 subaccount = nhiều shop): phiên mở
                    //    thẳng subaccount.shopee.com → TryLoginSubaccountAsync tự điền form subaccount, mở hộp thư cho
                    //    người dùng tự lấy mã, chờ nhập code tới khi ĐÃ ĐĂNG NHẬP subaccount (KHÔNG còn click "Kênh Người
                    //    bán"). entered=true → sau điều phối chạy VÒNG LẶP SHOP (đọc /portal/shop → từng shop mở tab →
                    //    Check/Process/Sync → đóng tab → delay 3–5' → lặp lại). entered=false → GIỮ cửa sổ cho người dùng
                    //    thao tác tay (poll giữ cửa sổ, KHÔNG chạy loop). MỌI nhánh degrade PHẢI bật _readyForActions
                    //    (kẻo WaitForSessionReady treo 5'); riêng nhánh captcha-relaunch KHÔNG bật (phiên sắp tháo dỡ + mở lại).
                    // Logger dùng chung cho điều phối + vòng lặp shop (đọc _logLabel tại call-time → đổi nhãn theo shop).
                    var loginLog = (Action<string>)(m => _services.Log.Append(_logLabel, m));
                    bool entered = false;
                    if (acc is not null)
                    {
                        SetStatus(SessionState.Running, "Đang đăng nhập Nền tảng tài khoản phụ (subaccount)...");
                        try { entered = await session.TryLoginSubaccountAsync(acc.Email, acc.Password, acc.VerifyEmail, acc.VerifyEmailPassword, loginLog, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch { entered = false; }

                        if (!entered)
                        {
                            _services.Log.Append(_logLabel,
                                "Không tự đăng nhập được Nền tảng tài khoản phụ — GIỮ cửa sổ để bạn thao tác tay.");
                        }
                        else
                        {
                            // Đã vào Seller Centre (banhang.shopee.vn ở Pages[0]) → state machine cũ chạy làm lưới an toàn.
                            // Chờ trạng thái trang rõ ràng (SPA có thể còn render) — poll tối đa ~12s; Unknown quá lâu
                            // → đi tiếp như hôm nay. Hồ sơ đã đăng nhập → DetectPageStateAsync trả LoggedIn ngay (không đợi).
                            var state = ShopeePageState.Unknown;
                            var detectDeadline = DateTime.UtcNow.AddSeconds(12);
                            do
                            {
                                state = await session.DetectPageStateAsync(ct).ConfigureAwait(false);
                                if (state != ShopeePageState.Unknown) break;
                                await Task.Delay(700, ct).ConfigureAwait(false);
                            }
                            while (DateTime.UtcNow < detectDeadline);

                            // a) Form đăng nhập → tự điền user/pass (KIỂU NGƯỜI) rồi chờ trang đổi, phát hiện lại.
                            if (state == ShopeePageState.LoginForm && !string.IsNullOrEmpty(acc.Password))
                            {
                                SetStatus(SessionState.Running, "Đang tự đăng nhập (kiểu người)...");
                                try { await session.TryHumanLoginAsync(acc.Email, acc.Password, ct).ConfigureAwait(false); }
                                catch (OperationCanceledException) { throw; }
                                catch { /* không phá luồng — người dùng tự nhập tay nếu cần */ }

                                await Task.Delay(8000, ct).ConfigureAwait(false); // chờ sau bấm đăng nhập
                                state = await session.DetectPageStateAsync(ct).ConfigureAwait(false);
                            }

                            // b) Trang verify → tự xác minh qua email Hotmail (nếu có cấu hình); thiếu cấu hình/thất
                            //    bại → GIỮ phiên như hôm nay (người dùng verify tay), vẫn bật sẵn sàng ở dưới.
                            if (state == ShopeePageState.Verify)
                            {
                                if (!string.IsNullOrWhiteSpace(acc.VerifyEmail) && !string.IsNullOrWhiteSpace(acc.VerifyEmailPassword))
                                {
                                    SetStatus(SessionState.Running, "Shopee yêu cầu xác minh — đang tự xác minh qua email...");
                                    bool verified;
                                    try { verified = await session.TryVerifyByEmailAsync(acc.VerifyEmail, acc.VerifyEmailPassword, _services.Settings.GetAutoConfirmEmail(), loginLog, ct).ConfigureAwait(false); }
                                    catch (OperationCanceledException) { throw; }
                                    catch { verified = false; }

                                    if (verified)
                                    {
                                        state = await session.DetectPageStateAsync(ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        _services.Log.Append(_logLabel,
                                            "Không tự xác minh qua email được — GIỮ cửa sổ để bạn xác minh tay.");
                                    }
                                }
                                else
                                {
                                    _services.Log.Append(_logLabel,
                                        "Shopee yêu cầu xác minh nhưng chưa cấu hình Email xác minh cho tài khoản — " +
                                        "hãy xác minh tay hoặc thêm Email xác minh ở màn Tài khoản.");
                                }
                            }

                            // c) Trang captcha → đóng phiên, xóa hồ sơ, mở lại (tối đa MaxCaptchaResets lần).
                            if (state == ShopeePageState.Captcha)
                            {
                                if (captchaResets < MaxCaptchaResets)
                                {
                                    captchaResets++;
                                    _services.Log.Append(_logLabel,
                                        $"Gặp captcha — đóng phiên, xóa hồ sơ trình duyệt, thử lại (lần {captchaResets}/{MaxCaptchaResets}).");
                                    SetStatus(SessionState.Running, $"Gặp captcha — đổi hồ sơ, mở lại (lần {captchaResets}/{MaxCaptchaResets})...");
                                    relaunchForCaptcha = true;
                                }
                                else
                                {
                                    _services.Log.Append(_logLabel,
                                        $"Captcha lặp lại sau {MaxCaptchaResets} lần xóa hồ sơ — GIỮ cửa sổ để bạn xử lý tay.");
                                }
                            }
                        }
                    }

                    // Sau điều phối: nhánh KHÔNG relaunch-captcha → BẬT sẵn sàng ngay (kể cả degrade verify/
                    // captcha-hết-lượt) để nút Sync/Kiểm tra hàng loạt không đợi chu kỳ đọc đơn lần đầu; nhánh
                    // captcha-relaunch → KHÔNG bật, rơi xuống finally dispose rồi reset hồ sơ + mở lại.
                    if (!relaunchForCaptcha)
                    {
                        _readyForActions = true;
                    }

                    // 5) Tự bắt & lưu cookie trong lúc cửa sổ mở; kết thúc khi người dùng đóng hết cửa sổ.
                    if (!relaunchForCaptcha)
                    {
                        SetStatus(SessionState.Running,
                            $"Đã mở trình duyệt. Đăng nhập xong app sẽ tự theo dõi đơn mỗi {orderIntervalMin}'; đóng cửa sổ để dừng.");
                    }
                    if (firstOpen)
                    {
                        ToShipCount = null; // reset CHỈ ở lần mở đầu; relaunch giữ số cũ (nhịp đọc đơn tự làm mới).
                    }

                    string? lastSaved = null;
                    // Cần 0 cửa sổ ở 2 vòng LIÊN TIẾP mới coi là đã đóng (tránh thoát nhầm lúc chuyển tab).
                    int zeroPageStreak = 0;
                    // Nhịp watchdog proxy: kiểm proxy đang gán còn sống không, jitter 2–3' (hồi nhanh khi proxy xoay).
                    var nextProxyCheck = DateTime.UtcNow.AddSeconds(proxyRng.Next(120, 180));

                    if (!relaunchForCaptcha && entered)
                    {
                        // ===== VÒNG LẶP SHOP (mô hình 1 subaccount = nhiều shop) =====
                        // Đọc /portal/shop → mỗi shop: mở tab "Chi tiết" → Check/Process/Sync (chạy trên tab shop qua
                        // WorkPage) → đóng tab → delay ngẫu nhiên 3–5' → shop kế; hết danh sách lặp lại từ đầu tới khi
                        // Dừng/đóng cửa sổ. Tôn trọng ct/IsClosed/OpenPageCount==0 hai vòng/hardCap; watchdog proxy đầu
                        // mỗi vòng ngoài (proxy chết → relaunchForProxy → break ra dispose + relaunch, đăng nhập lại).
                        var shopRng = new Random();
                        _shopLoopRunning = true;
                        try
                        {
                            while (!relaunchForProxy && !session.IsClosed && DateTime.UtcNow < hardCap && !ct.IsCancellationRequested)
                            {
                                // Watchdog proxy đầu vòng ngoài (chỉ phiên KiotProxy). Proxy chết → đổi + break relaunch.
                                if (_kiotClient is not null && DateTime.UtcNow >= nextProxyCheck)
                                {
                                    _navigating = true;
                                    try
                                    {
                                        var replacement = await ProxyWatchdog.TryGetReplacementAsync(
                                            _kiotClient, _healthChecker, _currentProxy, ProxyRecheckDelayMs, ct).ConfigureAwait(false);
                                        if (replacement is not null)
                                        {
                                            _currentProxy = replacement;
                                            relaunchForProxy = true;
                                            _readyForActions = false;
                                        }
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch { /* watchdog lỗi (mạng/API) → bỏ qua, thử lại chu kỳ sau */ }
                                    finally { _navigating = false; }

                                    nextProxyCheck = DateTime.UtcNow.AddSeconds(proxyRng.Next(120, 180));
                                    if (relaunchForProxy)
                                    {
                                        SetStatus(SessionState.Running, "Proxy cũ chết — đang đổi proxy, mở lại trình duyệt...");
                                        break; // thoát loop → dispose Brave → relaunch với proxy mới
                                    }
                                }

                                // Đọc danh sách shop (Goto /portal/shop trên Pages[0], tự SetWorkPage(null)).
                                IReadOnlyList<ShopListItem> shops;
                                try { shops = await session.ReadShopListAsync(loginLog, ct).ConfigureAwait(false); }
                                catch (OperationCanceledException) { throw; }
                                catch { shops = Array.Empty<ShopListItem>(); }

                                // Lưu cookie 1 lần/đầu vòng (đã về trang danh sách — đã đăng nhập). CHỈ lưu khi có cookie
                                // ĐĂNG NHẬP Shopee (tránh đè cookie hợp lệ cũ bằng cookie theo dõi).
                                try
                                {
                                    var json = await session.CaptureCookiesJsonAsync().ConfigureAwait(false);
                                    if (!string.IsNullOrEmpty(json) && json != lastSaved && ShopeeLoginCookies.IsLoggedIn(json) && TrySaveCookie(json))
                                    {
                                        lastSaved = json;
                                    }
                                }
                                catch { /* context đã đóng giữa chừng — bỏ qua */ }

                                if (shops.Count == 0)
                                {
                                    _services.Log.Append(_logLabel, "Không đọc được danh sách shop — thử lại sau 1'.");
                                    await InterruptibleDelayAsync(session, 60_000, ct).ConfigureAwait(false);
                                    continue;
                                }

                                foreach (var shop in shops)
                                {
                                    if (ct.IsCancellationRequested || session.IsClosed) break;
                                    if (session.OpenPageCount == 0) { if (++zeroPageStreak >= 2) break; }
                                    else zeroPageStreak = 0;

                                    var shopLabel = string.IsNullOrWhiteSpace(shop.LoginName) ? shop.ShopName : shop.LoginName;
                                    _logLabel = string.IsNullOrWhiteSpace(shopLabel) ? (acc?.Email ?? _logLabel) : shopLabel;
                                    _currentShopId = shop.ShopId;
                                    _currentShopLogin = shopLabel;
                                    SetStatus(SessionState.Running, $"Đang xử lý shop {shopLabel}...");
                                    try
                                    {
                                        bool opened;
                                        _navigating = true;
                                        try { opened = await session.OpenShopDetailAsync(shop, loginLog, ct).ConfigureAwait(false); }
                                        finally { _navigating = false; }

                                        if (opened)
                                        {
                                            // Flow 1 shop = thân SyncFullAsync (Check → Process nếu ToShip>0 → Sync). Các hàm
                                            // con chạy trên WorkPage() (tab shop) và TỰ quản _navigating → KHÔNG giữ
                                            // _navigating quanh cả cụm (kẻo các hàm con bail vì _navigating).
                                            await ChayFlowMotShopAsync().ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            _services.Log.Append(_logLabel, $"Không mở được shop {shopLabel} — bỏ qua lượt này.");
                                        }

                                        _navigating = true;
                                        try { await session.CloseShopTabAsync(ct).ConfigureAwait(false); }
                                        finally { _navigating = false; }
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch (Exception ex)
                                    {
                                        _services.Log.Append(_logLabel, "Lỗi khi xử lý shop: " + ex.Message);
                                        try { _navigating = true; await session.CloseShopTabAsync(ct).ConfigureAwait(false); }
                                        catch (OperationCanceledException) { throw; }
                                        catch { /* best-effort đóng tab */ }
                                        finally { _navigating = false; }
                                    }
                                    finally
                                    {
                                        _currentShopId = null;
                                        _currentShopLogin = null;
                                        _logLabel = acc?.Email ?? _logLabel; // trả nhãn về email tài khoản giữa các shop
                                    }

                                    if (ct.IsCancellationRequested || session.IsClosed) break;

                                    // Delay ngẫu nhiên 3–5' giữa các shop (kể cả trước khi lặp lại từ đầu). ct-aware +
                                    // THỨC NGAY khi đóng cửa sổ (session.Closed).
                                    await InterruptibleDelayAsync(session, shopRng.Next(180_000, 300_001), ct).ConfigureAwait(false);
                                }
                            }
                        }
                        finally
                        {
                            _shopLoopRunning = false;
                            _currentShopId = null;
                            _currentShopLogin = null;
                            _logLabel = acc?.Email ?? _logLabel;
                        }
                    }
                    else
                    {
                        // ===== POLL GIỮ CỬA SỔ (chưa đăng nhập subaccount / degrade). relaunchForCaptcha → while false
                        //       ngay ở entry → bỏ qua, xuống finally dispose + reset hồ sơ + mở lại ở dưới. =====
                        const int PollMs = 1000;
                        const int OrderRetrySec = 30;
                        var nextOrderCheck = DateTime.UtcNow;
                        bool firstOrderCheck = true;
                        while (!relaunchForCaptcha && !session.IsClosed && DateTime.UtcNow < hardCap && !ct.IsCancellationRequested)
                        {
                            await Task.WhenAny(session.Closed, Task.Delay(PollMs, ct)).ConfigureAwait(false);

                            if (ct.IsCancellationRequested)
                            {
                                break;
                            }

                            if (session.OpenPageCount == 0)
                            {
                                if (++zeroPageStreak >= 2)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                zeroPageStreak = 0;
                            }

                            string json;
                            try { json = await session.CaptureCookiesJsonAsync().ConfigureAwait(false); }
                            catch { break; } // context đã đóng giữa chừng

                            // CHỈ lưu khi đã có cookie ĐĂNG NHẬP Shopee (tránh đè cookie hợp lệ cũ bằng cookie theo dõi).
                            if (!string.IsNullOrEmpty(json) && json != lastSaved && ShopeeLoginCookies.IsLoggedIn(json))
                            {
                                if (TrySaveCookie(json))
                                {
                                    lastSaved = json;
                                }
                            }

                            // Nhịp theo dõi đơn "Chờ Lấy Hàng": tới hạn thì reload + đọc số. Lần đầu KHÔNG reload.
                            // Đang điều hướng xử lý đơn (_navigating) → BỎ QUA nhịp này (reload sẽ phá thao tác giữa chừng).
                            if (!_navigating && DateTime.UtcNow >= nextOrderCheck)
                            {
                                // GIỮ cờ _navigating suốt lượt đọc (có thể kéo dài ~38s: reload 30s + poll 8s) để
                                // loại trừ HAI CHIỀU với nút Kiểm tra / Xử lý đơn — không cho Goto/click tay chạy
                                // chồng lên lượt reload đang bay trên cùng trang (hai bên cùng fail ảo).
                                _navigating = true;
                                int? count;
                                try
                                {
                                    count = await session.ReadToShipCountAsync(reload: !firstOrderCheck, ct).ConfigureAwait(false);
                                }
                                finally
                                {
                                    _navigating = false;
                                }

                                if (count is int n)
                                {
                                    // Điểm ĐĂNG NHẬP-OK ĐẦU TIÊN của lần mở này: đọc được số "Chờ Lấy Hàng" ⇒ đang
                                    // ở trang chủ đã đăng nhập. Nếu tài khoản còn mang cờ "TK chưa xác nhận" (vd
                                    // người dùng vừa xác minh tay xong) thì GỠ để nhãn đỏ tự lành.
                                    if (firstOrderCheck)
                                    {
                                        TryClearVerifyFailedAfterLogin();
                                    }
                                    firstOrderCheck = false;
                                    ToShipCount = n; // VM tự định dạng dòng hiển thị theo số này
                                    nextOrderCheck = DateTime.UtcNow.AddMinutes(orderIntervalMin); // đã đăng nhập → chu kỳ cấu hình
                                }
                                else
                                {
                                    // Chưa đăng nhập / chưa đọc được → thử lại sớm, KHÔNG reload.
                                    nextOrderCheck = DateTime.UtcNow.AddSeconds(OrderRetrySec);
                                }
                            }

                            // ===== NHỊP WATCHDOG PROXY (~10') =====
                            // CHỈ với phiên nguồn KiotProxy (_kiotClient != null) và khi KHÔNG đang điều hướng
                            // (loại trừ với đọc đơn / nút Kiểm tra / Xử lý đơn). Bật _navigating SUỐT lượt kiểm
                            // (health-check tối đa ~8s×2 + 5s ⇒ ~21s) để không thao tác nào chạy chồng lên.
                            if (_kiotClient is not null && !_navigating && DateTime.UtcNow >= nextProxyCheck)
                            {
                                _navigating = true;
                                try
                                {
                                    var replacement = await ProxyWatchdog.TryGetReplacementAsync(
                                        _kiotClient, _healthChecker, _currentProxy, ProxyRecheckDelayMs, ct).ConfigureAwait(false);
                                    if (replacement is not null)
                                    {
                                        _currentProxy = replacement;
                                        relaunchForProxy = true;
                                        // Sắp dispose + relaunch (State GIỮ Running suốt lúc đổi proxy) → tắt sẵn
                                        // sàng NGAY để nút Sync/Kiểm tra không chạy vào phiên đang bị tháo dỡ.
                                        _readyForActions = false;
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw; // Dừng chủ động → để catch NGOÀI xử như HỦY.
                                }
                                catch
                                {
                                    /* watchdog lỗi (mạng/API) → bỏ qua, thử lại chu kỳ sau */
                                }
                                finally
                                {
                                    _navigating = false;
                                }

                                nextProxyCheck = DateTime.UtcNow.AddSeconds(proxyRng.Next(120, 180));
                                if (relaunchForProxy)
                                {
                                    SetStatus(SessionState.Running, "Proxy cũ chết — đang đổi proxy, mở lại trình duyệt...");
                                    break; // thoát poll → dispose Brave → relaunch với proxy mới
                                }
                            }
                        }
                    } // hết nhánh else (poll giữ cửa sổ)

                    // Lần bắt cookie CHỐT trước khi dispose (đăng nhập xong đóng cửa sổ ngay vẫn bắt kịp).
                    try
                    {
                        var json = await session.CaptureCookiesJsonAsync().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(json) && json != lastSaved && ShopeeLoginCookies.IsLoggedIn(json))
                        {
                            if (TrySaveCookie(json))
                            {
                                lastSaved = json;
                            }
                        }
                    }
                    catch { /* browser đã chết hẳn — bỏ qua */ }

                    // Kết quả trung thực (KHÔNG khẳng định "chưa đăng nhập"). Relaunch đổi proxy / né captcha →
                    // GIỮ status đã đặt ở trên, KHÔNG đè bằng câu tổng kết.
                    if (!relaunchForProxy && !relaunchForCaptcha)
                    {
                        StatusText = lastSaved != null
                            ? "Đã lưu cookie đăng nhập vào tài khoản."
                            : "Chưa lưu được cookie. Nếu đã đăng nhập, phiên vẫn được giữ trong hồ sơ (lần sau mở lại vẫn còn).";
                    }
                }
                finally
                {
                    // Dispose (kill Brave) sau MỖI vòng — dù kết thúc bình thường hay để relaunch với proxy mới.
                    try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* đã chết — bỏ qua */ }
                    if (ReferenceEquals(_session, session))
                    {
                        _session = null;
                    }
                }

                // Gặp captcha → phiên ĐÃ dispose ở finally (Brave chết + WaitForExit) → an toàn xóa hồ sơ, rồi
                // mở lại từ đầu (chuỗi detect/login lại chạy lại). Xóa thất bại (khóa) → degrade êm: janitor đã
                // retry + log, vẫn mở lại với hồ sơ cũ (không chặn — nhưng thường captcha lại).
                if (relaunchForCaptcha)
                {
                    var log = (Action<string>)(m => _services.Log.Append(_logLabel, m));
                    ProfileJanitor.TryResetDirectory(userDataDir, log);
                    firstOpen = false; // vòng sau coi như relaunch (settle + retry mở như đường proxy)
                    continue;
                }

                if (!relaunchForProxy)
                {
                    break; // kết thúc bình thường (đóng cửa sổ / hết giờ) → ra ngoài → finally NGOÀI → Stopped
                }
                firstOpen = false; // vòng sau: relaunch với _currentProxy mới
            }
        }
        catch (OperationCanceledException)
        {
            // Bị dừng chủ động (Dừng / thoát app) — không phải lỗi.
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            _session = null;
            _readyForActions = false; // phiên kết thúc (Stopped/Error/hủy) → không còn sẵn sàng

            // NHẢ KiotProxy key về pool — CHỈ ở finally NGOÀI cùng này (phiên đã đóng HẲN), KHÔNG giữa các
            // lần relaunch: phiên giữ NGUYÊN key suốt đời (relaunch/đổi IP do watchdog dùng lại key cũ). Gọi
            // đúng MỘT LẦN cho mỗi vòng đời RunAsync; an toàn cả khi phiên chưa cấp key nào (Release là no-op).
            _releaseKiotKey(_accountId);

            lock (_lifecycleLock)
            {
                // Kết thúc bình thường / bị hủy → Stopped; giữ nguyên Error để còn hiển thị lỗi.
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
