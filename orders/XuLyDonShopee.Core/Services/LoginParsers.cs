using System.Globalization;
using System.Text;
using System.Text.Json;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// HÀM THUẦN của luồng đăng nhập: chuẩn hóa text, khớp nhãn (mail cảnh báo bảo mật / link xác nhận / nav
/// subaccount) và chuyển JSON đọc từ DOM thành model. KHÔNG đụng trình duyệt nên <b>unit-test thẳng được</b>
/// (xem <c>ShopeeLoginVerifyEmailTests</c>, <c>SubaccountNavMatchTests</c>, <c>ShopListParseTests</c> — các test
/// đó gọi qua forwarder <see cref="ShopeeLoginService"/>).
/// </summary>
internal static class LoginParsers
{
    /// <summary>Chuẩn hóa text để so khớp bền: bỏ dấu tiếng Việt (kể cả đ→d), gộp mọi cụm khoảng trắng về một
    /// dấu cách, trim, hạ chữ thường. Dùng cho lọc tiêu đề "Cảnh báo bảo mật" (so <c>Contains</c> không dấu).</summary>
    internal static string NormalizeForMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var decomposed = collapsed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            // Bỏ dấu thanh/dấu phụ (combining marks); đ/Đ không tách được bằng FormD → thay thủ công bên dưới.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (ch)
            {
                case 'đ': sb.Append('d'); break;
                case 'Đ': sb.Append('D'); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>True nếu text của một dòng mail (InnerText: người gửi + tiêu đề + preview) là mail
    /// <b>"Cảnh báo bảo mật Tài khoản Shopee"</b> — người gửi khớp "shopee" VÀ nội dung (chuẩn hóa không dấu)
    /// CHỨA "canh bao bao mat". Loại mail trả hàng/khuyến mãi/khác của Shopee.</summary>
    internal static bool IsSecurityWarningMailRow(string? rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText) || !LoginSelectors.ShopeeSenderRegex.IsMatch(rowText))
        {
            return false;
        }

