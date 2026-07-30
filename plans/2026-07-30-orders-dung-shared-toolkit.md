# Plan: Orders dùng hạ tầng chung `shared/Shopee.Toolkit` (3F)

- **Ngày:** 2026-07-30
- **Trạng thái:** chờ (chạy SAU khi plan B1 merge — chung khu orders)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

`orders/` không ref `suite/Shopee.Core` (chủ đích — né dây Avalonia). Hệ quả: 4 hạ tầng bị chép tay 2 bản, đã bắt đầu LỆCH (kiểm chứng 30/07):

1. **WebSocketServer:** `orders/XuLyDonShopee.Core/Services/OrdersWebSocketServer.cs` (155 dòng, header tự khai "Chép khuôn WebSocketServer của module Search") vs `suite/Shopee.Module.Search/Engine/WebSocketServer.cs` (126 dòng). **Drift mới:** bản orders có `SendAsync` fail-fast (fix 1B.3) + `SendOptions` — bản Search KHÔNG. Hợp nhất PHẢI giữ fail-fast.
2. **Brave launch args:** `orders/…/Services/BraveLaunchArgs.cs` (150 dòng: BuildArgs + BuildCleanPocArgs, hỗ trợ --load-extension + DisableLoadExtensionCommandLineSwitch) vs `suite/Shopee.Core/Browser/BraveArgsBuilder.cs`.
3. **BrowserLocator:** `orders/…/Services/BrowserLocator.cs` (266 dòng) vs `suite/Shopee.Core/Platform/Windows/WindowsBrowserLocator.cs` + `Linux/LinuxBrowserLocator.cs` — bản suite có registry fallback (đầy đủ hơn).
4. **Bộ selector Microsoft (MS-mail-login):** `orders/…/Services/ShopeeLoginService.cs` `LoginHotmailAsync:~1225` + `Ms*Selectors:~550-571` vs `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs:27-45` (tự khai "PORT từ ShopeeLoginService"). Driver khác nhau (Playwright vs CDP) — thứ TRÙNG là các bộ selector.

Mẫu làm đúng có sẵn: `shared/Shopee.Proxy.Kiot` (cả 2 phía ref).

## 2. Phạm vi

- **Làm:** tạo project mới `shared/Shopee.Toolkit` (net8.0, KHÔNG dep UI/Avalonia/Playwright) chứa 4 hạ tầng trên; orders + suite chuyển sang dùng; xoá bản trùng.
- **Không làm:** không đổi hành vi (trừ Search WebSocketServer NHẬN fail-fast của orders — thay đổi chủ đích, ghi rõ); không đụng `extensions/**`, `server/**`; không gộp driver MS-login (chỉ selector + logic thuần chuỗi).

## 3. Các bước thực hiện

1. Tạo `shared/Shopee.Toolkit/Shopee.Toolkit.csproj`; add vào `ShopeeSuite.sln` (orders và suite project đều nằm sln này — kiểm tra cả `server/ShopeeHub.sln` không cần).
2. **WebSocketServer** → `shared/Shopee.Toolkit/Ws/WebSocketServer.cs`: lấy bản orders làm gốc (có fail-fast + SendOptions); Search chuyển sang dùng; xoá 2 bản cũ. Đối chiếu diff 2 bản trước khi xoá — tính năng nào chỉ bản Search có thì giữ qua option.
3. **BraveArgs** → `shared/Shopee.Toolkit/Browser/BraveArgs.cs`: hợp nhất BuildArgs orders + BraveArgsBuilder suite (tham số hoá khác biệt; --load-extension + DisableLoadExtensionCommandLineSwitch giữ nguyên); 2 phía chuyển sang dùng; xoá bản cũ. So từng flag — thiếu/thừa flag Brave là đổi hành vi anti-bot.
4. **BrowserLocator** → `shared/Shopee.Toolkit/Browser/BrowserLocator.cs`: lấy bản suite (Windows registry fallback + Linux) làm gốc; orders chuyển sang; xoá bản orders. Suite Core giữ wrapper mỏng nếu Platform/* đang là contract nơi khác dùng.
5. **MS selectors** → `shared/Shopee.Toolkit/MsLogin/MsLoginSelectors.cs`: gom các bộ selector (user/pass/sign-in/OTP/stay-signed-in…) thành hằng dùng chung; `HotmailOtpReader` (suite) + `ShopeeLoginService.LoginHotmailAsync` (orders) cùng đọc từ đây; so 2 bộ hiện tại — selector nào chỉ 1 bên có thì đưa vào chung (2 bên đều hưởng), ghi bảng đối chiếu.
6. Build + test cả 2 solution; chạy test orders đầy đủ.

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 lỗi 0 warning; test không tụt baseline.
- [ ] Grep: không còn class WebSocketServer/BraveLaunchArgs/BrowserLocator định nghĩa ngoài `shared/Shopee.Toolkit` (trừ wrapper mỏng khai báo rõ); selector Microsoft literal chỉ còn trong MsLoginSelectors.
- [ ] `shared/Shopee.Toolkit.csproj` không ref Avalonia/Playwright/WPF.
- [ ] Báo cáo: bảng diff 2 bản của từng hạ tầng + quyết định giữ gì.

## 5. Rủi ro & lưu ý

- Chạy SAU B1 (chung file orders Services). Số dòng plan là ước lượng — tìm theo symbol.
- Search WS nhận fail-fast: kiểm tra caller Search xử lý exception mới (grep chỗ gọi SendAsync phía Search, bọc catch nếu caller đang dựa vào nuốt-im).
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
