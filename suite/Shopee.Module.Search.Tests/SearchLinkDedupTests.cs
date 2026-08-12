using Shopee.Modules.Search;
using ShopeeStatApp.Services;

namespace Shopee.Module.Search.Tests;

/// <summary>
/// Hai cái bẫy "hai lượt cào ghi đè nhau" của chế độ tìm theo file:
/// <list type="number">
/// <item>Cùng một link nằm ở 2 file (hoặc 2 dòng) đều được tick ⇒ 2 lane cào song song CÙNG link: file Excel
/// per-link ghi đè nhau, bản ghi hủy-theo-link đè nhau, còn UI thì gộp về 1 tab nên user không thấy gì lạ.</item>
/// <item>Tên file per-link lấy theo SLUG danh mục, mà slug KHÔNG duy nhất: 2 danh mục khác nhau cùng slug là
/// link chạy sau ghi đè kết quả của link trước — mất trắng, im lặng.</item>
/// </list>
/// </summary>
public sealed class SearchLinkDedupTests
{
    private static (int Index, string Link, string SourceFile) Muc(int index, string link, string file = "a.xlsx") =>
        (index, link, file);

    [Fact]
    public void DedupLinks_LinkTrungGiuaHaiFile_ChiGiuMucDau()
    {
        var links = new[]
        {
            Muc(1, "https://shopee.vn/dien-thoai-cat.11036030", "file-a.xlsx"),
            Muc(7, "https://shopee.vn/thoi-trang-cat.11035567", "file-a.xlsx"),
            Muc(3, "https://shopee.vn/dien-thoai-cat.11036030", "file-b.xlsx"),
        };

        var giu = SearchRunner.DedupLinks(links);

        Assert.Equal(2, giu.Count);
        Assert.Equal((1, "https://shopee.vn/dien-thoai-cat.11036030", "file-a.xlsx"), giu[0]);   // giữ mục ĐẦU
        Assert.Equal(7, giu[1].Index);
    }

    [Fact]
    public void DedupLinks_KhacHoaThuong_VanLaTrung()
    {
        var links = new[]
        {
            Muc(1, "https://shopee.vn/Dien-Thoai-cat.11036030"),
            Muc(2, "https://shopee.vn/dien-thoai-CAT.11036030"),
        };

        Assert.Single(SearchRunner.DedupLinks(links));
    }

    [Fact]
    public void DedupLinks_KhongCoTrung_GiuNguyenSoLuongVaThuTu()
    {
        var links = new[]
        {
            Muc(1, "https://shopee.vn/a-cat.1"),
            Muc(2, "https://shopee.vn/b-cat.2"),
            Muc(3, "https://shopee.vn/c-cat.3"),
        };

        var giu = SearchRunner.DedupLinks(links);

        Assert.Equal(3, giu.Count);
        Assert.Equal([1, 2, 3], giu.Select(x => x.Index));
    }

    /// <summary>Link rỗng/trắng bị LOẠI HẲN — bản đầu chỉ dedup nên mục rỗng ĐẦU TIÊN vẫn lọt vào hàng việc,
    /// lane mở Brave rồi báo lỗi vô nghĩa.</summary>
    [Fact]
    public void DedupLinks_LinkRong_BiLoaiHan()
    {
        var links = new[] { Muc(1, ""), Muc(2, "   "), Muc(3, "https://shopee.vn/a-cat.1") };

        var giu = SearchRunner.DedupLinks(links);

        Assert.Equal([3], giu.Select(x => x.Index));
    }

    /// <summary>Hai danh mục KHÁC nhau nhưng cùng slug — nhãn đặt tên file phải khác nhau, nếu không file
    /// của link sau đè lên file của link trước.</summary>
    [Fact]
    public void FileLabel_HaiLinkTrungSlug_KhacMaDanhMuc_ChoRaHaiTenKhacNhau()
    {
        var a = FileRunCoordinator.FileLabel("https://shopee.vn/dien-thoai-cat.11036030");
        var b = FileRunCoordinator.FileLabel("https://shopee.vn/dien-thoai-cat.11036132");

        Assert.Equal("dien-thoai-11036030", a);
        Assert.Equal("dien-thoai-11036132", b);
        Assert.NotEqual(a, b);
    }

    /// <summary>Link không đọc được mã danh mục → giữ nguyên nhãn cũ (không đẻ ra đuôi "-0").</summary>
    [Fact]
    public void FileLabel_KhongCoMaDanhMuc_GiuNguyenNhan()
    {
        Assert.Equal("dien-thoai", FileRunCoordinator.FileLabel("https://shopee.vn/dien-thoai"));
        Assert.Equal("category", FileRunCoordinator.FileLabel(""));
    }
}
