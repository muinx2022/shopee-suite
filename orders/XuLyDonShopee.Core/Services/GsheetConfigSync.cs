namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Kết quả quyết định "bên nhận có ghi đè bằng bản của bên gửi không" — dùng cho CẢ HAI chiều: Hub → client
/// (<see cref="GsheetConfigSync.QuyetDinhApBanHub"/>) và client → Hub (<see cref="GsheetConfigSync.QuyetDinhNhanBanClient"/>).
/// </summary>
/// <param name="Ap">true = ghi <paramref name="Url"/> + <paramref name="Tab"/> đè lên bản của bên nhận.</param>
/// <param name="Url">URL Web App sẽ ghi (đã trim) — chỉ có nghĩa khi <paramref name="Ap"/> = true.</param>
/// <param name="Tab">Tab override sẽ ghi (đã trim; <c>""</c> = tự động theo tháng) — chỉ có nghĩa khi <paramref name="Ap"/> = true.</param>
public readonly record struct GsheetApplyDecision(bool Ap, string Url, string Tab);

/// <summary>
/// Luật ĐỒNG BỘ cấu hình Google Sheet giữa Hub và máy này — hàm THUẦN (không đụng DB/mạng/UI) để test được
/// và để dùng CHUNG một luật ở cả 3 nơi: client kéo về (<c>OrdersModuleHost</c>), client đẩy lên (màn Cài đặt),
/// hub validate ô nhập (trang <c>/config/orders</c>, file này được LINK vào project hub-web).
/// </summary>
public static class GsheetConfigSync
{
    /// <summary>Tiền tố BẮT BUỘC của URL Web App Apps Script (bản deploy <c>/exec</c>).</summary>
    public const string TienToUrl = "https://script.google.com/";

    /// <summary>
    /// Kiểm tra URL Web App: TRỐNG là hợp lệ (= tắt đồng bộ GSheet); khác trống thì phải bắt đầu bằng
    /// <see cref="TienToUrl"/>. Trả <c>null</c> khi hợp lệ, ngược lại là THÔNG ĐIỆP LỖI để hiện cho người dùng
    /// (mẫu <see cref="OrderNotifyService.KiemTraUrl"/>).
    /// </summary>
    public static string? KiemTraUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null; // trống → tắt đồng bộ (caller xử lý)
        }

        return url.Trim().StartsWith(TienToUrl, StringComparison.OrdinalIgnoreCase)
            ? null
            : "Link không hợp lệ — phải bắt đầu bằng " + TienToUrl;
    }

    /// <summary>
    /// Quyết định có áp bản cấu hình GSheet của HUB xuống máy này không.
    /// <list type="number">
    /// <item><b>Hub RỖNG thì KHÔNG đè:</b> URL của hub trống = hub CHƯA cấu hình (URL trống là công tắc TẮT
    /// ghi sheet) → trả <c>Ap = false</c>. Nếu đè thì mọi máy trong fleet âm thầm mất cấu hình đang chạy.</item>
    /// <item><b>Khối GSheet là MỘT đơn vị:</b> hub có URL ⇒ hub đã cấu hình ⇒ áp CẢ URL LẪN tab — tab trống lúc
    /// này mang đúng nghĩa "tự động theo tháng", không phải "chưa cấu hình" (nên KHÔNG dùng sentinel "(auto)").</item>
    /// <item>Giống hệt bản local (sau khi trim) → <c>Ap = false</c> để khỏi ghi SQLite mỗi nhịp poll vô ích.</item>
    /// </list>
    /// </summary>
    public static GsheetApplyDecision QuyetDinhApBanHub(string? hubUrl, string? hubTab, string? localUrl, string? localTab)
    {
        var url = (hubUrl ?? "").Trim();
        if (url.Length == 0)
        {
            return new GsheetApplyDecision(false, "", ""); // hub chưa cấu hình → GIỮ NGUYÊN bản local
        }

        var tab = (hubTab ?? "").Trim();
        var giongLocal = string.Equals(url, (localUrl ?? "").Trim(), StringComparison.Ordinal)
                         && string.Equals(tab, (localTab ?? "").Trim(), StringComparison.Ordinal);
        return new GsheetApplyDecision(!giongLocal, url, tab);
    }

    /// <summary>
    /// Máy này có được ĐẨY cấu hình GSheet của mình lên Hub không: chỉ khi URL local KHÁC TRỐNG — một máy chưa
    /// cấu hình mà đẩy lên sẽ xoá mất cấu hình của cả fleet.
    /// </summary>
    public static bool NenDayLenHub(string? localUrl) => !string.IsNullOrWhiteSpace(localUrl);

    /// <summary>
    /// Quyết định HUB có nhận bản client vừa đẩy lên không — ĐỐI XỨNG với <see cref="QuyetDinhApBanHub"/>, cùng
    /// một nguyên tắc "khối GSheet là MỘT đơn vị":
    /// <list type="number">
    /// <item>URL client gửi lên RỖNG ⇒ máy đó chưa cấu hình ⇒ KHÔNG ghi gì (một máy trống không được xoá cấu
    /// hình của cả fleet). Tab có giá trị cũng mặc kệ — không nhận một nửa khối.</item>
    /// <item>URL NON-EMPTY ⇒ client đang thực sự cấu hình khối GSheet ⇒ ghi CẢ HAI field, KỂ CẢ tab RỖNG: tab
    /// rỗng ở đây là ý định hợp lệ ("tự động theo tháng"), không phải "thiếu dữ liệu". Nhờ vậy người dùng xoá
    /// được override từ client — trước đây hub giữ tab cũ rồi đẩy ngược về, đổi ở client không bao giờ ăn.</item>
    /// <item>Trùng bản hub đang có → <c>Ghi = false</c> để KHÔNG bump version (khỏi cả fleet re-pull vô ích).</item>
    /// </list>
    /// </summary>
    public static GsheetApplyDecision QuyetDinhNhanBanClient(string? clientUrl, string? clientTab, string? hubUrl, string? hubTab)
    {
        var url = (clientUrl ?? "").Trim();
        if (url.Length == 0)
        {
            return new GsheetApplyDecision(false, "", ""); // client chưa cấu hình → GIỮ NGUYÊN bản hub
        }

        var tab = (clientTab ?? "").Trim();
        var giongHub = string.Equals(url, (hubUrl ?? "").Trim(), StringComparison.Ordinal)
                       && string.Equals(tab, (hubTab ?? "").Trim(), StringComparison.Ordinal);
        return new GsheetApplyDecision(!giongHub, url, tab);
    }
}
