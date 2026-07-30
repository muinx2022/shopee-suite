# Plan: Sửa bug mới phía hub + luồng thống kê đơn dùng chung (đợt B2)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
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

(chưa)
