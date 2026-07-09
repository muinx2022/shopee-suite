using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Modules.UpdateProduct;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.UpdateProduct;

/// <summary>Loại workflow update đang chạy của 1 shop — để UI đổi đúng nút (Import/Update/Tên SP) ⇄ Dừng.</summary>
public enum UpdateKind { Import, Update, Rewrite }

/// <summary>
/// Module "Bigseller Update Product". Tick chọn 1 hoặc NHIỀU tài khoản BigSeller (mỗi tk 1 shop ↔ sheet
/// + map cột riêng) rồi chạy 1 trong 3 workflow SONG SONG cho mọi tk đã chọn: Import to store (Playwright),
/// Update product (C#/Playwright), Update tên SP (AI). Cookie BigSeller lấy từ kho chung.
/// </summary>
public sealed partial class UpdateProductViewModel : ModuleViewModelBase
{
    /// <summary>Mỗi tài khoản BigSeller là 1 "đích chạy" (tick chọn + chọn shop) — chạy được nhiều tk song song.</summary>
    public ObservableCollection<UpdateRunTargetViewModel> RunTargets { get; } = [];

    // Ảnh/Video DÙNG CHUNG cho mọi tk; từ-dòng/đến-dòng/worker là RIÊNG từng tk (xem UpdateRunTargetViewModel).
    // Điền sẵn mặc định để khỏi gõ lại mỗi lần mở app (vẫn sửa được trong cấu hình update).
    [ObservableProperty] private string _imagePath = @"D:\images\1.jpg";
    [ObservableProperty] private string _videoFolder = @"D:\videos";
    [ObservableProperty] private string _openAiKeyFile = "";

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

    private bool _uiLoaded;

    // Chiếu kho BigSeller → RunTargets, giữ SelectedTarget theo Id (idiom Store.Changed→Reload gom vào
    // ObservableProjection). Guard "chỉ rebuild khi cấu trúc đổi" vẫn nằm ở SyncFromStore (richer than id-set).
    private readonly ObservableProjection<BigSellerAccount, UpdateRunTargetViewModel> _projection;

