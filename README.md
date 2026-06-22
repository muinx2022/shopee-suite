# Shopee Suite

Bộ công cụ Shopee hợp nhất (WPF .NET 8) — 1 app, sidebar trái, các module: Tài khoản & Proxy, BigSeller,
Shopee Scrape, Shopee Search, Bigseller Update Product, Check Shopee Account, Cài đặt.

**C# thuần** (Microsoft.Playwright + CDP) — KHÔNG còn Python.

## Cấu trúc
```
ShopeeSuite.sln
suite/
  Shopee.Core/                 lõi dùng chung (CDP, browser, proxy, cookie, account store…)
  Shopee.Module.CheckAccount/  kiểm tra tài khoản Shopee (Edge)
  Shopee.Module.MultiBrave/    Scrape (Brave + extension)
  Shopee.Module.Search/        Search (Edge + extension)
  Shopee.Module.UpdateProduct/ Import/Update product (BigSeller, Playwright + Brave)
  Shopee.Suite/                shell WPF (View + ViewModel các module)
extensions/
  shopee-scrape/   extension Brave cho Scrape  (trước: ext/)
  shopee-search/   extension Edge cho Search   (trước: shopee-stat-ext/)
```
csproj của `Shopee.Suite` tự copy 2 extension vào output (`extensions/shopee-scrape`, `extensions/shopee-search`).
Dữ liệu chạy lưu ở `%AppData%\ShopeeSuite\` (account, AI config) + `%AppData%\ShopeeStatApp\tasks.db` (Search).

## Build / chạy / publish
```bat
dotnet build ShopeeSuite.sln -c Release
dotnet run --project suite/Shopee.Suite/Shopee.Suite.csproj
publish-suite.cmd        :: ra publish/ShopeeSuite/ShopeeSuite.exe (self-contained win-x64)
```
