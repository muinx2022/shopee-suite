using System.IO;
using Shopee.Core.Accounts;
using Shopee.Core.Infrastructure;
using Shopee.Core.Proxy;
using Shopee.Modules.MultiBrave;
using Shopee.Modules.Search;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Dựng spec engine (Scrape/Search) từ 1 <see cref="ShopeeAccount"/> — gom phần LẶP giữa
/// <c>ScrapeViewModel.ToSpec</c> ⇄ <c>SearchViewModel.ToSpec</c>: bộ ba override proxy KiotProxy (lấy XOAY VÒNG
/// từ kho dùng chung, ghi đè proxy gắn sẵn của acc; kho rỗng → giữ proxy của acc) + thư mục profile.
/// </summary>
public static class ShopeeAccountSpecFactory
{
    /// <summary>Proxy XOAY VÒNG từ kho KiotProxy dùng chung (ghi đè proxy gắn sẵn của acc); kho rỗng → giữ
    /// proxy của acc (fallback). Bộ ba này giống hệt ở cả Scrape lẫn Search.</summary>
    private static (string Kiot, string Manual) ResolveProxy(ShopeeAccount a)
    {
        var pooled = KiotProxyPoolStore.Shared.ProxyForAccount(a.Id);
        return (pooled?.KiotKey ?? a.KiotProxyKey, pooled?.Manual ?? a.ManualProxy);
    }

    /// <summary>Spec cho engine Scrape (v31). Profile: dùng <see cref="ResolveScrapeProfileDir"/>.</summary>
    public static ScrapeAccountSpec ToScrapeSpec(ShopeeAccount a, string sheet)
    {
        var (kiot, manual) = ResolveProxy(a);
        return new(a.Id, a.DisplayName, a.ShopeeAccountLogin, a.OpenWithShopeeAccount,
            kiot, a.Region, a.ProxyType, manual, a.RequireProxy, sheet, 0, 0,
            ResolveScrapeProfileDir(a));
    }

    /// <summary>Spec cho engine Search (stat). Profile: LUÔN <see cref="SharedProfileDir"/> (riêng-máy).</summary>
    public static SearchAccountSpec ToSearchSpec(ShopeeAccount a)
    {
        var (kiot, manual) = ResolveProxy(a);
        return new(a.Id, a.DisplayName, a.ShopeeAccountLogin, a.OpenWithShopeeAccount,
            kiot, a.ProxyType, manual, SharedProfileDir(a), a.RequireProxy);
    }

    /// <summary>Thư mục profile RIÊNG-MÁY của tk: LUÔN dưới gốc profile CỤC BỘ theo Id. KHÔNG dùng
    /// <see cref="ShopeeAccount.ProfileRelativePath"/> thô — nó có thể là đường dẫn TUYỆT ĐỐI của MÁY KHÁC (acc
    /// đến từ Hub lưu path "C:\Users\&lt;user máy Hub&gt;\…") khiến client cố tạo profile dưới C:\Users\&lt;máy
    /// khác&gt;\ → "Access denied". Profile là riêng từng máy nên KHÔNG truyền xuyên máy. (= Search.LocalProfileDir cũ)</summary>
    private static string SharedProfileDir(ShopeeAccount a) =>
        Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", a.Id);

    /// <summary>Thư mục profile (Edge) đã đăng nhập Shopee của tk cho Scrape — engine import session từ đây sang
    /// Brave để khỏi login form. ProfileRelativePath TRỐNG hoặc TUYỆT ĐỐI → dùng gốc profile CỤC BỘ theo Id
    /// (KHÔNG tin path tuyệt đối lưu sẵn: có thể là path MÁY KHÁC → "Access denied"). Chỉ path TƯƠNG ĐỐI mới
    /// ghép với gốc cục bộ (giống mọi máy). (= Scrape.ResolveShopeeProfileDir cũ)</summary>
    private static string ResolveScrapeProfileDir(ShopeeAccount a)
    {
        var rel = a.ProfileRelativePath;
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel))
            return SharedProfileDir(a);
        return Path.Combine(SuitePaths.ModuleDir("shared"), rel.Replace('/', Path.DirectorySeparatorChar));
    }
}
