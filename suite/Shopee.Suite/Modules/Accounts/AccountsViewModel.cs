using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.Browser;
using Shopee.Core.Infrastructure;
using Shopee.Core.Proxy;
using Shopee.Modules.CheckAccount;

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

    public AccountsViewModel()
    {
        Reload();
        // Tự làm mới khi kho chung thay đổi (vd: Check Shopee Account lưu tk mới vào).
        AccountStore.Shared.Changed += () =>
        {
            var d = Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) Reload();
            else d.BeginInvoke(Reload);
        };
        // Cột "Tình trạng" cập nhật khi Scrape/Search bắt đầu/kết thúc/mượn/nhả tk — chỉ refresh cột, không reload cả list.
        ShopeeAccountUsage.Shared.Changed += () =>
        {
            var d = Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) RefreshUsageColumn();
            else d.BeginInvoke(RefreshUsageColumn);
        };
    }

    private void RefreshUsageColumn()
    {
        foreach (var it in Items) it.RefreshUsage();
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
        Selected.Model.Disabled = false;
        Selected.Model.LastError = null;
        AccountStore.Shared.Save();   // Changed đã tự Reload — KHÔNG gọi Reload() lần nữa.
        Status = $"Đã bật lại \"{name}\" → quay về rotation.";
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
            Dialogs.Show("Không có tài khoản lỗi/captcha nào để kiểm tra.",
                "Kiểm tra tk lỗi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Dialogs.Show(
                $"Sẽ tự đăng nhập lần lượt {targets.Count} tài khoản lỗi/captcha:\n\n" +
                "• Vào được trang chủ → bỏ cờ lỗi, đưa về kho.\n" +
                "• KHÔNG vào được (sai mật khẩu / captcha / lỗi) → XÓA HẲN tài khoản.\n\n" +
                "Thao tác xóa KHÔNG hoàn tác được. Tiếp tục?",
                "Kiểm tra & dọn tk lỗi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        IsChecking = true;
        var okNames = new List<string>();
        var failNames = new List<string>();
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var a = targets[i];
                var name = a.DisplayName;
                Status = $"[{i + 1}/{targets.Count}] Đang đăng nhập \"{name}\"… (✓ {okNames.Count} · ✗ {failNames.Count})";

                var success = false;
                if (!string.IsNullOrWhiteSpace(a.ShopeeAccountLogin))
                {
                    try
                    {
                        var proxy = await ResolveProxyAsync(a);
                        var profileDir = Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", a.Id);
                        Directory.CreateDirectory(profileDir);
                        // Giữ trình duyệt mở 10–15s sau khi có kết quả (giả lập người dùng) rồi mới đóng.
                        var holdMs = _rng.Next(10_000, 15_001);
                        var checker = new ShopeeAccountChecker();
                        var result = await checker.CheckAsync(a.ShopeeAccountLogin, proxy, profileDir, holdMs, CancellationToken.None);
                        success = result.Outcome == CheckOutcome.Success;   // chỉ vào được home mới tính OK
                    }
                    catch { success = false; }
                }

                string verdict;
                if (success)
                {
                    a.Disabled = false;
                    a.LastError = null;                 // → đưa về kho
                    AccountStore.Shared.Save();         // Changed → Reload → list refresh NGAY (acc rời danh sách lỗi)
                    okNames.Add(name);
                    verdict = "✓ OK → về kho";
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
            Status = $"Xong: {okNames.Count} OK → kho · {failNames.Count} đã xóa · còn {AccountStore.Shared.Accounts.Count} tk.";

            // Bảng tổng kết: liệt kê RÕ acc nào về kho, acc nào đã xóa (cắt bớt nếu quá dài).
            static string Section(string title, List<string> names)
            {
                if (names.Count == 0) return "";
                var show = names.Take(40).ToList();
                var more = names.Count - show.Count;
                var body = string.Join("\n  • ", show) + (more > 0 ? $"\n  • … và {more} tk nữa" : "");
                return $"\n\n{title} ({names.Count}):\n  • {body}";
            }
            Dialogs.Show(
                $"Hoàn tất kiểm tra {targets.Count} tài khoản." +
                Section("✓ OK → về kho", okNames) +
                Section("✗ Đã xóa (không vào được)", failNames),
                "Kết quả dọn tk lỗi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Dialogs.Show("Lỗi khi dọn tk: " + ex.Message, "Kiểm tra tk lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    [RelayCommand]
    private void Add()
    {
        var model = new ShopeeAccount { Label = "Tài khoản mới" };
        AccountStore.Shared.Add(model);
        var vm = new AccountItemViewModel(model);
        Items.Add(vm);
        Selected = vm;
        Status = $"{Items.Count} tài khoản.";
    }

    /// <summary>
    /// Xóa các tài khoản đang chọn. Nhận danh sách chọn (DataGrid.SelectedItems) qua CommandParameter
    /// để xóa NHIỀU acc cùng lúc; nếu không có thì xóa acc đang Selected. Xóa hàng loạt bằng 1 lần
    /// ReplaceAll (ghi file + Reload 1 lần) thay vì Remove từng cái (mỗi cái 1 lần ghi → giật).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete(object? selectedItems)
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
        if (Dialogs.Show(msg, "Xóa tài khoản", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var ids = targets.Select(t => t.Model.Id).ToHashSet(StringComparer.Ordinal);
        // ReplaceAll → Save → Changed → Reload tự dựng lại Items (không cần Items.Remove thủ công).
        AccountStore.Shared.ReplaceAll(AccountStore.Shared.Accounts.Where(a => !ids.Contains(a.Id)));
        Status = $"Đã xóa {ids.Count} tài khoản · còn {AccountStore.Shared.Accounts.Count}.";
    }

    [RelayCommand]
    private void Save()
    {
        // Kho lưu chung 1 file accounts.json (toàn bộ tk) → mỗi lần lưu ghi lại cả file (tk vừa sửa +
        // các tk khác giữ nguyên). Báo "đã lưu thay đổi" cho rõ thay vì "đã lưu N tk" (gây hiểu nhầm).
        var name = Selected?.DisplayName;
        AccountStore.Shared.Save();
        Status = string.IsNullOrWhiteSpace(name)
            ? "Đã lưu thay đổi."
            : $"Đã lưu thay đổi \"{name}\".";
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new ImportAccountsWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var logins = SplitLines(dlg.Logins);
        var proxies = SplitLines(dlg.ProxyKeys);

        if (logins.Count == 0 && proxies.Count == 0)
        {
            Dialogs.Show("Chưa nhập gì.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // CHỈ nhập proxy → dàn đều (xoay vòng) cho các tài khoản hiện có: acc1-proxy1, acc2-proxy2,
        // … accX-proxy(X mod N).
        if (logins.Count == 0)
        {
            var accs = AccountStore.Shared.Accounts.ToList();
            if (accs.Count == 0)
            {
                Dialogs.Show("Chưa có tài khoản nào để gán proxy. Hãy nhập tài khoản trước.",
                    "Import proxy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            for (var i = 0; i < accs.Count; i++)
                AssignProxy(accs[i], proxies[i % proxies.Count]);
            AccountStore.Shared.Save(); // Changed → Reload tự cập nhật lưới
            Status = $"Đã gán {proxies.Count} proxy (xoay vòng) cho {accs.Count} tài khoản.";
            return;
        }

        // Có tài khoản → tạo mới; nếu có proxy thì gán xoay vòng theo từng tài khoản.
        var added = 0;
        for (var i = 0; i < logins.Count; i++)
        {
            var model = new ShopeeAccount { ShopeeAccountLogin = logins[i] };
            if (proxies.Count > 0) AssignProxy(model, proxies[i % proxies.Count]);
            AccountStore.Shared.Add(model);
            Items.Add(new AccountItemViewModel(model));
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
