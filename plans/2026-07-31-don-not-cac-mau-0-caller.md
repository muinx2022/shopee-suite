# Plan: Dọn nốt các mẩu 0-caller đã đánh dấu trong đợt refactor

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh

Đợt refactor 30-31/07 đã xong; các agent để lại vài mẩu code chết cần quyết định — nay đã chốt: DỌN HẾT.

## 2. Các việc

**A. Dây event `CookieSaved` (orders — nửa sống nửa chết):** `AccountSession.TrySaveCookie` 0 caller và là nơi DUY NHẤT phát `CookieSaved` ⇒ event không bao giờ bắn ⇒ toàn bộ dây là chết. Xoá trọn: `AccountSession.TrySaveCookie` + event `CookieSaved`; `IAccountSession.CookieSaved`; forwarder trong `AccountSessionManager`; phía `AccountsViewModel` (đăng ký + `OnSessionCookieSaved` + `RefreshAfterCookieSaved` — kiểm tra `RefreshAfterCookieSaved` có caller khác không, có thì chỉ gỡ phần event); 2 test double (`AccountSessionManagerTests`, `AccountRowViewModelTests`). Trước khi xoá từng mảnh: grep 0-caller. Mốc 0 warning phải giữ.

**B. `OrdersRepository.GetOrdersForSlipCheck`:** 0 caller production (chỉ test gọi) → xoá method + test tương ứng (ghi rõ số test giảm).

**C. `LauncherSettings.WsPort`** (suite — field serialize chết, đã có xmldoc đánh dấu): xoá property (property thừa trong settings.json cũ sẽ bị deserializer bỏ qua — vô hại; xác nhận store nạp settings dùng System.Text.Json mặc định bỏ qua unknown member).

**D. `SearchOrchestrator.StopAsync`** (suite Search — 3F đã xác nhận 0 caller): grep lại rồi xoá.

**E. Rà lần cuối:** grep các symbol đã bị báo cáo là chết trong các plan 2026-07-30/31 (mục "cần soi"/"code chết") mà chưa ai xoá — nếu còn sót cái nào 0-caller thì xoá nốt, liệt kê trong báo cáo. KHÔNG mở rộng sang suy đoán mới.

## 3. Phạm vi & nghiệm thu

- Khu: `orders/**` + `suite/**`. Không đụng `server/**`, `extensions/**`, `shared/**`.
- [x] Build 2 solution 0 lỗi 0 warning; test: orders/Core/hub không tụt ngoài số test xoá chủ đích (ghi rõ từng con số).
- [x] Grep các symbol đã xoá = 0 hit source.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Kết quả build/test:** `ShopeeSuite.sln` 0 Warning / 0 Error; `server/ShopeeHub.sln` 0 Warning / 0 Error.
Test: orders **1459** (= 1461 − 2 ca `GetOrdersForSlipCheck` xoá chủ đích), `Shopee.Core.Tests` **61**,
`Shopee.Hub.Web.Tests` **44** — hai bộ sau giữ nguyên. KHÔNG commit.

### A. Dây event `CookieSaved` — xoá trọn (7 file)

Grep trước khi xoá: `TrySaveCookie` 0 caller (chỉ chính nó); `RefreshAfterCookieSaved` chỉ 1 caller là
`OnSessionCookieSaved` ⇒ xoá cả hai (KHÔNG có caller khác nên không phải giữ lại phần nào).

| Symbol xoá | File |
|---|---|
| `TrySaveCookie` + `event CookieSaved` | `orders/XuLyDonShopee.App/Services/AccountSession.cs` |
| `IAccountSession.CookieSaved` | `orders/XuLyDonShopee.App/Services/IAccountSession.cs` |
| `AccountSessionManager.CookieSaved` + forwarder trong `GetOrCreate` | `orders/XuLyDonShopee.App/Services/AccountSessionManager.cs` |
| đăng ký `Sessions.CookieSaved += OnSessionCookieSaved` | `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs` |
| `OnSessionCookieSaved` + `RefreshAfterCookieSaved` | `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.Phien.cs` |
| `event CookieSaved` + `RaiseCookieSaved` (test double) | `orders/XuLyDonShopee.Tests/AccountSessionManagerTests.cs` |
| `event CookieSaved` + lời gọi trong `StartAsync` (test double) | `orders/XuLyDonShopee.Tests/AccountRowViewModelTests.cs` |