        return NormalizeForMatch(rowText).Contains("canh bao bao mat", StringComparison.Ordinal);
    }

    /// <summary>True nếu <paramref name="text"/> khớp <see cref="LoginSelectors.ConfirmLinkRegex"/> (text của link
    /// cần bấm, vd "TẠI ĐÂY"). Phơi ra để test — KHÔNG còn khớp "here"/"click here".</summary>
    internal static bool MatchesConfirmLink(string? text)
        => !string.IsNullOrEmpty(text) && LoginSelectors.ConfirmLinkRegex.IsMatch(text);

    /// <summary>True nếu <paramref name="text"/> khớp <see cref="LoginSelectors.ConfirmExpiredRegex"/> (trang báo
    /// link đã hết hạn/hết hiệu lực). Phơi ra để test.</summary>
    internal static bool MatchesConfirmExpired(string? text)
        => !string.IsNullOrEmpty(text) && LoginSelectors.ConfirmExpiredRegex.IsMatch(text);

    /// <summary>True nếu <paramref name="text"/> là nav "Tài khoản của tôi" trên Nền tảng tài khoản phụ: CHUẨN HÓA
    /// không dấu (<see cref="NormalizeForMatch"/> — trị cả NFC/NFD, chữ HOA) rồi khớp
    /// <see cref="LoginSelectors.MyAccountNavRegex"/>. KHÔNG khớp "Phân bổ chat" / "Tài khoản" đơn lẻ. Phơi ra để test.</summary>
    internal static bool MatchesMyAccountNav(string? text)
        => LoginSelectors.MyAccountNavRegex.IsMatch(NormalizeForMatch(text));

    /// <summary>True nếu <paramref name="text"/> là entry "Kênh Người bán"/"Seller Centre": CHUẨN HÓA không dấu
    /// (<see cref="NormalizeForMatch"/>) rồi khớp <see cref="LoginSelectors.SellerChannelRegex"/>. KHÔNG khớp "Kênh"
    /// đơn lẻ. Phơi ra để test.</summary>
    internal static bool MatchesSellerChannelEntry(string? text)
        => LoginSelectors.SellerChannelRegex.IsMatch(NormalizeForMatch(text));

    // JS CHỈ-ĐỌC quét bảng shop: mỗi dòng tr[data-row-key] → {rowKey, name, login}. Bọc từng dòng trong try để
    // một dòng lạ KHÔNG phá cả bảng. Trả JSON.stringify(mảng). Tên đăng nhập = span trong ô td thứ 2 (fallback
    // text của td thứ 2). Selector dùng class-contains để bền khi Shopee thêm hậu tố hash vào tên class.
    internal const string ScanShopListJs = @"() => {
    const norm = s => (s || '').replace(/\s+/g, ' ').trim();
    const rows = document.querySelectorAll(""tr[data-row-key]"");
    const out = [];
    for (const row of rows) {
        try {
            const rowKey = row.getAttribute('data-row-key') || '';
            const nameEl = row.querySelector(""span[class*='shop-name-text']"");
            const name = nameEl ? norm(nameEl.textContent) : '';
            let login = '';
            const tds = row.querySelectorAll('td');
            if (tds.length >= 2) {
                const span = tds[1].querySelector('span');
                login = norm(span ? span.textContent : tds[1].textContent);
            }
            out.push({ rowKey: rowKey, name: name, login: login });
        } catch (e) { /* dòng lạ — bỏ qua */ }
    }
    return JSON.stringify(out);
}";

    // Deserialize không phân biệt hoa/thường: khóa JSON rowKey/name/login khớp thuộc tính record.
    private static readonly JsonSerializerOptions ShopRowJsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record RawShopRow(string? RowKey, string? Name, string? Login);

    /// <summary>
    /// HÀM THUẦN (test được): chuyển JSON mảng <c>{rowKey,name,login}</c> (do <see cref="ScanShopListJs"/> đọc từ
    /// DOM) thành <see cref="ShopListItem"/>. Trim mọi trường; BỎ dòng không có <c>rowKey</c> (không định vị được
    /// để mở). Dòng thiếu login vẫn nhận (LoginName rỗng). JSON rỗng/hỏng → danh sách rỗng.
    /// </summary>
    internal static IReadOnlyList<ShopListItem> ParseShopListJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ShopListItem>();
        }

        List<RawShopRow>? raw;
        try { raw = JsonSerializer.Deserialize<List<RawShopRow>>(json, ShopRowJsonOpts); }
        catch { return Array.Empty<ShopListItem>(); }

        if (raw is null)
        {
            return Array.Empty<ShopListItem>();
        }

        var list = new List<ShopListItem>();
        foreach (var r in raw)
        {
            var id = (r.RowKey ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                continue; // không có mã shop → không định vị được dòng để mở → bỏ
            }
            list.Add(new ShopListItem(id, (r.Name ?? string.Empty).Trim(), (r.Login ?? string.Empty).Trim()));
        }
        return list;
    }

    /// Parse JSON (chuỗi <c>ScanOrdersJs</c> trả về) → danh sách <see cref="SyncedOrder"/>. Bọc
    /// từng phần tử trong try (phần tử lạ không phá cả danh sách); đơn KHÔNG có mã (orderSn rỗng) bị BỎ.
    /// Tổng tiền parse qua <see cref="ShopeeShippingNav.ParseVndAmount"/> (bỏ mọi ký tự không phải số).
    /// </summary>
    internal static List<SyncedOrder> ParseOrdersJson(string? json)
    {
        var result = new List<SyncedOrder>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var orderSn = GetJsonString(el, "orderSn");
                    if (string.IsNullOrWhiteSpace(orderSn))
                    {
                        continue; // không có mã đơn → không làm khóa được, bỏ
                    }

                    var itemsJson = "[]";
                    var itemCount = 0;
                    string? itemSummary = null;
                    string? sku = null;
                    if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        itemsJson = items.GetRawText();
                        itemCount = items.GetArrayLength();
                        if (itemCount > 0)
                        {
                            itemSummary = NullIfBlank(GetJsonString(items[0], "name"));
                            sku = ShopeeShippingNav.ExtractSku(itemSummary);
                        }
                    }

                    var totalText = GetJsonString(el, "totalText");
                    result.Add(new SyncedOrder
                    {
                        OrderSn = orderSn,
                        ShopeeOrderId = NullIfBlank(GetJsonString(el, "shopeeOrderId")),
                        BuyerUsername = NullIfBlank(GetJsonString(el, "buyer")),
                        ItemsJson = itemsJson,
                        ItemCount = itemCount,
                        ItemSummary = itemSummary,
                        Sku = sku,
                        TotalPriceText = NullIfBlank(totalText),
                        TotalPrice = ShopeeShippingNav.ParseVndAmount(totalText),
                        PaymentMethod = NullIfBlank(GetJsonString(el, "payment")),
                        Status = NullIfBlank(GetJsonString(el, "status")),
                        StatusDescription = NullIfBlank(GetJsonString(el, "statusDesc")),
                        CancelReason = NullIfBlank(GetJsonString(el, "cancelReason")),
                        Channel = NullIfBlank(GetJsonString(el, "channel")),
                        Carrier = NullIfBlank(GetJsonString(el, "carrier")),
                        TrackingNumber = NullIfBlank(GetJsonString(el, "tracking")),
                    });
                }
                catch { /* phần tử lạ — bỏ qua, không phá cả danh sách */ }
            }
        }
        catch { /* JSON hỏng — trả những gì đã parse được */ }

        return result;
    }

    /// <summary>Đọc chuỗi từ property JSON (chỉ nhận String; thiếu / kiểu khác → rỗng).</summary>
    private static string GetJsonString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Rỗng/khoảng-trắng → null (để cột DB để NULL thay vì chuỗi rỗng).</summary>
    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}
