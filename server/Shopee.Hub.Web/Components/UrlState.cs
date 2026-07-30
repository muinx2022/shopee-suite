using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Shopee.Hub.Web.Components;

/// <summary>Phần MÁY MÓC của khuôn "mọi view-state phải vào URL" — đọc query, so trước/sau, dựng lại chuỗi query,
/// NavigateTo(replace) — trước bị chép tay ở Fleet/Dispatch/AllData. Phần "trang này muốn gì vào URL" (tên key,
/// giá trị nào coi là mặc định) vẫn nằm ở trang: helper chỉ nhận/trả cặp (key, chuỗi).</summary>
public static class UrlState
{
    /// <summary>Query hiện tại của trang. Dùng khi việc khôi phục phải nhìn NHIỀU key một lượt (vd Fleet: có cả
    /// acc lẫn shop mới chọn shop, thiếu shop thì chỉ chọn acc).</summary>
    public static UrlQuery Read(NavigationManager nav) =>
        new(QueryHelpers.ParseQuery(new Uri(nav.Uri).Query));

    /// <summary>Khôi phục kiểu từng-key độc lập: gọi <paramref name="fields"/> theo thứ tự với giá trị đọc được
    /// (rỗng nếu URL không có key đó) — trang tự lo validate/kẹp giá trị trong callback.</summary>
    public static void Restore(NavigationManager nav, params (string Key, Action<string> Apply)[] fields)
    {
        var q = Read(nav);
        foreach (var (key, apply) in fields) apply(q[key]);
    }

    /// <summary>Ghi view-state vào query. Giá trị null/rỗng → BỎ key khỏi URL (trang tự quy giá trị mặc định về
    /// rỗng trước khi truyền vào). CHỈ navigate khi query thật sự đổi, kẻo vòng render.</summary>
    public static void Update(NavigationManager nav, bool replace, params (string Key, string? Value)[] fields)
    {
        var cur = Read(nav);
        if (fields.All(f => cur[f.Key] == (f.Value ?? ""))) return;   // query đã khớp → khỏi navigate

        var query = string.Join("&", fields
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .Select(f => f.Key + "=" + Uri.EscapeDataString(f.Value!)));
        var path = new Uri(nav.Uri).AbsolutePath;
        nav.NavigateTo(query.Length == 0 ? path : path + "?" + query, forceLoad: false, replace: replace);
    }
}

/// <summary>Bọc mỏng quanh query đã parse: key không có → chuỗi rỗng (mọi chỗ dùng đều muốn thế).</summary>
public readonly struct UrlQuery
{
    private readonly Dictionary<string, StringValues>? _query;

    public UrlQuery(Dictionary<string, StringValues> query) => _query = query;

    public string this[string key] =>
        _query is not null && _query.TryGetValue(key, out var v) ? v.ToString() : "";

    /// <summary>Số nguyên trong query; không parse được hoặc nhỏ hơn <paramref name="min"/> → <paramref name="fallback"/>.</summary>
    public int Int(string key, int fallback, int min = int.MinValue) =>
        int.TryParse(this[key], out var n) && n >= min ? n : fallback;

    /// <summary>Cờ bật/tắt — quy ước "1" là bật (mọi thứ khác, kể cả thiếu key, là tắt).</summary>
    public bool Flag(string key) => this[key] == "1";
}
