using System.Text;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm dựng CSV thuần: header đủ cột, escape RFC 4180 (dấu phẩy/ngoặc kép/xuống dòng), CRLF cuối
/// dòng, và BOM UTF-8 ở bản byte để Excel mở tiếng Việt không vỡ.
/// </summary>
public class OrderCsvExporterTests
{
    private static OrderExportRow Row(
        string account = "shop@a.com", string sn = "SN1", string buyer = "buyer",
        string product = "Giày", string total = "₫166.500", string estimate = "₫160.000",
        string payment = "COD", string status = "Đã hủy", string note = "", string carrier = "SPX",
        string tracking = "SPX1", string synced = "16/07/2026 09:30")
        => new(account, sn, buyer, product, total, estimate, payment, status, note, carrier, tracking, synced);

    [Fact]
    public void BuildCsv_DongDauLaHeaderDayDu12Cot()
    {
        var csv = OrderCsvExporter.BuildCsv(new[] { Row() });

        var firstLine = csv.Split("\r\n")[0];
        Assert.Equal(
            "Tài khoản,Mã đơn,Người mua,Sản phẩm,Tổng tiền,Ước tính,Thanh toán,Trạng thái,Mô tả/Lý do hủy,ĐVVC,Mã vận đơn,Sync lúc",
            firstLine);
        Assert.Equal(12, OrderCsvExporter.Headers.Length);
    }

    [Fact]
    public void BuildCsv_CotUocTinh_NgaySauCotTongTien()
    {
        var csv = OrderCsvExporter.BuildCsv(new[] { Row(total: "₫166.500", estimate: "₫160.000") });

        // Dòng dữ liệu (dòng thứ 2) có "Ước tính" ngay sau "Tổng tiền".
        var dataLine = csv.Split("\r\n")[1];
        Assert.Contains("₫166.500,₫160.000", dataLine);
    }

    [Fact]
    public void BuildCsv_MoiBanGhiMotDong_KetThucBangCRLF()
    {
        var csv = OrderCsvExporter.BuildCsv(new[] { Row(), Row(sn: "SN2") });

        Assert.EndsWith("\r\n", csv);
        var parts = csv.Split("\r\n");
        // header + 2 dòng dữ liệu + phần rỗng sau CRLF cuối = 4
        Assert.Equal(4, parts.Length);
        Assert.Equal(string.Empty, parts[3]);
    }

    [Fact]
    public void Escape_BinhThuong_GiuNguyen()
        => Assert.Equal("abc", OrderCsvExporter.Escape("abc"));

    [Fact]
    public void Escape_CoDauPhay_ThiBocNgoacKep()
        => Assert.Equal("\"a,b\"", OrderCsvExporter.Escape("a,b"));

    [Fact]
    public void Escape_CoNgoacKep_ThiNhanDoiVaBoc()
        => Assert.Equal("\"a\"\"b\"", OrderCsvExporter.Escape("a\"b"));

    [Fact]
    public void Escape_CoXuongDong_ThiBoc()
    {
        Assert.Equal("\"a\nb\"", OrderCsvExporter.Escape("a\nb"));
        Assert.Equal("\"a\rb\"", OrderCsvExporter.Escape("a\rb"));
    }

    [Fact]
    public void Escape_Null_ThanhChuoiRong()
        => Assert.Equal(string.Empty, OrderCsvExporter.Escape(null));

    [Fact]
    public void BuildCsv_TruongCoDauPhay_DuocBocTrongToanCuc()
    {
        var csv = OrderCsvExporter.BuildCsv(new[] { Row(product: "Áo, quần", note: "Lý do: \"hủy\"") });

        Assert.Contains("\"Áo, quần\"", csv);
        Assert.Contains("\"Lý do: \"\"hủy\"\"\"", csv);
    }

    [Fact]
    public void BuildCsvWithBom_BatDauBangBomUtf8()
    {
        var bytes = OrderCsvExporter.BuildCsvWithBom(new[] { Row() });

        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public void BuildCsvWithBom_PhanThanBangUtf8CuaChuoiCsv()
    {
        var rows = new[] { Row() };
        var bytes = OrderCsvExporter.BuildCsvWithBom(rows);

        var body = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal(OrderCsvExporter.BuildCsv(rows), body);
    }

    // ===================== Chống CSV/formula injection =====================

    [Fact]
    public void SanitizeField_BatDauKyTuNguyHiem_ChenNhayDon()
    {
        Assert.Equal("'=HYPERLINK(\"x\")", OrderCsvExporter.SanitizeField("=HYPERLINK(\"x\")"));
        Assert.Equal("'+1+1", OrderCsvExporter.SanitizeField("+1+1"));
        Assert.Equal("'-2+3", OrderCsvExporter.SanitizeField("-2+3"));
        Assert.Equal("'@SUM(A1)", OrderCsvExporter.SanitizeField("@SUM(A1)"));
        Assert.Equal("'\tTAB", OrderCsvExporter.SanitizeField("\tTAB"));
        Assert.Equal("'\rCR", OrderCsvExporter.SanitizeField("\rCR"));
    }

    [Fact]
    public void SanitizeField_BinhThuong_GiuNguyen()
    {
        Assert.Equal("Giày", OrderCsvExporter.SanitizeField("Giày"));
        Assert.Equal("₫166.500", OrderCsvExporter.SanitizeField("₫166.500"));   // ₫ ở đầu, không nguy hiểm
        Assert.Equal("a=b", OrderCsvExporter.SanitizeField("a=b"));              // '=' KHÔNG ở đầu
        Assert.Equal(string.Empty, OrderCsvExporter.SanitizeField(null));
        Assert.Equal(string.Empty, OrderCsvExporter.SanitizeField(""));
    }

    [Fact]
    public void BuildCsv_FormulaInjection_ChenNhayDonRoiApRFC4180()
    {
        // '=' ở đầu + chứa dấu phẩy/ngoặc kép → thêm ' rồi bọc ngoặc kép + nhân đôi ngoặc kép.
        var csv = OrderCsvExporter.BuildCsv(new[] { Row(buyer: "=HYPERLINK(\"http://x\",1)") });
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\",1)\"", csv);

        // '@' ở đầu, không có ký tự đặc biệt RFC4180 → chỉ thêm ' , không bọc ngoặc kép.
        var csv2 = OrderCsvExporter.BuildCsv(new[] { Row(product: "@cmd") });
        Assert.Contains(",'@cmd,", csv2);
    }
}
