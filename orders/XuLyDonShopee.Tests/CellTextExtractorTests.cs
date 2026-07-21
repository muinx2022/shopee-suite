using Avalonia.Controls;
using XuLyDonShopee.App.Views;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test <see cref="CellTextExtractor.ExtractCellText"/>: rút đúng text ô cho double-click-copy, bền với các
/// dạng ô của màn Đơn hàng. Dựng control bằng tay (không render, không khởi tạo app) — helper duyệt cả visual
/// lẫn logical tree nên <c>Border.Child</c>/<c>Panel.Children</c> đã là con ngay khi gán.
/// </summary>
public class CellTextExtractorTests
{
    [Fact]
    public void O_text_thuong_tra_text_o()
    {
        var cell = new TextBlock { Text = "SPX0123456789" };
        Assert.Equal("SPX0123456789", CellTextExtractor.ExtractCellText(cell));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void O_text_rong_tra_null(string text)
    {
        var cell = new TextBlock { Text = text };
        Assert.Null(CellTextExtractor.ExtractCellText(cell));
    }

    [Fact]
    public void O_null_tra_null()
    {
        Assert.Null(CellTextExtractor.ExtractCellText(null));
    }

    [Fact]
    public void O_trang_thai_pill_border_boc_textblock_tra_text_con()
    {
        // Cột "Trạng thái": Border (pill) chứa TextBlock trạng thái.
        var cell = new Border { Child = new TextBlock { Text = "Đã giao" } };
        Assert.Equal("Đã giao", CellTextExtractor.ExtractCellText(cell));
    }

    [Fact]
    public void O_phieu_chua_button_bo_qua_tra_null()
    {
        // Cột "Phiếu": nút "In phiếu" (Button bọc TextBlock) → không copy chuỗi "In phiếu".
        var cell = new Border { Child = new Button { Content = new TextBlock { Text = "In phiếu" } } };
        Assert.Null(CellTextExtractor.ExtractCellText(cell));
    }

    [Fact]
    public void O_button_la_goc_cung_bo_qua()
    {
        var cell = new Button { Content = new TextBlock { Text = "In phiếu" } };
        Assert.Null(CellTextExtractor.ExtractCellText(cell));
    }

    [Fact]
    public void Textblock_long_nhieu_lop_van_lay_duoc()
    {
        // Ô lồng sâu (Border → StackPanel → TextBlock) vẫn tìm ra TextBlock.
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "0987654321" });
        var cell = new Border { Child = panel };
        Assert.Equal("0987654321", CellTextExtractor.ExtractCellText(cell));
    }

    [Fact]
    public void Lay_textblock_dau_tien_theo_thu_tu_duyet()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "đầu" });
        panel.Children.Add(new TextBlock { Text = "sau" });
        var cell = new Border { Child = panel };
        Assert.Equal("đầu", CellTextExtractor.ExtractCellText(cell));
    }
}
