using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.Infrastructure;
using Shopee.Core.Proxy;
using Shopee.Modules.CheckAccount;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.CheckAccount;

/// <summary>
/// ViewModel cho module "Check tài khoản". Port từ MainForm WinForms cũ: cùng logic luồng/queue,
/// xoay proxy, giữ profile khi thành công. Tài khoản OK được lưu thẳng vào KHO CHUNG
/// (Tài khoản & Proxy) — không còn copy sang 2 app v31/shopee-stat như trước.
/// </summary>
public sealed partial class CheckAccountViewModel : ObservableObject
{
    private static readonly string DataDir = SuitePaths.ModuleDir("check-account");
    private static readonly string OkFilePath = Path.Combine(DataDir, "tk-ok.txt");
    private static readonly string SettingsPath = Path.Combine(DataDir, "check-settings.json");
    // Profile bền theo tài khoản — layout (<dir>/Default) nên copy thẳng sang kho chung được.
    private static readonly string ProfilesRoot = Path.Combine(DataDir, "profiles");
    // Profile dùng chung của cả suite (account trong kho chung trỏ vào đây).
    private static readonly string SharedProfilesDir = Path.Combine(SuitePaths.ModuleDir("shared"), "profiles");

    // Trạng thái proxy theo từng kiotproxy key: IP hiện tại + mốc (epoch ms) được đổi tiếp.
    private readonly Dictionary<string, (string? ip, long next)> _proxyState = new(StringComparer.Ordinal);

    private ProxyPool? _pool;
    private CancellationTokenSource? _cts;

    public LogBuffer LogLines { get; } = new("check-account.log");
    public ObservableCollection<OkAccountRow> OkAccounts { get; } = [];

    [ObservableProperty] private string _accounts = "";
    [ObservableProperty] private string _proxyList = "";

    [ObservableProperty] private int _lanes = 2;

