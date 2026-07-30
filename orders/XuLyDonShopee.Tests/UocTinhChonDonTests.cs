using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test luật CHỌN đơn cần mở trang chi tiết lấy "Số tiền cuối cùng"
/// (<see cref="UocTinhDon.ChonDonLayUocTinh"/>) và phần LOG chẩn đoán của
/// <see cref="UocTinhDon.MergeFinalAmounts"/>.
/// <para>Điểm phải ghim: đơn RỜI trạng thái "chuẩn bị hàng" mà chưa có ước tính vẫn phải được thử lại (trước đây
/// mất VĨNH VIỄN — ô "tiền bán" trên Google Sheet trống mãi), nhưng phần nới thêm đó phải bị chốt chặn cứng
/// (≤ <see cref="UocTinhDon.MaxBuUocTinh"/> đơn/lượt, ≤ <see cref="UocTinhDon.SoNgayBuUocTinh"/>
/// ngày, ưu tiên đơn MỚI) kẻo nổ số tab chi tiết mỗi vòng ⇒ tăng rủi ro captcha.</para>
/// </summary>
public class UocTinhChonDonTests
{
    private static readonly DateTime HomNay = new(2026, 7, 28);
    private static readonly IReadOnlySet<string> KhongBoQua = new HashSet<string>();

    /// <summary>Mã đơn Shopee mở đầu bằng ngày đặt (<c>yyMMdd</c>) — dựng mã đúng khuôn thật cho ngày cho trước.</summary>
    private static string Ma(DateTime ngay, string duoi = "PN0HHCS5") => ngay.ToString("yyMMdd") + duoi;

    private static SyncedOrder Don(string sn, string status, long? final = null, string? shopeeId = "123456")
        => new() { OrderSn = sn, Status = status, FinalAmount = final, ShopeeOrderId = shopeeId };

    private static (List<SyncedOrder> Chinh, List<SyncedOrder> Bu) Chon(params SyncedOrder[] orders)
        => UocTinhDon.ChonDonLayUocTinh(orders, KhongBoQua, HomNay);

    // ===== NgayDonTuMa: 6 ký tự đầu mã đơn =====

    [Theory]
    [InlineData("260726PN0HHCS5", 2026, 7, 26)]
    [InlineData("260728TV14FVU8", 2026, 7, 28)]
    [InlineData("260619GSNQ36U7", 2026, 6, 19)]
    public void NgayDonTuMa_LayDuocNgayDat(string sn, int nam, int thang, int ngay)
    {
        Assert.Equal(new DateTime(nam, thang, ngay), UocTinhDon.NgayDonTuMa(sn));
    }

    /// <summary>Mã không đọc được ngày → null ⇒ đơn đó KHÔNG được lấy bù (thà bỏ còn hơn đoán).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("26072")]           // chưa đủ 6 ký tự
    [InlineData("26AB28TV14FVU8")]  // có chữ trong 6 ký tự đầu
    [InlineData("269928TV14FVU8")]  // tháng 99 — không phải ngày hợp lệ
    public void NgayDonTuMa_MaLa_TraVeNull(string? sn)
    {
        Assert.Null(UocTinhDon.NgayDonTuMa(sn));
    }

    // ===== Nhóm CHÍNH: luật cũ giữ nguyên =====

    [Fact]
    public void DangChuanBi_ThieuUocTinh_VaoNhomChinh()
    {
        var don = Don(Ma(HomNay), "Chờ lấy hàng");
        var (chinh, bu) = Chon(don);

        Assert.Same(don, Assert.Single(chinh));
        Assert.Empty(bu);
    }

    /// <summary>Đơn ĐÃ có ước tính → không chọn nữa, dù ở trạng thái nào (khỏi mở lại trang chi tiết vô ích).</summary>
    [Theory]
    [InlineData("Chờ lấy hàng")]
    [InlineData("Đã giao cho ĐVVC")]
    [InlineData("Đang giao")]
    public void DaCoUocTinh_KhongChon(string status)
    {
        var (chinh, bu) = Chon(Don(Ma(HomNay), status, final: 374227));

        Assert.Empty(chinh);
        Assert.Empty(bu);
    }

    /// <summary>Không có <c>ShopeeOrderId</c> thì không mở được trang chi tiết → bỏ ở CẢ hai nhóm.</summary>
    [Fact]
    public void ThieuShopeeOrderId_KhongChon()
    {
        var (chinh, bu) = Chon(
            Don(Ma(HomNay), "Chờ lấy hàng", shopeeId: null),
            Don(Ma(HomNay), "Đang giao", shopeeId: "  "));

        Assert.Empty(chinh);
        Assert.Empty(bu);
    }

