# Plan: số đơn "chuẩn bị hàng" chung toàn hệ thống (Hub đếm từ bảng đơn)

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Tab **Shopee → Tài khoản → chi tiết → "Kết quả"** hiện số đơn đã "Chuẩn bị hàng" theo shop/ngày. Số này lấy từ
bảng `prepare_daily` **nằm trong máy**: mỗi lần chính máy đó arrange xong một đơn thì +1. Hệ quả người dùng gặp:
máy A chạy trước chuẩn bị 2 đơn của shop X → máy A hiện 2; máy B (Hoàng) chạy sau, Shopee không còn đơn nào ở
"Chờ lấy hàng" nên máy B hiện 0. Không máy nào sai — con số đơn giản là chưa bao giờ rời khỏi máy.

**Người dùng đã CHỐT hai điều (không được tự đổi):**

1. **Hub đếm từ bảng đơn**, KHÔNG cộng bộ đếm rời của từng máy. Lý do: hub đã có bảng `orders` khóa
   `(shop_id, order_sn)` — mỗi đơn đúng MỘT dòng, nên đếm ra là con số thật, không thể cộng trùng dù bao nhiêu
   máy cùng chạy, và máy cài lại cũng không mất số.
2. **Mất Hub thì hiện số của máy + ghi chú** — không để lưới trống. Phải cho người dùng biết đang xem số cục bộ
   chứ không phải số toàn hệ thống.

### Hiện trạng code liên quan (đã khảo sát, dùng làm mốc)

**Client — chỗ đếm:**
- `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs` dòng ~751: `_onOrderPrepared?.Invoke(shopLogin)`
  gọi mỗi khi arrange xong 1 đơn. **Ngay dòng dưới đã có `prep.OrderCode` = MÃ ĐƠN** (chính là `order_sn`; nó
  đang được dùng làm khóa cho `capturedTracking` và tên file phiếu).
- `orders/XuLyDonShopee.App/Services/AccountSession.cs` ~807: `onOrderPrepared: shopLogin => …
  IncrementPrepared(...) + RaisePrepareCountChanged(...)`.
- `orders/XuLyDonShopee.Core/Data/ResultsRepository.cs`: `prepare_daily` + `GetPreparedByDay(accountId, day)`.
- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: `LoadResults()` dựng `ResultRows` từ
  `Results.GetShops` LEFT JOIN `Results.GetPreparedByDay`; `ShopPrepareRow` có `PreparedCount`, `IsChecking`,
  `DaKiemTra`, `ShowTick`.

**Seam hub của module (module KHÔNG tham chiếu `Shopee.Core`):**
- `orders/XuLyDonShopee.App/Services/AppServices.cs`: các hook `Func<…>` — đã có `PushOrdersToHub` (dòng 60),
  `QueryHubOrders` (100), `ListHubShops` (108).
- `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`: rót hook. `WireHubPush` (~74) NHÓM đơn theo shop và
  đẩy với `OrdersPushRequest.ShopUsername = o.ShopLogin` (fallback username subaccount).
  ⇒ **Shop trên hub khóa theo ĐÚNG `shop_login` mà client dùng** — khớp thẳng với `account_shops.shop_login`,
  không cần dịch tên.

**Hub:**
- `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs`: `EnsureOrdersSchema()` (bảng `orders`), `UpsertOrders`
  (`ON CONFLICT(shop_id,order_sn) DO UPDATE`, vừa sửa dùng `COALESCE` cho `final_amount`/`tracking_number`).
- `server/Shopee.Hub.Web/Endpoints/ClientApiEndpoints.cs`: các route client (`/orders-config`, `/api/orders`,
  `/api/shops`, `/api/orders/push`…). `suite/Shopee.Core/Coordination/HubRoutes.cs`: hằng đường dẫn.

## 2. Phạm vi

