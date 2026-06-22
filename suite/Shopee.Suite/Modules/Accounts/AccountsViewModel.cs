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

    [ObservableProperty] private string _selectedFilter = "Còn dùng được";
    partial void OnSelectedFilterChanged(string value) => Reload();

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

    /// <summary>Mở Brave với ĐÚNG profile + proxy của tk này tới Shopee để KIỂM TRA tay (giải captcha,
    /// xem còn login không). Cùng profile mà Scrape import session → giải captcha xong, lần scrape sau OK.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Check()
    {
        if (Selected is null) return;
        var a = Selected.Model;
        var name = a.DisplayName;
        Status = $"Đang mở Brave kiểm tra \"{name}\"…";
        try
        {
            var bravePath = BrowserLauncher.Detect(BrowserKind.Brave)
                ?? throw new FileNotFoundException("Không tìm thấy brave.exe. Hãy cài Brave Browser.");
            var proxy = await ResolveProxyAsync(a);
            var profileDir = Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", a.Id);
            Directory.CreateDirectory(profileDir);

            var args = new List<string>
            {
                $"--user-data-dir=\"{profileDir}\"",
                "--profile-directory=Default",
                "--new-window",
                "--no-first-run",
                "--no-default-browser-check",
                "--hide-crash-restore-bubble",
            };
            if (!string.IsNullOrWhiteSpace(proxy)) args.Add($"--proxy-server={proxy}");
            args.Add("\"https://shopee.vn/\"");

            // Cửa sổ này KHÔNG do app quản lý (không CDP) → mở rồi để người dùng tự thao tác/đóng.
            Process.Start(new ProcessStartInfo
            {
                FileName = bravePath,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
            });
            Status = $"Đã mở Brave kiểm tra \"{name}\" (proxy: {proxy ?? "không"}). Giải captcha xong, đóng cửa sổ rồi bấm \"Bật lại\".";
        }
        catch (Exception ex)
        {
            Dialogs.Show("Không mở được kiểm tra: " + ex.Message, "Kiểm tra", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = "Lỗi mở kiểm tra.";
        }
    }

    private static async Task<string?> ResolveProxyAsync(ShopeeAccount a)
    {
        if (!string.IsNullOrWhiteSpace(a.ManualProxy))
            return a.ManualProxy.Contains("://") ? a.ManualProxy.Trim() : "http://" + a.ManualProxy.Trim();
        if (!string.IsNullOrWhiteSpace(a.KiotProxyKey))
        {
            var r = await KiotProxyClient.FetchNewAsync(a.KiotProxyKey, default, a.ProxyType);
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

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is null) return;
        if (Dialogs.Show($"Xóa tài khoản \"{Selected.DisplayName}\"?", "Xóa tài khoản",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        AccountStore.Shared.Remove(Selected.Model.Id);
        Items.Remove(Selected);
        Selected = Items.FirstOrDefault();
        Status = $"{Items.Count} tài khoản.";
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
