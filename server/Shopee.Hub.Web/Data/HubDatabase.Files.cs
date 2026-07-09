using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: manifest + blob file dùng chung trên đĩa (liệt kê / đọc / stream / ghi / xoá).</summary>
public sealed partial class HubDatabase
{
    // ── Files (manifest + blob trên đĩa) ────────────────────────────────────────
    public List<FileManifestEntry> ListFiles()
    {
        lock (_gate)
        {
            var list = new List<FileManifestEntry>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT name,version,hash,size,mtime FROM files";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new FileManifestEntry { Name = S(rd, 0), Version = rd.GetInt32(1), Hash = S(rd, 2), Size = rd.GetInt64(3), Mtime = D(rd, 4) });
            return list;
        }
    }

    public byte[]? ReadFile(string name)
    {
        var path = SafeFullPath(FilesDir, name);
        if (path is null) return null;
        // Đọc blob KHÔNG cần _gate (không đụng SqliteConnection); file ghi nguyên tử qua tmp+Move nên đọc luôn an toàn.
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Mở stream ĐỌC 1 file blob để endpoint stream thẳng ra response (không nạp cả file vào RAM — file
    /// tối đa 256MB). FileShare.ReadWrite|Delete để PutFile (tmp+Move đè) không bị chặn bởi download đang chạy.
    /// null nếu tên xấu / file không tồn tại / mở lỗi.</summary>
    public Stream? OpenFileRead(string name)
    {
        var path = SafeFullPath(FilesDir, name);
        if (path is null) return null;
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Ghi file. ifMatch != null ⇒ kiểm tra version khớp (optimistic concurrency).</summary>
    public FilePutResponse PutFile(string name, byte[] data, int? ifMatch, string updatedBy)
    {
        var path = SafeFullPath(FilesDir, name);
        if (path is null) return new FilePutResponse(false, 0, "bad-name");

        // Ghi bytes ra tmp NGOÀI lock (việc nặng) → không chặn lease/heartbeat của cả fleet. Tmp duy nhất (Guid)
        // để 2 client ghi cùng tên không đạp lên nhau. Chỉ rename + cập nhật DB mới nằm trong lock.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var hash = Sha256(data);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);

        lock (_gate)
        {
            var current = ReadFileMetaLocked(name);
            if (ifMatch.HasValue && current is not null && current.Version != ifMatch.Value)
            {
                try { File.Delete(tmp); } catch { }
                return new FilePutResponse(false, current.Version, "version-conflict");
            }

            // Guard hoa-thường (Linux case-sensitive): đã có 1 file trùng tên khác hoa-thường (vd 'Workbooks/x'
            // vs 'workbooks/x') → từ chối để manifest + thư mục files\ không tách đôi/nhân bản trên ext4.
            if (current is null)
            {
                var variant = FindCaseVariantLocked(name);
                if (variant is not null)
                {
                    try { File.Delete(tmp); } catch { }
                    return new FilePutResponse(false, 0, "case-variant:" + variant);
                }
            }

            var newVer = (current?.Version ?? 0) + 1;
            File.Move(tmp, path, overwrite: true);   // rename nhanh, giữ trong lock để khớp với bản ghi DB

            var now = DateTimeOffset.UtcNow;
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO files(name,version,hash,size,mtime,updated_by,updated_at)
VALUES($n,$v,$h,$s,$mt,$ub,$ua)
ON CONFLICT(name) DO UPDATE SET version=$v, hash=$h, size=$s, mtime=$mt, updated_by=$ub, updated_at=$ua;";
            c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$v", newVer);
            c.Parameters.AddWithValue("$h", hash);
            c.Parameters.AddWithValue("$s", (long)data.Length);
            c.Parameters.AddWithValue("$mt", Iso(now));
            c.Parameters.AddWithValue("$ub", updatedBy);
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.ExecuteNonQuery();
            return new FilePutResponse(true, newVer, null);
        }
    }

    /// <summary>Tên file đã có trong manifest TRÙNG <paramref name="name"/> khi bỏ qua hoa-thường nhưng KHÁC
    /// chính tả (chỉ khác case). null nếu không có. Dùng để chặn nhân bản file trên Linux (case-sensitive).</summary>
    private string? FindCaseVariantLocked(string name)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT name FROM files WHERE name=$n COLLATE NOCASE AND name<>$n LIMIT 1";
        c.Parameters.AddWithValue("$n", name);
        return c.ExecuteScalar() as string;
    }

    /// <summary>Xoá 1 file dùng chung: bỏ bản ghi manifest + blob trên đĩa. Trả false nếu tên xấu.
    /// (Web-hub: trang Files cho admin xoá workbook cũ; UI CHẶN xoá config/* để khỏi tự bắn chân.)</summary>
    public bool DeleteFile(string name)
    {
        var path = SafeFullPath(FilesDir, name);
        if (path is null) return false;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM files WHERE name=$n";
            c.Parameters.AddWithValue("$n", name);
            c.ExecuteNonQuery();
        }
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        return true;
    }

    private FileManifestEntry? ReadFileMetaLocked(string name)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT name,version,hash,size,mtime FROM files WHERE name=$n";
        c.Parameters.AddWithValue("$n", name);
        using var rd = c.ExecuteReader();
        return rd.Read()
            ? new FileManifestEntry { Name = S(rd, 0), Version = rd.GetInt32(1), Hash = S(rd, 2), Size = rd.GetInt64(3), Mtime = D(rd, 4) }
            : null;
    }
}
