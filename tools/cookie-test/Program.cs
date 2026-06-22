using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Validate: giải mã cookie shopee.vn từ 1 profile Edge đã login (cùng máy → DPAPI giải được).
Console.OutputEncoding = Encoding.UTF8;

var userDataDir = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ShopeeSuite", "shared", "profiles", "0c3defb6d89349a5988c933a4205a729");

Console.WriteLine($"Profile: {userDataDir}");

// 1) Lấy AES key từ Local State (DPAPI).
var localStatePath = Path.Combine(userDataDir, "Local State");
var ls = JsonDocument.Parse(File.ReadAllText(localStatePath));
var encKeyB64 = ls.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!;
var encKey = Convert.FromBase64String(encKeyB64);
var dpapiBlob = encKey[5..]; // bỏ prefix "DPAPI"
var aesKey = ProtectedData.Unprotect(dpapiBlob, null, DataProtectionScope.CurrentUser);
Console.WriteLine($"AES key: {aesKey.Length} bytes (mong đợi 32).");

// 2) Đọc Cookies DB (copy ra temp để tránh lock).
var dbPath = Path.Combine(userDataDir, "Default", "Network", "Cookies");
var tmp = Path.Combine(Path.GetTempPath(), "ck_" + Guid.NewGuid().ToString("N") + ".db");
File.Copy(dbPath, tmp, true);

var found = new List<(string host, string name, string val)>();
try
{
    using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Cache=Private");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT host_key, name, encrypted_value FROM cookies WHERE host_key LIKE '%shopee%'";
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
    {
        var host = rdr.GetString(0);
        var name = rdr.GetString(1);
        var enc = (byte[])rdr["encrypted_value"];
        var val = DecryptValue(enc, aesKey);
        found.Add((host, name, val));
    }
}
finally { try { File.Delete(tmp); } catch { } }

Console.WriteLine($"\nTổng cookie shopee: {found.Count}");
foreach (var (host, name, val) in found.OrderBy(c => c.name))
{
    var ok = val.Length > 0;
    // KHÔNG in giá trị (nhạy cảm) — chỉ in tên + độ dài + giải mã OK?
    Console.WriteLine($"  {host,-22} {name,-12} len={val.Length,-5} {(ok ? "OK" : "FAIL")}");
}
var key = new[] { "SPC_ST", "SPC_EC", "SPC_F", "SPC_SI", "SPC_U" };
Console.WriteLine("\n-- Cookie session quan trọng --");
foreach (var k in key)
{
    var c = found.FirstOrDefault(x => string.Equals(x.name, k, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"  {k,-8}: {(c.name is null ? "(không có)" : $"có, giải mã len={c.val.Length}")}");
}

static string DecryptValue(byte[] enc, byte[] key)
{
    if (enc.Length < 3) return "";
    var prefix = Encoding.ASCII.GetString(enc, 0, 3);
    if (prefix is "v10" or "v11")
    {
        var nonce = enc[3..15];
        var tag = enc[^16..];
        var cipher = enc[15..^16];
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
    try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser)); }
    catch { return ""; }
}
