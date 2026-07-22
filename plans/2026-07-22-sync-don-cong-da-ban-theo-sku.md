# Plan: Sync đơn — đơn "đã giao" thì +1 "Đã bán" theo SKU + đẩy hub

- **Ngày:** 2026-07-22
- **Trạng thái:** hoàn thành (code) · chờ deploy hub + release client
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Khi SYNC ĐƠN Shopee (module `orders/`), đơn nào **chuyển sang trạng thái đã giao** thì **+1 vào "Đã bán"** của sản phẩm khớp **SKU** trong kho sản phẩm (Hub Postgres) — tức đẩy thẳng lên hub.

**Quyết định đã chốt với người dùng (qua hỏi đáp):**
1. **Trạng thái tính "đã bán" (+1):** đơn ở một trong {`"Đã giao"`, `"Hoàn thành"`, `"Giao hàng thành công"`} — **khớp TUYỆT ĐỐI** (case-insensitive, trim), nên **loại** `"Đã giao cho đơn vị vận chuyển"`/`"...cho ĐVVC"` (mới giao ĐVVC, chưa nhận). `"Đã hủy"` → không tính.
2. **Chống đếm trùng (idempotent):** mỗi đơn chỉ +1 **một lần**. Vì sync đọc lại toàn trang mỗi lần, phải có cột cờ.
3. **KHÔNG đếm bù (no backfill):** chỉ +1 khi đơn **chuyển TỪ chưa-giao SANG đã-giao** giữa 2 lần sync. Đơn **lần đầu đã thấy delivered sẵn** (đơn cũ có từ trước khi bật tính năng) → **grandfather**: đánh dấu đã-đếm nhưng KHÔNG +1.
4. **Khớp SKU: TẠM khớp TOÀN BỘ (mọi shop)** — chưa phân shop. (Per-shop cần ánh xạ shop-đơn ↔ (BigSeller acct, sheet) — hiện KHÔNG có liên kết, để làm sau ở plan riêng.)
5. **Phạm vi sync: đọc tab "Tất cả", giới hạn 10 trang đầu.** (Không đổi sang cơ chế "chỉ Chờ lấy hàng + quét tab sau" vì cần testid các tab sau từ DOM thật + rework rủi ro.)

**Hiện trạng code (khảo sát):**
- Sync: `AccountSession.SyncOrdersAsync` (`orders/XuLyDonShopee.App/Services/AccountSession.cs:609`) → `ShopeeLoginService.SyncAllOrdersAsync` (`orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs:3391`) đọc tab "Tất cả" (`:3454-3455`), vòng quét trang (`:3462`), cap `MaxSyncPages = 20` (`:3389`, `:3540`). `UpsertMany` (`OrdersRepository.cs:50`) trả `(Inserted, Updated, InsertedOrders)` — KHÔNG có tín hiệu "đơn vừa đổi status".
- DB đơn SQLite: bảng `orders` (`orders/XuLyDonShopee.Core/Data/Database.cs:98-128`), khóa `UNIQUE(account_id, order_sn)`; mẫu thêm cột `EnsureColumn` (`Database.cs:169`), mẫu cột cờ `hub_synced_at` (`:112,:161`).
- Kho SP trên Hub Postgres: `product_rows` + `product_sold` keyed `(account_id, sheet, row_no)`; SKU ở `product_rows.sku`. Tăng bán: `MarkSoldAsync(keys)` = `sold_count+1` (`server/Shopee.Hub.Web/Data/ProductDb.AllData.cs:90`). Chưa có truy vấn "tìm dòng theo SKU rồi +1".
- Cổng client↔hub SP: `HubClient.MarkProductsSoldAsync` (`suite/Shopee.Core/Coordination/HubClient.cs:208`) POST `/products/mark-sold` (`HubRoutes.cs:69`). Routes ở `HubRoutes.cs:68-73`; endpoint hub ở `server/Shopee.Hub.Web/Api/ProductApiEndpoints.cs`.
- Hook client↔hub cho module đơn: mẫu `PushOrdersToHub` — khai báo `AppServices.cs:39`, rót ở `OrdersModuleHost.WireHubPush` (`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs:57-93`), chạy nền `StartHubPushInBackground` (`AccountSession.cs:699,820,842`). Module đơn KHÔNG tham chiếu `Shopee.Core`/`HubClient` → phải rót hook từ shell.
- SKU đơn: `SyncedOrder.Sku`/`OrderRow.Sku` (rút từ đuôi tên SP, `ShopeeShippingNav.ExtractSku`); dạng "B#####".

