# Plan: Tách ShopeeLoginService (orders) + AccountsViewModel (đợt 4 — orders B)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
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

(chưa)
