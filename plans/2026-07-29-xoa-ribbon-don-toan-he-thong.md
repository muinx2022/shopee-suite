# Plan: Xóa ribbon + màn "Đơn toàn hệ thống" (client)

- **Ngày:** 2026-07-29
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Auto

## 1. Bối cảnh & mục tiêu

Người dùng muốn **xóa ribbon "Đơn toàn hệ thống"** trên tab Shopee và **toàn bộ code liên quan**
(màn xem đơn mọi máy từ Hub trên client).

Đã khảo sát: đây là tính năng CHỈ ĐỌC phía client (plan `2026-07-26-xem-don-toan-he-thong-tu-hub.md`).
Trang **Hub web** `/orders` (admin xem đơn) là đường riêng — **KHÔNG xóa**.

**Giữ nguyên (không liên quan / vẫn cần):**
- `HubOrdersConfig` — cấu hình GSheet đồng bộ hub→client (trùng tên "HubOrders" nhưng khác việc).
- Trang Hub web `/orders` + `GET /api/orders` + DTO `HubOrderItem`/`HubOrdersPage` (API admin/client khác).
- `GET /api/shops` + `HubShopItem` (DTO shop rút gọn).
- Hook `GetPrepareStats` / tab Kết quả (số đơn chuẩn bị hàng chung).

## 2. Phạm vi

- **Làm:** Gỡ ribbon + màn + hook đọc + client HubClient methods chỉ phục vụ màn này + test; đánh lại chỉ số
  nav (Thống kê từ 3 → 2).
- **Không làm:** KHÔNG đụng Hub web `/orders`, KHÔNG đụng API hub, KHÔNG đụng `HubOrdersConfig`/GSheet,
  KHÔNG commit/release (trừ commit file plan này). KHÔNG đụng WIP `OrderStatistics*` / `ConfigOrders.razor` /
  `scratchpad/` đang uncommitted.

## 3. Các bước thực hiện

### Bước 1 — Xóa file màn + test + contract

Xóa hẳn:
- `orders/XuLyDonShopee.App/Views/HubOrdersView.axaml`
- `orders/XuLyDonShopee.App/Views/HubOrdersView.axaml.cs`
- `orders/XuLyDonShopee.App/ViewModels/HubOrdersViewModel.cs`
- `orders/XuLyDonShopee.App/ViewModels/HubOrderRowViewModel.cs`
- `orders/XuLyDonShopee.App/Services/HubOrdersContracts.cs`
- `orders/XuLyDonShopee.Tests/HubOrdersViewModelTests.cs`

### Bước 2 — `MainViewModel.cs`

- Bỏ field/property `_hubOrdersVm` / `HubOrdersVm` và khởi tạo.
- `NavItems`: bỏ mục "Đơn toàn hệ thống" → còn Tài khoản / Đơn hàng / Thống kê.
- `OnSelectedNavIndexChanged`: bỏ `case 2` (HubOrders); `case 3` (Thống kê) → thành `case 2`.
- Cập nhật XML doc (không còn 4 màn với hub orders).

### Bước 3 — `ShellViewModel.cs` (ribbon Shopee)

- Xóa `oHubOrders` (RibbonScreenItem "Đơn toàn hệ thống").
- Nhóm "Màn hình": `{ oAccounts, oOrders, oStatistics }`.
- Đổi index của `oStatistics` từ `3` → `2` (khớp `MainViewModel` sau khi bỏ HubOrders).

### Bước 4 — `AppServices.cs`

- Xóa property `QueryHubOrders` và `ListHubShops` (kèm XML doc).

### Bước 5 — `OrdersModuleHost.cs`

- Bỏ gọi `WireHubOrdersRead(Services);` trong khởi tạo.
- Xóa toàn bộ method `WireHubOrdersRead` và helper `ToHubOrderView`.
- Sửa XML doc của `WirePrepareStatsRead` nếu còn `see cref="WireHubOrdersRead"` → đổi sang `WireHubPush`
  hoặc bỏ cref.

### Bước 6 — `HubClient.cs`

- Xóa method `QueryOrdersAsync` (chỉ phục vụ màn này).
- Xóa method `ListShopsAsync` **chỉ khi** sau bước 5 không còn call site nào (đã xác nhận: chỉ
  `WireHubOrdersRead` gọi). Nếu còn chỗ khác thì GIỮ.

### Bước 7 — Kiểm chứng

- `dotnet build ShopeeSuite.sln` sạch.
- `dotnet test orders/XuLyDonShopee.Tests` xanh (số test giảm đúng bằng số test trong
  `HubOrdersViewModelTests` đã xóa; không fail vì thiếu type).
- Grep toàn repo (trừ `plans/` + `CHANGELOG.md`): không còn `HubOrdersViewModel`, `QueryHubOrders`,
  `ListHubShops`, `WireHubOrdersRead`, chuỗi ribbon `"Đơn toàn hệ thống"` trong `.cs`/`.axaml`.

### Bước 8 — Cập nhật trạng thái plan

Đặt `Trạng thái: hoàn thành` ở đầu file plan này.

## 4. Tiêu chí nghiệm thu

- [ ] Ribbon tab Shopee chỉ còn: Tài khoản · Đơn hàng · Thống kê (không còn "Đơn toàn hệ thống").
- [ ] Bấm Thống kê vẫn mở đúng màn (index 2).
- [ ] Build solution sạch; test orders xanh.
- [ ] Grep bước 7 sạch (trừ lịch sử plan/CHANGELOG).
- [ ] Hub web `/orders` và đồng bộ GSheet vẫn hoạt động (không đụng code đó).

## 5. Rủi ro & lưu ý

- **Lệch index ribbon ↔ MainViewModel** là bẫy chính: sửa cả hai chỗ (Shell index + switch case).
- Đừng xóa nhầm `HubOrdersConfig` / DTO `HubOrderItem` phía API.
- Không xóa plan cũ `plans/2026-07-26-xem-don-toan-he-thong-tu-hub.md` (lịch sử quyết định).

---

## Báo cáo thực thi (Auto điền sau khi xong)

- Đã xóa 6 file màn/contract/test HubOrders.
- Đã sửa MainViewModel (3 màn, Thống kê = index 2), ShellViewModel (bỏ ribbon), AppServices (bỏ 2 hook),
  OrdersModuleHost (bỏ WireHubOrdersRead + ToHubOrderView), HubClient (bỏ QueryOrdersAsync + ListShopsAsync).
- Comment API hub cập nhật (không còn nhắc màn đã xóa); API/DTO/`HubOrdersConfig` giữ nguyên.
- Build sạch; test **1381 passed** (giảm 13 = đúng số test HubOrdersViewModelTests).
- Grep `.cs`/`.axaml`: sạch (trừ plans/CHANGELOG lịch sử).
- **Không commit.**
