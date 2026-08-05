using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shopee.Toolkit.MsLogin;

namespace XuLyDonShopee.Core.Services;

/// <summary>Một dòng trên trang "Trả hàng/Hoàn tiền/Hủy" đã ghép ĐỦ CẢ HAI mã — chỉ những dòng như thế mới
/// được lưu vào đơn (thiếu một trong hai thì bỏ, xem <see cref="TraHangParser.GhepCap"/>).</summary>
public sealed record YeuCauTraHang(string MaDon, string MaYeuCau);

/// <summary>Một dòng THÔ extension gửi về: id đơn Shopee lấy từ <c>href</c> + HTML của khối đầu dòng
/// (<c>.return-row-item-head</c>, đã bỏ <c>img</c>/<c>svg</c>). Extension CHỈ duyệt DOM, KHÔNG phân loại — luật
/// nhận diện nằm ở C# (<see cref="TraHangParser.TachMa"/>) để test được và để log nguyên văn HTML khi luật trượt.
/// <para>
/// <b><see cref="ShopeeOrderId"/> thường RỖNG:</b> trên trang trả hàng, <c>href</c> của dòng là
/// <c>/portal/sale/return/&lt;returnId&gt;</c> chứ KHÔNG phải <c>/portal/sale/order/&lt;id&gt;</c> nên regex ở
/// extension không bắt được gì. Không sao — ghép cặp CHỈ dùng <see cref="HeadHtml"/>, field này thuần chẩn đoán.
/// KHÔNG được đổi regex sang bắt <c>/return/(\d+)</c>: nhét return-id vào field tên "orderId" là sai ngữ nghĩa.
/// </para>
/// <para>
/// <b><see cref="LaTraHang"/></b> = <c>href</c> trỏ <c>/portal/sale/return/…</c> ⇒ dòng TRẢ HÀNG thật; <c>false</c>
/// = <c>/portal/sale/order/…</c> ⇒ dòng ĐƠN HỦY (không bao giờ có mã yêu cầu). Đây là chốt chặn THỨ HAI, độc lập
/// với việc chọn được tab hay không. <c>null</c> = extension đời CŨ chưa gửi cờ → coi như KHÔNG BIẾT và GIỮ dòng
/// (hành vi cũ), đừng lọc mất sạch khi client chưa kịp cập nhật.
/// </para></summary>
public sealed record DongTraHang(string? ShopeeOrderId, string HeadHtml, bool? LaTraHang = null);

/// <summary>Kết quả một lượt đọc trang trả hàng (đã parse JSON extension gửi). <see cref="SoYeuCau"/> null =
/// không đọc được số (text lạ); <see cref="SortApplied"/> false = KHÔNG đổi được sắp xếp sang "Ngày yêu cầu
/// (Mới - Cũ)" nên "N dòng đầu" có thể sai; <see cref="TabTraHang"/> false = KHÔNG chọn được tab
/// "Đơn Trả hàng Hoàn tiền" nên số đọc được là của tab "Tất cả" — GỘP cả Đơn Hủy / Đơn Giao hàng không thành
/// công, hai loại KHÔNG có mã yêu cầu trả hàng. <see cref="SortApplied"/> false chỉ để caller log CẢNH BÁO;
/// <see cref="TabTraHang"/> false thì caller BỎ HẲN LƯỢT (mốc giữ nguyên) — ghi số tab "Tất cả" vào mốc là đầu
/// độc mốc, xem <see cref="ShopFlowRunner.QuyetDinhLuotTraHang"/>.
/// <para><see cref="ChanDoan"/> = mô tả 4 dấu hiệu trang lúc extension BỎ lượt vì không đọc được ô tổng (url,
/// title, ô tổng có/rỗng, số dòng, có tab-wrapper) — null khi đọc bình thường. Thuần để LOG.</para></summary>
public sealed record KetQuaDocTraHang(
    int? SoYeuCau, bool SortApplied, bool TabTraHang, IReadOnlyList<DongTraHang> Dong, string? ChanDoan = null);

/// <summary>Kết quả ghép cặp: <see cref="Cap"/> = dòng đủ hai mã; <see cref="ThieuMaYeuCau"/> = mô tả CHẨN ĐOÁN
/// (mã đơn + class/nhãn đọc được của từng khối + HTML thô rút gọn) của dòng CÓ mã đơn mà KHÔNG có mã yêu cầu;
/// <see cref="BoQuaDonHuy"/> = số dòng bị bỏ vì <c>href</c> nói đó là ĐƠN HỦY (chỉ để LOG).</summary>
public sealed record KetQuaGhepTraHang(
    IReadOnlyList<YeuCauTraHang> Cap, IReadOnlyList<string> ThieuMaYeuCau, int BoQuaDonHuy = 0);

