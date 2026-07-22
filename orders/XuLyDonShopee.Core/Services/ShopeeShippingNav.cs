namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Hàm thuần so khớp text/href của menu &amp; tab khi điều hướng tới "Cài Đặt Vận Chuyển" → tab "Địa Chỉ"
/// trên trang bán hàng Shopee. Tách khỏi Playwright để test được: chỉ nhận vào chuỗi đã lấy từ DOM
/// (InnerText/href) và trả về kết quả so khớp. Text lấy từ DOM có thể kèm xuống dòng/khoảng trắng thừa
/// hoặc rác badge → mọi so khớp đều chuẩn hóa trước qua <see cref="NormalizeUiText"/>.
/// </summary>
public static class ShopeeShippingNav
{
    /// <summary>
    /// Chuẩn hóa text lấy từ UI để so khớp bền: <c>null</c> → rỗng; thay mọi cụm khoảng trắng (kể cả
    /// xuống dòng/tab) bằng một dấu cách, <see cref="string.Trim()"/>, rồi hạ về chữ thường
    /// (<see cref="string.ToLowerInvariant"/>).
    /// </summary>
    public static string NormalizeUiText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        // Split theo mọi ký tự khoảng trắng (space, tab, xuống dòng, non-breaking...) rồi ghép lại bằng
        // một dấu cách → gộp mọi cụm whitespace về 1 space, tự loại khoảng trắng đầu/cuối.
        var collapsed = string.Join(' ', s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim().ToLowerInvariant();
    }

    /// <summary>True nếu <paramref name="href"/> trỏ tới trang Cài đặt vận chuyển
    /// (chứa <c>/portal/all-settings/shipping</c>).</summary>
    public static bool IsShippingSettingHref(string? href)
        => href is not null && href.Contains("/portal/all-settings/shipping", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>True nếu text (đã chuẩn hóa) chính là "cài đặt vận chuyển".</summary>
    public static bool IsShippingSettingText(string? s)
        => NormalizeUiText(s) == "cài đặt vận chuyển";

    /// <summary>True nếu text (đã chuẩn hóa) chính là "quản lý đơn hàng" (mục cha ở menu trái).</summary>
    public static bool IsOrderMenuText(string? s)
        => NormalizeUiText(s) == "quản lý đơn hàng";

    /// <summary>
    /// True nếu text (đã chuẩn hóa) <b>chứa</b> "địa chỉ" — InnerText của tab "Địa Chỉ" có thể kèm rác
    /// badge nên dùng "chứa" thay vì bằng tuyệt đối. Hai tab còn lại ("đơn vị vận chuyển" /
    /// "chứng từ vận chuyển") không chứa chuỗi này nên không bị nhầm.
    /// </summary>
    public static bool IsAddressTabText(string? s)
        => NormalizeUiText(s).Contains("địa chỉ", System.StringComparison.Ordinal);

    /// <summary>Parse chuỗi trạng thái từ JS ("ready"/"collapsed"/"covered", không phân biệt hoa thường,
    /// kèm khoảng trắng thừa) về <see cref="LinkReadiness"/>; null/rỗng/lạ → Unknown.</summary>
    public static LinkReadiness ParseLinkReadiness(string? s) => NormalizeUiText(s) switch
    {
        "ready" => LinkReadiness.Ready,
        "collapsed" => LinkReadiness.Collapsed,
        "covered" => LinkReadiness.Covered,
        _ => LinkReadiness.Unknown,
    };

    // ===== Bước 2: so khớp địa chỉ lấy hàng theo tỉnh + text nút/checkbox/modal trong tab "Địa Chỉ" =====

    /// <summary>
    /// Rút <b>tên lõi tỉnh/thành</b> từ giá trị <c>Account.PickupAddress</c> của app (chuẩn hóa rồi bỏ
    /// tiền tố loại đơn vị hành chính nếu có): "Hà Nội" → <c>"hà nội"</c>; "TP Hồ Chí Minh" →
    /// <c>"hồ chí minh"</c>; "Thanh Hóa" → <c>"thanh hóa"</c>; "Thành phố Hà Nội" → <c>"hà nội"</c>;
    /// "Tỉnh Thanh Hóa" → <c>"thanh hóa"</c>. Null/rỗng → chuỗi rỗng.
    /// </summary>
    public static string ProvinceCoreName(string? appProvince)
    {
        var norm = NormalizeUiText(appProvince);
        if (norm.Length == 0)
        {
            return string.Empty;
        }

        // Bỏ tiền tố loại đơn vị hành chính để còn lại tên lõi. "tp." (dính) đứng trước "tp " (có dấu cách).
        string[] prefixes = { "thành phố ", "tỉnh ", "tp.", "tp " };
        foreach (var prefix in prefixes)
        {
            if (norm.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                norm = norm.Substring(prefix.Length).Trim();
                break;
            }
        }

        return norm;
    }

    /// <summary>
    /// True nếu ô "Địa chỉ" (<paramref name="detailText"/>, InnerText của <c>.detail</c>, có thể nhiều
    /// dòng) thuộc tỉnh <paramref name="appProvince"/>. So khớp trên <b>dòng cuối không rỗng</b> (dòng
    /// tỉnh/thành trên Shopee, vd "Tỉnh Thanh Hóa"): chuẩn hóa rồi kiểm tra <b>chứa</b>
    /// <see cref="ProvinceCoreName"/>. Detail chỉ có 1 dòng / không tách được → so trên toàn chuỗi
    /// (fallback). appProvince rỗng hoặc detail rỗng → false. Chỉ xét dòng cuối nên KHÔNG khớp nhầm khi
    /// dòng đầu chứa tên một tỉnh khác.
    /// </summary>
    public static bool AddressDetailMatchesProvince(string? detailText, string? appProvince)
    {
        var core = ProvinceCoreName(appProvince);
        if (core.Length == 0 || string.IsNullOrWhiteSpace(detailText))
        {
            return false;
        }

        // Dòng cuối KHÔNG rỗng (tách theo '\n'; NormalizeUiText sẽ cắt '\r'/khoảng trắng thừa). Detail 1
        // dòng → dòng cuối chính là toàn chuỗi (fallback tự nhiên).
        string? lastNonEmpty = null;
        foreach (var line in detailText.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lastNonEmpty = line;
            }
        }

        var target = NormalizeUiText(lastNonEmpty ?? detailText);
        return target.Contains(core, System.StringComparison.Ordinal);
    }

