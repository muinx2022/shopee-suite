namespace Shopee.Core.Infrastructure;

/// <summary>
/// Đọc/ghi file JSON kiểu "kho cấu hình" — gom đúng MỘT khuôn vốn bị chép ở 13 store của suite
/// (accounts.json, bigseller.json, machine.json, op-progress.json…):
/// <list type="bullet">
///   <item>ĐỌC: thiếu file / đọc lỗi / JSON hỏng → trả <c>null</c>, KHÔNG ném — store tự quyết định
///   giá trị thay thế (danh sách rỗng, config mặc định…).</item>
///   <item>GHI: tạo thư mục cha → ghi file tạm tên DUY NHẤT (pid+guid) → <c>File.Move(overwrite)</c> đè,
///   có retry khi file bị khoá (nguyên tử ở mức file, bền với kill giữa chừng và với 2 tiến trình cùng ghi)
///   → trả false nếu lỗi, KHÔNG ném.</item>
/// </list>
/// UTF-8 CÓ BOM, đúng như <c>File.WriteAllText(path, json, Encoding.UTF8)</c> mà cả 13 store đang dùng —
/// đổi sang không-BOM là đổi byte của mọi file cấu hình production, KHÔNG được đổi.
/// Helper KHÔNG tự khoá: mỗi store giữ lock riêng và gọi vào đây y như trước (trong hoặc ngoài lock).
/// </summary>
public static class JsonAtomicFile
{
    /// <summary>
    /// Đọc <paramref name="path"/> rồi deserialize thành <typeparamref name="T"/>. Thiếu file, đọc lỗi,
    /// JSON hỏng hoặc JSON là <c>null</c> → trả <c>null</c> (KHÔNG ném). Store nào cần phân biệt "chưa có
    /// file" với "file hỏng" (thiếu file thì GIỮ giá trị đang có) thì tự kiểm <see cref="File.Exists"/> trước.
    /// </summary>
    public static T? TryLoad<T>(string path, JsonSerializerOptions? options = null, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), options);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Không đọc được {Path.GetFileName(path)}: {ex.Message}");
            return default;
        }
    }

    /// <summary>Serialize <paramref name="value"/> rồi ghi nguyên tử (xem <see cref="SaveText"/>).
    /// false = lỗi serialize/ghi (đã nuốt, không ném).</summary>
    public static bool Save<T>(string path, T value, JsonSerializerOptions? options = null, Action<string>? log = null)
    {
        try
        {
            return SaveText(path, JsonSerializer.Serialize(value, options), log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Không ghi được {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Ghi sẵn chuỗi JSON ra file theo cùng cơ chế nguyên tử. Dùng khi store phải serialize BÊN TRONG lock
    /// của nó rồi mới ghi file NGOÀI lock (AiConfigStore, HubClientConfigStore, HubServerConfigStore) —
    /// giữ đúng thứ tự vốn có thay vì kéo cả lượt ghi đĩa vào trong lock.
    /// Tên file tạm DUY NHẤT theo pid+guid + retry <c>File.Move</c> — cùng bài với
    /// <c>BigSellerCookieEngine.WriteAtomic</c>. Trước đây mọi store dùng chung tên cố định
    /// <c>&lt;file&gt;.tmp</c>: app chạy song song nhiều tiến trình (shortcut <c>--mode</c>) thì 2 tiến trình
    /// giẫm lên cùng một file tạm ⇒ bên này Move xong bên kia mất file ⇒ Save trả false ⇒ caller
    /// (vd <c>AccountStore.Add</c>) HOÀN TÁC dù dữ liệu đã ghi được. Tmp mồ côi bị xoá khi ghi hỏng.
    /// </summary>
    public static bool SaveText(string path, string json, Action<string>? log = null)
    {
        var tmp = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(tmp, json, Encoding.UTF8);

            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); return true; }
                // Số lần/nhịp retry lấy y theo BigSellerCookieEngine.WriteAtomic, NHƯNG bắt rộng hơn: khi 2
                // tiến trình thay cùng một đích, Windows trả ACCESS_DENIED (⇒ UnauthorizedAccessException)
                // chứ KHÔNG phải IOException — chỉ bắt IOException là retry chết đúng lúc cần nhất
                // (test Save_HaiLuongCungGhiMotFile_* đỏ ngay). WriteAtomic đang dính đúng lỗ này.
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)
                {
                    Thread.Sleep(150);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            log?.Invoke($"Không ghi được {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }
}
