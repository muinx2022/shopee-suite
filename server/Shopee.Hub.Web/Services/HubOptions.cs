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

    /// <summary>Webhook "máy client rơi offline khi đang giữ việc" (1 URL). Trống = tắt. KHÔNG lùi về ô legacy
    /// <see cref="NotifyWebhooks"/>: đó là lưới an toàn cho 3 kênh CŨ được tách ra từ nó, kênh mới này chưa từng
    /// nằm trong đó nên lùi về sẽ bắn tin máy-offline vào kênh "đơn mới".</summary>
    public const string NotifyWebhookMayOffline = "notify.webhook_may_offline";

    /// <summary>Bật cảnh báo máy offline: "1" = bật, còn lại (kể cả thiếu khoá) = tắt.</summary>
    public const string NotifyMayOfflineBat = "notify.may_offline_bat";

    /// <summary>Ngưỡng mất nhịp (PHÚT) trước khi báo máy offline. Thiếu/không parse được → mặc định
    /// <c>MachineOfflineWatchService.NguongMacDinhPhut</c>.</summary>
    public const string NotifyMayOfflinePhut = "notify.may_offline_phut";

    /// <summary>Webhook TIN TỔNG KẾT CUỐI NGÀY (1 URL). Trống = tắt. KHÔNG lùi về ô legacy
    /// <see cref="NotifyWebhooks"/> (cùng lý do như kênh máy offline: kênh mới chưa từng nằm trong đó).</summary>
    public const string NotifyWebhookTongKet = "notify.webhook_tong_ket";

    /// <summary>Bật tin tổng kết cuối ngày: "1" = bật, còn lại (kể cả thiếu khoá) = tắt.</summary>
    public const string NotifyTongKetBat = "notify.tong_ket_bat";

    /// <summary>GIỜ gửi tin tổng kết (0–23, giờ Việt Nam). Thiếu/không parse được → <c>DailyDigest.GioMacDinh</c>.</summary>
    public const string NotifyTongKetGio = "notify.tong_ket_gio";

    /// <summary>NGÀY VIỆT NAM (<c>yyyy-MM-dd</c>) của tin tổng kết ĐÃ GỬI gần nhất — chống gửi trùng khi hub
    /// restart quanh giờ gửi. Do <c>DailyDigestService</c> ghi, KHÔNG có ô nhập trên /settings.</summary>
    public const string NotifyTongKetDaGuiNgay = "notify.tong_ket_da_gui_ngay";
}
