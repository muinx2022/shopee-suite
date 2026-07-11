using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Suite.Services;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Lớp cơ sở dùng chung cho các ViewModel module (Scrape/Search/Update/CheckAccount/BigSeller): gom phần
/// boilerplate lặp lại ở cả 5 VM —
///  • <see cref="LogLines"/> (<see cref="LogBuffer"/>: giữ N dòng cuối trên UI khỏi đơ + ghi ĐẦY ĐỦ ra file),
///  • <see cref="Status"/> (ObservableProperty),
///  • <see cref="Log"/> / <see cref="OnUi"/> (ghi log + marshal sang UI thread),
///  • <see cref="Warn"/> (đặt Status + hộp thoại; bản <c>silent</c> chỉ ghi log, tránh mở modal treo UI trên
///    đường push-dispatch — hợp nhất bản có-silent của Scrape/Update và bản không của Search qua tham số),
///  • lệnh "Mở log" (<c>OpenLogFileCommand</c>).
///
/// KHÔNG gom: <c>IsBusy</c>/<c>IsRunning</c> (mỗi VM có [NotifyCanExecuteChangedFor] khác nhau) và
/// <c>BeginRun/EndRun</c>. CheckAccount giữ <c>SetStatus</c> riêng (marshal sang UI thread vì set Status từ
/// luồng worker).
/// </summary>
public abstract partial class ModuleViewModelBase : ObservableObject
{
    /// <summary>Nhật ký module: giữ 500 dòng cuối trên UI (khỏi đơ) + ghi ĐẦY ĐỦ ra logs\{fileName}.</summary>
    public LogBuffer LogLines { get; }

    /// <summary>Kho log RIÊNG theo tk BigSeller (tạo-khi-cần) — tab log per-acc đợt sau bind vào đây; buffer bền
    /// qua rebuild VM per-acc. <see cref="LogLines"/> vẫn là buffer GỘP toàn module như cũ.</summary>
    public AccountLogRegistry AccountLogs { get; }

    private readonly string _dialogTitle;

    [ObservableProperty] private string _status = "Sẵn sàng.";

    protected ModuleViewModelBase(string logFileName, string dialogTitle)
    {
        LogLines = new LogBuffer(logFileName);
        AccountLogs = new AccountLogRegistry(Path.GetFileNameWithoutExtension(logFileName));
        _dialogTitle = dialogTitle;
    }

    /// <summary>Ghi 1 dòng log (marshal sang UI thread — an toàn gọi từ luồng nền).</summary>
    protected void Log(string text) => OnUi(() => LogLines.Add(text));

    /// <summary>Ghi 1 dòng log vào CẢ buffer module (file gộp như cũ) LẪN buffer riêng của acc (UI đợt sau bind vào).</summary>
    protected void LogAcc(string accountId, string displayName, string text)
        => OnUi(() => { LogLines.Add(text); AccountLogs.Get(accountId, displayName).Add(text); });

    /// <summary>Chạy NGAY nếu đang ở UI thread, ngược lại xếp hàng sang UI thread.</summary>
    protected static void OnUi(Action action) => UiThread.Post(action);

    /// <summary>Cảnh báo: đặt <see cref="Status"/> rồi (silent → CHỈ ghi log, KHÔNG mở modal để tránh treo UI
    /// trên đường push-dispatch; ngược lại mở hộp thoại). Đường không-silent (Search) chỉ việc gọi Warn(msg).</summary>
    protected void Warn(string msg, bool silent = false)
    {
        Status = msg;
        if (silent) Log("⚠ " + msg);
        else Dialogs.Notify(msg, _dialogTitle);
    }

    /// <summary>Mở file log ĐẦY ĐỦ (UI chỉ giữ 500 dòng cuối).</summary>
    [RelayCommand]
    private void OpenLogFile() => ShellOpener.RevealFile(LogLines.FilePath);
}
