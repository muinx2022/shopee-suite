using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho hàm lõi thuần <see cref="BrowserLocator.FindFirstExisting"/> và
/// <see cref="BrowserLocator.ResolveExecutableCore"/> (không phụ thuộc máy thật — tiêm predicate).
/// Không test <see cref="BrowserLocator.FindBraveExecutable"/>/<c>FindChromeExecutable</c>/<c>FindEdgeExecutable</c>
/// vì phụ thuộc hệ thống file cụ thể.
/// </summary>
public class BrowserLocatorTests
{
    [Fact]
    public void FindFirstExisting_CoPhanTuKhop_TraPhanTuDauTienKhop()
    {
        var candidates = new[] { "a", "b", "c" };

        var result = BrowserLocator.FindFirstExisting(candidates, p => p == "b" || p == "c");

        Assert.Equal("b", result);
    }

    [Fact]
    public void FindFirstExisting_KhongPhanTuNaoKhop_TraNull()
    {
        var candidates = new[] { "a", "b", "c" };

        var result = BrowserLocator.FindFirstExisting(candidates, _ => false);

        Assert.Null(result);
    }

    [Fact]
    public void FindFirstExisting_BoQuaNullVaChuoiRong()
    {
        var candidates = new string?[] { null, "", "   ", "match" };

        var result = BrowserLocator.FindFirstExisting(candidates!, p => p == "match");

        Assert.Equal("match", result);
    }

    [Fact]
    public void FindFirstExisting_NhieuPhanTuKhop_UuTienPhanTuTruoc()
    {
        // Cả "first" lẫn "second" đều khớp predicate → phải trả phần tử đầu tiên theo thứ tự.
        var candidates = new[] { "first", "second" };

        var result = BrowserLocator.FindFirstExisting(candidates, _ => true);

        Assert.Equal("first", result);
    }

    [Fact]
    public void FindFirstExisting_DanhSachRong_TraNull()
    {
        var result = BrowserLocator.FindFirstExisting(System.Array.Empty<string>(), _ => true);

        Assert.Null(result);
    }

    // ===== ResolveExecutableCore: phân giải BrowserChoice (thứ tự Auto = Chrome → Edge → Brave) =====

    // Predicate stub tiện dụng: trả path nếu "có", null nếu "không có".
    private static System.Func<string?> Have(string path) => () => path;
    private static readonly System.Func<string?> None = () => null;

    [Fact]
    public void ResolveCore_Auto_CoDuCa3_TraChrome()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Auto, Have("chrome"), Have("edge"), Have("brave"));

        Assert.Equal("chrome", result);
    }

    [Fact]
    public void ResolveCore_Auto_ChiEdgeVaBrave_TraEdge()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Auto, None, Have("edge"), Have("brave"));

        Assert.Equal("edge", result);
    }

    [Fact]
    public void ResolveCore_Auto_ChiBrave_TraBrave()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Auto, None, None, Have("brave"));

        Assert.Equal("brave", result);
    }

    [Fact]
    public void ResolveCore_Auto_KhongCoGi_TraNull()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Auto, None, None, None);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCore_Chrome_Co_TraChrome()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Chrome, Have("chrome"), Have("edge"), Have("brave"));

        Assert.Equal("chrome", result);
    }

    [Fact]
    public void ResolveCore_Chrome_Khong_TraNull()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Chrome, None, Have("edge"), Have("brave"));

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCore_Edge_Co_TraEdge()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Edge, Have("chrome"), Have("edge"), Have("brave"));

        Assert.Equal("edge", result);
    }

    [Fact]
    public void ResolveCore_Edge_Khong_TraNull()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Edge, Have("chrome"), None, Have("brave"));

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCore_Brave_Co_TraBrave()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Brave, Have("chrome"), Have("edge"), Have("brave"));

        Assert.Equal("brave", result);
    }

    [Fact]
    public void ResolveCore_Brave_Khong_TraNull()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.Brave, Have("chrome"), Have("edge"), None);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCore_BundledChromium_LuonNull()
    {
        var result = BrowserLocator.ResolveExecutableCore(
            BrowserChoice.BundledChromium, Have("chrome"), Have("edge"), Have("brave"));

        Assert.Null(result);
    }

    // ===== ClassifyExe: phân loại exe path thành slug loại trình duyệt (thuần, path giả) =====

    [Fact]
    public void ClassifyExe_KhopChrome_TraChrome()
    {
        var result = BrowserLocator.ClassifyExe("chrome.exe", "chrome.exe", "edge.exe", "brave.exe");

        Assert.Equal("chrome", result);
    }

    [Fact]
    public void ClassifyExe_KhopEdge_TraEdge()
    {
        var result = BrowserLocator.ClassifyExe("edge.exe", "chrome.exe", "edge.exe", "brave.exe");

        Assert.Equal("edge", result);
    }

    [Fact]
    public void ClassifyExe_KhopBrave_TraBrave()
    {
        var result = BrowserLocator.ClassifyExe("brave.exe", "chrome.exe", "edge.exe", "brave.exe");

        Assert.Equal("brave", result);
    }

    [Fact]
    public void ClassifyExe_ExeNull_TraChromium()
    {
        var result = BrowserLocator.ClassifyExe(null, "chrome.exe", "edge.exe", "brave.exe");

        Assert.Equal("chromium", result);
    }

    [Fact]
    public void ClassifyExe_KhongKhopCaiNao_TraChromium()
    {
        var result = BrowserLocator.ClassifyExe("chromium.exe", "chrome.exe", "edge.exe", "brave.exe");

        Assert.Equal("chromium", result);
    }

    [Fact]
    public void ClassifyExe_ChiCaiBrave_TraBrave()
    {
        // Chrome/Edge chưa cài (null) — chỉ Brave có; exe khớp Brave.
        var result = BrowserLocator.ClassifyExe("brave.exe", null, null, "brave.exe");

        Assert.Equal("brave", result);
    }

    [Fact]
    public void ClassifyExe_KhongPhanBietHoaThuong()
    {
        // Đường dẫn Windows không phân biệt hoa/thường → so khớp OrdinalIgnoreCase.
        var result = BrowserLocator.ClassifyExe("CHROME.EXE", "chrome.exe", "edge.exe", "brave.exe");

        Assert.Equal("chrome", result);
    }
}
