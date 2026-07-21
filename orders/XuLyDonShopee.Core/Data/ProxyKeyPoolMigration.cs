using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Migration MỘT LẦN: gộp mọi <see cref="Models.Account.ProxyKey"/> KHÔNG rỗng (API key KiotProxy gán cố
/// định cho từng tài khoản theo cơ chế CŨ) vào pool KiotProxy CHUNG (<see cref="SettingsRepository"/>) — để
/// KHÔNG mất key sẵn có khi chuyển sang cấp phát key theo pool lúc chạy. Idempotent qua cờ settings
/// <see cref="SettingsRepository.ProxyKeyMigratedV1"/>. KHÔNG xóa cột/field <c>ProxyKey</c> (giữ vestigial —
/// tránh phá schema/test; giá trị cũ cứ nằm đó, chỉ ngừng dùng).
/// </summary>
public static class ProxyKeyPoolMigration
{
    /// <summary>
    /// Chạy migration nếu CHƯA chạy (cờ chưa đặt). Đọc pool hiện có + mọi <c>ProxyKey</c> tài khoản (trim,
    /// dedup, giữ thứ tự xuất hiện đầu tiên) rồi ghi lại pool CHUNG và đặt cờ. Trả <c>true</c> nếu ĐÃ chạy
    /// lần này, <c>false</c> nếu bỏ qua vì cờ đã đặt (đã migrate trước đó).
    /// </summary>
    public static bool EnsureMigrated(AccountRepository accounts, SettingsRepository settings)
    {
        if (settings.Get(SettingsRepository.ProxyKeyMigratedV1) == "1")
        {
            return false; // đã migrate → không chạy lại (idempotent)
        }

        // Gộp: pool hiện có TRƯỚC, rồi ProxyKey cố định của từng tài khoản (bỏ rỗng, trim).
        var merged = new List<string>(settings.GetKiotProxyKeys());
        foreach (var acc in accounts.GetAll())
        {
            var key = acc.ProxyKey?.Trim();
            if (!string.IsNullOrEmpty(key))
            {
                merged.Add(key);
            }
        }

        // Dedup + chuẩn hóa qua parser (đồng nhất với cách pool được đọc/ghi ở SettingsRepository).
        var deduped = KiotProxyKeyParser.Parse(KiotProxyKeyParser.Join(merged));

        // Chỉ ghi khi CÓ key để gộp (fresh DB không key → khỏi tạo settings row rỗng vô nghĩa).
        if (deduped.Count > 0)
        {
            settings.SetKiotProxyKeys(deduped);
        }
        settings.Set(SettingsRepository.ProxyKeyMigratedV1, "1");
        return true;
    }
}
