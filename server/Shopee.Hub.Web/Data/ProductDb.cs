using Npgsql;

namespace Shopee.Hub;

/// <summary>
/// Kho dữ liệu SẢN PHẨM trên Postgres (Docker chạy cạnh hub trong VM) — thay dần workbook Excel sync-qua-file.
/// Hạng mục này CHỈ là HẠ TẦNG: connect + migration + ping; API /products/* làm ở hạng mục sau. SQL viết TAY
/// như <see cref="HubDatabase"/> (không ORM). Hub KHÔNG được crash nếu Postgres chưa lên (lúc VM boot, Docker có
/// thể lên SAU hub) → <see cref="ProductDbInitService"/> retry; <see cref="IsReady"/>=false tới khi migrate xong.
///
/// Ghi chú nghiệp vụ (bảng product_rows/product_sheets ở migration 1):
///  - <c>row_no</c> = số dòng TUYỆT ĐỐI giữ nguyên từ Excel (header dòng 1, data từ dòng 2) → migration lossless.
///  - Mọi cột dữ liệu để <c>text</c> để copy nguyên trạng từ Excel; runner phía client tự parse số/tiền.
///  - <c>sheet</c> = tên sheet cũ, là ĐỊNH DANH shop, khớp ledger key hiện tại.
/// </summary>
public sealed partial class ProductDb : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Danh sách migration áp TUẦN TỰ theo version; bản đã ghi trong schema_version thì bỏ qua.
    /// (Style SQL-tay như <see cref="HubDatabase.EnsureSchema"/>; Postgres chạy DDL trong transaction nên
    /// mỗi bản áp nguyên-khối hoặc rollback sạch.)</summary>
    private static readonly (int Version, string Sql)[] Migrations =
    {
        (1, @"
CREATE TABLE product_rows (
  id             bigserial PRIMARY KEY,
  account_id     text NOT NULL,
  sheet          text NOT NULL,
  row_no         int  NOT NULL,
  link           text,
  price_original text,
  price_sale     text,
  sku            text,
  item_id        text,
  name_original  text,
  name_rewritten text,
  category       text,
  shop_name      text,
  rating         text,
  sold_month     text,
  likes          text,
  reviews        text,
  region         text,
  image          text,
  meta_shop_id   text,
  meta_item_id   text,
  extra          jsonb,
  rewritten_at   timestamptz,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  updated_by     text,
  UNIQUE (account_id, sheet, row_no)
);
CREATE INDEX ix_pr_acct_sheet ON product_rows(account_id, sheet);
CREATE INDEX ix_pr_item ON product_rows(item_id);
CREATE TABLE product_sheets (
  account_id  text NOT NULL,
  sheet       text NOT NULL,
  source_file text,
  imported_at timestamptz,
  updated_at  timestamptz,
  PRIMARY KEY (account_id, sheet)
);"),
    };

    public ProductDb(string connString)
    {
        // Pooling mặc định (Npgsql bật sẵn) → connection tái dùng cho ping/migrate/API sau.
        _dataSource = NpgsqlDataSource.Create(connString);
    }

    /// <summary>true SAU khi <see cref="InitAsync"/> (connect + migrate) thành công. /health + API sau đọc cờ này.</summary>
    public bool IsReady { get; private set; }

    /// <summary>SELECT 1 với timeout ngắn ~2s. KHÔNG ném — trả false khi lỗi (dùng cho /health, không được treo).</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await using var cmd = _dataSource.CreateCommand("SELECT 1;");
            await cmd.ExecuteScalarAsync(cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Mở connection, chạy migration, set <see cref="IsReady"/>. Lỗi thì NÉM (caller lo retry).</summary>
    public async Task InitAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await ApplyMigrationsAsync(conn, ct);
        IsReady = true;
    }

    private static async Task ApplyMigrationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Bảng theo dõi bản đã áp (như user_version của SQLite nhưng nhiều-bản).
        await using (var c = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_version(version int PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());", conn))
            await c.ExecuteNonQueryAsync(ct);

        var applied = new HashSet<int>();
        await using (var c = new NpgsqlCommand("SELECT version FROM schema_version;", conn))
        await using (var rd = await c.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) applied.Add(rd.GetInt32(0));

        // Áp bản còn thiếu, MỖI bản trong 1 transaction (DDL + ghi schema_version cùng commit → idempotent).
        foreach (var (version, sql) in Migrations.OrderBy(m => m.Version))
        {
            if (applied.Contains(version)) continue;
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var c = new NpgsqlCommand(sql, conn, tx))
                await c.ExecuteNonQueryAsync(ct);
            await using (var c = new NpgsqlCommand("INSERT INTO schema_version(version) VALUES($1);", conn, tx))
            {
                c.Parameters.AddWithValue(version);
                await c.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
