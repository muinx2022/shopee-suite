using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Màn "Đơn hàng". Ngoài binding MVVM, code-behind lo phần thao tác con trỏ trên <see cref="DataGrid"/>:
/// double-click MỘT ô → copy text ô vào clipboard + hiện toast nhỏ "Đã copy text …" ngay tại ô, tự ẩn ~1.2s.
/// </summary>
public partial class OrdersView : UserControl
{
    /// <summary>Thời gian toast "Đã copy" tự hiển thị trước khi ẩn.</summary>
    private static readonly TimeSpan ToastDuration = TimeSpan.FromMilliseconds(1200);

    /// <summary>Số ký tự tối đa của nội dung ô hiển thị trong toast (dài hơn thì cắt + "…").</summary>
    private const int ToastMaxChars = 60;

    /// <summary>Hẹn giờ ẩn toast; dùng lại 1 instance, reset mỗi lần double-click.</summary>
    private DispatcherTimer? _toastTimer;

    public OrdersView()
    {
        InitializeComponent();
        OrdersGrid.CellPointerPressed += OnCellPointerPressed;
    }

    /// <summary>
    /// Nhận double-click (ClickCount==2, chuột trái) trên một ô → nếu ô có text đáng copy thì copy + báo toast.
    /// Ô nút "Phiếu" / ô trống → <see cref="CellTextExtractor.ExtractCellText"/> trả null → im lặng.
    /// </summary>
    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        var pointer = e.PointerPressedEventArgs;
        if (pointer.ClickCount != 2)
        {
            return;
        }

        // Chỉ phản hồi double-click CHUỘT TRÁI (bỏ qua phải/giữa để không copy ngoài ý muốn).
        if (!pointer.GetCurrentPoint(e.Cell).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var text = CellTextExtractor.ExtractCellText(e.Cell);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CopyToClipboard(text);
        ShowCopyToast(e.Cell, text);
    }

    /// <summary>
    /// Copy <paramref name="text"/> vào clipboard qua <see cref="TopLevel.Clipboard"/> (có thể null nếu view
    /// chưa gắn cây). Chạy nền, nuốt lỗi để clipboard bận/không khả dụng không làm sập UI.
    /// </summary>
    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        _ = SetClipboardTextAsync(clipboard, text);
    }

    private static async Task SetClipboardTextAsync(IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch
        {
            // Clipboard đang bị tiến trình khác giữ / nền tảng không hỗ trợ → bỏ qua.
        }
    }

    /// <summary>Hiện toast "Đã copy text {ô rút gọn}" neo trên ô vừa double-click, hẹn tự ẩn sau ~1.2s.</summary>
    private void ShowCopyToast(Control cell, string cellText)
    {
        var display = cellText.Length > ToastMaxChars
            ? cellText[..ToastMaxChars] + "…"
            : cellText;
        CopyToastText.Text = $"Đã copy text {display}";

        // Đóng toast cũ (nếu double-click ô khác khi toast trước còn hiện) rồi neo lại vào ô mới.
        CopyToastPopup.IsOpen = false;
        CopyToastPopup.PlacementTarget = cell;
        CopyToastPopup.IsOpen = true;

        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private DispatcherTimer CreateToastTimer()
    {
        var timer = new DispatcherTimer { Interval = ToastDuration };
        timer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            CopyToastPopup.IsOpen = false;
        };
        return timer;
    }
}
