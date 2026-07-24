using Microsoft.Data.Sqlite;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Quản lý kết nối và khởi tạo cơ sở dữ liệu SQLite cục bộ.
/// </summary>
public class Database
{
    /// <summary>Đường dẫn file .db đang dùng.</summary>
    public string Path { get; }

    private readonly string _connectionString;

    /// <summary>
    /// Khởi tạo database. Nếu <paramref name="dbPath"/> null thì dùng đường dẫn mặc định
    /// (%APPDATA%\XuLyDonShopee\app.db trên Windows, ~/.config/XuLyDonShopee/app.db trên Linux).
    /// Tự tạo thư mục và các bảng nếu chưa có.
    /// </summary>
    public Database(string? dbPath = null)
    {
        Path = dbPath ?? DefaultPath();

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path
        }.ToString();

        Initialize();
    }

    /// <summary>Đường dẫn file DB mặc định trong thư mục dữ liệu ứng dụng của người dùng.</summary>
    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "XuLyDonShopee", "app.db");
    }

    /// <summary>
    /// Thư mục MẶC ĐỊNH lưu phiếu/hóa đơn khi người dùng chưa chọn ở Cài đặt:
    /// <c>{thư mục chứa app.db}\Phieu-giao-hang</c> (nằm cạnh dữ liệu app). Là nguồn mặc định của
    /// <see cref="SettingsRepository.GetInvoiceFolder"/>.
    /// </summary>
    public string DefaultInvoiceDir()
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? ".", "Phieu-giao-hang");

    /// <summary>Mở một kết nối mới (đã Open). Caller chịu trách nhiệm Dispose.</summary>
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS accounts (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    Email      TEXT NOT NULL,
    Password   TEXT NOT NULL,
    Phone      TEXT,
    Cookie     TEXT,
    Note       TEXT,
    ProxyKey   TEXT,
    PickupAddress TEXT,
    VerifyEmail TEXT,
    VerifyEmailPassword TEXT,
    Status     TEXT NOT NULL,
    CreatedAt  TEXT NOT NULL,
    UpdatedAt  TEXT NOT NULL,
    verify_failed_at TEXT
);

CREATE TABLE IF NOT EXISTS proxies (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    Host       TEXT NOT NULL,
    Port       INTEGER NOT NULL,
    Username   TEXT,
    Password   TEXT,
    Type       TEXT NOT NULL,
    Status     TEXT NOT NULL,
    CreatedAt  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);

