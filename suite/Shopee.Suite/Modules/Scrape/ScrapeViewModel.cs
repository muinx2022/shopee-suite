using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Infrastructure;
using Shopee.Core.Scrape;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>
/// Module "Shopee Scrape". Tick chọn 1 hoặc NHIỀU tài khoản BigSeller (mỗi tk 1 shop ↔ sheet/workbook).
/// Hệ thống TỰ ĐỘNG dùng cả kho tài khoản Shopee (xoay vòng), chạy N process song song. Nhiều tk
/// BigSeller chạy LẦN LƯỢT (không đồng thời để khỏi tranh kho Shopee + profile). Tk Shopee dính
/// captcha/proxy lỗi thì tự đổi tk khác.
/// </summary>
public sealed partial class ScrapeViewModel : ObservableObject
{
    public ObservableCollection<ScrapeTargetViewModel> ScrapeTargets { get; } = [];
    public ObservableCollection<ScrapeInstanceViewModel> Instances { get; } = [];
    public ObservableCollection<ErroredAccountRow> ErroredAccounts { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] private string _videoDir = @"D:\videos";
    [ObservableProperty] private string _status = "Sẵn sàng.";
    [ObservableProperty] private int _poolCount;

    /// <summary>Tk BigSeller đang click để xem/sửa config chi tiết (panel phải).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTarget))]
    [NotifyCanExecuteChangedFor(nameof(ShowStatsCommand))]
    private ScrapeTargetViewModel? _selectedTarget;

