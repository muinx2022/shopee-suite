using XuLyDonShopee.Core.Services;
using ToolkitLocator = Shopee.Toolkit.Browser.BrowserLocator;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho hàm lõi thuần <see cref="ToolkitLocator.FindFirstExisting"/> (bộ dò dùng chung
/// shared/Shopee.Toolkit) — không phụ thuộc máy thật vì tiêm predicate. Không test
/// <see cref="BrowserLocator.FindBraveExecutable"/> vì nó phụ thuộc hệ thống file cụ thể.
/// <para>Các test <c>ResolveExecutableCore</c> (ma trận Auto/Chrome/Edge/Brave/Chromium đóng gói) và
/// <c>ClassifyExe</c> đã BỎ 11/08/2026 cùng với enum <c>BrowserChoice</c>: app chỉ chạy Brave nên không còn
/// gì để phân giải. Hậu tố thư mục hồ sơ giờ là hằng <see cref="BrowserLocator.LoaiHoSo"/> — có test riêng
/// khoá chuỗi đó ở <c>ChiChayBraveTests</c>.</para>
/// </summary>
public class BrowserLocatorTests
{
    [Fact]
    public void FindFirstExisting_CoPhanTuKhop_TraPhanTuDauTienKhop()
    {
        var candidates = new[] { "a", "b", "c" };

        var result = ToolkitLocator.FindFirstExisting(candidates, p => p == "b" || p == "c");

        Assert.Equal("b", result);
    }

    [Fact]
    public void FindFirstExisting_KhongPhanTuNaoKhop_TraNull()
    {
        var candidates = new[] { "a", "b", "c" };

        var result = ToolkitLocator.FindFirstExisting(candidates, _ => false);

        Assert.Null(result);
    }

    [Fact]
    public void FindFirstExisting_BoQuaNullVaChuoiRong()
    {
        var candidates = new string?[] { null, "", "   ", "match" };

        var result = ToolkitLocator.FindFirstExisting(candidates!, p => p == "match");

        Assert.Equal("match", result);
    }

    [Fact]
    public void FindFirstExisting_NhieuPhanTuKhop_UuTienPhanTuTruoc()
    {
        // Cả "first" lẫn "second" đều khớp predicate → phải trả phần tử đầu tiên theo thứ tự.
        var candidates = new[] { "first", "second" };

        var result = ToolkitLocator.FindFirstExisting(candidates, _ => true);

        Assert.Equal("first", result);
    }

    [Fact]
    public void FindFirstExisting_DanhSachRong_TraNull()
    {
        var result = ToolkitLocator.FindFirstExisting(System.Array.Empty<string>(), _ => true);

        Assert.Null(result);
    }
}
