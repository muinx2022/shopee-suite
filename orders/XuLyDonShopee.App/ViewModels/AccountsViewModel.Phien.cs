using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// <b>Phiên chạy</b> của màn Tài khoản: mở/dừng phiên (một tài khoản hoặc hàng loạt theo tick), lát cắt "Chạy
/// thử (bridge)" extension↔C#, đổ trạng thái phiên vào form và ghi cookie phiên bắt được về DB.
/// <para>Là phần <c>partial</c> của <see cref="AccountsViewModel"/> — property công khai vẫn nằm trên VM chính
/// (XAML bind thẳng <c>BusyStatus</c>/<c>OrderStatus</c>/<c>CanRun</c>/<c>CanStop</c>).</para>
/// </summary>
public partial class AccountsViewModel
{
    /// <summary>Dòng hướng dẫn/trạng thái hiển thị (đổ từ phiên của tài khoản đang chọn; null = ẩn).</summary>
    [ObservableProperty]
    private string? _busyStatus;

    /// <summary>Trạng thái theo dõi đơn "Chờ Lấy Hàng" (đổ từ phiên của tài khoản đang chọn; null = ẩn).</summary>
    [ObservableProperty]
    private string? _orderStatus;

    /// <summary>Cho dừng khi tài khoản đang chọn có phiên đang chạy.</summary>
    public bool CanStopSeller => _editingId is not null && _services.Sessions.IsRunning(_editingId ?? -1);

    /// <summary>
    /// Cho nút "Chạy" khi đang xem/sửa một tài khoản ĐÃ LƯU (có Id) — KHÔNG phụ thuộc phiên đang chạy. Bấm =
    /// MỞ PHIÊN (đăng nhập subaccount rồi tự lặp qua các shop). Tài khoản mới chưa lưu (IsNew) → tắt nút.
    /// </summary>
    public bool CanRun => IsEditing && !IsNew && _editingId is not null;

    /// <summary>Nút "■ Dừng" bật khi có phiên production đang chạy HOẶC đang chạy thử bridge (để hủy được cả hai).</summary>
    public bool CanStop => CanStopSeller || _bridgeRunning;

    /// <summary>Nhãn nguồn log cho các thông báo cấp-BATCH (không thuộc một shop cụ thể) — ghi file &amp; phân
    /// biệt với log per-account (per-account dùng email của shop).</summary>
    private const string BatchLogSource = "Hàng loạt";

    /// <summary>
    /// "Truy cập TK" (nút trên dòng TK chưa xác nhận): CHỌN tài khoản đó (đổ Chi tiết + nhật ký) rồi TỰ MỞ phiên
    /// trình duyệt để người dùng xác minh tay trên cửa sổ Brave. Phiên đang chạy sẵn → chỉ chọn + báo (KHÔNG mở
    /// trùng). Mở phiên bằng ĐÚNG đường sẵn có <see cref="AccountSessionManager.Start"/> (idempotent) — không chế
    /// đường mở mới. Gọi từ code-behind (giống <see cref="ToggleRowTick"/>).
    /// </summary>
    public void TruyCapTk(AccountRowViewModel row)
    {
        // Chọn dòng: OnSelectedRowChanged tự nạp form + đưa cửa sổ Brave (nếu có) ra trước.
        SelectedRow = row;

        var id = row.Id;
        var email = row.Email;
        if (_services.Sessions.IsRunning(id))
        {
            const string msg = "Phiên đang mở — xác minh trên cửa sổ Brave của tài khoản này.";
            _services.Log.Append(email, msg);
            BusyStatus = msg;
            return;
        }

        _services.Log.Append(email, "Truy cập TK: mở trang bán hàng để xác minh tay...");
        _services.Sessions.Start(id);
        UpdateSelectedSessionStatus();
    }

    /// <summary>"Dừng đã chọn" — dừng phiên của mọi tài khoản đang tick (Stop tự no-op nếu không có phiên).</summary>
    [RelayCommand]
    private void StopSelected()
    {
        foreach (var row in Accounts.Where(r => r.IsSelected).ToList())
        {
            _services.Sessions.Stop(row.Id);
        }

        UpdateSelectedSessionStatus();
    }

