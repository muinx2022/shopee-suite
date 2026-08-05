# Shopee Suite

Bộ công cụ hợp nhất (WPF, .NET 8) gom 4 app cũ (check-shopee-account, open-multi-brave-v31,
update-product, shopee-stat) thành **một ứng dụng độc lập**. Suite **không** gọi/khởi chạy 4 app cũ —
toàn bộ logic đã được đưa vào `Shopee.Core` + `Shopee.Module.*`.

## Chạy khi đang phát triển (trong repo)
```
dotnet run --project suite/Shopee.Suite
```
hoặc `ShopeeSuite.cmd`. Build đã tự gói `ext/`, `shopee-stat-ext/`, `update-product-python/` vào output.

## Phát hành cho máy khác (Velopack + GitHub Releases)
**Máy dùng thật KHÔNG build và KHÔNG chép thư mục publish** — làm vậy là máy đó vĩnh viễn không nhận được
bản delta lẫn lệnh cập nhật từ Hub.

- **Ra bản mới (máy dev):** bump `version.txt` + ghi `CHANGELOG.md` → chạy `release-suite.cmd` ở gốc repo
  (`vpk download` → `dotnet publish` → `vpk pack` → `vpk upload github`).
- **Cài ở máy đích:** tải `ShopeeSuite-win-Setup.exe` từ
  [GitHub Releases](https://github.com/muinx2022/shopee-suite/releases) và chạy. App cài vào
  `%LocalAppData%\ShopeeSuite` (self-contained, **không cần cài .NET**).
- **Cập nhật về sau:** trong app, **Cài đặt → Phiên bản & cập nhật → "Cập nhật & khởi động lại"** (client tự tải delta).

`publish-suite.cmd` → `publish\ShopeeSuite\` **chỉ dùng để chạy thử cục bộ ở máy dev**, không phải đường
cài cho máy dùng thật. Cả hai đường đóng gói đều kèm sẵn:
- `extension/` — extension Brave cho **Scrape**.
- `shopee-stat-ext/` — extension Edge cho **Search**.
- `update-product-python/` — script Python cho workflow **Update product**.
- `appsettings.json` — cấu hình API Shopee cho Search.

## Yêu cầu ở máy đích
- **Brave** (Scrape, Update Product) và **Microsoft Edge** (Search, Check Account) đã cài.
- Đã đăng nhập sẵn tài khoản Shopee (hoặc để app tự đăng nhập qua mục Tài khoản & Proxy).
- **Chỉ** workflow *Update product* (Python) cần: cài **Python 3** + `pip install -r update-product-python/requirements.txt` (một lần). Các chức năng khác (Check / Scrape / Search / Import / Update tên SP) **không cần Python**.

## Dữ liệu người dùng
Lưu ở `%AppData%\ShopeeSuite\` (accounts.json, bigseller.json, profiles, output…). Search dùng thêm
`%AppData%\ShopeeStatApp\tasks.db`.
