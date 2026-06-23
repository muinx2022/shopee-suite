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

    // Phiên chạy hiện tại (sống khi IsBusy) — chứa pool tk Shopee dùng chung + registry job để
    // chạy/dừng RIÊNG từng tk giữa chừng. null = đang rảnh.
    private RunSession? _session;

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
        // Tk đang xem ở panel chi tiết — chỉ là trạng thái UI (không lưu), giữ lại qua reload cho mượt.
        var prevDetailId = SelectedTarget?.Account.Id;

        ScrapeTargets.Clear();
        // Mỗi ScrapeTargetViewModel TỰ nạp config đã lưu (tick chọn + shop + số dòng/process) theo
        // Account.Id từ ScrapeTargetConfigStore → giữ nguyên lựa chọn người dùng qua reload + khởi động lại.
        foreach (var a in BigSellerStore.Shared.Accounts)
            ScrapeTargets.Add(new ScrapeTargetViewModel(a));
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
            if (ValidateTarget(t, pool.Count, out var problem)) jobs.Add(t);
            else problems.Add(problem);
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

        var session = new RunSession { SourceUserData = sourceUserData, Resume = resume };
        session.Available.AddRange(pool);
        // Seed bộ đếm vòng-LRU = mốc cao nhất đã lưu → cấp phát tiếp vòng, không nện lại tk đầu sau restart.
        session.LruTick = AccountStore.Shared.Accounts.Select(a => a.LastUsedTick).DefaultIfEmpty(0).Max();
        _session = session;

        IsBusy = true;
        LogLines.Clear();
        Instances.Clear();
        ErroredAccounts.Clear();
        foreach (var p in problems) Log($"⚠ Bỏ qua {p}.");
        if (lockedCount > 0) Log($"ⓘ {lockedCount} tk Shopee đang bị BigSeller khác giữ (chưa hoàn thành) — bỏ qua lượt này.");
        Log(resume
            ? $"⏯ Tiếp tục {jobs.Count} BigSeller — chỉ chạy phần dòng CÒN THIẾU. Kho {pool.Count} tk Shopee."
            : $"▶ Scrape {jobs.Count} BigSeller (RESET — chạy lại từ đầu). Kho {pool.Count} tk Shopee.");

        // Phóng job cho từng tk đã chọn (mỗi job = 1 token RIÊNG → dừng được lẻ giữa chừng).
        foreach (var t in jobs) StartJob(session, t, resume);

        // Coordinator: chờ tới khi registry rỗng. Cho phép thêm/bớt job ĐỘNG giữa chừng (StartOne/StopOne).
        try
        {
            while (true)
            {
                Task[] running;
                lock (session.JobsLock)
                {
                    running = session.Jobs.Values.Select(h => h.Task).ToArray();
                    if (running.Length == 0) { session.Finalizing = true; break; }   // set + break atomically dưới lock
                }
                // KHÔNG ConfigureAwait(false): giữ UI thread để finally set Status/IsBusy an toàn
                // (set ObservableProperty + NotifyCanExecuteChanged phải ở UI thread).
                await Task.WhenAny(running);
            }
        }
        finally
        {
            Status = session.MasterCts.IsCancellationRequested ? "Đã dừng." : $"Hoàn tất {jobs.Count} tài khoản.";
            Log($"── {Status} ──");
            try { AccountStore.Shared.Save(); } catch { }   // lưu LastUsedTick (vòng-LRU) bền qua restart
            _session = null;                                 // null TRƯỚC khi Dispose để StartOne kịp bail
            session.MasterCts.Dispose();
            IsBusy = false;
        }
    }

    // Mượn slice tk Shopee theo VÒNG-LRU (tk nghỉ lâu nhất / chưa dùng trước). take-all-or-wait: chỉ lấy
    // khi đủ cả slice cùng lúc → không giữ một phần → không deadlock. 1 tk không bị 2 BigSeller mở cùng lúc.
    private async Task<List<ShopeeAccount>?> BorrowAsync(
        RunSession s, int need, IReadOnlyCollection<string> preferIds, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (s.AllocLock)
            {
                s.Available.RemoveAll(a => a.Disabled);   // tk dính captcha → loại khỏi kho
                if (s.Available.Count >= need)
                {
                    var slice = s.Available
                        .OrderByDescending(a => preferIds.Contains(a.Id))   // resume: ưu tiên tk đã đặt chỗ
                        .ThenBy(a => a.LastUsedTick)                          // rồi tk NGHỈ LÂU NHẤT (0 = chưa dùng) trước
                        .Take(need)
                        .ToList();
                    foreach (var x in slice) { s.Available.Remove(x); x.LastUsedTick = Interlocked.Increment(ref s.LruTick); }
                    s.BorrowedCount++;
                    return slice;
                }
                if (s.BorrowedCount == 0) return null;   // không ai đang giữ tk để trả + không đủ → bỏ cuộc
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static void GiveBack(RunSession s, List<ShopeeAccount> slice)
    {
        lock (s.AllocLock) { s.Available.AddRange(slice.Where(a => !a.Disabled)); s.BorrowedCount--; }
    }

    private async Task RunOneJobAsync(RunSession s, JobHandle h, bool resume)
    {
        var target = h.Target;
        var account = target.Account;
        var shop = target.SelectedShop!;
        var sheet = shop.ShopeeDataSheet;
        var need = Math.Max(1, target.ShopeeCount);
        var maxProc = Math.Max(1, Math.Min(target.MaxProcess, need));
        var startRow = Math.Max(1, target.StartRow);
        var rowsPer = Math.Max(1, target.RowsPerAccount);
        var seq = h.Seq;
        var ct = h.Cts.Token;

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

            int freeNow; lock (s.AllocLock) freeNow = s.Available.Count;
            Log($"[{account.DisplayName}] đang xin {need} tk Shopee từ kho (rảnh {freeNow})"
                + (freeNow < need ? " — CHỜ tk khác nhả (take-all-or-wait)…" : "…"));
            borrowed = await BorrowAsync(s, need, preferIds, ct).ConfigureAwait(false);
            var usable = (borrowed ?? []).Where(a => !a.Disabled).ToList();
            if (usable.Count == 0) { Log($"[{account.DisplayName}] không đủ tk Shopee khả dụng — bỏ qua."); return; }

            var procs = Math.Max(1, Math.Min(maxProc, usable.Count));
            OnUi(() =>
            {
                for (var i = 1; i <= procs; i++)
                    Instances.Add(new ScrapeInstanceViewModel($"{seq}:P{i}", $"[{account.DisplayName}] P{i}"));
            });

            var totalSeg = segments.Sum(x => x.to - x.from + 1);
            Log($"── {(resume ? "⏯ Tiếp tục" : "▶")} BigSeller \"{account.DisplayName}\" · shop \"{shop.DisplayName}\" · {totalSeg} dòng cần chạy (tổng sheet {totalRows}) · {usable.Count} tk Shopee ({procs} process) ──");

            // Ghi nhận lượt chạy + ĐẶT CHỖ tk Shopee vào store (giữ tới khi hoàn thành / nhả tay).
            var reservedIds = usable.Select(a => a.Id).ToList();
            if (resume) ScrapeProgressStore.Shared.BeginResume(account.Id, sheet, account.DisplayName, reservedIds, totalRows);
            else ScrapeProgressStore.Shared.BeginFresh(account.Id, sheet, account.DisplayName, reservedIds, totalRows);
            OnUi(target.RefreshProgress);

            runner = new ScrapeRunner(account.WorkbookPath, VideoDir, braveExe: null, s.SourceUserData, bigSellerAccountName: account.DisplayName);
            lock (s.JobsLock) h.Runner = runner;
            // Mỗi chunk xong → lưu tiến độ ngay (bền với dừng/treo).
            runner.RowsCompleted += (from, to) =>
            {
                ScrapeProgressStore.Shared.MarkCompleted(account.Id, sheet, from, to);
                OnUi(target.RefreshProgress);
            };
            WireRunner(runner, seq, account.DisplayName);
            var specs = usable.Select(a => ToSpec(a, sheet)).ToList();

            // Mượn BÙ từ kho khi 1 tk dính CAPTCHA: lấy 1 tk nghỉ lâu nhất, đặt-chỗ + để GiveBack.
            Task<ScrapeAccountSpec?> BorrowReplacementAsync()
            {
                ShopeeAccount? acc = null;
                lock (s.AllocLock)
                {
                    s.Available.RemoveAll(a => a.Disabled);
                    if (s.Available.Count > 0)
                    {
                        acc = s.Available.OrderBy(a => a.LastUsedTick).First();   // tk nghỉ lâu nhất
                        s.Available.Remove(acc);
                        acc.LastUsedTick = Interlocked.Increment(ref s.LruTick);
                        borrowed!.Add(acc);
                    }
                }
                if (acc is null) return Task.FromResult<ScrapeAccountSpec?>(null);   // hết tk trong kho
                ScrapeProgressStore.Shared.AddReservedShopeeAccount(account.Id, sheet, acc.Id);
                Log($"[{account.DisplayName}] ➕ mượn bù tk Shopee \"{acc.DisplayName}\" (thay tk dính captcha).");
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
            lock (s.JobsLock) s.Jobs.Remove(account.Id);
            if (borrowed is not null) GiveBack(s, borrowed);
            // Dọn các dòng process của job này khỏi lưới (trước đây dòng cũ không bao giờ bị xoá).
            var prefix = seq + ":";
            OnUi(() => { for (var i = Instances.Count - 1; i >= 0; i--) if (Instances[i].Key.StartsWith(prefix, StringComparison.Ordinal)) Instances.RemoveAt(i); });
            OnUi(target.RefreshProgress);
            h.Cts.Dispose();
        }
    }

    /// <summary>Tạo + phóng 1 job cho tk BigSeller. Gán Task TRONG lock (tránh coordinator thấy Task rỗng).
    /// Trả false nếu phiên đang kết thúc hoặc tk đó đã có job đang chạy.</summary>
    private bool StartJob(RunSession s, ScrapeTargetViewModel target, bool resume)
    {
        lock (s.JobsLock)
        {
            if (s.Finalizing) return false;
            if (s.Jobs.ContainsKey(target.Account.Id)) return false;   // chặn trùng job/tk
            var h = new JobHandle
            {
                Target = target,
                Seq = Interlocked.Increment(ref s.JobSeq),
                Cts = CancellationTokenSource.CreateLinkedTokenSource(s.MasterCts.Token),
            };
            // KHÔNG truyền token vào Task.Run: nếu token đã huỷ lúc lên lịch, body (và finally dọn dẹp)
            // sẽ KHÔNG chạy → job kẹt trong registry → coordinator lặp vô hạn. Body tự kiểm token + bắt OCE.
            h.Task = Task.Run(() => RunOneJobAsync(s, h, resume));
            s.Jobs[target.Account.Id] = h;
        }
        return true;
    }

    /// <summary>Chạy RIÊNG 1 tk giữa lúc đang run (tick checkbox khi busy). Mid-run = RESUME.
    /// Trả true nếu đã phóng job.</summary>
    private bool StartOneAccount(ScrapeTargetViewModel target)
    {
        var s = _session;
        if (s is null || s.MasterCts.IsCancellationRequested) return false;
        var poolCount = AccountStore.Shared.Accounts.Count(a => !a.Disabled);
        if (!ValidateTarget(target, poolCount, out var problem)) { Warn($"Không chạy được: {problem}"); return false; }
        // Acc này giờ THUỘC lượt chạy → đòi lại các tk Shopee mà CHÍNH nó đang đặt-chỗ (đã bị loại khỏi
        // kho phiên lúc bắt đầu vì khi đó nó là "BigSeller khác"). Không có bước này, acc start giữa chừng
        // sẽ thiếu tk và kẹt chờ vô hạn ở BorrowAsync.
        ReclaimReservedToPool(s, target);
        if (StartJob(s, target, resume: true))
        {
            Log($"➕ [{target.DisplayName}] đã thêm vào lượt chạy (tiếp tục phần còn thiếu)…");
            return true;
        }
        Log($"[{target.DisplayName}] đang chạy rồi — bỏ qua.");
        return false;
    }

    /// <summary>Đưa các tk Shopee mà <paramref name="target"/> đang ĐẶT CHỖ (theo tiến độ đã lưu) trở lại
    /// kho phiên — chỉ những tk chưa có trong kho và đang rảnh thật (không bị job khác trong phiên giữ).</summary>
    private void ReclaimReservedToPool(RunSession s, ScrapeTargetViewModel target)
    {
        var shop = target.SelectedShop;
        if (shop is null) return;
        var reserved = ScrapeProgressStore.Shared.Find(target.Account.Id, shop.ShopeeDataSheet)?.ReservedShopeeAccountIds;
        if (reserved is null || reserved.Count == 0) return;

        // Tk đang bị các job KHÁC trong phiên giữ (đang đặt-chỗ ở store, status running, không phải acc này) → KHÔNG đòi.
        var heldByOthers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pr in ScrapeProgressStore.Shared.All())
        {
            if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase)) continue;
            if (pr.AccountId == target.Account.Id && string.Equals(pr.Sheet ?? "", shop.ShopeeDataSheet ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            lock (s.JobsLock) if (!s.Jobs.ContainsKey(pr.AccountId)) continue;   // chỉ né tk của job ĐANG chạy trong phiên
            foreach (var id in pr.ReservedShopeeAccountIds) heldByOthers.Add(id);
        }

        var added = 0;
        lock (s.AllocLock)
        {
            var have = new HashSet<string>(s.Available.Select(a => a.Id), StringComparer.Ordinal);
            foreach (var id in reserved)
            {
                if (have.Contains(id) || heldByOthers.Contains(id)) continue;
                var acc = AccountStore.Shared.Find(id);
                if (acc is null || acc.Disabled) continue;
                s.Available.Add(acc);
                have.Add(id);
                added++;
            }
        }
        if (added > 0) Log($"↩ [{target.DisplayName}] đòi lại {added} tk Shopee đang đặt-chỗ về kho phiên.");
    }

    /// <summary>Dừng RIÊNG 1 tk (untick khi busy): huỷ token + đóng Brave của RIÊNG runner đó; tk khác chạy tiếp.</summary>
    private async Task StopOneAccount(ScrapeTargetViewModel target)
    {
        var s = _session;
        if (s is null) return;
        JobHandle? h;
        lock (s.JobsLock) s.Jobs.TryGetValue(target.Account.Id, out h);
        if (h is null) return;
        h.Cts.Cancel();                                   // bẻ gãy chờ BorrowAsync / AcquireAsync / RunChunk
        var runner = h.Runner;
        if (runner is not null) { try { await runner.StopAllAsync(); } catch { } }
        // finally của job tự dọn: xoá khỏi Jobs, GiveBack, xoá dòng lưới, FinishRun(...,0)=giữ tiến độ+tk.
    }

    /// <summary>Click checkbox khi ĐANG run: hỏi xác nhận rồi chạy/dừng RIÊNG tk đó. Gọi từ code-behind.</summary>
    public async Task ToggleAccountDuringRun(ScrapeTargetViewModel target)
    {
        var s = _session;
        if (s is null) return;
        bool running;
        lock (s.JobsLock) running = s.Jobs.ContainsKey(target.Account.Id);

        if (running)
        {
            if (Dialogs.Show($"Xác nhận HỦY chạy \"{target.DisplayName}\"?\nCác tài khoản khác vẫn chạy bình thường.",
                    "Hủy chạy tài khoản", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await StopOneAccount(target);
            target.IsSelected = false;
        }
        else
        {
            if (Dialogs.Show($"Xác nhận CHẠY \"{target.DisplayName}\" (tiếp tục phần dòng còn thiếu)?",
                    "Chạy tài khoản", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (StartOneAccount(target)) target.IsSelected = true;
        }
    }

    /// <summary>Kiểm tra 1 đích Scrape có hợp lệ để chạy không (shop/cookie/sheet/workbook/đủ tk).</summary>
    private static bool ValidateTarget(ScrapeTargetViewModel t, int poolCount, out string problem)
    {
        var a = t.Account; var s = t.SelectedShop;
        if (s is null) { problem = $"{a.DisplayName}: chưa chọn shop"; return false; }
        if (!a.HasCookie) { problem = $"{a.DisplayName}: chưa có cookie BigSeller (đăng nhập ở mục BigSeller)"; return false; }
        if (string.IsNullOrWhiteSpace(s.ShopeeDataSheet)) { problem = $"{a.DisplayName}/{s.DisplayName}: shop chưa gán sheet"; return false; }
        if (string.IsNullOrWhiteSpace(a.WorkbookPath) || !File.Exists(a.WorkbookPath)) { problem = $"{a.DisplayName}: workbook không tồn tại"; return false; }
        if (Math.Max(1, t.ShopeeCount) > poolCount) { problem = $"{a.DisplayName}: cần {t.ShopeeCount} tk Shopee nhưng kho chỉ có {poolCount}"; return false; }
        problem = ""; return true;
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private async Task Stop()
    {
        var s = _session;
        if (s is null) return;
        s.MasterCts.Cancel();   // mọi token job (linked) huỷ theo
        Status = "Đang dừng…";
        List<ScrapeRunner> snapshot;
        lock (s.JobsLock) snapshot = s.Jobs.Values.Where(h => h.Runner is not null).Select(h => h.Runner!).ToList();
        foreach (var r in snapshot)
        {
            try { await r.StopAllAsync(); } catch { }
        }
        // KHÔNG null _session ở đây — coordinator finally lo (sau khi job drain xong).
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

    private void WireRunner(ScrapeRunner runner, int seq, string bigSellerName)
    {
        string K(string key) => $"{seq}:{key}";   // namespace key theo job để nhiều BigSeller chạy đồng thời không đụng lưới
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

    // ── Phiên chạy + handle từng job (để chạy/dừng RIÊNG từng tk khi đang run) ──
    private sealed class RunSession
    {
        public required string SourceUserData;
        public readonly List<ShopeeAccount> Available = [];        // kho tk Shopee dùng chung trong phiên
        public readonly object AllocLock = new();
        public int BorrowedCount;
        public readonly CancellationTokenSource MasterCts = new(); // huỷ TOÀN phiên (Stop)
        public readonly Dictionary<string, JobHandle> Jobs = new(StringComparer.Ordinal);   // key = BigSeller Account.Id
        public readonly object JobsLock = new();
        public int JobSeq;          // tăng dần — namespace key lưới UI (thay jobIndex cũ)
        public bool Finalizing;     // coordinator đã chốt kết thúc (đặt dưới JobsLock)
        public bool Resume;         // chế độ phiên
        public long LruTick;        // bộ đếm vòng-LRU cấp phát tk Shopee
    }

    private sealed class JobHandle
    {
        public required ScrapeTargetViewModel Target;
        public required int Seq;
        public required CancellationTokenSource Cts;   // linked tới MasterCts → dừng RIÊNG 1 tk
        public ScrapeRunner? Runner;
        public Task Task = Task.CompletedTask;
    }
}
