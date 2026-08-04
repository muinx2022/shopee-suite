using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// MỘT sản phẩm của đơn, đọc ở <b>TRANG CHI TIẾT</b> đơn (extension: <c>pageReadOrderProducts</c>). Khác hẳn dữ
/// liệu quét ở trang DANH SÁCH: <see cref="Sku"/> là SKU THẬT (không đoán từ tên) và <see cref="PhanLoai"/> đã
/// sạch (không dính đuôi <c>[SKU SKU]</c>).
/// <para>
/// <see cref="MetaLa"/> = các dòng <c>.product-meta</c> KHÔNG khớp nhãn nào (nguyên văn) — để log, đừng đoán bừa.
/// <see cref="BiCat"/> = danh sách đã bị cắt vì vượt trần sản phẩm/đơn của extension (log, đừng im lặng).
/// </para>
/// </summary>
public sealed record SanPhamDon(
    int? Stt,
    string? Ten,
    string? PhanLoai,
    string? Sku,
    long? DonGia,
    int? SoLuong,
    long? ThanhTien,
    string? Anh,
    IReadOnlyList<string> MetaLa,
    bool BiCat);

/// <summary>Cặp chuỗi NHIỀU DÒNG gửi lên Google Sheet: dòng thứ <i>i</i> của <see cref="Sku"/> khớp dòng thứ
/// <i>i</i> của <see cref="PhanLoai"/> (sản phẩm thiếu SKU để DÒNG TRỐNG, không nhảy dòng — nhảy là lệch cặp).</summary>
public sealed record CotGsheetSanPham(string Sku, string PhanLoai);

