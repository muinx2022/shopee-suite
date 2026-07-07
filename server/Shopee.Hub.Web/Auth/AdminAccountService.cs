using System.Security.Cryptography;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Auth;

/// <summary>
/// Tài khoản admin DUY NHẤT để đăng nhập web UI. Mật khẩu băm PBKDF2-SHA256 (210k vòng, salt 16B ngẫu nhiên),
/// lưu trong bảng <c>settings</c> của hub.db (không file riêng, không plaintext). Tạo lần đầu qua biến môi
/// trường HUB_ADMIN_USER/PASSWORD lúc khởi động HOẶC trang /setup (chỉ mở khi CHƯA có admin).
/// </summary>
public sealed class AdminAccountService
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly HubDatabase _db;
    public AdminAccountService(HubDatabase db) => _db = db;

    /// <summary>Đã có admin chưa (quyết định hiện /setup hay /login).</summary>
    public bool HasAdmin => !string.IsNullOrEmpty(_db.GetSetting(SettingKeys.AdminUser))
                            && !string.IsNullOrEmpty(_db.GetSetting(SettingKeys.AdminHash));

    public string? Username => _db.GetSetting(SettingKeys.AdminUser);

    /// <summary>Đặt (tạo/đổi) admin. Sinh salt mới + băm PBKDF2. Ghi username/hash/salt/iter vào settings.</summary>
    public void SetAdmin(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("username/password trống");
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        _db.SetSetting(SettingKeys.AdminUser, username.Trim());
        _db.SetSetting(SettingKeys.AdminHash, Convert.ToBase64String(hash));
        _db.SetSetting(SettingKeys.AdminSalt, Convert.ToBase64String(salt));
        _db.SetSetting(SettingKeys.AdminIter, Iterations.ToString());
    }

    /// <summary>Kiểm tra user+pass. So sánh hash bằng FixedTimeEquals (chống timing attack).</summary>
    public bool Verify(string username, string password)
    {
        var storedUser = _db.GetSetting(SettingKeys.AdminUser);
        var storedHash = _db.GetSetting(SettingKeys.AdminHash);
        var storedSalt = _db.GetSetting(SettingKeys.AdminSalt);
        if (string.IsNullOrEmpty(storedUser) || string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSalt))
            return false;
        if (!string.Equals(username?.Trim(), storedUser, StringComparison.Ordinal)) return false;

        var iter = int.TryParse(_db.GetSetting(SettingKeys.AdminIter), out var it) ? it : Iterations;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(storedSalt); expected = Convert.FromBase64String(storedHash); }
        catch { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Seed admin từ env HUB_ADMIN_USER/PASSWORD nếu chưa có admin nào (idempotent).</summary>
    public void SeedFromEnvIfEmpty()
    {
        if (HasAdmin) return;
        var u = Environment.GetEnvironmentVariable("HUB_ADMIN_USER");
        var p = Environment.GetEnvironmentVariable("HUB_ADMIN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(u) && !string.IsNullOrEmpty(p)) SetAdmin(u, p);
    }
}