    public UpdateProductViewModel() : base("workspace-update.log", "Update Product")
    {
        // Khôi phục ảnh/video/key OpenAI đã LƯU (dùng chung mọi tk) — trước đây mất khi đóng app.
        var ui = UpdateProductUiStore.Shared.Current;
        _imagePath = ui.ImagePath;
        _videoFolder = ui.VideoFolder;
        _openAiKeyFile = ui.OpenAiKeyFile;
        _uiLoaded = true;

        _projection = new ObservableProjection<BigSellerAccount, UpdateRunTargetViewModel>(
            RunTargets, () => BigSellerStore.Shared.Accounts, a => new UpdateRunTargetViewModel(a),
            t => t.Account.Id, a => a.Id, () => SelectedTarget, v => SelectedTarget = v);
        Reload();
        BigSellerStore.Shared.Changed += () =>
        {
            if (IsRunning || HasActiveWsJob) return;   // đang chạy (batch hoặc inline per-shop) → đừng rebuild list
            UiThread.Post(SyncFromStore);
            // Máy THIẾU ảnh local → sau khi sync acc/workbook, tự kéo ảnh chung Hub về khu workbook + trỏ ô chọn ảnh
            // (chỉ khi đang không có ảnh hợp lệ → tránh gọi mạng liên tục; bản Hub đổi sẽ được lấy lại lúc chạy Update).
            if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath))
                _ = EnsureUpdateImageAsync(CancellationToken.None);
        };
    }

    /// <summary>Chỉ rebuild khi CẤU TRÚC đổi (thêm/bớt account hoặc shop) — KHÔNG rebuild khi chỉ sửa thuộc
    /// tính (tick/shop/config GIỜ lưu trên model nên không cần dựng lại), tránh mất focus lúc đang gõ +
    /// tránh vòng lặp Save→Changed→Reload vô ích.</summary>
    private void SyncFromStore()
    {
        var storeSig = Sig(BigSellerStore.Shared.Accounts);
        var vmSig = string.Join("|", RunTargets.Select(t => t.Account.Id + ":" + string.Join(",", t.Shops.Select(s => s.Id))));
        if (storeSig == vmSig && !ShopsDetached()) return;
        Reload();
    }

    /// <summary>Id trùng nhưng OBJECT shop trong VM khác object trong store (bị thay nguyên danh sách, ví dụ
    /// sync Hub cũ) → phải Reload, không thì SelectedShop "mồ côi": gõ worker/dòng rơi vào object chết,
    /// Persist không lưu được, Hub giao việc lại đọc object live → chạy sai config.</summary>
    private bool ShopsDetached() => RunTargets.Any(t =>
        !t.Shops.SequenceEqual(t.Account.Shops, ReferenceEqualityComparer.Instance));

    private static string Sig(IEnumerable<BigSellerAccount> accounts) =>
        string.Join("|", accounts.Select(a => a.Id + ":" + string.Join(",", a.Shops.Select(s => s.Id))));

    // Ảnh/Video/key OpenAI (dùng chung) — LƯU ngay khi đổi để bền qua mở lại app.
    partial void OnImagePathChanged(string value) => SaveUiSettings();
    partial void OnVideoFolderChanged(string value) => SaveUiSettings();
    partial void OnOpenAiKeyFileChanged(string value) => SaveUiSettings();

    private void SaveUiSettings()
    {
        if (!_uiLoaded) return;   // đừng lưu trong lúc đang nạp ban đầu
        UpdateProductUiStore.Shared.Save(new UpdateProductUiSettings
        {
            ImagePath = ImagePath,
            VideoFolder = VideoFolder,
            OpenAiKeyFile = OpenAiKeyFile,
        });
    }

    [RelayCommand]
    private void Reload()
    {
        // tick chọn + shop đang chọn + cấu hình chạy GIỜ lưu trên model (BigSellerStore) → VM (factory của
        // projection) tự khôi phục tick/shop/config từ model đã LƯU. Chỉ giữ lựa chọn panel-chi-tiết (thuần
        // UI) theo Id — ObservableProjection lo.
        _projection.Rebuild();
        Status = $"{RunTargets.Count} tài khoản BigSeller.";
    }

    [RelayCommand]
    private void SelectAllTargets() { foreach (var t in RunTargets) t.IsSelected = true; }

    [RelayCommand]
    private void UnselectAllTargets() { foreach (var t in RunTargets) t.IsSelected = false; }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var path = await FilePicker.OpenFileAsync("Chọn ảnh", "Ảnh|*.jpg;*.jpeg;*.png;*.webp|Tất cả|*.*");
        if (path is not null) ImagePath = path;
    }

    [RelayCommand]
    private async Task BrowseVideoFolderAsync()
    {
        var dir = await FilePicker.PickFolderAsync("Chọn thư mục video");
        if (dir is not null) VideoFolder = dir;
    }

    /// <summary>Mở dialog map field ↔ cột Excel cho shop của 1 đích chạy (mỗi shop dữ liệu có thể khác).</summary>
    [RelayCommand]
    private async Task OpenMapAsync(UpdateRunTargetViewModel? target)
    {
        var shop = target?.SelectedShop;
        if (shop is null) { Warn("Chọn shop trước khi map dữ liệu."); return; }
        await WindowHost.ShowDialogAsync(new ColumnMapWindow(shop));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RunImport() => RunWorkflowAsync("Import to store", (r, ctx, ct) => r.RunImportAsync(ctx, ct));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RunUpdate() => RunWorkflowAsync("Update product", (r, ctx, ct) => r.RunUpdateAsync(ctx, ct), pullSharedImage: true);

    // Name-rewrite chỉ đọc workbook + OpenAI, KHÔNG mở BigSeller → không cần kiểm tra đăng nhập.
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RunNameRewrite() => RunWorkflowAsync("Update tên SP", (r, ctx, ct) => r.RunNameRewriteAsync(ctx, ct),
        requiresBigSellerLogin: false);

    // ── v1.1 (màn gộp): chạy RIÊNG từng tk BigSeller, CHẠY SONG SONG được — mỗi tk 1 token + runner RIÊNG,
    //    ĐỘC LẬP với đường batch của màn cũ (không đụng _cts/_runners/IsRunning). Dùng cho nút inline theo shop. ──
    public Task RunImportSingleAsync(UpdateRunTargetViewModel t, bool silent = false, int? startRow = null, int? endRow = null, bool? importFromClaimedTab = null) =>
        RunOneWorkflowAsync("Import to store", UpdateKind.Import, (r, ctx, ct) => r.RunImportAsync(ctx, ct), t, requiresBigSellerLogin: true, silent: silent, startRow: startRow, endRow: endRow, importFromClaimedTab: importFromClaimedTab);
    public Task RunUpdateSingleAsync(UpdateRunTargetViewModel t, bool silent = false, int? startRow = null, int? endRow = null) =>
        RunOneWorkflowAsync("Update product", UpdateKind.Update, (r, ctx, ct) => r.RunUpdateAsync(ctx, ct), t, requiresBigSellerLogin: true, silent: silent, startRow: startRow, endRow: endRow);
    public Task RunNameRewriteSingleAsync(UpdateRunTargetViewModel t, bool silent = false, int? startRow = null, int? endRow = null) =>
        RunOneWorkflowAsync("Update tên SP", UpdateKind.Rewrite, (r, ctx, ct) => r.RunNameRewriteAsync(ctx, ct), t, requiresBigSellerLogin: false, silent: silent, startRow: startRow, endRow: endRow);

    /// <summary>Tiền-kiểm điều kiện chạy import/update/rewrite cho 1 đích — KHÔNG mở dialog. Cho AssignmentWorker.</summary>
    public bool CanDispatchUpdate(UpdateRunTargetViewModel t, string op, out string problem) =>
        ValidateUpdateTarget(t, requiresBigSellerLogin: op != "rewrite", out problem);

    private sealed class WsJob
    {
        public required CancellationTokenSource Cts;
        public required string ShopId;
        public required UpdateKind Kind;
        public UpdateProductRunner? Runner;
    }
    // Sổ job inline per-shop (key = Account.Id). Kho thuần — refresh UI vẫn do RunOneWorkflowAsync gọi
    // RaiseJobsChanged() đúng chỗ trong try/finally. Xem PerAccountJobRegistry.
    private readonly PerAccountJobRegistry<WsJob> _wsJobs = new();

    /// <summary>Bắn khi tập job update đổi (start/finish) — UI đổi nút Import/Update/Tên SP ⇄ Dừng + khoá tk.</summary>
    public event Action? JobsChanged;

    /// <summary>true nếu tk này đang chạy 1 workflow update (1 tk chỉ 1 workflow tại 1 thời điểm).</summary>
    public bool IsUpdateRunning(string accountId) => _wsJobs.Contains(accountId);

    /// <summary>true nếu CÒN bất kỳ workflow update inline (per-shop) nào đang chạy — để chặn reload/rebuild
    /// list giữa chừng (đường ws KHÔNG set IsRunning của batch).</summary>
    public bool HasActiveWsJob => _wsJobs.Count > 0;

    /// <summary>Shop + loại workflow đang chạy của tk (để biết nút nào ở dòng nào đổi thành Dừng).</summary>
    public bool TryGetRunningUpdate(string accountId, out string shopId, out UpdateKind kind)
    {
        if (_wsJobs.TryGet(accountId, out var job)) { shopId = job.ShopId; kind = job.Kind; return true; }
        shopId = ""; kind = UpdateKind.Import; return false;
    }

    private void RaiseJobsChanged() => UiThread.Post(() => JobsChanged?.Invoke());

    private async Task RunOneWorkflowAsync(
        string name, UpdateKind kind, Func<UpdateProductRunner, UpdateProductContext, CancellationToken, Task> action,
        UpdateRunTargetViewModel t, bool requiresBigSellerLogin, bool force = false, bool silent = false,
        int? startRow = null, int? endRow = null, bool? importFromClaimedTab = null)
    {
        if (!ValidateUpdateTarget(t, requiresBigSellerLogin, out var problem)) { Warn(problem + ".", silent); return; }
        var a = t.Account; var s = t.SelectedShop!;

        // 1 tk BigSeller CHỈ 1 workflow tại 1 thời điểm → các shop CÙNG tk KHÔNG import/update song song.
        // TryAdd = dedup + add ATOMIC dưới lock của registry.
        var job = new WsJob { Cts = new CancellationTokenSource(), ShopId = s.Id, Kind = kind };
        if (!_wsJobs.TryAdd(a.Id, job)) { Warn($"{a.DisplayName}: đang chạy 1 workflow rồi — bấm ■ để dừng trước.", silent); job.Cts.Dispose(); return; }
        // ĐÃ đăng ký job → MỌI thứ sau đây phải nằm TRONG try/finally để dù setup (BuildContext / ctor runner)
        // ném thì finally vẫn gỡ job khỏi _wsJobs (trước đây setup nằm NGOÀI try → throw làm tk kẹt ■ vĩnh viễn).
        var prefix = $"[{a.DisplayName}]";
        var coordOp = kind switch { UpdateKind.Import => CoordOp.Import, UpdateKind.Update => CoordOp.Update, _ => CoordOp.Rewrite };
        var coordKey = new CoordKey(a.Id, s.Id, s.ShopeeDataSheet, coordOp);
        ILeaseHandle? lease = null;
        try
        {
            RaiseJobsChanged();   // → UI: nút vừa bấm đổi thành ■, các nút khác cùng tk khoá lại

            // KHOÁ VIỆC XUYÊN MÁY: 2 máy không cùng import/update/rewrite một shop. Bị giữ / mất hub → CHẶN.
            var attempt = await Coordination.Hub.AcquireAsync(coordKey, force || CoordinationRuntime.ForceNextRun, job.Cts.Token).ConfigureAwait(false);
            if (!attempt.Granted)
            {
                Log($"{prefix} ⛔ {name} đang được máy \"{attempt.Result.BlockedByHostname}\" chạy (hoặc mất kết nối Hub) — bỏ qua. Bấm 'Chạy đè' nếu chắc máy kia đã dừng.");
                return;
            }
            lease = attempt.Handle;

            var ai = AiConfigStore.Shared.Current;
            // CHỈ Update: ảnh local riêng (nếu có) hoặc kéo ảnh chung Hub về khu workbook + trỏ ô chọn ảnh.
            var img = kind == UpdateKind.Update ? await EnsureUpdateImageAsync(job.Cts.Token).ConfigureAwait(false) : ImagePath;
            if (kind == UpdateKind.Update && (string.IsNullOrWhiteSpace(img) || !File.Exists(img)))
            {
                Log($"{prefix} ⚠ Chưa có ảnh Update — BigSeller cần ảnh để cập nhật SP. Upload ảnh chung trên Hub (trang Files) hoặc đặt ảnh local.");
                Coordination.Hub.PublishCompletion(coordKey, "stopped", 0);
                return;
            }
            var ctx = BuildContext(t, ai, startRow, endRow, importFromClaimedTab, kind == UpdateKind.Update ? img : null);
            var runner = new UpdateProductRunner();
            runner.Log += m => Log($"{prefix} {m}");
            // Mỗi dòng import/update/rewrite xong → đẩy lên ledger Hub (khoảng dòng) cho Thống kê "shop này đã làm
            // những dòng nào". Fire-and-forget, no-op khi Hub tắt (chạy 1 máy).
            runner.RowsCompleted += (from, to) => Coordination.Hub.PublishProgress(coordKey, from, to);
            job.Runner = runner;
            Log($"▶ {name} — {prefix} (chạy song song).");
            await action(runner, ctx, job.Cts.Token).ConfigureAwait(false);
            Log($"{prefix} ✔ xong {name}.");
            Coordination.Hub.PublishCompletion(coordKey, "completed", 0);
        }
        catch (OperationCanceledException) { Log($"{prefix} ■ đã dừng."); Coordination.Hub.PublishCompletion(coordKey, "stopped", 0); }
        catch (Exception ex) { Log($"{prefix} ✖ lỗi: {ex.Message}"); Coordination.Hub.PublishCompletion(coordKey, "stopped", 0); }
        finally
        {
            if (lease is not null) { try { await lease.DisposeAsync().ConfigureAwait(false); } catch { } }
            _wsJobs.Remove(a.Id);
            job.Cts.Dispose();
            RaiseJobsChanged();   // → UI: trả nút về trạng thái thường
        }
    }

    /// <summary>Dừng RIÊNG workflow update của 1 tk (các tk khác chạy tiếp). Cho nút ■ inline theo shop.</summary>
    public void StopSingle(string accountId)
    {
        if (_wsJobs.TryGet(accountId, out var job)) CancelJob(job);
    }

    /// <summary>Dừng TẤT CẢ workflow update đang chạy (mọi tk) — cho nút "Dừng tất cả".</summary>
    public void StopAllSingle()
    {
        foreach (var j in _wsJobs.SnapshotSelect(j => j)) CancelJob(j);
    }

    // Huỷ 1 job an toàn: job có thể vừa xong + finally đã Dispose Cts giữa lúc ta đọc dict → Cancel() ném
    // ObjectDisposedException. Nuốt hết (Cancel + Stop) để nút ■ (RelayCommand void) không bao giờ làm crash UI.
    private static void CancelJob(WsJob job)
    {
        try { job.Cts.Cancel(); } catch { }
        try { job.Runner?.Stop(); } catch { }
    }

    private async Task RunWorkflowAsync(
        string name, Func<UpdateProductRunner, UpdateProductContext, CancellationToken, Task> action,
        bool requiresBigSellerLogin = true, IReadOnlyList<UpdateRunTargetViewModel>? only = null, bool pullSharedImage = false)
    {
        // only != null (v1.1): chạy RIÊNG tk được chỉ định. Bảo vệ chặn chạy chồng (đường command đã có
        // CanExecute=IsIdle; guard này thêm an toàn cho đường gọi single).
        if (IsRunning) { Warn("Đang chạy — hãy Dừng trước khi chạy lượt mới."); return; }
        var picked = (only ?? RunTargets.Where(t => t.IsSelected)).ToList();
        if (picked.Count == 0) { Warn("Chưa tick chọn tài khoản BigSeller nào để chạy."); return; }

        var ai = AiConfigStore.Shared.Current;

        // CHỈ Update: đảm bảo ảnh MỘT LẦN trước khi build/chạy song song (ảnh local riêng hoặc kéo ảnh chung Hub về
        // khu workbook + trỏ ô chọn ảnh). Chưa có CTS ở đây nên dùng None — pull nhanh, best-effort, timeout _bulkHttp.
        var img = pullSharedImage ? await EnsureUpdateImageAsync(CancellationToken.None).ConfigureAwait(false) : ImagePath;

        // BigSeller BẮT BUỘC có ảnh để update SP → thiếu ảnh = update fail. Cả local lẫn Hub đều KHÔNG có (offline /
        // chưa upload) → CHẶN sớm + báo rõ, khỏi chạy rồi lỗi từng SP.
        if (pullSharedImage && (string.IsNullOrWhiteSpace(img) || !File.Exists(img)))
        {
            Warn("Chưa có ảnh Update — BigSeller cần ảnh để cập nhật SP.\n" +
                 "→ Upload ảnh chung trên Hub (trang Files) HOẶC chọn ảnh local ở ô \"Ảnh Update (máy này)\".");
            return;
        }

        // Validate từng đích; tk lỗi (chưa cookie/sheet/workbook) bị bỏ qua, không chặn các tk khác.
        var jobs = new List<(BigSellerAccount Account, UpdateProductContext Ctx)>();
        var problems = new List<string>();
        foreach (var t in picked)
        {
            if (!ValidateUpdateTarget(t, requiresBigSellerLogin, out var problem)) { problems.Add(problem); continue; }
            jobs.Add((t.Account, BuildContext(t, ai, imageOverride: pullSharedImage ? img : null)));
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

    // Validate 1 đích update (shop/sheet/workbook/cookie). true = hợp lệ; false → problem = thông điệp lỗi
    // KHÔNG có dấu chấm cuối (caller tự thêm). Dùng CHUNG cho đường single (RunOneWorkflowAsync) lẫn batch
    // (RunWorkflowAsync) → một nguồn sự thật cho "đích nào chạy được".
    private static bool ValidateUpdateTarget(UpdateRunTargetViewModel t, bool requiresBigSellerLogin, out string problem)
    {
        var a = t.Account; var s = t.SelectedShop;
        if (s is null) { problem = $"{a.DisplayName}: chưa chọn shop"; return false; }
        if (string.IsNullOrWhiteSpace(s.ShopeeDataSheet)) { problem = $"{a.DisplayName}/{s.DisplayName}: shop chưa gán sheet"; return false; }
        if (string.IsNullOrWhiteSpace(a.WorkbookPath) || !File.Exists(a.WorkbookPath)) { problem = $"{a.DisplayName}: workbook không tồn tại"; return false; }
        if (requiresBigSellerLogin && !a.HasCookie) { problem = $"{a.DisplayName}: chưa có cookie BigSeller"; return false; }
        problem = ""; return true;
    }

    /// <summary>Đường dẫn ảnh Update dùng chung tải từ Hub về — nằm CÙNG khu workbook (hub-cache\workbooks\).
    /// BigSeller cần ảnh THẬT (đường dẫn cụ thể) để update; đây là ảnh "chọn sẵn" cho máy thiếu ảnh local.</summary>
    private static string SyncedUpdateImagePath => Path.Combine(SuitePaths.HubCacheDir, "workbooks", "default-update.jpg");

    /// <summary>Đảm bảo có ảnh Update rồi TRẢ đường dẫn ảnh hiệu lực (để chạy) + tự trỏ ô chọn ảnh sang đó.
    /// Quy tắc: (1) ImagePath là ảnh LOCAL RIÊNG hợp lệ (khác ảnh-Hub-đã-sync, file có thật) → GIỮ, không kéo Hub
    /// (tôn trọng nút "…"). (2) Ngược lại (trống / file không tồn tại / đang trỏ chính ảnh-Hub) → KÉO ảnh chung Hub
    /// về khu workbook (hash-skip nếu không đổi → tự cập nhật khi admin đổi ảnh) rồi trỏ ImagePath sang đó (hiện
    /// trong ô + tự lưu). Kéo lỗi/offline → trả ImagePath hiện tại (guard chặn nếu thiếu). Best-effort, không ném.</summary>
    private async Task<string> EnsureUpdateImageAsync(CancellationToken ct)
    {
        var synced = SyncedUpdateImagePath;
        var isCustomLocal = !string.IsNullOrWhiteSpace(ImagePath)
            && !string.Equals(ImagePath, synced, StringComparison.OrdinalIgnoreCase)
            && File.Exists(ImagePath);
        if (isCustomLocal) return ImagePath;   // ảnh local riêng của user → giữ nguyên

        try
        {
            var sync = CoordinationRuntime.ConfigSync;
            if (sync is not null)
            {
                var got = await sync.PullSharedAssetAsync(HubConfigSync.DefaultUpdateImageRemote, synced, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(got) && File.Exists(got))
                {
                    if (!string.Equals(ImagePath, got, StringComparison.OrdinalIgnoreCase))
                        UiThread.Post(() => ImagePath = got);   // hiện trong ô chọn ảnh + OnImagePathChanged tự lưu
                    return got;
                }
            }
        }
        catch { }
        return ImagePath;   // không kéo được → giữ (guard sẽ chặn nếu file thiếu)
    }

    private UpdateProductContext BuildContext(UpdateRunTargetViewModel t, AiConfig ai, int? startRow = null, int? endRow = null,
        bool? importFromClaimedTab = null, string? imageOverride = null)
    {
        var a = t.Account; var s = t.SelectedShop!;
        // AI (viết lại tên/mô tả) dùng cấu hình AI CHUNG ở Cài đặt; key truyền THẲNG qua context
        // (không set biến môi trường process-wide để Brave/Playwright không kế thừa key).
        var aiModel = string.IsNullOrWhiteSpace(ai.OpenAiModel) ? s.OpenAiModel : ai.OpenAiModel;
        // Khoảng dòng: ưu tiên override (Hub giao việc, >0) → KHÔNG ghi đè cấu hình shop; 0/null = dùng cấu hình.
        var sr = startRow is int x && x > 0 ? x : t.StartRow;
        var er = endRow is int y && y > 0 ? y : t.EndRow;
        // "Import từ tab Đã nhận": ưu tiên cờ Hub ghim vào việc (checkbox tab Giao việc); null = dùng cấu hình shop.
        var fromClaimedTab = importFromClaimedTab ?? s.BigSellerImportFromClaimedTab;
        return new UpdateProductContext(
            a.Id, a.Email, a.WorkbookPath, a.CookieFile,
            s.Id, s.DisplayName, s.ShopeeDataSheet,
            aiModel, "", ai.BatchSize, "",
            sr, er, imageOverride ?? ImagePath, VideoFolder,
            s.BigSellerCrawlUrl, fromClaimedTab,
            1, t.UpdateWorkers, t.ListingReloadSeconds, ai.OpenAiApiKey,   // Import LUÔN 1 lane (1 process)
            s.ColumnMap.LinkColumn, s.ColumnMap.PriceColumn, s.ColumnMap.SkuColumn,
            s.ColumnMap.ItemIdColumn, s.ColumnMap.ProductNameColumn, s.ColumnMap.RewrittenNameColumn,
            a.Password);
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

}