## 2. Phạm vi

- **Làm:** thêm route hub "+1 theo SKU (mọi shop)"; cột cờ + phát hiện chuyển-sang-đã-giao (no backfill) ở module đơn; giới hạn sync 10 trang; hook rót từ shell gọi hub +1 nền sau sync.
- **Không làm:**
  - KHÔNG làm ánh xạ per-shop (để plan sau) — tạm +1 mọi dòng khớp SKU.
  - KHÔNG đổi sang cơ chế "chỉ đọc Chờ lấy hàng + quét tab sau".
  - KHÔNG đụng gsheet/notify; KHÔNG đụng module Scrape/Search.
  - KHÔNG đếm theo số lượng trong đơn — **+1 mỗi đơn delivered** cho SKU đại diện của đơn (`SyncedOrder.Sku`). Đơn không có SKU (null) → bỏ qua.

## 3. Các bước thực hiện

### A. HUB — route "+1 Đã bán theo SKU" (khớp tuyệt đối, mọi shop)

1. `server/Shopee.Hub.Web/Data/ProductDb.*` — thêm method `MarkSoldBySkuAsync(string sku)` (hoặc nhận `IReadOnlyList<string> skus` để gộp 1 lượt): tăng `sold_count+1` cho **mọi** dòng có `product_rows.sku = $sku` (khớp tuyệt đối, KHÔNG ILIKE). Thực hiện trong 1 câu: `INSERT INTO product_sold(...) SELECT account_id,sheet,row_no,1,now(),now() FROM product_rows WHERE sku=$sku ON CONFLICT (account_id,sheet,row_no) DO UPDATE SET sold_count=product_sold.sold_count+1, last_sold_at=now()`. Trả số dòng đã +1 (để log/telemetry). Đặt cạnh `MarkSoldAsync` (`ProductDb.AllData.cs:90`) hoặc file `ProductDb.Sku.cs`.
2. `server/Shopee.Hub.Web/Api/ProductApiEndpoints.cs` — thêm endpoint POST `"/products/mark-sold-by-sku"` nhận body `{ skus: string[] }` (hoặc 1 sku) → gọi `ProductDb.MarkSoldBySkuAsync`. Theo mẫu endpoint `/products/mark-sold` (`ProductApiEndpoints.cs:162-168`).
3. `suite/Shopee.Core/Coordination/HubRoutes.cs` — thêm `ProductsMarkSoldBySku = "/products/mark-sold-by-sku"` (cạnh `:69`).
4. `suite/Shopee.Core/Coordination/HubClient.cs` — thêm `Task<bool> MarkProductsSoldBySkuAsync(IReadOnlyList<string> skus, CancellationToken ct=default)` POST route trên (mẫu `MarkProductsSoldAsync` `:208`). Có thể thêm DTO request trong `ProductDtos.cs`.

### B. MODULE ĐƠN — cột cờ + phát hiện chuyển-sang-đã-giao (no backfill)

5. `orders/XuLyDonShopee.Core/Data/Database.cs` — thêm cột `sold_counted_at TEXT` (NULL = chưa xử lý đếm) vào bảng `orders` qua `EnsureColumn` (mẫu `hub_synced_at`, `:161`). (Không cần cột status cũ riêng — dùng chính cột `status` hiện có trong DB làm "trạng thái lần sync trước".)
6. `orders/XuLyDonShopee.Core/Services/ShopeeShippingNav.cs` — thêm predicate thuần + test: `LaDaGiaoDaBan(string? status)` = true nếu status (trim, lower) **đúng bằng** một trong {"đã giao", "hoàn thành", "giao hàng thành công"}. KHÔNG dùng Contains (để "đã giao cho đơn vị vận chuyển" và "giao hàng không thành công" KHÔNG khớp). Thêm test trong `orders/XuLyDonShopee.Tests` (mẫu `OrderStatusPillConverterTests`), phủ các chuỗi biên: "Đã giao cho ĐVVC", "Đã giao cho đơn vị vận chuyển", "Giao hàng không thành công".
7. `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs` — cơ chế phát hiện + hàng đợi đếm:
   - **Phát hiện chuyển trạng thái:** so trạng thái CŨ trong DB (trước upsert) vs MỚI (từ scan). Cách gọn: thêm method `IReadOnlyList<string> CollectNewlyDeliveredSkus(IEnumerable<SyncedOrder> scanned)` chạy TRƯỚC/CÙNG `UpsertMany`:
     - Với mỗi đơn scan có `LaDaGiaoDaBan(new.Status)` == true:
       - Nếu đơn **đã tồn tại** trong DB, `sold_counted_at IS NULL`, và **status CŨ trong DB KHÔNG delivered** → đây là **chuyển sang đã-giao** → gom `Sku` (nếu non-null) để +1, và set `sold_counted_at=now`.
       - Nếu đơn **mới toanh** (chưa có trong DB) và đã delivered ngay → **grandfather**: set `sold_counted_at=now`, KHÔNG +1.
       - Nếu đơn đã tồn tại, `sold_counted_at IS NULL`, status CŨ ĐÃ delivered (đơn cũ có sẵn từ trước tính năng) → **grandfather**: set `sold_counted_at=now`, KHÔNG +1.
     - Trả danh sách SKU cần +1.
   - Thêm method `MarkSoldCounted(accountId, orderSns)` set `sold_counted_at` (mẫu `MarkHubSynced` `:273-295`, `COALESCE` chống ghi đè).
   - LƯU Ý thứ tự: đọc trạng thái cũ phải TRƯỚC khi `UpsertMany` ghi đè cột `status`. Có thể query cũ trong cùng transaction, hoặc gộp logic vào `UpsertMany` (trả thêm danh sách SKU newly-delivered). Chọn cách rõ ràng, có thể test.