    // ===== Nhóm BÙ: đơn đã rời trạng thái chuẩn bị =====

    /// <summary>Ca xương sống của Bước 6: đơn rời trạng thái mà thiếu ước tính vẫn phải được thử lại.</summary>
    [Fact]
    public void RoiTrangThai_ThieuUocTinh_TrongHanNgay_VaoNhomBu()
    {
        var don = Don(Ma(HomNay.AddDays(-2)), "Đã giao cho ĐVVC");
        var (chinh, bu) = Chon(don);

        Assert.Empty(chinh);
        Assert.Same(don, Assert.Single(bu));
    }

    [Fact]
    public void RoiTrangThai_CuHonHanNgay_KhongChon()
    {
        var (chinh, bu) = Chon(Don(Ma(HomNay.AddDays(-UocTinhDon.SoNgayBuUocTinh - 1)), "Đang giao"));

        Assert.Empty(chinh);
        Assert.Empty(bu);
    }

    /// <summary>Đúng biên <see cref="UocTinhDon.SoNgayBuUocTinh"/> ngày → VẪN lấy (biên đóng).</summary>
    [Fact]
    public void RoiTrangThai_DungBienNgay_VanChon()
    {
        var (_, bu) = Chon(Don(Ma(HomNay.AddDays(-UocTinhDon.SoNgayBuUocTinh)), "Đang giao"));

        Assert.Single(bu);
    }

    /// <summary>Đồng hồ/múi giờ máy lệch làm mã đơn "hôm nay" trông như ngày mai → VẪN lấy (nới biên trên 1 ngày);
    /// xa hơn nữa là mã rác → bỏ.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public void RoiTrangThai_NgayTuongLai_ChiChapNhanLech1Ngay(int lech, bool duocChon)
    {
        var (_, bu) = Chon(Don(Ma(HomNay.AddDays(lech)), "Đang giao"));

        Assert.Equal(duocChon ? 1 : 0, bu.Count);
    }

    /// <summary>Đơn HỦY không có "Số tiền cuối cùng" để lấy → không tốn lượt mở tab (luật cũ cũng đã loại vì
    /// đơn hủy không ở trạng thái chuẩn bị).</summary>
    [Fact]
    public void DonHuy_KhongVaoNhomBu()
    {
        var (chinh, bu) = Chon(Don(Ma(HomNay), "Đã hủy"));

        Assert.Empty(chinh);
        Assert.Empty(bu);
    }

    /// <summary>Mã đơn không suy được ngày → không có căn cứ cửa sổ 7 ngày ⇒ bỏ, không đoán.</summary>
    [Fact]
    public void RoiTrangThai_MaKhongCoNgay_KhongChon()
    {
        var (_, bu) = Chon(Don("KHONGPHAIMADON", "Đang giao"));

        Assert.Empty(bu);
    }

    /// <summary>Quá trần → chỉ lấy <see cref="UocTinhDon.MaxBuUocTinh"/> đơn, ưu tiên MỚI nhất.</summary>
    [Fact]
    public void QuaTran_ChiLayToiDa_VaUuTienDonMoiNhat()
    {
        // 7 đơn từ hôm nay lùi dần 6 ngày (đều trong hạn) → phải lấy 5 đơn MỚI nhất, thứ tự mới → cũ.
        var dons = Enumerable.Range(0, 7)
            .Select(i => Don(Ma(HomNay.AddDays(-i), $"X{i}AAAAA"), "Đang giao"))
            .ToArray();

        var (_, bu) = Chon(dons);

        Assert.Equal(UocTinhDon.MaxBuUocTinh, bu.Count);
        Assert.Equal(
            dons.Take(UocTinhDon.MaxBuUocTinh).Select(o => o.OrderSn),
            bu.Select(o => o.OrderSn));
    }

