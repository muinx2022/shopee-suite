# Plan: Sửa bug mới phía hub + luồng thống kê đơn dùng chung (đợt B2)

- **Ngày:** 2026-07-30
- **Trạng thái:** đã code xong — chờ phiên chính nghiệm thu
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Review đa-agent 30/07 (có xác minh đối kháng) tìm ra loạt bug trong luồng "thống kê đơn dùng chung" (nhiều máy chạy app Đơn hàng đẩy đơn lên Hub qua `POST /api/orders/push`; Hub SQLite là nguồn số chung; client đọc `GET /api/orders/prepare-stats` + `/api/orders/stats`) và vài bug/dead-code phía hub web. Plan này sửa khu **server/ + suite/Shopee.Core (DTO coordination) + suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs + orders/…/ViewModels** (màn thống kê client). Plan B1 chạy song song sửa `orders/**/Services` + extension — **KHÔNG sửa** `orders/XuLyDonShopee.App/Services/**`, `orders/XuLyDonShopee.Core/Services/**` (trừ nơi nêu rõ dưới đây), `extensions/**` ở plan này.

## 2. Phạm vi

- **Làm:** 5 nhóm dưới.
- **Không làm:** không xoá `/api/shops` + `/api/orders` (user chốt 29/07 giữ làm API admin); không deploy VM/release client (Fable lo sau); không commit; không đổi hợp đồng orders/push ngoài việc THÊM field optional.

## 3. Các bước thực hiện

### Nhóm A — Race "cờ đã đẩy" phía client (bug dữ liệu nặng nhất)

**A1. `MarkHubSynced` COALESCE nuốt cờ reset đặt trong lúc push đang bay** — `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs:668` (đúng lớp bug "cờ đã đẩy kẹt" đã sửa v1.6.3).
Hiện trạng: luồng đẩy chụp snapshot `GetForHubPush` (WHERE `hub_synced_at IS NULL`) → POST lô 200 đơn (tunnel, có thể hàng chục giây) → `MarkHubSynced` set `hub_synced_at = COALESCE(hub_synced_at, $at)`. Nếu TRONG lúc lô bay có chỗ reset `hub_synced_at=NULL` cho đơn trong lô — `UpsertMany` khi status/cancel_reason đổi (:191-195), `MarkPrepared` (:438-441), `SetReturnRequestCodes` (:531-535) — thì COALESCE(NULL,$at) niêm phong luôn, dữ liệu mới KHÔNG BAO GIỜ lên hub. Xảy ra chắc chắn theo flow mỗi shop (sync xong bắn push nền, arrange chạy ngay sau MarkPrepared chính các đơn đó). Hệ quả: prepare-stats thiếu; đơn hủy kẹt "Chờ lấy hàng" trên hub vĩnh viễn (client sau đó XÓA đơn local vì tưởng đã đẩy → hết đường tự sửa); mã trả bị nuốt → hub không notify.
Sửa theo hướng cột thế hệ:
- Thêm cột `hub_push_gen INTEGER NOT NULL DEFAULT 0` (migration theo mẫu migration hiện có của app.db).
- MỌI chỗ reset `hub_synced_at=NULL` (3 chỗ trên + chỗ khác nếu grep thấy) đồng thời `hub_push_gen = hub_push_gen + 1`.
- `GetForHubPush` trả kèm gen từng đơn; `MarkHubSynced` đổi thành `UPDATE … SET hub_synced_at=$at WHERE … AND hub_synced_at IS NULL AND hub_push_gen=$gen` (theo từng đơn trong lô).
- Đơn bị đổi giữa chừng giữ NULL → lượt outbox 2' sau tự đẩy lại. Viết test tái dựng race (reset giữa snapshot và mark → đơn vẫn phải được đẩy lại).

### Nhóm B — Shop nhân đôi + mất số (hub + client)

