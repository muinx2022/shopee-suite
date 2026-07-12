using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Modules.UpdateProduct;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.BigSeller;

/// <summary>
/// Mục "BigSeller" dùng chung (Scrape + Update Product). Quản lý kho <see cref="BigSellerStore"/>:
/// tài khoản BigSeller + workbook + danh sách shop (mỗi shop 1 sheet) + đăng nhập lấy cookie chung.
/// </summary>
public sealed partial class BigSellerViewModel : ModuleViewModelBase
{
    public ObservableCollection<BigSellerAccountItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(LoginCommand), nameof(AddShopCommand), nameof(CleanMediasCommand))]
    private BigSellerAccountItemViewModel? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShopSelection))]
    private BigSellerShopViewModel? _selectedShop;

    public bool HasSelection => Selected is not null;
    public bool HasShopSelection => SelectedShop is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(StopLoginCommand), nameof(LoginAllCommand))]
    private bool _isLoggingIn;

    public bool IsIdle => !IsLoggingIn;

    private CancellationTokenSource? _loginCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanMediasCommand), nameof(StopCleanMediasCommand))]
    private bool _isCleaningMedia;

    private CancellationTokenSource? _mediaCts;

    // Chiếu kho BigSeller → Items, giữ Selected theo Id (idiom Store.Changed→Reload gom vào ObservableProjection).
    private readonly ObservableProjection<BigSellerAccount, BigSellerAccountItemViewModel> _projection;

    public BigSellerViewModel() : base("bigseller-login.log", "BigSeller")
    {
        _projection = new ObservableProjection<BigSellerAccount, BigSellerAccountItemViewModel>(
            Items, () => BigSellerStore.Shared.Accounts, a => new BigSellerAccountItemViewModel(a),
            i => i.Model.Id, a => a.Id, () => Selected, v => Selected = v);
        Reload();
        // Khôi phục/import (BackupService) cập nhật kho dùng chung rồi bắn Changed → tab này phải nạp lại
        // (trước đây chỉ Reload lúc khởi tạo nên import xong vẫn trống tới khi mở lại app).
        BigSellerStore.Shared.Changed += OnStoreChanged;
    }

    private void OnStoreChanged() => UiThread.Post(SyncFromStore);

    /// <summary>Nạp lại toàn bộ khi TẬP tài khoản đổi (import/khôi phục, Add/Delete) — KHÔNG rebuild khi chỉ
    /// sửa thuộc tính (tránh mất focus lúc đang nhập + tránh nhân đôi dòng). Khi tập acc không đổi, vẫn đối
    /// chiếu danh sách SHOP của từng acc: Hub sync có thể thêm/bớt shop trên một acc ĐÃ có (tập acc y nguyên
    /// nên guard trên không bắt được) → trước đây shop mới từ Hub không hiện tới khi khởi động lại app.</summary>
    private void SyncFromStore()
    {
        // Tập acc đổi → rebuild (giữ Selected theo Id); tập y nguyên → fast-path rẻ: chỉ đối chiếu danh sách
        // SHOP của từng acc (Hub sync có thể thêm/bớt shop trên một acc ĐÃ có → guard tập-acc không bắt được).
        if (_projection.ReloadIfChanged()) { Status = $"{Items.Count} tài khoản BigSeller."; return; }
        foreach (var item in Items) item.SyncShopsFromModel();   // fast-path rẻ khi tập shop không đổi
    }

    private void Reload()
    {
        _projection.Rebuild();
        Status = $"{Items.Count} tài khoản BigSeller.";
    }

    private bool SaveStore(string success, string failure)
    {
        if (BigSellerStore.Shared.Save())
        {
            Status = success;
            return true;
        }

        Status = failure;
        return false;
    }

    [RelayCommand]
    private void Add()
    {
        // Acc MỚI tạo từ client mặc định hub-mode (kho SP ở Postgres — workbook Excel đã bỏ đồng bộ). Đổi lại
        // sang excel-mode làm trên web Hub nếu cần bản chuyển tiếp.
        var model = new BigSellerAccount { Label = "BigSeller mới", DataSource = "hub" };
        if (BigSellerStore.Shared.Add(model))   // → Changed → SyncFromStore dựng lại Items (gồm acc mới)
        {
            Selected = Items.FirstOrDefault(i => i.Model.Id == model.Id) ?? Selected;
            Status = $"{Items.Count} tài khoản BigSeller.";
            HubBigSellerUpsert.Schedule();   // đẩy acc mới lên Hub (kẻo lượt pull kế mirror-xoá)
        }
        else
        {
            Status = "Không thêm được tài khoản BigSeller mới.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var sel = Selected;
        if (sel is null) return;
        if (!await Dialogs.ConfirmAsync($"Xóa tài khoản BigSeller \"{sel.DisplayName}\"?", "Xóa"))
            return;
        if (BigSellerStore.Shared.Remove(sel.Model.Id))   // → Changed → SyncFromStore dựng lại Items
        {
            Selected = Items.FirstOrDefault();
            Status = $"{Items.Count} tài khoản BigSeller.";
        }
        else
        {
            Status = $"Không xóa được \"{sel.DisplayName}\".";
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (SaveStore("Đã lưu cấu hình BigSeller.", "Không lưu được cấu hình BigSeller."))
            HubBigSellerUpsert.Schedule();   // lưu tay → đẩy field chung mới lên Hub
    }

    [RelayCommand]
    private async Task BrowseWorkbookAsync()
    {
        var sel = Selected;
        if (sel is null) return;
        var path = await FilePicker.OpenFileAsync("Chọn workbook", "Excel|*.xlsx;*.xlsm|Tất cả|*.*");
        if (path is null) return;
        sel.WorkbookPath = path;
        SaveStore(
            $"Workbook có {sel.SheetOptions.Count} sheet.",
            "Không lưu được đường dẫn workbook.");
    }

    [RelayCommand]
    private async Task BrowseCookieAsync()
    {
        var sel = Selected;
        if (sel is null) return;
        var path = await FilePicker.SaveFileAsync("File cookie BigSeller", "JSON|*.json",
            defaultFileName: string.IsNullOrWhiteSpace(sel.CookieFile) ? "bigseller-cookies.json" : Path.GetFileName(sel.CookieFile),
            overwritePrompt: false);
        if (path is null) return;
        sel.CookieFile = path;
        SaveStore("Đã cập nhật file cookie BigSeller.", "Không lưu được file cookie BigSeller.");
    }

    [RelayCommand]
    private void RefreshSheets()
    {
        if (Selected is null) return;
        Selected.RefreshSheets();
        Status = $"Workbook có {Selected.SheetOptions.Count} sheet.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddShop()
    {
        if (Selected is null) return;
        SelectedShop = Selected.AddShop();
        if (SaveStore("Đã thêm shop mới.", "Không lưu được shop mới."))
            HubBigSellerUpsert.Schedule();   // đẩy shop mới lên Hub
    }

    [RelayCommand]
    private void RemoveShop()
    {
        if (Selected is null || SelectedShop is null) return;
        Selected.RemoveShop(SelectedShop);
        SelectedShop = Selected.Shops.FirstOrDefault();
        SaveStore("Đã xóa shop.", "Không lưu được thay đổi danh sách shop.");
    }

    // ── Đăng nhập BigSeller (lấy cookie chung) ──────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (Selected is null) return;

        // Mặc định file cookie nếu chưa đặt.
        if (string.IsNullOrWhiteSpace(Selected.CookieFile))
        {
            Selected.CookieFile = Path.Combine(
                SuitePaths.ModuleDir("shared"), "bigseller-cookies", Selected.Model.Id + ".json");
            Save();
        }

        IsLoggingIn = true;
        LogLines.Clear();
        _loginCts = new CancellationTokenSource();
        var cookieFile = Selected.CookieFile;
        var account = Selected;
        try
        {
            // Profile Brave RIÊNG theo từng tài khoản (theo Id) → mỗi tk BigSeller 1 phiên/cookie độc
            // lập, không bị "đăng nhập vào tk cũ" do dùng chung profile.
            var profileDir = Path.Combine(SuitePaths.ModuleDir("bigseller-login"), account.Model.Id);

            // Tk có proxy riêng (KiotProxy key) → ĐĂNG NHẬP qua đúng proxy đó để token lưu ra khớp IP với
            // lúc scrape (scrape route bigseller.com qua proxy này). Không có key → mở IP máy như cũ.
            string? proxyServer = null;
            if (account.Model.HasProxy)
            {
                Log("Đang lấy proxy riêng của tk BigSeller (KiotProxy)…");
                proxyServer = await OpenMultiBraveLauncherV3.BigSellerProxyResolver.ResolveServerAsync(
                    account.Model.KiotProxyKey, account.Model.Region, account.Model.ProxyType, Log);
            }

            // Tk có email + mật khẩu → khi mở profile mà CHƯA đăng nhập, TỰ điền form + giải captcha (AI) thay vì
            // đợi login tay. Chạy trong CHÍNH Brave vừa mở (khớp IP proxy nếu có). Thiếu email/mật khẩu → đợi tay như cũ.
            Func<int, CancellationToken, Task<bool>>? autoLogin = null;
            if (!string.IsNullOrWhiteSpace(account.Model.Email) && !string.IsNullOrWhiteSpace(account.Model.Password))
            {
                autoLogin = async (port, token) =>
                {
                    var outcome = await OpenMultiBraveLauncherV3.BigSellerAutoLogin.ForceLoginInBraveAsync(
                        port, account.Model.Id, account.Model.Email, account.Model.Password, cookieFile, Log, token);
                    if (outcome == OpenMultiBraveLauncherV3.AutoLoginOutcome.NeedsOtp)
                        Log("⚠ BigSeller đòi mã email (thiết bị mới) — đăng nhập TAY 1 lần trong cửa sổ để tạo device-trust; sau đó auto-login chạy được (chỉ captcha).");
                    return outcome == OpenMultiBraveLauncherV3.AutoLoginOutcome.Success;
                };
            }

            // Lưu cookie xong, cửa sổ Brave GIỮ NGUYÊN — chỉ đóng khi bấm Dừng. onSaved báo ngay
            // khi vừa lưu (lúc cửa sổ còn đang mở) để UI cập nhật trạng thái cookie tức thì.
            var ok = await BigSellerLoginRunner.RunLoginAsync(
                cookieFile, profileDir, Log, _loginCts.Token, () => OnLoginSaved(account), proxyServer, autoLogin);
            account.NotifyCookieChanged();
            Status = ok ? "Đăng nhập BigSeller thành công." : "Chưa lấy được cookie BigSeller.";

            // MÁY HUB: đẩy cookie vừa lấy lên Hub NGAY để client kéo về liền, khỏi chờ auto-push (3 phút).
            // Chỉ Hub là nguồn → chỉ Hub push; client (hiếm khi tự login) thì không. KHÔNG truyền _loginCts.Token
            // (lúc này đã bị Cancel do bấm Dừng để đóng cửa sổ) — push phải chạy độc lập. Best-effort.
            if (ok && HubServerConfigStore.Shared.Current.Enabled
                   && CoordinationRuntime.ConfigSync is { } sync)
            {
                Log("Đang đẩy cookie mới lên Hub để các máy khác đồng bộ…");
                try { Log(await sync.PushAsync()); }
                catch (Exception ex) { Log("✘ Đẩy lên Hub lỗi (sẽ tự đẩy lại ở chu kỳ auto): " + ex.Message); }
            }
        }
        finally
        {
            IsLoggingIn = false;
            _loginCts.Dispose();
            _loginCts = null;
        }
    }

    /// <summary>Xóa TOÀN BỘ media trong Material Center (thư viện ảnh) BigSeller của tk đang chọn — chạy
    /// luồng dọn media của Update sản phẩm theo yêu cầu (mở Brave riêng bằng cookie tk, dọn xong tự đóng).</summary>
    [RelayCommand(CanExecute = nameof(CanCleanMedias))]
    private async Task CleanMediasAsync()
    {
        var sel = Selected;
        if (sel is null) return;

        // Chưa có cookie → không có phiên BigSeller để dọn; hướng người dùng đăng nhập trước.
        if (!sel.Model.HasCookie)
        {
            Warn($"{sel.DisplayName}: chưa có cookie BigSeller — bấm \"Mở Profile Bigseller\" hoặc \"Đăng nhập tất cả\" trước.");
            return;
        }

        // Xóa trên server BigSeller, KHÔNG hoàn tác → hỏi xác nhận rõ ràng.
        if (!await Dialogs.ConfirmAsync(
                $"Xóa TOÀN BỘ media trong thư viện ảnh (Material Center) của tk \"{sel.DisplayName}\" trên BigSeller?\n\n" +
                "• Xóa trên server BigSeller, KHÔNG hoàn tác được.\n" +
                "• App sẽ mở 1 cửa sổ Brave riêng, dọn xong tự đóng — không ảnh hưởng update đang chạy.",
                "Xóa Medias"))
            return;

        IsCleaningMedia = true;
        LogLines.Clear();
        _mediaCts = new CancellationTokenSource();
        var a = sel.Model;
        var s = a.Shops.FirstOrDefault();
        try
        {
            Status = $"Đang xóa media (Material Center) của \"{sel.DisplayName}\"…";
            // Context tối thiểu: dọn media chỉ cần account/cookie (+ shop để lấy profile/port); workbook/AI không dùng.
            var ctx = new UpdateProductContext(
                a.Id, a.Email, a.WorkbookPath, a.CookieFile,
                s?.Id ?? "", s?.DisplayName ?? "", s?.ShopeeDataSheet ?? "",
                "", "", 1, "",
                0, 0,
                "", "", "", false,
                1, 1, 5,
                Password: a.Password);
            var runner = new UpdateProductRunner();
            runner.Log += Log;
            await runner.RunMediaCleanupAsync(ctx, _mediaCts.Token);
            Status = $"✔ Đã xóa media (Material Center) của \"{sel.DisplayName}\".";
        }
        catch (OperationCanceledException) { Status = "■ Đã dừng xóa media."; Log(Status); }
        catch (Exception ex) { Status = "✘ Lỗi xóa media: " + ex.Message; Log(Status); }
        finally
        {
            IsCleaningMedia = false;
            _mediaCts.Dispose();
            _mediaCts = null;
        }
    }

    private bool CanCleanMedias() => HasSelection && !IsCleaningMedia;

    [RelayCommand(CanExecute = nameof(IsCleaningMedia))]
    private void StopCleanMedias() => _mediaCts?.Cancel();

    /// <summary>TỰ ĐĂNG NHẬP TẤT CẢ (headless, KHÔNG hiện cửa sổ): lần lượt từng tk có đủ Email+Mật khẩu → mở
    /// Brave --headless, điền form + giải captcha (AI) → LƯU cookie ra file (client dùng chung). Tk đòi mã email
    /// (thiết bị mới) sẽ được báo để đăng nhập TAY 1 lần (Mở Profile) tạo device-trust.</summary>
    [RelayCommand(CanExecute = nameof(CanLoginAll))]
    private async Task LoginAllAsync()
    {
        var accts = Items.Where(a => !string.IsNullOrWhiteSpace(a.Model.Email) && !string.IsNullOrWhiteSpace(a.Model.Password)).ToList();
        if (accts.Count == 0)
        {
            await Dialogs.InfoAsync("Chưa tài khoản nào điền đủ Email + Mật khẩu BigSeller để tự đăng nhập.", "Đăng nhập tất cả");
            return;
        }
        if (!await Dialogs.ConfirmAsync(
                $"Tự đăng nhập HEADLESS (không hiện cửa sổ) {accts.Count} tài khoản BigSeller rồi lưu cookie?\n\n" +
                "• Cần API key OpenAI (trang Cấu hình AI trên Hub) để giải captcha.\n" +
                "• Tk đòi mã email (thiết bị mới) sẽ được báo — vào Mở Profile đăng nhập tay 1 lần để tạo device-trust.",
                "Đăng nhập tất cả"))
            return;

        IsLoggingIn = true;
        LogLines.Clear();
        _loginCts = new CancellationTokenSource();
        int ok = 0, otp = 0, fail = 0;
        try
        {
            for (var i = 0; i < accts.Count; i++)
            {
                _loginCts.Token.ThrowIfCancellationRequested();
                var a = accts[i];
                Status = $"[{i + 1}/{accts.Count}] Đang tự đăng nhập \"{a.DisplayName}\"…  (✔{ok} ⚠{otp} ✘{fail})";
                Log($"[{i + 1}/{accts.Count}] {a.DisplayName} — tự đăng nhập headless…");

                // Mặc định file cookie nếu chưa đặt (client dùng chung với scrape/update).
                if (string.IsNullOrWhiteSpace(a.CookieFile))
                {
                    a.CookieFile = Path.Combine(SuitePaths.ModuleDir("shared"), "bigseller-cookies", a.Model.Id + ".json");
                    Save();
                }

                // Proxy riêng của tk (nếu có) → token mint khớp IP với scrape.
                string? proxy = null;
                if (a.Model.HasProxy)
                {
                    try { proxy = await OpenMultiBraveLauncherV3.BigSellerProxyResolver.ResolveServerAsync(a.Model.KiotProxyKey, a.Model.Region, a.Model.ProxyType, Log); }
                    catch (Exception ex) { Log("  Lấy proxy lỗi: " + ex.Message + " — thử IP máy."); }
                }

                var outcome = await OpenMultiBraveLauncherV3.BigSellerAutoLogin.LoginHeadlessAsync(
                    a.Model.Id, a.Model.Email, a.Model.Password, a.CookieFile, proxy, Log, _loginCts.Token);

                switch (outcome)
                {
                    case OpenMultiBraveLauncherV3.AutoLoginOutcome.Success:
                        ok++; a.NotifyCookieChanged(); Log($"  ✔ {a.DisplayName}: đã lưu cookie."); break;
                    case OpenMultiBraveLauncherV3.AutoLoginOutcome.NeedsOtp:
                        otp++; Log($"  ⚠ {a.DisplayName}: BigSeller đòi mã email (thiết bị mới) — vào Mở Profile đăng nhập TAY 1 lần."); break;
                    default:
                        fail++; Log($"  ✘ {a.DisplayName}: chưa đăng nhập được (kiểm mật khẩu / thử lại)."); break;
                }
            }
            Status = $"Đăng nhập tất cả xong: ✔ {ok} · ⚠ {otp} cần OTP · ✘ {fail} lỗi (/{accts.Count} tk).";
            await Dialogs.InfoAsync(Status, "Đăng nhập tất cả");
        }
        catch (OperationCanceledException) { Status = $"Đã dừng. Đã xong ✔{ok} ⚠{otp} ✘{fail}."; }
        catch (Exception ex) { Status = "✘ Lỗi: " + ex.Message; }
        finally { IsLoggingIn = false; _loginCts?.Dispose(); _loginCts = null; }
    }

    private bool CanLoginAll() => IsIdle && Items.Count > 0;

    /// <summary>Gọi từ luồng nền của runner ngay khi cookie vừa được lưu (cửa sổ vẫn mở).</summary>
    private void OnLoginSaved(BigSellerAccountItemViewModel account)
    {
        void Apply()
        {
            account.NotifyCookieChanged();
            Status = "✔ Đã lưu cookie — cửa sổ vẫn mở, bấm Dừng khi xong.";
        }
        UiThread.Post(Apply);
    }

    private bool CanLogin() => HasSelection && IsIdle;

    [RelayCommand(CanExecute = nameof(IsLoggingIn))]
    private void StopLogin() => _loginCts?.Cancel();
}