    /// <summary>"Dừng tất cả" — dừng mọi phiên đang chạy (đóng &amp; kill hết Brave).</summary>
    [RelayCommand]
    private async Task StopAllAsync()
    {
        await _services.Sessions.StopAllAsync();
        UpdateSelectedSessionStatus();
    }

    /// <summary>Dừng phiên của tài khoản đang chọn (đóng &amp; kill Brave của phiên đó, không ảnh hưởng phiên khác).
    /// Chạy được CẢ khi chỉ có phiên "chạy thử bridge" (không có phiên production): vẫn hủy + đóng trình duyệt.</summary>
    [RelayCommand]
    private void Stop()
    {
        var wasBridge = _bridgeRunning;

        // Hủy lát cắt bridge (cancel _bridgeCts → đóng cả trình duyệt điều khiển Playwright lẫn trình duyệt sạch)
        // + đóng cửa sổ POC. Chạy KỂ CẢ khi _editingId null (chỉ có bridge, chưa mở phiên production nào).
        TryKillPoc();

        if (_editingId is long accountId)
        {
            _services.Sessions.Stop(accountId);
            UpdateSelectedSessionStatus();
        }

        if (wasBridge)
        {
            var email = (_editingId is long id ? _services.Accounts.GetById(id)?.Email : null) ?? EditEmail;
            _services.Log.Append(email, "Đã dừng chạy thử + đóng trình duyệt.");
        }
    }

    /// <summary>
    /// "Chạy" — nút hành động chính màn Tài khoản (mô hình 1 subaccount = nhiều shop): MỞ PHIÊN cho tài khoản
    /// đang xem (khởi động Brave → đăng nhập subaccount → tự lặp qua các shop). Idempotent qua
    /// <see cref="AccountSessionManager.Start"/> (đang chạy thì thôi, không mở trùng). Vòng lặp shop tự chạy
    /// trong RunAsync sau đăng nhập nên KHÔNG gọi <c>SyncFullAsync</c> thủ công (tránh giẫm vòng lặp). Phiên
    /// đang chạy vòng lặp shop → chỉ log rồi thôi.
    /// </summary>
    [RelayCommand]
    private void Run()
    {
        // Chụp accountId + email — bám theo tài khoản đang mở trên form.
        if (_editingId is not long accountId)
        {
            return;
        }

        var email = _services.Accounts.GetById(accountId)?.Email ?? EditEmail;

        // Phiên đang chạy vòng lặp shop → không mở lại (Start vốn idempotent, nhưng báo cho rõ rồi thôi).
        if (_services.Sessions.Get(accountId) is { IsShopLoopRunning: true })
        {
            _services.Log.Append(email, "Đang chạy rồi.");
            return;
        }

        TryKillPoc(); // đóng cửa sổ POC "mở sạch" (nếu còn) trước khi phiên production launch — tránh khoá hồ sơ chung.
        _services.Log.Append(email, "Chạy: mở phiên — đăng nhập rồi tự lặp qua các shop...");
        _services.Sessions.Start(accountId); // mở phiên; vòng lặp shop tự chạy trong RunAsync
        UpdateSelectedSessionStatus();
    }

    /// <summary>
    /// "Chạy đã chọn" (HÀNG LOẠT) — với MỌI tài khoản đang tick: MỞ PHIÊN (<see cref="AccountSessionManager.Start"/>,
    /// idempotent). Mỗi phiên tự đăng nhập subaccount rồi lặp qua các shop của nó (RunAsync) nên KHÔNG chạy hành
    /// động thủ công (Sync/Kiểm tra) — vòng lặp shop tự làm. Chụp danh sách (id, email) các dòng tick MỘT LẦN —
    /// KHÔNG giữ tham chiếu <see cref="AccountRowViewModel"/>. Rỗng → log "Chưa tick tài khoản nào." rồi thôi;
    /// phiên đang chạy vòng lặp shop → bỏ qua (log "Đang chạy rồi.").
    /// </summary>
    [RelayCommand]
    private void RunSelected()
    {
        // Chụp (id, email) của các dòng ĐANG tick MỘT LẦN.
        var targets = Accounts
            .Where(r => r.IsSelected)
            .Select(r => (Id: r.Id, Email: r.Email))
            .ToList();

        if (targets.Count == 0)
        {
            _services.Log.Append(BatchLogSource, "Chưa tick tài khoản nào.");
            return;
        }

        foreach (var target in targets)
        {
            // Phiên đang chạy vòng lặp shop → không mở lại (Start idempotent, nhưng báo cho rõ).
            if (_services.Sessions.Get(target.Id) is { IsShopLoopRunning: true })
            {
                _services.Log.Append(target.Email, "Đang chạy rồi.");
                continue;
            }

            _services.Sessions.Start(target.Id); // mở phiên; vòng lặp shop tự chạy trong RunAsync
        }

        UpdateSelectedSessionStatus();
        _services.Log.Append(BatchLogSource, $"Đã mở phiên chạy cho {targets.Count} tài khoản đã chọn.");
    }

