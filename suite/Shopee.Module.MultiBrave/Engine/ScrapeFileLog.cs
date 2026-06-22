using Shopee.Core.Infrastructure;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Ghi log runner Scrape ra file CỐ ĐỊNH <c>%AppData%\ShopeeSuite\logs\scrape.log</c> (không phụ thuộc
/// nơi build/chạy) để chẩn đoán mà KHÔNG cần copy thủ công từ cửa sổ log. Tự cắt khi file vượt 5MB.
/// </summary>
internal static class ScrapeFileLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 5 * 1024 * 1024;
    private static int _writeCount;

    private static string LogPath => Path.Combine(SuitePaths.ModuleDir("logs"), "scrape.log");

    public static void Write(string? instance, string message)
    {
        try
        {
            var path = LogPath;
            lock (Lock)
            {
                // Cứ ~500 dòng kiểm tra kích thước 1 lần; quá ngưỡng thì xoá khởi tạo lại (tránh phình vô hạn).
                if (++_writeCount % 500 == 0)
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes)
                            File.WriteAllText(path, "");
                    }
                    catch { }
                }

                var tag = string.IsNullOrWhiteSpace(instance) ? "" : $"[{instance}] ";
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag}{message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