Kèm theo: xmldoc lớp `AccountSession` trỏ `<see cref="CookieSaved"/>` → đổi sang `<see cref="Changed"/>`;
comment trong `AccountsViewModelTests.cs:382` nhắc "luồng CookieSaved" → viết lại (test dùng
`SaveCapturedCookie`, không liên quan event). Mốc 0 warning giữ nguyên (không còn CS0067).

### B. `OrdersRepository.GetOrdersForSlipCheck` — xoá method + 2 test

- `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`: xoá method + xmldoc (−27 dòng).
- `orders/XuLyDonShopee.Tests/SlipRedownloadTests.cs`: xoá 2 `[Fact]`
  (`GetOrdersForSlipCheck_TraDungManStatusTracking`, `GetOrdersForSlipCheck_KhongDon_TraRong`) + helper `Order`
  chỉ phục vụ 2 ca đó; sửa xmldoc lớp; bỏ 2 `using` thành thừa (`Core.Data`, `Core.Models`).
  **Số test: 1461 → 1459 (−2).** Giữ nguyên 3 ca `SlipFileIsValidPdf`.

### C. `LauncherSettings.WsPort` — xoá property

`suite/Shopee.Module.Search/Engine/LauncherSettings.cs`. Đã xác nhận `AppSettingsService.Opts`
(`AppSettingsService.cs:5-10`) chỉ set `WriteIndented`/`PropertyNamingPolicy`/`DefaultIgnoreCondition` —
KHÔNG có `UnmappedMemberHandling = Disallow` ⇒ System.Text.Json mặc định **bỏ qua** member lạ, `wsPort` còn
trong settings.json cũ vô hại.

### D. `SearchOrchestrator.StopAsync` — xoá

Grep lại trước khi xoá: `SearchOrchestrator` chỉ được dựng ở `SearchSession.cs:132`, `StopAsync` trong
`suite/Shopee.Module.Search/**` = 0 hit sau khi xoá (0 caller trước đó). Field `_searchActive` vẫn còn 7 chỗ
đọc/ghi khác nên không thành chết theo.

### E. Rà lần cuối — KHÔNG còn gì để xoá

Grep toàn repo (trừ `plans/`) các symbol từng bị báo là chết trong plan 30-31/07: `TryClearVerifyFailedAfterLogin`,
`PrepareResult.SlipTabUrl`, `ThieuPhieu`, `SetWorkPage`/`WorkPage`/`_workPage`, `UserSelectors`/`PasswordSelectors`/
`SubmitSelectors`/`UsePasswordRegex`/`OtherWaysRegex`/`KmsiYesRegex`/`ShopDetailRegex`, `FindFirstVisibleAsync`,
`ScanShopListJs`, `SellerUrl`/`ShopListUrl`, `PasswordOption`, `ProxyRotator`/`KiotKeyPool`/`ProxyParser`,
`RemoveShopeeAccount`/`AppendShopeeAccounts` — **tất cả đã bị xoá ở các đợt trước, 0 hit source**. Hit còn lại
đều là symbol KHÁC tên gần giống, vẫn sống: `MsUserSelectors`/`SubUserSelectors`/… (orders `LoginSelectors`),
`KiotProxyRotator` (suite MultiBrave), `ShopeeLoginLineOptions.PasswordOptional`, `ReinjectWsPortAsync`
(cổng động, không liên quan `LauncherSettings.WsPort`). `CdpUnreachableTimeoutSeconds` không còn chết —
`RunnerSwLifecycle.cs:106` đã gia hạn deadline theo fix của plan `hop-nhat-cdp-targets-kill-brave`.
Ngoài phạm vi, KHÔNG đụng: 2 hit `ScanShopListJs` trong `extensions/shopee-orders/background.js` (chỉ là comment
"port từ … phía C#").

### Điểm cần phiên chính soi

1. **Hành vi mất đi (đúng chủ đích, nhưng ghi để soi):** dây `CookieSaved` tuy 0-phát nhưng
   `RefreshAfterCookieSaved` là chỗ DUY NHẤT làm mới cookie/`UpdatedAt` lên instance trong `_all` + form khi
   phiên nền ghi cookie. Đường ghi cookie hiện tại là `SaveCapturedCookie` (VM, tự `RefreshList`), nên không
   mất gì trên thực tế — nhưng nếu sau này cầu nối extension lại ghi cookie từ thread nền thì phải dựng lại
   cơ chế báo về UI.
2. `using XuLyDonShopee.Core.Data;` trong `AccountSession.cs` có vẻ đã thừa từ TRƯỚC đợt này (`CookieJson` nằm
   ở `Core.Services`) — để nguyên vì ngoài phạm vi và không gây warning.
