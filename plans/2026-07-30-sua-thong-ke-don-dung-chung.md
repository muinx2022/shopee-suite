# Plan: Sửa 4 lỗi của thống kê đơn dùng chung từ Hub

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** opus-dev
- **Nền:** commit `7abe9a3` (bản thống kê dùng chung ban đầu, đã commit làm mốc quay lui)

## 1. Bối cảnh

Tính năng "thống kê đơn dùng chung từ Hub" vừa xong nhưng review phát hiện 4 lỗi, trong đó 3 lỗi chặn
phát hành. User đã chốt: **đếm đơn theo mốc tạo đơn**, và **sửa hết rồi mới phát hành**.

### Phát hiện nền tảng (đọc kỹ, ảnh hưởng cách sửa)

`SyncedOrder` (`orders/XuLyDonShopee.Core/Models/SyncedOrder.cs`) **KHÔNG có ngày đặt đơn thật của
Shopee** — app chưa bao giờ cào trường đó. Cái mà client gọi là "ngày tạo đơn" chính là
`orders.created_at` của **DB local** = *thời điểm đơn lần đầu được ghi vào máy đó* (đặt lúc INSERT, GIỮ
NGUYÊN khi UPDATE — xem `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs:84-92`).

⇒ Bản Hub tương đương đúng nghĩa là **"lần đầu đơn xuất hiện trên Hub"**: một mốc DUY NHẤT toàn hệ
thống, đặt lúc INSERT và không bao giờ đổi. **Không** được dùng `synced_at` (bị ghi đè mỗi lần đồng bộ
lại — `HubDatabase.Orders.cs:153`).

### 4 lỗi phải sửa

| # | Lỗi | Vị trí |
|---|---|---|
| 1 | **Chặn luồng UI** — `.GetAwaiter().GetResult()` gọi HTTP lên Hub; `_http.Timeout = 8s` (`HubClient.cs:25`); kích hoạt ở `OnFromDateChanged`/`OnToDateChanged`/đổi shop ⇒ đơ tới 8 giây mỗi lần chỉnh ngày | `OrderStatisticsViewModel.cs:190` |
| 2 | **Lệch múi giờ 7 tiếng** — `SpecifyKind(..., Unspecified).ToUniversalTime()` quy đổi theo giờ MÁY CHỦ; VM Hub là `Etc/UTC` (đã kiểm bằng `timedatectl`) trong khi client gửi ngày giờ VN | `HubDatabase.Orders.cs:232-233` |
| 3 | **Lọc sai trường** — Hub lọc `synced_at`, client lọc `created_at` ⇒ cùng khoảng ngày ra hai tập đơn khác nhau; đơn cũ đồng bộ lại bị đếm vào hôm nay | `HubDatabase.Orders.cs:247` |
| 4 | **Project test không biên dịch** ⇒ 1445 test chưa chạy lần nào | `OrdersViewModelTests.cs:133, 136` |

Kèm 2 điểm nên sửa: **hỏng im lặng** (Hub lỗi → lặng lẽ dùng số local, không dấu hiệu) và **chuỗi hiển
thị dựng trên server** (`lastSynced.ToLocalTime()` ở Hub = UTC ⇒ "đồng bộ lần cuối" hiện sai 7 tiếng).

## 2. Phạm vi

### Làm

Hub: `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs`, `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs`
Client: `suite/Shopee.Core/Coordination/HubClient.cs`, `suite/Shopee.Core/Coordination/HubOrderDtos.cs`,
`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`,
`orders/XuLyDonShopee.App/Services/AppServices.cs`,
`orders/XuLyDonShopee.App/ViewModels/OrderStatisticsViewModel.cs`,
`orders/XuLyDonShopee.Tests/OrdersViewModelTests.cs`
Giao diện (nếu cần thêm nhãn nguồn): `orders/XuLyDonShopee.App/Views/OrderStatisticsView.axaml`

### Không làm

- **Không** đi cào ngày đặt đơn thật từ Shopee — việc lớn riêng, không thuộc đợt này.
- Không đụng module khác, không đổi luồng đẩy đơn (`OrdersPush`), không bump version, **không commit**.
- Không đổi các trường/route hiện có ngoài phần thống kê.

## 3. Các bước

### Bước 1 — Hub: cột mốc "lần đầu thấy đơn" (`first_seen_at`)

`HubDatabase.Orders.cs`:

1. `EnsureOrdersSchema` (`:62`) — thêm `first_seen_at TEXT` vào `CREATE TABLE`, **và** một câu
   `ALTER TABLE orders ADD COLUMN first_seen_at TEXT` chạy được trên DB đã tồn tại (SQLite không có
   `IF NOT EXISTS` cho ADD COLUMN → bọc try/catch hoặc soi `PRAGMA table_info` trước; **xem các cột thêm
   sau trong file này đã làm kiểu gì rồi làm theo**, đừng phát minh cách mới).
