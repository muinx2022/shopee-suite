namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Cấu hình cho lớp dữ liệu native (thay API Python): đường dẫn workbook + thư mục lưu video.
/// ViewModel của module Scrape đặt giá trị trước khi chạy. Vì một lần scrape chỉ làm trên 1 shop
/// (1 workbook + 1 sheet) nên dùng tĩnh là đủ.
/// </summary>
public static class ScrapeNativeSettings
{
    public static string WorkbookPath = "";
    public static string VideoOutputDir = @"D:\videos";
}
