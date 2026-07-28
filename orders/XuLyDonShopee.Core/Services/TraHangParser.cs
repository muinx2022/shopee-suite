using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
/// </para></summary>
public sealed record DongTraHang(string? ShopeeOrderId, string HeadHtml);

/// <summary>Kết quả một lượt đọc trang trả hàng (đã parse JSON extension gửi). <see cref="SoYeuCau"/> null =
/// không đọc được số (text lạ); <see cref="SortApplied"/> false = KHÔNG đổi được sắp xếp sang "Ngày yêu cầu
/// (Mới - Cũ)" nên "N dòng đầu" có thể sai — caller phải log cảnh báo.</summary>
public sealed record KetQuaDocTraHang(int? SoYeuCau, bool SortApplied, IReadOnlyList<DongTraHang> Dong);

/// <summary>Kết quả ghép cặp: <see cref="Cap"/> = dòng đủ hai mã; <see cref="ThieuMaYeuCau"/> = mô tả CHẨN ĐOÁN
/// (mã đơn + class/nhãn đọc được của từng khối + HTML thô rút gọn) của dòng CÓ mã đơn mà KHÔNG có mã yêu cầu.</summary>
public sealed record KetQuaGhepTraHang(IReadOnlyList<YeuCauTraHang> Cap, IReadOnlyList<string> ThieuMaYeuCau);

/// <summary>4 nhánh luật đếm số yêu cầu (xem <see cref="TraHangParser.QuyetDinhCheck"/>).</summary>
public enum LuatSoYeuCau
{
    /// <summary>Chưa có mốc (shop này chưa từng check) → CHỈ ghi nhớ số, không check dòng nào.</summary>
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

    /// <summary>Mọi thẻ HTML (kể cả <c>&lt;!----&gt;</c> của Vue) — dùng để cắt biên khối và bóc text nhãn.</summary>
    private static readonly Regex TheHtml = new("<[^>]*>", RegexOptions.Compiled);

    /// <summary>Thuộc tính <c>class</c> trong một thẻ mở (nháy kép hoặc nháy đơn).</summary>
    private static readonly Regex ThuocTinhClass =
        new("class\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Luật đếm ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Luật người dùng chốt: <paramref name="mocCu"/> null (lần đầu) → CHỈ ghi nhớ; số không đổi → bỏ qua;
    /// số GIẢM → chỉ cập nhật mốc; số TĂNG k → check k dòng ĐẦU. Ba nhánh đầu đều trả
    /// <see cref="QuyetDinhTraHang.SoDongCanCheck"/> = 0 nhưng GIỮ nhánh riêng để log/nghiệm thu phân biệt được.
    /// <paramref name="soMoi"/> âm (rác) được kẹp về 0.
    /// </summary>
    public static QuyetDinhTraHang QuyetDinhCheck(int? mocCu, int soMoi)
    {
        var moi = Math.Max(0, soMoi);
        if (mocCu is null)
        {
            return new QuyetDinhTraHang(LuatSoYeuCau.LanDau, 0);
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
    /// Parse JSON <c>{soYeuCauText, sortApplied, list:[{shopeeOrderId, headHtml}]}</c> extension gửi kèm
    /// <c>pageData kind="returns"</c>. JSON rỗng/hỏng/thiếu field → kết quả RỖNG (<see cref="KetQuaDocTraHang.SoYeuCau"/>
    /// null, danh sách rỗng) chứ KHÔNG ném — bước này là bước phụ, hỏng thì đi tiếp.
    /// </summary>
    public static KetQuaDocTraHang ParseKetQua(string? json)
    {
        var rong = new KetQuaDocTraHang(null, false, Array.Empty<DongTraHang>());
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
                    dong.Add(new DongTraHang(string.IsNullOrWhiteSpace(soi) ? null : soi, html!));
                }
            }

            return new KetQuaDocTraHang(soYeuCau, sortApplied, dong);
        }
        catch (JsonException)
        {
            return rong; // extension gửi rác → coi như không đọc được, bước sau tự bỏ qua
        }
    }

    // ── Tách + ghép cặp mã ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghép cặp <c>(mã đơn, mã yêu cầu)</c> cho các dòng trong <paramref name="dong"/>: dòng đủ CẢ HAI mã vào
    /// <see cref="KetQuaGhepTraHang.Cap"/>; dòng CÓ mã đơn mà THIẾU mã yêu cầu vào
    /// <see cref="KetQuaGhepTraHang.ThieuMaYeuCau"/> dưới dạng chuỗi chẩn đoán (mã đơn + nhãn đọc được + HTML thô
    /// rút gọn) để nhật ký lần chạy thật lộ ngay class/nhãn thật. Dòng không có mã đơn nào → bỏ im lặng (dòng lạ).
    /// Mã đơn TRÙNG trong lô → chỉ giữ cặp ĐẦU (danh sách đã sắp mới→cũ nên cặp đầu là mới nhất).
    /// </summary>
    public static KetQuaGhepTraHang GhepCap(IEnumerable<DongTraHang> dong)
    {
        var cap = new List<YeuCauTraHang>();
        var thieu = new List<string>();
        var daThay = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in dong ?? Array.Empty<DongTraHang>())
        {
            if (d is null)
            {
                continue;
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

        return new KetQuaGhepTraHang(cap, thieu);
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

    /// <summary>Bỏ dấu tiếng Việt + hạ chữ + gộp khoảng trắng (đề phòng giao diện đổi ngôn ngữ / đổi chữ hoa).</summary>
    internal static string KhongDau(string? s)
    {
        var norm = ShopeeShippingNav.NormalizeUiText(s);
        if (norm.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue; // dấu thanh
            }
            sb.Append(ch == 'đ' ? 'd' : ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

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
