using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Hiển thị các hộp thoại modal (xác nhận, chọn thư mục, lưu CSV). Cửa sổ chính được shell gán khi app
/// khởi động (<c>DialogService.MainWindow = …</c>).
/// <para>
/// ⚠ ĐỢT 1 của cuộc port: 2 hộp thoại RIÊNG của module (ConfirmDialog, OrderDetailDialog) chưa dựng lại
/// bằng WPF (đợt 5) → <see cref="ConfirmAsync"/>/<see cref="InfoAsync"/> tạm dùng <see cref="MessageBox"/>
/// của Windows, còn <see cref="EditOrderAsync"/> trả null (coi như người dùng bấm Hủy). Chọn thư mục / lưu
/// CSV đã là bản WPF THẬT (Microsoft.Win32) nên dùng được ngay.
/// </para>
/// </summary>
public static class DialogService
{
    public static Window? MainWindow { get; set; }

    /// <summary>Hộp thoại xác nhận (Đồng ý/Hủy). Trả true nếu người dùng đồng ý.</summary>
    public static Task<bool> ConfirmAsync(string title, string message)
    {
        if (MainWindow is null)
        {
            return Task.FromResult(false);
        }

        var result = MessageBox.Show(MainWindow, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.OK);
    }

    /// <summary>Hộp thoại thông báo (chỉ nút Đóng).</summary>
    public static Task InfoAsync(string title, string message)
    {
        if (MainWindow is null)
        {
            return Task.CompletedTask;
        }

        MessageBox.Show(MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mở hộp thoại chọn THƯ MỤC cho người dùng chọn nơi lưu hóa đơn. Trả về đường dẫn thư mục đã chọn,
    /// hoặc null nếu chưa gán cửa sổ chính / người dùng bấm Hủy.
    /// <paramref name="startFolder"/> (nếu có &amp; tồn tại) dùng làm thư mục mở đầu; không có → mở mặc định.
    /// </summary>
    public static Task<string?> PickInvoiceFolderAsync(string? startFolder = null)
    {
        if (MainWindow is null)
        {
            return Task.FromResult<string?>(null);
        }

        var dlg = new OpenFolderDialog { Title = "Chọn thư mục lưu hóa đơn" };
        try
        {
            if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
                dlg.InitialDirectory = startFolder;
        }
        catch
        {
            // đường dẫn lỗi → mở mặc định
        }

        return Task.FromResult(dlg.ShowDialog(MainWindow) == true ? dlg.FolderName : null);
    }

    /// <summary>
    /// Hộp thoại thông tin đơn + đổi trạng thái. <paramref name="statuses"/> = các trạng thái đã sync về
    /// (chuỗi tự do). Trả về trạng thái người dùng chọn khi bấm "Lưu", hoặc null nếu Hủy / chưa gán cửa sổ.
    /// <para>ĐỢT 1: cửa sổ chi tiết đơn chưa port (đợt 5) → luôn trả null (như bấm Hủy).</para>
    /// </summary>
    public static Task<string?> EditOrderAsync(OrderRowViewModel row, IReadOnlyList<string> statuses)
    {
        _ = row;
        _ = statuses;
        System.Diagnostics.Trace.WriteLine(
            "[Orders] Hộp thoại chi tiết đơn chưa được port sang WPF (đợt 5) — bỏ qua lần mở này.");
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Mở SaveFileDialog cho người dùng chọn nơi lưu, rồi GHI <paramref name="content"/> (đã gồm BOM) ra
    /// file đó. Trả về đường dẫn đã lưu, hoặc null nếu chưa gán cửa sổ chính / người dùng bấm Hủy.
    /// </summary>
    public static async Task<string?> SaveCsvAsync(string suggestedFileName, byte[] content)
    {
        if (MainWindow is null)
        {
            return null;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Xuất đơn hàng ra CSV",
            FileName = suggestedFileName,
            DefaultExt = "csv",
            Filter = "CSV (mở bằng Excel)|*.csv",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(MainWindow) != true)
        {
            return null;
        }

        // Ghi đè file cũ thì cắt phần đuôi dư → dùng Create (truncate) thay vì mở ghi chèn.
        await using (var stream = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(content);
        }

        return dlg.FileName;
    }
}