/// <summary>Kết quả lọc theo cửa sổ ngày (<see cref="TraHangParser.LocTheoCuaSo"/>): <see cref="GiuLai"/> = cặp
/// còn trong hạn; <see cref="BoQuaViCu"/> = số cặp bị bỏ vì NGÀY YÊU CẦU quá cũ; <see cref="GiuViKhongRoNgay"/> =
/// số cặp GIỮ LẠI dù mã yêu cầu không suy được ngày. Hai con số sau chỉ để LOG — nhìn nhật ký là biết vì sao đọc
/// mấy chục dòng mà chỉ lưu được vài mã, khỏi tưởng hỏng.</summary>
public sealed record KetQuaLocCuaSo(
    IReadOnlyList<YeuCauTraHang> GiuLai, int BoQuaViCu, int GiuViKhongRoNgay);

/// <summary>4 nhánh luật đếm số yêu cầu (xem <see cref="TraHangParser.QuyetDinhCheck"/>).</summary>
public enum LuatSoYeuCau
{
    /// <summary>Chưa có mốc (shop này chưa từng check) → check <c>min(số yêu cầu, trần dòng/lượt)</c> dòng ĐẦU
    /// rồi mới ghi mốc. Bản đầu CHỈ ghi mốc mà không đọc dòng nào: hệ quả là shop nào cũng chốt mốc ở lượt đầu
    /// rồi im lặng mãi, toàn bộ yêu cầu đang có KHÔNG bao giờ được đọc.</summary>
    LanDau,

    /// <summary>Số không đổi → bỏ qua hẳn.</summary>
    KhongDoi,

    /// <summary>Số GIẢM (yêu cầu đã xử xong, rớt khỏi danh sách) → chỉ cập nhật lại mốc.</summary>
    Giam,

    /// <summary>Số TĂNG k → check k dòng ĐẦU (danh sách đã sắp "Ngày yêu cầu Mới - Cũ").</summary>
    Tang,
}

/// <summary>Quyết định sau khi đọc số yêu cầu: nhánh luật + số dòng đầu cần check (0 với 3 nhánh đầu).</summary>
public readonly record struct QuyetDinhTraHang(LuatSoYeuCau Luat, int SoDongCanCheck);

/// <summary>
/// Hàm THUẦN cho bước "check đơn trả hàng" (bước CUỐI của flow mỗi shop): parse JSON extension gửi về, tách
/// cặp <c>(mã đơn, mã yêu cầu trả hàng)</c>, và luật đếm số yêu cầu giữa hai lượt. Tách khỏi trình duyệt để
/// test được — extension chỉ duyệt DOM rồi gửi HTML thô.
/// <para>
/// <b>Class đã XÁC NHẬN trên HTML thật</b> (một dòng trả hàng đầy đủ, 2026-07-28): khối mã đơn là
/// <c>&lt;div class="id order-id"&gt;</c>, khối mã yêu cầu là <c>&lt;div class="id return-id"&gt;</c>, cả hai
/// dùng chung ô giá trị <c>&lt;span class="id-content"&gt;</c>. Nhận diện đi theo 3 tầng
/// <b>class → nhãn → vị trí</b> (xem <see cref="TachMa"/>): giữ đủ cả 3 vì class vẫn có thể đổi tiếp. Dòng có
/// mã đơn mà KHÔNG có mã yêu cầu được gom vào <see cref="KetQuaGhepTraHang.ThieuMaYeuCau"/> KÈM class/nhãn dò
/// được + HTML thô — nhật ký lần chạy thật lộ ngay cấu trúc mới nếu cả 3 tầng đều trượt.
/// </para>
/// </summary>
public static class TraHangParser
{
    /// <summary>Trần ký tự HTML thô đưa vào thông báo chẩn đoán (đủ thấy khối mã, không làm ngập nhật ký).</summary>
    private const int TranHtmlChanDoan = 600;

    /// <summary>
    /// Trần số dòng đọc trong MỘT lượt (nhánh <see cref="LuatSoYeuCau.LanDau"/> kẹp về đây). Đây là chỗ khai báo
    /// DUY NHẤT phía C# — mọi caller lấy qua hằng này, đừng gõ lại số.
    /// <para><b>Phải khớp <c>MAX_RETURN_ROWS</c> trong <c>extensions/shopee-orders/background.js</c></b>: extension
    /// đã cắt danh sách gửi về ở đúng trần đó, xin nhiều hơn cũng không có (cùng khuôn cặp hằng
    /// <c>MAX_ORDER_PAGES</c> ↔ <c>MaxSyncPages</c>). Hai runtime khác nhau nên không dùng chung được một literal;
    /// sửa một bên PHẢI sửa bên kia.</para>
    /// <para>Không nới: cửa sổ <see cref="SoNgayCuaSoTraHang"/> ngày đằng nào cũng cắt phần lịch sử sâu hơn, mà
    /// mỗi dòng thêm là thêm HTML gửi qua cầu nối. (Lý do CŨ — "đơn không còn trong DB thì lưu mã cũng vứt" — đã
    /// HẾT hiệu lực từ khi mã trả hàng có bảng <c>return_codes</c> sống độc lập với vòng đời đơn.)</para>
    /// </summary>
    public const int TranDongMoiLuot = 50;

