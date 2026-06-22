using System.Net.Sockets;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Tiện ích nhận diện lỗi "connection refused" khi gọi CDP của Brave (cổng debug chưa sẵn sàng).
/// (Trước đây còn tự khởi động Python API — đã bỏ: Scrape đọc workbook/video bằng C# native.)
/// </summary>
internal static class ApiServerHelper
{
    public static bool IsConnectionRefused(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is HttpRequestException { InnerException: SocketException se } &&
                se.SocketErrorCode == SocketError.ConnectionRefused)
                return true;

            if (cur is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return true;

            if (cur.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string ConnectionRefusedHelp =>
        "Không kết nối được trình duyệt (CDP) — Brave chưa mở hoặc cổng gỡ lỗi chưa sẵn sàng.\n\n" +
        "Mở lại Brave / chờ vài giây rồi bấm Chạy tiếp lại.";
}