CREATE TABLE IF NOT EXISTS orders (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id         INTEGER NOT NULL,
    shop_id            TEXT,
    order_sn           TEXT NOT NULL,
    shopee_order_id    TEXT,
    buyer_username     TEXT,
    items_json         TEXT,
    item_count         INTEGER,
    item_summary       TEXT,
    sku                TEXT,
    gsheet_synced_at   TEXT,
    gsheet_file_url    TEXT,
    gsheet_da_huy      INTEGER,
    gsheet_da_co_van_don INTEGER,
    gsheet_tab         TEXT,
    hub_synced_at      TEXT,
    hub_slip_synced_at TEXT,
    sold_counted_at    TEXT,
    total_price        INTEGER,
    total_price_text   TEXT,
    final_amount       INTEGER,
    final_amount_text  TEXT,
    payment_method     TEXT,
    status             TEXT,
    status_description TEXT,
    cancel_reason      TEXT,
    channel            TEXT,
    carrier            TEXT,
    tracking_number    TEXT,
    synced_at          TEXT,
    created_at         TEXT,
    updated_at         TEXT,
    UNIQUE(account_id, order_sn)
);";
        cmd.ExecuteNonQuery();

        // Migration cho DB CŨ đã tồn tại: CREATE TABLE IF NOT EXISTS ở trên KHÔNG sửa bảng cũ, nên
        // thêm cột mới bằng ALTER TABLE ADD COLUMN (không phá dữ liệu người dùng đang có).
        EnsureColumn(conn, "accounts", "ProxyKey", "TEXT");
        EnsureColumn(conn, "accounts", "PickupAddress", "TEXT");

        // Hộp thư Hotmail/Outlook nhận mail xác minh Shopee + mật khẩu hộp thư — thêm cho DB CŨ (chưa có 2 cột).
        EnsureColumn(conn, "accounts", "VerifyEmail", "TEXT");
        EnsureColumn(conn, "accounts", "VerifyEmailPassword", "TEXT");

        // Cờ "TK chưa xác nhận": thời điểm (UTC ISO-8601) lần cuối autorun phát hiện phiên còn kẹt ở trang
        // verify/login/captcha khi kết thúc lượt (NULL = bình thường). Bền qua restart để danh sách TK lỗi còn
        // đó hôm sau; tự gỡ khi phiên đăng nhập được. Thêm cho DB CŨ.
        EnsureColumn(conn, "accounts", "verify_failed_at", "TEXT");

        // "Số tiền cuối cùng" lấy từ trang chi tiết đơn (cột "Ước tính" ở màn Đơn hàng) — thêm cho DB CŨ đã
        // có bảng orders (kiểm cột tồn tại trước khi ALTER, không phá dữ liệu sẵn có).
        EnsureColumn(conn, "orders", "final_amount", "INTEGER");
        EnsureColumn(conn, "orders", "final_amount_text", "TEXT");

        // SKU sản phẩm (5 ký tự alphanumeric cuối cùng của tên sản phẩm) — thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "sku", "TEXT");

        // Cờ chống đẩy TRÙNG lên Google Sheet: gsheet_synced_at = thời điểm đã ghi đơn lên sheet;
        // gsheet_file_url = link file phiếu đã upload (cột C); gsheet_da_huy = trạng thái hủy ĐÃ ĐẨY lần
        // trước (0/1; NULL = chưa đẩy) để đổi màu khi trạng thái hủy thay đổi; gsheet_da_co_van_don = lần đẩy
        // gần nhất có gửi mã vận đơn chưa (0/1; NULL = chưa đẩy) để tự điền cột B khi vận đơn xuất hiện sau.
        // Thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "gsheet_synced_at", "TEXT");
        EnsureColumn(conn, "orders", "gsheet_file_url", "TEXT");
        EnsureColumn(conn, "orders", "gsheet_da_huy", "INTEGER");
        EnsureColumn(conn, "orders", "gsheet_da_co_van_don", "INTEGER");

        // Tab (sheet) đã ghi LẦN ĐẦU của đơn: từ bản "tab theo tháng" trở đi, tab đích tự tính "Tháng MM-yyyy"
        // (hoặc override ở Cài đặt). Nhớ tab lần đầu để đơn cũ CẬP NHẬT lại (bổ sung vận đơn/phiếu/đổi màu hủy)
        // vẫn về ĐÚNG tab đã ghi — không nhân đôi dòng khi sang tháng mới. Thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "gsheet_tab", "TEXT");

        // Backfill MỘT LẦN cho đơn ĐÃ ghi sheet TRƯỚC bản này (gsheet_synced_at NOT NULL) nhưng chưa có gsheet_tab:
        // chúng nằm ở tab tên trong setting cũ (mặc định LEGACY "tháng 4"). Nhớ lại kẻo lần đẩy cập nhật sau nhân
        // đôi dòng ở tab tháng mới. NULLIF(TRIM(...),'') coi setting chuỗi-trắng như chưa đặt → mặc định "tháng 4".
        // Idempotent tự nhiên: chỉ chạm dòng gsheet_tab CÒN NULL (chạy lại lần 2 không đổi gì).
        BackfillGsheetTab(conn);

        // Cờ chống đẩy TRÙNG lên HUB đơn hàng (module Đơn hàng đẩy đơn lên hub sau mỗi lượt Sync):
        // hub_synced_at = thời điểm đơn ĐÃ được hub nhận OK (NULL = chưa đẩy, còn trong hàng đợi ngầm → lượt
        // sync sau tự đẩy bù khi hub sống lại). Thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "hub_synced_at", "TEXT");

        // Cờ chống đẩy TRÙNG FILE PHIẾU lên HUB (module Đơn hàng đẩy phiếu PDF sau khi đơn đã lên hub):
        // hub_slip_synced_at = thời điểm phiếu của đơn ĐÃ được hub lưu OK (NULL = chưa đẩy phiếu → lượt sync sau
        // tự đẩy khi có file phiếu local hợp lệ). Thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "hub_slip_synced_at", "TEXT");

        // Cờ chống ĐẾM TRÙNG "Đã bán" theo SKU trên kho hub: sold_counted_at = thời điểm đơn ĐÃ được tính +1
        // (hoặc grandfather = đơn đã-giao-sẵn được đánh dấu KHÔNG +1). NULL = chưa xử lý đếm. Đơn CHUYỂN từ
        // chưa-giao → đã-giao mới +1; set cờ SAU khi hub +1 OK (kẻo mất đếm nếu hub lỗi). Thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "sold_counted_at", "TEXT");

        // Mã shop (mô hình 1 tài khoản subaccount = nhiều shop): gắn shop_id vào mỗi đơn để lọc theo shop khi đẩy
        // GSheet (cột Tên Shop = tên đăng nhập shop) — KHÔNG đẩy nhầm đơn shop này với tên shop kia. Đơn CŨ shop_id
        // NULL (vẫn đẩy như trước theo account khi lọc shopId=null). KHÔNG backfill. Thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "shop_id", "TEXT");

        // Tên đăng nhập shop (vd "alina99.store") để HIỂN THỊ/LỌC màn Đơn hàng — khác shop_id (SỐ). Sync gắn từ
        // `_currentShopLogin` (callback cầu nối set trước khi lưu); đơn cũ NULL (fallback "(shop ?)" khi hiển thị).
        // KHÔNG backfill. Thêm cho DB CŨ.
        EnsureColumn(conn, "orders", "shop_login", "TEXT");
    }

    /// <summary>
    /// Đảm bảo bảng <paramref name="table"/> có cột <paramref name="column"/>. Nếu chưa có (DB cũ) thì
    /// <c>ALTER TABLE ... ADD COLUMN</c> — chỉ THÊM cột, KHÔNG đụng dữ liệu sẵn có. Idempotent: chạy
    /// nhiều lần không lỗi (đã có cột thì bỏ qua). Tên bảng/cột/kiểu là hằng nội bộ nên nội suy an toàn.
    /// </summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string columnType)
    {
        // Đọc danh sách cột hiện có; cột "name" nằm ở chỉ số 1 của PRAGMA table_info.
        var exists = false;
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnType};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Backfill cột <c>orders.gsheet_tab</c> cho đơn ĐÃ ghi sheet trước bản "tab theo tháng": tab đích của
    /// chúng là tên tab CŨ trong setting <c>gsheet_tab_name</c> (chuỗi-trắng/thiếu → mặc định LEGACY
    /// <see cref="SettingsRepository.DefaultGsheetTabName"/> "tháng 4"). Chỉ chạm đơn có
    /// <c>gsheet_synced_at NOT NULL</c> và <c>gsheet_tab IS NULL</c> ⇒ idempotent (lần 2 không đổi; đơn chưa
    /// đẩy giữ NULL). <c>NULLIF(TRIM(...),'')</c> để setting toàn khoảng trắng không thành tab sai.
    /// </summary>
    private static void BackfillGsheetTab(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE orders SET gsheet_tab = COALESCE(
    NULLIF(TRIM((SELECT value FROM settings WHERE key = 'gsheet_tab_name')), ''), 'tháng 4')
WHERE gsheet_synced_at IS NOT NULL AND gsheet_tab IS NULL;";
        cmd.ExecuteNonQuery();
    }
}