- **Làm:** đánh dấu đơn "đã chuẩn bị hàng lúc nào" ở client → đẩy lên hub theo đường đẩy đơn ĐANG CHẠY → hub có
  route đếm theo shop/ngày → tab "Kết quả" hiện số từ hub, mất hub thì hiện số máy + ghi chú.
- **Không làm:**
  - KHÔNG bỏ `prepare_daily` — giữ nguyên làm nguồn dự phòng khi mất hub.
  - KHÔNG backfill đơn đã chuẩn bị TRƯỚC bản này (không có dữ liệu ngày để suy) — số quá khứ trên hub sẽ là 0,
    chấp nhận, KHÔNG bịa.
  - KHÔNG đụng khóa/lease chống hai máy tranh nhau — việc riêng, plan khác.
  - KHÔNG đổi cột "Shop", thứ tự shop, tick tiến độ vừa làm.
  - KHÔNG thêm bộ đếm rời gửi lên hub (người dùng đã loại phương án này).

## 3. Các bước thực hiện

### Bước 1 — Client Core: callback báo kèm MÃ ĐƠN

`OrdersBridgeSession`: đổi `_onOrderPrepared` từ `Action<string>` thành `Action<string, string>` — tham số
`(shopLogin, orderSn)`, truyền `prep.OrderCode`. Giữ nguyên vị trí gọi (TRƯỚC `TrySaveSlip`) và tính null-safe.
Cập nhật comment mô tả callback ở đầu lớp + doc `<param>`.

### Bước 2 — Client DB: cột `prepared_at`

- `Database.cs`: `EnsureColumn(conn, "orders", "prepared_at", "TEXT");` — thời điểm arrange xong đơn (ISO UTC,
  dùng `DbSerialization.FormatDate` như các cột thời gian khác). NULL = chưa/không biết.
- `OrdersRepository`: thêm
  `public void MarkPrepared(long accountId, string orderSn, DateTime atUtc)` —
  `UPDATE orders SET prepared_at = COALESCE(prepared_at, $at), hub_synced_at = NULL WHERE account_id=$a AND order_sn=$sn`.
  - `COALESCE(prepared_at, $at)`: chỉ ghi LẦN ĐẦU — arrange lại/chạy lại không dời ngày sang hôm khác.
  - `hub_synced_at = NULL`: mở cờ để lượt đẩy hub kế mang `prepared_at` lên (xem
    `plans/2026-07-26-dong-bo-trang-thai-ket-thuc-hub-gsheet.md` — hub chỉ lấy đơn `hub_synced_at IS NULL`).
- `SyncedOrder` (model của module) thêm `PreparedAt` (DateTime?); `GetForHubPush` SELECT thêm cột và map vào.

### Bước 3 — Client App: gọi `MarkPrepared`

`AccountSession` (~807): callback nhận thêm `orderSn` → **giữ nguyên** `IncrementPrepared` +
`RaisePrepareCountChanged` (dự phòng offline) và **gọi thêm** `_services.Orders.MarkPrepared(_accountId, orderSn,
DateTime.UtcNow)`. `orderSn` rỗng → bỏ qua phần MarkPrepared (vẫn +1 đếm cục bộ như cũ).

### Bước 4 — Giao thức: mang `prepared_at` lên hub

- `suite/Shopee.Core/Coordination/HubOrderDtos.cs` → `OrderPushItem`: thêm 2 property
  - `PreparedAt` (string? ISO UTC),
  - `PreparedDay` (string? `yyyy-MM-dd` theo GIỜ ĐỊA PHƯƠNG của máy chuẩn bị đơn).
  Ghi doc: `PreparedDay` là KHÓA NHÓM để đếm theo ngày — client tính sẵn nên hub KHÔNG cần biết múi giờ
  (mọi máy ở VN nhưng vẫn không hard-code offset ở hub).
- `OrdersModuleHost.ToPushItem`: map từ `SyncedOrder.PreparedAt` — `PreparedAt` = ISO UTC, `PreparedDay` =
  `PreparedAt.Value.ToLocalTime().ToString("yyyy-MM-dd")`. NULL → cả hai null.