**B1. Chuẩn hoá khoá shop trên hub** — `server/Shopee.Hub.Web/Data/HubDatabase.Shops.cs:140` (`GetOrCreateShopByUsername`) + `HubDatabase.Orders.cs:275` (`GetSharedOrderStatistics` filter).
Hiện trạng: `WHERE username=$u` + unique index `ux_shops_username` đều case-sensitive → "ShopA"/"shopa" thành 2 dòng shop; khoá chống trùng đơn là (shop_id, order_sn) → CÙNG đơn đếm 2 lần trong stats; filter theo shop trả 0 im lặng. Đường sinh trùng thật: fallback ResolveShopUsername = email subaccount rồi sau đổi sang shop_login thật; 2 máy gõ email lệch hoa/thường.
Sửa:
- Migration (theo mẫu migration hiện có của hub.db, idempotent): gộp các dòng `shops` trùng username sau khi bỏ hoa/thường — chuyển `orders` (và mọi bảng FK theo shop_id: slips trên đĩa keyed theo shopId? kiểm tra `slips/<shopId>/`) về shop_id của dòng CŨ NHẤT; nếu cùng order_sn tồn tại ở cả 2 shop_id thì giữ bản có synced_at mới nhất; xoá dòng shop thừa; dựng lại unique index thành `username COLLATE NOCASE` (giữ nguyên chữ hoa/thường đang lưu để hiển thị).
- `GetOrCreateShopByUsername`: `WHERE username=$u COLLATE NOCASE` (+ Trim).
- Filter stats `AND s.username = $shop COLLATE NOCASE`; rà các chỗ so username khác trong `server/` cùng kiểu.

**B2. Map client "bản sau thắng" → MẤT số** — `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs:753-764` (`ChuanHoaKhoaShop`) + `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs:394-401` (`WirePrepareStatsRead`).
Sửa cả 2 chỗ sang **cộng dồn**: `map.TryGetValue(k, out var cur); map[k] = cur + s.Count;` (2 dòng hub trùng khoá sau bỏ hoa/thường là cùng 1 shop vật lý). Vẫn giữ dù B1 đã dedup — phòng hub chưa migrate.

### Nhóm C — Thời gian

**C1. Notify hub dùng `DateTime.Now` trên VM UTC → lệch 7 tiếng** — `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs` dòng ~434 (`FireNotifyDonMoi`), ~451 (`FireNotifyDonTra`), ~466 (`FireNotifyLoiApp`).
Sửa: mốc giờ đưa vào tin nhắn quy đổi `Asia/Ho_Chi_Minh` (TimeZoneInfo.ConvertTime từ DateTimeOffset.UtcNow; .NET 8 hỗ trợ IANA ID cả Windows/Linux). Đặt helper 1 chỗ (vd trong `OrderNotifyService`) dùng cho cả 3.

**C2. `first_seen_at` = giờ hub NHẬN push → đơn rơi sai ngày khi gửi bù/qua nửa đêm** — `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs:104,161` + `suite/Shopee.Core/Coordination/OrderDtos.cs` (`OrderPushItem`).
Sửa: thêm `OrderPushItem.CreatedAt` (string ISO UTC, nullable — optional để client cũ vẫn hợp lệ); phía client, chỗ build push item (grep nơi tạo `OrderPushItem` trong `orders/` / `suite/Shopee.Suite/Infrastructure/`) điền từ `orders.created_at`; hub INSERT dùng `COALESCE($createdAt, $sa)` cho `first_seen_at`. Client cũ không gửi → hành vi như cũ. Ghi CHANGELOG: **deploy hub TRƯỚC, release client SAU**.

**C3. Màn `/orders` hub hiển thị `LocalDateTime` của server UTC** — `server/…/Components/Pages/Orders.razor:82` (khối mobile mới) + `:97` (cột Sync desktop, wart sẵn có cùng kiểu).
Sửa cả 2 theo tiền lệ trong hub (xem `FleetStateService.Ago` kiểu relative-time, hoặc format theo `Asia/Ho_Chi_Minh` + helper C1) — chọn một cách, dùng thống nhất.

### Nhóm D — Hiệu năng + notify hub

