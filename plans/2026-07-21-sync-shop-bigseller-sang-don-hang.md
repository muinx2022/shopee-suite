# Plan: Sync shop từ tài khoản BigSeller sang module Đơn hàng

- **Ngày:** 2026-07-21
- **Trạng thái:** chờ thực thi (xếp lịch SAU khi merge nhánh ribbon — cùng vùng UI suite)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** cây làm việc CHÍNH `D:\Projects\shopee-suite`, nhánh `feature/gop-don-hang`. KHÔNG đụng `main`.

## 1. Bối cảnh & mục tiêu

Màn "Cấu hình BigSeller" quản lý tài khoản BigSeller, mỗi tài khoản có danh sách shop
(`BigSellerAccount.Shops`). Người dùng muốn: **nút sync danh sách shop sang module Đơn hàng**
— tạo sẵn dòng tài khoản shop bên Đơn hàng để đỡ nhập tay.

Sự thật dữ liệu (khảo sát 2026-07-21, trích file:dòng trong báo cáo khảo sát):
- `BigSellerShop` (`suite/Shopee.Core/BigSeller/BigSellerShop.cs:4-39`) CHỈ có `Name` +
  cấu hình sheet/crawl/AI — **KHÔNG có username/password/cookie Shopee**. Credential duy nhất
  là của tài khoản BigSeller (không phải shop).
- `Account` bên Đơn hàng (`orders/.../Models/Account.cs`): `Email` bắt buộc (username đăng
  nhập), `Password` bắt buộc ở UI nhưng **repo `Insert` nhận chuỗi rỗng hợp lệ**
  (`AccountRepository.cs:42`, DB `NOT NULL` nhưng nhận `""`); **DB KHÔNG có UNIQUE email**,
  dedupe chỉ ở UI (`AccountsViewModel.Save():481-488`) → đường sync PHẢI TỰ DEDUPE.
- Account thiếu password: mở phiên vẫn chạy — `AccountSession.cs:1322-1329` bỏ qua auto-login,
  Brave mở cho người dùng login tay, app tự bắt cookie lưu (`:1375-1385`). → UX chấp nhận được.
- Cầu nối: `BigSellerViewModel` (project `Shopee.Suite`) gọi thẳng
  `OrdersModuleHost.Services?.Accounts` (`Infrastructure/OrdersModuleHost.cs:23`) — nhớ
  null-check (module Đơn hàng có thể init hỏng).
- Màn Tài khoản bên Đơn hàng tự `Reload()` khi vào lại tab (`MainViewModel.OnSelectedNavIndexChanged`)
  — KHÔNG cần chế event mới; chỉ cần thông báo kết quả cho người dùng biết.

Quy ước danh tính: `Account.Email` = `BigSellerShop.Name` (trim) — thực tế người dùng đặt tên
shop trùng username cửa hàng (vd `shop9x.store`), và hub cũng key shop theo giá trị này.

## 2. Phạm vi

- **Làm:**
  - `suite/Shopee.Suite/Modules/BigSeller/BigSellerViewModel.cs`: command mới
    `SyncShopsToOrdersCommand` (RelayCommand) — sync các shop của **tài khoản BigSeller đang
    chọn**:
    1. `var svc = OrdersModuleHost.Services;` null → set thông báo trạng thái "Module Đơn hàng
       chưa sẵn sàng" (dùng đúng cơ chế StatusText/thông báo sẵn có của VM), return.
    2. `existing` = `svc.Accounts.GetAll()` → HashSet email (trim, OrdinalIgnoreCase).
    3. Duyệt `Shops` của account đang chọn: `Name` trim rỗng → bỏ; đã có trong `existing`
       (hoặc trùng trong chính lô) → đếm skip; còn lại →
       `svc.Accounts.Insert(new Account { Email = name, Password = "", Note = "Sync từ BigSeller (<label tài khoản>)" })`
       (Status mặc định ChuaKiemTra), đếm added.
    4. Thông báo kết quả: "Đã thêm X shop sang Đơn hàng, bỏ qua Y đã có/trùng." — kèm nhắc
       "Bổ sung mật khẩu hoặc đăng nhập tay lần đầu ở tab Đơn hàng."
    5. Bọc try/catch — lỗi → thông báo lỗi, không ném.
  - `suite/Shopee.Suite/Modules/BigSeller/BigSellerView.axaml`: nút "⇄ Sync shop → Đơn hàng"
    đặt ở header panel shops (cạnh `+ Thêm shop` / `− Xóa shop`), Enable khi có tài khoản đang
    chọn; ToolTip giải thích rõ hành vi + chuyện thiếu mật khẩu.
- **Không làm:**
  - KHÔNG đụng code module orders (repo Insert dùng nguyên trạng); KHÔNG thêm UNIQUE/upsert
    vào DB orders trong việc này; KHÔNG sync ngược (một chiều BigSeller → Đơn hàng).
  - KHÔNG sửa `ShellViewModel`/`MainWindow` (vùng ribbon vừa merge); KHÔNG đụng `server/`.
  - KHÔNG commit.

## 3. Kiểm chứng

- `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi 0 warning mới.
- `dotnet test orders/...Tests` → baseline pass nguyên (không sửa gì orders nên phải nguyên).
- Kiểm hành vi bằng chạy app (nếu WDAC cho phép): tạo tài khoản BigSeller giả + 2 shop
  (`shop-test-a`, `shop-test-b`) → bấm Sync → mở tab Đơn hàng > Tài khoản thấy 2 dòng mới,
  Note ghi nguồn; bấm Sync LẦN 2 → thông báo bỏ qua 2, không tạo trùng. (Chạy trên DB thật
  `%APPDATA%\XuLyDonShopee\app.db` — tạo bằng TÊN SHOP TEST rõ ràng rồi XÓA 2 dòng test qua
  chính UI sau khi kiểm xong, ghi lại trong báo cáo. Không đụng dòng dữ liệu thật.)

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch, test orders nguyên.
- [ ] Sync tạo đúng account (Email=tên shop, Password rỗng, Note nguồn), dedupe cả với DB lẫn
      trong lô; sync lặp không tạo trùng.
- [ ] Module Đơn hàng chưa sẵn sàng → thông báo nhẹ nhàng, không crash.
- [ ] Dòng test đã dọn khỏi DB thật sau khi kiểm.

## 5. Rủi ro & lưu ý

- DB orders KHÔNG có UNIQUE email — dedupe phải chắc (trim + OrdinalIgnoreCase, cả trong lô).
- Đây là ghi vào DB THẬT của người dùng — chỉ Insert, không Update/Delete dữ liệu có sẵn;
  dòng test phải dọn sạch.
- Roadmap đã chốt KHÔNG gộp kho tài khoản 2 bên — đây là luồng tiện ích MỘT CHIỀU tạo bản ghi
  mới, không phải hợp nhất kho; giữ đúng tinh thần đó (không tham chiếu ngược).

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
