namespace Shopee.Hub.Web.Services;

/// <summary>Gộp pattern "lưu config + xử lý version-conflict" của các trang Config (trước chép tay 5 trang).</summary>
public static class ConfigSave
{
    /// <summary>Lưu 1 file config với optimistic concurrency. Ok → cập nhật <paramref name="version"/> + message ✔
    /// (kèm <paramref name="okDetail"/>, vd " 12 acc"). version-conflict → gọi <paramref name="reload"/> để trang
    /// đọc lại bản mới + message ⚠ (người dùng sửa lại rồi lưu). Lỗi khác → ✘.</summary>
    public static string Apply(FileStoreConfigService cfg, string file, object value, ref int version, Action reload, string okDetail = "")
    {
        var res = cfg.Save(file, value, version);
        if (res.Ok) { version = res.Version; return $"✔ Đã lưu{okDetail} (v{res.Version})."; }
        if (res.Conflict == "version-conflict") { reload(); return "⚠ Client vừa đẩy bản mới — đã tải lại, hãy sửa lại rồi lưu."; }
        return $"✘ Lỗi lưu: {res.Conflict}";
    }
}