    /// <summary>Mọi thẻ HTML (kể cả <c>&lt;!----&gt;</c> của Vue) — dùng để cắt biên khối và bóc text nhãn.</summary>
    private static readonly Regex TheHtml = new("<[^>]*>", RegexOptions.Compiled);

    /// <summary>Thuộc tính <c>class</c> trong một thẻ mở (nháy kép hoặc nháy đơn).</summary>
    private static readonly Regex ThuocTinhClass =
        new("class\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Luật đếm ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Luật người dùng chốt: <paramref name="mocCu"/> null (lần đầu) → check <c>min(soMoi, tranDong)</c> dòng ĐẦU
    /// rồi ghi mốc; số không đổi → bỏ qua; số GIẢM → chỉ cập nhật mốc; số TĂNG k → check k dòng ĐẦU.
    /// <see cref="LuatSoYeuCau.KhongDoi"/>/<see cref="LuatSoYeuCau.Giam"/> đều trả
    /// <see cref="QuyetDinhTraHang.SoDongCanCheck"/> = 0 nhưng GIỮ nhánh riêng để log/nghiệm thu phân biệt được.
    /// <paramref name="soMoi"/> âm (rác) được kẹp về 0.
    /// <para>LẦN ĐẦU phải đọc thật, không chỉ ghi mốc: nếu chỉ ghi mốc thì mọi yêu cầu ĐANG CÓ của shop không bao
    /// giờ được đọc — chỉ yêu cầu phát sinh SAU mốc mới lọt vào nhánh <see cref="LuatSoYeuCau.Tang"/>.</para>
    /// </summary>
    /// <param name="tranDong">Trần số dòng đọc lần đầu — mặc định <see cref="TranDongMoiLuot"/>; tham số hoá chỉ
    /// để test kẹp trần được với số nhỏ, caller thật KHÔNG truyền.</param>
    public static QuyetDinhTraHang QuyetDinhCheck(int? mocCu, int soMoi, int tranDong = TranDongMoiLuot)
    {
        var moi = Math.Max(0, soMoi);
        if (mocCu is null)
        {
            return new QuyetDinhTraHang(LuatSoYeuCau.LanDau, Math.Min(moi, Math.Max(0, tranDong)));
        }
        if (moi == mocCu.Value)
        {
            return new QuyetDinhTraHang(LuatSoYeuCau.KhongDoi, 0);
        }
        if (moi < mocCu.Value)
        {
            return new QuyetDinhTraHang(LuatSoYeuCau.Giam, 0);
        }
        return new QuyetDinhTraHang(LuatSoYeuCau.Tang, moi - mocCu.Value);
    }

    /// <summary>
    /// Số yêu cầu từ text ô <c>.return-list-summary-title</c> (vd "7 Yêu cầu" → 7, "1.234 Yêu cầu" → 1234):
    /// lấy CỤM SỐ ĐẦU TIÊN, cho phép dấu chấm/phẩy ngăn nghìn Ở GIỮA hai nhóm chữ số. Không có chữ số / null /
    /// tràn <see cref="int"/> → <c>null</c> (KHÔNG ném).
    /// </summary>
    public static int? ParseSoYeuCau(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var i = 0;
        while (i < title.Length && !char.IsDigit(title[i]))
        {
            i++;
        }
        if (i >= title.Length)
        {
            return null;
        }

        var so = new StringBuilder();
        while (i < title.Length)
        {
            var ch = title[i];
            if (char.IsDigit(ch))
            {
                so.Append(ch);
                i++;
                continue;
            }
            // Dấu ngăn nghìn CHỈ được tính khi ngay sau nó lại là chữ số ("1.234" ok; "7. Yêu cầu" dừng ở 7).
            if ((ch == '.' || ch == ',') && i + 1 < title.Length && char.IsDigit(title[i + 1]))
            {
                i++;
                continue;
            }
            break;
        }

        return int.TryParse(so.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    // ── Parse JSON extension gửi về ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse JSON <c>{soYeuCauText, sortApplied, tabTraHang, list:[{shopeeOrderId, headHtml}]}</c> extension gửi kèm
    /// <c>pageData kind="returns"</c>. JSON rỗng/hỏng/thiếu field → kết quả RỖNG (<see cref="KetQuaDocTraHang.SoYeuCau"/>
    /// null, danh sách rỗng) chứ KHÔNG ném — bước này là bước phụ, hỏng thì đi tiếp.
    /// </summary>
    public static KetQuaDocTraHang ParseKetQua(string? json)
    {
        var rong = new KetQuaDocTraHang(null, false, false, Array.Empty<DongTraHang>(), null);
        if (string.IsNullOrWhiteSpace(json))
        {
            return rong;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return rong;
            }

            var soYeuCau = root.TryGetProperty("soYeuCauText", out var t) && t.ValueKind == JsonValueKind.String
                ? ParseSoYeuCau(t.GetString())
                : null;
            var sortApplied = root.TryGetProperty("sortApplied", out var s) && s.ValueKind == JsonValueKind.True;
            // Thiếu field (bản extension CŨ) → false ⇒ caller log cảnh báo. Mặc định "coi như chưa đúng tab" là
            // phía AN TOÀN: cảnh báo thừa còn hơn im lặng khi số đang lẫn đơn hủy.
            var tabTraHang = root.TryGetProperty("tabTraHang", out var tb) && tb.ValueKind == JsonValueKind.True;

            var dong = new List<DongTraHang>();
            if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in list.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var html = el.TryGetProperty("headHtml", out var h) && h.ValueKind == JsonValueKind.String
                        ? h.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        continue; // dòng không có HTML đầu dòng → không tách được mã nào
                    }
                    var soi = el.TryGetProperty("shopeeOrderId", out var o) && o.ValueKind == JsonValueKind.String
                        ? o.GetString()
                        : null;
                    // Cờ THIẾU (extension đời cũ) → null = "không biết" ⇒ GhepCap giữ dòng như trước. Chỉ khi
                    // extension nói rõ true/false mới có chốt chặn theo href.
                    bool? laTraHang = el.TryGetProperty("laTraHang", out var lt)
                        ? lt.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => (bool?)null,
                        }
                        : null;
                    dong.Add(new DongTraHang(string.IsNullOrWhiteSpace(soi) ? null : soi, html!, laTraHang));
                }
            }