    /// <summary>True nếu text (đã chuẩn hóa) chính là "địa chỉ lấy hàng" — nhãn tag đánh dấu địa chỉ
    /// đang là địa chỉ lấy hàng của shop.</summary>
    public static bool IsPickupTagText(string? s)
        => NormalizeUiText(s) == "địa chỉ lấy hàng";

    /// <summary>True nếu text (đã chuẩn hóa) chính là "sửa" (nút Sửa của một địa chỉ).</summary>
    public static bool IsEditButtonText(string? s)
        => NormalizeUiText(s) == "sửa";

    /// <summary>True nếu text (đã chuẩn hóa) chính là "hủy" (nút Hủy trong modal).</summary>
    public static bool IsCancelButtonText(string? s)
        => NormalizeUiText(s) == "hủy";

    /// <summary>True nếu text (đã chuẩn hóa) chính là "lưu" (nút Lưu trong footer modal).</summary>
    public static bool IsSaveButtonText(string? s)
        => NormalizeUiText(s) == "lưu";

    /// <summary>
    /// True nếu nhãn checkbox (đã chuẩn hóa) chính là "đặt làm địa chỉ lấy hàng" — cái cần tick. KHÔNG
    /// khớp "đặt làm địa chỉ mặc đinh" (chính tả thật của Shopee, thiếu dấu) hay "đặt làm địa chỉ trả hàng".
    /// </summary>
    public static bool IsSetPickupCheckboxText(string? s)
        => NormalizeUiText(s) == "đặt làm địa chỉ lấy hàng";

    /// <summary>True nếu tiêu đề modal (đã chuẩn hóa) chính là "sửa địa chỉ".</summary>
    public static bool IsEditAddressModalTitle(string? s)
        => NormalizeUiText(s) == "sửa địa chỉ";

    /// <summary>
    /// True nếu text (đã chuẩn hóa) chính là "đồng ý" — nút <b>primary</b> của hộp xác nhận đổi địa chỉ
    /// lấy hàng (modal thứ hai bật lên SAU khi bấm Lưu, không phải lúc nào cũng hiện). Bấm nút này CHỐT
    /// việc đổi địa chỉ lấy hàng nhưng đồng thời TẮT kênh vận chuyển "Trong Ngày" và hình thức "Thanh toán
    /// khi nhận hàng" — đúng theo yêu cầu người dùng. Text "đồng ý" đủ phân biệt với nút primary "lưu" của
    /// modal Sửa Địa chỉ.
    /// </summary>
    public static bool IsConfirmButtonText(string? s)
        => NormalizeUiText(s) == "đồng ý";