2. **Backfill** một lần: `UPDATE orders SET first_seen_at = synced_at WHERE first_seen_at IS NULL`.
   Ghi rõ trong comment: với đơn cũ đã đồng bộ nhiều lần, giá trị này là lần đồng bộ GẦN NHẤT chứ không
   phải lần đầu — số liệu lịch sử TRƯỚC bản vá chỉ là xấp xỉ. Đây là hạn chế đã biết, chấp nhận.
3. `UpsertOrders` (`:82`) — INSERT thì đặt `first_seen_at = now`; nhánh `ON CONFLICT ... DO UPDATE`
   **TUYỆT ĐỐI KHÔNG** đụng `first_seen_at` (đây chính là lỗi #3, đừng lặp lại).
4. Thêm index `ix_orders_first_seen` trên `first_seen_at` (truy vấn thống kê lọc theo cột này).

### Bước 2 — Hub: nhận mốc UTC, trả SỐ THÔ, bỏ dựng chuỗi hiển thị

Nguồn gốc lỗi #2 là *server tự quy đổi ngày*. Sửa tận gốc: **client gửi thẳng mốc UTC**, server không
suy diễn múi giờ gì nữa.

`ClientApiEndpoints.cs` (`:260`):

- Đổi tham số từ `from`/`to` (chuỗi ngày) sang `fromUtc`/`toUtc` (chuỗi ISO-8601 "o").
- Parse bằng `DateTime.TryParse(..., CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out ...)`
  — **không** dùng `TryParse` một tham số (phụ thuộc culture server).
- Giữ `RequireAuthorization("Client")` như hiện tại (đã đúng, đừng bỏ).

`HubDatabase.GetSharedOrderStatistics`:

- Nhận thẳng `DateTime fromUtc, DateTime toUtcExclusive` — **xóa toàn bộ đoạn `SpecifyKind/ToUniversalTime`**.
- `WHERE o.first_seen_at >= $from AND o.first_seen_at < $to`.
- **Bỏ các trường chuỗi hiển thị** khỏi kết quả (`TrackingText`, `EstimateCoverageText`, `LastSyncedText`,
  `ScopeText`, `EmptyMessage`) — server không được dựng chuỗi tiếng Việt/định dạng ngày theo giờ máy chủ
  (chính chỗ `lastSynced.ToLocalTime()` đang sai 7 tiếng). Thay bằng **số thô**:
  `WithTracking`, `WithFinalAmount`, `ActiveOrders`, `LastSeenUtc` (nullable). Client tự định dạng.
- Dọn luôn chỗ tính doanh thu theo trạng thái: hiện mỗi nhóm quét lại cả `active` (O(nhóm×n)) — gom một
  lượt là đủ.

Cập nhật DTO tương ứng ở `HubOrderDtos.cs` và `AppServices.cs` (bản ghi `SharedOrderStatistics` phía
module) + `MapSharedStats` ở `OrdersModuleHost.cs`.

### Bước 3 — Client: gửi mốc UTC, KHÔNG chặn luồng UI

`HubClient.GetOrderStatisticsAsync` — đổi tham số sang `DateTime fromUtc, DateTime toUtcExclusive`, gửi
`fromUtc=...&toUtc=...` định dạng `"o"` (`CultureInfo.InvariantCulture`). Giữ nguyên cách nuốt lỗi trả
`null` (đúng ý "hub cũ/offline → fallback"), **nhưng** phân biệt: hết thời gian chờ / lỗi mạng vẫn trả
`null`, còn `OperationCanceledException` do người dùng huỷ thì ném tiếp như hiện tại.

`OrderStatisticsViewModel.ApplyStatistics` — đây là phần quan trọng nhất:

1. **Bỏ hẳn** `LoadSharedStatistics` kiểu chặn (`.GetAwaiter().GetResult()`).
2. Luồng mới: **vẽ số local NGAY** (đồng bộ, như trước khi có tính năng này) → rồi **gọi Hub bất đồng bộ**;
   có kết quả thì mới thay bằng số chung.
3. **Chống trả lời lạc nhịp:** giữ một số thứ tự (vd `_statsRequestId`), tăng mỗi lần `ApplyStatistics`;
   kết quả về mà số thứ tự không còn khớp thì **bỏ qua** — kẻo người dùng đổi ngày liên tục rồi kết quả cũ
   về sau đè lên kết quả mới.
4. Marshal kết quả về UI thread trước khi gán (bài học cũ: không giữ tham chiếu state qua `await`).
5. Chuyển `range.CreatedFromUtc` / `range.CreatedBeforeUtc` xuống hook (đã có sẵn trong `CreatedRange`) —
   **không** truyền `FromLocalDate` nữa.

### Bước 4 — Hết hỏng im lặng: nói rõ đang xem số nào

Thêm một thuộc tính (vd `SourceText`) và hiển thị dưới phần tiêu đề của tab Thống kê:

- Có số từ Hub → *"Số chung toàn hệ thống (từ Hub)."*
- Hub không phản hồi / hub cũ → *"Số trên MÁY NÀY — Hub không phản hồi nên chưa gộp được số chung."*

Chuỗi phải rõ để người dùng biết con số đang nhìn là của riêng máy hay toàn hệ thống. **Không** thêm hộp
thoại/popup — chỉ một dòng chữ.

### Bước 5 — Sửa project test cho biên dịch được

`OrdersViewModelTests.cs`:

- `:133` gán `services.Sessions` — thuộc tính **chỉ-đọc**. `:136` gọi `vm.RedownloadSlipCommand` —
  **không tồn tại** (code thật là hàm private `RedownloadSlipForRowAsync`, nối vào từng dòng qua
  `OrderRowViewModel`).
- Test này thuộc tính năng "tải phiếu", **không** liên quan thống kê. Xử lý: **đọc code thật rồi viết lại
  test cho khớp API đang có** (đi qua `OrderRowViewModel` như code thật), giữ đúng ý định ban đầu là kiểm
  thông báo khi tài khoản đang chờ đến lượt. Nếu API hiện tại **không cách nào** kiểm được ý đó mà không
  phải sửa code sản phẩm thì **xóa test** và ghi rõ lý do trong báo cáo — **không** sửa code sản phẩm chỉ
  để chiều test.
- Dọn `using XuLyDonShopee.Core.Services;` trùng (`:7` và `:10`, cảnh báo CS0105).

## 4. Kiểm chứng

### Build & test — cả hai phải xanh

```text
dotnet build ShopeeSuite.sln -c Debug
dotnet test  orders/XuLyDonShopee.Tests
```

Test phải **biên dịch được** và toàn bộ xanh (nền hiện tại 1445 test). Đây là tiêu chí cứng — lỗi #4 nghĩa
là trước đó không ai chạy được test.

### Test mới BẮT BUỘC viết (không có thì coi như chưa xong)

1. **`first_seen_at` không đổi khi đồng bộ lại:** upsert một đơn, ghi lại `first_seen_at`, upsert lại
   chính đơn đó với dữ liệu mới → `first_seen_at` **giữ nguyên**, `synced_at` đổi.
2. **Lọc theo khoảng UTC đúng biên:** đơn ở đúng mốc `from` phải được đếm, đơn ở đúng mốc `to` phải bị
   loại (biên `[from, to)`).
3. **Không còn phụ thuộc múi giờ máy chủ:** truyền mốc UTC vào và khẳng định kết quả không đổi — nếu viết
   được test đổi `TimeZoneInfo` thì tốt, không thì kiểm bằng cách truyền mốc UTC tường minh và đối chiếu
   số đơn.
4. **Số local và số Hub khớp nhau** trên cùng một bộ dữ liệu và cùng khoảng ngày (đây chính là lỗi #3 —
   test này là cái chốt chặn nó tái diễn).

### Kiểm bằng tay phần không chặn UI

Không cần chạy app thật nếu khó; **nhưng phải chỉ ra bằng code** rằng không còn lời gọi chặn nào
(`GetAwaiter().GetResult()` / `.Result` / `.Wait()`) trong đường thống kê — dán kết quả grep vào báo cáo.

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` xanh; `dotnet test` **biên dịch được** và xanh (báo số thật).
- [ ] 4 test mới ở mục trên đều có và xanh.
- [ ] Grep chứng minh đường thống kê không còn lời gọi chặn luồng.
- [ ] Hub không còn dòng nào tự quy đổi ngày theo giờ máy chủ, không còn dựng chuỗi hiển thị tiếng Việt.
- [ ] `first_seen_at` có migration chạy được trên DB CŨ đã có dữ liệu (không mất dữ liệu, không sập khi
      khởi động lại hub).
- [ ] Tab Thống kê nói rõ đang xem số chung hay số riêng máy.

## 6. Rủi ro & lưu ý

- **Hub đang chạy thật trên VM với dữ liệu thật.** Migration phải an toàn với DB đã có dữ liệu: thêm cột +
  backfill, KHÔNG tạo lại bảng, KHÔNG xóa cột. **Không tự deploy lên VM** — phiên chính lo việc đó.
- **Đổi tham số route là phá tương thích:** client cũ (bản đã phát hành) gọi `?from=&to=` sẽ nhận
  `BadRequest` → tab Thống kê của bản cũ tự fallback local. Chấp nhận được (fallback vốn là thiết kế),
  nhưng **ghi rõ vào báo cáo** để phiên chính deploy Hub và phát hành client cùng đợt.
- Không tự ý đổi ngữ nghĩa các cột khác của bảng `orders`.
- Nếu thấy plan sai so với code thật thì **báo lại rồi mới làm**.

---

## Báo cáo thực thi

<Để trống — người thực thi điền.>
