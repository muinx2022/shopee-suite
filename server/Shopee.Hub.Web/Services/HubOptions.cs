namespace Shopee.Hub.Web.Services;

/// <summary>Cấu hình khởi động của web-hub (từ appsettings / biến môi trường). Bất biến sau khi khởi động;
/// các trạng thái thay đổi lúc chạy (admin, api token, cờ điều phối) nằm ở bảng <c>settings</c> trong DB.</summary>
public sealed class HubOptions
{
    /// <summary>Thư mục dữ liệu: hub.db + files\ + dp-keys\ + backups\. Env HUB_DATA_DIR ưu tiên.</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Cho phép client (bản WPF cũ) PUT đè config/*.json không. Sau cutover đặt false để web là
    /// nguồn sự thật duy nhất — client cũ đẩy config bị 403 (nuốt lỗi phía client, không hại).</summary>
    public bool AllowClientConfigPush { get; set; } = true;
}

/// <summary>Khoá bảng <c>settings</c> dùng trong DB (gom 1 nơi tránh gõ sai chuỗi).</summary>
public static class SettingKeys
{
    public const string ApiToken = "api.token";
    public const string AdminUser = "admin.user";
    public const string AdminHash = "admin.hash";
    public const string AdminSalt = "admin.salt";
    public const string AdminIter = "admin.iter";
    public const string DispatcherEnabled = "dispatcher.enabled";
    public const string DispatcherAuto = "dispatcher.auto";
    /// <summary>LEGACY: nhiều dòng URL báo đơn mới. Còn đọc để migrate → <see cref="NotifyWebhookDonMoi"/>.</summary>
    public const string NotifyWebhooks = "notify.webhooks";

    /// <summary>Webhook khi client push đơn MỚI (1 URL). Trống = tắt.</summary>
    public const string NotifyWebhookDonMoi = "notify.webhook_don_moi";

    /// <summary>Webhook lỗi app — hub lưu đồng bộ UI; client gửi. Trống = tắt.</summary>
    public const string NotifyWebhookLoiApp = "notify.webhook_loi_app";

    /// <summary>Webhook đơn trả hàng — hub lưu đồng bộ UI; client gửi. Trống = tắt.</summary>
    public const string NotifyWebhookDonTra = "notify.webhook_don_tra";
}
