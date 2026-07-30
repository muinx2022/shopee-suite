using System.Text.Json;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// <b>"Số tiền cuối cùng" (cột Ước tính) + danh sách sản phẩm đọc từ TRANG CHI TIẾT đơn</b>: chọn đơn nào đáng mở
/// chi tiết và gộp kết quả extension trả về vào DTO. Toàn hàm THUẦN — tách khỏi <see cref="OrdersBridgeSession"/>
/// (đợt dọn 2026-07-30) để test được luật chọn/gộp mà không cần trình duyệt, và để vòng flow shop
/// (<see cref="ShopFlowRunner"/>) chỉ còn phần điều phối.
/// </summary>
internal static class UocTinhDon
{
    /// <summary>Trần số đơn ĐÃ RỜI trạng thái "chuẩn bị hàng" được lấy BÙ "Số tiền cuối cùng" mỗi lượt. TÁCH RIÊNG
    /// khỏi trần 30 đơn/lượt của phần chính (extension) và xếp SAU phần chính, để đơn đang chuẩn bị — việc gấp —
    /// KHÔNG bị đơn cũ chiếm chỗ. Đừng nới: mỗi đơn bù là một lượt mở tab chi tiết, nới là nổ số tab + rủi ro captcha.</summary>
    internal const int MaxBuUocTinh = 5;

    /// <summary>Số ngày lùi tối đa (theo NGÀY trong mã đơn) còn được lấy bù "Số tiền cuối cùng" — đủ phủ vòng đời
    /// một đơn; cũ hơn thì coi như bỏ hẳn, đừng mở lại mãi.</summary>
    internal const int SoNgayBuUocTinh = 7;

    /// <summary>Ngày đặt đơn suy từ <b>6 ký tự ĐẦU</b> mã đơn Shopee (<c>yyMMdd</c>: "260726PN0HHCS5" → 26/07/2026).
    /// <para><see cref="SyncedOrder"/> KHÔNG có trường ngày (DTO quét DOM danh sách không đọc ngày đặt), mà mã đơn
    /// thì luôn mở đầu bằng ngày — đây là nguồn ngày DUY NHẤT có sẵn. Không đủ 6 chữ số / không phải ngày hợp lệ →
    /// <c>null</c> ⇒ đơn đó KHÔNG được lấy bù (thà bỏ còn hơn đoán).</para></summary>
    internal static DateTime? NgayDonTuMa(string? orderSn)
    {
        if (string.IsNullOrWhiteSpace(orderSn) || orderSn!.Length < 6)
        {
            return null;
        }

        var dau = orderSn[..6];
        foreach (var c in dau)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }

