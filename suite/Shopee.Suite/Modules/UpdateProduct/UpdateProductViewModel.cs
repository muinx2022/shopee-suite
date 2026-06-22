using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Modules.UpdateProduct;

namespace Shopee.Suite.Modules.UpdateProduct;

/// <summary>
/// Module "Bigseller Update Product". Tick chọn 1 hoặc NHIỀU tài khoản BigSeller (mỗi tk 1 shop ↔ sheet
/// + map cột riêng) rồi chạy 1 trong 3 workflow SONG SONG cho mọi tk đã chọn: Import to store (Playwright),
/// Update product (C#/Playwright), Update tên SP (AI). Cookie BigSeller lấy từ kho chung.
/// </summary>
public sealed partial class UpdateProductViewModel : ObservableObject
{
    /// <summary>Mỗi tài khoản BigSeller là 1 "đích chạy" (tick chọn + chọn shop) — chạy được nhiều tk song song.</summary>
    public ObservableCollection<UpdateRunTargetViewModel> RunTargets { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    // Ảnh/Video DÙNG CHUNG cho mọi tk; từ-dòng/đến-dòng/worker là RIÊNG từng tk (xem UpdateRunTargetViewModel).
    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private string _videoFolder = "";
    [ObservableProperty] private string _openAiKeyFile = "";
    [ObservableProperty] private string _status = "Sẵn sàng.";

    /// <summary>Tk BigSeller đang click để xem/sửa cấu hình chi tiết (panel phải) — giống Shopee Scrape.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTarget))]
    private UpdateRunTargetViewModel? _selectedTarget;

    public bool HasSelectedTarget => SelectedTarget is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunImportCommand), nameof(RunUpdateCommand), nameof(RunNameRewriteCommand), nameof(StopCommand))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    private CancellationTokenSource? _cts;
    private readonly object _runnersLock = new();
    private readonly List<UpdateProductRunner> _runners = [];

    public UpdateProductViewModel()
    {
        Reload();
        BigSellerStore.Shared.Changed += () =>
        {
            if (IsRunning) return;
            var d = Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) Reload();
            else d.BeginInvoke(Reload);
        };
    }

    [RelayCommand]
    private void Reload()
    {
        // Giữ lựa chọn + cấu hình per-target cũ theo Id để reload không mất trạng thái.
        var prevSelected = RunTargets.Where(t => t.IsSelected).Select(t => t.Account.Id).ToHashSet();
        var prevShop = RunTargets.ToDictionary(t => t.Account.Id, t => t.SelectedShop?.Id);
        var prevCfg = RunTargets.ToDictionary(t => t.Account.Id,
            t => (t.StartRow, t.EndRow, t.ImportWorkers, t.UpdateWorkers, t.ListingReloadSeconds));
        var prevDetailId = SelectedTarget?.Account.Id;

        RunTargets.Clear();
        foreach (var a in BigSellerStore.Shared.Accounts)
        {
            var vm = new UpdateRunTargetViewModel(a) { IsSelected = prevSelected.Contains(a.Id) };
            vm.SelectedShop =
                (prevShop.TryGetValue(a.Id, out var sid) && sid is not null
                    ? vm.Shops.FirstOrDefault(s => s.Id == sid)
                    : null)
                ?? vm.Shops.FirstOrDefault();
            if (prevCfg.TryGetValue(a.Id, out var c))
                (vm.StartRow, vm.EndRow, vm.ImportWorkers, vm.UpdateWorkers, vm.ListingReloadSeconds) = c;
            RunTargets.Add(vm);
        }
        SelectedTarget = RunTargets.FirstOrDefault(t => t.Account.Id == prevDetailId) ?? RunTargets.FirstOrDefault();
        Status = $"{RunTargets.Count} tài khoản BigSeller.";
    }

    [RelayCommand]
    private void SelectAllTargets() { foreach (var t in RunTargets) t.IsSelected = true; }

    [RelayCommand]
    private void UnselectAllTargets() { foreach (var t in RunTargets) t.IsSelected = false; }

    [RelayCommand]
    private void BrowseImage()
    {
        var dlg = new OpenFileDialog { Filter = "Ảnh|*.jpg;*.jpeg;*.png;*.webp|Tất cả|*.*", Title = "Chọn ảnh" };
        if (dlg.ShowDialog() == true) ImagePath = dlg.FileName;
    }

    [RelayCommand]
    private void BrowseVideoFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục video" };
        if (dlg.ShowDialog() == true) VideoFolder = dlg.FolderName;
    }

    /// <summary>Mở dialog map field ↔ cột Excel cho shop của 1 đích chạy (mỗi shop dữ liệu có thể khác).</summary>
    [RelayCommand]
    private void OpenMap(UpdateRunTargetViewModel? target)
    {
        var shop = target?.SelectedShop;
        if (shop is null) { Warn("Chọn shop trước khi map dữ liệu."); return; }
        var win = new ColumnMapWindow(shop) { Owner = Application.Current?.MainWindow };
        win.ShowDialog();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RunImport() => RunWorkflowAsync("Import to store", (r, ctx, ct) => r.RunImportAsync(ctx, ct));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RunUpdate() => RunWorkflowAsync("Update product", (r, ctx, ct) => r.RunUpdateAsync(ctx, ct));

    // Name-rewrite chỉ đọc workbook + OpenAI, KHÔNG mở BigSeller → không cần kiểm tra đăng nhập.
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RunNameRewrite() => RunWorkflowAsync("Update tên SP", (r, ctx, ct) => r.RunNameRewriteAsync(ctx, ct),
        requiresBigSellerLogin: false);

    private async Task RunWorkflowAsync(
        string name, Func<UpdateProductRunner, UpdateProductContext, CancellationToken, Task> action,
        bool requiresBigSellerLogin = true)
    {
        var picked = RunTargets.Where(t => t.IsSelected).ToList();
        if (picked.Count == 0) { Warn("Chưa tick chọn tài khoản BigSeller nào để chạy."); return; }

        var ai = AiConfigStore.Shared.Current;

        // Validate từng đích; tk lỗi (chưa cookie/sheet/workbook) bị bỏ qua, không chặn các tk khác.
        var jobs = new List<(BigSellerAccount Account, UpdateProductContext Ctx)>();
        var problems = new List<string>();
        foreach (var t in picked)
        {
            var a = t.Account; var s = t.SelectedShop;
            if (s is null) { problems.Add($"{a.DisplayName}: chưa chọn shop"); continue; }
            if (string.IsNullOrWhiteSpace(s.ShopeeDataSheet)) { problems.Add($"{a.DisplayName}/{s.DisplayName}: shop chưa gán sheet"); continue; }
            if (string.IsNullOrWhiteSpace(a.WorkbookPath) || !File.Exists(a.WorkbookPath)) { problems.Add($"{a.DisplayName}: workbook không tồn tại"); continue; }
            if (requiresBigSellerLogin && !a.HasCookie) { problems.Add($"{a.DisplayName}: chưa có cookie BigSeller"); continue; }
            jobs.Add((a, BuildContext(t, ai)));
        }

        if (jobs.Count == 0) { Warn($"Không có tài khoản hợp lệ để chạy {name}.\n" + string.Join("\n", problems)); return; }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        LogLines.Clear();
        Log($"▶ {name} — chạy SONG SONG {jobs.Count} tài khoản.");
        foreach (var p in problems) Log($"  ⚠ Bỏ qua {p}.");

        try
        {
            var ct = _cts.Token;
            var tasks = jobs.Select(j => Task.Run(() => RunOneAsync(name, action, j.Account, j.Ctx, ct), ct)).ToList();
            await Task.WhenAll(tasks);
            Status = ct.IsCancellationRequested ? "Đã dừng." : $"Hoàn tất: {name} ({jobs.Count} tài khoản).";
            Log($"── {Status} ──");
        }
        catch (Exception ex)
        {
            if (_cts?.IsCancellationRequested == true) { Status = "Đã dừng."; Log("── Đã dừng. ──"); }
            else Warn($"Lỗi {name}: " + ex.Message);
        }
        finally
        {
            lock (_runnersLock) _runners.Clear();
            // KHÔNG Dispose _cts: tác vụ async sót có thể còn dùng token → bỏ tham chiếu cho GC dọn.
            _cts = null;
            IsRunning = false;
        }
    }

    private UpdateProductContext BuildContext(UpdateRunTargetViewModel t, AiConfig ai)
    {
        var a = t.Account; var s = t.SelectedShop!;
        // AI (viết lại tên/mô tả) dùng cấu hình AI CHUNG ở Cài đặt; key truyền THẲNG qua context
        // (không set biến môi trường process-wide để Brave/Playwright không kế thừa key).
        var aiModel = string.IsNullOrWhiteSpace(ai.OpenAiModel) ? s.OpenAiModel : ai.OpenAiModel;
        return new UpdateProductContext(
            a.Id, a.Email, a.WorkbookPath, a.CookieFile,
            s.Id, s.DisplayName, s.ShopeeDataSheet,
            aiModel, "", ai.BatchSize, "",
            t.StartRow, t.EndRow, ImagePath, VideoFolder,
            s.BigSellerCrawlUrl, s.BigSellerImportFromClaimedTab,
            t.ImportWorkers, t.UpdateWorkers, t.ListingReloadSeconds, ai.OpenAiApiKey,
            s.ColumnMap.LinkColumn, s.ColumnMap.PriceColumn, s.ColumnMap.SkuColumn,
            s.ColumnMap.ItemIdColumn, s.ColumnMap.ProductNameColumn, s.ColumnMap.RewrittenNameColumn);
    }

    private async Task RunOneAsync(
        string name, Func<UpdateProductRunner, UpdateProductContext, CancellationToken, Task> action,
        BigSellerAccount account, UpdateProductContext ctx, CancellationToken ct)
    {
        var prefix = $"[{account.DisplayName}]";
        var runner = new UpdateProductRunner();
        runner.Log += m => Log($"{prefix} {m}");
        lock (_runnersLock) _runners.Add(runner);
        try
        {
            await action(runner, ctx, ct).ConfigureAwait(false);
            Log($"{prefix} ✔ xong {name}.");
        }
        catch (OperationCanceledException) { Log($"{prefix} ■ đã dừng."); }
        catch (Exception ex) { Log($"{prefix} ✖ lỗi: {ex.Message}"); }
        finally { lock (_runnersLock) _runners.Remove(runner); }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _cts?.Cancel();
        lock (_runnersLock)
            foreach (var r in _runners) { try { r.Stop(); } catch { } }
        Status = "Đang dừng…";
    }

    private void Log(string text)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) LogLines.Add(text);
        else d.BeginInvoke(() => LogLines.Add(text));
    }

    private void Warn(string msg)
    {
        Status = msg;
        Dialogs.Show(msg, "Update Product", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
