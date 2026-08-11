using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Lưu/đọc cấu hình dạng key-value trong bảng <c>settings</c>.
/// </summary>
public class SettingsRepository
{
    /// <summary>Key lưu danh sách API key KiotProxy (mỗi dòng một key).</summary>
    public const string KiotProxyApiKeys = "kiotproxy_api_key";

    /// <summary>Cờ đã CHẠY migration gộp <c>Account.ProxyKey</c> cố định (cơ chế cũ) vào pool KiotProxy chung.
    /// Có giá trị "1" ⇒ đã migrate, KHÔNG chạy lại (idempotent). Xem <see cref="ProxyKeyPoolMigration"/>.</summary>
    public const string ProxyKeyMigratedV1 = "proxykey_migrated_v1";

    /// <summary>Key: thư mục lưu phiếu/hóa đơn người dùng chọn (rỗng/thiếu → mặc định cạnh app.db).</summary>
    private const string InvoiceFolderKey = "invoice_folder";

    /// <summary>Key: chu kỳ theo dõi đơn (phút) giữa các lần tự đọc "Chờ Lấy Hàng" (thiếu/lạ → 30, kẹp [1,1440]).</summary>
    private const string OrderIntervalMinutesKey = "order_interval_minutes";

    /// <summary>Key: URL Web App Google Apps Script (kết thúc <c>/exec</c>) để đẩy đơn lên Google Sheet.</summary>
    private const string GsheetWebAppUrlKey = "gsheet_webapp_url";

    /// <summary>Key: tên tab (sheet) đích để ghi đơn — dùng làm OVERRIDE: có giá trị = ghi cố định vào tab đó;
    /// trống/thiếu = TỰ ĐỘNG theo tháng ("Tháng MM-yyyy", caller tự resolve qua <see cref="GsheetTabName"/>).</summary>
    private const string GsheetTabNameKey = "gsheet_tab_name";

    /// <summary>Key: link (hoặc ID) bảng tính Google Sheet THỨ HAI — file phụ nhận thêm cột A–E. Trống/thiếu =
    /// KHÔNG ghi file phụ. KHÁC <see cref="GsheetWebAppUrlKey"/>: đây là link docs.google.com/spreadsheets/…,
    /// không phải Web App <c>/exec</c>.</summary>
    private const string GsheetSheet2Key = "gsheet_sheet2";

    /// <summary>Tên tab mặc định LEGACY — CHỈ còn dùng cho backfill migration cột <c>orders.gsheet_tab</c>
    /// (đơn ĐÃ ghi sheet trước bản "tab theo tháng" nằm ở tab tên setting cũ, mặc định "tháng 4"). KHÔNG còn
    /// là giá trị trả về của <see cref="GetGsheetTabName"/> nữa (trống giờ trả chuỗi rỗng).</summary>
    public const string DefaultGsheetTabName = "tháng 4";

    /// <summary>Key LEGACY: một URL chung (đơn mới + lỗi app). Còn dùng để migrate lazy sang 3 key mới.</summary>
    private const string NotifyWebhookUrlKey = "notify_webhook_url";

    /// <summary>Key: webhook khi có đơn mới (Slack / Discord / Telegram) — trống → tắt.</summary>
    private const string NotifyWebhookDonMoiKey = "notify_webhook_url_don_moi";

    /// <summary>Key: webhook khi lỗi app (hiện: không đặt được địa chỉ lấy hàng) — trống → tắt.</summary>
    private const string NotifyWebhookLoiAppKey = "notify_webhook_url_loi_app";

    /// <summary>Key: webhook khi check flow phát hiện đơn trả hàng mới — trống → tắt.</summary>
    private const string NotifyWebhookDonTraKey = "notify_webhook_url_don_tra";

    // Khoá 'browser_choice' đã BỎ 11/08/2026 (app chỉ chạy Brave — xem BrowserLocator). Dòng cũ trong bảng
    // settings của máy đang dùng cứ để nguyên: không ai đọc nữa, xoá đi chỉ thêm một bước migrate vô ích.

    /// <summary>Key: cờ "Xóa profile và tạo lại" khi mở phiên mới (thiếu/lạ → false = TẮT). BẬT ⇒ mỗi phiên
    /// mở mới xóa thư mục hồ sơ trình duyệt của tài khoản rồi tạo lại sạch (phải đăng nhập lại).</summary>
    private const string SyncFreshProfileKey = "sync_fresh_profile";

    /// <summary>Key: cờ "Tự động xác nhận" khi Shopee bắt verify qua email (thiếu/lạ → false = TẮT). BẬT ⇒ app tự
    /// tìm mail Shopee + bấm link "TẠI ĐÂY" + chờ đăng nhập; TẮT ⇒ chỉ đăng nhập hộp thư rồi DỪNG cho user tay.</summary>
    private const string AutoConfirmEmailKey = "auto_confirm_email";

    /// <summary>Key: các cột PHỤ đang ẨN ở bảng màn Đơn hàng (danh sách id ngăn bằng dấu phẩy).
    /// Thiếu/trống = hiện đủ mọi cột. Lựa chọn hiển thị này là của RIÊNG MÁY NÀY (không đẩy lên Hub).</summary>
    private const string OrdersHiddenColumnsKey = "orders_hidden_columns";

