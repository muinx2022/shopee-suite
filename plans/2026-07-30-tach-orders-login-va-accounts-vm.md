# Plan: Tách ShopeeLoginService (orders) + AccountsViewModel (đợt 4 — orders B)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (chờ phiên chính nghiệm thu)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

- `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs` (~2.400 dòng) — bản Playwright còn dùng cho bước login subaccount + verify email; sau 3F đã đọc selector Microsoft từ `Shopee.Toolkit.MsLogin.MsLoginSelectors`.
- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs` (~1.950 dòng) — VM chính màn tài khoản: danh sách account, chạy/dừng phiên, thống kê chuẩn-bị-hàng (local + hub), kéo danh bạ từ Hub…

Mục tiêu (refactor thuần, KHÔNG đổi hành vi):
1. `ShopeeLoginService` tách theo trục có sẵn trong plan 25/07: **`BrowserBootstrap`** (mở browser Playwright + proxy-auth + profile), **`MicrosoftMailLogin`** (LoginHotmailAsync + OpenMailboxSignedInAsync + đọc mã verify — dùng MsLoginSelectors), **`SubaccountLoginFlow`** (TryLoginSubaccountAsync + TryVerifyByEmailAsync + human-input), **`LoginParsers`** (parse thuần chuỗi/DOM → TEST ĐƯỢC). `ShopeeLoginService`/`LoginSession` giữ làm facade. Cấu trúc được phép chỉnh theo thực tế, ghi rõ.
2. `AccountsViewModel` tách: khối thống kê (local + hub prepare-stats — phần vừa sửa B2 giữ nguyên luật cộng dồn) thành class/VM con; khối kéo-danh-bạ-từ-Hub thành service/handler riêng; row-VM nếu đang lồng trong file thì ra file riêng. Mục tiêu VM chính ≤ ~800 dòng.

## 2. Phạm vi

- **Làm:** 2 việc trên; chỉ đụng `orders/**`. LƯU Ý: agent khác đang làm song song trên `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs` + `orders/XuLyDonShopee.App/Services/AccountSession.cs` + `SlipFiles` — TUYỆT ĐỐI không sửa 2 file đó (đọc thì được); file mới đặt tên không trùng (`OrderPersistPipeline`, `SlipFiles`, `OrdersBridgeLauncher`, `ShopFlowRunner` là tên bên kia dùng).
- **Không làm:** không đổi hành vi; selector/delay/human-input giữ từng giá trị (anti-bot); KHÔNG commit.

## 3. Các bước thực hiện

1. Đọc 2 file + test liên quan (`ShopeeShippingNavTests`, `SlipRedownloadTests`, `OrderStatisticsViewModelTests`, `PrepareHubCountTests`…).
2. Tách từng khối một, build + test sau mỗi khối.
3. `LoginParsers` viết test cho các parser thuần tách ra được (tối thiểu 5 ca).
4. Build + test toàn bộ.

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 lỗi 0 warning; test orders ≥ 1440 + test mới.
- [ ] `ShopeeLoginService.cs` ≤ ~800 dòng (facade); `AccountsViewModel.cs` ≤ ~800; không file mới > ~800.
- [ ] Bảng "khối nào → file nào" trong báo cáo; chuỗi log + delay không đổi.

## 5. Rủi ro & lưu ý

- Code login là đường sống của module Đơn hàng — di chuyển nguyên khối, không "tiện tay" gộp/sửa.
- XAML binding tới AccountsViewModel: đổi chỗ property là gãy UI — giữ nguyên tên/property công khai trên VM chính (delegate xuống class con nếu cần).
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Kết quả build/test:** `dotnet build ShopeeSuite.sln` → 0 lỗi / 0 warning. `dotnet test orders/XuLyDonShopee.Tests`
→ **1452 pass / 0 fail** (baseline 1440 + 12 ca mới). `dotnet test suite/Shopee.Core.Tests` → 43 pass (sanity, không đụng).
KHÔNG commit.

### 1. `ShopeeLoginService.cs` 2413 → 342 dòng (facade)

Tách thành 10 lớp `internal static` cùng namespace `XuLyDonShopee.Core.Services` (đặt thẳng trong `Services/`,
tiền tố `Login*` — KHÔNG tạo thư mục con để không lệch quy ước folder = namespace của project).

| Khối | File mới | Nội dung |
|---|---|---|
| Selector + regex | `LoginSelectors.cs` (126) | toàn bộ mảng selector Shopee/subaccount/Microsoft + 15 Regex (kể cả alias `MsLoginSelectors`) |
| Hàm thuần | `LoginParsers.cs` (228) | `NormalizeForMatch`, `IsSecurityWarningMailRow`, `MatchesConfirmLink/Expired/MyAccountNav/SellerChannelEntry`, `ScanShopListJs`, `ParseShopListJson`, `ParseOrdersJson` |
| Dò DOM | `LoginPageProbe.cs` (293) | `IsAnyVisibleByClientRects`, `IsElementVisibleByClientRects`, `IsSelectorVisible`, `ReadAlertText`, `FindFirstVisible(ByRects)`, `FindVisibleByText(InFrames)`, `FindByNormalizedTextInFrames`, `IsPointOnElement`, `HasBoundingBox` |
| Human-input | `LoginHumanInput.cs` (195) | `HumanFill`, `HumanMoveTo`, `HumanMoveAndClick(Verified)`, `TryHumanClickVisible` |
| Mở trình duyệt | `LoginBrowserBootstrap.cs` (246) | `EnsureBrowserInstalled`, `DescribeBrowser`, `PathEquals`, `LaunchAndConnectAsync`, `WaitForDevToolsPort`, `WaitForCdpEndpoint`, `EnsureChromiumInstalledForFallback` |
| Trạng thái phiên | `ShopeeSessionState.cs` (88) | `CaptureCookiesJsonAsync`, `DetectPageStateAsync` (nhận `IBrowserContext`) |
| Đăng nhập mail MS | `MicrosoftMailLogin.cs` (207) | `OpenMailboxSignedInAsync`, `LoginHotmailAsync` |
| Quét + bấm link mail | `ShopeeMailConfirm.cs` (416) | `OpenShopeeMailAndConfirmAsync`, `ClickConfirmLinkInMailAsync`, `TryResendVerifyEmailAsync`, `TryClickPivotAsync`, `FindAllShopeeMailRowsAsync`, enum `ConfirmOutcome` |
| Luồng subaccount | `SubaccountLoginFlow.cs` (301) | thân `TryLoginSubaccountAsync` |
| Luồng verify email | `EmailVerifyFlow.cs` (160) | thân `TryVerifyByEmailAsync` |

`ShopeeLoginService` giữ nguyên: 4 const URL, 8 forwarder `internal static` cho test (nay trỏ `LoginParsers`),
`EnsureBrowserInstalled`/`DescribeBrowser`/`OpenAsync`, và nested `LoginSession` (nay là facade thuần: giữ vòng
đời process/browser/context + ủy quyền 4 method của `ILoginSession`). **Interface `ILoginSession` không đổi một
chữ** → `OrdersBridgeSession.cs` (agent khác) không bị đụng.

### 2. `AccountsViewModel.cs` 1959 → 519 dòng

| Khối | File | Ghi chú |
|---|---|---|
| Danh sách/lọc/tick/xóa · lựa chọn · panel log | `AccountsViewModel.cs` (519) | + ctor/Dispose/`RunOnUi` |
| Form Chi tiết (Edit\*, Save/Cancel, LoadIntoForm/ClearForm) | `AccountsViewModel.Form.cs` (296) | partial |
| Tab "Kết quả" (thống kê local + hub, sang ngày, cột tiến độ) | `AccountsViewModel.KetQua.cs` (552) | partial |
| Phiên chạy + bridge + cookie | `AccountsViewModel.Phien.cs` (497) | partial |
| Row-VM lưới Kết quả | `ShopPrepareRow.cs` (50) | lớp riêng, tách khỏi file VM |
| Kéo danh bạ từ Hub | `Services/HubDirectoryPuller.cs` (146) | **lớp riêng thật sự** + `TinhLoginCanThem` |

### 3. Test mới

`orders/XuLyDonShopee.Tests/ParseOrdersJsonTests.cs` — 12 ca cho `LoginParsers.ParseOrdersJson` (trước đây
KHÔNG có test nào): map đủ trường (SKU/tiền VND), đơn thiếu `orderSn` bị bỏ, items thiếu/không-phải-mảng, nhiều
item, chuỗi rỗng → null, property sai kiểu, JSON rỗng/hỏng.

### 4. Điểm cần phiên chính soi

1. **Đã SỬA một lệch hành vi do chính việc tách sinh ra** (`LoginBrowserBootstrap.LaunchAndConnectAsync`): bản cũ
   gán `process` vào biến ngoài của `OpenAsync` NGAY sau khi launch, nên lỗi ở bước chờ/nối CDP vẫn được catch
   ngoài kill cây Brave. Sau khi tách, `OpenAsync` chỉ nhận tuple lúc trả về ⇒ lỗi giữa chừng sẽ để Brave mồ côi
   giữ khóa hồ sơ. Đã thêm try/catch trong `LaunchAndConnectAsync` tự Close browser + Kill tree + Dispose rồi
   rethrow → tương đương bản cũ. **Đây là chỗ đáng review kỹ nhất.**
2. **Lệch so với spec — `AccountsViewModel` dùng `partial` thay vì "VM con":** XAML bind thẳng `ResultRows`,
   `ResultDate`, `TongChuanBiHang`, `DangDungSoHub`, `BusyStatus`… (xem `AccountsView.axaml` 610/619/625/639/648).
   Đẩy xuống VM con rồi delegate lại phải tự bắc chuỗi `PropertyChanged` — đúng cái rủi ro plan cảnh báo. Đã
   chọn `partial` (đúng nếp `HubDatabase` 8 partial của repo) + tách **lớp riêng thật** cho 2 chỗ an toàn
   (`ShopPrepareRow`, `HubDirectoryPuller`).
3. **Đã kiểm chứng "không đổi hành vi" bằng 2 phép so máy** (script trong scratchpad): (a) đa tập **string +
   số literal** cũ vs mới — RỖNG cả hai chiều ở CẢ hai file ⇒ không sót/đổi một selector, delay hay chuỗi log
   nào; (b) đa tập **dòng code đã bỏ comment** — mọi chênh lệch đều là đổi tên qualifier (`FindVisibleByTextAsync`
   → `LoginPageProbe.FindVisibleByTextAsync`), đổi `_context`→`context`, `private`→`internal`; riêng
   `AccountsViewModel` chỉ chênh đúng khối `KeoTuHubAsync` (đổi sang 3 callback, GIỮ nguyên thứ tự
   log/BusyStatus/Reload từng nhánh).
4. **Code chết đã DI CHUYỂN NGUYÊN, chưa xóa** (ngoài phạm vi refactor thuần, đề nghị phiên chính quyết):
   `LoginSession.SetWorkPage`/`WorkPage`/`_workPage`, `LoginSelectors.UserSelectors`/`PasswordSelectors`/
   `SubmitSelectors`/`UsePasswordRegex`/`OtherWaysRegex`/`KmsiYesRegex`/`ShopDetailRegex`,
   `LoginPageProbe.FindFirstVisibleAsync`, `LoginParsers.ScanShopListJs` (chỉ còn được nhắc trong doc comment),
   và 2 const public `ShopeeLoginService.SellerUrl`/`ShopListUrl` (không nơi nào gọi).
5. `AccountsViewModel.cs` bản cũ có BOM UTF-8 → đã ghi lại BOM sau khi viết mới (các file mới không BOM, khớp
   phần lớn repo).
