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

    /// <summary>Key: số tài khoản mỗi lô của bộ "Chạy tự động".</summary>
    public const string AutoRunBatchSize = "autorun_batch_size";

    /// <summary>Key: số phút nghỉ giữa các lô của bộ "Chạy tự động".</summary>
    public const string AutoRunGapMinutes = "autorun_gap_minutes";

    /// <summary>Key: có tự Sync đơn hàng trong mỗi lượt "Chạy tự động" hay không.</summary>
    public const string AutoRunDoSync = "autorun_do_sync";

    /// <summary>Key: có tự Xử lý đơn (arrange + in phiếu) trong mỗi lượt "Chạy tự động" hay không.</summary>
    public const string AutoRunDoProcess = "autorun_do_process";

    /// <summary>Key: thư mục lưu phiếu/hóa đơn người dùng chọn (rỗng/thiếu → mặc định cạnh app.db).</summary>
    private const string InvoiceFolderKey = "invoice_folder";

    /// <summary>Key: chu kỳ theo dõi đơn (phút) giữa các lần tự đọc "Chờ Lấy Hàng" (thiếu/lạ → 30, kẹp [1,1440]).</summary>
    private const string OrderIntervalMinutesKey = "order_interval_minutes";

    /// <summary>Key: URL Web App Google Apps Script (kết thúc <c>/exec</c>) để đẩy đơn lên Google Sheet.</summary>
    private const string GsheetWebAppUrlKey = "gsheet_webapp_url";

    /// <summary>Key: tên tab (sheet) đích để ghi đơn (thiếu/trống → mặc định <c>"tháng 4"</c>).</summary>
    private const string GsheetTabNameKey = "gsheet_tab_name";

    /// <summary>Tên tab đích mặc định khi người dùng chưa đặt.</summary>
    public const string DefaultGsheetTabName = "tháng 4";

    /// <summary>Key: URL webhook báo "đơn mới" (Slack / Discord / Telegram) — trống → tắt tính năng.</summary>
    private const string NotifyWebhookUrlKey = "notify_webhook_url";

    /// <summary>Key: trình duyệt người dùng chọn để mở phiên (thiếu/lạ → <see cref="BrowserChoice.Auto"/>).</summary>
    private const string BrowserChoiceKey = "browser_choice";

    /// <summary>Key: cờ "Xóa profile và tạo lại" khi mở phiên mới (thiếu/lạ → false = TẮT). BẬT ⇒ mỗi phiên
    /// mở mới xóa thư mục hồ sơ trình duyệt của tài khoản rồi tạo lại sạch (phải đăng nhập lại).</summary>
    private const string SyncFreshProfileKey = "sync_fresh_profile";

    /// <summary>Key: cờ "Tự động xác nhận" khi Shopee bắt verify qua email (thiếu/lạ → false = TẮT). BẬT ⇒ app tự
    /// tìm mail Shopee + bấm link "TẠI ĐÂY" + chờ đăng nhập; TẮT ⇒ chỉ đăng nhập hộp thư rồi DỪNG cho user tay.</summary>
    private const string AutoConfirmEmailKey = "auto_confirm_email";

    private readonly Database _db;

    public SettingsRepository(Database db) => _db = db;

    /// <summary>Đọc danh sách API key KiotProxy đã lưu (đã chuẩn hóa).</summary>
    public List<string> GetKiotProxyKeys() => KiotProxyKeyParser.Parse(Get(KiotProxyApiKeys));

    /// <summary>Lưu danh sách API key KiotProxy (chuẩn hóa rồi ghép mỗi dòng một key).</summary>
    public void SetKiotProxyKeys(IEnumerable<string> keys)
        => Set(KiotProxyApiKeys, KiotProxyKeyParser.Join(keys));

    /// <summary>Đọc cấu hình "Chạy tự động" từ các khóa rời (thiếu/hỏng → mặc định an toàn, đã chuẩn hóa).</summary>
    public AutoRunSettings GetAutoRunSettings() => AutoRunSettings.Parse(
        Get(AutoRunBatchSize),
        Get(AutoRunGapMinutes),
        Get(AutoRunDoSync),
        Get(AutoRunDoProcess));

    /// <summary>Ghi cấu hình "Chạy tự động" ra các khóa rời (chuẩn hóa trước khi ghi).</summary>
    public void SetAutoRunSettings(AutoRunSettings settings)
    {
        var s = AutoRunSettings.Normalize(settings.BatchSize, settings.GapMinutes, settings.DoSync, settings.DoProcess);
        Set(AutoRunBatchSize, AutoRunSettings.IntToStorage(s.BatchSize));
        Set(AutoRunGapMinutes, AutoRunSettings.IntToStorage(s.GapMinutes));
        Set(AutoRunDoSync, AutoRunSettings.BoolToStorage(s.DoSync));
        Set(AutoRunDoProcess, AutoRunSettings.BoolToStorage(s.DoProcess));
    }

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

    /// <summary>Tên tab đích ghi đơn (đã trim); thiếu/trống → mặc định <see cref="DefaultGsheetTabName"/> ("tháng 4").</summary>
    public string GetGsheetTabName()
    {
        var v = Get(GsheetTabNameKey)?.Trim();
        return string.IsNullOrEmpty(v) ? DefaultGsheetTabName : v;
    }

    /// <summary>Lưu tên tab đích (trim); null/trống → xóa key (⇒ Get trả mặc định "tháng 4").</summary>
    public void SetGsheetTabName(string? name)
    {
        var v = name?.Trim();
        Set(GsheetTabNameKey, string.IsNullOrEmpty(v) ? null : v);
    }

    /// <summary>URL webhook báo "đơn mới" đã lưu (đã trim); trống/chưa đặt → <c>null</c> (tắt thông báo).</summary>
    public string? GetNotifyWebhookUrl()
    {
        var v = Get(NotifyWebhookUrlKey)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Lưu URL webhook báo "đơn mới" (trim); null/trống → xóa key (⇒ tắt thông báo).</summary>
    public void SetNotifyWebhookUrl(string? url)
    {
        var v = url?.Trim();
        Set(NotifyWebhookUrlKey, string.IsNullOrEmpty(v) ? null : v);
    }

    /// <summary>Trình duyệt người dùng chọn để mở phiên (thiếu/lạ → <see cref="BrowserChoice.Auto"/>).</summary>
    public BrowserChoice GetBrowserChoice() => BrowserChoices.Parse(Get(BrowserChoiceKey));

    /// <summary>Lưu lựa chọn trình duyệt (chuẩn hóa qua <see cref="BrowserChoices.ToStorage"/>).</summary>
    public void SetBrowserChoice(BrowserChoice choice) => Set(BrowserChoiceKey, BrowserChoices.ToStorage(choice));

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