    public bool HasSelectedTarget => SelectedTarget is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ResumeCommand), nameof(StopCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    private CancellationTokenSource? _cts;
    private readonly object _runnersLock = new();
    private readonly List<ScrapeRunner> _runners = [];

    public ScrapeViewModel()
    {
        Reload();
        AccountStore.Shared.Changed += OnStoresChanged;
        BigSellerStore.Shared.Changed += OnStoresChanged;
    }

    private void OnStoresChanged()
    {
        if (IsBusy) return;
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Reload();
        else d.BeginInvoke(Reload);
    }

    [RelayCommand]
    private void Reload()
    {
        // Giữ lựa chọn cũ (tk đã tick + shop + tk đang xem detail) theo Id để reload không mất trạng thái.
        var prevSelected = ScrapeTargets.Where(t => t.IsSelected).Select(t => t.Account.Id).ToHashSet();
        var prevShop = ScrapeTargets.ToDictionary(t => t.Account.Id, t => t.SelectedShop?.Id);
        var prevDetailId = SelectedTarget?.Account.Id;

        ScrapeTargets.Clear();
        foreach (var a in BigSellerStore.Shared.Accounts)
        {
            var vm = new ScrapeTargetViewModel(a) { IsSelected = prevSelected.Contains(a.Id) };
            vm.SelectedShop =
                (prevShop.TryGetValue(a.Id, out var sid) && sid is not null
                    ? vm.Shops.FirstOrDefault(s => s.Id == sid)
                    : null)
                ?? vm.Shops.FirstOrDefault();
            ScrapeTargets.Add(vm);
        }
        SelectedTarget = ScrapeTargets.FirstOrDefault(t => t.Account.Id == prevDetailId) ?? ScrapeTargets.FirstOrDefault();
        PoolCount = AccountStore.Shared.Accounts.Count(a => !a.Disabled);
        Status = $"{ScrapeTargets.Count} BigSeller · {PoolCount} acc Shopee (tự xoay vòng).";
    }

    [RelayCommand]
    private void SelectAllTargets() { foreach (var t in ScrapeTargets) t.IsSelected = true; }

    [RelayCommand]
    private void UnselectAllTargets() { foreach (var t in ScrapeTargets) t.IsSelected = false; }

    /// <summary>Chạy = RESET: xoá tiến độ đã lưu, chạy lại từ "Từ dòng".</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Run() => StartAsync(resume: false);

    /// <summary>Tiếp tục = RESUME: chỉ chạy các dòng CÒN THIẾU theo tiến độ đã lưu.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Resume() => StartAsync(resume: true);

    private async Task StartAsync(bool resume)
    {
        var picked = ScrapeTargets.Where(t => t.IsSelected).ToList();
        if (picked.Count == 0) { Warn("Tick chọn ít nhất 1 tài khoản BigSeller."); return; }

        var pool = AccountStore.Shared.Accounts.Where(a => !a.Disabled).ToList();
        if (pool.Count == 0) { Warn("Kho chưa có tài khoản Shopee (thêm ở mục Tài khoản & Proxy)."); return; }

        var sourceUserData = BrowserLauncher.DetectUserData(BrowserKind.Brave);
        if (sourceUserData is null)
        { Warn("Không tìm thấy User Data của Brave (profile Default). Hãy mở Brave ít nhất 1 lần."); return; }

        // Validate từng đích (dùng config RIÊNG của từng tk). Đích lỗi bị bỏ qua, không chặn đích khác.
        var jobs = new List<ScrapeTargetViewModel>();
        var problems = new List<string>();
        foreach (var t in picked)
        {
            var a = t.Account; var s = t.SelectedShop;
            if (s is null) { problems.Add($"{a.DisplayName}: chưa chọn shop"); continue; }
            if (!a.HasCookie) { problems.Add($"{a.DisplayName}: chưa có cookie BigSeller (đăng nhập ở mục BigSeller)"); continue; }
            if (string.IsNullOrWhiteSpace(s.ShopeeDataSheet)) { problems.Add($"{a.DisplayName}/{s.DisplayName}: shop chưa gán sheet"); continue; }
            if (string.IsNullOrWhiteSpace(a.WorkbookPath) || !File.Exists(a.WorkbookPath)) { problems.Add($"{a.DisplayName}: workbook không tồn tại"); continue; }
            if (Math.Max(1, t.ShopeeCount) > pool.Count) { problems.Add($"{a.DisplayName}: cần {t.ShopeeCount} tk Shopee nhưng kho chỉ có {pool.Count}"); continue; }
            jobs.Add(t);
        }
        if (jobs.Count == 0) { Warn("Không có tài khoản hợp lệ để scrape.\n" + string.Join("\n", problems)); return; }

        // Loại khỏi kho các tk Shopee đang bị BigSeller KHÁC (chưa hoàn thành) GIỮ CHỖ — tk chỉ được nhả
        // khi BigSeller đó chạy xong (hoặc nhả tay ở Thống kê). BigSeller trong lượt này thì giữ lại tk
        // đặt-chỗ của CHÍNH nó để resume mượn lại.
        var thisRunKeys = jobs
            .Select(j => (j.Account.Id, Sheet: j.SelectedShop!.ShopeeDataSheet))
            .ToHashSet();
        var lockedByOthers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pr in ScrapeProgressStore.Shared.All())
        {
            if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase)) continue;
            if (thisRunKeys.Contains((pr.AccountId, pr.Sheet))) continue;
            foreach (var id in pr.ReservedShopeeAccountIds) lockedByOthers.Add(id);
        }
        var lockedCount = pool.RemoveAll(a => lockedByOthers.Contains(a.Id));

        IsBusy = true;
        _cts = new CancellationTokenSource();
        LogLines.Clear();
        Instances.Clear();
        ErroredAccounts.Clear();
        foreach (var p in problems) Log($"⚠ Bỏ qua {p}.");
        if (lockedCount > 0) Log($"ⓘ {lockedCount} tk Shopee đang bị BigSeller khác giữ (chưa hoàn thành) — bỏ qua lượt này.");
        Log(resume
            ? $"⏯ Tiếp tục {jobs.Count} BigSeller — chỉ chạy phần dòng CÒN THIẾU. Kho {pool.Count} tk Shopee."
            : $"▶ Scrape {jobs.Count} BigSeller (RESET — chạy lại từ đầu). Kho {pool.Count} tk Shopee.");

        // Kho tk Shopee dùng chung: mỗi BigSeller mượn ĐÚNG số tk của nó (take-all-or-wait — chỉ lấy khi
        // đủ cả slice cùng lúc → không giữ một phần → không deadlock). 1 tk không bị 2 BigSeller mở cùng lúc.
        var available = pool.ToList();
        var allocLock = new object();
        var borrowedCount = 0;
        var ct = _cts.Token;

        async Task<List<ShopeeAccount>?> BorrowAsync(int need, IReadOnlyCollection<string> preferIds)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (allocLock)
                {
                    available.RemoveAll(a => a.Disabled);   // tk dính captcha → loại khỏi kho
                    if (available.Count >= need)
                    {
                        // Ưu tiên mượn lại đúng tk đã đặt chỗ trước (resume), rồi mới tới tk bất kỳ.
                        var slice = available
                            .OrderByDescending(a => preferIds.Contains(a.Id))
                            .Take(need)
                            .ToList();
                        foreach (var s in slice) available.Remove(s);
                        borrowedCount++;
                        return slice;
                    }
                    if (borrowedCount == 0) return null;   // không ai đang giữ tk để trả + không đủ → bỏ cuộc
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        void GiveBack(List<ShopeeAccount> slice)
        {
            lock (allocLock) { available.AddRange(slice.Where(a => !a.Disabled)); borrowedCount--; }
        }

        async Task RunOneJobAsync(int jobIndex, ScrapeTargetViewModel target)
        {
            var account = target.Account;
            var shop = target.SelectedShop!;
            var sheet = shop.ShopeeDataSheet;
            var need = Math.Max(1, target.ShopeeCount);
            var maxProc = Math.Max(1, Math.Min(target.MaxProcess, need));
            var startRow = Math.Max(1, target.StartRow);
            var rowsPer = Math.Max(1, target.RowsPerAccount);

            List<ShopeeAccount>? borrowed = null;
            ScrapeRunner? runner = null;
            try
            {
                int totalRows;
                try { totalRows = await Task.Run(() => ScrapeWorkbook.TotalDataRows(account.WorkbookPath, sheet), ct).ConfigureAwait(false); }
                catch (Exception ex) { Log($"[{account.DisplayName}] ✘ lỗi đọc workbook: {ex.Message} — bỏ qua."); return; }
                // "Đến dòng" > 0 → DỪNG tại đó (cắt tổng số dòng cần chạy).
                var endRow = Math.Max(0, target.EndRow);
                if (endRow > 0 && endRow < totalRows) totalRows = endRow;
                if (totalRows < startRow) { Log($"[{account.DisplayName}] sheet \"{sheet}\" chỉ có {totalRows} dòng (bắt đầu {startRow}) — bỏ qua."); return; }

                // RESET → xoá tiến độ cũ. Tính các khoảng cần chạy (reset = cả đoạn; resume = phần còn thiếu).
                if (!resume) ScrapeProgressStore.Shared.Clear(account.Id, sheet);
                var segments = resume
                    ? ScrapeProgressStore.Shared.RemainingSegments(account.Id, sheet, startRow, totalRows)
                    : new List<(int from, int to)> { (startRow, totalRows) };
                if (segments.Count == 0)
                {
                    Log($"[{account.DisplayName}] ✓ Không còn dòng nào để chạy (đã xong tới {totalRows}). Thêm dòng mới rồi Tiếp tục.");
                    return;
                }

                // Resume: ưu tiên mượn lại đúng tk đã đặt chỗ trước đó.
                var preferIds = (resume ? ScrapeProgressStore.Shared.Find(account.Id, sheet)?.ReservedShopeeAccountIds : null) ?? [];

                borrowed = await BorrowAsync(need, preferIds).ConfigureAwait(false);
                var usable = (borrowed ?? []).Where(a => !a.Disabled).ToList();
                if (usable.Count == 0) { Log($"[{account.DisplayName}] không đủ tk Shopee khả dụng — bỏ qua."); return; }

                var procs = Math.Max(1, Math.Min(maxProc, usable.Count));
                OnUi(() =>
                {
                    for (var i = 1; i <= procs; i++)
                        Instances.Add(new ScrapeInstanceViewModel($"{jobIndex}:P{i}", $"[{account.DisplayName}] P{i}"));
                });

                var totalSeg = segments.Sum(s => s.to - s.from + 1);
                Log($"── {(resume ? "⏯ Tiếp tục" : "▶")} BigSeller \"{account.DisplayName}\" · shop \"{shop.DisplayName}\" · {totalSeg} dòng cần chạy (tổng sheet {totalRows}) · {usable.Count} tk Shopee ({procs} process) ──");

                // Ghi nhận lượt chạy + ĐẶT CHỖ tk Shopee vào store (giữ tới khi hoàn thành / nhả tay).
                var reservedIds = usable.Select(a => a.Id).ToList();
                if (resume) ScrapeProgressStore.Shared.BeginResume(account.Id, sheet, account.DisplayName, reservedIds, totalRows);
                else ScrapeProgressStore.Shared.BeginFresh(account.Id, sheet, account.DisplayName, reservedIds, totalRows);
                OnUi(target.RefreshProgress);

                runner = new ScrapeRunner(account.WorkbookPath, VideoDir, braveExe: null, sourceUserData, bigSellerAccountName: account.DisplayName);
                // Mỗi chunk xong → lưu tiến độ ngay (bền với dừng/treo).
                runner.RowsCompleted += (from, to) =>
                {
                    ScrapeProgressStore.Shared.MarkCompleted(account.Id, sheet, from, to);
                    OnUi(target.RefreshProgress);
                };
                WireRunner(runner, jobIndex, account.DisplayName);
                lock (_runnersLock) _runners.Add(runner);
                var specs = usable.Select(a => ToSpec(a, sheet)).ToList();

                // Mượn BÙ từ kho khi 1 tk hỏng (captcha/lỗi 2 lần): lấy 1 tk còn rảnh, đặt-chỗ + để GiveBack.
                Task<ScrapeAccountSpec?> BorrowReplacementAsync()
                {
                    ShopeeAccount? acc = null;
                    lock (allocLock)
                    {
                        available.RemoveAll(a => a.Disabled);
                        if (available.Count > 0) { acc = available[0]; available.RemoveAt(0); borrowed!.Add(acc); }
                    }
                    if (acc is null) return Task.FromResult<ScrapeAccountSpec?>(null);   // hết tk trong kho
                    ScrapeProgressStore.Shared.AddReservedShopeeAccount(account.Id, sheet, acc.Id);
                    Log($"[{account.DisplayName}] ➕ mượn bù tk Shopee \"{acc.DisplayName}\" (thay tk hỏng).");
                    OnUi(target.RefreshProgress);
                    return Task.FromResult<ScrapeAccountSpec?>(ToSpec(acc, sheet));
                }

                await runner.RunAutoAsync(specs, segments, rowsPer, totalRows, procs, account.CookieFile, ct, BorrowReplacementAsync).ConfigureAwait(false);

                // Kết thúc: xong hết [startRow..total] → completed + nhả tk; còn dở → stopped + GIỮ tk.
                ScrapeProgressStore.Shared.FinishRun(account.Id, sheet, startRow, totalRows);
                var after = ScrapeProgressStore.Shared.Find(account.Id, sheet);
                Log(string.Equals(after?.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    ? $"[{account.DisplayName}] ✔ Hoàn thành toàn bộ — đã nhả {need} tk Shopee về kho."
                    : $"[{account.DisplayName}] ■ Chưa xong (xong tới dòng {after?.LastRowReached ?? 0}) — GIỮ tk cho lần Tiếp tục.");
            }
            catch (OperationCanceledException)
            {
                Log($"[{account.DisplayName}] ■ đã dừng — giữ tiến độ + tk cho lần Tiếp tục.");
                try { ScrapeProgressStore.Shared.FinishRun(account.Id, sheet, startRow, 0); } catch { }
            }
            catch (Exception ex) { Log($"[{account.DisplayName}] ✘ lỗi: {ex.Message}"); }
            finally
            {
                if (runner is not null) lock (_runnersLock) _runners.Remove(runner);
                if (borrowed is not null) GiveBack(borrowed);
                OnUi(target.RefreshProgress);
            }
        }

        try
        {
            var tasks = new List<Task>();
            for (var ji = 0; ji < jobs.Count; ji++)
            {
                var jobIndex = ji;
                var target = jobs[ji];
                tasks.Add(Task.Run(() => RunOneJobAsync(jobIndex, target), ct));
            }
            await Task.WhenAll(tasks);
            Status = ct.IsCancellationRequested ? "Đã dừng." : $"Hoàn tất {jobs.Count} tài khoản.";
            Log($"── {Status} ──");
        }
        catch (Exception ex)
        {
            if (_cts?.IsCancellationRequested == true) { Status = "Đã dừng."; Log("── Đã dừng. ──"); }
            else Warn("Lỗi scrape: " + ex.Message);
        }
        finally
        {
            lock (_runnersLock) _runners.Clear();
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private async Task Stop()
    {
        _cts?.Cancel();
        Status = "Đang dừng…";
        List<ScrapeRunner> snapshot;
        lock (_runnersLock) snapshot = _runners.ToList();
        foreach (var r in snapshot)
        {
            try { await r.StopAllAsync(); } catch { }
        }
    }

    /// <summary>Mở cửa sổ Thống kê của tk BigSeller đang chọn: số tk giữ, tiến độ, dòng cuối, nhả tay.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedTarget))]
    private void ShowStats()
    {
        if (SelectedTarget is null) return;
        var vm = new ScrapeStatsViewModel(SelectedTarget.Account.Id, SelectedTarget.Account.DisplayName);
        var win = new ScrapeStatsWindow(vm) { Owner = Application.Current?.MainWindow };
        win.ShowDialog();
        SelectedTarget.RefreshProgress();   // có thể đã nhả tay / xoá tiến độ → cập nhật nhãn.
    }

    private static ScrapeAccountSpec ToSpec(ShopeeAccount a, string sheet) => new(
        a.Id, a.DisplayName, a.ShopeeAccountLogin, a.OpenWithShopeeAccount,
        a.KiotProxyKey, a.Region, a.ProxyType, a.ManualProxy, a.RequireProxy, sheet, 0, 0,
        ResolveShopeeProfileDir(a));

    /// <summary>Thư mục profile (Edge) đã đăng nhập Shopee của tk — engine import session từ đây sang
    /// Brave để khỏi login form. ProfileRelativePath có thể tuyệt đối (do tab Kiểm tra tài khoản lưu)
    /// hoặc tương đối "profiles/{Id}".</summary>
    private static string ResolveShopeeProfileDir(ShopeeAccount a)
    {
        var rel = a.ProfileRelativePath;
        if (string.IsNullOrWhiteSpace(rel))
            return Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", a.Id);
        return Path.IsPathRooted(rel)
            ? rel
            : Path.Combine(SuitePaths.ModuleDir("shared"), rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private void WireRunner(ScrapeRunner runner, int jobIndex, string bigSellerName)
    {
        string K(string key) => $"{jobIndex}:{key}";   // namespace key theo job để nhiều BigSeller chạy đồng thời không đụng lưới
        runner.InstanceLog += (key, line) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            LogLines.Add($"[{bigSellerName}][{inst?.Label ?? key}] {line}");
        });
        runner.InstanceStatus += (key, st) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            if (inst is not null) inst.Status = st;
        });
        runner.SlotAssigned += (key, account, range) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            if (inst is not null) { inst.AccountName = account; inst.RangeText = range; }
        });
        runner.AccountErrored += (id, label, reason) => OnUi(() =>
        {
            var now = DateTime.Now.ToString("HH:mm:ss");
            var row = ErroredAccounts.FirstOrDefault(x => x.Id == id);
            if (row is null) ErroredAccounts.Insert(0, new ErroredAccountRow(id, label, reason, now));
            else { row.Reason = reason; row.Time = now; }
            LogLines.Add($"⚠ Tk lỗi: {label} — {reason}");
            FlagAccountErrored(id, $"Dính captcha/lỗi (Scrape) — {DateTime.Now:dd/MM HH:mm}: {reason}");
        });
    }

    // Đánh dấu BỀN account dính captcha/lỗi: Disabled (tự bỏ qua lượt sau) + LastError → gom ở mục
    // "Tài khoản & Proxy" (bộ lọc "Bị lỗi / captcha") để xử lý sau rồi "Bật lại".
    private static void FlagAccountErrored(string id, string reason)
    {
        var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null || acc.Disabled) return;
        acc.Disabled = true;
        acc.LastError = reason;
        AccountStore.Shared.Save();
    }

    private void Log(string text) => OnUi(() => LogLines.Add(text));

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    private void Warn(string msg)
    {
        Status = msg;
        Dialogs.Show(msg, "Shopee Scrape", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
