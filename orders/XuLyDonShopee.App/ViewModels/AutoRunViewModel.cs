using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Màn "Chạy tự động": cấu hình lô (N tài khoản/lô, M phút nghỉ, bật/tắt Sync & Xử lý đơn) + nút Bắt đầu/Dừng
/// + vùng trạng thái tiến trình. Logic vòng chạy nằm ở <see cref="AutoRunService"/> (bền qua điều hướng, đăng
/// ký một lần trong <see cref="AppServices"/>); VM chỉ bind cấu hình và điều khiển.
/// </summary>
public partial class AutoRunViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly AutoRunService _auto;

    public AutoRunViewModel(AppServices services)
    {
        _services = services;
        _auto = services.AutoRun;

        // Nghe scheduler (có thể phát từ thread nền) → marshal về UI thread rồi cập nhật nút/trạng thái.
        _auto.Changed += OnAutoChanged;

        Reload();
    }

    // ===== Cấu hình (đổ từ DB, người dùng chỉnh) =====
    [ObservableProperty]
    private int _batchSize = AutoRunSettings.DefaultBatchSize;

    [ObservableProperty]
    private int _gapMinutes = AutoRunSettings.DefaultGapMinutes;

    [ObservableProperty]
    private bool _doSync;

    [ObservableProperty]
    private bool _doProcess;

    /// <summary>Thông báo sau khi lưu cấu hình (null = ẩn).</summary>
    [ObservableProperty]
    private string? _savedMessage;

    /// <summary>Vòng chạy tự động đang hoạt động (đọc từ service).</summary>
    public bool IsRunning => _auto.IsRunning;

    /// <summary>Đang chạy → khóa ô cấu hình (tránh đổi giữa chừng).</summary>
    public bool CanEditConfig => !_auto.IsRunning;

    /// <summary>Nhãn nút toggle theo trạng thái.</summary>
    public string ToggleButtonText => _auto.IsRunning ? "Dừng chạy tự động" : "Bắt đầu chạy tự động";

    /// <summary>Dòng trạng thái hiển thị (phase của service hoặc mặc định).</summary>
    public string StatusText => _auto.CurrentPhase ?? (_auto.IsRunning ? "Đang chạy..." : "Chưa chạy.");

    /// <summary>TOÀN BỘ nhật ký hoạt động (mọi shop + dòng cấp batch "Chạy tự động") — KHÔNG lọc theo tài khoản
    /// vì autorun chạy nhiều shop; hiển thị hết để theo dõi tiến trình. Bind trực tiếp collection CHUNG của
    /// <see cref="ActivityLog"/>; code-behind cuộn xuống cuối khi có dòng mới.</summary>
    public ObservableCollection<LogEntry> LogEntries => _services.Log.Entries;

    /// <summary>Đường dẫn file log hôm nay (hiển thị mờ dưới panel để biết log được ghi ở đâu).</summary>
    public string LogPath => _services.Log.CurrentLogPath;

    /// <summary>MainViewModel gọi khi chuyển sang màn này: nạp lại cấu hình từ DB + đồng bộ trạng thái runtime.</summary>
    public void Reload()
    {
        var cfg = _services.Settings.GetAutoRunSettings();
        BatchSize = cfg.BatchSize;
        GapMinutes = cfg.GapMinutes;
        DoSync = cfg.DoSync;
        DoProcess = cfg.DoProcess;
        SavedMessage = null;
        NotifyRuntime();
    }

    /// <summary>Nút "Lưu cấu hình": chuẩn hóa + ghi DB + phản ánh giá trị đã chuẩn hóa lên form.</summary>
    [RelayCommand]
    private void SaveConfig()
    {
        PersistConfig();
        SavedMessage = "Đã lưu cấu hình.";
    }

    /// <summary>Nút "Xóa" trên panel nhật ký: xóa TOÀN BỘ dòng đang hiển thị (KHÔNG xóa file log trên đĩa).</summary>
    [RelayCommand]
    private void ClearLog() => _services.Log.Clear();

    /// <summary>Nút Bắt đầu/Dừng: đang chạy → Dừng (chờ thoát sạch); chưa chạy → lưu cấu hình rồi Bắt đầu.</summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_auto.IsRunning)
        {
            await _auto.StopAsync();
        }
        else
        {
            // Lưu cấu hình hiện tại trước khi chạy (vòng lặp đọc DB ở đầu mỗi lượt → dùng đúng giá trị mới).
            PersistConfig();
            SavedMessage = null;
            _auto.Start();
        }

        NotifyRuntime();
    }

    /// <summary>Chuẩn hóa cấu hình đang nhập, ghi DB, phản ánh lại lên form. Trả về bản đã chuẩn hóa.</summary>
    private AutoRunSettings PersistConfig()
    {
        var cfg = AutoRunSettings.Normalize(BatchSize, GapMinutes, DoSync, DoProcess);
        _services.Settings.SetAutoRunSettings(cfg);
        // Phản ánh bản đã chuẩn hóa (vd người dùng nhập 0 → 1).
        BatchSize = cfg.BatchSize;
        GapMinutes = cfg.GapMinutes;
        return cfg;
    }

    /// <summary>Scheduler đổi trạng thái (có thể từ thread nền) → marshal về UI thread rồi cập nhật binding.</summary>
    private void OnAutoChanged() => RunOnUi(NotifyRuntime);

    /// <summary>Phát property-changed cho các thuộc tính đọc từ service (nút/trạng thái/khóa cấu hình).</summary>
    private void NotifyRuntime()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanEditConfig));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Chạy <paramref name="action"/> trên UI thread (chạy ngay nếu đã ở UI thread).</summary>
    private static void RunOnUi(Action action)
    {
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess())
        {
            action();
        }
        else
        {
            ui.Post(action);
        }
    }
}
