using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Shopee.Core.Ai;
using Shopee.Core.Browser;
using Shopee.Core.Infrastructure;

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

    // ── Hiệu năng (trần cửa sổ Brave) ───────────────────────────────────────────
    /// <summary>Trần cửa sổ do người dùng đặt. 0 = tự động (min CPU/2, RAM/2).</summary>
    [ObservableProperty] private int _maxWindows;

    /// <summary>Thông tin máy: "CPU: 12 nhân · RAM: 32 GB".</summary>
    public string MachineInfo => $"CPU: {BraveFleet.CpuCores} nhân   ·   RAM: {BraveFleet.TotalRamGb} GB";

    /// <summary>Diễn giải trần tự động: "Tự động = min(CPU/2 = 6, RAM/2 = 16) = 6 cửa sổ".</summary>
    public string AutoWindowsInfo =>
        $"Tự động = min(CPU/2 = {System.Math.Max(2, BraveFleet.CpuCores / 2)}, RAM/2 = {BraveFleet.TotalRamGb / 2}) = {BraveFleet.AutoMaxWindows} cửa sổ";

    public SettingsViewModel() => LoadFromStore();

    private void LoadFromStore()
    {
        MaxWindows = PerformanceSettingsStore.Shared.Current.MaxConcurrentWindows;
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
        var v = System.Math.Max(0, MaxWindows);
        PerformanceSettingsStore.Shared.Save(new PerformanceSettings { MaxConcurrentWindows = v });
        // Áp dụng ngay vào RAM (có hiệu lực đầy đủ từ lượt chạy kế tiếp).
        BraveFleet.MaxConcurrentWindows = v > 0 ? v : BraveFleet.AutoMaxWindows;
        Status = v > 0
            ? $"Đã đặt trần {v} cửa sổ Brave (áp dụng từ lượt chạy kế tiếp)."
            : $"Đã đặt TỰ ĐỘNG — {BraveFleet.AutoMaxWindows} cửa sổ cho máy này.";
    }

    [RelayCommand] private void ResetNamePrompt() => NameRewritePrompt = AiPrompts.DefaultNameRewrite;
    [RelayCommand] private void ResetDescriptionPrompt() => DescriptionPrompt = AiPrompts.DefaultDescription;

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
            Status = $"✓ Khôi phục: BigSeller +{r.BigSellerAdded}/bỏ {r.BigSellerSkipped} · Shopee +{r.ShopeeAdded}/bỏ {r.ShopeeSkipped} · cookie {r.CookiesCopied} · AI {(r.AiImported ? "có" : "—")}.";
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