- Hub `HubDatabase.Orders.cs`:
  - `EnsureOrdersSchema` thêm `prepared_at TEXT, prepared_day TEXT` + `CREATE INDEX IF NOT EXISTS
    ix_orders_prepared_day ON orders(prepared_day);`.
  - **Migration DB CŨ:** bảng `orders` đã tồn tại trên hub thật nên `CREATE TABLE IF NOT EXISTS` KHÔNG thêm cột —
    phải `ALTER TABLE … ADD COLUMN` có kiểm cột tồn tại (theo cách hub đang làm ở chỗ khác; nếu hub chưa có helper
    thì viết một helper nhỏ dùng `pragma table_info`).
  - `UpsertOrders` `DO UPDATE SET`: `prepared_at=COALESCE($pa,prepared_at), prepared_day=COALESCE($pd,prepared_day)`
    — đơn đẩy lại không kèm thì GIỮ, và máy khác đẩy lại KHÔNG ghi đè ngày của máy đã chuẩn bị.

### Bước 5 — Hub: route đếm

- `HubRoutes`: hằng mới `PrepareStats = "/prepare-stats"`.
- `ClientApiEndpoints`: `GET /prepare-stats?day=yyyy-MM-dd` — **cùng cơ chế xác thực với các route client sẵn có**
  (bám đúng `/orders-config`). Trả JSON list `{ shopUsername, count }`:

```sql
SELECT s.username, COUNT(*) FROM orders o JOIN shops s ON s.id = o.shop_id
WHERE o.prepared_day = $day GROUP BY s.username
```

  (Tên cột của bảng `shops` phải ĐỌC CODE HUB để dùng cho đúng — không đoán. Nếu shop định danh bằng cột khác
  `username` thì dùng đúng cột mà `OrdersPushRequest.ShopUsername` được lưu vào.)
  `day` thiếu/sai định dạng → 400. Không có dòng nào → list rỗng (KHÔNG 404).
- `HubClient` (`suite/Shopee.Core/Coordination/`): thêm `GetPrepareStatsAsync(string day, CancellationToken)`
  trả `IReadOnlyList<PrepareStatItem>?` (null = không lấy được). Timeout/backoff bám đúng cách các API client
  khác đang làm.

### Bước 6 — Client: hook + hiển thị

- `AppServices`: hook mới
  `public Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>?>>? QueryPrepareStats { get; set; }`
  (tham số = `day`; trả map `shop_login → count`; **null = không lấy được từ hub**). Doc theo văn phong các hook sẵn có.
- `OrdersModuleHost`: rót hook — hub chưa kết nối / lỗi → trả `null` (KHÔNG trả map rỗng: rỗng nghĩa là "hub bảo 0",
  null nghĩa là "không hỏi được"). Nuốt lỗi + `Trace`, trừ `OperationCanceledException` chủ động.
- `AccountsViewModel`:
  - `LoadResults()` giữ nguyên đường local (dựng dòng + số từ `GetPreparedByDay`) → lưới hiện NGAY.
  - Thêm `RefreshHubCountsAsync()`: gọi `QueryPrepareStats(ngày đang lọc)`; có kết quả → gán
    `row.PreparedCount` theo map (shop không có trong map → **0**, vì hub là nguồn sự thật), đặt cờ
    `DangDungSoHub = true`; trả null → GIỮ số local, `DangDungSoHub = false`.
  - **Gọi `RefreshHubCountsAsync` ở đúng 4 mốc** (KHÔNG gọi theo từng đơn — sẽ spam hub):
    mở/chọn tài khoản khác · đổi ngày lọc · `ShopListChanged` · `ShopCheckChanged` với `checking == false`
    (xong một shop — đúng nhịp người dùng mong đợi).
  - Bảo đảm chạy nền + marshal về UI thread như các handler sẵn có; huỷ/chồng lượt gọi phải an toàn
    (lượt sau đè lượt trước, không để kết quả cũ ghi đè kết quả mới).
