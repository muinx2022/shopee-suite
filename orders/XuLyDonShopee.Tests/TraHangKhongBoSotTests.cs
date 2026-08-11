using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Khoá lại 4 đường LÀM MẤT MÃ TRẢ HÀNG đã vá 09/08/2026 (plan
/// <c>plans/2026-08-09-check-tra-hang-khong-bo-sot.md</c>). Mỗi test ở đây tương ứng một đường mất mã THẬT đã
/// quan sát được, không phải ca giả định:
/// <list type="number">
/// <item><b>Số không đổi / giảm ⇒ vứt sạch dòng đã cào được.</b> Con số trên trang là MỨC TỒN, không phải bộ
/// đếm cộng dồn — "+3 mới, −3 xử xong" ra số y hệt. Bản cũ cắt danh sách theo hiệu số rồi VẪN ghi mốc ⇒ mất
/// vĩnh viễn.</item>
/// <item><b>Chỉ trang đầu.</b> Shop tồn 141/340 yêu cầu chốt mốc ngay lượt đầu, phần sâu không bao giờ đọc tới.</item>
/// <item><b>Sheet nuốt mã.</b> Script trả <c>ok:true</c> kèm <c>boQua:true</c> khi không tra thấy dòng đơn;
/// client hiểu thành "đã ghi" rồi không bao giờ thử lại.</item>
/// <item><b>Shop hỏng sớm ⇒ bỏ luôn bước.</b> Không đặt được địa chỉ lấy hàng thì shop đó không bao giờ được
/// check trả hàng, dù trang trả hàng chẳng liên quan gì tới địa chỉ.</item>
/// </list>
/// </summary>
public class TraHangKhongBoSotTests
{
    private const string Tinh = "Thanh Hóa";
    private static readonly DateTime Luc = new(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc);

    private static ShopFlowRunner Runner(
        BridgeTestRig rig,
        Func<string, int?>? returnCountLast = null,
        Action<string, int>? saveReturnCount = null,
        Func<IReadOnlyList<YeuCauTraHang>, string>? saveReturnCodes = null,
        Func<IReadOnlyList<YeuCauTraHang>, int>? demMaTraChuaBiet = null,
        Func<string, bool>? conSotTraHang = null,
        Action<string, bool, string>? luuConSotTraHang = null)
        => new(rig.Channel, rig.Log, invoiceDir: null, Tinh, syncCallback: null, finalDoneSns: null,
            onOrderPrepared: null, returnCountLast, saveReturnCount, saveReturnCodes,
            layDonThieuPhieu: null, demMaTraChuaBiet: demMaTraChuaBiet, dangCoCanhBaoDiaChi: null,
            conSotTraHang: conSotTraHang, luuConSotTraHang: luuConSotTraHang);

    /// <summary>HTML đầu dòng đúng khuôn trang thật (class <c>order-id</c> / <c>return-id</c>).</summary>
    private static string DongHtml(string maDon, string maYeuCau) =>
        "<div class=\"id order-id\"><span>Mã đơn hàng</span><span class=\"id-content\">" + maDon + "</span></div>"
        + "<div class=\"id return-id\"><span>Mã yêu cầu trả hàng</span><span class=\"id-content\">" + maYeuCau + "</span></div>";

    /// <summary>Mã yêu cầu của HÔM NAY — cửa sổ lọc đo trên ngày yêu cầu nên mã cứng sẽ hết hạn theo thời gian.</summary>
    private static string MaYeuCauHomNay(string duoi = "0TS2VYAW3") => DateTime.Now.ToString("yyMMdd") + duoi;

    private static object TrangTraHang(
        string soYeuCauText, bool tabTraHang = true, bool sortApplied = true, bool coTrangSau = false,
        params object[] dong) => new
        {
            action = "pageData",
            kind = "returns",
            data = new { soYeuCauText, sortApplied, tabTraHang, coTrangSau, list = dong },
        };

    // ===================== 1. Số không đổi / giảm VẪN phải lưu mã =====================

    /// <summary>
    /// ĐƯỜNG MẤT MÃ LỚN NHẤT. Mốc 7, đọc lại vẫn 7 — nhưng trong danh sách có một yêu cầu MỚI (yêu cầu cũ đã xử
    /// xong nên rớt ra, số bù trừ về đúng 7). Bản cũ: nhánh <c>KhongDoi</c> → <c>Take(0)</c> → vứt sạch dòng
    /// extension ĐÃ cào được, rồi vẫn ghi mốc ⇒ mã đó không bao giờ được đọc lại.
    /// </summary>
    [Fact]
    public async Task SoKhongDoi_VanParseHetDong_VaLuuMa()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => 7,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return $"{cap.Count} mã"; });

        var ma = MaYeuCauHomNay();
        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("7 Yêu cầu", dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", ma), laTraHang = true } }));
        await chay;

        Assert.Equal(new YeuCauTraHang("260731AAAAAA", ma), Assert.Single(capDaLuu));
        Assert.Equal(("shop1", 7), Assert.Single(moc));
    }

    /// <summary>Số GIẢM (2 yêu cầu xử xong, 1 yêu cầu mới) — vẫn phải lưu mã mới.</summary>
    [Fact]
    public async Task SoGiam_VanParseHetDong_VaLuuMa()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => 141,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return "ok"; });

        var ma = MaYeuCauHomNay();
        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("140 Yêu cầu", dong:
            new object[] { new { headHtml = DongHtml("260731BBBBBB", ma), laTraHang = true } }));
        await chay;

        Assert.Equal(new YeuCauTraHang("260731BBBBBB", ma), Assert.Single(capDaLuu));
    }

    /// <summary>Nhánh TĂNG k: đọc HẾT dòng chứ không chỉ k dòng đầu. Tăng 1 mà trang có 3 dòng mới (2 yêu cầu
    /// cũ vừa xử xong bù trừ vào hiệu số) ⇒ phải lưu cả 3.</summary>
    [Fact]
    public async Task SoTang1_NhungTrangCo3DongMoi_LuuCa3()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => 7,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return "ok"; });

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("8 Yêu cầu", dong: new object[]
        {
            new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay("0AAAAAAAA")), laTraHang = true },
            new { headHtml = DongHtml("260731BBBBBB", MaYeuCauHomNay("0BBBBBBBB")), laTraHang = true },
            new { headHtml = DongHtml("260731CCCCCC", MaYeuCauHomNay("0CCCCCCCC")), laTraHang = true },
        }));
        await chay;

        Assert.Equal(3, capDaLuu.Count);
    }

    /// <summary>
    /// Một ĐƠN có HAI yêu cầu trả hàng (khách bấm lại sau khi yêu cầu trước bị hủy/từ chối, hoặc đơn nhiều
    /// phần). Luật user chốt 09/08: <b>giữ mã MỚI NHẤT</b> (dòng đầu — danh sách sắp mới→cũ), mã còn lại KHÔNG
    /// lưu nhưng phải RA NHẬT KÝ. Trước đây dòng thứ hai bị bỏ IM LẶNG, nhìn log không biết đơn đó có hai mã.
    /// </summary>
    [Fact]
    public async Task MotDonHaiYeuCau_GiuMaMoiNhat_VaGhiNhatKy()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => 7,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return "ok"; });

        var maMoi = MaYeuCauHomNay("0MOIMOIMO");
        var maCu = MaYeuCauHomNay("0CUCUCUCU");
        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("7 Yêu cầu", dong: new object[]
        {
            new { headHtml = DongHtml("260731AAAAAA", maMoi), laTraHang = true },   // dòng trên = mới nhất
            new { headHtml = DongHtml("260731AAAAAA", maCu), laTraHang = true },    // cùng đơn, yêu cầu cũ
        }));
        await chay;

        Assert.Equal(new YeuCauTraHang("260731AAAAAA", maMoi), Assert.Single(capDaLuu));
        Assert.True(rig.CoLog("NHIỀU HƠN MỘT yêu cầu"));
        Assert.True(rig.CoLog($"BỎ {maCu}"));   // nhật ký nói RÕ mã nào bị bỏ, không chỉ đếm
    }

    // ===================== 2. Phân trang =====================

    /// <summary>
    /// <b>KHOÁ LỖI PHẢN BIỆN BẮT 09/08:</b> shop ĐÃ CÓ MỐC (mọi shop đang chạy đều thế — mốc được ghi mỗi lượt
    /// từ 29/07, không migration nào reset) mà số yêu cầu KHÔNG ĐỔI ⇒ bản vá đầu tiên suy độ sâu từ mốc nên
    /// không lật trang nào, tức đúng nhóm shop tồn đọng lại không bao giờ được quét sâu. Nay độ sâu do DỮ LIỆU
    /// quyết định: trang đầu còn ra mã MỚI ⇒ phải lật tiếp, bất kể mốc.
    /// <para>Lật MỖI LẦN MỘT TRANG (<c>maxPages = 1</c>) rồi hỏi lại "trang này còn mã mới không" — dừng ngay
    /// khi hết, nên lượt thường không tốn vòng nào.</para>
    /// </summary>
    [Fact]
    public async Task CoMocRoi_TrangDauConMaMoi_VanLatTrang_VaGopDong()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var capDaLuu = new List<YeuCauTraHang>();
        var soLanLuu = 0;
        var daBiet = new HashSet<string>(StringComparer.Ordinal) { "0CCCCCCCC" }; // mã trang 3 đã có trong kho
        var flow = Runner(rig,
            returnCountLast: _ => 141,                       // ĐÃ có mốc; số đọc về cũng 141 ⇒ nhánh KhongDoi
            saveReturnCount: (_, _) => { },
            saveReturnCodes: cap => { soLanLuu++; capDaLuu.AddRange(cap); return "ok"; },
            demMaTraChuaBiet: cap => cap.Count(c => !daBiet.Contains(c.MaYeuCau.Substring(6))));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (var lenh = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequests", lenh.RootElement.GetProperty("action").GetString());
        }
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay("0AAAAAAAA")), laTraHang = true } }));

        // Trang 2: vẫn còn mã MỚI ⇒ phải xin tiếp trang nữa.
        using (var lenh2 = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequestsMore", lenh2.RootElement.GetProperty("action").GetString());
            Assert.Equal(1, lenh2.RootElement.GetProperty("maxPages").GetInt32());
        }
        await rig.GuiAsync(TrangTraHang("", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260730BBBBBB", MaYeuCauHomNay("0BBBBBBBB")), laTraHang = true } }));

        // Trang 3: toàn mã ĐÃ BIẾT ⇒ dừng lật (trang sau chỉ cũ hơn nữa).
        using (var lenh3 = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequestsMore", lenh3.RootElement.GetProperty("action").GetString());
        }
        await rig.GuiAsync(TrangTraHang("", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260729CCCCCC", MaYeuCauHomNay("0CCCCCCCC")), laTraHang = true } }));
        await chay;

        Assert.Equal(3, capDaLuu.Count);
        Assert.Equal(1, soLanLuu);   // GỘP: một lượt lưu cho cả ba trang (một lượt notify)
        // Không xin thêm trang thứ tư.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Lượt THƯỜNG: trang đầu không có mã nào mới ⇒ KHÔNG lật trang nào, chi phí đúng bằng trước đây.</summary>
    [Fact]
    public async Task TrangDauKhongCoMaMoi_KhongLatTrang()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = Runner(rig,
            returnCountLast: _ => 141,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: _ => "ok",
            demMaTraChuaBiet: _ => 0);                       // kho đã có hết

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true } }));
        await chay;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    /// <b>KHOÁ LỖI PHẢN BIỆN BẮT 09/08:</b> lượt đọc CHƯA đủ sâu (lật sang trang sau mà không đọc được dòng nào
    /// — bấm trượt nút / danh sách không vẽ lại / captcha) thì <b>KHÔNG được chốt mốc</b>. Chốt mốc lúc này là
    /// lặp lại đúng cái bẫy "mốc nhảy khi chưa đọc hết" mà cả đợt này đi vá.
    /// </summary>
    [Fact]
    public async Task LatTrangTruot_KhongChotMoc()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var flow = Runner(rig,
            returnCountLast: _ => 141,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: _ => "ok",
            demMaTraChuaBiet: _ => 1);                       // trang đầu còn mã mới ⇒ đòi lật tiếp

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true } }));

        using (await rig.NhanLenhAsync()) { }               // readReturnRequestsMore
        await rig.GuiAsync(TrangTraHang("", coTrangSau: false)); // lật trượt → 0 dòng
        await chay;

        Assert.Empty(moc);                                   // MỐC GIỮ NGUYÊN
        Assert.True(rig.CoLog("đọc CHƯA đủ sâu"));
    }

    /// <summary>KHÔNG đổi được sắp xếp ⇒ thứ tự trang là "Ngày đến hạn", lật trang chỉ là nhặt ngẫu nhiên trong
    /// lịch sử ⇒ CHỈ đọc trang đầu, KHÔNG gửi lượt đọc thêm.</summary>
    [Fact]
    public async Task KhongDoiDuocSapXep_KhongLatTrang()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = Runner(rig,
            returnCountLast: _ => null,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: _ => "ok");

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("340 Yêu cầu", sortApplied: false, coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true } }));
        await chay;

        Assert.True(rig.CoLog("chỉ đọc trang đầu"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Extension đời CŨ không gửi <c>coTrangSau</c> ⇒ không gửi lệnh <c>readReturnRequestsMore</c> mà
    /// nó không biết (gửi rồi ngồi chờ hết 90s là mất luôn phần trang đầu).</summary>
    [Fact]
    public async Task ExtensionDoiCu_KhongCoTrangSau_KhongGuiLenhMoi()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => null,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return "ok"; });

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        // Khuôn payload đời cũ: KHÔNG có field coTrangSau.
        await rig.GuiAsync(new
        {
            action = "pageData",
            kind = "returns",
            data = new
            {
                soYeuCauText = "340 Yêu cầu",
                sortApplied = true,
                tabTraHang = true,
                list = new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true } },
            },
        });
        await chay;

        Assert.Single(capDaLuu);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    // ===================== 2b. Ô tổng CÓ mà 0 dòng = hỏng THẬT (11/08/2026) =====================

    /// <summary>
    /// Selector dòng / khối đầu dòng đổi ⇒ ô tổng vẫn đọc được "33 yêu cầu" nhưng KHÔNG dòng nào ra. Bản trước
    /// bỏ nguyên khối lưu (điều kiện <c>dong.Count > 0</c>) mà KHÔNG log, rồi vẫn chốt mốc ⇒ mọi shop "trông
    /// khỏe" trong khi 0 mã nào được đọc, lặp lại mọi vòng. Phải: GIỮ mốc + cảnh báo + bật cờ còn sót.
    /// </summary>
    [Fact]
    public async Task OTongCoSo_MaKhongDocDuocDongNao_GiuMoc_CanhBao_BatCoConSot()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 30,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: _ => "ok",
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("33 Yêu cầu"));   // ô tổng CÓ số, list RỖNG
        await chay;

        Assert.Empty(moc);                                // MỐC GIỮ NGUYÊN
        Assert.Equal(("shop1", true, ShopFlowRunner.LyDoSotDocHong), Assert.Single(co)); // còn sót + đúng lý do
        Assert.True(rig.CoLog("KHÔNG đọc được dòng nào"));
    }

    /// <summary>ĐỐI CHỨNG: shop SẠCH thật (ô tổng 0, không dòng nào) phải giữ NGUYÊN hành vi cũ — mốc 0 ghi bình
    /// thường, không cảnh báo, không bật cờ.</summary>
    [Fact]
    public async Task OTongBang0_KhongCoDong_VanChotMoc0_KhongCanhBao()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 3,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: _ => "ok",
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("0 Yêu cầu"));
        await chay;

        Assert.Equal(("shop1", 0), Assert.Single(moc));
        Assert.Equal(("shop1", false, ""), Assert.Single(co));
        Assert.False(rig.CoLog("KHÔNG đọc được dòng nào"));
    }

    // ===================== 2c. Cờ "còn sót" bền theo shop: rút cạn tồn đọng (11/08/2026) =====================

    /// <summary>
    /// LƯỢT SAU của một shop từng chạm trần: trang đầu KHÔNG còn mã mới (phần đuôi bỏ lại nằm SAU dải-đã-biết,
    /// không bao giờ hiện ở trang đầu) — nếu chỉ nhìn "trang đầu còn mã mới" thì không lượt nào với tới nó nữa,
    /// tới lúc nó trôi lên thì đã quá cửa sổ 20 ngày ⇒ mất vĩnh viễn. Cờ bật ⇒ VẪN lật trang.
    /// Lượt này đọc tới ĐÁY (hết trang) ⇒ tắt cờ + chốt mốc.
    /// </summary>
    [Fact]
    public async Task ConSot_TrangDauKhongCoMaMoi_VanLatTrang_DocToiDay_TatCo()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => 141,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return "ok"; },
            demMaTraChuaBiet: _ => 0,                     // kho đã biết HẾT mã của mọi trang đọc được
            conSotTraHang: _ => true,                     // lượt trước còn sót
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay("0AAAAAAAA")), laTraHang = true } }));

        // Đúng chỗ bản cũ dừng: trang đầu 0 mã mới. Cờ bật ⇒ phải xin trang tiếp.
        using (var lenh2 = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequestsMore", lenh2.RootElement.GetProperty("action").GetString());
        }
        // Trang 2 CŨNG toàn mã cũ — phải đi XUYÊN QUA vùng đã biết mới tới phần đuôi lượt trước bỏ lại, nên
        // KHÔNG được dừng ở đây (bản cũ `break` ngay khi trang không còn mã mới).
        await rig.GuiAsync(TrangTraHang("", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260730BBBBBB", MaYeuCauHomNay("0BBBBBBBB")), laTraHang = true } }));
        using (var lenh3 = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequestsMore", lenh3.RootElement.GetProperty("action").GetString());
        }
        await rig.GuiAsync(TrangTraHang("", coTrangSau: false, dong:
            new object[] { new { headHtml = DongHtml("260729CCCCCC", MaYeuCauHomNay("0CCCCCCCC")), laTraHang = true } }));
        await chay;

        Assert.Equal(3, capDaLuu.Count);                    // gom cả ba trang
        Assert.Equal(("shop1", 141), Assert.Single(moc));   // đọc tới đáy ⇒ chốt mốc
        Assert.Equal(("shop1", false, ""), Assert.Single(co)); // TẮT cờ + xoá lý do — tồn đọng đã rút cạn
        Assert.True(rig.CoLog("RÚT TỒN"));
    }

    /// <summary>Lượt chạm TRẦN DÒNG: bỏ lại phần đuôi ⇒ giữ mốc + BẬT cờ để lượt sau rút tiếp.</summary>
    [Fact]
    public async Task ChamTranDong_GiuMoc_VaBatCoConSot()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 340,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: _ => "ok",
            demMaTraChuaBiet: cap => cap.Count,           // trang đầu toàn mã mới ⇒ đòi lật tiếp
            conSotTraHang: _ => false,
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        // Trang đầu đã đủ TRẦN DÒNG của cả lượt → vào vòng lật là chạm trần ngay, không xin thêm trang nào.
        var dongDay = Enumerable.Range(0, TraHangParser.TranDongMoiLuot)
            .Select(i => (object)new
            {
                headHtml = DongHtml($"260731{i:D8}", MaYeuCauHomNay($"0{i:D8}")),
                laTraHang = true,
            })
            .ToArray();

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("340 Yêu cầu", coTrangSau: true, dong: dongDay));
        await chay;

        Assert.Empty(moc);                                  // mốc GIỮ NGUYÊN
        Assert.Equal(("shop1", true, ShopFlowRunner.LyDoSotTran), Assert.Single(co)); // bật cờ, lý do = trần
        Assert.True(rig.CoLog($"chạm trần {TraHangParser.TranDongMoiLuot} dòng"));
    }

    /// <summary>Lật TRƯỢT (bấm trượt / danh sách không vẽ lại): đã giữ mốc từ trước, nay còn phải BẬT cờ — không
    /// thì lượt sau lại chỉ nhìn trang đầu và bỏ luôn phần chưa đọc.</summary>
    [Fact]
    public async Task LatTrangTruot_BatCoConSot()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 141,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: _ => "ok",
            demMaTraChuaBiet: _ => 1,
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true } }));
        using (await rig.NhanLenhAsync()) { }                    // readReturnRequestsMore
        await rig.GuiAsync(TrangTraHang("", coTrangSau: false)); // lật trượt → 0 dòng
        await chay;

        Assert.Equal(("shop1", true, ShopFlowRunner.LyDoSotLatTruot), Assert.Single(co));
    }

    /// <summary>ĐỐI CHỨNG giữ hành vi cũ: shop KHÔNG còn sót + trang đầu không có mã mới ⇒ vẫn KHÔNG lật trang
    /// (chi phí lượt thường không đổi), chốt mốc và ghi cờ tắt.</summary>
    [Fact]
    public async Task KhongConSot_TrangDauKhongCoMaMoi_VanKhongLatTrang()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 141,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: _ => "ok",
            demMaTraChuaBiet: _ => 0,
            conSotTraHang: _ => false,
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true } }));
        await chay;

        Assert.Equal(("shop1", false, ""), Assert.Single(co));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Cờ "còn sót" KHÔNG được vượt ràng buộc cũ: không đổi được sắp xếp thì thứ tự trang không tin
    /// được ⇒ vẫn CẤM lật trang (cùng lắm là bật cờ để lượt sau thử lại). Cờ ghi ra phải mang lý do
    /// <c>sap_xep</c> — chính là ca "cờ đứng im 1 qua các lượt" mà người vận hành cần nhìn thấy vì sao.</summary>
    [Fact]
    public async Task ConSot_NhungKhongDoiDuocSapXep_VanKhongLatTrang()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 141,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: _ => "ok",
            demMaTraChuaBiet: _ => 5,
            conSotTraHang: _ => true,
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", sortApplied: false, coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true } }));
        await chay;

        Assert.True(rig.CoLog("chỉ đọc trang đầu"));
        Assert.Equal(("shop1", true, ShopFlowRunner.LyDoSotSapXep), Assert.Single(co));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Chạm trần TRANG (lật đủ <c>TranTrangTraHang</c> trang mà chưa tới đáy): giữ mốc + bật cờ với lý
    /// do <c>tran</c>. Nhánh này trước không có test nào đi qua — ghi nhầm hằng lý do ở đó thì toàn bộ suite vẫn
    /// xanh (phản biện 11/08/2026).</summary>
    [Fact]
    public async Task ChamTranTrang_GiuMoc_BatCoConSot_LyDoTran()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 500,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: _ => "ok",
            demMaTraChuaBiet: cap => cap.Count,           // trang nào cũng toàn mã mới ⇒ không dừng vì "hết mới"
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        // Mỗi trang RẤT ÍT dòng để lật đủ 10 trang vẫn còn xa trần DÒNG (200) — ép đúng nhánh trần TRANG.
        await rig.GuiAsync(TrangTraHang("500 Yêu cầu", coTrangSau: true, dong:
            new object[] { new { headHtml = DongHtml("260731ZZZZZZ", MaYeuCauHomNay("0ZZZZZZZZ")), laTraHang = true } }));
        for (var trang = 0; trang < TraHangParser.TranTrangTraHang; trang++)
        {
            using (var lenh = await rig.NhanLenhAsync())
            {
                Assert.Equal("readReturnRequestsMore", lenh.RootElement.GetProperty("action").GetString());
            }
            await rig.GuiAsync(TrangTraHang("", coTrangSau: true, dong:
                new object[] { new { headHtml = DongHtml($"260731{trang:D6}", MaYeuCauHomNay($"1{trang:D8}")), laTraHang = true } }));
        }
        await chay;

        Assert.Empty(moc);                                  // mốc GIỮ NGUYÊN
        Assert.Equal(("shop1", true, ShopFlowRunner.LyDoSotTran), Assert.Single(co));
        Assert.True(rig.CoLog($"chạm trần {TraHangParser.TranTrangTraHang} trang"));
    }

    /// <summary>Bảng mã lý do → lời người-đọc-được: mỗi mã phải ra đúng lời của nó, và mã LẠ (kể cả chuỗi rỗng —
    /// điểm hạ cờ mới quên gán) phải TỰ TỐ trong nhật ký thay vì in câu trống vô nghĩa.</summary>
    [Theory]
    [InlineData(ShopFlowRunner.LyDoSotTran, "chạm trần")]
    [InlineData(ShopFlowRunner.LyDoSotSapXep, "sắp xếp")]
    [InlineData(ShopFlowRunner.LyDoSotLatTruot, "lật trang trượt")]
    [InlineData(ShopFlowRunner.LyDoSotDocHong, "không đọc được dòng nào")]
    public void MoTaLyDoSot_RaDungLoi(string ma, string mongChua)
        => Assert.Contains(mongChua, ShopFlowRunner.MoTaLyDoSot(ma));

    [Fact]
    public void MoTaLyDoSot_MaLaHoacRong_TuToLaLoiCode()
    {
        Assert.Contains("không rõ lý do", ShopFlowRunner.MoTaLyDoSot(""));
        Assert.Contains("không rõ lý do", ShopFlowRunner.MoTaLyDoSot("ma_la_hoac_go_sai"));
    }

    /// <summary>Hỏng sắp xếp VÀ 0 dòng trong CÙNG một lượt: lý do ghi ra phải là <c>doc_hong</c> (đè
    /// <c>sap_xep</c>) — "không đọc được dòng nào" là chẩn đoán sắc hơn "không đổi được thứ tự".</summary>
    [Fact]
    public async Task SapXepHong_VaKhongDocDuocDongNao_LyDoLaDocHong()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var co = new List<(string Shop, bool ConSot, string LyDo)>();
        var flow = Runner(rig,
            returnCountLast: _ => 30,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: _ => "ok",
            luuConSotTraHang: (shop, conSot, lyDo) => co.Add((shop, conSot, lyDo)));

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("33 Yêu cầu", sortApplied: false, coTrangSau: true)); // 0 dòng
        await chay;

        Assert.Equal(("shop1", true, ShopFlowRunner.LyDoSotDocHong), Assert.Single(co));
        Assert.True(rig.CoLog("KHÔNG đọc được dòng nào"));
    }

    // ===================== 2d. Lượt BỎ vì sai tab vẫn VỚT mã thật (T2, review 11/08) =====================

    /// <summary>Extension KHÔNG chọn được tab (tabTraHang=false) ⇒ BỎ LƯỢT bảo vệ mốc — nhưng dòng trả hàng
    /// THẬT đã cào được (laTraHang=true, ghép đủ hai mã) phải được VỚT vào kho, không vứt theo lượt: vứt là mất
    /// tới khi mã trôi khỏi cửa sổ 20 ngày.</summary>
    [Fact]
    public async Task BoLuotSaiTab_VanVotMaThatDaCaoDuoc_MocGiuNguyen()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => 5,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return $"{cap.Count} mã"; });

        var ma = MaYeuCauHomNay();
        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("9 Yêu cầu", tabTraHang: false, dong:
            new object[] { new { headHtml = DongHtml("260811BBBBBB", ma), laTraHang = true } }));
        await chay;

        Assert.Empty(moc);                                                        // mốc KHÔNG được ghi
        Assert.Equal(new YeuCauTraHang("260811BBBBBB", ma), Assert.Single(capDaLuu)); // mã VẪN được vớt
        Assert.True(rig.CoLog("VỚT"));
    }

    /// <summary>Chốt NGHI SAI TAB THEO DỮ LIỆU (đa số dòng là đơn hủy) cũng phải vớt phần trả hàng thật.</summary>
    [Fact]
    public async Task NghiSaiTabTheoDuLieu_VanVotMaThat_MocGiuNguyen()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => 5,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return $"{cap.Count} mã"; });

        var ma = MaYeuCauHomNay();
        // 10 dòng: 9 đơn hủy + 1 trả hàng thật → NghiSaiTabTheoDuLieu nổ (đủ sàn 10, tỷ lệ 0,9 ≥ 0,8).
        var dong = new List<object>();
        for (var i = 0; i < 9; i++)
        {
            dong.Add(new { headHtml = DongHtml($"260811{i:D6}", ""), laTraHang = false });
        }
        dong.Add(new { headHtml = DongHtml("260811CCCCCC", ma), laTraHang = true });

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }
        await rig.GuiAsync(TrangTraHang("30 Yêu cầu", dong: dong.ToArray()));
        await chay;

        Assert.Empty(moc);
        Assert.Equal(new YeuCauTraHang("260811CCCCCC", ma), Assert.Single(capDaLuu));
        Assert.True(rig.CoLog("VỚT"));
    }

    // ===================== 3. Sheet không nuốt mã =====================

    /// <summary>Script trả <c>ok:true</c> + <c>boQua:true</c> (không tra thấy mã đơn ở tab nào) ⇒ phải đọc được
    /// thành <see cref="GsheetOrderResult.BoQua"/> để caller KHÔNG đánh dấu đã-đẩy.</summary>
    [Fact]
    public void DocKetQua_BoQua_DocDuocCo()
    {
        var kq = GoogleSheetSyncService.DocKetQua(
            "{\"results\":[{\"maDon\":\"D1\",\"ok\":true,\"boQua\":true},{\"maDon\":\"D2\",\"ok\":true}]}");

        Assert.True(kq[0].Ok);
        Assert.True(kq[0].BoQua);
        Assert.True(kq[1].Ok);
        Assert.False(kq[1].BoQua);
    }

    /// <summary>Thiếu TIÊU ĐỀ CỘT: script vẫn trả ok:true nhưng đã BỎ giá trị. Với lô mã-trả (chỉ ghi đúng cột
    /// đó) thì cảnh báo này = chưa ghi được gì.</summary>
    [Fact]
    public void DocCanhBao_ThieuTieuDeCot_DocDuoc()
    {
        Assert.Contains("mã đơn trả hàng", GoogleSheetSyncService.DocCanhBao(
            "{\"results\":[],\"canhBao\":\"Không tìm thấy cột theo tiêu đề: mã đơn trả hàng.\"}")!);
        Assert.Null(GoogleSheetSyncService.DocCanhBao("{\"results\":[]}"));
        Assert.Null(GoogleSheetSyncService.DocCanhBao("không phải json"));
    }

    /// <summary>Mã quá hạn thử lại rơi khỏi hàng đợi đẩy — nếu không thì mã của đơn chưa từng lên sheet sẽ gõ
    /// cửa Apps Script mỗi lượt suốt 90 ngày (tuổi dọn của bảng).</summary>
    [Fact]
    public void LayMaTraHangChuaDay_QuaHanThuLai_RoiKhoiHangDoi()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("MOI", "R-MOI") }, "shop", Luc.AddDays(-1));
        repo.LuuMaTraHang(1, new[] { ("CU", "R-CU") }, "shop",
            Luc.AddDays(-ReturnCodesRepository.SoNgayThuLaiSheet - 1));

        Assert.Equal("MOI", Assert.Single(repo.LayMaTraHangChuaDay(1, Luc)).OrderSn);
        Assert.Equal(1, repo.DemChuaDay(1, Luc)); // badge đếm ĐÚNG cái sẽ được đẩy
    }

    /// <summary>Trần thử lại phải NHỎ HƠN hẳn tuổi dọn — hết hạn thử lại rồi bản ghi vẫn còn đó để soi.</summary>
    [Fact]
    public void HanThuLai_NhoHonTuoiDon()
        => Assert.True(ReturnCodesRepository.SoNgayThuLaiSheet < ReturnCodesRepository.SoNgayGiuMac);

    /// <summary>
    /// Mã quá hạn thử lại phải ĐẾM ĐƯỢC để còn log. Rơi khỏi hàng đợi mà không ai đếm là hỏng IM LẶNG — đúng
    /// kiểu lỗi cả đợt này đi vá, không được để bản vá đẻ lại.
    /// </summary>
    [Fact]
    public void DemQuaHanThuLai_DemDungNhomDaThoiThu()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("MOI", "R-MOI") }, "shop", Luc.AddDays(-1));
        repo.LuuMaTraHang(1, new[] { ("CU", "R-CU") }, "shop",
            Luc.AddDays(-ReturnCodesRepository.SoNgayThuLaiSheet - 1));
        repo.LuuMaTraHang(1, new[] { ("DA_DAY", "R-DD") }, "shop",
            Luc.AddDays(-ReturnCodesRepository.SoNgayThuLaiSheet - 1));
        repo.DanhDauDaDay(1, new[] { ("DA_DAY", "R-DD") }, Luc);

        // Chỉ đếm mã CHƯA đẩy được mà đã quá hạn — mã đã đẩy xong thì không phải nợ nần gì.
        Assert.Equal(1, repo.DemQuaHanThuLai(1, Luc));
        Assert.Equal(1, repo.DemChuaDay(1, Luc));   // "MOI" vẫn trong hạn, vẫn đếm vào badge
    }

    /// <summary>
    /// ĐẦU-CUỐI qua Web App GIẢ — đúng cảnh người dùng nhìn thấy: app quét ra mã, đẩy lên sheet, script trả
    /// <c>ok:true</c> + <c>boQua:true</c> (chưa có dòng của đơn trên sheet) ⇒ mã phải NẰM LẠI hàng đợi để lượt
    /// sau đẩy tiếp. Bản cũ đánh dấu đã-đẩy ở đây và mã biến mất im lặng.
    /// </summary>
    [Fact]
    public async Task ScriptBoQua_KhongDanhDauDaDay_ManamLaiHangDoi()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp { BoQuaTatCa = true };
        var (services, accId) = DungKhoMa(temp, web);

        Assert.Equal(KetQuaDay.KhongCanDay, await HubOutbox.PushReturnCodesToGsheetAsync(
            accId, services, log: _ => { }, ct: CancellationToken.None));

        Assert.Equal(("D1", "R1"), Assert.Single(services.ReturnCodes.LayMaTraHangChuaDay(accId)));
    }

    /// <summary>Đối chứng: script ghi được thật (không có <c>boQua</c>) ⇒ mã rời hàng đợi, không đẩy trùng.</summary>
    [Fact]
    public async Task ScriptGhiDuoc_ThiDanhDauDaDay()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = DungKhoMa(temp, web);

        Assert.Equal(KetQuaDay.ThanhCong, await HubOutbox.PushReturnCodesToGsheetAsync(
            accId, services, log: _ => { }, ct: CancellationToken.None));

        Assert.Empty(services.ReturnCodes.LayMaTraHangChuaDay(accId));
    }

    /// <summary>Thiếu TIÊU ĐỀ CỘT "Mã đơn trả hàng": script trả ok:true nhưng đã BỎ giá trị ⇒ với lô mã-trả
    /// (chỉ ghi đúng cột đó) phải coi như CHƯA đẩy, để người dùng sửa tiêu đề rồi lượt sau vào đúng chỗ.</summary>
    [Fact]
    public async Task ThieuTieuDeCot_CoiNhuChuaDay()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp { ChuaGhiMaTraTatCa = true };
        var (services, accId) = DungKhoMa(temp, web);

        await HubOutbox.PushReturnCodesToGsheetAsync(accId, services, log: _ => { }, ct: CancellationToken.None);

        Assert.Single(services.ReturnCodes.LayMaTraHangChuaDay(accId));
    }

    /// <summary>
    /// ĐỐI CHỨNG cho lỗi phản biện bắt: <c>canhBao</c> là cấp PHẢN HỒI và gom chung cả FILE PHỤ, nên nó
    /// <b>KHÔNG</b> được kéo theo cả lô. File phụ thiếu tiêu đề cột trong khi file chính ghi xong ⇒ mã phải
    /// được đánh dấu ĐÃ ĐẨY. Bản trước hạ cả lô ⇒ đẩy lại mỗi chu kỳ suốt 14 ngày, đốt quota Apps Script.
    /// </summary>
    [Fact]
    public async Task CanhBaoCapPhanHoi_NhungDongBaoGhiDuoc_ThiVanDanhDauDaDay()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp
        {
            // Script trả canhBao (vì FILE PHỤ thiếu cột) NHƯNG từng dòng KHÔNG mang cờ chuaGhiMaTra.
            CanhBao = "Không tìm thấy cột theo tiêu đề: mã đơn trả hàng. Các giá trị này KHÔNG được ghi.",
        };
        var (services, accId) = DungKhoMa(temp, web);

        await HubOutbox.PushReturnCodesToGsheetAsync(accId, services, log: _ => { }, ct: CancellationToken.None);

        Assert.Empty(services.ReturnCodes.LayMaTraHangChuaDay(accId));
    }

    /// <summary>Dựng kho mã một dòng (D1 → R1) + Web App giả đã cấu hình.</summary>
    private static (AppServices Services, long AccountId) DungKhoMa(
        TempDatabase temp, HubOutboxGsheetHuyTests.FakeGsheetWebApp web)
    {
        var services = new AppServices(temp.Path);
        var accountId = services.Accounts.Insert(new Account { Email = "tra-hang@example.com" });
        services.Settings.SetGsheetWebAppUrl(web.Url);
        services.ReturnCodes.LuuMaTraHang(accountId, new[] { ("D1", "R1") }, "shop", DateTime.UtcNow);
        return (services, accountId);
    }

    // ===================== 4. Không đặt được địa chỉ vẫn phải check trả hàng =====================

    /// <summary>
    /// Shop KHÔNG đặt được địa chỉ lấy hàng → thân flow trả sớm (không in phiếu, đúng chủ ý). Nhưng bước check
    /// trả hàng phải VẪN chạy: trang trả hàng không liên quan gì tới địa chỉ lấy hàng. Bản cũ đặt bước này ở
    /// cuối thân nên shop dính lỗi địa chỉ — lỗi thường trực — không bao giờ được check.
    /// </summary>
    [Fact]
    public async Task KhongDatDuocDiaChi_VanGuiLenhCheckTraHang()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = new ShopFlowRunner(rig.Channel, rig.Log, invoiceDir: TempDirTam(), Tinh,
            syncCallback: null, finalDoneSns: null, onOrderPrepared: null,
            returnCountLast: _ => 7,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return "ok"; });

        var chay = flow.RunShopOrdersAsync("sid", "shop1", toShip: 2, CancellationToken.None);

        using (await rig.NhanLenhAsync()) { }                                    // syncOrders
        await rig.GuiAsync(new { action = "pageData", kind = "orders", data = "[]" });
        using (var lenh = await rig.NhanLenhAsync())                             // setPickupAddress
        {
            Assert.Equal("setPickupAddress", lenh.RootElement.GetProperty("action").GetString());
        }
        await rig.GuiAsync(new { action = "pickupDone", ok = false });           // KHÔNG đặt được

        // Mắt xích cuối vẫn phải tới: lệnh kế tiếp là check trả hàng.
        using (var lenh = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequests", lenh.RootElement.GetProperty("action").GetString());
        }
        var ma = MaYeuCauHomNay();
        await rig.GuiAsync(TrangTraHang("7 Yêu cầu", dong:
            new object[] { new { headHtml = DongHtml("260731AAAAAA", ma), laTraHang = true } }));

        await chay;
        Assert.Equal(new YeuCauTraHang("260731AAAAAA", ma), Assert.Single(capDaLuu));
        Assert.Equal("shop1", flow.PickupFailedShop);   // vẫn báo đúng lỗi địa chỉ cho vòng ngoài
    }

    /// <summary>
    /// Extension trả <c>error</c> cho chặng "set địa chỉ về địa chỉ khác" (ca thật sau V7: service worker chết
    /// giữa shop → mất ngữ cảnh tab, <c>orderTabIdStrict</c> báo lỗi thay vì thao tác trên tab picker). Bước này
    /// là BEST-EFFORT chạy SAU khi shop đã xử xong: lỗi ở đây phải được nuốt y như quá hạn — shop KHÔNG được tính
    /// "hỏng giữa chừng" (mất tổng kết, 3 shop liên tiếp là dừng cả vòng) và bước check trả hàng VẪN phải chạy.
    /// Bản đầu chỉ <c>catch (TimeoutException)</c> nên <c>error</c> (InvalidOperationException) xuyên thẳng ra ngoài.
    /// </summary>
    [Fact]
    public async Task TraDiaChiVeKhac_ExtensionBaoLoi_VanChayCheckTraHang_KhongNemRaNgoai()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = new ShopFlowRunner(rig.Channel, rig.Log, invoiceDir: TempDirTam(), Tinh,
            syncCallback: null, finalDoneSns: null, onOrderPrepared: null,
            returnCountLast: _ => 0,
            saveReturnCount: (_, _) => { },
            saveReturnCodes: _ => "ok");

        var chay = flow.RunShopOrdersAsync("sid", "shop1", toShip: 2, CancellationToken.None);

        using (await rig.NhanLenhAsync()) { }                                    // syncOrders
        await rig.GuiAsync(new { action = "pageData", kind = "orders", data = "[]" });
        using (await rig.NhanLenhAsync()) { }                                    // setPickupAddress
        await rig.GuiAsync(new { action = "pickupDone", ok = true });
        using (await rig.NhanLenhAsync()) { }                                    // prepareNextOrder
        await rig.GuiAsync(new { action = "noOrder" });
        using (var lenh = await rig.NhanLenhAsync())                             // setPickupAddressToOther
        {
            Assert.Equal("setPickupAddressToOther", lenh.RootElement.GetProperty("action").GetString());
        }
        await rig.GuiAsync(new { action = "error", message = "set địa chỉ khác: mất ngữ cảnh tab shop — bỏ lượt" });

        // Lỗi best-effort đã được nuốt ⇒ mắt xích cuối vẫn tới: lệnh kế là check trả hàng.
        using (var lenh = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequests", lenh.RootElement.GetProperty("action").GetString());
        }
        await rig.GuiAsync(TrangTraHang("0 Yêu cầu"));

        await chay;                                    // KHÔNG ném — shop không bị tính "hỏng giữa chừng"
        Assert.Equal("shop1", flow.PickupOkShop);      // tín hiệu "địa chỉ OK" giữ nguyên cho vòng ngoài
    }

    private static string TempDirTam()
    {
        var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xlds_th_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(d);
        return d;
    }
}