    private readonly Database _db;

    public SettingsRepository(Database db) => _db = db;

    /// <summary>Đọc danh sách API key KiotProxy đã lưu (đã chuẩn hóa).</summary>
    public List<string> GetKiotProxyKeys() => KiotProxyKeyParser.Parse(Get(KiotProxyApiKeys));

    /// <summary>Lưu danh sách API key KiotProxy (chuẩn hóa rồi ghép mỗi dòng một key).</summary>
    public void SetKiotProxyKeys(IEnumerable<string> keys)
        => Set(KiotProxyApiKeys, KiotProxyKeyParser.Join(keys));

    /// <summary>
    /// Thư mục lưu phiếu/hóa đơn THỰC DÙNG: giá trị người dùng đã chọn (đã trim) nếu có, ngược lại mặc định
    /// cạnh app.db (<see cref="Database.DefaultInvoiceDir"/>). NGUỒN DUY NHẤT cho cả 3 nơi — xử lý đơn (lưu
    /// phiếu), link "In phiếu" ở màn Đơn hàng (mở phiếu) và ô hiển thị ở Cài đặt — để không nơi nào lệch chỗ.
    /// </summary>
    public string GetInvoiceFolder()
    {
        var folder = AppGeneralSettings.Parse(Get(InvoiceFolderKey), null).InvoiceFolder; // trim; rỗng nếu chưa đặt
        return string.IsNullOrEmpty(folder) ? _db.DefaultInvoiceDir() : folder;
    }

    /// <summary>Lưu thư mục lưu hóa đơn người dùng chọn (rỗng → xóa cấu hình ⇒ quay về mặc định app).</summary>
    public void SetInvoiceFolder(string? path)
    {
        var folder = AppGeneralSettings.Parse(path, null).InvoiceFolder; // trim
        Set(InvoiceFolderKey, string.IsNullOrEmpty(folder) ? null : folder);
    }

    /// <summary>Chu kỳ theo dõi đơn (phút): config đã kẹp [1,1440]; thiếu/lạ → 30.</summary>
    public int GetOrderIntervalMinutes()
        => AppGeneralSettings.Parse(null, Get(OrderIntervalMinutesKey)).OrderIntervalMinutes;

    /// <summary>Lưu chu kỳ theo dõi đơn (phút) — chuẩn hóa (kẹp [1,1440]) trước khi ghi.</summary>
    public void SetOrderIntervalMinutes(int minutes)
    {
        var norm = AppGeneralSettings.Normalize(null, minutes).OrderIntervalMinutes;
        Set(OrderIntervalMinutesKey, AppGeneralSettings.IntToStorage(norm));
    }