            return new KetQuaDocTraHang(soYeuCau, sortApplied, tabTraHang, dong, DocChanDoan(root));
        }
        catch (JsonException)
        {
            return rong; // extension gửi rác → coi như không đọc được, bước sau tự bỏ qua
        }
    }

    /// <summary>
    /// Gói CHẨN ĐOÁN extension gửi kèm khi BỎ lượt vì không đọc được ô tổng (<c>chanDoan</c>) → một dòng text cho
    /// nhật ký. Thiếu field / không phải object → <c>null</c> (lượt đọc bình thường không có gói này).
    /// <para>Bốn dấu hiệu phân biệt DỨT ĐIỂM ba nguyên nhân: ô tổng KHÔNG tồn tại = Shopee đổi selector; ô tổng
    /// CÓ mà rỗng = hết giờ THẬT (nới thời gian mới có nghĩa); không có tab-wrapper = lạc trang; có dòng render mà
    /// không có ô tổng = ô tổng đổi chỗ.</para>
    /// </summary>
    private static string? DocChanDoan(JsonElement root)
    {
        if (!root.TryGetProperty("chanDoan", out var cd) || cd.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = cd.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var title = cd.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var coOTong = cd.TryGetProperty("coOTong", out var co) && co.ValueKind == JsonValueKind.True;
        var textOTong = cd.TryGetProperty("textOTong", out var tx) && tx.ValueKind == JsonValueKind.String
            ? tx.GetString()
            : null;
        var soDong = cd.TryGetProperty("soDong", out var sd) && sd.ValueKind == JsonValueKind.Number
                     && sd.TryGetInt32(out var n)
            ? n
            : 0;
        var coWrapper = cd.TryGetProperty("coTabWrapper", out var cw) && cw.ValueKind == JsonValueKind.True;

        var oTong = !coOTong
            ? "KHÔNG có .return-list-summary-title"
            : (string.IsNullOrEmpty(textOTong) ? "ô tổng CÓ nhưng RỖNG" : $"ô tổng = '{textOTong}'");
        return $"url={url} · title='{title}' · {oTong} · {soDong} dòng .return-row-item · "
            + $"{(coWrapper ? "CÓ" : "KHÔNG có")} .return-case-tab-wrapper";
    }

    // ── Tách + ghép cặp mã ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghép cặp <c>(mã đơn, mã yêu cầu)</c> cho các dòng trong <paramref name="dong"/>: dòng đủ CẢ HAI mã vào
    /// <see cref="KetQuaGhepTraHang.Cap"/>; dòng CÓ mã đơn mà THIẾU mã yêu cầu vào
    /// <see cref="KetQuaGhepTraHang.ThieuMaYeuCau"/> dưới dạng chuỗi chẩn đoán (mã đơn + nhãn đọc được + HTML thô
    /// rút gọn) để nhật ký lần chạy thật lộ ngay class/nhãn thật. Dòng không có mã đơn nào → bỏ im lặng (dòng lạ).
    /// Mã đơn TRÙNG trong lô → chỉ giữ cặp ĐẦU (danh sách đã sắp mới→cũ nên cặp đầu là mới nhất).
    /// <para>
    /// Dòng có <see cref="DongTraHang.LaTraHang"/> = <c>false</c> (href <c>/portal/sale/order/…</c> ⇒ ĐƠN HỦY) bị
    /// BỎ ngay, đếm vào <see cref="KetQuaGhepTraHang.BoQuaDonHuy"/> để log. Cờ <c>null</c> (extension đời cũ chưa
    /// gửi) → GIỮ như trước: chốt chặn mới không được làm câm bản client chưa cập nhật.
    /// </para>
    /// </summary>
    public static KetQuaGhepTraHang GhepCap(IEnumerable<DongTraHang> dong)
    {
        var cap = new List<YeuCauTraHang>();
        var thieu = new List<string>();
        var daThay = new HashSet<string>(StringComparer.Ordinal);
        var boQuaDonHuy = 0;

        foreach (var d in dong ?? Array.Empty<DongTraHang>())
        {
            if (d is null)
            {
                continue;
            }
            if (d.LaTraHang == false)
            {
                boQuaDonHuy++;
                continue; // href nói rõ đây là dòng ĐƠN HỦY → không có mã yêu cầu, đừng phí công tách
            }

            var ma = TachMa(d.HeadHtml);
            if (string.IsNullOrEmpty(ma.MaDon))
            {
                continue; // không đọc được mã đơn → dòng lạ, không ghép được vào đơn nào
            }
            if (!daThay.Add(ma.MaDon!))
            {
                continue;
            }

            if (string.IsNullOrEmpty(ma.MaYeuCau))
            {
                thieu.Add(MoTaChanDoan(ma.MaDon!, ma.ClassKhoi, ma.Nhan, d.HeadHtml));
                continue;
            }
            cap.Add(new YeuCauTraHang(ma.MaDon!, ma.MaYeuCau!));
        }

        return new KetQuaGhepTraHang(cap, thieu, boQuaDonHuy);
    }

    /// <summary>
    /// Số ngày lùi tối đa (theo <b>NGÀY YÊU CẦU</b>, suy từ 6 ký tự đầu mã yêu cầu) còn được lấy mã trả hàng.
    /// <para>
    /// <b>20 = 15 ngày chính sách trả hàng của Shopee + biên.</b> CỐ Ý là hằng RIÊNG, KHÔNG dùng chung
    /// <see cref="UocTinhDon.SoNgayBuUocTinh"/> (7 ngày): con số đó đo trên NGÀY ĐẶT ĐƠN cho việc lấy bù
    /// "Số tiền cuối cùng" — khác trục, khác ý nghĩa, sửa một bên không được kéo bên kia theo.
    /// </para>
    /// </summary>
    public const int SoNgayCuaSoTraHang = 20;

    /// <summary>
    /// Lọc các cặp vừa <see cref="GhepCap"/> theo CỬA SỔ <b>NGÀY YÊU CẦU</b>: giữ cặp có ngày yêu cầu (suy từ 6 ký
    /// tự đầu MÃ YÊU CẦU, <see cref="UocTinhDon.NgayDonTuMa"/> — mã yêu cầu cũng mở đầu bằng <c>yyMMdd</c>,
    /// vd <c>2607280TS2VYAW3</c> → 28/07) không cũ hơn <paramref name="homNay"/> trừ <paramref name="soNgay"/> ngày.
    /// Biên ĐÓNG (đúng <paramref name="soNgay"/> ngày vẫn giữ).
    /// <para>
    /// <b>Vì sao đo trên MÃ YÊU CẦU chứ không mã đơn:</b> Shopee cho trả hàng trong 15 ngày, nên một yêu cầu HÔM
    /// NAY có thể thuộc đơn đặt từ rất lâu. Đo trên ngày ĐẶT ĐƠN là vứt đúng những mã vừa phát sinh của đơn cũ —
    /// mà từ khi mã trả hàng có bảng sống độc lập (<c>return_codes</c>), đơn còn hay đã bị dọn KHÔNG còn quan trọng.
    /// </para>
    /// <para>
    /// <b>⚠ Vì sao LỌC chứ không DỪNG SỚM:</b> danh sách trên trang sắp theo ngày yêu cầu mới → cũ, nhưng KHÔNG
    /// đơn điệu tuyệt đối (sắp xếp có thể không áp được — xem <see cref="KetQuaDocTraHang.SortApplied"/>). Gặp một
    /// dòng quá hạn mà <c>break</c> là cắt mất các dòng SAU vẫn còn trong hạn.
    /// </para>
    /// <para>
    /// Mã yêu cầu KHÔNG suy được ngày → <b>GIỮ</b> (thà thừa còn hơn mất mã) và đếm riêng để log. KHÁC với luật cũ
    /// đo trên mã đơn: mã yêu cầu chính là thứ ta cần lấy, không được mạnh tay loại vì một khuôn mã lạ.
    /// </para>
    /// </summary>
    public static KetQuaLocCuaSo LocTheoCuaSo(
        IEnumerable<YeuCauTraHang>? cap, DateTime homNay, int soNgay)
    {
        var giu = new List<YeuCauTraHang>();
        var moc = homNay.Date.AddDays(-Math.Max(0, soNgay));
        var boCu = 0;
        var giuKhongRoNgay = 0;

        foreach (var c in cap ?? Array.Empty<YeuCauTraHang>())
        {
            if (c is null)
            {
                continue;
            }
            if (UocTinhDon.NgayDonTuMa(c.MaYeuCau) is not DateTime ngay)
            {
                giuKhongRoNgay++;
                giu.Add(c);
                continue;
            }
            if (ngay < moc)
            {
                boCu++;
                continue; // CỐ Ý không break — xem phần ⚠ ở doc
            }
            giu.Add(c);
        }

        return new KetQuaLocCuaSo(giu, boCu, giuKhongRoNgay);
    }

    /// <summary>Mã tách được từ MỘT khối đầu dòng + CLASS thẻ bao và NHÃN của từng khối (để chẩn đoán khi luật
    /// trượt). <see cref="ClassKhoi"/> và <see cref="Nhan"/> cùng độ dài, cùng thứ tự khối.</summary>
    internal sealed record MaTraHang(
        string? MaDon, string? MaYeuCau, IReadOnlyList<string> Nhan, IReadOnlyList<string> ClassKhoi);

    /// <summary>Loại khối suy ra từ CLASS thẻ bao — <see cref="Khong"/> = class không nói gì (phải xét nhãn).</summary>
    private enum LoaiKhoi
    {
        Khong,
        Don,
        YeuCau,
    }

    /// <summary>
    /// Tách <c>(mã đơn, mã yêu cầu)</c> từ HTML khối <c>.return-row-item-head</c>. Mỗi khối = một phần tử có
    /// class chứa token <c>id-content</c>; GIÁ TRỊ = text ngay trong phần tử đó. Phân loại khối theo 3 tầng,
    /// dừng ở tầng đầu tiên nói được:
    /// <list type="number">
    /// <item><b>CLASS thẻ bao</b> (ưu tiên cao nhất, đã xác nhận trên trang thật): thẻ mở gần nhất phía trước
    /// khối có class chứa token <c>return-id</c> → mã yêu cầu, token <c>order-id</c> → mã đơn.</item>
    /// <item><b>NHÃN</b> (dự phòng khi Shopee đổi class) = text của thẻ <c>&lt;span&gt;</c> MỞ gần nhất phía
    /// trước khối — với khuôn <c>&lt;span&gt;Mã đơn hàng&lt;/span&gt;&lt;span class="id-content"&gt;…</c> thì
    /// đó đúng là nhãn. CỐ Ý thu hẹp tới đúng một <c>&lt;span&gt;</c> chứ không lấy "mọi text từ khối trước":
    /// khối ĐẦU nằm ngay sau <c>&lt;div class="username"&gt;</c> nên cách cũ nuốt luôn TÊN NGƯỜI MUA, mà tên đó
    /// người dùng tự đặt — username kiểu "returnking88" sẽ khớp nhánh yêu cầu và gán mã ĐƠN HÀNG thành mã yêu
    /// cầu, tức ghi mã SAI lên Google Sheet. Phân loại theo nhãn đã BỎ DẤU + hạ chữ: chứa
    /// <c>yeu cau</c>/<c>return</c>/<c>request</c> → mã yêu cầu; chứa <c>ma don hang</c>/<c>order</c> → mã đơn.
    /// Xét nhánh YÊU CẦU TRƯỚC vì nhãn tiếng Anh của yêu cầu có thể chứa cả chữ "order" (vd "Return order ID"),
    /// còn nhãn mã đơn không bao giờ chứa "yêu cầu".</item>
    /// <item><b>VỊ TRÍ</b> (dự phòng CUỐI): đúng 2 khối mà KHÔNG khối nào xác định được bằng class LẪN nhãn →
    /// khối 1 = mã đơn, khối 2 = mã yêu cầu. CỐ Ý không dự phòng khi đã nhận ra được một khối: khối còn lại lúc
    /// đó có class/nhãn RÕ RÀNG không phải yêu cầu (vd "Mã vận đơn") — đoán bừa sẽ ghi mã SAI lên Google Sheet,
    /// tệ hơn là bỏ trống.</item>
    /// </list>
    /// </summary>
    internal static MaTraHang TachMa(string? headHtml)
    {
        var nhan = new List<string>();
        var classKhoi = new List<string>();
        var giaTri = new List<string>();
        var loai = new List<LoaiKhoi>();
        if (string.IsNullOrWhiteSpace(headHtml))
        {
            return new MaTraHang(null, null, nhan, classKhoi);
        }

        var cacThe = TheHtml.Matches(headHtml);
        var cuoiKhoiTruoc = 0;
        for (var k = 0; k < cacThe.Count; k++)
        {
            var m = cacThe[k];
            if (m.Value.StartsWith("</", StringComparison.Ordinal) || !ClassChuaToken(m.Value, "id-content"))
            {
                continue;
            }

            // Giá trị = text node ngay sau thẻ mở (khối .id-content chỉ chứa text).
            var batDau = m.Index + m.Length;
            var ketThuc = headHtml.IndexOf('<', batDau);
            if (ketThuc < 0)
            {
                ketThuc = headHtml.Length;
            }
            var value = BocText(headHtml.Substring(batDau, ketThuc - batDau));
            // Cả class thẻ bao lẫn nhãn đều chỉ dò trong khoảng [cuối khối trước → khối này): thẻ bao của khối
            // này luôn MỞ SAU giá trị khối trước, nên chặn thế là đủ và không bao giờ mượn nhầm của khối trước.
            var lop = ClassTheBao(cacThe, k, cuoiKhoiTruoc);
            var nhanKhoi = NhanGanNhat(headHtml, cacThe, k, cuoiKhoiTruoc);
            // Dời mốc kể cả khi khối RỖNG (Vue chưa render): nếu không, nhãn của khối rỗng sẽ DÍNH vào nhãn khối
            // kế → phân loại sai (vd nhãn "Mã yêu cầu" của khối rỗng kéo theo giá trị "Mã vận đơn" của khối sau).
            cuoiKhoiTruoc = ketThuc;
            if (value.Length == 0)
            {
                continue; // khối rỗng → không phải mã nào
            }

            nhan.Add(nhanKhoi);
            classKhoi.Add(lop);
            giaTri.Add(value);
            loai.Add(LoaiTheoClass(lop));
        }

        // Tầng 1 — CLASS.
        string? maDon = null, maYeuCau = null;
        for (var i = 0; i < giaTri.Count; i++)
        {
            if (maYeuCau is null && loai[i] == LoaiKhoi.YeuCau)
            {
                maYeuCau = giaTri[i];
            }
            else if (maDon is null && loai[i] == LoaiKhoi.Don)
            {
                maDon = giaTri[i];
            }
        }

        // Tầng 2 — NHÃN, CHỈ cho khối mà class không nói gì (class đã nói thì class thắng, khỏi xét lại).
        for (var i = 0; i < giaTri.Count; i++)
        {
            if (loai[i] != LoaiKhoi.Khong)
            {
                continue;
            }
            var n = KhongDau(nhan[i]);
            if (maYeuCau is null && LaNhanYeuCau(n))
            {
                maYeuCau = giaTri[i];
            }
            else if (maDon is null && LaNhanDon(n))
            {
                maDon = giaTri[i];
            }
        }

        // Tầng 3 — dự phòng: đúng 2 khối, không khối nào nhận ra được → theo VỊ TRÍ (mã đơn luôn đứng trước).
        if (maDon is null && maYeuCau is null && giaTri.Count == 2)
        {
            maDon = giaTri[0];
            maYeuCau = giaTri[1];
        }

        return new MaTraHang(maDon, maYeuCau, nhan, classKhoi);
    }

    /// <summary>Class của thẻ BAO khối: thẻ MỞ gần nhất phía trước khối <paramref name="viTriKhoi"/> (không lùi
    /// quá mốc <paramref name="tu"/>) có class chứa token <c>return-id</c> hoặc <c>order-id</c>. Rỗng nếu không
    /// có thẻ nào như thế — lúc đó việc phân loại rơi xuống tầng nhãn.
    /// <para>CỐ Ý không ràng buộc tên thẻ phải là <c>div</c>: token class mới là dấu hiệu, Shopee đổi
    /// <c>div</c> thành thẻ khác vẫn nhận ra được.</para></summary>
    private static string ClassTheBao(MatchCollection cacThe, int viTriKhoi, int tu)
    {
        for (var j = viTriKhoi - 1; j >= 0 && cacThe[j].Index >= tu; j--)
        {
            var the = cacThe[j].Value;
            if (the.StartsWith("</", StringComparison.Ordinal))
            {
                continue;
            }
            var cls = LayClass(the);
            if (CoToken(cls, "return-id") || CoToken(cls, "order-id"))
            {
                return cls;
            }
        }
        return string.Empty;
    }

    /// <summary>Loại khối theo class thẻ bao (xem <see cref="ClassTheBao"/>).</summary>
    private static LoaiKhoi LoaiTheoClass(string cls)
    {
        if (CoToken(cls, "return-id"))
        {
            return LoaiKhoi.YeuCau;
        }
        return CoToken(cls, "order-id") ? LoaiKhoi.Don : LoaiKhoi.Khong;
    }

    /// <summary>NHÃN của khối: text từ thẻ <c>&lt;span&gt;</c> MỞ gần nhất phía trước khối
    /// <paramref name="viTriKhoi"/> (không lùi quá mốc <paramref name="tu"/>) tới thẻ mở của khối. Rỗng nếu
    /// không có <c>&lt;span&gt;</c> nào → coi như không khớp nhãn.</summary>
    private static string NhanGanNhat(string headHtml, MatchCollection cacThe, int viTriKhoi, int tu)
    {
        var mocKhoi = cacThe[viTriKhoi].Index;
        for (var j = viTriKhoi - 1; j >= 0 && cacThe[j].Index >= tu; j--)
        {
            var the = cacThe[j].Value;
            if (!LaTheMoSpan(the))
            {
                continue;
            }
            var batDau = cacThe[j].Index + the.Length;
            return batDau <= mocKhoi ? BocText(headHtml.Substring(batDau, mocKhoi - batDau)) : string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Thẻ MỞ <c>&lt;span…&gt;</c> (không tính <c>&lt;/span&gt;</c>, không dính <c>&lt;spanx&gt;</c>).</summary>
    private static bool LaTheMoSpan(string the)
        => the.Length > 5
           && the.StartsWith("<span", StringComparison.OrdinalIgnoreCase)
           && (the[5] == '>' || the[5] == '/' || char.IsWhiteSpace(the[5]));

    /// <summary>Nhãn của khối MÃ YÊU CẦU TRẢ HÀNG (nhãn đã bỏ dấu + hạ chữ).</summary>
    private static bool LaNhanYeuCau(string nhanKhongDau)
        => nhanKhongDau.Contains("yeu cau", StringComparison.Ordinal)
           || nhanKhongDau.Contains("return", StringComparison.Ordinal)
           || nhanKhongDau.Contains("request", StringComparison.Ordinal);

    /// <summary>Nhãn của khối MÃ ĐƠN HÀNG (nhãn đã bỏ dấu + hạ chữ).</summary>
    private static bool LaNhanDon(string nhanKhongDau)
        => nhanKhongDau.Contains("ma don hang", StringComparison.Ordinal)
           || nhanKhongDau.Contains("order", StringComparison.Ordinal);

    /// <summary>Thẻ mở <paramref name="the"/> có class chứa ĐÚNG token <paramref name="token"/> không (so theo
    /// token, không phải "chứa chuỗi" — <c>id-content-x</c> KHÔNG khớp <c>id-content</c>).</summary>
    private static bool ClassChuaToken(string the, string token) => CoToken(LayClass(the), token);

    /// <summary>Giá trị thuộc tính <c>class</c> của một thẻ mở (rỗng nếu thẻ không có class).</summary>
    private static string LayClass(string the)
    {
        var m = ThuocTinhClass.Match(the);
        if (!m.Success)
        {
            return string.Empty;
        }
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    /// <summary>Chuỗi class <paramref name="cls"/> có ĐÚNG token <paramref name="token"/> không.</summary>
    private static bool CoToken(string cls, string token)
    {
        if (cls.Length == 0)
        {
            return false;
        }
        foreach (var t in cls.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Bóc HẾT thẻ HTML (kể cả <c>&lt;!----&gt;</c>), giải mã thực thể (<c>&amp;nbsp;</c>…) rồi gộp
    /// khoảng trắng + trim. Dùng cho cả nhãn lẫn giá trị.</summary>
    private static string BocText(string html)
    {
        var text = System.Net.WebUtility.HtmlDecode(TheHtml.Replace(html, " "));
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>Bỏ dấu tiếng Việt + hạ chữ + gộp khoảng trắng (đề phòng giao diện đổi ngôn ngữ / đổi chữ hoa).
    /// <para>Thân nằm ở <see cref="MsLoginSelectors.NormalizeForMatch"/> (shared/Shopee.Toolkit) — DÙNG CHUNG với
    /// <c>LoginParsers.NormalizeForMatch</c> và phía Hub/BigSeller (<c>HotmailOtpReader</c>); trước đây đây là bản
    /// chép thứ ba. Bản chung hạ chữ ở BƯỚC CUỐI thay vì bước đầu — cùng kết quả (Đ tách thành D rồi mới hạ, đ có
    /// nhánh riêng), đã đối chiếu bằng <c>TraHangParserTests</c>.</para></summary>
    internal static string KhongDau(string? s) => MsLoginSelectors.NormalizeForMatch(s);

    /// <summary>Chuỗi CHẨN ĐOÁN cho dòng thiếu mã yêu cầu: mã đơn + CLASS/NHÃN dò được của từng khối + HTML thô
    /// (cắt bớt) — nhật ký lần chạy thật nhìn vào đây là biết luật trượt ở tầng nào và cấu trúc thật là gì.</summary>
    private static string MoTaChanDoan(
        string maDon, IReadOnlyList<string> classKhoi, IReadOnlyList<string> nhan, string headHtml)
    {
        var html = headHtml.Length > TranHtmlChanDoan
            ? headHtml.Substring(0, TranHtmlChanDoan) + "…(cắt)"
            : headHtml;
        var khoi = new List<string>(nhan.Count);
        for (var i = 0; i < nhan.Count; i++)
        {
            khoi.Add($"class='{(i < classKhoi.Count ? classKhoi[i] : string.Empty)}' nhãn='{nhan[i]}'");
        }
        return $"{maDon}: khối=[{string.Join(" | ", khoi)}] html={html}";
    }
}
