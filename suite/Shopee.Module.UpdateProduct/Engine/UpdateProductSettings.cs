using Shopee.Core.Browser;

namespace UpdateProduct;

internal static class UpdateProductSettings
{
    public static void Normalize(UpdateProductSettingsFile settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BraveExe))
            settings.BraveExe = DetectBraveExe()?.FullName ?? "";
        if (string.IsNullOrWhiteSpace(settings.SourceUserData))
            settings.SourceUserData = DetectBraveUserData()?.FullName ?? "";

        settings.Accounts ??= [];
        if (settings.Accounts.Count == 0)
            settings.Accounts.Add(BigSellerAccountConfig.CreateDefault());

        foreach (var account in settings.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id))
                account.Id = Guid.NewGuid().ToString("N");
            account.Shops ??= [];
            if (account.Shops.Count == 0)
                account.Shops.Add(ShopConfig.CreateDefault());
            if (string.IsNullOrWhiteSpace(account.BigSellerCookieFile))
                account.BigSellerCookieFile = ResolveAccountCookieFile(account);

            foreach (var shop in account.Shops)
            {
                if (string.IsNullOrWhiteSpace(shop.Id))
                    shop.Id = Guid.NewGuid().ToString("N");
                shop.UseSharedProfiles = true;
                shop.BigSellerStartRow = Math.Max(2, shop.BigSellerStartRow);
                shop.BigSellerEndRow = Math.Max(0, shop.BigSellerEndRow);
                shop.BigSellerImportMaxProcess = Math.Clamp(shop.BigSellerImportMaxProcess, 1, 10);
                shop.BigSellerUpdateMaxProcess = Math.Clamp(shop.BigSellerUpdateMaxProcess, 1, 10);
                shop.BigSellerListingReloadSeconds = Math.Clamp(shop.BigSellerListingReloadSeconds, 3, 600);
                shop.OpenAiBatchSize = Math.Clamp(shop.OpenAiBatchSize, 1, 500);
            }
        }

        var activeAccount = settings.Accounts.FirstOrDefault(a => a.Id == settings.ActiveAccountId) ?? settings.Accounts[0];
        settings.ActiveAccountId = activeAccount.Id;
        var activeShop = activeAccount.Shops.FirstOrDefault(s => s.Id == settings.ActiveShopId) ?? activeAccount.Shops[0];
        settings.ActiveShopId = activeShop.Id;
    }

    public static string ResolveAccountCookieFile(BigSellerAccountConfig account) =>
        AppSession.ResolvePersistentDataPath("account-cookies", $"{account.Id}-bigseller.json");

    // Định vị Brave (đường dẫn cố định + registry App Paths) đã gộp về bộ định vị chung của Core, chạy sau
    // facade nền tảng (Windows dùng registry, Linux GĐ3 dùng which/usr/bin…).
    private static FileInfo? DetectBraveExe() =>
        BrowserLauncher.Detect(BrowserKind.Brave) is { } exe ? new FileInfo(exe) : null;

    private static DirectoryInfo? DetectBraveUserData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return null;

        var path = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
        return Directory.Exists(path) ? new DirectoryInfo(path) : null;
    }
}