    /// <summary>URL Web App Google Sheet đã lưu (đã trim); trống/chưa đặt → <c>null</c> (tắt tính năng đồng bộ).</summary>
    public string? GetGsheetWebAppUrl()
    {
        var v = Get(GsheetWebAppUrlKey)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Lưu URL Web App Google Sheet (trim); null/trống → xóa key (⇒ tắt đồng bộ).</summary>
    public void SetGsheetWebAppUrl(string? url)
    {
        var v = url?.Trim();
        Set(GsheetWebAppUrlKey, string.IsNullOrEmpty(v) ? null : v);
    }

    /// <summary>Tên tab đích ghi đơn — OVERRIDE do người dùng đặt (đã trim); thiếu/trống → <c>""</c> (chuỗi
    /// rỗng) = TỰ ĐỘNG theo tháng. Caller tự resolve chuỗi rỗng thành <see cref="GsheetTabName.ForMonth"/>.</summary>
    public string GetGsheetTabName()
    {
        var v = Get(GsheetTabNameKey)?.Trim();
        return string.IsNullOrEmpty(v) ? string.Empty : v;
    }

    /// <summary>Lưu tên tab đích override (trim); null/trống → xóa key (⇒ Get trả "" = tự động theo tháng).</summary>
    public void SetGsheetTabName(string? name)
    {
        var v = name?.Trim();
        Set(GsheetTabNameKey, string.IsNullOrEmpty(v) ? null : v);
    }

    /// <summary>Link/ID bảng tính Google Sheet thứ hai đã lưu (đã trim); trống/chưa đặt → <c>null</c> (không ghi
    /// file phụ). Caller bóc ID bằng <see cref="GsheetConfigSync.BocIdSheet"/> trước khi gửi lên Apps Script.</summary>
    public string? GetGsheetSheet2()
    {
        var v = Get(GsheetSheet2Key)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Lưu link/ID bảng tính Google Sheet thứ hai (trim); null/trống → xóa key (⇒ không ghi file phụ).</summary>
    public void SetGsheetSheet2(string? s)
    {
        var v = s?.Trim();
        Set(GsheetSheet2Key, string.IsNullOrEmpty(v) ? null : v);
    }

    /// <summary>URL webhook "có đơn mới" (đã trim). Lazy migrate: nếu 3 key mới đều trống mà key legacy còn URL
    /// → trả URL legacy (user cũ vẫn nhận tin cho tới khi Lưu lại Settings).</summary>
    public string? GetNotifyWebhookUrlDonMoi() => GetNotifyWebhookMoiHoacLegacy(NotifyWebhookDonMoiKey, dungLegacy: true);

    /// <summary>URL webhook "lỗi app". Lazy migrate giống <see cref="GetNotifyWebhookUrlDonMoi"/>.</summary>
    public string? GetNotifyWebhookUrlLoiApp() => GetNotifyWebhookMoiHoacLegacy(NotifyWebhookLoiAppKey, dungLegacy: true);

    /// <summary>URL webhook "có đơn trả hàng". Không dùng legacy (trước đây không có loại tin này).</summary>
    public string? GetNotifyWebhookUrlDonTra() => GetNotifyWebhookMoiHoacLegacy(NotifyWebhookDonTraKey, dungLegacy: false);

    /// <summary>Lưu 3 webhook notify cùng lúc; xóa key legacy để không “hồi sinh” URL cũ khi xóa hết ô mới.</summary>
    public void SetNotifyWebhookUrls(string? donMoi, string? loiApp, string? donTra)
    {
        Set(NotifyWebhookDonMoiKey, ChuanHoaWebhookHoacNull(donMoi));
        Set(NotifyWebhookLoiAppKey, ChuanHoaWebhookHoacNull(loiApp));
        Set(NotifyWebhookDonTraKey, ChuanHoaWebhookHoacNull(donTra));
        Set(NotifyWebhookUrlKey, null);
    }

    private string? GetNotifyWebhookMoiHoacLegacy(string keyMoi, bool dungLegacy)
    {
        var moi = Get(keyMoi)?.Trim();
        if (!string.IsNullOrEmpty(moi))
        {
            return moi;
        }

        if (!dungLegacy)
        {
            return null;
        }

        // Chỉ fallback legacy khi CHƯA từng lưu bản 3 ô (cả 3 key mới trống).
        if (!string.IsNullOrEmpty(Get(NotifyWebhookDonMoiKey)?.Trim())
            || !string.IsNullOrEmpty(Get(NotifyWebhookLoiAppKey)?.Trim())
            || !string.IsNullOrEmpty(Get(NotifyWebhookDonTraKey)?.Trim()))
        {
            return null;
        }

        var legacy = Get(NotifyWebhookUrlKey)?.Trim();
        return string.IsNullOrEmpty(legacy) ? null : legacy;
    }

    private static string? ChuanHoaWebhookHoacNull(string? url)
    {
        var v = url?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Cờ "Xóa profile và tạo lại" khi mở phiên mới: nhận "true"/"1" (bất kể hoa/thường) ⇒ true;
    /// thiếu/rỗng/lạ ⇒ false (mặc định TẮT — hành vi cũ không đổi).</summary>
    public bool GetSyncFreshProfile()
    {
        var v = Get(SyncFreshProfileKey)?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return false;
        }

        return bool.TryParse(v, out var b) ? b : v == "1";
    }

    /// <summary>Lưu cờ "Xóa profile và tạo lại" ("true"/"false").</summary>
    public void SetSyncFreshProfile(bool value) => Set(SyncFreshProfileKey, value ? "true" : "false");

    /// <summary>Cờ "Tự động xác nhận" khi verify email: "true"/"1" ⇒ true; thiếu/rỗng/lạ ⇒ false (mặc định TẮT —
    /// app chỉ đăng nhập hộp thư rồi dừng cho user tự bấm link).</summary>
    public bool GetAutoConfirmEmail()
    {
        var v = Get(AutoConfirmEmailKey)?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return false;
        }

        return bool.TryParse(v, out var b) ? b : v == "1";
    }

    /// <summary>Lưu cờ "Tự động xác nhận" ("true"/"false").</summary>
    public void SetAutoConfirmEmail(bool value) => Set(AutoConfirmEmailKey, value ? "true" : "false");

    /// <summary>Id các cột PHỤ đang ẨN ở bảng đơn (đã trim, bỏ mục rỗng, không trùng). Thiếu/trống → tập rỗng
    /// = hiện đủ mọi cột. So sánh id theo ĐÚNG chữ (Ordinal) — id do view đặt, không phải chuỗi người dùng gõ.</summary>
    public HashSet<string> GetHiddenOrderColumns()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var raw = Get(OrdersHiddenColumnsKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part);
        }

        return set;
    }

    /// <summary>Lưu id các cột phụ đang ẩn; tập rỗng → xóa key (⇒ lần đọc sau trả tập rỗng = hiện đủ).</summary>
    public void SetHiddenOrderColumns(IEnumerable<string> ids)
    {
        var clean = ids
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Set(OrdersHiddenColumnsKey, clean.Count == 0 ? null : string.Join(",", clean));
    }

    /// <summary>Lấy giá trị theo key, trả null nếu chưa có.</summary>
    public string? Get(string key)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }

    /// <summary>Ghi (thêm mới hoặc cập nhật) giá trị theo key.</summary>
    public void Set(string key, string? value)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO settings (key, value) VALUES ($key, $value)
                            ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