    // ===================== Lát cắt "Chạy thử (bridge)" — extension ↔ C# =====================

    /// <summary>Tiến trình trình duyệt SẠCH (không CDP) đang mở cho tài khoản đang chọn; null = không có.</summary>
    private System.Diagnostics.Process? _pocProcess;

    /// <summary>Phiên cầu nối GĐ1 đang chạy (WebSocket ↔ extension) cho tài khoản đang chọn; null = không có.</summary>
    private OrdersBridgeSession? _bridgeSession;

    /// <summary>Nguồn huỷ cho lát cắt cầu nối đang chạy (■ Dừng → cancel).</summary>
    private System.Threading.CancellationTokenSource? _bridgeCts;

    /// <summary>Đang chạy lát cắt cầu nối → chặn bấm lại (tránh mở trùng phiên/khoá hồ sơ chung).</summary>
    private bool _bridgeRunning;

    /// <summary>
    /// "🧪 Chạy thử (đăng nhập + shop)" — GĐ2 CẦU NỐI extension↔C#: mở trình duyệt SẠCH (KHÔNG Playwright/CDP,
    /// KHÔNG remote-debugging-port, KHÔNG proxy) với ĐÚNG hồ sơ persistent của tài khoản đang xem → mở
    /// <c>subaccount.shopee.com</c> kèm hash <c>#_od_ws=&lt;port&gt;</c>; extension nối WebSocket rồi: tự điền form
    /// đăng nhập subaccount → CHỜ user nhập mã (mở hộp thư Playwright riêng cho user tự đọc mã) → SSO sang
    /// "Kênh Người bán" → <c>/portal/shop</c> → chạy lát cắt: đọc shop → mở "Chi tiết" shop đầu bằng trusted click
    /// (kỳ vọng KHÔNG captcha) → đọc số "Chờ Lấy Hàng". Kết quả đổ ra panel log.
    /// Gate như CanRun (đang xem 1 acc đã lưu). Phiên production của acc đang chạy → từ chối (đụng khoá hồ sơ chung).
    /// </summary>
    [RelayCommand]
    private async Task ChayThuBridge()
    {
        if (_editingId is not long accountId)
        {
            return;
        }

        var acc = _services.Accounts.GetById(accountId);
        var email = acc?.Email ?? EditEmail;

        // Lát cắt cũ còn chạy → không mở chồng (một phiên/lần test).
        if (_bridgeRunning)
        {
            _services.Log.Append(email, "Đang chạy thử (bridge) rồi — đợi xong hoặc bấm ■ Dừng.");
            return;
        }

        // Đang có phiên production (Playwright) trên hồ sơ này → không mở (Chromium chỉ cho 1 tiến trình/hồ sơ).
        if (_services.Sessions.IsRunning(accountId))
        {
            const string msg = "Đang có phiên chạy — bấm ■ Dừng trước khi Chạy thử (bridge).";
            _services.Log.Append(email, msg);
            BusyStatus = msg;
            return;
        }

        _bridgeRunning = true;
        OnPropertyChanged(nameof(CanStop)); // bật nút ■ Dừng trong lúc chạy thử bridge
        try
        {
            TryKillPoc(); // đóng cửa sổ/phiên cũ (nếu còn) trước khi mở mới — tránh khoá hồ sơ

            // Công thức hồ sơ Y HỆT AccountSession: baseDir = thư mục Database.Path; kind theo browserChoice ở Cài đặt.
            var baseDir = System.IO.Path.GetDirectoryName(_services.Database.Path) ?? ".";
            var browserChoice = _services.Settings.GetBrowserChoice();
            var browserKind = BrowserLocator.ResolveBrowserKind(browserChoice);
            var userDataDir = BrowserProfilePaths.ForAccount(baseDir, accountId, browserKind);

            // GĐ3: thư mục lưu phiếu (Cài đặt) + tỉnh địa chỉ lấy hàng (theo account, mặc định trong session).
            var invoiceDir = _services.Settings.GetInvoiceFolder();
            var province = acc?.PickupAddress;

            _bridgeCts = new System.Threading.CancellationTokenSource();
            var session = new OrdersBridgeSession(userDataDir, browserChoice,
                m => _services.Log.Append(email, m), invoiceDir, province);
            _bridgeSession = session;

            _services.Log.Append(email,
                "Chạy thử (đăng nhập + shop): đăng nhập subaccount bằng trình duyệt điều khiển → chờ bạn nhập mã → đóng → mở lại sạch + extension → đọc shop.");
            BusyStatus = "Đang chạy thử (đăng nhập + shop)...";

            var login = new OrdersLoginParams(
                acc?.Email ?? EditEmail,
                acc?.Password ?? string.Empty,
                acc?.VerifyEmail,
                acc?.VerifyEmailPassword);

            OrdersBridgeSliceResult result;
            try
            {
                result = await session.RunLoginThenSliceAsync(login, _bridgeCts.Token);
            }
            finally
            {
                _pocProcess = session.Process; // để TryKillPoc / ■ Dừng đóng được cửa sổ
            }

            if (result.Captcha)
            {
                var m = "Chạy thử (bridge): PHÁT HIỆN captcha/verify — kiến trúc CHƯA né được, cần soi lại.";
                _services.Log.Append(email, m);
                BusyStatus = m;
            }
            else if (result.Error is not null)
            {
                _services.Log.Append(email, "Chạy thử (bridge) chưa xong: " + result.Error);
                BusyStatus = "Chạy thử (bridge): " + result.Error;
            }
            else
            {
                var line =
                    $"Chạy thử (bridge) OK: {result.Shops.Count} shop; shop đầu id={result.FirstShopId}; " +
                    $"Chờ Lấy Hàng={result.ToShipCount?.ToString() ?? "?"}; đọc {result.OrdersCount} đơn" +
                    (result.SlipsSaved > 0 ? $"; lưu {result.SlipsSaved} phiếu" : string.Empty) +
                    " — KHÔNG captcha.";
                _services.Log.Append(email, line);
                BusyStatus = line;
            }
        }
        catch (System.OperationCanceledException)
        {
            _services.Log.Append(email, "Đã hủy chạy thử (bridge).");
            BusyStatus = "Đã hủy chạy thử (bridge).";
        }
        catch (System.Exception ex)
        {
            _services.Log.Append(email, "Lỗi chạy thử (bridge): " + ex.Message);
            BusyStatus = "Lỗi chạy thử (bridge): " + ex.Message;
        }
        finally
        {
            _bridgeRunning = false;
            OnPropertyChanged(nameof(CanStop)); // tắt lại nút ■ Dừng nếu không còn phiên nào
            try { _bridgeSession?.Dispose(); } catch { /* bỏ qua */ }
            _bridgeSession = null;
            try { _bridgeCts?.Dispose(); } catch { /* bỏ qua */ }
            _bridgeCts = null;
        }
    }