8. `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs` — **giới hạn 10 trang:** đổi `MaxSyncPages` (`:3389`) từ 20 → **10** (giữ nguyên đọc tab "Tất cả"). Ghi comment lý do.

### C. WIRE HOOK — gọi hub +1 nền sau sync

9. `orders/XuLyDonShopee.App/Services/AppServices.cs` — thêm hook `Func<IReadOnlyList<string>, Task<bool>>? IncrementSoldBySku` (cạnh `PushOrdersToHub` `:39`).
10. `orders/XuLyDonShopee.App/Services/AccountSession.cs` — trong `SyncOrdersAsync` (cạnh `StartHubPushInBackground` `:699`): sau `UpsertMany`, lấy danh sách SKU newly-delivered (bước 7), nếu có → `StartSoldCountInBackground(...)` (mẫu `StartHubPushInBackground` `:820,842`, chống chồng + best-effort) gọi `_services.IncrementSoldBySku(skus)`; thành công thì `MarkSoldCounted(...)`. Thứ tự đúng: chỉ set cờ `sold_counted_at` khi hub +1 OK (kẻo mất đếm nếu hub lỗi). Log rõ "+N Đã bán theo SKU: ...".
11. `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` — rót hook `IncrementSoldBySku` bằng `CoordinationRuntime.Client.MarkProductsSoldBySkuAsync` (mẫu `WireHubPush` `:57-93`).

### D. Build + test + deploy

12. Build: `dotnet build ShopeeSuite.sln` + build hub `dotnet build server/Shopee.Hub.Web`. `dotnet test orders/XuLyDonShopee.Tests` (đặc biệt test predicate mới). TẤT CẢ phải xanh.
13. (Fable làm sau nghiệm thu) Deploy hub (route mới) + release client — xem mục Deploy.

## 4. Tiêu chí nghiệm thu

- [ ] Build `ShopeeSuite.sln` + `server/Shopee.Hub.Web` thành công; `dotnet test orders/XuLyDonShopee.Tests` xanh.
- [ ] Predicate `LaDaGiaoDaBan`: đúng cho {"Đã giao","Hoàn thành","Giao hàng thành công"} (mọi hoa/thường/space thừa); **sai** cho "Đã giao cho ĐVVC", "Đã giao cho đơn vị vận chuyển", "Giao hàng không thành công", "Đã hủy" — có test.
- [ ] Route hub `/products/mark-sold-by-sku`: +1 `sold_count` cho MỌI dòng có `sku` khớp TUYỆT ĐỐI; SKU không tồn tại → +0 (không lỗi). (Đối chiếu SQL.)
- [ ] Logic no-backfill (đối chiếu code): chỉ +1 khi đơn **chuyển từ chưa-giao → đã-giao**; đơn mới-đã-giao-sẵn và đơn-cũ-đã-giao-sẵn → grandfather (set cờ, KHÔNG +1).
- [ ] Idempotent: `sold_counted_at` chỉ set sau khi hub +1 OK; đơn đã đếm không +1 lần nữa dù sync đọc lại.
- [ ] `MaxSyncPages` = 10 (đọc tab "Tất cả", tối đa 10 trang).
- [ ] Hook rót từ `OrdersModuleHost`; module đơn KHÔNG tham chiếu trực tiếp `Shopee.Core`.