        return DateTime.TryParseExact(dau, "yyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Chọn đơn cần mở TRANG CHI TIẾT để lấy "Số tiền cuối cùng", tách làm HAI nhóm:
    /// <list type="bullet">
    /// <item><b>Chính</b> — đơn đang "chuẩn bị hàng"/"chờ lấy hàng" chưa có ước tính (luật cũ, không đổi).</item>
    /// <item><b>Bù</b> — đơn ĐÃ RỜI trạng thái đó mà vẫn thiếu ước tính, còn trong <see cref="SoNgayBuUocTinh"/> ngày.
    /// Không có nhóm này thì đơn nào rời trạng thái trước khi kịp lấy ước tính là mất VĨNH VIỄN (ô "tiền bán" trên
    /// Google Sheet trống mãi) — có đơn hỏng CÓ HỆ THỐNG, thử lại bao nhiêu lượt cũng không ra. Bù được chốt chặn
    /// bằng <see cref="MaxBuUocTinh"/> và ưu tiên đơn MỚI trước (Shopee còn dữ liệu, khả năng lấy được cao hơn).</item>
    /// </list>
    /// Đơn ĐÃ HỦY bị loại khỏi nhóm bù ("Số tiền cuối cùng" chỉ có nghĩa với đơn còn sống; luật cũ đã loại sẵn
    /// vì đơn hủy không ở trạng thái chuẩn bị). Cả hai nhóm đều đòi có <c>ShopeeOrderId</c> (không có thì không mở
    /// được trang chi tiết) và không nằm trong <paramref name="done"/>.
    /// </summary>
    /// <param name="homNay">Ngày "hôm nay" (giờ máy) để tính cửa sổ <see cref="SoNgayBuUocTinh"/> ngày.</param>
    internal static (List<SyncedOrder> Chinh, List<SyncedOrder> Bu) ChonDonLayUocTinh(
        IReadOnlyList<SyncedOrder> orders, IReadOnlySet<string> done, DateTime homNay)
    {
        bool ThieuUocTinh(SyncedOrder o) =>
            o.FinalAmount is null
            && !string.IsNullOrWhiteSpace(o.ShopeeOrderId)
            && !done.Contains(o.OrderSn);

        var chinh = orders
            .Where(o => ShopeeShippingNav.LaChuanBiHang(o.Status) && ThieuUocTinh(o))
            .ToList();

        var mocCu = homNay.Date.AddDays(-SoNgayBuUocTinh);
        var bu = orders
            .Where(o => !ShopeeShippingNav.LaChuanBiHang(o.Status)
                && ThieuUocTinh(o)
                && !ShopeeShippingNav.LaDonHuy(o.Status, o.StatusDescription, o.CancelReason)
                && NgayDonTuMa(o.OrderSn) is DateTime ngay
                && ngay >= mocCu
                // Nới biên trên 1 ngày: ngày trong mã đơn là giờ Việt Nam, đồng hồ/múi giờ máy lệch một chút là
                // đơn VỪA đặt hôm nay — ca đáng lấy bù nhất — bị loại oan. Xa hơn 1 ngày thì mã đó là rác.
                && ngay <= homNay.Date.AddDays(1))
            .OrderByDescending(o => NgayDonTuMa(o.OrderSn)!.Value)   // đơn MỚI trước
            .ThenByDescending(o => o.OrderSn, StringComparer.Ordinal) // hoà ngày → thứ tự ĐỊNH được, khỏi phập phù
            .Take(MaxBuUocTinh)
            .ToList();

        return (chinh, bu);
    }

    /// <summary>Lý do đọc được từ cờ <c>nguon</c> extension gửi kèm (<c>pageChanDoanUocTinh</c>) — chỉ để LOG.</summary>
    private static string LyDoHutUocTinh(string? nguon) => nguon switch
    {
        "dang-tai" => "thẻ đang tải, hết giờ",
        "khong-thay" => "không thấy thẻ",
        _ => "không rõ",
    };

    /// <summary>Parse JSON mảng <c>{orderSn, finalText, sanPham:[…]}</c> (extension trả) → map theo <c>orderSn</c> → gán
    /// <see cref="SyncedOrder.FinalAmount"/> (<see cref="ShopeeShippingNav.ParseVndAmount"/>) + <see cref="SyncedOrder.FinalAmountText"/>
    /// cho đơn khớp trong <paramref name="orders"/> (CHỈ khi finalText khác rỗng). Trả số đơn đã gán. Best-effort (JSON lỗi → 0).
    /// <para>
    /// Gộp LUÔN danh sách SẢN PHẨM trang chi tiết (<see cref="SanPhamDonParser"/>) vào <see cref="SyncedOrder.ItemsJson"/>:
    /// trang chi tiết là nguồn CHUẨN (SKU thật, phân loại sạch, đủ sản phẩm hơn) nên THAY cả mảng; đọc được rỗng →
    /// GIỮ NGUYÊN mảng cũ quét ở trang danh sách, không xoá. Dòng meta lạ / danh sách bị cắt vì vượt trần → log
    /// qua <paramref name="log"/>, đừng nuốt im lặng.
    /// </para></summary>
    internal static int MergeFinalAmounts(IReadOnlyList<SyncedOrder> orders, string? finalsJson, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(finalsJson))
        {
            return 0;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var sanPhamMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var nhoBangDoanhThu = 0;          // đọc được TRONG KHI thẻ remote CHƯA có số ⇒ số đơn bảng doanh thu CỨU được
        var hut = new List<string>();     // đơn KHÔNG lấy được, kèm lý do phân biệt được (xem LyDoHutUocTinh)
        try
        {
            using var doc = JsonDocument.Parse(finalsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var sn = el.TryGetProperty("orderSn", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    var ft = el.TryGetProperty("finalText", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    var ng = el.TryGetProperty("nguon", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(sn))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(ft))
                    {
                        map[sn!] = ft!;
                        if (ng == "chi-bang")
                        {
                            nhoBangDoanhThu++;
                        }
                    }
                    else
                    {
                        hut.Add($"{sn} ({LyDoHutUocTinh(ng)})");
                    }
                    if (el.TryGetProperty("sanPham", out var sp) && sp.ValueKind == JsonValueKind.Array)
                    {
                        sanPhamMap[sn!] = sp.GetRawText();
                    }
                }
            }
        }
        catch { return 0; }

        // Bố cục nào đang phổ biến + đơn nào hụt vì gì — trước đây chỉ có "3/4 đơn", soi log không ra manh mối.
        if (nhoBangDoanhThu > 0)
        {
            log($"Số tiền cuối cùng: {nhoBangDoanhThu} đơn chỉ đọc được nhờ BẢNG DOANH THU trang chính (thẻ [type='FinalAmount'] chưa có số).");
        }
        if (hut.Count > 0)
        {
            var ke = string.Join(", ", hut.Take(3));
            log($"KHÔNG lấy được Số tiền cuối cùng {hut.Count} đơn: {ke}{(hut.Count > 3 ? ", …" : "")}.");
        }

        var got = 0;
        var donCoSanPham = 0;
        var tongSanPham = 0;
        foreach (var order in orders)
        {
            if (map.TryGetValue(order.OrderSn, out var finalText) && !string.IsNullOrWhiteSpace(finalText))
            {
                order.FinalAmount = ShopeeShippingNav.ParseVndAmount(finalText);
                order.FinalAmountText = finalText;
                got++;
            }

            if (!sanPhamMap.TryGetValue(order.OrderSn, out var raw))
            {
                continue;
            }
            var sanPham = SanPhamDonParser.Parse(raw);
            if (sanPham.Count == 0)
            {
                continue; // trang chi tiết không đọc được sản phẩm nào → GIỮ NGUYÊN mảng cũ
            }

            order.ItemsJson = SanPhamDonParser.TaoItemsJson(sanPham);
            order.ItemCount = sanPham.Count; // giữ đúng bất biến "item_count = độ dài mảng items"
            donCoSanPham++;
            tongSanPham += sanPham.Count;

            if (sanPham.Any(sp => sp.BiCat))
            {
                log($"Đơn {order.OrderSn}: danh sách sản phẩm VƯỢT trần của extension — đã cắt còn {sanPham.Count}, có thể thiếu sản phẩm.");
            }
            foreach (var la in sanPham.SelectMany(sp => sp.MetaLa).Distinct(StringComparer.Ordinal).Take(3))
            {
                log($"Đơn {order.OrderSn}: dòng thông tin sản phẩm KHÔNG khớp nhãn nào → '{la}'.");
            }
        }

        if (donCoSanPham > 0)
        {
            log($"Đọc sản phẩm trang chi tiết: {donCoSanPham} đơn, {tongSanPham} sản phẩm.");
        }
        return got;
    }
}