- `AccountsView.axaml`: cạnh ô lọc ngày, thêm một `TextBlock` nhỏ (`FontSize 11`, `TextMuted`):
  - `DangDungSoHub = true` → "Số toàn hệ thống (Hub)";
  - `false` → "Số của máy này — chưa gộp được từ Hub".
  Dùng 2 `TextBlock` `IsVisible` loại trừ nhau (tab này để `x:CompileBindings="False"`, tránh converter phức tạp).

### Bước 7 — Test

1. `MarkPrepared` ghi `prepared_at` lần đầu; gọi lại KHÔNG dời thời điểm; và **reset `hub_synced_at` về NULL**
   (kiểm qua `GetForHubPush` lấy được đơn).
2. `MarkPrepared` với mã đơn không tồn tại → không ném, không đổi dòng nào.
3. `GetForHubPush` trả `PreparedAt` đúng giá trị đã ghi.
4. VM: `QueryPrepareStats` trả map {shopA:5} ⇒ dòng shopA hiện 5, dòng shopB (không có trong map) hiện 0,
   `DangDungSoHub` = true.
5. VM: `QueryPrepareStats` trả null ⇒ số local GIỮ nguyên, `DangDungSoHub` = false.
6. VM: hook null (chưa rót — bản chạy không có hub) ⇒ hành vi y như cũ, không ném.

### Bước 8 — Build & test

- `dotnet build ShopeeSuite.sln` và `dotnet build server/ShopeeHub.sln` → 0 error, 0 warning.
- `dotnet test` → 100% xanh (mốc hiện tại **1024 test**).

## 4. Tiêu chí nghiệm thu

- [ ] Build cả 2 solution: 0 error, 0 warning. `dotnet test` xanh, số test > 1024.
- [ ] `MarkPrepared` dùng `COALESCE(prepared_at, $at)` và set `hub_synced_at = NULL`.
- [ ] `PreparedDay` do CLIENT tính theo giờ địa phương; hub KHÔNG hard-code múi giờ ở đâu cả.
- [ ] Hub `UpsertOrders` dùng `COALESCE` cho `prepared_at`/`prepared_day` (đẩy lại không xoá, máy khác không đè).
- [ ] Hub có migration `ALTER TABLE` cho DB cũ (đã kiểm bằng đọc code, ghi rõ trong báo cáo cách kiểm).
- [ ] Hook trả `null` khi mất hub (KHÔNG map rỗng); VM phân biệt đúng hai ca (test 4 & 5).
- [ ] `RefreshHubCountsAsync` KHÔNG được gọi trong `OnPrepareCountChanged` (chống spam hub).

## 5. Rủi ro & lưu ý

- **null vs rỗng** là điểm dễ sai nhất: map rỗng = "hub bảo shop này 0 đơn"; null = "không hỏi được hub" → giữ
  số local. Nhầm hai cái này sẽ làm lưới về 0 mỗi khi rớt mạng.
- `prep.OrderCode` ĐÃ được dùng làm khóa `capturedTracking` (map sang `order_sn`) nên chắc chắn là mã đơn — nhưng
  vẫn phải kiểm lại khi sửa, đừng để lệch khóa.
- Đơn chuẩn bị TRƯỚC bản này có `prepared_at` NULL → không vào số hub. Đây là hành vi ĐÚNG theo phạm vi; nếu thấy
  số hôm nay tụt so với số cục bộ thì là do đơn cũ, không phải lỗi.
- Nhớ nguyên tắc chung: **bên rỗng không đè bên có** (xem plan trạng thái kết thúc ngày 26/07).
- Sau khi merge phải **deploy hub TRƯỚC** rồi mới release client (client mới gửi field hub cũ không hiểu thì
  field rơi mất) — Fable làm, agent KHÔNG deploy, KHÔNG commit, KHÔNG bump version.

---

## Báo cáo thực thi (Opus điền sau khi xong)