    /// <summary>Hai nhóm TÁCH RIÊNG: đơn đang chuẩn bị KHÔNG bị đơn bù chiếm chỗ, và trần 5 chỉ áp cho nhóm bù.</summary>
    [Fact]
    public void HaiNhomTachRieng_NhomChinhKhongBiTranNhomBuChanLai()
    {
        var chuanBi = Enumerable.Range(0, 12)
            .Select(i => Don(Ma(HomNay, $"C{i:00}AAAA"), "Chờ lấy hàng")).ToArray();
        var daRoi = Enumerable.Range(0, 9)
            .Select(i => Don(Ma(HomNay.AddDays(-1), $"R{i:00}AAAA"), "Đang giao")).ToArray();

        var (chinh, bu) = Chon(chuanBi.Concat(daRoi).ToArray());

        Assert.Equal(12, chinh.Count);                                  // nhóm chính KHÔNG bị cắt bởi trần nhóm bù
        Assert.Equal(UocTinhDon.MaxBuUocTinh, bu.Count);
        Assert.All(chinh, o => Assert.Contains(o, chuanBi));
        Assert.All(bu, o => Assert.Contains(o, daRoi));
    }

    /// <summary>Đơn nằm trong tập "đã có ước tính trong DB" (App rót) → bỏ ở CẢ hai nhóm.</summary>
    [Fact]
    public void NamTrongTapDaXong_KhongChon()
    {
        var sn1 = Ma(HomNay, "AAAAAAAA");
        var sn2 = Ma(HomNay.AddDays(-1), "BBBBBBBB");
        var done = new HashSet<string> { sn1, sn2 };

        var (chinh, bu) = UocTinhDon.ChonDonLayUocTinh(
            new[] { Don(sn1, "Chờ lấy hàng"), Don(sn2, "Đang giao") }, done, HomNay);

        Assert.Empty(chinh);
        Assert.Empty(bu);
    }

    // ===== LOG chẩn đoán (Bước 5) =====

    private static List<string> GopVaLog(string finalsJson)
    {
        var nhatKy = new List<string>();
        UocTinhDon.MergeFinalAmounts(
            new[] { Don("SN1", "Chờ lấy hàng"), Don("SN2", "Chờ lấy hàng") }, finalsJson, nhatKy.Add);
        return nhatKy;
    }

    /// <summary>Đơn hụt phải được nêu ĐÍCH DANH kèm lý do phân biệt được — trước đây log chỉ có "3/4 đơn",
    /// soi lại không biết đơn nào, vì sao.</summary>
    [Fact]
    public void DonHut_DuocLogKemMaVaLyDo()
    {
        var nhatKy = GopVaLog(
            "[{\"orderSn\":\"SN1\",\"finalText\":\"\",\"nguon\":\"khong-thay\"},"
            + "{\"orderSn\":\"SN2\",\"finalText\":\"\",\"nguon\":\"dang-tai\"}]");

        var dong = Assert.Single(nhatKy, d => d.Contains("KHÔNG lấy được", StringComparison.Ordinal));
        Assert.Contains("SN1 (không thấy thẻ)", dong, StringComparison.Ordinal);
        Assert.Contains("SN2 (thẻ đang tải, hết giờ)", dong, StringComparison.Ordinal);
    }

    /// <summary>Đếm riêng số đơn mà BẢNG DOANH THU cứu được (thẻ remote chưa có số) — để biết bố cục nào phổ biến.</summary>
    [Fact]
    public void DocDuocNhoBangDoanhThu_DuocDemRieng()
    {
        var nhatKy = GopVaLog(
            "[{\"orderSn\":\"SN1\",\"finalText\":\"₫374.227\",\"nguon\":\"chi-bang\"},"
            + "{\"orderSn\":\"SN2\",\"finalText\":\"₫923.774\",\"nguon\":\"ca-hai\"}]");

        // Chỉ SN1 được bảng doanh thu cứu; SN2 thẻ remote cũng có số nên không tính.
        Assert.Single(nhatKy, d => d.Contains("1 đơn chỉ đọc được nhờ BẢNG DOANH THU", StringComparison.Ordinal));
        Assert.DoesNotContain(nhatKy, d => d.Contains("KHÔNG lấy được", StringComparison.Ordinal));
    }

    /// <summary>Payload đời cũ (chưa có <c>nguon</c>) vẫn gộp được, chỉ là lý do ghi "không rõ" — KHÔNG ném.</summary>
    [Fact]
    public void ThieuTruongNguon_VanChayVaGhiKhongRo()
    {
        var nhatKy = GopVaLog("[{\"orderSn\":\"SN1\",\"finalText\":\"\"},{\"orderSn\":\"SN2\",\"finalText\":\"₫1\"}]");

        Assert.Contains(nhatKy, d => d.Contains("SN1 (không rõ)", StringComparison.Ordinal));
        Assert.DoesNotContain(nhatKy, d => d.Contains("BẢNG DOANH THU", StringComparison.Ordinal));
    }
}
