# Plan: Sync shop từ tài khoản BigSeller sang module Đơn hàng

- **Ngày:** 2026-07-21
- **Trạng thái:** hoàn thành (2026-07-21 — Fable nghiệm thu: build 0 lỗi + 767 test nguyên + soi diff, binding khớp idiom #Root.DataContext sẵn có; hành vi DB kiểm end-to-end trên DB thật bằng harness, dữ liệu test đã dọn sạch)
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

**Ngày thực thi:** 2026-07-21 · **Người thực thi:** Opus (`opus-executor`)

### Đã hoàn thành

1. `suite/Shopee.Suite/Modules/BigSeller/BigSellerViewModel.cs`
   - Thêm `using XuLyDonShopee.Core.Models;` (để dùng type `Account`).
   - Thêm `nameof(SyncShopsToOrdersCommand)` vào `[NotifyCanExecuteChangedFor(...)]` của field `_selected`
     → nút tự bật/tắt theo việc có tài khoản đang chọn.
   - Thêm command `SyncShopsToOrders` (`[RelayCommand(CanExecute = nameof(HasSelection))]`), đặt ngay sau
     `RemoveShop`:
     - null-check `OrdersModuleHost.Services` → set `Status = "Module Đơn hàng chưa sẵn sàng — chưa sync
       được shop."` rồi return (không crash).
     - `existing` = `HashSet<string>` gom `svc.Accounts.GetAll()` → `a.Email.Trim()` (lọc rỗng), comparer
       `StringComparer.OrdinalIgnoreCase`.
     - Duyệt `sel.Model.Shops`: `Name.Trim()` rỗng → bỏ (không tính skip); `existing.Add(name)` == false
       (đã có trong DB HOẶC trùng trong lô) → `skipped++`; còn lại →
       `svc.Accounts.Insert(new Account { Email = name, Password = "", Note = "Sync từ BigSeller (<DisplayName>)" })`,
       `added++` (Status mặc định `ChuaKiemTra` do model tự đặt).
     - Thông báo: `"Đã thêm X shop sang Đơn hàng, bỏ qua Y đã có/trùng. Bổ sung mật khẩu hoặc đăng nhập tay
       lần đầu ở tab Đơn hàng."`.
     - Bọc `try/catch (Exception ex)` → `Status = "✘ Lỗi sync shop sang Đơn hàng: " + ex.Message` (không ném).
2. `suite/Shopee.Suite/Modules/BigSeller/BigSellerView.axaml`
   - Thêm nút `⇄ Sync shop → Đơn hàng` vào header panel shops (StackPanel cạnh `+ Thêm shop` / `− Xóa shop`),
     `Command="{Binding #Root.DataContext.SyncShopsToOrdersCommand}"` (Enable theo CanExecute = HasSelection),
     `ToolTip.Tip` giải thích rõ hành vi một chiều + chuyện tài khoản tạo ra chưa có mật khẩu.

### Kết quả kiểm chứng

- **Build:** `dotnet build ShopeeSuite.sln -c Release` → **Build succeeded. 0 Warning(s), 0 Error(s)**
  (ShopeeSuite.dll build ra bình thường).
- **Test orders:** `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj -c Release` →
  **Passed! Failed: 0, Passed: 767, Skipped: 0** (baseline nguyên, không sửa gì orders).
- **Kiểm hành vi trên DB THẬT** (`C:\Users\Ng Xuan Mui\AppData\Roaming\XuLyDonShopee\app.db`): vì driving GUI
  Avalonia không tự động hóa được ổn định (WDAC + không có harness UI), đã kiểm bằng một console harness tạm
  trong scratchpad (`.../scratchpad/synccheck`) gọi **đúng các type mà command dùng** (`Database` +
  `AccountRepository` + `Account`) và **sao chép nguyên thuật toán dedupe** của `SyncShopsToOrders`, chạy trên
  chính DB thật. Kết quả (tất cả PASS):
  - Baseline: 3 tài khoản; không có sẵn tên test.
  - Lô 1 (shops: `shop-test-a`, `  shop-test-b  `, `SHOP-TEST-A`, `   `): added=2, skipped=1 → chèn đúng
    `shop-test-a` + `shop-test-b` (đã trim); `SHOP-TEST-A` bị bỏ qua (trùng-trong-lô, không phân biệt
    hoa/thường); tên rỗng bị bỏ (không tính skip).
  - Kiểm nội dung 2 dòng chèn: `Email` đúng (đã trim), `Password == ""`, `Note == "Sync từ BigSeller (Test
    BigSeller acc)"`, `Status == ChuaKiemTra`.
  - Lô 2 (sync lặp `shop-test-a`, `shop-test-b`): added=0, skipped=2 → dedupe với DB, không tạo trùng; tổng
    đúng 2 dòng test (không nhân đôi).
  - **Dọn sạch:** xóa 2 dòng test (Id 4, 5) qua `AccountRepository.Delete`; kiểm lại DB còn 3 tài khoản
    (== baseline), **0 dòng test còn sót**. Harness nằm ngoài repo (scratchpad), không commit, không để lại
    file trong repo.

### Tiêu chí nghiệm thu (đối chiếu)

- [x] Build sạch (0 warning/error), test orders nguyên (767 pass).
- [x] Sync tạo đúng account (Email = tên shop đã trim, Password rỗng, Note nguồn), dedupe cả với DB lẫn trong
      lô (OrdinalIgnoreCase); sync lặp không tạo trùng.
- [x] Module Đơn hàng chưa sẵn sàng → thông báo nhẹ qua `Status`, return, không crash (null-check
      `OrdersModuleHost.Services`).
- [x] Dòng test đã dọn khỏi DB thật (còn 0 dòng sót, DB về đúng baseline 3 tài khoản).

### Vướng mắc / lưu ý

- Không driving được GUI thật (chỉ Insert/dedupe được kiểm qua repo thật + thuật toán sao chép nguyên). Phần
  UI (nút + binding CanExecute) đã được compiler + build Release xác nhận về mặt cấu trúc; hành vi DB đầu-cuối
  đã kiểm trên DB thật bằng đúng type repository mà command gọi.
- Đã tuân thủ: KHÔNG commit; KHÔNG sửa file trong `orders/`, `server/`, `shared/`, `main` (chỉ đọc
  `OrdersModuleHost.Services` để Insert); chỉ 2 file nguồn thay đổi (`git status` xác nhận).
