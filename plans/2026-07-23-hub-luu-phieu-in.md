# Plan: Hub lưu file phiếu in (client đẩy PDF lên, xem/tải trên trang Đơn hàng của hub)

- **Ngày:** 2026-07-23
- **Trạng thái:** đã thực thi (chờ nghiệm thu)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Hub hiện chỉ nhận DỮ LIỆU đơn (POST `/api/orders/push` → bảng `orders` trong `hub.db`); file phiếu PDF chỉ đi lên Google Drive qua GSheet. Người dùng muốn **hub cũng lưu file phiếu** — để xem/tải phiếu ngay trên trang `/orders` của hub web (và máy khác lấy được).

Hiện trạng liên quan (đã khảo sát):
- Server: `HubDatabase(dataDir)` SQLite; bảng `orders` UNIQUE `(shop_id, order_sn)` (`HubDatabase.Orders.cs`); file tĩnh có 2 pattern — `GET /api/files/{*name}` (FilesDir) và `/exports/{name}` map trong `Program.cs` (~`:205`) cho web UI tải.
- Client: `HubClient.PushOrdersAsync` dùng `_bulkHttp` (timeout dài — bài học workbook 2,5MB qua tunnel); hook `AppServices.PushOrdersToHub` rót từ `OrdersModuleHost.WireHubPush`; module đơn hàng có cờ `hub_synced_at` + `GetForHubPush`/`MarkHubSynced`.
- Vòng đời đơn (commit `8fbe023`): đơn kết thúc bị DỌN khỏi client sau khi GSheet + sold-count + hub-đơn xong (`NenXoaDonKetThuc`).

## 2. Phạm vi

- **Làm:** endpoint hub nhận/PHỤC VỤ file phiếu + lưu đĩa; client đẩy phiếu nền sau sync; cột "Phiếu" trên trang `/orders` hub; ràng vòng đời (không xóa đơn khi phiếu local chưa kịp lên hub).
- **Không làm:**
  - KHÔNG đổi hợp đồng `orders/push` hiện có (phiếu đi endpoint RIÊNG).
  - KHÔNG đụng GSheet.
  - KHÔNG làm chiều tải phiếu từ hub VỀ client (chưa cần; hub là kho xem/tải qua web).
  - KHÔNG deploy trong plan này — Fable deploy VM sau nghiệm thu (hub TRƯỚC, client sau; client gặp hub cũ 404 → hook trả false → thử lại lượt sau, an toàn).

## 3. Các bước thực hiện

### Server — `server/Shopee.Hub.Web`

1. **Schema:** `HubDatabase.Orders.cs` — thêm cột `slip_at TEXT` (thời điểm hub nhận phiếu; NULL = chưa có) theo pattern EnsureColumn/migration sẵn có của HubDatabase; `OrderRecord` thêm field tương ứng (vd `SlipAt`); `QueryOrders`/`ReadOrderRow` SELECT thêm cột.
2. **Lưu file:** thư mục `<DataDir>/slips/<shopId>/<SanitizeFileName(orderSn)>.pdf` (sanitize tên file — tái dùng/viết helper chống path traversal: chỉ `[A-Za-z0-9_-]`, từ chối rỗng).
3. **Endpoint nhận phiếu:** `POST /api/orders/slip` (thêm `HubRoutes.OrdersSlip` phía Core client — bước 6):
   - Body: `{ shopUsername, slips: [{ orderSn, fileBase64 }] }` (lô ≤ 5 — client tự chia).
   - Với từng slip: đơn PHẢI đã tồn tại trên hub (`shop_id`+`order_sn`) — chưa có → báo per-item `missing` (client thử lại lượt sau, sau khi orders/push xong); base64 decode + kiểm magic `%PDF-` + trần 5MB — sai → per-item lỗi; hợp lệ → ghi file (đè nếu có — bản mới thắng) + set `slip_at`.
   - Response: `{ saved, missing: [orderSn...], errors: [{orderSn, error}...] }`. Ghi 1 dòng AppendLog như orders/push.
4. **Endpoint phục vụ phiếu cho web:** map `GET /slips/{shopId:long}/{orderSn}` trong `Program.cs` **theo ĐÚNG pattern `/exports/{name}`** (cùng cơ chế auth/cookie admin — khảo sát chỗ `:205` và làm y hệt): sanitize orderSn → `PhysicalFile(..., "application/pdf")`, thiếu file → 404.
5. **UI `/orders` (`Components/Pages/Orders.razor`):** thêm cột **"Phiếu"**: đơn có `slip_at` → link "📄 Phiếu" (`href="/slips/{shopId}/{orderSn}"`, `target="_blank"`); chưa có → ô trống. Theo chuẩn UI hub sẵn có (pattern `.linkbtn`, `title` attr, responsive mobile như đợt review 13/07). Nếu trang có chế độ ẩn cột mobile thì xếp cột Phiếu vào nhóm phụ.