    /// <summary>Kill tiến trình trình duyệt sạch + huỷ lát cắt cầu nối đang mở (nếu có) — giải phóng khoá hồ sơ
    /// dùng chung với phiên production.</summary>
    private void TryKillPoc()
    {
        try { _bridgeCts?.Cancel(); } catch { /* bỏ qua */ }

        // Trong lúc lát cắt đang chạy, tiến trình nằm ở _bridgeSession.Process (_pocProcess chỉ set sau khi xong).
        var bridgeProc = _bridgeSession?.Process;
        try { if (bridgeProc is { HasExited: false }) bridgeProc.Kill(entireProcessTree: true); }
        catch { /* bỏ qua */ }

        try { if (_pocProcess is { HasExited: false }) _pocProcess.Kill(entireProcessTree: true); }
        catch { /* bỏ qua */ }
        _pocProcess = null;
    }

    // ===================== Trạng thái phiên đổ về form + cookie bắt được =====================

    /// <summary>
    /// Phiên "SẴN SÀNG THAO TÁC" theo CỜ TƯỜNG MINH <see cref="IAccountSession.ReadyForActions"/> của phiên.
    /// <b>Căn cứ:</b> cờ đó chỉ bật <c>true</c> tại đúng điểm sau khi luồng tự-đăng-nhập (<c>TryHumanLoginAsync</c>,
    /// đã await xong) hoàn tất VÀ đọc được số "Chờ Lấy Hàng" lần đầu của lần mở hiện tại — và được ĐẶT LẠI
    /// false ở đầu mỗi lần mở/relaunch + khi Stopped/Error (xem <c>AccountSession._readyForActions</c>). KHÔNG
    /// suy từ <c>ToShipCount != null</c> nữa vì số đơn không reset khi relaunch → dễ "sẵn sàng ảo" ngay trong
    /// lúc đang đăng nhập lại. Vẫn kèm <c>state == Running</c> làm lớp chốt (phòng cờ lỡ sót). Hàm thuần (test được).
    /// </summary>
    public static bool IsSessionReadyForActions(SessionState state, bool readyForActions)
        => state == SessionState.Running && readyForActions;

