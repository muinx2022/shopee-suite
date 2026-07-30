# Module Đơn hàng (`orders/`)

Theo dõi + xử đơn Shopee Seller Centre: đăng nhập Nền tảng tài khoản phụ (`subaccount.shopee.com`) → SSO sang
Seller Centre → lặp qua từng shop (đọc đơn, đặt địa chỉ lấy hàng, Chuẩn bị hàng, in phiếu, check đơn trả hàng)
→ lưu SQLite + đẩy Google Sheet/Hub.

## Ba project

| Project | Vai trò |
|---|---|
| `XuLyDonShopee.Core` | Thư viện lõi: model, `Data/` (SQLite qua `Microsoft.Data.Sqlite`), `Services/` (luồng đăng nhập Playwright, cầu nối extension, parser, GSheet). Ref `shared/Shopee.Proxy.Kiot` + `shared/Shopee.Toolkit` — **KHÔNG** ref `suite/Shopee.Core`. |
| `XuLyDonShopee.App` | **Avalonia 11.3** (KHÔNG phải WPF) + `CommunityToolkit.Mvvm`. Build ra **DLL**, không phải exe — shell `Shopee.Suite` nạp làm module. `ViewModels/`, `Views/`, `Services/` (vòng đời phiên, đẩy hub/GSheet). |
| `XuLyDonShopee.Tests` | xUnit. `Using Include="Xunit"` sẵn nên file test không cần `using Xunit;`. |

`net8.0`, `Nullable=enable` cả ba. Hai project nguồn đều mở `InternalsVisibleTo` cho Tests → **test thẳng được
hàm `internal`**, không phải nới `public` chỉ để test.

## Build / test

```
dotnet build orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj
dotnet test  orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj
```

Sửa cả `shared/` hoặc `suite/` thì build cả solution: `dotnet build ShopeeSuite.sln`. Giữ **0 warning** — đó là
mốc nghiệm thu, không phải mong muốn.

## Quy ước code

- **Tên tiếng Việt KHÔNG DẤU cho luật nghiệp vụ.** Hàm/hằng mang một quyết định nghiệp vụ thì đặt đúng tên
  nghiệp vụ đó: `NenXoaDonKetThuc`, `QuyetDinhLuotTraHang`, `QuyetDinhSauDatDiaChi`, `LuuMaTraHang`,
  `TranDonMoiLuotShop`, `ChuKyThuong`. Hạ tầng thuần kỹ thuật (`TryReadSlipBase64`, `ParseOrdersJson`) giữ
  tiếng Anh. Comment/xmldoc viết tiếng Việt CÓ dấu.
- **Hàm thuần tách khỏi hàm đụng trình duyệt** để test được không cần Playwright — xem `LoginParsers`,
  `UocTinhDon`, `ShopFlowRunner.QuyetDinh*`. Thêm luật mới thì tách hàm thuần rồi test ma trận ca.
- **Best-effort thì nuốt lỗi, nhưng phải để lại dấu vết.** `catch (Exception ex)` catch-all chỉ-ghi-log dùng
  `ex.ToString()` (đủ stack để lần ra); nhánh lỗi ĐÃ phân loại (vd `catch (InvalidOperationException)` =
  extension chưa kết nối) và mọi chuỗi hiện ra UI/`StatusText` giữ `ex.Message` cho gọn.
- **Số trần/hạn giờ phải có tên.** Hạn từng chặng cầu nối nằm ở `OrdersBridgeChannel.ChoChang`; đừng rắc
  `TimeSpan.FromSeconds(n)` trần vào chỗ gọi.

## Cầu nối extension

App không tự lái DOM Seller Centre (Playwright bị anti-bot chặn) mà nói chuyện với extension
`extensions/shopee-orders` qua WebSocket loopback cổng cố định `OrdersBridgeChannel.BridgePort` = **47821**
(khớp `DEFAULT_PORT` trong `background.js` — đổi một bên là gãy). Mỗi bước là một "chặng": `Arm…()` tạo TCS mới
NGAY TRƯỚC `SendAsync`, rồi `AwaitAsync` chính TCS đó. Một phiên/lần chạy (chưa đa-lane).
