# Plan: Xem đơn TOÀN HỆ THỐNG từ Hub (đọc thẳng, không chép về máy)

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh & mục tiêu

Người dùng: *"client 1 sync hub được, nhưng chưa có flow để sync từ hub về các client"* — muốn **ở máy nào cũng
xem được đơn của MỌI máy**. Đã chốt: phạm vi **toàn bộ đơn**, mục đích **CHỈ ĐỂ XEM** (máy khác không xử lý).

**QUYẾT ĐỊNH THIẾT KẾ (Fable chốt, đã nêu với người dùng): ĐỌC THẲNG TỪ HUB, KHÔNG chép về CSDL máy.**
Người dùng ban đầu nói "mirror", nhưng kết quả nhìn thấy là như nhau (thấy toàn bộ đơn) mà cách này an toàn hơn
hẳn. Lý do phải tránh chép về bảng `orders` local:
1. Đơn trên Hub thuộc shop của **máy khác** → không có `account_id` local để gắn; bịa ra là hỏng khoá
   `(account_id, order_sn)`.
2. Chép vào `orders` sẽ **lây nhiễm mọi luồng đang chạy**: bị đẩy ngược lên Hub, bị ghi trùng dòng Google Sheet,
   bị vòng dọn "đơn kết thúc" xoá, bị vòng chờ đẩy nhặt nhầm.
3. Xem đơn toàn hệ thống vốn đã cần Hub sống → không được lợi gì khi lưu offline.
⇒ Đọc thẳng: luôn tươi, không phình CSDL, **không đụng một dòng nào** của luồng nghiệp vụ hiện có.

## 2. Hiện trạng (đã khảo sát — bám theo)

- **Hub đã có sẵn** `GET /api/orders?shopId=&status=&q=&page=&pageSize=`
  (`server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs:224`) trả `{ items, total, page, pageSize }`;
  `items` là `List<OrderRecord>` (`HubDatabase.Orders.cs:8`) — **kiểu của riêng hub, client KHÔNG thấy**.
  Endpoint này đang gọi `LogLegacyHit(...)` → mỗi lần client gọi sẽ ghi 1 dòng **cảnh báo sai lệch** vào log hub.
- `GET /api/shops` (`HubRoutes.Shops`) trả danh sách shop — cần để đổi `shopId` (số) sang tên shop.
- `suite/Shopee.Core/Coordination/OrderDtos.cs` chỉ có DTO **đẩy lên** (`OrderPushItem`…), **chưa có DTO đọc về**.
- `HubClient` **chưa có** method đọc đơn / đọc shop.
- **Ràng buộc kiến trúc:** module Đơn hàng KHÔNG tham chiếu `Shopee.Core` ⇒ mọi thứ dính hub phải đi qua **hook
  `Func<...>` rót từ `OrdersModuleHost`** (khuôn sẵn có: `PushOrdersToHub`, `PushGsheetConfigToHub`…).

## 3. Phạm vi

- **Làm:** DTO dùng chung + method `HubClient` + hook + **một màn CHỈ-ĐỌC mới** trong tab Shopee.
- **KHÔNG làm:** không chép đơn về CSDL local; không đụng màn "Đơn hàng" hiện có (nó là đơn của máy này, có
  hành động in/xuất/sync — trộn nguồn vào đó là mời lỗi); không thêm hành động xử lý đơn trên màn mới.

## 4. Các bước thực hiện

### Bước 1 — DTO dùng chung (chống lệch âm thầm)
Tạo `suite/Shopee.Core/Coordination/HubOrderDtos.cs`:
- `HubOrderItem`: `Id, ShopId, OrderSn, ShopeeOrderId, BuyerUsername, ItemCount, ItemSummary, Sku, TotalPrice,
  TotalPriceText, FinalAmount, FinalAmountText, PaymentMethod, Status, StatusDescription, CancelReason, Channel,
  Carrier, TrackingNumber, SyncedAt, SlipAt`.
- `HubOrdersPage`: `Items (List<HubOrderItem>), Total, Page, PageSize`.
- `HubShopItem`: `Id, Username, Name` (khớp `db.ListShops()` — tự đọc kiểu thật rồi map).
LINK vào `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` (theo mẫu đã link `OrdersSharedConfig.cs`).

### Bước 2 — Hub: trả DTO dùng chung + bỏ cảnh báo sai
- `GET /api/orders`: **map tường minh** `OrderRecord` → `HubOrderItem` rồi trả `HubOrdersPage`.
  Map tay (KHÔNG dựa vào trùng tên tự động) để đổi tên field bên hub không âm thầm làm rỗng cột bên client.