    /// <summary>
    /// Xử lý sự kiện đổi trạng thái của các phiên (có thể đến từ thread nền) — marshal về UI thread rồi
    /// đổ trạng thái phiên của tài khoản đang chọn vào ô hiển thị + cập nhật nút.
    /// </summary>
    private void OnSessionsChanged() => RunOnUi(() =>
    {
        // Đổ trạng thái phiên vào TỪNG dòng (chấm chạy / "Chờ lấy: N") + cập nhật ô hiển thị của form.
        SyncAllRows();
        UpdateSelectedSessionStatus();
    });

    /// <summary>
    /// Đồng bộ trạng thái phiên vào mọi dòng đang hiển thị. LUÔN chạy trên UI thread (gọi từ
    /// <see cref="RunOnUi"/>) — chỉ đọc <see cref="Accounts"/> và set thuộc tính row, KHÔNG cấu trúc lại
    /// ObservableCollection từ thread nền.
    /// </summary>
    private void SyncAllRows()
    {
        foreach (var row in Accounts)
        {
            row.SyncFromSession(_services.Sessions.Get(row.Id));
        }
    }

    /// <summary>
    /// Một phiên nền vừa lưu cookie vào DB cho <paramref name="accountId"/> — marshal về UI thread để dựng
    /// lại danh sách (ObservableCollection chỉ được đụng trên UI thread) và cập nhật form nếu đang mở đúng
    /// tài khoản đó.
    /// </summary>
    private void OnSessionCookieSaved(long accountId) => RunOnUi(() => RefreshAfterCookieSaved(accountId));

