using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.App.Views;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Hiển thị các hộp thoại modal (xác nhận, thông báo, chi tiết đơn, chọn thư mục, lưu CSV). Cửa sổ chính
/// được shell gán khi app khởi động (<c>DialogService.MainWindow = …</c>).
/// <para>
/// WPF <c>ShowDialog()</c> chặn luồng và trả <c>bool?</c>; chữ ký async ở đây giữ nguyên như bản Avalonia
/// (QĐ 13 của plan tổng) nên nơi gọi không phải sửa. Kết quả tuỳ biến (trạng thái đơn đã chọn) lấy qua
/// property <c>OrderDetailDialog.Result</c> thay cho <c>Close(string?)</c> của Avalonia.
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

        var dlg = new ConfirmDialog(title, message) { Owner = MainWindow };
        return Task.FromResult(dlg.ShowDialog() == true);
    }

    /// <summary>Hộp thoại thông báo (chỉ nút Đóng).</summary>
    public static Task InfoAsync(string title, string message)
    {
        if (MainWindow is null)
        {
            return Task.CompletedTask;
        }

        var dlg = new ConfirmDialog(title, message, infoOnly: true) { Owner = MainWindow };
        dlg.ShowDialog();
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
    /// </summary>
    public static Task<string?> EditOrderAsync(OrderRowViewModel row, IReadOnlyList<string> statuses)
    {
        if (MainWindow is null)
        {
            return Task.FromResult<string?>(null);
        }

        var dlg = new OrderDetailDialog(row, statuses) { Owner = MainWindow };
        return Task.FromResult(dlg.ShowDialog() == true ? dlg.Result : null);
    }

    /// <summary>
    /// Mở màn CHẨN ĐOÁN "đơn kết thúc chưa dọn được" (H2.5) — modal trên cửa sổ chính. Chưa gán cửa sổ chính →
    /// không làm gì (giữ nếp mọi hàm ở đây).
    /// </summary>
    public static void ShowChanDoanDon(ChanDoanDonViewModel vm)
    {
        if (MainWindow is null)
        {
            return;
        }

        var dlg = new ChanDoanDonDialog(vm) { Owner = MainWindow };
        dlg.ShowDialog();
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
