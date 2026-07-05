using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shopee.Core.Platform;

namespace OpenMultiBraveLauncherV3;

/// <summary>1 cookie đã giải mã, đủ field để inject qua CDP Storage.setCookies.</summary>
internal sealed record BrowserCookie(
    string Name, string Value, string Domain, string Path,
    bool Secure, bool HttpOnly, string? SameSite, long? ExpiresUnix);

/// <summary>
/// Đọc + giải mã cookie của 1 host từ profile Chromium (Edge/Brave/Chrome) ĐÃ ĐĂNG NHẬP trên CÙNG MÁY.
/// Dùng để import session Shopee (do tab "Kiểm tra tài khoản" login bằng Edge) sang profile Brave của
/// Scrape → engine thấy đã đăng nhập (SPC_ST/SPC_EC) → KHỎI điền form → tránh captcha do login lại.
/// Cookie value mã hoá bằng AES-GCM với key bọc DPAPI (chỉ giải được trên đúng máy + user đã tạo).
/// </summary>
internal static class ChromiumCookieReader
{
    public static List<BrowserCookie> ReadCookies(string userDataDir, string hostLike)
    {
        var result = new List<BrowserCookie>();
        try
        {
            var localStatePath = System.IO.Path.Combine(userDataDir, "Local State");
            var dbPath = System.IO.Path.Combine(userDataDir, "Default", "Network", "Cookies");
            if (!File.Exists(localStatePath) || !File.Exists(dbPath))
                return result;

            var aesKey = GetAesKey(localStatePath);
            if (aesKey is null)
                return result;

            // Copy DB ra temp (tránh lock khi Edge/Brave đang mở) rồi đọc read-only.
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssck_" + Guid.NewGuid().ToString("N") + ".db");
            File.Copy(dbPath, tmp, true);
            try
            {
                // Pooling=False: KHÔNG giữ connection trong pool sau Dispose → handle file nhả ngay để File.Delete
                // (finally) xoá được file tạm. Bật pooling (mặc định) + mỗi file 1 GUID = pool giữ handle tới khi
                // thoát app → rò hàng nghìn ssck_*.db trong %TEMP%.
                using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Cache=Private;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT host_key, name, encrypted_value, path, is_secure, is_httponly, expires_utc, samesite " +
                    "FROM cookies WHERE host_key LIKE $h";
                cmd.Parameters.AddWithValue("$h", "%" + hostLike + "%");
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var host = rdr.GetString(0);
                    var name = rdr.GetString(1);
                    var enc = (byte[])rdr["encrypted_value"];
                    var val = DecryptValue(enc, aesKey);
                    if (string.IsNullOrEmpty(val)) continue;
                    var path = rdr.IsDBNull(3) ? "/" : rdr.GetString(3);
                    var secure = rdr.GetInt64(4) != 0;
                    var httpOnly = rdr.GetInt64(5) != 0;
                    var expUtc = rdr.GetInt64(6);
                    var ss = rdr.IsDBNull(7) ? -1 : rdr.GetInt64(7);
                    result.Add(new BrowserCookie(name, val, host, string.IsNullOrEmpty(path) ? "/" : path,
                        secure, httpOnly, MapSameSite(ss), ToUnix(expUtc)));
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch { result.Clear(); }
        return result;
    }

    private static byte[]? GetAesKey(string localStatePath)
    {
        try
        {
            using var ls = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!ls.RootElement.TryGetProperty("os_crypt", out var oc) ||
                !oc.TryGetProperty("encrypted_key", out var ek))
                return null;
            var b64 = ek.GetString();
            if (string.IsNullOrEmpty(b64)) return null;
            var enc = Convert.FromBase64String(b64);
            if (enc.Length < 6) return null;          // bỏ prefix "DPAPI" (5 byte)
            return PlatformServices.OsCrypt.UnprotectCurrentUser(enc[5..]);
        }
        catch { return null; }
    }

    private static string DecryptValue(byte[] enc, byte[] key)
    {
        try
        {
            if (enc.Length < 3) return "";
            var prefix = Encoding.ASCII.GetString(enc, 0, 3);
            if (prefix is "v10" or "v11")
            {
                // [v1x][12-byte nonce][ciphertext][16-byte tag]
                if (enc.Length < 3 + 12 + 16) return "";
                var nonce = enc[3..15];
                var tag = enc[^16..];
                var cipher = enc[15..^16];
                var plain = new byte[cipher.Length];
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            // Cookie cũ (trước v10) mã hoá DPAPI trực tiếp.
            var plainLegacy = PlatformServices.OsCrypt.UnprotectCurrentUser(enc);
            return plainLegacy is null ? "" : Encoding.UTF8.GetString(plainLegacy);
        }
        catch { return ""; }
    }

    private static string? MapSameSite(long s) => s switch { 0 => "None", 1 => "Lax", 2 => "Strict", _ => null };

    /// <summary>expires_utc của Chromium = micro-giây từ 1601-01-01 UTC → Unix giây. ≤0 = cookie phiên.</summary>
    private static long? ToUnix(long chromeEpochMicros)
    {
        if (chromeEpochMicros <= 0) return null;
        var unix = chromeEpochMicros / 1_000_000 - 11644473600;
        return unix > 0 ? unix : null;
    }
}