**D1. 3 hàm đọc stats còn giữ `lock(_gate)` toàn cục** — `HubDatabase.Orders.cs`: `PrepareStatsByDay` (~376-391, bị MỌI client gọi 4 mốc + sau mỗi shop), `ShopOrderSummaries` (~401-421, DispatchOrdersTab gọi mỗi ≤10s), `DistinctOrderStatuses` (~426-435).
Sửa: chuyển cả 3 sang `using var conn = OpenReadConnection();` đúng mẫu `GetSharedOrderStatistics` sau commit 6893481 (WAL đọc song song ghi). Đừng quên bỏ `lock` tương ứng.

**D2. Notify "đơn trả" spam khi adoption/restore** — `HubDatabase.Orders.cs:201` (`UpsertOrders` phát hiện mã trả mới): đơn `exists=false` (INSERT lần đầu) mang sẵn ReturnRequestCode cũng vào `ReturnCodeChangedItems` → hub mới dựng/shop mới vào là bắn loạt tin cũ.
Sửa: chỉ coi là "mã trả mới" khi `exists && rrc đổi`. Ghi chú hạn chế (đơn đã có mã trước push đầu tiên sẽ không notify từ hub — client lo, xem B1 plan kia).

**D3. Nhận app-alert `Kind="don_tra"`** — `ClientApiEndpoints.cs` endpoint `/api/orders/app-alert` (~277-285).
Hiện trạng: mọi Kind đều đi `FireNotifyLoiApp` (kênh lỗi app). Plan B1 (song song) cho client gửi `Kind="don_tra"`, `ShopName`=shopLogin, `Detail`="SN1=CODE1; SN2=CODE2" khi có mã trả của đơn đã dọn khỏi app.
Sửa: nhánh `r.Kind == "don_tra"` → gửi qua kênh webhook **đơn trả** (dùng đường/format của `FireNotifyDonTra`/`TaoTinNhanDonTra`, nội dung từ ShopName + Detail; mốc giờ theo helper C1); các Kind khác giữ nguyên FireNotifyLoiApp. Cập nhật doc-comment `OrdersAppAlertRequest` (OrderDtos.cs:96-98) liệt kê Kind mới.

### Nhóm E — Client màn Thống kê + dọn hub

**E1. Màn Thống kê client báo "Hub không phản hồi" khi lượt hỏi còn đang bay + số nhảy local↔chung** — `orders/XuLyDonShopee.App/ViewModels/OrderStatisticsViewModel.cs` (~dòng 33, 141).
Sửa: tách 3 trạng thái nguồn: "đang hỏi Hub…" (đặt khi bắn LoadSharedStatisticsAsync) / "số chung (Hub)" / "Hub không phản hồi" (CHỈ khi lượt hỏi trả null). Khi đã có số chung mà `OrdersChanged` bắn → giữ số chung đang hiện, chỉ thay khi kết quả hub mới về (bỏ bước vẽ local đè khi nguồn hiện tại là hub còn tươi).

**E2. `ActivityLog` không được Dispose khi thoát** — `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` (`StopAsync`): gọi `services.Log.Dispose()` (flush nốt _pending) như món nợ plan `2026-07-30-log-don-hang-het-do.md` đã ghi.

**E3. Xoá 2 endpoint legacy đã đủ bằng chứng** — `ClientApiEndpoints.cs:125-137`: `/accounts/append` + `/accounts/remove` (journalctl VM 10→30/07 KHÔNG có `legacy endpoint hit` nào). Xoá cả `LogLegacyHit` (:415, hết caller), DTO `AccountRemoveRequest` (:533), `FileStoreConfigService.AppendShopeeAccounts` nếu hết caller, const trong `suite/Shopee.Core/Coordination/HubRoutes.cs` + method HubClient tương ứng nếu có.

**E4. Dọn `QueryOrders`/`CountOrders`** — `HubDatabase.Orders.cs:213,220`: chỉ còn test gọi. Xoá `CountOrders`; `QueryOrders` xoá hoặc thu private; test `HubSharedOrderStatsTests.cs:38` chuyển sang `QueryOrdersPage(...)`.