/// <summary>
/// Hàm THUẦN tách danh sách SẢN PHẨM của một đơn. Extension chỉ duyệt DOM rồi gửi TEXT THÔ (tiền/số lượng chưa
/// parse) — mọi luật số nằm ở đây để test được, đúng nếp <c>soYeuCauText</c> của bước trả hàng.
/// <para>
/// Dùng cho HAI nguồn, cùng một luật: (a) JSON extension gửi kèm <c>kind="finals"</c>
/// (<c>{stt, ten, phanLoai, sku, donGia, soLuong, thanhTien, anh, metaLa}</c>); (b) <c>items_json</c> đã lưu
/// trong DB (<c>{name, variation, amount, image}</c> + khóa mới <c>{phanLoai, sku, donGia, thanhTien}</c>) — nên
/// khóa được đọc theo BÍ DANH (<c>ten</c>/<c>name</c>, <c>soLuong</c>/<c>amount</c>, <c>anh</c>/<c>image</c>).
/// </para>
/// <para>
/// Dữ liệu đến từ web nên phải chịu rác: JSON rỗng/hỏng/không phải mảng → danh sách RỖNG, KHÔNG ném; số không
/// parse được → <c>null</c>, KHÔNG ném.
/// </para>
/// </summary>
public static class SanPhamDonParser
{
    /// <summary>Giữ tiếng Việt NGUYÊN VĂN trong <c>items_json</c> (đọc được bằng mắt khi soi DB/log), y như chuỗi
    /// extension gửi về trước đây (<c>GetRawText</c>).</summary>
    private static readonly JsonWriterOptions TuyChonGhi = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Parse JSON mảng sản phẩm → danh sách <see cref="SanPhamDon"/> theo ĐÚNG thứ tự trong mảng. Phần tử không
    /// có CẢ <c>ten</c> lẫn <c>sku</c> lẫn <c>phanLoai</c> bị BỎ (dòng rác). Rỗng / hỏng / không phải mảng →
    /// danh sách rỗng.
    /// </summary>
    public static IReadOnlyList<SanPhamDon> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SanPhamDon>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SanPhamDon>();
            }

            var list = new List<SanPhamDon>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue; // phần tử lạ (số/chuỗi) — bỏ qua, không phá cả đơn
                }

                var ten = Chuoi(el, "ten", "name");
                var phanLoai = Chuoi(el, "phanLoai");
                var sku = Chuoi(el, "sku");
                if (ten is null && phanLoai is null && sku is null)
                {
                    continue; // không tên, không SKU, không phân loại → dòng rác
                }

                list.Add(new SanPhamDon(
                    Stt: SoNguyen(Chuoi(el, "stt")),
                    Ten: ten,
                    PhanLoai: phanLoai,
                    Sku: sku,
                    DonGia: Tien(el, "donGia"),
                    SoLuong: SoNguyen(Chuoi(el, "soLuong", "amount")),
                    ThanhTien: Tien(el, "thanhTien"),
                    Anh: Chuoi(el, "anh", "image"),
                    MetaLa: MangChuoi(el, "metaLa"),
                    BiCat: el.TryGetProperty("bicat", out var bc) && bc.ValueKind == JsonValueKind.True));
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<SanPhamDon>(); // extension/DB gửi rác → coi như không đọc được sản phẩm
        }
    }

    /// <summary>
    /// Dựng <c>items_json</c> từ danh sách sản phẩm trang chi tiết. <b>TƯƠNG THÍCH NGƯỢC:</b> 4 khóa cũ
    /// <c>name/variation/amount/image</c> LUÔN có mặt (hub, <see cref="PhanLoaiExtractor"/> và màn xem đang đọc
    /// chúng) — <c>variation</c> = phân loại, <c>amount</c> = số lượng như bản quét trang danh sách. Bốn khóa MỚI
    /// <c>phanLoai/sku/donGia/thanhTien</c> chỉ ghi khi trang chi tiết đọc được (thiếu → vắng khóa, không ghi rỗng).
    /// </summary>
    public static string TaoItemsJson(IReadOnlyList<SanPhamDon> sanPham)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, TuyChonGhi))
        {
            w.WriteStartArray();
            foreach (var sp in sanPham)
            {
                w.WriteStartObject();
                w.WriteString("name", sp.Ten ?? string.Empty);
                w.WriteString("variation", sp.PhanLoai ?? string.Empty);
                w.WriteString("amount", sp.SoLuong?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                w.WriteString("image", sp.Anh ?? string.Empty);
                if (!string.IsNullOrEmpty(sp.PhanLoai))
                {
                    w.WriteString("phanLoai", sp.PhanLoai);
                }
                if (!string.IsNullOrEmpty(sp.Sku))
                {
                    w.WriteString("sku", sp.Sku);
                }
                if (sp.DonGia is not null)
                {
                    w.WriteNumber("donGia", sp.DonGia.Value);
                }
                if (sp.ThanhTien is not null)
                {
                    w.WriteNumber("thanhTien", sp.ThanhTien.Value);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Chọn bản <c>items_json</c> để LƯU khi upsert. Bản đọc ở TRANG CHI TIẾT (có khóa <c>sku</c>/<c>phanLoai</c>)
    /// GIÀU hơn bản quét ở trang DANH SÁCH (chỉ <c>name/variation/amount/image</c>) — <b>không được để bản nghèo
    /// đè bản giàu</b>: trang chi tiết chỉ mở lại cho đơn THIẾU ước tính, nên đơn đã có ước tính mà bị đè là mất
    /// SKU/phân loại VĨNH VIỄN.
    /// <para>
    /// Luật: <paramref name="moi"/> rỗng/null → GIỮ <paramref name="cu"/> (đừng xóa dữ liệu bằng rỗng);
    /// <paramref name="moi"/> CÓ dữ liệu chi tiết → lấy <paramref name="moi"/> (bản mới nhất luôn thắng);
    /// <paramref name="moi"/> KHÔNG có mà <paramref name="cu"/> CÓ → GIỮ <paramref name="cu"/>; cả hai đều không
    /// có → lấy <paramref name="moi"/> (bản mới nhất từ trang danh sách — giữ hành vi cũ).
    /// </para>
    /// <para>
    /// Nhận biết "giàu" bằng <see cref="Parse"/>, KHÔNG so chuỗi thô (JSON có thể đổi thứ tự khóa); JSON hỏng đọc
    /// ra danh sách RỖNG nên tự rơi về bên đọc được. Caller phải cho <c>item_count</c> ĐI THEO bản được chọn.
    /// </para>
    /// </summary>
    public static string? ChonItemsJson(string? cu, string? moi)
    {
        if (string.IsNullOrWhiteSpace(moi))
        {
            return cu; // không có gì để ghi → tuyệt đối không xóa bản đang có
        }

        var spMoi = Parse(moi);
        if (spMoi.Count == 0)
        {
            // "[]" / rác: chỉ được ghi đè khi bản cũ cũng chẳng có sản phẩm nào.
            return Parse(cu).Count == 0 ? moi : cu;
        }
        if (CoDuLieuChiTiet(spMoi))
        {
            return moi; // bản mới nhất + đủ chi tiết → luôn thắng
        }
        return CoDuLieuChiTiet(Parse(cu)) ? cu : moi; // moi nghèo: cu giàu thì GIỮ cu, cu cũng nghèo thì lấy moi
    }

    /// <summary>
    /// Hai cột "Mã Sp" và "Phân loại" của Google Sheet, dựng từ CÙNG một danh sách đã sắp thứ tự nên hai chuỗi
    /// BẰNG SỐ DÒNG (nối bằng <c>"\n"</c> — <c>setValue</c> của Apps Script cho ra ô nhiều dòng). Số lượng gắn
    /// hậu tố <c>". SL: N"</c> vào phân loại khi đã biết N ≥ 1 (kể cả 1), qua
    /// <see cref="PhanLoaiExtractor.GanSoLuong"/>.
    /// <para>
    /// Trả <c>null</c> = KHÔNG có dữ liệu trang chi tiết (đơn cũ: <c>items_json</c> chỉ có
    /// <c>name/variation/amount/image</c>, hoặc không đọc được sản phẩm nào) ⇒ caller phải giữ NGUYÊN đường cũ
    /// (<see cref="PhanLoaiExtractor.TuItemsJson"/> + cột SKU đoán từ tên), KHÔNG gửi chuỗi rỗng đè ô đang có.
    /// </para>
    /// </summary>
    public static CotGsheetSanPham? CotGsheet(string? itemsJson)
    {
        var sanPham = Parse(itemsJson);
        if (sanPham.Count == 0)
        {
            return null;
        }
        // Chỉ đi đường MỚI khi thật sự có dữ liệu trang CHI TIẾT — khóa sku/phanLoai chỉ trang chi tiết mới có.
        if (!CoDuLieuChiTiet(sanPham))
        {
            return null;
        }

        var sku = new List<string>(sanPham.Count);
        var phanLoai = new List<string>(sanPham.Count);
        foreach (var sp in sanPham)
        {
            sku.Add(sp.Sku ?? string.Empty); // thiếu SKU → DÒNG TRỐNG, tuyệt đối không nhảy dòng (lệch cặp)
            var pl = PhanLoaiExtractor.DonGian(sp.PhanLoai);
            phanLoai.Add(PhanLoaiExtractor.GanSoLuong(pl, sp.SoLuong));
        }
        return new CotGsheetSanPham(string.Join("\n", sku), string.Join("\n", phanLoai));
    }

    /// <summary>Danh sách có dữ liệu TRANG CHI TIẾT chưa? = ít nhất MỘT sản phẩm mang <c>sku</c> hoặc
    /// <c>phanLoai</c> — hai khóa chỉ trang chi tiết mới có (bản quét trang danh sách chỉ có <c>variation</c>).</summary>
    private static bool CoDuLieuChiTiet(IReadOnlyList<SanPhamDon> sanPham)
        => sanPham.Any(sp => !string.IsNullOrEmpty(sp.Sku) || !string.IsNullOrEmpty(sp.PhanLoai));

    /// <summary>Chuỗi từ property ĐẦU TIÊN có mặt trong <paramref name="khoa"/> (bí danh): đổi <c>&amp;nbsp;</c>
    /// thành khoảng trắng + trim. Thiếu / kiểu khác / rỗng → <c>null</c>.</summary>
    private static string? Chuoi(JsonElement el, params string[] khoa)
    {
        foreach (var k in khoa)
        {
            if (!el.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                return s!.Replace('\u00A0', ' ').Trim();
            }
        }
        return null;
    }

    /// <summary>Tiền: nhận CẢ text thô extension gửi ("303.050") LẪN số đã ghi trong <c>items_json</c> (303050).
    /// Không parse được → <c>null</c>.</summary>
    private static long? Tien(JsonElement el, string khoa)
    {
        if (!el.TryGetProperty(khoa, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            return ShopeeShippingNav.ParseVndAmount(v.GetString());
        }
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    }

    /// <summary>Số nguyên từ text thô: giữ mọi CHỮ SỐ rồi parse (nên tiền tố <c>x</c>/<c>×</c> của ô số lượng tự
    /// rụng). Không có chữ số / tràn <see cref="int"/> → <c>null</c>.</summary>
    private static int? SoNguyen(string? s)
    {
        var n = ShopeeShippingNav.ParseVndAmount(s);
        return n is >= 0 and <= int.MaxValue ? (int)n.Value : null;
    }

    /// <summary>Mảng chuỗi (bỏ phần tử rỗng/kiểu khác); thiếu / không phải mảng → mảng rỗng.</summary>
    private static IReadOnlyList<string> MangChuoi(JsonElement el, string khoa)
    {
        if (!el.TryGetProperty(khoa, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var it in v.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var s = it.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                list.Add(s!.Trim());
            }
        }
        return list.Count == 0 ? Array.Empty<string>() : list;
    }
}