    [ObservableProperty] private string _status = "Sẵn sàng.";
    [ObservableProperty] private string _okStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty] private bool _selectAll;

    public CheckAccountViewModel() => LoadSettings();

    // ── Run ────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RunAsync()
    {
        var lines = SplitLines(Accounts);
        if (lines.Count == 0)
        {
            await Dialogs.InfoAsync("Chưa có tài khoản nào để check.", "Check tài khoản");
            return;
        }

        var proxies = SplitLines(ProxyList);
        SaveSettings();

        _pool = proxies.Count > 0 ? new ProxyPool(proxies, _proxyState) : null;
        if (_pool is not null) _pool.Log += AppendLog;

        IsRunning = true;
        _cts = new CancellationTokenSource();
        LogLines.Clear();
        AppendLog(_pool is null
            ? "(không có proxy — chạy bằng IP máy)"
            : $"({proxies.Count} proxy — mỗi tk 1 IP mới, key chưa tới giờ đổi thì chuyển key khác)");

        var laneCount = Math.Max(1, Math.Min(Lanes, lines.Count));
        var total = lines.Count;
        var queue = new ConcurrentQueue<string>(lines);

        var remaining = new List<string>(lines);
        var remainingLock = new object();
        var statsLock = new object();
        var fileLock = new object();
        var okCount = 0;
        var failCount = 0;
        var manualCount = 0;
        var processed = 0;

        void ResolveRemaining(string line)
        {
            string[] snapshot;
            lock (remainingLock) { remaining.Remove(line); snapshot = remaining.ToArray(); }
            OnUi(() => Accounts = string.Join(Environment.NewLine, snapshot));
        }

        AppendLog(laneCount > 1 ? $"▶ Chạy {laneCount} luồng song song." : "▶ Chạy 1 luồng.");

        async Task WorkerAsync(int laneId)
        {
            // Mỗi luồng có checker riêng (state chuột/_rng theo từng phiên, không chia sẻ giữa thread).
            var checker = new ShopeeAccountChecker();
            void LaneLog(string m) => AppendLog($"[L{laneId}]{m}");
            checker.Log += LaneLog;
            try
            {
                while (!_cts!.IsCancellationRequested && queue.TryDequeue(out var line))
                {
                    var shortName = line.Split('|')[0];
                    var n = Interlocked.Increment(ref processed);
                    SetStatus($"Đang chạy {laneCount} luồng… ({n}/{total})");
                    LaneLog($" ({n}/{total}) {shortName}");

                    // Lấy 1 IP mới cho tài khoản này (xoay key khi key hiện tại chưa tới giờ đổi).
                    string? proxy = null;
                    if (_pool is not null)
                    {
                        proxy = await _pool.AcquireFreshAsync(_cts.Token);
                        if (proxy is null)
                        {
                            LaneLog("   ⚠ không lấy được proxy nào → giữ lại tk này");
                            lock (statsLock) manualCount++;
                            continue;
                        }
                    }

                    // Giữ mỗi tk thêm 25–30s bất kể thành công/thất bại để giả lập người dùng.
                    var hold = Random.Shared.Next(25_000, 30_001);

                    // Profile bền theo tài khoản; tạo mới sạch mỗi lần check để test đúng mật khẩu.
                    var profileDir = Path.Combine(ProfilesRoot, SafeProfileName(shortName));
                    TryDeleteDir(profileDir);

                    CheckResult result;
                    try
                    {
                        result = await checker.CheckAsync(line, proxy, profileDir, hold, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        result = new CheckResult(CheckOutcome.Error, ex.Message);
                    }

                    switch (result.Outcome)
                    {
                        case CheckOutcome.Success:
                            lock (statsLock) okCount++;
                            lock (fileLock) AppendSuccess(line);
                            ResolveRemaining(line);
                            LaneLog("   ✔ THÀNH CÔNG → đã lưu vào tk-ok.txt");
                            LaneLog("   📁 giữ profile: " + profileDir);
                            break;
                        case CheckOutcome.WrongPassword:
                            lock (statsLock) failCount++;
                            ResolveRemaining(line);
                            LaneLog("   ✘ SAI MẬT KHẨU → đã xoá khỏi danh sách");
                            break;
                        case CheckOutcome.NeedsManual:
                            lock (statsLock) manualCount++;
                            LaneLog("   ⚠ " + result.Message + " → giữ lại trong danh sách");
                            break;
                        default:
                            LaneLog("   ⚠ Lỗi: " + result.Message + " → giữ lại trong danh sách");
                            break;
                    }

                    // Chỉ giữ profile khi login thành công (để copy sang shopee-stat/v31); còn lại xoá.
                    if (result.Outcome != CheckOutcome.Success)
                        TryDeleteDir(profileDir);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LaneLog("   ⚠ luồng dừng do lỗi: " + ex.Message); }
            finally { checker.Log -= LaneLog; }
        }

        try
        {
            var workers = Enumerable.Range(1, laneCount).Select(WorkerAsync).ToArray();
            await Task.WhenAll(workers);
        }
        finally
        {
            if (_pool is not null)
            {
                _pool.Log -= AppendLog;
                UpdateProxyStateFromPool();
                SaveSettings();
                _pool = null;
            }
            IsRunning = false;
            var done = _cts!.IsCancellationRequested ? "Đã dừng." : "Hoàn tất.";
            SetStatus($"{done} OK={okCount}, sai mật khẩu={failCount}, cần tay={manualCount}.");
            AppendLog($"── {done} OK={okCount}, sai mật khẩu={failCount}, cần xử lý tay={manualCount} ──");
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    private void UpdateProxyStateFromPool()
    {
        if (_pool is null) return;
        foreach (var e in _pool.Entries)
            if (e.IsKey)
                _proxyState[e.Raw] = (e.CurrentIp, e.NextChangeAtMs);
    }

    [RelayCommand]
    private void OpenProfiles()
    {
        try
        {
            Directory.CreateDirectory(ProfilesRoot);
            ShellOpener.OpenFolder(ProfilesRoot);
        }
        catch (Exception ex)
        {
            Dialogs.Notify(ex.Message, "Mở thư mục profile", DialogIcon.Error);
        }
    }

    // ── TK OK ────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ReloadOk() => LoadOkGrid();

    public void LoadOkGrid()
    {
        OkAccounts.Clear();
        SelectAll = false;
        List<string> lines;
        try
        {
            lines = File.Exists(OkFilePath)
                ? File.ReadAllLines(OkFilePath, Encoding.UTF8)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList()
                : [];
        }
        catch (Exception ex)
        {
            Dialogs.Notify("Lỗi đọc tk-ok.txt: " + ex.Message, "TK OK", DialogIcon.Error);
            return;
        }

        foreach (var line in lines)
            OkAccounts.Add(new OkAccountRow(line));
        SelectAll = false;
    }

    [RelayCommand]
    private void OpenOkFile()
    {
        try
        {
            if (!File.Exists(OkFilePath)) File.WriteAllText(OkFilePath, "", Encoding.UTF8);
            ShellOpener.RevealFile(OkFilePath);
        }
        catch (Exception ex)
        {
            Dialogs.Notify(ex.Message, "Mở file", DialogIcon.Error);
        }
    }

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var row in OkAccounts) row.Selected = value;
    }

    /// <summary>Lưu các tk OK đã chọn vào KHO CHUNG (Tài khoản & Proxy): tạo/cập nhật ShopeeAccount
    /// và copy profile sang thư mục profile dùng chung. Lưu xong thì XÓA khỏi danh sách TK OK +
    /// file tk-ok.txt. Scrape/Search dùng lại từ kho chung.</summary>
    [RelayCommand]
    private async Task SaveToShared()
    {
        var selectedRows = OkAccounts.Where(r => r.Selected && r.Line.Length > 0).ToList();
        if (selectedRows.Count == 0)
        {
            await Dialogs.InfoAsync("Tích chọn ít nhất 1 tài khoản để lưu.", "Lưu vào kho chung");
            return;
        }

        var savedRows = new List<OkAccountRow>();
        var added = 0; var updated = 0; var missing = 0; var failed = 0;
        for (var i = 0; i < selectedRows.Count; i++)
        {
            var row = selectedRows[i];
            var line = row.Line;
            var username = line.Split('|')[0];
            OkStatus = $"Đang lưu {i + 1}/{selectedRows.Count}: {username}…";

            try
            {
                var existing = AccountStore.Shared.Accounts
                    .FirstOrDefault(a => string.Equals(a.ShopeeAccountLogin, line, StringComparison.Ordinal));
                var acc = existing ?? new ShopeeAccount { ShopeeAccountLogin = line, Label = username };
                acc.EnsureProfilePath();

                // Copy profile đã đăng nhập (nếu có) sang thư mục dùng chung theo Id account.
                var src = Path.Combine(ProfilesRoot, SafeProfileName(username));
                if (Directory.Exists(Path.Combine(src, "Default")))
                {
                    var dest = Path.Combine(SharedProfilesDir, acc.Id);
                    await Task.Run(() => CopyProfile(src, dest));
                    acc.ProfileRelativePath = dest;
                }
                else
                {
                    // Profile chưa đăng nhập → KHÔNG đăng ký vào kho chung và KHÔNG xóa khỏi TK OK,
                    // để người dùng chạy check lại (giống app gốc CopySelectedAsync).
                    missing++;
                    SetRowStatus(line, "⚠ chưa có profile (chạy check lại)");
                    continue;
                }

                if (existing is null)
                {
                    if (AccountStore.Shared.Add(acc)) added++;
                    else throw new IOException("Không lưu được tài khoản vào kho chung.");
                }
                else
                {
                    if (AccountStore.Shared.Save()) updated++;
                    else throw new IOException("Không cập nhật được tài khoản trong kho chung.");
                }

                // Đã copy cookie sang kho chung + lưu tk thành công → XOÁ bản nguồn check-account\profiles\{username}
                // (không còn cần). Trước đây giữ lại → mỗi tk tồn 2 bản profile song song, phình ~11 GB vô ích.
                TryDeleteDir(src);

                savedRows.Add(row);
            }
            catch (Exception ex)
            {
                failed++;
                SetRowStatus(line, "✘ lỗi: " + ex.Message);
            }
        }

        // Đã lưu xong → bỏ khỏi danh sách TK OK + ghi lại file tk-ok.txt (chỉ giữ tk chưa lưu).
        foreach (var row in savedRows) OkAccounts.Remove(row);
        RewriteOkFile();
        SelectAll = false;

        OkStatus = $"Đã lưu {savedRows.Count} tk vào kho chung (thiếu profile {missing}, lỗi {failed}). Còn {OkAccounts.Count} tk.";
        await Dialogs.InfoAsync(
            $"Đã lưu {savedRows.Count} tài khoản vào kho chung và xóa khỏi TK OK.\n" +
            $"Thêm mới: {added} · Cập nhật: {updated}\nThiếu profile: {missing} · Lỗi: {failed}",
            "Lưu vào kho chung");
    }

    /// <summary>Ghi lại tk-ok.txt từ các dòng còn trong lưới (sau khi đã bỏ tk vừa lưu).</summary>
    private void RewriteOkFile()
    {
        try
        {
            var remaining = OkAccounts.Select(r => r.Line).Where(l => l.Length > 0).Distinct();
            File.WriteAllLines(OkFilePath, remaining, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppendLog("  (không ghi lại được tk-ok.txt: " + ex.Message + ")");
        }
    }

    private void SetRowStatus(string line, string status)
    {
        var row = OkAccounts.FirstOrDefault(r => r.Line == line);
        if (row is not null) row.Status = status;
    }

    /// <summary>
    /// Copy sang kho chung CHỈ các file cần để đọc/inject cookie session Shopee: <c>Local State</c> (chứa key
    /// giải mã) + <c>Default\Network\Cookies</c> (+ journal, Preferences). Scrape KHÔNG BAO GIỜ mở profile này
    /// bằng Brave — chỉ đọc 2 file trên (xem ChromiumCookieReader) — nên copy nguyên user-data-dir như trước
    /// (kể cả Cache/Service Worker/Safe Browsing ~78 MB/tk) là lãng phí đĩa. Ghi đè đích nếu đã có.
    /// </summary>
    private static void CopyProfile(string src, string dest)
    {
        var relFiles = new[]
        {
            "Local State",
            Path.Combine("Default", "Network", "Cookies"),
            Path.Combine("Default", "Network", "Cookies-journal"),
            Path.Combine("Default", "Preferences"),
        };

        // Chặn ghi đè dest (có thể đang là bản login TỐT từ lần lưu trước) bằng bản nguồn THIẾU cookie: nếu src
        // không đủ Local State + Default\Network\Cookies thì ném để caller đếm lỗi + giữ nguyên dest cũ.
        if (!File.Exists(Path.Combine(src, "Local State")) ||
            !File.Exists(Path.Combine(src, "Default", "Network", "Cookies")))
            throw new FileNotFoundException("Profile nguồn thiếu Local State/Cookies — không ghi đè kho chung.");

        // Copy vào thư mục TẠM rồi Move đè NGUYÊN TỬ. Nếu copy hỏng giữa chừng (đĩa đầy/IO lỗi khi re-save),
        // dest CŨ còn nguyên → không mất profile login đang tốt. Move cùng ổ gần như nguyên tử.
        var tmp = dest + ".new-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            Directory.CreateDirectory(tmp);
            foreach (var rel in relFiles)
            {
                var s = Path.Combine(src, rel);
                if (!File.Exists(s)) continue;
                var targetPath = Path.Combine(tmp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(s, targetPath, overwrite: true);
            }
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(tmp, dest);
        }
        catch
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
            throw;
        }
    }

    // ── File OK ──────────────────────────────────────────────────────────────────

    private void AppendSuccess(string line)
    {
        try { File.AppendAllText(OkFilePath, line + Environment.NewLine, Encoding.UTF8); }
        catch (Exception ex) { AppendLog("  (không ghi được tk-ok.txt: " + ex.Message + ")"); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private static List<string> SplitLines(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>Tên thư mục profile an toàn từ username (giữ chữ/số/.-_@, còn lại thay '_').</summary>
    private static string SafeProfileName(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "acc-" + Guid.NewGuid().ToString("N")[..8];
        var sb = new StringBuilder(username.Length);
        foreach (var ch in username.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '@' ? ch : '_');
        var name = sb.ToString().Trim('_', '.');
        return name.Length == 0 ? "acc-" + Guid.NewGuid().ToString("N")[..8] : name;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private void SetStatus(string text) => OnUi(() => Status = text);

    private void AppendLog(string text) => UiThread.Post(() => LogLines.Add(text));

    private static void OnUi(Action action) => UiThread.Post(action);

    // ── Settings (nhớ proxy key + danh sách) ─────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.TryGetProperty("proxyList", out var pl)) ProxyList = pl.GetString() ?? "";
            else if (root.TryGetProperty("proxyKey", out var pk)) ProxyList = pk.GetString() ?? ""; // bản cũ
            if (root.TryGetProperty("accounts", out var acc)) Accounts = acc.GetString() ?? "";
            if (root.TryGetProperty("lanes", out var ln) && ln.TryGetInt32(out var lanes))
                Lanes = Math.Max(1, Math.Min(5, lanes));

            _proxyState.Clear();
            if (root.TryGetProperty("proxyState", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ps.EnumerateArray())
                {
                    var raw = item.TryGetProperty("raw", out var r) ? r.GetString() : null;
                    if (string.IsNullOrEmpty(raw)) continue;
                    var ip = item.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() : null;
                    var next = item.TryGetProperty("next", out var nEl) && nEl.TryGetInt64(out var nn) ? nn : 0;
                    _proxyState[raw] = (ip, next);
                }
            }

        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                proxyList = ProxyList,
                accounts = Accounts,
                lanes = Lanes,
                proxyState = _proxyState.Select(kv => new { raw = kv.Key, ip = kv.Value.ip, next = kv.Value.next }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json, Encoding.UTF8);
        }
        catch { }
    }
}
