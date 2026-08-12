using Microsoft.Data.Sqlite;
using ShopeeStatApp.Models;
using ShopeeStatApp.Services;

namespace Shopee.Module.Search.Tests;

/// <summary>
/// Hợp đồng cột của bảng <c>shop_products</c> — nguồn DUY NHẤT của "Xuất tất cả". Trước 2026-08-12
/// <c>SaveShopProducts</c> nhận cat id của link vào tham số tên <c>shopId</c> và ghi thẳng vào cột
/// <c>shop_id</c> (guard <c>shopId &gt; 0 ? shopId : p.ShopId</c> luôn chọn cat id vì cat id luôn &gt; 0),
/// nên MỌI link xuất ra là <c>product/{catId}/{itemId}</c> → 404. Khoá lại: shop lấy từ sản phẩm, danh mục
/// của link đi vào 2 cột riêng, và CSDL cũ (chưa có 2 cột đó) phải nâng cấp được tại chỗ.
/// </summary>
public sealed class SearchTaskStoreShopIdTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ss-search-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "tasks.db");

    private const long CatId = 11035568;      // mã danh mục trong link (KHÔNG phải shop)
    private const long ShopId = 123456789;    // shop thật của sản phẩm
    private const string Link = "https://shopee.vn/thoi-trang-nam-cat.11035568";

    private static ProductResult Product(long itemId, long shopId = ShopId) => new()
    {
        ItemId = itemId,
        ShopId = shopId,
        Name = "Áo thun nam",
        ShopName = "Shop Thời Trang",
        Category = "Áo thun",
        PriceVnd = 99000,
        MonthlySold = 12,
    };

    [Fact]
    public void SaveShopProducts_GhiShopIdCuaSanPham_ConCatIdVaoCotDanhMuc()
    {
        var store = new SearchTaskStore(DbPath);

        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [Product(777)]);

        var row = ReadRow(777);
        Assert.Equal(ShopId, row.ShopId);
        Assert.Equal(CatId, row.CategoryId);
        Assert.Equal("thoi-trang-nam", row.CategoryLabel);
        Assert.Equal("Shop Thời Trang", row.ShopName);
    }

    [Fact]
    public void GetAllShopProducts_TraVeShopIdThat_LinkSanPhamDungDinhDang()
    {
        var store = new SearchTaskStore(DbPath);
        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [Product(777)]);

        var all = store.GetAllShopProducts();

        var p = Assert.Single(all);
        Assert.Equal(ShopId, p.ShopId);
        Assert.Equal($"https://shopee.vn/product/{ShopId}/777", p.Link);
    }

    [Fact]
    public void LuuLaiCungSanPham_UpsertTheoShopVaItem_KhongDeThemDong()
    {
        var store = new SearchTaskStore(DbPath);
        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [Product(777)]);

        var lai = Product(777);
        lai.MonthlySold = 34;
        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [lai]);

        var p = Assert.Single(store.GetAllShopProducts());
        Assert.Equal(34, p.MonthlySold);
        Assert.Equal(ShopId, p.ShopId);
    }

    [Fact]
    public void CsdlCu_ThieuHaiCotDanhMuc_DuocNangCapTaiCho_GiuNguyenDongCu()
    {
        TaoCsdlTruocKhiCoCotDanhMuc(itemIdCu: 111, shopIdCu: CatId);   // dòng cũ: shop_id đang mang cat id

        var store = new SearchTaskStore(DbPath);   // mở = chạy migration
        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [Product(777)]);

        Assert.Equal(2, store.GetAllShopProducts().Count);   // dòng cũ còn nguyên, không mất khi ALTER
        var moi = ReadRow(777);
        Assert.Equal(ShopId, moi.ShopId);
        Assert.Equal(CatId, moi.CategoryId);
        var cu = ReadRow(111);
        Assert.Equal(0, cu.CategoryId);           // dòng cũ không đoán được danh mục → mặc định 0/''
        Assert.Equal("", cu.CategoryLabel);

        // Mở lại lần nữa: migration phải idempotent (không ném "duplicate column name").
        var store2 = new SearchTaskStore(DbPath);
        Assert.Equal(2, store2.GetAllShopProducts().Count);
    }

    /// <summary>Dòng cũ (shop_id mang cat id) không đụng UNIQUE(shop_id,item_id) với dòng mới → nếu không
    /// thay thế thì cùng một SP thành 2 dòng và tab Danh mục đếm đôi. Cào lại phải THAY bản cũ.</summary>
    [Fact]
    public void CaoLaiSanPhamCu_DongMangCatIdBiThayThe_TabDanhMucKhongDemDoi()
    {
        var store = new SearchTaskStore(DbPath);
        ChenDongKieuCu(itemId: 777, shopIdSai: CatId, category: "Áo thun");

        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [Product(777)]);

        var p = Assert.Single(store.GetAllShopProducts());
        Assert.Equal(ShopId, p.ShopId);
        Assert.Single(store.GetShopProductsByCategory("Áo thun"));   // trước fix: 2 dòng
    }

    /// <summary>Excel per-link xuất từ CSDL GỘP theo link: lượt dừng giữa chừng (chỉ cào lại được 1 SP)
    /// không làm teo bộ dữ liệu của các lượt trước; SP của link khác không lẫn vào.</summary>
    [Fact]
    public void GetShopProductsBySourceLink_GopMoiLuot_KhongTeoKhiDungGiuaChung()
    {
        var store = new SearchTaskStore(DbPath);
        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [Product(1), Product(2), Product(3)]);
        store.SaveShopProducts(999, "link-khac", "https://shopee.vn/khac-cat.999", [Product(50)]);

        var laiMotSp = Product(1);
        laiMotSp.MonthlySold = 99;   // lượt hôm nay dừng sớm, mới cào lại được đúng SP này
        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [laiMotSp]);

        var gop = store.GetShopProductsBySourceLink(Link);

        Assert.Equal(3, gop.Count);                                     // đủ 3 SP, không teo còn 1
        Assert.Equal(99, gop.Single(p => p.ItemId == 1).MonthlySold);   // giữ bản quét mới nhất
        Assert.DoesNotContain(gop, p => p.ItemId == 50);                // không lẫn link khác
    }

    /// <summary>Extension search không trả tên shop → shop_name rỗng; cột "Shop" khi xuất fallback sang
    /// nhãn danh mục (đúng thứ cột này vẫn hiển thị trước 2026-08-12), có tên thật thì tên thật thắng.</summary>
    [Fact]
    public void TenShopRong_XuatFallbackSangNhanDanhMuc()
    {
        var store = new SearchTaskStore(DbPath);
        var khongTen = Product(777);
        khongTen.ShopName = "";
        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [khongTen]);

        Assert.Equal("thoi-trang-nam", Assert.Single(store.GetAllShopProducts()).ShopName);
        Assert.Equal("thoi-trang-nam", Assert.Single(store.GetShopProductsBySourceLink(Link)).ShopName);

        store.SaveShopProducts(CatId, "thoi-trang-nam", Link, [Product(777)]);   // có tên thật
        Assert.Equal("Shop Thời Trang", Assert.Single(store.GetAllShopProducts()).ShopName);
    }

    /// <summary>DELETE dọn bản cũ chạy theo item_id MỖI sản phẩm — thiếu index trên item_id là quét toàn
    /// bảng cho từng SP (đo thật ~60× chậm, giữ khoá ghi SQLite chặn lane khác + nút Dừng). Index phải có
    /// ở CẢ CSDL tạo mới lẫn CSDL cũ nâng cấp.</summary>
    [Fact]
    public void CsdlMoiVaCu_DeuCoIndexItemId()
    {
        _ = new SearchTaskStore(DbPath);                       // tạo mới
        Assert.True(CoIndexItemId(), "CSDL tạo mới thiếu index item_id");

        Dispose();                                             // xoá, dựng lại theo schema cũ
        TaoCsdlTruocKhiCoCotDanhMuc(itemIdCu: 111, shopIdCu: CatId);
        _ = new SearchTaskStore(DbPath);                       // mở = migration
        Assert.True(CoIndexItemId(), "CSDL cũ nâng cấp thiếu index item_id");
    }

    private bool CoIndexItemId()
    {
        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_shop_products_item_id'";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    /// <summary>Chèn thẳng 1 dòng kiểu CŨ (shop_id mang cat id) vào CSDL đã có schema mới —
    /// mô phỏng dữ liệu để lại từ bản trước 2026-08-12.</summary>
    private void ChenDongKieuCu(long itemId, long shopIdSai, string category)
    {
        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shop_products (shop_id, item_id, name, category, source_link, scanned_at)
            VALUES ($shop, $item, 'SP cũ', $cat, $link, '2026-08-01T00:00:00.0000000Z')
            """;
        cmd.Parameters.AddWithValue("$shop", shopIdSai);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$link", Link);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Dựng CSDL theo ĐÚNG schema trước 2026-08-12 (không có category_id/category_label).</summary>
    private void TaoCsdlTruocKhiCoCotDanhMuc(long itemIdCu, long shopIdCu)
    {
        Directory.CreateDirectory(_dir);
        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE shop_products (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                shop_id INTEGER NOT NULL,
                shop_name TEXT NOT NULL DEFAULT '',
                source_link TEXT NOT NULL DEFAULT '',
                item_id INTEGER NOT NULL,
                name TEXT NOT NULL DEFAULT '',
                price_vnd REAL NOT NULL DEFAULT 0,
                original_price_vnd REAL NOT NULL DEFAULT 0,
                monthly_sold INTEGER NOT NULL DEFAULT 0,
                rating REAL NOT NULL DEFAULT 0,
                liked_count INTEGER NOT NULL DEFAULT 0,
                comment_count INTEGER NOT NULL DEFAULT 0,
                shop_location TEXT NOT NULL DEFAULT '',
                image_url TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT '',
                scanned_at TEXT NOT NULL,
                UNIQUE(shop_id, item_id)
            );
            INSERT INTO shop_products (shop_id, item_id, name, scanned_at)
            VALUES ($shop, $item, 'SP cũ', '2026-08-01T00:00:00.0000000Z');
            """;
        cmd.Parameters.AddWithValue("$shop", shopIdCu);
        cmd.Parameters.AddWithValue("$item", itemIdCu);
        cmd.ExecuteNonQuery();
    }

    private (long ShopId, string ShopName, long CategoryId, string CategoryLabel) ReadRow(long itemId)
    {
        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT shop_id, shop_name, category_id, category_label FROM shop_products WHERE item_id = $i";
        cmd.Parameters.AddWithValue("$i", itemId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"Không thấy dòng item_id={itemId}");
        return (reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // nhả handle WAL để xoá được thư mục tạm
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