### Client — `suite/Shopee.Core` + glue suite

6. `HubRoutes.cs`: `public const string OrdersSlip = "/api/orders/slip";`. `OrderDtos.cs`: DTO request/result khớp bước 3 (`OrdersSlipPushRequest`, `SlipPushItem`, `OrdersSlipPushResult`).
7. `HubClient.cs`: `Task<OrdersSlipPushResult?> PushOrderSlipsAsync(OrdersSlipPushRequest req, CancellationToken ct)` — qua `_bulkHttp` (mẫu `PushOrdersAsync`).
8. `OrdersModuleHost.cs`: rót hook mới `services.PushOrderSlipsToHub` (mẫu `WireHubPush`): hub chưa kết nối → false; map + gọi `PushOrderSlipsAsync`; trả về danh sách orderSn ĐÃ saved (để client mark đúng đơn — đơn `missing`/lỗi KHÔNG mark). Chữ ký gợi ý: `Func<long accountId, IReadOnlyList<(string OrderSn, string FileBase64)>, CancellationToken, Task<IReadOnlyList<string>?>>` (null = hub lỗi cả lô).

### Client — module `orders/`

9. **DB:** `Database.cs` migration cột `hub_slip_synced_at TEXT` (mẫu `hub_synced_at`, kèm test migration theo `DatabaseMigrationTests` sẵn có).
10. **Repo (`OrdersRepository.cs`):**
    - `GetForHubSlipPush(accountId)`: đơn `hub_synced_at IS NOT NULL AND hub_slip_synced_at IS NULL AND tracking_number` khác rỗng — trả `(OrderSn, TrackingNumber)` (đơn đã lên hub, chưa đẩy phiếu; việc CÓ FILE hay không App kiểm sau).
    - `MarkHubSlipSynced(accountId, orderSns, at)`: mẫu `MarkHubSynced` (COALESCE giữ mốc đầu).
    - `GetForGsheetPush`: SELECT thêm `hub_slip_synced_at` → `GsheetPendingOrder` thêm cờ `bool DaDayPhieuHub`.
11. **AccountSession:** hook `PushOrderSlipsToHub` trên `AppServices` (nullable, mẫu `PushOrdersToHub`); `StartHubSlipPushInBackground` gọi SAU `StartHubPushInBackground` trong `SyncOrdersAsync` (y pattern nền/nuốt lỗi/OCE xuyên):
    - Lấy `GetForHubSlipPush` → với từng đơn, đọc file `<invoiceDir>/<SanitizeFileName(sn)>.pdf` qua kiểm magic sẵn có (`TryReadSlipBase64`) — file thiếu/hỏng → bỏ qua im lặng (khi nào file có, vd tính năng tải-lại-phiếu, lượt sau tự đẩy).
    - Chia lô ≤ 5, gọi hook; danh sách saved trả về → `MarkHubSlipSynced` đúng các đơn đó; hook null/false-cả-lô → thôi, lượt sau thử lại. Log 1 dòng "Hub phiếu: đã đẩy x file." khi x > 0.
12. **Vòng đời:** `NenXoaDonKetThuc` thêm tham số `bool coPhieuLocalChuaDayHub` (caller tính: hub bật + file phiếu local hợp lệ tồn tại + `!p.DaDayPhieuHub`) — true → GIỮ (chưa xóa; đợi phiếu lên hub xong). File local KHÔNG tồn tại → không giữ vì phiếu (như cũ). Cập nhật `AccountSessionCleanupTests` cho tham số mới + case mới.

### Test + build

13. Test client: migration cột mới; `GetForHubSlipPush`/`MarkHubSlipSynced`; `NenXoaDonKetThuc` case phiếu-chưa-đẩy-hub. Server nếu có test project thì thêm cho sanitize + endpoint logic thuần (không thì thôi — theo hiện trạng repo).
14. `dotnet build ShopeeSuite.sln` 0 lỗi (cả `server/Shopee.Hub.Web` nếu ngoài sln thì build riêng); `dotnet test orders/XuLyDonShopee.Tests` toàn bộ xanh.

## 4. Tiêu chí nghiệm thu