    /// <summary>
    /// True nếu text (đã chuẩn hóa) chính là "kiểm tra chi tiết" — nút phụ (không primary) của hộp xác
    /// nhận đổi địa chỉ lấy hàng. Dùng làm <b>dấu hiệu riêng</b> để nhận đúng hộp xác nhận này (footer có
    /// CẢ "Đồng ý" lẫn "Kiểm tra chi tiết") — TUYỆT ĐỐI KHÔNG bấm nút này.
    /// </summary>
    public static bool IsCheckDetailButtonText(string? s)
        => NormalizeUiText(s) == "kiểm tra chi tiết";

    // ===== Bước 3: điều hướng "Tất cả" → Chuẩn bị hàng → tự mang ra bưu cục → In phiếu giao =====

    /// <summary>True nếu <paramref name="href"/> trỏ tới trang danh sách đơn (chứa <c>/portal/sale/order</c>).</summary>
    public static bool IsAllOrdersHref(string? href)
        => href is not null && href.Contains("/portal/sale/order", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>True nếu text (đã chuẩn hóa) chính là "tất cả" — link vào danh sách đơn (tab "Chờ xử lý").</summary>
    public static bool IsAllOrdersText(string? s)
        => NormalizeUiText(s) == "tất cả";

    /// <summary>True nếu nhãn TAB (đã chuẩn hóa) chính là "tất cả" — tab "Tất cả" trên thanh tab của trang
    /// danh sách đơn (<c>l1-tab-all</c>), phân biệt với các tab khác ("chờ lấy hàng"/"đang giao"...). Tab
    /// "Tất cả" KHÔNG kèm badge số nên khớp tuyệt đối (khác <see cref="IsToShipTabText"/> dùng StartsWith).</summary>
    public static bool IsAllTabText(string? s)
        => NormalizeUiText(s) == "tất cả";

    /// <summary>True nếu text (đã chuẩn hóa) BẮT ĐẦU bằng "chờ lấy hàng" — nhãn tab "Chờ lấy hàng" trên
    /// trang danh sách đơn (nhãn có thể kèm badge số: "Chờ lấy hàng (10)"). Dùng StartsWith vì badge.</summary>
    public static bool IsToShipTabText(string? s)
        => NormalizeUiText(s).StartsWith("chờ lấy hàng", System.StringComparison.Ordinal);

    /// <summary>True nếu text (đã chuẩn hóa) chính là "chuẩn bị hàng" — nút hành động trong card đơn.</summary>
    public static bool IsPrepareOrderButtonText(string? s)
        => NormalizeUiText(s) == "chuẩn bị hàng";

    /// <summary>True nếu text (đã chuẩn hóa) chính là "xác nhận" — nút xác nhận trong modal "Giao Đơn Hàng".</summary>
    public static bool IsConfirmArrangeButtonText(string? s)
        => NormalizeUiText(s) == "xác nhận";

    /// <summary>True nếu text (đã chuẩn hóa) chính là "in phiếu giao" — nút in phiếu trong modal "Thông Tin Chi Tiết".</summary>
    public static bool IsPrintSlipButtonText(string? s)
        => NormalizeUiText(s) == "in phiếu giao";

    /// <summary>
    /// True nếu tiêu đề lựa chọn (đã chuẩn hóa) <b>chứa</b> "tự mang hàng tới bưu cục" — option "Tôi sẽ tự
    /// mang hàng tới Bưu cục" trong modal "Giao Đơn Hàng". Dùng "chứa" vì text option có thể kèm chữ khác
    /// ("Tôi sẽ ..."). Các option khác (giao cho đơn vị vận chuyển...) không chứa chuỗi này.
    /// </summary>
    public static bool IsDropoffTitleText(string? s)
        => NormalizeUiText(s).Contains("tự mang hàng tới bưu cục", System.StringComparison.Ordinal);

    /// <summary>True nếu tiêu đề modal (đã chuẩn hóa) chính là "giao đơn hàng".</summary>
    public static bool IsShipOrderModalTitle(string? s)
        => NormalizeUiText(s) == "giao đơn hàng";

    /// <summary>True nếu tiêu đề modal (đã chuẩn hóa) chính là "thông tin chi tiết".</summary>
    public static bool IsDetailModalTitle(string? s)
        => NormalizeUiText(s) == "thông tin chi tiết";

    /// <summary>
    /// Rút <b>mã đơn</b> từ InnerText của ô <c>.order-sn</c> (dạng "Mã đơn hàng 260715ABC..."): gộp khoảng
    /// trắng rồi lấy <b>token cuối</b> (mã đơn là chuỗi liền không dấu cách, đứng sau nhãn "Mã đơn hàng").
    /// GIỮ NGUYÊN hoa/thường (không chuẩn hóa lowercase — mã đơn có chữ hoa). Null/rỗng → chuỗi rỗng.
    /// </summary>
    public static string ExtractOrderCode(string? orderSnText)
    {
        if (string.IsNullOrWhiteSpace(orderSnText))
        {
            return string.Empty;
        }

        var tokens = orderSnText.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? string.Empty : tokens[^1];
    }

    /// <summary>
    /// Rút giá trị tham số <c>job_id</c> từ URL tab phiếu giao (<c>...awbprint?job_id=...&amp;shop_id=...</c>)
    /// bằng khớp <c>job_id=([^&amp;]+)</c>. Không có / null/rỗng → chuỗi rỗng. Dùng đặt tên file phiếu khi
    /// không có mã đơn.
    /// </summary>
    public static string ExtractJobId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var m = System.Text.RegularExpressions.Regex.Match(url, "job_id=([^&]+)");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    /// <summary>
    /// [LỖI THỜI — KHÔNG còn là nguồn thư mục] Đường dẫn phiếu cũ. Thư mục lưu phiếu giờ đọc từ Cài đặt
    /// (<c>SettingsRepository.GetInvoiceFolder()</c>, mặc định <c>{thư mục app.db}\Phieu-giao-hang</c>) và
    /// được TRUYỀN VÀO cả nơi TẢI phiếu (<c>ProcessFirstOrderAsync</c>/<c>SaveSlipAsync</c>) lẫn nơi MỞ phiếu
    /// (<c>OrderRowViewModel.SlipPath</c>). Hằng này GIỮ lại làm mốc lịch sử, KHÔNG dùng làm nguồn nữa.
    /// Tên file vẫn = <see cref="SanitizeFileName"/>(order_sn) + ".pdf".
    /// </summary>
    public const string SlipDownloadDir = @"D:\Phieu-giao-hang";

    /// <summary>
    /// Làm sạch chuỗi để đặt <b>tên file</b> phiếu: giữ chữ/số/<c>-</c>/<c>_</c>, thay ký tự khác (dấu cách,
    /// <c>/ \ : * ? " &lt; &gt; |</c>...) bằng <c>_</c>, rồi cắt <c>_</c> thừa ở hai đầu. Null/rỗng/chỉ toàn
    /// ký tự lạ → <c>"phieu"</c> (không bao giờ trả tên rỗng).
    /// </summary>
    public static string SanitizeFileName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return "phieu";
        }

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        }

        var result = sb.ToString().Trim('_');
        return result.Length == 0 ? "phieu" : result;
    }

    /// <summary>
    /// Trích <b>SKU</b> từ tên sản phẩm = chuỗi <b>ASCII chữ/số</b> LIÊN TỤC cuối cùng, VÀ phải chứa ít nhất
    /// một chữ số. Duyệt từ cuối tên: bỏ qua các ký tự đuôi không phải ASCII chữ/số (khoảng trắng, dấu câu,
    /// ký tự tiếng Việt có dấu…), rồi gom chuỗi ASCII chữ/số liền mạch. SKU thật luôn có số (vd "B02435"),
    /// nên tên tiếng Việt kết thúc bằng từ thường ("màu đỏ", "cotton") không có số → trả <c>null</c> để KHÔNG
    /// bắt nhầm. Null/rỗng/không có ASCII chữ/số ở cuối / không có chữ số → <c>null</c>.
    /// </summary>
    public static string? ExtractSku(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        // Bỏ qua đuôi không phải ASCII chữ/số (khoảng trắng, dấu câu, ký tự có dấu…).
        int end = productName.Length - 1;
        while (end >= 0 && !char.IsAsciiLetterOrDigit(productName[end]))
        {
            end--;
        }
        if (end < 0)
        {
            return null; // không có ký tự ASCII chữ/số nào
        }

        // Gom ngược tới đầu chuỗi ASCII chữ/số liền mạch.
        int start = end;
        while (start >= 0 && char.IsAsciiLetterOrDigit(productName[start]))
        {
            start--;
        }

        var sku = productName.Substring(start + 1, end - start);

        // Phải có ít nhất 1 chữ số — SKU thật dạng "B02435"; loại "cotton"/"đỏ" (từ thường không số).
        foreach (var ch in sku)
        {
            if (char.IsAsciiDigit(ch))
            {
                return sku;
            }
        }

        return null;
    }

    /// <summary>
    /// True nếu đơn ở trạng thái <b>hủy</b> (dùng để tô nền đỏ dòng trên Google Sheet). Tiêu chí: có
    /// <paramref name="cancelReason"/> (Shopee chỉ điền lý do khi đơn bị hủy) — HOẶC <paramref name="status"/>
    /// (hạ chữ thường) chứa <c>"hủy"</c> hoặc <c>"huỷ"</c> (HAI biến thể dấu tiếng Việt — dữ liệu thật có cả
    /// "Đã hủy" lẫn "Đơn Huỷ") hoặc <c>"cancel"</c>. Mọi tham số trống → false.
    /// <para>
    /// <b>CỐ Ý KHÔNG quét <paramref name="statusDescription"/></b> (vẫn nhận để giữ chữ ký 3 tham số): mô tả
    /// phụ của đơn ĐANG CHẠY thường chứa câu như "Đơn sẽ bị hủy nếu quá hạn giao…" → nếu quét sẽ tô đỏ NHẦM
    /// đơn "Chờ lấy hàng". Chỉ dựa vào <paramref name="status"/> (trạng thái chính) + <paramref name="cancelReason"/>.
    /// </para>
    /// </summary>
    public static bool LaDonHuy(string? status, string? statusDescription, string? cancelReason)
    {
        // Có lý do hủy = đơn đã bị hủy.
        if (!string.IsNullOrWhiteSpace(cancelReason))
        {
            return true;
        }

        // CHỈ trạng thái chính (KHÔNG quét statusDescription — xem doc: tránh false positive "sẽ bị hủy…").
        return ChuaTuHuy(status);
    }

    /// <summary>
    /// True nếu đơn ở trạng thái <b>ĐÃ GIAO</b> tính "Đã bán" (+1 SKU khi đơn chuyển sang trạng thái này). Tiêu chí:
    /// <paramref name="status"/> sau chuẩn hoá (<see cref="NormalizeUiText"/>: trim + gộp khoảng trắng + lower) KHỚP
    /// TUYỆT ĐỐI một trong {"đã giao", "hoàn thành", "giao hàng thành công"}.
    /// <para>
    /// <b>CỐ Ý dùng khớp tuyệt đối (KHÔNG Contains)</b> để LOẠI các trạng thái "giao dở/không thành công":
    /// "Đã giao cho ĐVVC" / "Đã giao cho đơn vị vận chuyển" (mới bàn giao ĐVVC, khách CHƯA nhận) và "Giao hàng
    /// không thành công" đều KHÔNG khớp (chúng CHỨA "đã giao"/"giao hàng" nhưng không BẰNG các chuỗi trên). "Đã hủy"
    /// → false. Null/rỗng → false.
    /// </para>
    /// </summary>
    public static bool LaDaGiaoDaBan(string? status)
    {
        var s = NormalizeUiText(status);
        return s == "đã giao" || s == "hoàn thành" || s == "giao hàng thành công";
    }

    /// <summary>True nếu chuỗi (hạ chữ thường) chứa "hủy"/"huỷ" (2 biến thể dấu) hoặc "cancel".</summary>
    private static bool ChuaTuHuy(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var t = s.ToLowerInvariant();
        return t.Contains("hủy", System.StringComparison.Ordinal)
            || t.Contains("huỷ", System.StringComparison.Ordinal)
            || t.Contains("cancel", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Parse chuỗi tổng tiền lấy từ DOM về số nguyên VND: <b>bỏ MỌI ký tự không phải chữ số ASCII</b> rồi
    /// parse. "₫166.500" → <c>166500</c>; "₫1.234.567" → <c>1234567</c>; "166.500đ" → <c>166500</c>.
    /// Null/rỗng/không có chữ số nào / tràn số → <c>null</c> (không parse được → cột total_price để NULL).
    /// </summary>
    public static long? ParseVndAmount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= '0' and <= '9')
            {
                sb.Append(ch);
            }
        }

        if (sb.Length == 0)
        {
            return null;
        }

        return long.TryParse(sb.ToString(), out var value) ? value : null;
    }
}

/// <summary>Trạng thái sẵn sàng nhận click của link trong submenu (đọc từ DOM bằng JS hình học).</summary>
public enum LinkReadiness
{
    /// <summary>Không đọc được / giá trị lạ — coi như không rõ, xử lý thận trọng.</summary>
    Unknown,
    /// <summary>Link nhận click tại tâm của nó — click được ngay.</summary>
    Ready,
    /// <summary>Submenu đang CỤP (chiều cao ~0 / tâm link thuộc về phần tử ngoài submenu) — cần bung mục cha.</summary>
    Collapsed,
    /// <summary>Link đang bị phần tử khác TRONG cùng submenu đè (popover hover...) — chờ rồi thử lại, KHÔNG click mục cha.</summary>
    Covered,
}