    /// <summary>Đổ trạng thái/số đơn của phiên theo tài khoản ĐANG CHỌN vào ô hiển thị; cập nhật nút mở/dừng.</summary>
    private void UpdateSelectedSessionStatus()
    {
        var id = _editingId ?? SelectedRow?.Id;
        var session = id is long sid ? _services.Sessions.Get(sid) : null;

        BusyStatus = session?.StatusText;
        OrderStatus = FormatOrderStatus(session?.ToShipCount);

        OnPropertyChanged(nameof(CanStopSeller));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRun));
    }

    /// <summary>Định dạng dòng theo dõi đơn "Chờ Lấy Hàng" từ số đọc được (null = ẩn).</summary>
    private static string? FormatOrderStatus(int? count)
    {
        if (count is not int n)
        {
            return null;
        }

        return n > 0
            ? $"Chờ Lấy Hàng: {n} đơn — vẫn theo dõi mỗi 30'."
            : "Chờ Lấy Hàng: 0 — kiểm lại sau 30'.";
    }

    /// <summary>
    /// Sau khi một phiên nền đã ghi cookie vào DB cho <paramref name="accountId"/>, CẬP NHẬT TẠI CHỖ — KHÔNG
    /// dựng lại cả danh sách. Danh sách không hiển thị cookie nên không cần rebuild; rebuild ở đây (sự kiện
    /// <c>CookieSaved</c> bắn liên tục khi nhiều phiên đăng nhập + theo dõi 30') sẽ xóa tick người dùng và
    /// đảo thứ tự "nổi lên đầu". Chỉ cần: (1) cập nhật cookie/UpdatedAt lên đúng instance <see cref="Account"/>
    /// đang có trong <c>_all</c> (row bọc CHÍNH instance này → Save sau không ghi đè cookie về null), (2) nếu
    /// đang MỞ đúng tài khoản đó thì cập nhật form. Chạy trên UI thread (gọi từ <see cref="RunOnUi"/>).
    /// </summary>
    private void RefreshAfterCookieSaved(long accountId)
    {
        var fresh = _services.Accounts.GetById(accountId);
        if (fresh is null)
        {
            return; // tài khoản đã bị xóa — không có gì để cập nhật
        }

        // Cập nhật cookie/UpdatedAt trên instance đang có trong _all (row bọc chính instance này) → GIỮ tick
        // + thứ tự (không đụng ObservableCollection).
        var cached = _all.FirstOrDefault(a => a.Id == accountId);
        if (cached is not null)
        {
            cached.Cookie = fresh.Cookie;
            cached.UpdatedAt = fresh.UpdatedAt;
        }

        // Đang mở đúng tài khoản đó → cập nhật form (EditCookie đổi → HasCookie/CookieSizeText tự cập nhật).
        if (_editingId == accountId)
        {
            EditCookie = fresh.Cookie ?? string.Empty;
            UpdatedAtText = FormatDate(fresh.UpdatedAt);
        }
    }

    /// <summary>Kết quả của thao tác lưu cookie đã bắt được vào tài khoản.</summary>
    public enum SaveCookieResult
    {
        /// <summary>JSON không chứa cookie nào (người dùng có thể chưa đăng nhập xong).</summary>
        NoCookie,

        /// <summary>Không còn tài khoản targetId trong DB (có thể đã bị xóa).</summary>
        AccountMissing,

        /// <summary>Đã ghi cookie vào tài khoản.</summary>
        Saved
    }

    /// <summary>
    /// Ghi chuỗi cookie JSON đã bắt được vào ĐÚNG tài khoản <paramref name="targetId"/>. KHÔNG đọc lại
    /// <c>_editingId</c> nên không bị ảnh hưởng khi người dùng đổi chọn/tạo mới trong lúc chờ browser
    /// (chống race ghi nhầm/crash). Tách khỏi Playwright để test được ở mức ViewModel.
    /// </summary>
    /// <remarks>
    /// Luôn dựng lại danh sách (<see cref="RefreshList"/>) để instance trong <see cref="Accounts"/> có
    /// cookie mới — tránh mất cookie khi người dùng chọn lại tài khoản (instance cũ có Cookie rỗng rồi
    /// bị Save ghi đè về null). Chỉ cập nhật FORM và kéo lựa chọn về targetId khi người dùng VẪN đang
    /// mở đúng tài khoản đó; nếu đã chuyển đi thì vẫn lưu DB cho targetId nhưng giữ nguyên form/lựa chọn.
    /// </remarks>
    public SaveCookieResult SaveCapturedCookie(long targetId, string cookieJson)
    {
        if (CookieJson.Deserialize(cookieJson).Count == 0)
        {
            return SaveCookieResult.NoCookie;
        }

        var acc = _services.Accounts.GetById(targetId);
        if (acc is null)
        {
            return SaveCookieResult.AccountMissing;
        }

        acc.Cookie = cookieJson;
        _services.Accounts.Update(acc);

        // Làm mới cache trước khi dựng lại danh sách.
        _all = _services.Accounts.GetAll();

        if (_editingId == targetId)
        {
            // Người dùng vẫn đang mở tài khoản này → cập nhật form + chọn lại instance mới có cookie.
            EditCookie = cookieJson;
            UpdatedAtText = FormatDate(acc.UpdatedAt);
            RefreshList(targetId);
        }
        else
        {
            // Đã chuyển sang tài khoản khác / đang tạo mới → dựng lại danh sách (để instance của
            // targetId có cookie) nhưng giữ nguyên lựa chọn & form hiện tại.
            RefreshList(_editingId ?? SelectedRow?.Id);
        }

        return SaveCookieResult.Saved;
    }
}
