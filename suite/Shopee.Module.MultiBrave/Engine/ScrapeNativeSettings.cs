namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Cấu hình cho lớp dữ liệu native (thay API Python): thư mục lưu video. ViewModel của module Scrape đặt
/// giá trị trước khi chạy; dùng CHUNG mọi BigSeller nên để tĩnh (workbook thì per-instance qua InstanceConfig).
/// </summary>
public static class ScrapeNativeSettings
{
    public static string VideoOutputDir = @"D:\videos";
}
