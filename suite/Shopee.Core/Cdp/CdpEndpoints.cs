namespace Shopee.Core.Cdp;

/// <summary>
/// Dựng URL các endpoint HTTP của DevTools (CDP) — MỘT chỗ duy nhất giữ quy tắc
/// <b>luôn 127.0.0.1, KHÔNG "localhost"</b>: trên Windows "localhost" phân giải ::1 (IPv6) trước, mà
/// Brave/Chromium chỉ nghe CDP trên IPv4 127.0.0.1 → gọi qua "localhost" bị chậm/timeout/đứt.
/// Trước đây quy tắc này chỉ nằm trong 1 comment lẻ còn URL thì nội suy tay ở ~20 chỗ.
/// </summary>
public static class CdpEndpoints
{
    /// <summary>Host CDP — cố định IPv4 loopback (xem chú thích lớp).</summary>
    public const string Host = "127.0.0.1";

    /// <summary>Gốc <c>http://127.0.0.1:{port}</c>.</summary>
    public static string Base(int port) => $"http://{Host}:{port}";

    /// <summary>Danh sách target (<c>/json/list</c>).</summary>
    public static string List(int port) => $"{Base(port)}/json/list";

    /// <summary>Thông tin browser + <c>webSocketDebuggerUrl</c> cấp browser (<c>/json/version</c>).</summary>
    public static string Version(int port) => $"{Base(port)}/json/version";

    /// <summary>Danh sách target bản rút gọn (<c>/json</c>) — alias cũ của <see cref="List"/>.</summary>
    public static string Targets(int port) => $"{Base(port)}/json";

    /// <summary>Mở tab mới tới <paramref name="url"/> (<c>/json/new</c>). Luôn mở FOREGROUND.</summary>
    public static string New(int port, string url) => $"{Base(port)}/json/new?{Uri.EscapeDataString(url)}";

    /// <summary>Đóng target theo id (<c>/json/close/{id}</c>).</summary>
    public static string Close(int port, string targetId) =>
        $"{Base(port)}/json/close/{Uri.EscapeDataString(targetId)}";
}
