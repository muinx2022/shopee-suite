using Microsoft.Data.Sqlite;
using ShopeeStatApp.Services;

namespace Shopee.Module.Search.Tests;

/// <summary>
/// <c>SaveProduct</c> chạy <c>COUNT(*) FROM task_products WHERE task_id=?</c> sau MỖI sản phẩm, trong cùng
/// transaction và dưới khoá ghi — thiếu index theo <c>task_id</c> là mỗi SP đếm lại trên toàn bảng (chung
/// cho mọi task, tích lũy qua nhiều ngày), ack WS trễ dần tới mức extension timeout 30s. Index phải có ở CẢ
/// CSDL tạo mới lẫn CSDL cũ mở lại (nâng cấp tại chỗ).
/// </summary>
public sealed class SearchTaskStoreIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ss-search-index-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "tasks.db");

    [Fact]
    public void CsdlMoi_CoIndexTaskProducts()
    {
        _ = new SearchTaskStore(DbPath);

        Assert.True(CoIndex("ix_task_products_task"), "CSDL tạo mới thiếu index task_products(task_id)");
    }

    [Fact]
    public void CsdlCu_ChuaCoIndex_MoLenLaCo()
    {
        TaoCsdlKhongIndex();
        Assert.False(CoIndex("ix_task_products_task"), "CSDL dựng tay lẽ ra chưa có index — test đang xanh giả");

        _ = new SearchTaskStore(DbPath);   // mở = chạy Initialize

        Assert.True(CoIndex("ix_task_products_task"), "CSDL cũ nâng cấp thiếu index task_products(task_id)");
    }

    /// <summary>Index nằm trên ĐÚNG cột <c>task_id</c> của ĐÚNG bảng <c>task_products</c> — đặt tên đúng mà
    /// trỏ nhầm cột thì COUNT vẫn quét bảng.</summary>
    [Fact]
    public void Index_TroDungBangVaCot()
    {
        _ = new SearchTaskStore(DbPath);

        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT ii.name FROM pragma_index_list('task_products') il
            JOIN pragma_index_info(il.name) ii
            WHERE il.name = 'ix_task_products_task'
            """;
        var cot = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) cot.Add(reader.GetString(0));

        Assert.Equal(["task_id"], cot);
    }

    private bool CoIndex(string name)
    {
        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    /// <summary>Dựng CSDL theo schema cũ: có bảng <c>task_products</c> nhưng KHÔNG có index task_id.</summary>
    private void TaoCsdlKhongIndex()
    {
        Directory.CreateDirectory(_dir);
        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE task_products (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                shop_id INTEGER NOT NULL,
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
                shop_name TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                UNIQUE(task_id, item_id, shop_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // nhả handle WAL để xoá được thư mục tạm
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
