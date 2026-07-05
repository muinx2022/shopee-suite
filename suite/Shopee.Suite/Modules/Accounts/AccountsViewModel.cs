using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.Browser;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Core.Proxy;
using Shopee.Modules.CheckAccount;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Accounts;

/// <summary>
/// Mục "Tài khoản & Proxy" dùng chung. Quản lý kho <see cref="AccountStore"/>: thêm/xóa/sửa
/// tài khoản Shopee + cấu hình proxy + profile. Scrape và Search đều đọc cùng kho này.
/// </summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    public ObservableCollection<AccountItemViewModel> Items { get; } = [];

    public string[] RegionOptions { get; } = ["random", "bac", "trung", "nam"];
    public string[] ProxyTypeOptions { get; } = ["http", "socks5"];

    // Bộ lọc hiển thị: MẶC ĐỊNH chỉ hiện tk còn dùng được; chọn "Bị lỗi/captcha" mới hiện tk đã bị
    // đánh dấu (Disabled) khi dính captcha lúc Scrape/Search — để xử lý sau.
    public string[] FilterOptions { get; } = ["Còn dùng được", "Bị lỗi / captcha", "Tất cả"];

    public const string ErrorFilter = "Bị lỗi / captcha";

    [ObservableProperty] private string _selectedFilter = "Còn dùng được";
    partial void OnSelectedFilterChanged(string value)
    {
        Reload();
        OnPropertyChanged(nameof(IsErrorFilter));
        OpenForCheckCommand.NotifyCanExecuteChanged();   // double-click mở giải captcha CHỈ ở bộ lọc "Bị lỗi/captcha"
    }

    /// <summary>Chỉ true khi đang xem bộ lọc "Bị lỗi / captcha" — nút "Kiểm tra tk lỗi" chỉ hiện lúc đó.</summary>
    public bool IsErrorFilter => SelectedFilter == ErrorFilter;

    public int ErroredCount => AccountStore.Shared.Accounts.Count(a => a.Disabled);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(ReenableCommand))]
    private AccountItemViewModel? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty] private string _status = "";

    /// <summary>Nội dung kho KiotProxy dùng chung (mỗi dòng 1 key/host:port) — bind vào textbox.</summary>
    [ObservableProperty] private string _proxyPoolText = "";

    public AccountsViewModel()
    {
        Reload();
        LoadProxyPool();
        // Kho proxy đổi (Lưu tay hoặc client tự nhận từ Hub) → cập nhật textbox.
        KiotProxyPoolStore.Shared.Changed += () => UiThread.Post(LoadProxyPool);
        // Tự làm mới khi kho chung thay đổi (vd: Check Shopee Account lưu tk mới vào).
        AccountStore.Shared.Changed += () => UiThread.Post(Reload);
        // Cột "Tình trạng" cập nhật khi Scrape/Search bắt đầu/kết thúc/mượn/nhả tk — chỉ refresh cột, không reload cả list.
        ShopeeAccountUsage.Shared.Changed += () => UiThread.Post(RefreshUsageColumn);
        StartReportsPolling();   // chỉ Hub: định kỳ nạp "acc client báo lỗi"
    }

    private void RefreshUsageColumn()
    {
        foreach (var it in Items) it.RefreshUsage();
    }

    private void LoadProxyPool() => ProxyPoolText = string.Join(Environment.NewLine, KiotProxyPoolStore.Shared.Keys);

    /// <summary>Lưu kho KiotProxy dùng chung (mỗi dòng 1 entry). Máy Hub tự đẩy lên → client tự nhận.</summary>
    [RelayCommand]
    private void SaveProxyPool()
    {
        var entries = (ProxyPoolText ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (KiotProxyPoolStore.Shared.ReplaceAll(entries))
            Status = $"Đã lưu kho proxy: {KiotProxyPoolStore.Shared.Count} proxy (xoay vòng cho acc lúc chạy).";
        else
            Status = "Lưu kho proxy thất bại.";
    }

    private bool SaveStore(string success, string failure)
    {
        if (AccountStore.Shared.Save())
        {
            Status = success;
            return true;
        }

        Status = failure;
        return false;
    }

    private void Reload()
    {
        var keepId = Selected?.Model.Id;
        Items.Clear();
        IEnumerable<ShopeeAccount> src = SelectedFilter switch
        {
            "Bị lỗi / captcha" => AccountStore.Shared.Accounts.Where(a => a.Disabled),
            "Tất cả" => AccountStore.Shared.Accounts,
            _ => AccountStore.Shared.Accounts.Where(a => !a.Disabled),   // "Còn dùng được" (mặc định)
        };
        foreach (var a in src)
            Items.Add(new AccountItemViewModel(a));
        Selected = Items.FirstOrDefault(x => x.Model.Id == keepId) ?? Items.FirstOrDefault();
        var total = AccountStore.Shared.Accounts.Count;
        var err = ErroredCount;
        Status = err > 0
            ? $"{Items.Count} hiển thị · {total} tổng · ⚠ {err} dính captcha/lỗi (lọc \"Bị lỗi\" để xem)."
            : $"{Items.Count} hiển thị · {total} tổng.";
        OnPropertyChanged(nameof(ErroredCount));
    }

    // Account đã xử lý xong (vd vừa re-verify) → bật lại để đưa về rotation Scrape/Search.
    [RelayCommand(CanExecute = nameof(CanReenable))]
    private void Reenable()
    {
        if (Selected is null) return;
        // Lấy tên TRƯỚC khi Save(): Save → Changed → Reload đồng bộ; nếu đang lọc "Bị lỗi" thì tk vừa bật
        // hết Disabled → biến mất khỏi danh sách → Selected thành null → dùng Selected sau đó sẽ NRE.
        var name = Selected.DisplayName;
        var id = Selected.Model.Id;                       // giữ TRƯỚC Save (Save→Reload có thể null Selected)
        var prevCaptcha = Selected.Model.CaptchaUrl;
        Selected.Model.Disabled = false;
        Selected.Model.LastError = null;
        Selected.Model.CaptchaUrl = null;                 // đã ổn → xoá luôn link captcha cũ
        if (!SaveStore(
                $"Đã bật lại \"{name}\" → quay về rotation.",
                $"Không lưu được thay đổi của \"{name}\"."))
        {
            Selected.Model.Disabled = true;
            Selected.Model.LastError ??= "Khôi phục trạng thái lỗi do lưu thất bại.";
            Selected.Model.CaptchaUrl = prevCaptcha;
            return;
        }
        // Client: bật lại = acc đã ổn → GỠ báo trên Hub (như "Đóng và lưu"), tránh Hub xoá nhầm acc đang tốt.
        if (CoordinationRuntime.Active && !HubServerConfigStore.Shared.Current.Enabled)
            _ = CoordinationRuntime.Hub?.ClearErroredAccountAsync(id);
    }

    private bool CanReenable() => Selected?.Model.Disabled == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckIdle))]
    [NotifyCanExecuteChangedFor(nameof(CheckErroredCommand))]
    private bool _isChecking;

    // Delay ngẫu nhiên giữa các lần check (giả lập người dùng, tránh login dồn dập → bớt bị chặn).
    private readonly Random _rng = new();

    public bool IsCheckIdle => !IsChecking;

    private bool CanCheckErrored() => IsCheckIdle;

    /// <summary>
    /// FLOW TỰ ĐỘNG DỌN TK LỖI: duyệt LẦN LƯỢT mọi tài khoản đang bị lỗi/captcha (Disabled), chạy
    /// auto-login Shopee (mở Brave + set cookie SPC_F + điền form human). Vào được trang chủ (có cookie
    /// phiên) → bỏ cờ lỗi, đưa về kho. KHÔNG vào được (sai mật khẩu / captcha / lỗi kỹ thuật — bất kỳ
    /// lý do gì) → XÓA HẲN tài khoản. Dùng chung engine với mục "Check Shopee Account".
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckErrored))]
    private async Task CheckErrored()
    {
        var targets = AccountStore.Shared.Accounts.Where(a => a.Disabled).ToList();
        if (targets.Count == 0)
        {
            await Dialogs.InfoAsync("Không có tài khoản lỗi/captcha nào để kiểm tra.", "Kiểm tra tk lỗi");
            return;
        }

        if (!await Dialogs.ConfirmAsync(
                $"Kiểm tra lần lượt {targets.Count} tài khoản lỗi/captcha:\n\n" +
                "• Tk CÓ link captcha đã lưu → TỰ ĐĂNG NHẬP trước (nếu chưa có phiên) rồi MỞ ĐÚNG trang đó để bạn GIẢI TAY; giải xong → về kho, chưa giải → GIỮ lại.\n" +
                "• Tk KHÔNG có link → tự đăng nhập: vào được → về kho; KHÔNG vào được → XÓA HẲN.\n\n" +
                "Thao tác xóa KHÔNG hoàn tác được. Tiếp tục?",
                "Kiểm tra & dọn tk lỗi", DialogIcon.Warning))
            return;

        IsChecking = true;
        var okNames = new List<string>();
        var failNames = new List<string>();
        var keptNames = new List<string>();   // tk captcha mở URL nhưng chưa giải → GIỮ lại (không xoá)
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var a = targets[i];
                var name = a.DisplayName;
                Status = $"[{i + 1}/{targets.Count}] Đang đăng nhập \"{name}\"… (✓ {okNames.Count} · ✗ {failNames.Count})";

                var hasCaptchaUrl = !string.IsNullOrWhiteSpace(a.CaptchaUrl);
                var success = false;
                try
                {
                    var proxy = await ResolveProxyAsync(a);
                    var profileDir = Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", a.Id);
                    Directory.CreateDirectory(profileDir);
                    var checker = new ShopeeAccountChecker();
                    if (hasCaptchaUrl)
                    {
                        // CÓ url captcha đã lưu → TỰ ĐĂNG NHẬP trước (nếu chưa có phiên) rồi MỞ ĐÚNG trang đó
                        // cho user GIẢI TAY. Giữ trình duyệt ~1 phút để giải; giải xong (đăng nhập được) thì
                        // đóng sớm rồi sang tk kế.
                        Status = $"[{i + 1}/{targets.Count}] Tự đăng nhập rồi mở trang captcha \"{name}\" — giải tay (~1 phút)…  (✓ {okNames.Count} · ✗ {failNames.Count})";
                        var result = await checker.LoginThenManualSolveAsync(a.ShopeeAccountLogin, a.CaptchaUrl!, proxy, profileDir, 60_000, CancellationToken.None);
                        success = result.Outcome == CheckOutcome.Success;
                    }
                    else if (!string.IsNullOrWhiteSpace(a.ShopeeAccountLogin))
                    {
                        // FALLBACK (chưa lưu url captcha): luồng login TỰ ĐỘNG như cũ. Giữ trình duyệt 10–15s.
                        var holdMs = _rng.Next(10_000, 15_001);
                        var result = await checker.CheckAsync(a.ShopeeAccountLogin, proxy, profileDir, holdMs, CancellationToken.None);
                        success = result.Outcome == CheckOutcome.Success;   // chỉ vào được home mới tính OK
                    }
                }
                catch { success = false; }

                string verdict;
                if (success)
                {
                    var previousCaptchaUrl = a.CaptchaUrl;
                    a.Disabled = false;
                    a.LastError = null;                 // → đưa về kho
                    a.CaptchaUrl = null;                // đã giải xong → xoá url captcha đã lưu
                    if (AccountStore.Shared.Save())     // Changed → Reload → list refresh NGAY (acc rời danh sách lỗi)
                    {
                        okNames.Add(name);
                        verdict = "✓ OK → về kho";
                    }
                    else
                    {
                        a.Disabled = true;
                        a.LastError = "Vẫn đang đánh dấu lỗi/captcha do lưu thất bại.";
                        a.CaptchaUrl = previousCaptchaUrl;
                        verdict = "⚠ lưu thất bại → giữ nguyên trạng thái lỗi";
                    }
                }
                else if (hasCaptchaUrl)
                {
                    // Tk có url captcha nhưng CHƯA giải → GIỮ lại (KHÔNG xoá) để thử lại sau.
                    keptNames.Add(name);
                    verdict = "⏳ chưa giải captcha → giữ lại";
                }
                else
                {
                    AccountStore.Shared.Remove(a.Id);   // Remove tự Save → Changed → Reload → list refresh NGAY (xóa khỏi list)
                    failNames.Add(name);
                    verdict = "✗ không vào được → ĐÃ XÓA";
                }

                // Hiện RÕ kết quả của acc vừa xong (✓/✗) — trình duyệt đã đóng, nghỉ vài giây rồi check tiếp.
                if (i < targets.Count - 1)
                {
                    var gapMs = _rng.Next(3_000, 6_000);
                    Status = $"[{i + 1}/{targets.Count}] \"{name}\": {verdict} · nghỉ {gapMs / 1000}s rồi tiếp…  (✓ {okNames.Count} · ✗ {failNames.Count})";
                    await Task.Delay(gapMs);
                }
                else
                {
                    Status = $"[{i + 1}/{targets.Count}] \"{name}\": {verdict}  (✓ {okNames.Count} · ✗ {failNames.Count})";
                }
            }

            // Đã áp dụng từng acc trong vòng lặp (Save/Remove → list refresh ngay).
            Status = $"Xong: {okNames.Count} OK → kho · {keptNames.Count} giữ (chưa giải captcha) · {failNames.Count} đã xóa · còn {AccountStore.Shared.Accounts.Count} tk.";

            // Bảng tổng kết: liệt kê RÕ acc nào về kho, acc nào đã xóa (cắt bớt nếu quá dài).
            static string Section(string title, List<string> names)
            {
                if (names.Count == 0) return "";
                var show = names.Take(40).ToList();
                var more = names.Count - show.Count;
                var body = string.Join("\n  • ", show) + (more > 0 ? $"\n  • … và {more} tk nữa" : "");
                return $"\n\n{title} ({names.Count}):\n  • {body}";
            }
            await Dialogs.InfoAsync(
                $"Hoàn tất kiểm tra {targets.Count} tài khoản." +
                Section("✓ OK → về kho", okNames) +
                Section("⏳ Giữ lại (chưa giải captcha)", keptNames) +
                Section("✗ Đã xóa (không vào được)", failNames),
                "Kết quả dọn tk lỗi");
        }
        catch (Exception ex)
        {
            Dialogs.Notify("Lỗi khi dọn tk: " + ex.Message, "Kiểm tra tk lỗi", DialogIcon.Warning);
            Status = "Đã dừng do lỗi — chưa áp dụng xóa.";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static async Task<string?> ResolveProxyAsync(ShopeeAccount a)
    {
        if (!string.IsNullOrWhiteSpace(a.ManualProxy))
            return a.ManualProxy.Contains("://") ? a.ManualProxy.Trim() : "http://" + a.ManualProxy.Trim();
        if (!string.IsNullOrWhiteSpace(a.KiotProxyKey))
        {
            // ƯU TIÊN /current để IP lúc kiểm tra/đăng nhập TRÙNG IP lúc scrape (cùng key, sống ~30').
            // CHỈ /new khi /current chưa có proxy (key chưa kích hoạt / hết hạn) — gán IP mới một lần,
            // các lần sau /current dùng lại. KHÔNG /new mỗi lần (sẽ xoay IP → scrape sau IP khác → captcha).
            var r = await KiotProxyClient.FetchCurrentAsync(a.KiotProxyKey, default, a.ProxyType);
            if (r.Proxy is null)
                r = await KiotProxyClient.FetchNewAsync(a.KiotProxyKey, default, a.ProxyType);
            return r.Proxy;
        }
        return null;
    }

    // ── Vai trò máy: client chỉ-xem + xử-lý-captcha; Hub/standalone full quản lý ──
    /// <summary>Client (đã nối Hub, KHÔNG phải máy Hub) → khóa quản lý danh sách (thêm/import/xóa/sửa/proxy).</summary>
    public bool IsReadOnlyMode => CoordinationRuntime.Active && !HubServerConfigStore.Shared.Current.Enabled;
    /// <summary>Hub hoặc standalone → full quản lý (thêm/xóa/import/sửa/xóa acc lỗi).</summary>
    public bool IsFullEditMode => !IsReadOnlyMode;
    /// <summary>Máy này là Hub → hiện panel "Acc client báo lỗi".</summary>
    public bool IsHubMode => HubServerConfigStore.Shared.Current.Enabled;

    // ── Xử lý captcha THỦ CÔNG (double-click mở Brave bằng ĐÚNG profile acc, giải rồi đóng) ──
    private BrowserLauncher? _checkLauncher;
    private string? _checkingId;
    private string _checkingName = "";
    /// <summary>Đang mở 1 Brave để giải captcha → bật các nút "Đóng và …".</summary>
    public bool IsCheckOpen => _checkLauncher is not null;

    /// <summary>Double-click 1 acc (CHỈ ở bộ lọc "Bị lỗi/captcha") → mở Brave bằng profile acc tại trang captcha
    /// (hoặc trang chủ) để GIẢI TAY, để MỞ. Giới hạn ở filter lỗi để luôn có nút "Đóng và…" mà đóng lại (khỏi rò Brave).</summary>
    [RelayCommand(CanExecute = nameof(IsErrorFilter))]
    private async Task OpenForCheck(AccountItemViewModel? row)
    {
        var acc = row?.Model ?? Selected?.Model;
        if (acc is null || !IsErrorFilter) return;
        CloseCheckBrowser();
        BrowserLauncher? launcher = null;
        try
        {
            var proxy = await ResolveProxyAsync(acc);
            var profileDir = Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", acc.Id);
            Directory.CreateDirectory(profileDir);
            var url = !string.IsNullOrWhiteSpace(acc.CaptchaUrl) ? acc.CaptchaUrl! : "https://shopee.vn";
            var hasLogin = !string.IsNullOrWhiteSpace(acc.ShopeeAccountLogin);

            // MỞ cửa sổ + GÁN _checkLauncher ĐỒNG BỘ (KHÔNG await xen giữa) → cửa sổ vừa mở là ĐÃ kill được
            // ngay: KillCheckBrowser/CloseCheckBrowser (rời màn / đóng app / bấm "Đóng và…") LUÔN với tới,
            // KHÔNG rò cửa sổ có phiên login kể cả khi auto-login còn đang chạy (có thể lâu).
            launcher = new BrowserLauncher(BrowserKind.Brave);
            _checkLauncher = launcher; _checkingId = acc.Id; _checkingName = acc.DisplayName;
            launcher.Launch(profileDir, proxy, hasLogin ? ShopeeAccountChecker.LoginUrl : url);
            OnCheckStateChanged();
            Status = $"Đang mở & tự đăng nhập \"{acc.DisplayName}\" trước khi giải captcha…";

            // TỰ ĐĂNG NHẬP trước (nếu chưa có phiên) rồi mở URL lỗi trên CHÍNH cửa sổ đó — GIỐNG "Check Shopee Account".
            await new ShopeeAccountChecker().LoginThenNavigateAsync(launcher, acc.ShopeeAccountLogin, url, CancellationToken.None);

            // Giữa lúc auto-login, user có thể đã bấm "Đóng và…" / rời màn (CloseCheckBrowser đã kill+null)
            // → đừng ghi đè status/thao tác đó.
            if (!ReferenceEquals(_checkLauncher, launcher)) return;
            Status = $"Đã mở \"{acc.DisplayName}\" (đã tự đăng nhập) — giải captcha xong bấm \"Đóng và lưu\""
                     + (IsReadOnlyMode ? " (hoặc \"Đóng và báo không sửa được\")." : " (hoặc \"Đóng và xóa tk\").");
        }
        catch (Exception ex)
        {
            if (launcher is not null && ReferenceEquals(_checkLauncher, launcher)) CloseCheckBrowser();
            Status = "Không mở được trình duyệt: " + ex.Message;
        }
    }

    private void CloseCheckBrowser()
    {
        try { _checkLauncher?.Kill(); } catch { }
        _checkLauncher = null; _checkingId = null; _checkingName = "";
        OnCheckStateChanged();
    }

    /// <summary>Đóng Brave check đang mở — gọi khi rời màn Tài khoản / đóng app (khỏi rò cửa sổ có phiên login).</summary>
    public void KillCheckBrowser() => CloseCheckBrowser();

    private void OnCheckStateChanged()
    {
        OnPropertyChanged(nameof(IsCheckOpen));
        CloseAndSaveCommand.NotifyCanExecuteChanged();
        CloseAndReportFailedCommand.NotifyCanExecuteChanged();
        CloseAndDeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Đã sửa OK: đóng Brave + bật lại acc (bỏ Disabled/lỗi/captcha) + lưu + gỡ báo trên Hub.</summary>
    [RelayCommand(CanExecute = nameof(IsCheckOpen))]
    private void CloseAndSave()
    {
        var id = _checkingId; var name = _checkingName;
        CloseCheckBrowser();
        if (id is null) return;
        var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is not null) { acc.Disabled = false; acc.LastError = null; acc.CaptchaUrl = null; AccountStore.Shared.Save(); }
        _ = CoordinationRuntime.Hub?.ClearErroredAccountAsync(id);   // đã sửa → gỡ báo
        Status = $"✓ Đã sửa & lưu \"{name}\" → quay về kho.";
    }

    /// <summary>CLIENT không sửa được: đóng Brave, GIỮ acc tắt, báo Hub "failed" để Hub quyết giữ/xóa.</summary>
    [RelayCommand(CanExecute = nameof(IsCheckOpen))]
    private void CloseAndReportFailed()
    {
        var id = _checkingId; var name = _checkingName;
        var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
        var reason = acc?.LastError ?? "Client không sửa được captcha";
        var captchaUrl = acc?.CaptchaUrl;
        CloseCheckBrowser();
        if (id is null) return;
        _ = CoordinationRuntime.Hub?.ReportErroredAccountAsync(id, reason, captchaUrl, "failed");
        Status = $"Đã báo Hub: không sửa được \"{name}\" (Hub sẽ quyết giữ/xóa).";
    }

    /// <summary>HUB/standalone: đóng Brave + XÓA acc khỏi kho (mirror gỡ mọi client) + gỡ báo.</summary>
    [RelayCommand(CanExecute = nameof(IsCheckOpen))]
    private void CloseAndDelete()
    {
        var id = _checkingId; var name = _checkingName;
        CloseCheckBrowser();
        if (id is null) return;
        AccountStore.Shared.Remove(id);
        _ = CoordinationRuntime.Hub?.ClearErroredAccountAsync(id);
        Status = $"Đã xóa \"{name}\" khỏi kho.";
    }

    // ── Panel "Acc client báo lỗi" (CHỈ Hub): client báo captcha/failed về đây, operator quyết ──
    public ObservableCollection<ClientErrorRow> ClientErrorReports { get; } = [];
    private UiThread.UiTimer? _reportTimer;

    private void StartReportsPolling()
    {
        if (!IsHubMode) return;
        _ = RefreshReports();
        _reportTimer = UiThread.Interval(TimeSpan.FromSeconds(15), () => _ = RefreshReports());
        _reportTimer.Start();
    }

    [RelayCommand]
    private async Task RefreshReports()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;
        List<AccountError> errs;
        try { errs = await hub.ErroredAccountsAsync(); } catch { return; }
        ClientErrorReports.Clear();
        foreach (var e in errs.OrderByDescending(x => x.ReportedAt))
        {
            var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == e.AccountId);
            if (acc is null) { _ = hub.ClearErroredAccountAsync(e.AccountId); continue; }   // acc đã bị xóa → gỡ báo mồ côi
            ClientErrorReports.Add(new ClientErrorRow
            {
                AccountId = e.AccountId,
                AccountName = acc.DisplayName,
                Machine = string.IsNullOrWhiteSpace(e.Hostname) ? e.MachineId : e.Hostname,
                StatusText = e.Status == "failed" ? "✗ Không sửa được" : "⚠ Đang captcha",
                Reason = e.Reason,
                When = e.ReportedAt == default ? "" : e.ReportedAt.ToLocalTime().ToString("dd/MM HH:mm"),
            });
        }
    }

    /// <summary>Hub xóa acc client báo lỗi khỏi kho (mirror gỡ mọi máy) + gỡ báo.</summary>
    [RelayCommand]
    private void DeleteReportedAccount(ClientErrorRow? row)
    {
        if (row is null) return;
        AccountStore.Shared.Remove(row.AccountId);
        _ = CoordinationRuntime.Hub?.ClearErroredAccountAsync(row.AccountId);
        ClientErrorReports.Remove(row);
        Status = $"Đã xóa acc \"{row.AccountName}\" (client báo lỗi) khỏi kho.";
    }

    /// <summary>Hub bỏ qua báo (giữ acc; vd để máy khác thử).</summary>
    [RelayCommand]
    private void DismissReport(ClientErrorRow? row)
    {
        if (row is null) return;
        _ = CoordinationRuntime.Hub?.ClearErroredAccountAsync(row.AccountId);
        ClientErrorReports.Remove(row);
    }

    // ── Đồng bộ acc + proxy từ Hub (chỉ hiện khi máy này là client của một Hub) ──
    /// <summary>true khi máy đã kết nối Hub → hiện nút "Đồng bộ acc".</summary>
    public bool IsHubClient => CoordinationRuntime.Active;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncIdle))]
    [NotifyCanExecuteChangedFor(nameof(SyncFromHubCommand))]
    private bool _isSyncing;

    public bool IsSyncIdle => !IsSyncing;
    private bool CanSyncFromHub() => IsSyncIdle && CoordinationRuntime.Active;

    [RelayCommand(CanExecute = nameof(CanSyncFromHub))]
    private async Task SyncFromHub()
    {
        var sync = CoordinationRuntime.ConfigSync;
        if (sync is null) { Status = "Chưa kết nối Hub."; return; }
        IsSyncing = true;
        Status = "Đang kéo tài khoản + proxy từ Hub…";
        try
        {
            var r = await sync.PullAccountsAsync();
            Reload();
            Status = $"✓ Đồng bộ: Shopee +{r.ShopeeAdded}/↻{r.ShopeeUpdated}/bỏ {r.ShopeeSkipped} · BigSeller +{r.BigSellerAdded}/↻{r.BigSellerUpdated}/bỏ {r.BigSellerSkipped} · cookie {r.CookiesCopied}.";
        }
        catch (Exception ex)
        {
            Status = "✘ Lỗi đồng bộ: " + ex.Message;
            Dialogs.Notify(ex.Message, "Đồng bộ acc từ Hub", DialogIcon.Warning);
        }
        finally { IsSyncing = false; }
    }

    [RelayCommand]
    private void Add()
    {
        var model = new ShopeeAccount { Label = "Tài khoản mới" };
        if (AccountStore.Shared.Add(model))
        {
            Selected = Items.FirstOrDefault(x => x.Model.Id == model.Id) ?? Selected;
            Status = $"{Items.Count} tài khoản.";
        }
        else
        {
            Status = "Không thêm được tài khoản mới.";
        }
    }

    /// <summary>
    /// Xóa các tài khoản đang chọn. Nhận danh sách chọn (DataGrid.SelectedItems) qua CommandParameter
    /// để xóa NHIỀU acc cùng lúc; nếu không có thì xóa acc đang Selected. Xóa hàng loạt bằng 1 lần
    /// ReplaceAll (ghi file + Reload 1 lần) thay vì Remove từng cái (mỗi cái 1 lần ghi → giật).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync(object? selectedItems)
    {
        var targets = (selectedItems as System.Collections.IList)?
                          .Cast<AccountItemViewModel>().ToList()
                      ?? [];
        if (targets.Count == 0 && Selected is not null)
            targets.Add(Selected);
        if (targets.Count == 0) return;

        var msg = targets.Count == 1
            ? $"Xóa tài khoản \"{targets[0].DisplayName}\"?"
            : $"Xóa {targets.Count} tài khoản đã chọn?";
        if (!await Dialogs.ConfirmAsync(msg, "Xóa tài khoản"))
            return;

        var ids = targets.Select(t => t.Model.Id).ToHashSet(StringComparer.Ordinal);
        // ReplaceAll → Save → Changed → Reload tự dựng lại Items (không cần Items.Remove thủ công).
        if (AccountStore.Shared.ReplaceAll(AccountStore.Shared.Accounts.Where(a => !ids.Contains(a.Id))))
        {
            Status = $"Đã xóa {ids.Count} tài khoản · còn {AccountStore.Shared.Accounts.Count}.";
            // Gỡ luôn báo lỗi (nếu có) của các acc vừa xóa → panel "Acc client báo lỗi" không còn dòng mồ côi.
            foreach (var id in ids) _ = CoordinationRuntime.Hub?.ClearErroredAccountAsync(id);
        }
        else
            Status = $"Không xóa được {ids.Count} tài khoản đã chọn.";
    }

    [RelayCommand]
    private void Save()
    {
        // Kho lưu chung 1 file accounts.json (toàn bộ tk) → mỗi lần lưu ghi lại cả file (tk vừa sửa +
        // các tk khác giữ nguyên). Báo "đã lưu thay đổi" cho rõ thay vì "đã lưu N tk" (gây hiểu nhầm).
        var name = Selected?.DisplayName;
        SaveStore(
            string.IsNullOrWhiteSpace(name) ? "Đã lưu thay đổi." : $"Đã lưu thay đổi \"{name}\".",
            string.IsNullOrWhiteSpace(name) ? "Không lưu được thay đổi." : $"Không lưu được thay đổi của \"{name}\".");
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dlg = new ImportAccountsWindow();
        if (await WindowHost.ShowDialogAsync(dlg) != true) return;

        var logins = SplitLines(dlg.Logins);
        var proxies = SplitLines(dlg.ProxyKeys);

        if (logins.Count == 0 && proxies.Count == 0)
        {
            await Dialogs.InfoAsync("Chưa nhập gì.", "Import");
            return;
        }

        // CHỈ nhập proxy → dàn đều (xoay vòng) cho các tài khoản hiện có: acc1-proxy1, acc2-proxy2,
        // … accX-proxy(X mod N).
        if (logins.Count == 0)
        {
            var accs = AccountStore.Shared.Accounts.ToList();
            if (accs.Count == 0)
            {
                await Dialogs.InfoAsync("Chưa có tài khoản nào để gán proxy. Hãy nhập tài khoản trước.",
                    "Import proxy");
                return;
            }
            for (var i = 0; i < accs.Count; i++)
                AssignProxy(accs[i], proxies[i % proxies.Count]);
            SaveStore(
                $"Đã gán {proxies.Count} proxy (xoay vòng) cho {accs.Count} tài khoản.",
                $"Không lưu được gán proxy cho {accs.Count} tài khoản.");
            return;
        }

        // Có tài khoản → tạo mới; nếu có proxy thì gán xoay vòng theo từng tài khoản.
        var added = 0;
        for (var i = 0; i < logins.Count; i++)
        {
            var model = new ShopeeAccount { ShopeeAccountLogin = logins[i] };
            if (proxies.Count > 0) AssignProxy(model, proxies[i % proxies.Count]);
            if (!AccountStore.Shared.Add(model))
            {
                Status = $"Import dừng lại: không lưu được tài khoản thứ {i + 1}.";
                return;
            }
            added++;
        }
        Selected = Items.LastOrDefault();
        Status = $"Đã import {added} tài khoản ({proxies.Count} proxy).";
    }

    /// <summary>Gán proxy cho tài khoản: nếu là proxy trực tiếp (host:port / scheme://) thì đặt
    /// ManualProxy, ngược lại coi là kiotproxy key.</summary>
    private static void AssignProxy(ShopeeAccount a, string proxy)
    {
        if (Shopee.Core.Proxy.ProxyPool.IsDirectProxy(proxy)) { a.ManualProxy = proxy; a.KiotProxyKey = ""; }
        else { a.KiotProxyKey = proxy; a.ManualProxy = ""; }
    }

    private static List<string> SplitLines(string text) => (text ?? "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>1 dòng trong panel "Acc client báo lỗi" trên Hub (acc client báo captcha/không-sửa-được).</summary>
public sealed class ClientErrorRow
{
    public string AccountId { get; init; } = "";
    public string AccountName { get; init; } = "";
    public string Machine { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Reason { get; init; } = "";
    public string When { get; init; } = "";
}