- **Bỏ `LogLegacyHit`** ở route này (nay là endpoint client CHÍNH THỨC, không còn legacy). Giữ nguyên ở các route
  legacy khác.
- `GET /api/shops`: trả `List<HubShopItem>` (map tường minh y trên) — hoặc giữ nguyên nếu shape đã khớp, tự kiểm.

### Bước 3 — Client: `HubClient`
Thêm 2 method (khuôn các method sẵn có, dùng `_http` timeout ngắn — đây là truy vấn UI, KHÔNG dùng `_bulkHttp` 5'):
- `Task<HubOrdersPage?> QueryOrdersAsync(long? shopId, string? status, string? q, int page, int pageSize, CancellationToken ct)`
- `Task<IReadOnlyList<HubShopItem>?> ListShopsAsync(CancellationToken ct)`
Lỗi mạng/hub cũ (404) → trả `null` (nghĩa "không lấy được"), KHÔNG ném.

### Bước 4 — Hook sang module Đơn hàng
- `orders/XuLyDonShopee.App/Services/AppServices.cs`: thêm 2 hook (mặc định `null` = tắt, app chạy độc lập y cũ):
  - `Func<HubOrdersQuery, CancellationToken, Task<HubOrdersResult?>>? QueryHubOrders`
  - `Func<CancellationToken, Task<IReadOnlyList<(long Id, string Name)>?>>? ListHubShops`
  → `HubOrdersQuery`/`HubOrdersResult` là **kiểu của riêng module Đơn hàng** (không thấy `Shopee.Core`);
  `OrdersModuleHost` chịu trách nhiệm chuyển đổi qua lại.
- `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`: rót 2 hook theo đúng khuôn `WireHubPush`
  (guard `CoordinationRuntime.Active`/`Client is null` → trả null; nuốt lỗi → `Trace`).

### Bước 5 — Màn mới "Đơn toàn hệ thống" (CHỈ ĐỌC)
- View + VM mới trong `orders/XuLyDonShopee.App/` (vd `Views/HubOrdersView.axaml` + `ViewModels/HubOrdersViewModel.cs`).
- Thêm vào **ribbon tab Shopee** (`suite/Shopee.Suite/ViewModels/ShellViewModel.cs`, cạnh "Tài khoản"/"Đơn hàng")
  bằng `RibbonScreenItem` — chọn icon từ bộ sẵn có (`AppIcons` cho nút điều hướng; tra bảng, đừng chế icon mới).
- Nội dung: lưới chỉ-đọc + thanh lọc **Shop / Trạng thái / Tìm kiếm** + **phân trang** (Hub đã hỗ trợ
  `page`/`pageSize`, LỌC VÀ PHÂN TRANG Ở PHÍA HUB — đừng tải hết về rồi lọc ở client).
- Cột: Mã đơn · Shop · Người mua · Sản phẩm · SKU · Tổng tiền · **Ước tính** · Trạng thái · Vận chuyển · Đồng bộ lúc.
- Nút: **Tải lại** (`IconRefresh`) — không có nút hành động nghiệp vụ nào khác.
- Dùng đúng hệ nút/icon/kiểu bảng mới (GĐ4): nút một dáng, màu chỉ ở icon; bảng theo style hiện hành.
- Style theo module Đơn hàng (merge `ModuleResources.axaml` như các view khác — xem `OrdersView.axaml`).

### Bước 6 — Trạng thái rỗng / Hub chết (BẮT BUỘC, chống "hỏng im lặng")
- Chưa cấu hình hub / hook null → hiện dòng: "Máy này chưa kết nối Hub — không xem được đơn toàn hệ thống."
- Gọi được nhưng hub không phản hồi → "Không lấy được dữ liệu từ Hub (Hub không phản hồi). Thử Tải lại."
- Hub trả 0 đơn → "Chưa có đơn nào trên Hub."
- **Ba ca này phải PHÂN BIỆT được** — không được cùng hiện một lưới trống.

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution + `dotnet build server/Shopee.Hub.Web` 0 error; `dotnet test` xanh.
- [ ] Mở màn mới: thấy đơn của **mọi shop/mọi máy** trên Hub (không chỉ máy này); lọc theo shop/trạng thái/tìm
      kiếm và chuyển trang chạy đúng (kiểm bằng cách so số tổng với trang `/orders` của Hub web).
- [ ] **KHÔNG có đơn nào bị ghi vào CSDL local** — kiểm `app.db` trước/sau khi mở màn: số dòng bảng `orders`
      KHÔNG đổi (đây là tiêu chí quan trọng nhất, chứng minh không lây nhiễm luồng hiện có).
- [ ] Ngắt Hub → hiện đúng thông báo "Hub không phản hồi", không phải lưới trống câm.
- [ ] App Đơn hàng chạy ĐỘC LẬP (hook null) → màn hiện thông báo chưa kết nối Hub, không lỗi.
- [ ] Màn "Đơn hàng" cũ (đơn của máy này) **không đổi hành vi**.
- [ ] Không còn dòng cảnh báo "legacy endpoint hit" cho `/api/orders` trong log hub.

## 6. Rủi ro & lưu ý

- **Map DTO tay** ở Bước 2: đây là điểm dễ "chạy mà rỗng cột" nếu chỉ dựa vào trùng tên. Map tường minh + tự kiểm
  bằng cách gọi thật một lượt.
- Hub trả `shopId` là SỐ → phải map sang tên shop; nạp danh sách shop MỘT LẦN rồi tra, đừng gọi mỗi dòng.
- **Không** dùng `_bulkHttp` (timeout 5') cho truy vấn UI — treo Hub sẽ làm màn đơ 5 phút.
- Gọi hub phải **bất đồng bộ, không chặn UI thread**; huỷ được khi người dùng đổi bộ lọc liên tục (giữ
  `CancellationTokenSource` cho lượt trước).
- Sửa hub ⇒ cần **deploy lại VM** sau khi xong (quy trình ở CLAUDE.md). Plan này CHỈ code, Fable quyết lịch deploy.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Đã làm đủ 6 bước. KHÔNG commit, KHÔNG deploy hub.**

Tạo mới: `suite/Shopee.Core/Coordination/HubOrderDtos.cs` · `orders/XuLyDonShopee.App/Services/HubOrdersContracts.cs` ·
`orders/XuLyDonShopee.App/ViewModels/HubOrdersViewModel.cs` + `HubOrderRowViewModel.cs` ·
`orders/XuLyDonShopee.App/Views/HubOrdersView.axaml(.cs)` · `orders/XuLyDonShopee.Tests/HubOrdersViewModelTests.cs` (13 test).
Sửa: hub csproj (LINK DTO) · `ClientApiEndpoints.cs` · `HubClient.cs` · `AppServices.cs` · `OrdersModuleHost.cs` ·
`MainViewModel.cs` (nav index 2) · `ShellViewModel.cs` (nút ribbon `AppIcons.Servers`).

Kiểm chứng: build solution + hub 0 error; `dotnet test` 1000/1000 xanh; hub CỤC BỘ (port 18099, data dir tạm) đẩy 3 đơn
2 shop rồi đọc lại qua `HubClient` thật → **0 cột rỗng** ở CẢ HAI hop map (`OrderRecord→HubOrderItem`,
`HubOrderItem→HubOrderView`); lọc shopId/status/q + phân trang chạy đúng phía hub; hub chết → `null`; log hub **0 dòng**
"legacy endpoint hit" cho `/api/orders` + `/api/shops` (đối chứng `/accounts/append` vẫn ghi). Harness Avalonia dựng
THẬT màn mới: 10 cột đúng, 2 PathIcon đều có Data, 5 ca trạng thái hiện đúng chữ khác nhau.

**Lệch plan (cần Fable soi):** (1) bỏ luôn `LogLegacyHit` ở `GET /api/shops` — plan chỉ yêu cầu bỏ ở `/api/orders`,
nhưng client nay gọi `/api/shops` mỗi lượt mở màn nên giữ lại sẽ spam cảnh báo sai y hệt lý do của `/api/orders`;
(2) `/api/shops` nay trả `HubShopItem` (3 field) thay vì `Shop` đầy đủ → **cắt** password/cookie/proxy_key khỏi dây
(repo không còn consumer nào khác của route này); (3) thêm ca thứ 4 "lọc không ra" tách khỏi "Hub có 0 đơn";
(4) ô lọc trạng thái lấy từ hợp (trạng thái thấy trong trang Hub vừa nhận ∪ `orders.status` local) vì Hub chưa có
route liệt kê trạng thái — plan không nêu, đã tránh thêm route mới.
