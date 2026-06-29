using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Shopee.Core.Ai;
using Shopee.Core.Browser;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Hub;

namespace Shopee.Suite.Modules.Settings;

/// <summary>
/// Mục "Cài đặt" — cấu hình AI dùng chung (provider + model + API key) cho viết lại tên/mô tả SP và
/// cập nhật danh mục. Lưu vào <see cref="AiConfigStore"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public string[] ProviderOptions { get; } = ["OpenAI", "Anthropic", "Gemini"];

    [ObservableProperty] private string _provider = "OpenAI";
    [ObservableProperty] private string _openAiModel = "";
    [ObservableProperty] private string _openAiApiKey = "";
    [ObservableProperty] private string _anthropicModel = "";
    [ObservableProperty] private string _anthropicApiKey = "";
    [ObservableProperty] private string _geminiModel = "";
    [ObservableProperty] private string _geminiApiKey = "";
    [ObservableProperty] private int _batchSize = 40;
    [ObservableProperty] private string _status = "";

    // System prompt cho 2 tác vụ AI (Update Product). Rỗng = dùng mặc định; ở đây prefill bản đang dùng.
    [ObservableProperty] private string _nameRewritePrompt = "";
    [ObservableProperty] private string _descriptionPrompt = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _testing;

    public bool IsIdle => !Testing;

    // ── Hiệu năng (ngân sách CPU/RAM → trần cửa sổ Brave) ────────────────────────
    /// <summary>Số nhân CPU cho phép app dùng (mỗi cửa sổ Brave ~1 nhân).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComputedMaxInfo))]
    private int _usableCpu;

    /// <summary>RAM (GB) cho phép app dùng (mỗi cửa sổ Brave ~2GB).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComputedMaxInfo))]
    private int _usableRamGb;

    /// <summary>Trần vật lý để hiển thị gợi ý "tối đa N".</summary>
    public int CpuCoresMax => BraveFleet.CpuCores;
    public int RamGbMax => BraveFleet.TotalRamGb;

    /// <summary>Thông tin máy: "Máy: CPU 12 nhân · RAM 32 GB".</summary>
    public string MachineInfo => $"Máy: CPU {BraveFleet.CpuCores} nhân   ·   RAM {BraveFleet.TotalRamGb} GB";

    /// <summary>Trần cửa sổ TÍNH live từ ngân sách đang nhập.</summary>
    public string ComputedMaxInfo =>
        $"→ Tối đa {BraveFleet.WindowsForBudget(UsableCpu, UsableRamGb)} cửa sổ Brave   (= min(CPU dùng, RAM dùng ÷ 2))";

    // ── Đồng bộ nhiều máy (kết nối tới Hub) ─────────────────────────────────────
    /// <summary>Nhãn máy này: "TÊN-MÁY (a1b2c3)" — để phân biệt trên bảng trạng thái.</summary>
    public string MachineLabel => MachineIdentity.Shared.Label;

    /// <summary>Tên hiển thị máy này (đặt tuỳ ý; mặc định = tên máy). Dùng để báo "Scraping by …".</summary>
    [ObservableProperty] private string _machineDisplayName = "";

    [ObservableProperty] private bool _hubEnabled;
    [ObservableProperty] private string _hubBaseUrl = "";
    [ObservableProperty] private string _hubApiToken = "";
    /// <summary>Trạng thái kết nối client→Hub (hiện ngay trên panel client để biết nối được chưa).</summary>
    [ObservableProperty] private string _hubClientStatus = "";

    // ── Chế độ Hub (máy này LÀM server) ─────────────────────────────────────────
    [ObservableProperty] private bool _isHubMode;
    [ObservableProperty] private int _hubPort = 8088;
    [ObservableProperty] private string _hubDomain = "";
    [ObservableProperty] private string _hubTunnelToken = "";
    [ObservableProperty] private string _hubServerApiToken = "";
    [ObservableProperty] private string _hubServerStatus = "";

    /// <summary>Hub trên máy này đang chạy? — để bật/tắt nút "Bật Hub" / "Dừng Hub" cho rõ trạng thái.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartHubCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopHubCommand))]
    private bool _isHubRunning;

    // ── Bộ chọn vai trò máy (mặc định KHÔNG chọn gì → không hiện panel nào) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoleHint))]
    private bool _isClientRole;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoleHint))]
    private bool _isHubRole;

    /// <summary>true khi chưa chọn vai trò nào → hiện dòng gợi ý.</summary>
    public bool ShowRoleHint => !IsClientRole && !IsHubRole;

    /// <summary>true trong lúc nạp từ store → chặn ghi-lại vai trò khi đang khôi phục (tránh vòng + ghi thừa).</summary>
    private bool _loadingRole;

    // Loại-trừ-nhau do VM kiểm soát (không dùng RadioButton GroupName — vì group bỏ-chọn bằng SetCurrentValue
    // KHÔNG đẩy false về binding hai chiều, khiến cả hai cờ cùng true). Setter này luôn giữ đúng tối đa 1 vai trò.
    partial void OnIsClientRoleChanged(bool value)
    {
        if (value)
        {
            IsHubRole = false;
            // Chọn CLIENT = muốn đồng bộ → bật sẵn ô "Bật đồng bộ" (tránh quên tick rồi tưởng không nối được).
            if (!_loadingRole && !HubEnabled) HubEnabled = true;
        }
        PersistRole();
    }

    partial void OnIsHubRoleChanged(bool value) { if (value) IsClientRole = false; PersistRole(); }

    /// <summary>Ghi vai trò đã chọn vào machine.json để mở lại app hiện đúng panel. Bỏ qua khi đang nạp.</summary>
    private void PersistRole()
    {
        if (_loadingRole) return;
        MachineIdentity.Shared.SetRole(IsHubRole ? "hub" : IsClientRole ? "client" : "");
    }

    public SettingsViewModel()
    {
        LoadFromStore();
        HubRuntime.Shared.StateChanged += OnHubStateChanged;
    }

    private void OnHubStateChanged()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) ApplyHubState();
        else d.BeginInvoke(ApplyHubState);
    }

    private void ApplyHubState()
    {
        IsHubRunning = HubRuntime.Shared.Running;
        HubServerStatus = IsHubRunning ? "🟢 Đang chạy" : "⚪ Đã dừng";
    }

    private void LoadFromStore()
    {
        MachineDisplayName = MachineIdentity.Shared.DisplayName;
        var hub = HubClientConfigStore.Shared.Current;
        HubEnabled = hub.Enabled;
        HubBaseUrl = hub.BaseUrl;
        HubApiToken = hub.ApiToken;

        var hubSrv = HubServerConfigStore.Shared.Current;
        IsHubMode = hubSrv.Enabled;
        HubPort = hubSrv.Port;
        HubDomain = hubSrv.Domain;
        HubTunnelToken = hubSrv.TunnelToken;
        HubServerApiToken = hubSrv.ApiToken;
        IsHubRunning = HubRuntime.Shared.Running;
        HubServerStatus = IsHubRunning ? "🟢 Đang chạy" : "⚪ Chưa chạy";

        // Khôi phục VAI TRÒ đã chọn để mở lại app hiện đúng panel. Ưu tiên giá trị đã lưu; nếu chưa từng
        // chọn (bản cũ) thì suy từ cấu hình đang bật để không vỡ máy đang dùng. _loadingRole chặn ghi-lại.
        _loadingRole = true;
        var role = MachineIdentity.Shared.Role;
        IsHubRole = role == "hub" || (role.Length == 0 && hubSrv.Enabled);
        IsClientRole = role == "client" || (role.Length == 0 && !hubSrv.Enabled && hub.Enabled);
        _loadingRole = false;
        HubClientStatus = CoordinationRuntime.Active && !hubSrv.Enabled ? "🔵 Đã bật đồng bộ Hub (bấm \"Kiểm tra\" để chắc nối được)." : "";

        var p = PerformanceSettingsStore.Shared.Current;
        UsableCpu = p.UsableCpuCores > 0 ? p.UsableCpuCores : System.Math.Max(2, BraveFleet.CpuCores / 2);
        UsableRamGb = p.UsableRamGb > 0 ? p.UsableRamGb : BraveFleet.TotalRamGb;
        var c = AiConfigStore.Shared.Current;
        Provider = c.Provider;
        OpenAiModel = c.OpenAiModel;
        OpenAiApiKey = c.OpenAiApiKey;
        AnthropicModel = c.AnthropicModel;
        AnthropicApiKey = c.AnthropicApiKey;
        GeminiModel = c.GeminiModel;
        GeminiApiKey = c.GeminiApiKey;
        BatchSize = c.BatchSize;
        // Prefill bằng prompt ĐANG DÙNG (mặc định nếu chưa đặt) để người dùng sửa trực tiếp.
        NameRewritePrompt = c.EffectiveNameRewritePrompt;
        DescriptionPrompt = c.EffectiveDescriptionPrompt;
    }

    private AiConfig Build() => new()
    {
        Provider = Provider,
        OpenAiModel = OpenAiModel.Trim(),
        OpenAiApiKey = OpenAiApiKey.Trim(),
        AnthropicModel = AnthropicModel.Trim(),
        AnthropicApiKey = AnthropicApiKey.Trim(),
        GeminiModel = GeminiModel.Trim(),
        GeminiApiKey = GeminiApiKey.Trim(),
        BatchSize = Math.Clamp(BatchSize <= 0 ? 40 : BatchSize, 1, 500),
        NameRewritePrompt = (NameRewritePrompt ?? "").Trim(),
        DescriptionPrompt = (DescriptionPrompt ?? "").Trim(),
    };

    [RelayCommand]
    private void Save()
    {
        AiConfigStore.Shared.Save(Build());
        Status = $"Đã lưu. Provider đang dùng: {Provider}.";
    }

    [RelayCommand]
    private void SavePerformance()
    {
        var cpu = System.Math.Clamp(UsableCpu, 1, BraveFleet.CpuCores);
        var ram = System.Math.Clamp(UsableRamGb, 1, BraveFleet.TotalRamGb);
        UsableCpu = cpu; UsableRamGb = ram;   // phản ánh kẹp về UI
        PerformanceSettingsStore.Shared.Save(new PerformanceSettings { UsableCpuCores = cpu, UsableRamGb = ram });
        var max = BraveFleet.WindowsForBudget(cpu, ram);
        BraveFleet.MaxConcurrentWindows = max;   // áp dụng ngay (đầy đủ từ lượt chạy kế tiếp)
        Status = $"Đã đặt: dùng {cpu} nhân + {ram}GB → tối đa {max} cửa sổ Brave (áp dụng từ lượt chạy kế tiếp).";
    }

    [RelayCommand] private void ResetNamePrompt() => NameRewritePrompt = AiPrompts.DefaultNameRewrite;
    [RelayCommand] private void ResetDescriptionPrompt() => DescriptionPrompt = AiPrompts.DefaultDescription;

    [RelayCommand]
    private void SaveMachineName()
    {
        MachineIdentity.Shared.SetDisplayName(MachineDisplayName);
        MachineDisplayName = MachineIdentity.Shared.DisplayName;   // trống → quay về tên máy
        OnPropertyChanged(nameof(MachineLabel));
        Status = $"Đã đặt tên máy: {MachineIdentity.Shared.DisplayName}.";
    }

    [RelayCommand]
    private void SaveHubClient()
    {
        var url = NormalizeHubUrl(HubBaseUrl);
        if (url is null)   // chỉ null khi nhập KHÁC rỗng nhưng không hợp lệ → tránh new Uri(...) ném lúc khởi động
        {
            Status = "✘ URL Hub không hợp lệ. Ví dụ: https://api.tencuaban.com";
            return;
        }
        HubBaseUrl = url;
        HubClientConfigStore.Shared.Save(new HubClientConfig
        {
            Enabled = HubEnabled,
            BaseUrl = url,
            ApiToken = (HubApiToken ?? "").Trim(),
        });
        Status = HubEnabled && !string.IsNullOrWhiteSpace(url)
            ? $"Đã lưu kết nối Hub: {url}. Bấm \"Kết nối ngay\" để áp dụng (hoặc khởi động lại app)."
            : "Đã tắt đồng bộ Hub — app chạy độc lập như cũ.";
    }

    /// <summary>Tạo client tạm từ URL/token đang nhập và ping /health để biết NGAY có nối được không.</summary>
    [RelayCommand]
    private async Task TestHubClient()
    {
        var url = NormalizeHubUrl(HubBaseUrl);
        if (string.IsNullOrWhiteSpace(url)) { HubClientStatus = "✘ Chưa nhập URL Hub."; return; }
        HubClientStatus = "⏳ Đang kiểm tra…";
        var cfg = new HubClientConfig { Enabled = true, BaseUrl = url, ApiToken = (HubApiToken ?? "").Trim() };
        try
        {
            var ok = await new HubClient(cfg, MachineIdentity.Shared.MachineId).PingAsync();
            HubClientStatus = ok
                ? "🟢 Kết nối OK — Hub có phản hồi."
                : "✘ Không nối được: sai URL/token hoặc máy-Hub chưa bật. Kiểm tra lại rồi thử tiếp.";
        }
        catch (Exception ex) { HubClientStatus = "✘ Lỗi: " + ex.Message; }
    }

    /// <summary>Lưu cấu hình client rồi áp dụng NGAY (không cần khởi động lại app) và ping kiểm chứng.</summary>
    [RelayCommand]
    private async Task ConnectNow()
    {
        SaveHubClient();
        if (!HubEnabled) { HubClientStatus = "Đồng bộ đang TẮT — tick \"Bật đồng bộ với Hub\" rồi thử lại."; return; }
        HubClientStatus = "⏳ Đang kết nối…";
        var active = CoordinationRuntime.Reconnect();
        if (!active) { HubClientStatus = "✘ Chưa kết nối — kiểm tra URL/token."; return; }
        var ok = CoordinationRuntime.Client is { } c && await c.PingAsync();
        HubClientStatus = ok
            ? "🟢 Đã kết nối Hub (áp dụng ngay, không cần khởi động lại)."
            : "🟡 Đã bật nhưng Hub chưa phản hồi — kiểm tra URL/token hoặc máy-Hub đã bật chưa.";
    }

    /// <summary>Chuẩn hoá URL Hub: tự thêm "https://" nếu thiếu scheme; "" nếu rỗng; null nếu KHÔNG hợp lệ (http/https tuyệt đối).</summary>
    private static string? NormalizeHubUrl(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (!s.Contains("://")) s = "https://" + s;
        s = s.TrimEnd('/');
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? s : null;
    }

    [RelayCommand]
    private void GenerateHubServerToken() =>
        HubServerApiToken = System.Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    [RelayCommand]
    private async Task PushConfigToHub()
    {
        var sync = CoordinationRuntime.ConfigSync;
        if (sync is null) { Status = "Chưa kết nối Hub (bật đồng bộ rồi khởi động lại app)."; return; }
        try { Status = await sync.PushAsync(); }
        catch (Exception ex) { Status = "✘ Lỗi đẩy cấu hình: " + ex.Message; }
    }

    private HubServerConfig BuildHubServerConfig(bool enabled) => new()
    {
        Enabled = enabled,
        Port = HubPort <= 0 ? 8088 : HubPort,
        Domain = (HubDomain ?? "").Trim(),
        TunnelToken = (HubTunnelToken ?? "").Trim(),
        ApiToken = (HubServerApiToken ?? "").Trim(),
    };

    [RelayCommand(CanExecute = nameof(CanStartHub))]
    private async Task StartHub()
    {
        if (string.IsNullOrWhiteSpace(HubServerApiToken)) GenerateHubServerToken();
        var cfg = BuildHubServerConfig(enabled: true);
        HubServerConfigStore.Shared.Save(cfg);
        HubServerStatus = "⏳ Đang khởi động…";
        try
        {
            await HubRuntime.Shared.StartAsync(cfg);
            IsHubMode = true;
            IsHubRunning = true;
            HubServerStatus = "🟢 Đang chạy" + (string.IsNullOrWhiteSpace(cfg.PublicUrl) ? "" : "  ·  " + cfg.PublicUrl);
            Status = "Hub đang chạy. Dán URL + API token này vào các máy client.";
        }
        catch (Exception ex)
        {
            HubServerStatus = "✘ Lỗi: " + ex.Message;
            Dialogs.Show(ex.Message, "Bật Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopHub))]
    private async Task StopHub()
    {
        try { await HubRuntime.Shared.StopAsync(); } catch { }
        HubServerConfigStore.Shared.Save(BuildHubServerConfig(enabled: false));
        IsHubMode = false;
        IsHubRunning = false;
        HubServerStatus = "⚪ Đã dừng";
        Status = "Đã dừng chế độ Hub trên máy này.";
    }

    private bool CanStartHub() => !IsHubRunning;
    private bool CanStopHub() => IsHubRunning;

    // ── Sao lưu / Khôi phục (đồng bộ sang máy khác) ─────────────────────────────
    [ObservableProperty] private bool _backupBigSeller = true;
    [ObservableProperty] private bool _backupShopee = true;
    [ObservableProperty] private bool _backupAi = true;
    /// <summary>false = GỘP (thêm mới, giữ cũ); true = THAY THẾ (ghi đè).</summary>
    [ObservableProperty] private bool _importReplace;
    [ObservableProperty] private string _rebaseWorkbookDir = "";

    private string SelectedParts()
    {
        var parts = new List<string>();
        if (BackupBigSeller) parts.Add("Tài khoản BigSeller (+ cookie)");
        if (BackupShopee) parts.Add("Tài khoản Shopee + proxy");
        if (BackupAi) parts.Add("Cấu hình AI (keys)");
        return parts.Count == 0 ? "(chưa chọn)" : string.Join(", ", parts);
    }

    [RelayCommand]
    private void ExportBackup()
    {
        if (!BackupBigSeller && !BackupShopee && !BackupAi) { Status = "Chọn ít nhất 1 mục để sao lưu."; return; }
        var dlg = new SaveFileDialog
        {
            Filter = "ShopeeSuite backup|*.zip",
            FileName = $"shopeesuite-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Title = "Lưu file sao lưu",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            BackupService.Export(dlg.FileName, new BackupOptions(BackupBigSeller, BackupShopee, BackupAi));
            Status = "✓ Đã sao lưu: " + dlg.FileName;
            Dialogs.Show($"Đã sao lưu xong:\n{dlg.FileName}\n\nGồm: {SelectedParts()}.\n⚠ File chứa cookie + API key — giữ bảo mật.",
                "Sao lưu", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status = "✘ Lỗi sao lưu: " + ex.Message;
            Dialogs.Show(ex.Message, "Sao lưu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void BrowseRebaseDir()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục chứa file data workbook trên máy này" };
        if (dlg.ShowDialog() == true) RebaseWorkbookDir = dlg.FolderName;
    }

    [RelayCommand]
    private void ImportBackup()
    {
        if (!BackupBigSeller && !BackupShopee && !BackupAi) { Status = "Chọn ít nhất 1 mục để khôi phục."; return; }
        var dlg = new OpenFileDialog { Filter = "ShopeeSuite backup|*.zip|Tất cả|*.*", Title = "Chọn file sao lưu (.zip)" };
        if (dlg.ShowDialog() != true) return;

        var mode = ImportReplace ? "THAY THẾ (xóa tk cũ rồi ghi đè)" : "GỘP (thêm mới, giữ tk cũ)";
        if (Dialogs.Show($"Khôi phục từ:\n{dlg.FileName}\n\nChế độ: {mode}\nMục: {SelectedParts()}\n\nTiếp tục?",
                "Khôi phục", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var r = BackupService.Import(dlg.FileName,
                new BackupOptions(BackupBigSeller, BackupShopee, BackupAi),
                ImportReplace,
                string.IsNullOrWhiteSpace(RebaseWorkbookDir) ? null : RebaseWorkbookDir);
            if (BackupAi && r.AiImported) LoadFromStore();   // làm mới ô AI trên màn hình
            Status = $"✓ Khôi phục: BigSeller +{r.BigSellerAdded}/↻{r.BigSellerUpdated}/bỏ {r.BigSellerSkipped} · Shopee +{r.ShopeeAdded}/bỏ {r.ShopeeSkipped} · cookie {r.CookiesCopied} · AI {(r.AiImported ? "có" : "—")}.";
            Dialogs.Show(Status, "Khôi phục", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status = "✘ Lỗi khôi phục: " + ex.Message;
            Dialogs.Show(ex.Message, "Khôi phục", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Test()
    {
        var cfg = Build();
        if (!cfg.HasActiveKey) { Status = $"Chưa nhập API key cho {Provider}."; return; }
        Testing = true;
        Status = $"Đang test {Provider} ({cfg.ActiveModel})…";
        try
        {
            var reply = await AiChat.CompleteAsync(cfg,
                "Bạn là trợ lý kiểm tra kết nối.", "Trả lời đúng một từ: OK", default, temperature: 0, maxTokens: 16);
            Status = $"✓ {Provider} OK — phản hồi: \"{reply.Trim()}\"";
        }
        catch (Exception ex)
        {
            Status = "✘ Lỗi: " + ex.Message;
            Dialogs.Show(ex.Message, "Test AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Testing = false; }
    }
}