**E5. Vặt:** (a) doc-comment "Map OrderRecord → HubOrderItem…" đang kẹt trên `AsUtc` (ClientApiEndpoints.cs:365-370) → dời về ngay trên `ToHubOrderItem`; (b) Import wizard: `MaintenanceService.cs:47` nâng cutoff file tạm 1h → 24h VÀ `ImportExcelWizard.razor` kiểm tra `File.Exists(_wizTempPath)` trước khi đọc, thiếu thì báo "file tạm đã hết hạn, chọn lại file" thay vì exception.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/ShopeeHub.sln` + `dotnet build ShopeeSuite.sln` 0 lỗi 0 warning; `dotnet test server/Shopee.Hub.Web.Tests` + `dotnet test orders/XuLyDonShopee.Tests` xanh không tụt baseline (30 + 1449, trừ test xoá chủ đích ở E4 — ghi rõ).
- [ ] Test mới: (1) race A1 (reset giữa snapshot và MarkHubSynced → đơn vẫn được đẩy lại); (2) migration B1 gộp shop trùng + đơn không đếm 2 lần (GetSharedOrderStatistics trên DB dựng sẵn 2 shop "ShopA"/"shopa" cùng order_sn); (3) UpsertOrders insert-lần-đầu-có-mã-trả KHÔNG vào ReturnCodeChangedItems; (4) first_seen_at lấy CreatedAt client khi có, fallback giờ nhận.
- [ ] Migration idempotent: chạy 2 lần trên cùng DB không lỗi, không đổi thêm gì.
- [ ] Grep sau dọn: `LogLegacyHit|AccountRemoveRequest|CountOrders` = 0 hit source (trừ plans/).
- [ ] Báo cáo từng mục A1→E5: đã làm gì, file+dòng, test nào cover.

## 5. Rủi ro & lưu ý

- Migration hub.db chạy trên dữ liệu production VM → phải theo đúng mẫu migration hiện có (xem cách HubDatabase mở schema/migrate), idempotent, không phá dữ liệu; slips trên đĩa keyed theo shopId — nếu gộp shop thì di chuyển/hợp nhất thư mục slips tương ứng.
- `OrderPushItem` là hợp đồng client↔hub: field mới phải OPTIONAL (client cũ không gửi vẫn hợp lệ). Deploy hub trước, client sau.
- KHÔNG sửa `orders/**/Services/**` hay `extensions/**` (khu của plan B1 đang chạy song song) — ngoại lệ duy nhất: `OrdersRepository.cs` (Data) + `OrderStatisticsViewModel.cs`/`AccountsViewModel.cs` (ViewModels) thuộc plan này.
- Mọi đường dẫn quy về thư mục làm việc của agent (worktree) — TUYỆT ĐỐI không đọc/ghi file của cây làm việc chính.
- KHÔNG commit; báo cáo xong để Fable nghiệm thu + commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Kết quả build/test cuối** (chạy trong worktree `agent-ab2177e3520eb6999`, đã fast-forward lên `main` 38dea24
vì worktree được tạo từ commit cũ hơn 19 nhịp nên chưa có file plan này):

| Lệnh | Kết quả |
|---|---|
| `dotnet build server/ShopeeHub.sln --no-incremental` | 0 lỗi, 0 warning |
| `dotnet build ShopeeSuite.sln --no-incremental` | 0 lỗi, 0 warning |
| `dotnet test server/Shopee.Hub.Web.Tests` | 44 pass / 0 fail (baseline 30 → **+14**) |
| `dotnet test orders/XuLyDonShopee.Tests` | 1462 pass / 0 fail (baseline 1449 → **+13**) |

Không xoá test nào (E4: test `HubSharedOrderStatsTests` chuyển sang `QueryOrdersPage`, không bỏ).

### A1 — Race "cờ đã đẩy"

Cặp cột **thế hệ** thay vì một cột `hub_push_gen` như plan mô tả (lý do ở mục "Điểm lệch" bên dưới):

- `orders/XuLyDonShopee.Core/Data/Database.cs:119-120` (CREATE TABLE) + `:280-292` (`EnsureColumn` cho DB cũ):
  thêm `hub_push_gen INTEGER NOT NULL DEFAULT 0` + `hub_push_gen_sent INTEGER`. `:323` — `BackfillHubFinalAmountOnce`
  cũng +1 gen (là một chỗ reset cờ).
- `OrdersRepository.cs` — mọi chỗ reset `hub_synced_at=NULL` đều +1 `hub_push_gen`: `:198-202` (`UpsertMany`,
  cùng đúng 4 điều kiện với nhánh reset), `:447-448` (`MarkPrepared`), `:542-543` (`SetReturnRequestCodes`).
- `OrdersRepository.cs:576-640` (`GetForHubPush`): CHỤP thế hệ (`hub_push_gen_sent = hub_push_gen`) trong CÙNG
  transaction với lượt đọc → hàm này GIỜ CÓ GHI (trước là đọc thuần). Thêm cột `created_at` vào SELECT (cho C2).
- `OrdersRepository.cs:699-720` (`MarkHubSynced`): `SET hub_synced_at=$at WHERE … AND hub_synced_at IS NULL AND
  (hub_push_gen_sent IS NULL OR hub_push_gen_sent = hub_push_gen)`. Bỏ `COALESCE` nhưng GIỮ hành vi "gọi 2 lần
  không dời mốc đầu" nhờ `hub_synced_at IS NULL`.
- `orders/XuLyDonShopee.Core/Models/SyncedOrder.cs:78-83`: thêm `CreatedAt` (theo đúng mẫu `PreparedAt`/
  `ReturnRequestCode`/`ShopLogin` — field repo cấp lại khi đọc hàng đợi, không phải cột quét DOM).

**Test:** `orders/XuLyDonShopee.Tests/HubPushGenRaceTests.cs` (7 test) — reset xen giữa snapshot↔mark cho cả 3
đường (`MarkPrepared` / đổi trạng thái / ghi mã trả) → đơn CÒN trong hàng đợi; đường bình thường vẫn đóng cờ;
đơn khác trong cùng lô không vạ lây; lượt đẩy kế tiếp chụp lại thế hệ mới nên không kẹt vĩnh viễn.
`DatabaseMigrationTests.cs:449-479` — migration trên DB cũ + **chạy 2 lần không đổi gì**.

### B1 — Chuẩn hoá khoá shop trên hub

- `server/Shopee.Hub.Web/Data/HubDatabase.Shops.cs:40-142`: `MergeDuplicateShopsOnce(dataDir)` — nhóm theo
  `LOWER(TRIM(username))`, giữ hàng **id nhỏ nhất**, đơn trùng `order_sn` ở cả 2 shop thì giữ bản `synced_at`
  mới nhất, chuyển đơn còn lại sang, xoá hàng shop thừa, **dời thư mục `slips/<shopId>`** (`MoveSlipDir`, nuốt
  lỗi I/O để không chặn khởi động hub), rồi `DROP INDEX` + dựng lại `ux_shops_username ON shops(username COLLATE
  NOCASE)`. Idempotent 2 lớp: khoá `settings['merge_shops_nocase_v1']` + bản thân phép gộp lần 2 không còn nhóm trùng.
- `HubDatabase.cs:73-75`: gọi sau `EnsureSchema()`/`MigrateSchema()`.
- `HubDatabase.Shops.cs:237-267` (`GetOrCreateShopByUsername`): `WHERE username=$u COLLATE NOCASE` + Trim (Trim
  áp cả cho giá trị INSERT).
- `HubDatabase.Orders.cs:282-285` (`GetSharedOrderStatistics`): `AND s.username = $shop COLLATE NOCASE`.
- Đã rà `server/**`: KHÔNG còn chỗ so username kiểu khác (`ListShopGroupsBySubAccount` vốn đã dùng
  `StringComparer.OrdinalIgnoreCase`; `PrepareStatsByDay` `GROUP BY s.username` giờ an toàn vì index NOCASE
  không cho 2 hàng lệch hoa/thường tồn tại).

**Test:** `server/Shopee.Hub.Web.Tests/HubShopMergeAndPushTests.cs` test 1-4 — dựng hub.db kiểu bản CŨ (index
phân biệt hoa/thường, 2 hàng `ShopA`/`shopa`, `SN1` nằm ở CẢ HAI) → sau migration còn 1 shop, `GetSharedOrderStatistics`
trả **2 đơn thay vì 3**, bản giữ lại là bản `synced_at` mới nhất, phiếu đã dời sang `slips/1/`; lọc theo shop
không phân biệt hoa/thường; chạy lần 2 không đổi gì; sau migration push lệch hoa/thường về cùng một `shop_id`.

### B2 — Map client cộng dồn

- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs:753-772` (`ChuanHoaKhoaShop`)
- `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs:391-407` (`WirePrepareStatsRead`)

Cả hai đổi sang `TryGetValue` + cộng. **Test:** `PrepareHubCountTests.HubTraHaiDongCungShop_CongDon_KhongLayBanSauThang`
(hub trả 5 + 3 cho cùng shop → lưới hiện 8, không phải 3).

### C1 — Mốc giờ notify

- **File mới** `server/Shopee.Hub.Web/Services/GioVietNam.cs`: `Doi()` / `BayGio()` / `DinhDang()` qua
  `TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")`, thiếu dữ liệu múi giờ → lùi về UTC+7 cố định
  (VN không có DST nên offset cố định vẫn ĐÚNG — cần vì hub bật `InvariantGlobalization`).
- `ClientApiEndpoints.cs`: cả 3 chỗ `DateTime.Now` → `GioVietNam.BayGio()` (`FireNotifyDonMoi` :423,
  `FireNotifyDonTra` :440, `FireNotifyLoiApp` :494).

**Test:** `AppAlertDonTraTests.GioVietNam_*` (22:30 UTC → "31/07 05:30", offset +7, mốc rỗng → chuỗi rỗng).

### C2 — `first_seen_at` theo mốc client

- `suite/Shopee.Core/Coordination/OrderDtos.cs:41-46`: `OrderPushItem.CreatedAt` (string? — **TÙY CHỌN**, client
  cũ không gửi vẫn hợp lệ).
- `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs:1063-1064` (`ToPushItem`): điền từ `SyncedOrder.CreatedAt`.
- `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs:163` — `first_seen_at` = `COALESCE($fsa,$sa)`; `:197` bind
  qua `NormalizeIso` (`:218-229`) — **bắt buộc chuẩn hoá**: client gửi `"o"` của DateTime UTC là hậu tố `Z`, hub
  ghi `+00:00`; lọc theo khoảng ở đây so CHUỖI nên trộn 2 dạng là lệch ở đúng biên (`'+' < 'Z'`).

**Test:** `HubShopMergeAndPushTests` test 6-7 (có `CreatedAt` → `first_seen_at` đúng mốc đó + chuỗi kết thúc
`+00:00` + rơi đúng khoảng ngày của mốc; không có → bằng `synced_at`; đẩy lại KHÔNG dời mốc) và
`HubPushGenRaceTests.GetForHubPush_TraCreatedAt_DeHubDatFirstSeen` phía client.

### C3 — Giờ hiển thị trang /orders

`server/Shopee.Hub.Web/Components/Pages/Orders.razor:82` (khối mobile) + `:97` (cột Sync desktop) → dùng
`GioVietNam.DinhDang(o.SyncedAt, "MM-dd HH:mm")` (chọn cách "format theo Asia/Ho_Chi_Minh + helper C1", dùng
thống nhất cả 2 chỗ; mốc rỗng → chuỗi rỗng như trước).

### D1 — 3 hàm đọc stats bỏ `lock(_gate)`

`HubDatabase.Orders.cs`: `PrepareStatsByDay` (:383), `ShopOrderSummaries` (:413), `DistinctOrderStatuses` (:440)
→ `using var conn = OpenReadConnection();`, bỏ `lock` tương ứng (mẫu `GetSharedOrderStatistics` sau 6893481).

### D2 — Notify "đơn trả" hết spam

`HubDatabase.Orders.cs:200-207`: thêm điều kiện `exists &&`. Hạn chế (đơn có mã TRƯỚC push đầu tiên thì hub
không notify) đã ghi thành comment ngay tại chỗ. **Test:** `HubShopMergeAndPushTests` test 5.

### D3 — app-alert `Kind="don_tra"` sang kênh đơn trả

- `ClientApiEndpoints.cs:261-278`: rẽ nhánh theo Kind (so `OrdinalIgnoreCase`), Kind khác giữ `FireNotifyLoiApp`.
- `:430-464`: `FireNotifyDonTraTuAppAlert` (dùng đúng key webhook `NotifyWebhookDonTra` + `TaoTinNhanDonTra` +
  mốc giờ C1) và `TachCapDonTra` — hàm THUẦN tách `"SN=CODE; SN=CODE"`, `internal` để test.
- `OrderDtos.cs:96-108`: doc-comment `OrdersAppAlertRequest` liệt kê 3 Kind + ý nghĩa `Detail` từng loại.

**Test:** `AppAlertDonTraTests` (tách nhiều cặp / bỏ phần rác không ném / null-rỗng → list rỗng).

### E1 — Màn Thống kê client

`orders/XuLyDonShopee.App/ViewModels/OrderStatisticsViewModel.cs`:
- `:33-42` thêm `SourceDangHoiText` ("đang hỏi Hub…") + `SourceSharedStaleText`; `ApplyLocal` (:154) đặt
  "đang hỏi" thay vì "Hub không phản hồi" — dòng "không phản hồi" chỉ đặt khi lượt hỏi THỰC SỰ trả null.
- `:51-60` nhớ `_dangHienSoHub` + `_shopSoHub` + `_rangeSoHub`; `ApplyStatistics` (:139-146) BỎ bước vẽ local
  đè khi lượt vẽ mới CÙNG shop + CÙNG khoảng ngày với số chung đang hiện (đây là "số nhảy" mỗi lượt sync).
- `LoadSharedStatisticsAsync` (:229-…) luôn marshal về UI qua `ApDungKetQuaHub`: null → chỉ sửa dòng nguồn
  (đang giữ số chung → nói rõ là số của lượt hỏi TRƯỚC, không im lặng).

**Test:** `OrderStatisticsViewModelTests` +4 (đang hỏi không kết luận hub chết; vẽ lại cùng khoảng giữ số chung;
đổi khoảng ngày thì vẽ lại số local; lượt hỏi mới thất bại → nói rõ là số cũ).

### E2 — Dispose ActivityLog

`OrdersModuleHost.cs:1075` (`StopAsync`): `svc.Log.Dispose()` đặt SAU `Sessions.StopAllAsync()` (phiên đang dừng
vẫn còn ghi log).

### E3 — Xoá 2 endpoint legacy

`ClientApiEndpoints.cs`: xoá `/accounts/append` + `/accounts/remove`, `LogLegacyHit`, record `AccountRemoveRequest`;
cập nhật doc-comment đầu class. `FileStoreConfigService.cs`: xoá `AppendShopeeAccounts` (hết caller).
Grep nghiệm thu `LogLegacyHit|AccountRemoveRequest|CountOrders|AppendShopeeAccounts` = **0 hit** trong source.

### E4 — Dọn `QueryOrders`/`CountOrders`

`HubDatabase.Orders.cs`: xoá cả hai (`QueryOrdersCore`/`CountOrdersCore` private vẫn dùng qua `QueryOrdersPage`).
`HubSharedOrderStatsTests.cs:38` → `QueryOrdersPage(...).Items`. Sửa 2 doc-comment/comment còn nhắc tên cũ
(`HubDatabase.Orders.cs` ShopOrderSummaries, `Orders.razor:162`).

### E5 — Vặt

- (a) `ClientApiEndpoints.cs`: doc-comment "Map OrderRecord → HubOrderItem…" dời từ trên `AsUtc` về đúng
  `ToHubOrderItem` (:367-369).
- (b) `MaintenanceService.cs:47`: cutoff file tạm 1h → **24h**; `ImportExcelWizard.razor`: thêm
  `TempFileConHan()` (kiểm `File.Exists`, thiếu → `_wizError` "File tạm đã hết hạn hoặc bị dọn — hãy chọn lại
  file Excel.") gọi ở đầu `WizSelectSheet` và `Import`.

---

## Điểm lệch so với plan / cần phiên chính soi lại

1. **A1 dùng 2 cột thay vì 1** (`hub_push_gen` + `hub_push_gen_sent`), và `MarkHubSynced` GIỮ NGUYÊN chữ ký
   `(accountId, IEnumerable<string> orderSns, atUtc)` thay vì nhận `$gen` từng đơn như plan mô tả.
   **Lý do:** gen phải đi từ `GetForHubPush` → qua `AccountSession.PushPendingToHubAsync` (chỉ chuyển
   `IReadOnlyList<string>` sang callback `markSynced`) → `HubOutbox.cs`. Cả hai file nằm trong
   `orders/XuLyDonShopee.App/Services/**` — khu plan B1 đang chạy song song, plan này CẤM đụng. Cột
   `hub_push_gen_sent` để repo tự chuyển mốc so sánh qua DB, cho ra ĐÚNG ngữ nghĩa plan yêu cầu mà không sửa file
   nào ngoài khu được giao. Nếu phiên chính muốn đúng chữ ký như plan thì làm ở đợt sau, khi B1 đã merge.
2. **`GetForHubPush` giờ CÓ GHI** (mở write-transaction để chụp thế hệ). Trước là đọc thuần. `PushGate` đã chống
   2 lượt đẩy chồng nhau cùng account nên không đua với chính nó; nhưng đây là thay đổi tính chất của hàm — đáng soi.
3. **`SyncedOrder.cs` (Models) và `Database.cs` (Data) bị sửa** — hai file không nằm trong danh sách khu được
   giao nhưng bắt buộc: `CreatedAt` phải đi qua `SyncedOrder` (đúng mẫu `PreparedAt`/`ShopLogin` sẵn có) và
   migration app.db chỉ có chỗ đặt duy nhất là `Database.Initialize()`. Không đụng `Services/**`.
4. **Helper giờ VN đặt ở `server/Shopee.Hub.Web/Services/GioVietNam.cs`**, KHÔNG đặt trong `OrderNotifyService`
   như plan gợi ý ("vd") — `OrderNotifyService.cs` nằm trong `orders/XuLyDonShopee.Core/Services/**` (khu cấm).
   Đặt ở hub cũng phục vụ luôn C3 (Orders.razor).
5. **CHƯA ghi CHANGELOG** cho C2 ("deploy hub TRƯỚC, release client SAU"). CHANGELOG không có mục "chưa phát
   hành", thêm mục version mới bây giờ sẽ đụng bản của plan B1 và chốt sớm số version. Ghi chú deploy-order đã
   nằm trong doc-comment `OrderPushItem.CreatedAt`; đề nghị phiên chính viết CHANGELOG lúc gộp B1+B2 và release.
6. **`FileStoreConfigService.RemoveShopeeAccount` giờ cũng hết caller** (endpoint `/accounts/remove` đã xoá).
   Plan chỉ nêu đích danh `AppendShopeeAccounts` nên tôi giữ lại `RemoveShopeeAccount` — phiên chính quyết có xoá nốt không.
7. **Chưa chạy thật trên dữ liệu production**: migration gộp shop mới chỉ verify bằng DB dựng trong test.
   Trước khi deploy nên backup `hub.db` (`VacuumInto` đã có sẵn ở job backup 3h sáng) và soi số shop trước/sau.
8. **Worktree đã fast-forward từ 0d7918c lên main 38dea24** (19 commit) — worktree được tạo từ commit cũ nên
   không có file plan; tại thời điểm đó worktree sạch và 0 commit đi trước main nên fast-forward an toàn.
