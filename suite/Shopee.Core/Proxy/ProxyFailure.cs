namespace Shopee.Core.Proxy;

/// <summary>
/// Phân loại lỗi proxy theo PHẠM VI ẢNH HƯỞNG. Lỗi proxy TẠM THỜI của một tk (rớt mạng, IP xấu, proxy lẻ
/// chết) chữa được bằng cách cho tk nghỉ rồi vá phần dở bằng tk khác. Lỗi HẠ TẦNG TOÀN CỤC (key/tài khoản
/// KiotProxy chết) thì KHÔNG: mọi tk Shopee dùng CHUNG một key nên đổi tk bao nhiêu cũng hỏng y hệt —
/// phải dừng cả job, không được bỏ qua dòng (nếu không job chạy hết mà dữ liệu thủng lỗ chỗ).
/// </summary>
public static class ProxyFailure
{
    // Dấu hiệu CHẮC CHẮN key/tài khoản proxy chết: mã lỗi + câu thông báo nguyên văn của KiotProxy, vd
    // "KiotProxy new 400: Key proxy đã hết hạn, vui lòng gia hạn để tiếp tục sử dụng | KEY_EXPIRED".
    private static readonly string[] Markers =
    {
        "KEY_EXPIRED",
        "KEY_NOT_FOUND",
        "Key proxy đã hết hạn",
        "vui lòng gia hạn",
    };

    // "hết hạn" đứng MỘT MÌNH không đủ ("phiên đăng nhập hết hạn" của Shopee là lỗi của một tk) → chỉ tính
    // khi câu lỗi có nhắc "key". Danh sách cố tình HẸP: bắt nhầm lỗi lẻ thành lỗi toàn cục sẽ giết oan cả
    // job đang chạy tốt — nguy hiểm hơn chính cái bug đang chữa.
    private static readonly string[] ExpiredWords = { "hết hạn", "het han" };

    // Proxy LẺ không tìm thấy (IP sticky đã hết vòng đời / key chưa kích hoạt) — câu này CÓ chữ "key" nên
    // phải loại tay khỏi luật "hết hạn + key" ở trên. Đây là lỗi tạm thời: cooldown + đổi tk là chữa được.
    private static readonly string[] SingleProxyMarkers =
    {
        "PROXY_NOT_FOUND_BY_KEY",
        "Could not find the proxy being used by key",
    };

    /// <summary>Lỗi HẠ TẦNG TOÀN CỤC: key/tài khoản proxy chết ⇒ MỌI tk Shopee đều hỏng như nhau, đổi tk vô ích.
    /// Khác hẳn lỗi proxy TẠM THỜI của một tk (rớt mạng, IP xấu) vốn xử lý bằng cooldown + vá bằng tk khác.</summary>
    public static bool IsFleetWideProxyFailure(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        foreach (var m in Markers)
            if (reason.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;

        // Luật YẾU (suy từ chữ "hết hạn") — chỉ dùng khi chắc chắn không phải câu "không tìm thấy proxy của key".
        foreach (var m in SingleProxyMarkers)
            if (reason.Contains(m, StringComparison.OrdinalIgnoreCase)) return false;
        if (!reason.Contains("key", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var w in ExpiredWords)
            if (reason.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