## 5. Rủi ro & lưu ý

- **Thứ tự đọc-status-cũ TRƯỚC upsert:** dễ sai nhất — nếu `UpsertMany` ghi đè `status` trước khi so sánh thì mất tín hiệu chuyển trạng thái → hoặc không đếm được, hoặc đếm nhầm. Phải chốt rõ trong 1 transaction.
- **Cờ set sau khi hub +1 OK:** nếu set `sold_counted_at` trước rồi hub lỗi → mất đếm vĩnh viễn. Ngược lại nếu +1 rồi crash trước khi set cờ → lần sau đếm lại (over-count). Cân nhắc: chấp nhận rủi ro nhỏ over-count khi crash giữa chừng (hiếm), ưu tiên KHÔNG mất đếm → +1 xong mới set cờ; hoặc gộp atomic nếu khả thi. Ghi rõ lựa chọn.
- **Khớp SKU toàn bộ (tạm):** nếu 2 shop trùng SKU → +1 cả hai (over-count cross-shop). Đã thống nhất chấp nhận tạm; per-shop làm sau. Route thiết kế để sau này thêm tham số (acct,sheet) scope dễ.
- **Giảm cap 20→10 trang đổi hành vi sync chung:** đơn ở trang >10 (cũ) sẽ không còn được cập nhật trạng thái (vẫn nằm trong DB từ trước). Chấp nhận theo yêu cầu ("tạm thế đã").
- **+1 mỗi đơn, không theo số lượng:** đơn nhiều SP/nhiều SKU chỉ +1 cho SKU đại diện (`SyncedOrder.Sku`). Nêu rõ, không tự mở rộng.
- Không đụng Scrape/Search/gsheet/notify.

## Deploy (Fable làm sau nghiệm thu)

- Có đổi HUB (route mới) → **deploy hub** (`dotnet publish server/Shopee.Hub.Web -c Release -p:PublishProfile=linux-x64` → scp dll → install + restart → health). Deploy hub TRƯỚC.
- Có đổi CLIENT → cân nhắc **release client** (bump version 1.4.x + CHANGELOG + release-suite.cmd) — hỏi/chốt với user khi tới bước đó.

---

## Báo cáo thực thi

Hoàn tất, Fable đã review diff thật + tự build/test: `dotnet build server/Shopee.Hub.Web` **0/0**, `dotnet build ShopeeSuite.sln` **0/0**, `dotnet test orders/XuLyDonShopee.Tests` **806 pass** (32 test mới). 15 file (14 sửa + 1 test mới), đều trong phạm vi.

- **Hub:** `ProductDb.MarkSoldBySkuAsync` (`btrim(sku)=$1` khớp tuyệt đối mọi shop, +1 `sold_count`, giữ `first_sold_at`), endpoint `/products/mark-sold-by-sku`, HubRoutes + HubClient.MarkProductsSoldBySkuAsync + DTO.
- **Client:** cột `sold_counted_at` (CREATE + EnsureColumn); predicate `LaDaGiaoDaBan` (khớp tuyệt đối, loại ĐVVC); `DetectNewlyDelivered` (đọc status cũ trước upsert, chỉ +1 khi chuyển chưa-giao→đã-giao, grandfather đơn đã-giao-sẵn) + `MarkSoldCounted`; `MaxSyncPages` 20→10; hook `IncrementSoldBySku` (AppServices) rót ở OrdersModuleHost, gọi nền trong AccountSession (cờ set SAU hub +1 OK).
- **Fable xác minh (đọc diff):** ShopeeLoginService.cs chỉ đổi MaxSyncPages (không đè bản login 34367fd); DetectNewlyDelivered/predicate/SQL đúng thiết kế.

### ⚠️ Hạn chế đã biết (executor nêu thẳng, cần user quyết có sửa không)

**Mất đếm khi hub OFFLINE đúng lượt đơn lần đầu chuyển sang đã-giao:** lượt đó `UpsertMany` đã ghi `status="Đã giao"` nhưng +1 hụt (hub tắt) → cờ chưa set. Lượt sync SAU, `DetectNewlyDelivered` thấy status CŨ = "Đã giao" (đã delivered) + cờ NULL → rơi vào **grandfather → KHÔNG +1** → đơn đó **mất đếm vĩnh viễn** (không retry). Là hệ quả của thiết kế no-backfill hiện tại. Muốn retry đúng cần tách trạng thái "đang chờ +1" khỏi việc ghi đè `status` — vd cột `sold_pending` (plan phụ). Hiện GIỮ đúng thiết kế đã chốt.