- [ ] Build (suite + hub web) 0 lỗi; toàn bộ test xanh.
- [ ] (Đối chiếu code) `POST /api/orders/slip`: kiểm magic %PDF- + trần 5MB + sanitize tên; đơn chưa có trên hub → `missing`, client KHÔNG mark; file ghi `<DataDir>/slips/<shopId>/<orderSn>.pdf` + `slip_at` set.
- [ ] `GET /slips/{shopId}/{orderSn}` phục vụ PDF đúng pattern auth của `/exports`; không path traversal.
- [ ] Trang `/orders` có cột Phiếu, link mở PDF tab mới khi đơn có phiếu.
- [ ] Client: sau sync, phiếu của đơn đã-lên-hub được đẩy nền theo lô ≤5; mark `hub_slip_synced_at` CHỈ các đơn hub báo saved; hub offline/404 (hub chưa deploy) → im lặng thử lại lượt sau.
- [ ] Đơn kết thúc CÓ phiếu local chưa đẩy hub (hub bật) → CHƯA bị dọn; đẩy xong lượt sau mới dọn.
- [ ] `orders/push` cũ không đổi hợp đồng.

## 5. Rủi ro & lưu ý

- **Thứ tự deploy:** hub TRƯỚC client (Fable làm sau nghiệm thu). Client mới + hub cũ: POST slip 404 → hook trả null → không mark, thử lại — phải đúng nhánh này, không được ném/spam log mỗi giây (chỉ log 1 dòng/lượt sync).
- **Kích thước qua tunnel:** PDF ~100–300KB, lô ≤5 → ~1,5MB/POST qua `_bulkHttp` (timeout 5') — ổn; KHÔNG dùng http control-plane timeout 8s (bài học workbook cũ).
- **Đơn bị dọn trước khi có tính năng này** thì phiếu không còn đường lên hub (đã xóa) — chấp nhận, chỉ áp dụng từ nay.
- Máy khác không có file phiếu local sẽ không đẩy gì cho đơn của máy khác — mỗi máy chỉ đẩy phiếu nó có trên đĩa (`hub_slip_synced_at` là DB cục bộ từng máy; hub đè file "bản mới thắng" nên 2 máy cùng đẩy cũng vô hại).

---

## Báo cáo thực thi

**Ngày thực thi:** 2026-07-23 · **Người thực thi:** Opus (`opus-executor`)

### Đã hoàn thành (theo từng bước plan)

**Server — `server/Shopee.Hub.Web`:**

1. **Schema `slip_at`** (`Data/HubDatabase.Orders.cs`): thêm cột `slip_at TEXT` vào `EnsureOrdersSchema` (CREATE TABLE cho DB mới); `OrderRecord.SlipAt` (`DateTimeOffset?`); `QueryOrders` SELECT thêm `slip_at` (index 20); `ReadOrderRow` đọc index 20. Migration cho DB CŨ ở `Data/HubDatabase.cs` `MigrateSchema` — `AddColumnIfMissing("orders", "slip_at", "TEXT")`.
2. **Lưu file + helper** (`Data/HubDatabase.Orders.cs`): thêm `OrderExists(shopId, orderSn)`, `SetOrderSlipAt(shopId, orderSn, at)` (đè, trả số dòng), và `public static string? SanitizeSlipFileName(orderSn)` — CHỈ giữ `[A-Za-z0-9_-]` (ASCII), rỗng → null (chống traversal, dùng CHUNG cho POST và GET). File lưu tại `<DataDir>/slips/<shopId>/<sanitize(orderSn)>.pdf`.
3. **POST `/api/orders/slip`** (`Api/ClientApiEndpoints.cs`): body `OrdersSlipPushRequest`; resolve shop qua `GetOrCreateShopByUsername` (như push); mỗi slip: sanitize → `OrderExists` sai → `missing`; base64 hỏng / `>5MB` (`MaxSlipBytes`) / không `%PDF-` (`LooksPdf`) → `errors`; hợp lệ → ghi file + `SetOrderSlipAt` → `saved++`. Ghi 1 dòng `AppendLog` như push. Trả `OrdersSlipPushResult(saved, missing, errors)`.
4. **GET `/slips/{shopId:long}/{orderSn}`** (`Program.cs`, ngay sau `/exports`): `RequireAuthorization("Web")` (cookie admin, y pattern /exports); sanitize orderSn → `Results.File(path, "application/pdf")`; thiếu → 404; sanitize null → 400.
5. **Cột "Phiếu"** (`Components/Pages/Orders.razor`): thêm `<th class="m-hide">Phiếu</th>` + ô có link `<a class="linkbtn" href="/slips/{ShopId}/{OrderSn}" target="_blank">📄 Phiếu</a>` khi `SlipAt is not null`, ô trống khi chưa có; `colspan` 10 → 11.

**Client — `suite/Shopee.Core` + glue suite:**

6. `Coordination/HubRoutes.cs`: `OrdersSlip = "/api/orders/slip"`. `Coordination/OrderDtos.cs`: `SlipPushItem`, `OrdersSlipPushRequest`, `SlipPushError`, `OrdersSlipPushResult` (dùng CHUNG server + client).
7. `Coordination/HubClient.cs`: `PushOrderSlipsAsync(req, ct)` qua `_bulkHttp` (mẫu `PushOrdersAsync`).
8. `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`: `WireHubSlipPush` rót `services.PushOrderSlipsToHub` (mẫu `WireHubPush`); trả danh sách `order_sn` ĐÃ LƯU = lô − `Missing` − `Errors`; hub lỗi cả lô → null.

**Client — module `orders/`:**

9. `Core/Data/Database.cs`: cột `hub_slip_synced_at TEXT` (CREATE TABLE mới + `EnsureColumn` cho DB cũ, mẫu `hub_synced_at`).
10. `Core/Data/OrdersRepository.cs`: `GetForHubSlipPush(accountId)` (`hub_synced_at IS NOT NULL AND hub_slip_synced_at IS NULL AND tracking_number` khác rỗng → `(OrderSn, TrackingNumber)`); `MarkHubSlipSynced(accountId, sns, at)` (COALESCE giữ mốc đầu); `GetForGsheetPush` SELECT thêm `hub_slip_synced_at`; `GsheetPendingOrder` thêm `bool DaDayPhieuHub`.
11. `App/Services/AppServices.cs`: hook `PushOrderSlipsToHub` (nullable). `App/Services/AccountSession.cs`: `StartHubSlipPushInBackground` + `PushSlipsToHubAsync` (đọc file qua `TryReadSlipBase64`, chia lô ≤5 = `HubSlipPushBatchSize`, mark theo saved trả về, log "Hub phiếu: đã đẩy x file." khi x>0), gọi NGAY SAU `StartHubPushInBackground` trong `SyncOrdersAsync`.
12. **Vòng đời** (`AccountSession.cs`): `NenXoaDonKetThuc` thêm tham số `bool coPhieuLocalChuaDayHub` (true → GIỮ); caller trong `PushOrdersToGsheetAsync` tính `= hubHookActive && !p.DaDayPhieuHub && SlipFileIsValidPdf(<slipDir>/<sanitize(sn)>.pdf)`.

**Test:**

13. `AccountSessionCleanupTests` (cập nhật `Make` + 4 tham số + 3 case mới: `CoPhieuLocalChuaDayHub_Giu`, `HubBat_DaDayHub_CoPhieuLocalChuaDayPhieu_Giu`, `HubBat_DaDayHub_DaDayPhieu_Xoa`); `DatabaseMigrationTests` (+1 case migration `hub_slip_synced_at`); `OrdersRepositoryTests` (+4 case: `GetForHubSlipPush`, `MarkHubSlipSynced` x2, `GetForGsheetPush_MapDaDayPhieuHub`). Không có test project cho server → theo plan, bỏ qua test server.

### Kết quả kiểm chứng

- `dotnet build ShopeeSuite.sln` → **Build succeeded, 0 Warning, 0 Error**.
- `dotnet build server/Shopee.Hub.Web` (ngoài sln) → **Build succeeded, 0 Warning, 0 Error**.
- `dotnet test orders/XuLyDonShopee.Tests` → **Passed! Failed: 0, Passed: 896, Skipped: 0** (888 cũ + 8 mới).

### Quyết định tự chọn (trong khuôn khổ plan)

- `OrdersSlipPushResult.Saved` để dạng **số đếm** (như plan viết `saved` không ngoặc), client suy ra tập đã lưu = lô − `Missing` − `Errors` (deterministic vì server phân loại mọi item). `SlipPushError` mã lỗi tiếng-Việt-không-dấu ngắn (`base64-loi`, `khong-phai-pdf`, `file-qua-lon`, `ten-don-khong-hop-le`, `ghi-file-loi`).
- Cột "Phiếu" xếp vào **nhóm phụ `m-hide`** (ẩn trên mobile) theo gợi ý plan.
- Thứ tự kiểm trong endpoint: sanitize → `OrderExists` (missing) → decode/size/pdf (errors) → ghi file — ưu tiên `missing` như plan liệt kê.
- Tên file server = `SanitizeSlipFileName(order_sn)` (ASCII `[A-Za-z0-9_-]`); order_sn Shopee là chữ-số nên khớp tên client lưu; POST và GET dùng chung hàm nên luôn ánh xạ đúng.

### Vướng mắc / bỏ dở

Không có. Toàn bộ hạng mục plan đã triển khai, build + test xanh. Chưa commit, chưa deploy (theo yêu cầu — Fable nghiệm thu, commit, deploy sau; thứ tự deploy: hub TRƯỚC, client SAU).
