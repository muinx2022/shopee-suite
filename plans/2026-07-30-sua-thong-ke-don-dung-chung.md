# Plan: Sửa 4 lỗi của thống kê đơn dùng chung từ Hub

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
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

*(opus-dev, 2026-07-30 — chưa commit, chưa deploy, chưa bump version)*

### File đã sửa/tạo

| File | Thay đổi |
|---|---|
| `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs` | Thêm `first_seen_at TEXT` vào `CREATE TABLE`; `UpsertOrders` đặt `first_seen_at=$sa` **chỉ ở nhánh INSERT** (nhánh `DO UPDATE` không đụng); `GetSharedOrderStatistics` đổi chữ ký sang `(DateTime fromUtc, DateTime toUtcExclusive, string? shopLogin)`, bỏ hết `SpecifyKind(...,Unspecified).ToUniversalTime()`, lọc `first_seen_at >= $from AND < $to`, bỏ 5 trường chuỗi hiển thị → trả số thô (`ActiveOrders/WithTracking/WithFinalAmount/LastSyncedUtc`); thêm record nội bộ `StatOrderRow` (gói luật hủy + tiền của đơn) và gom doanh thu-theo-trạng-thái một lượt qua dictionary |
| `server/Shopee.Hub.Web/Data/HubDatabase.cs` | `MigrateSchema`: `AddColumnIfMissing("orders","first_seen_at","TEXT")` + backfill `UPDATE ... SET first_seen_at = synced_at WHERE first_seen_at IS NULL` (chỉ chạy khi VỪA thêm cột, đúng mẫu `ledger.machines_json`) + `CREATE INDEX ix_orders_first_seen` đặt SAU ALTER (đúng mẫu `prepared_day`) |
| `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs` | `GET /api/orders/stats` đổi tham số `from/to` → `fromUtc/toUtc`, parse `InvariantCulture + RoundtripKind`; thêm helper `AsUtc` chuẩn hoá Kind; giữ nguyên `RequireAuthorization("Client")` (ở cấp group, dòng 35) |
| `suite/Shopee.Core/Coordination/HubOrderDtos.cs` | `SharedOrderStatistics`: bỏ `ScopeText/EmptyMessage/TrackingText/EstimateCoverageText/LastSyncedText`, thêm `ActiveOrders/WithTracking/WithFinalAmount/LastSyncedUtc` |
| `suite/Shopee.Core/Coordination/HubClient.cs` | `GetOrderStatisticsAsync(DateTime fromUtc, DateTime toUtcExclusive, ...)`, gửi `?fromUtc=&toUtc=` định dạng `"o"` InvariantCulture; giữ nguyên quy ước nuốt lỗi → `null`, `OperationCanceledException` (huỷ chủ động) vẫn ném |
| `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` | Hook truyền thẳng 2 mốc UTC (bỏ `ToString("yyyy-MM-dd")`); `MapSharedStats` map theo DTO mới |
| `orders/XuLyDonShopee.App/Services/AppServices.cs` | Record `SharedOrderStatistics` khớp DTO mới; cập nhật doc hook `QueryOrderStatistics` (tham số giờ là mốc UTC) |
| `orders/XuLyDonShopee.App/ViewModels/OrderStatisticsViewModel.cs` | Bỏ `LoadSharedStatistics` chặn luồng; tách `ApplyLocal` (vẽ số máy NGAY, đồng bộ) + `LoadSharedStatisticsAsync` (gọi hub ở nền, marshal về UI thread qua `Dispatcher.UIThread`); thêm `_statsRequestId` chống trả lời lạc nhịp; thêm `SourceText`; client tự dựng `TrackingText/EstimateCoverageText/LastSyncedText/ScopeText/EmptyMessage` |
| `orders/XuLyDonShopee.App/Views/OrderStatisticsView.axaml` | Thêm 1 dòng `SourceText` dưới tiêu đề; sửa câu "Cách tính" cho đúng cả 2 nguồn |
| `orders/XuLyDonShopee.Tests/OrdersViewModelTests.cs` | Sửa test không biên dịch (lỗi #4): bỏ `StubSession` + gán `services.Sessions`, viết lại đi qua `OrderRowViewModel.RedownloadSlipCommand` như code thật; dọn `using` trùng (CS0105) |
| `orders/XuLyDonShopee.Tests/OrderStatisticsViewModelTests.cs` | **+3 test**: chưa nối hub / hub không phản hồi (giữ số local + nói rõ nguồn) và **hub chậm 3s không chặn luồng vẽ** (hồi quy lỗi #1) |
| `server/Shopee.Hub.Web.Tests/HubSharedOrderStatsTests.cs` | **MỚI — 5 test** (4 test bắt buộc + 1 test migration) |
| `server/Shopee.Hub.Web.Tests/Shopee.Hub.Web.Tests.csproj` | Thêm `ProjectReference` tới `XuLyDonShopee.Core` với `Aliases="ordersCore"` (Shopee.Hub.Web LINK sẵn vài file nguồn của project này → tham chiếu thẳng sẽ CS0433) — chỉ để test "số hub khớp số local" |

### Build & test

```text
dotnet build ShopeeSuite.sln      -c Debug  → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet build server/ShopeeHub.sln -c Debug  → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test orders/XuLyDonShopee.Tests      → Passed! Failed: 0, Passed: 1449, Total: 1449
dotnet test server/Shopee.Hub.Web.Tests     → Passed! Failed: 0, Passed:   30, Total:   30
```

Trước bản vá project test KHÔNG biên dịch được (CS0200 dòng 133, CS1061 dòng 136) → 1445 test chưa từng chạy. Nay
1449 (1445 cũ + 3 test VM mới + 1 test cũ viết lại) và 30 test hub (25 cũ + 5 mới).

### 4 test bắt buộc (đều ở `server/Shopee.Hub.Web.Tests/HubSharedOrderStatsTests.cs`)

1. `FirstSeenAt_GiuNguyenKhiDongBoLai_ChiSyncedAtDoi` — upsert lại: `first_seen_at` bất biến, `synced_at` mới hơn, dữ liệu vẫn cập nhật.
2. `LocTheoKhoangUtc_BienDuoiDong_BienTrenMo` — đơn ở đúng mốc `from` được đếm, ở đúng mốc `to` bị loại.
3. `KhoangLoc_HieuTheoUtc_KhongPhuThuocGioMayChu` — mốc `2026-03-15T22:30Z`: khoảng UTC ôm đúng mốc → 1 đơn; khoảng cùng giờ đồng hồ nhưng lệch 7 tiếng (UTC+7) → 0 đơn, kết quả không đổi theo múi giờ máy chạy test.
4. `SoHubKhopSoLocal_KeCaSauKhiDongBoLai` — **chốt chặn lỗi #3**: cùng 3 đơn trên `OrdersRepository` (local) và `HubDatabase` (hub), đồng bộ lại ở thời điểm khác → khoảng chứa mốc cũ: local 3 = hub 3 (số đơn hủy cũng khớp); khoảng chứa mốc đồng bộ lại: local 0 = hub 0.

Thêm `MoDbCu_ThemCotFirstSeen_BackfillTuSyncedAt_VaVanUpsertDuoc` — dựng `hub.db` với bảng `orders` ĐÚNG schema cũ
(không có `first_seen_at`) + 1 đơn sẵn, mở `HubDatabase` như hub khởi động lại: không sập, backfill `first_seen_at =
synced_at`, đơn cũ còn nguyên, upsert đơn mới vẫn chạy.

**Đã kiểm chứng test bắt lỗi thật:** tạm đổi `WHERE o.first_seen_at` về `o.synced_at` → test 3 và 4 FAIL (2 passed /
2 failed), rồi hoàn nguyên.

### Grep chứng minh không còn lời gọi chặn luồng

```text
$ grep -rn "GetAwaiter()\.GetResult()|\.Result\b|\.Wait()" \
    orders/XuLyDonShopee.App/ViewModels/OrderStatisticsViewModel.cs \
    orders/XuLyDonShopee.App/Services/AppServices.cs \
    suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs \
    suite/Shopee.Core/Coordination/HubClient.cs
OrderStatisticsViewModel.cs:205:    /// ... (đây là lỗi cũ: <c>GetAwaiter().GetResult()</c>   ← DÒNG COMMENT

$ ... | grep -v "///"
exit=1   (không còn dòng CODE nào)

$ grep -rn "GetAwaiter()\.GetResult()|\.Wait()" orders/XuLyDonShopee.App --include=*.cs
(chỉ đúng dòng comment trên — cả module Đơn hàng không còn chỗ nào)
```

Kèm test hành vi `HubChamKhongChanLuongUi_SoLocalHienNgay`: hook hub `await Task.Delay(3s)`, dựng VM + đổi ngày phải
xong dưới 1s và đã có số local.

### Lệch so với plan / điểm cần soi lại

1. **Tên trường `LastSeenUtc` → `LastSyncedUtc`.** Plan đặt tên `LastSeenUtc`, nhưng giá trị là `MAX(synced_at)`
   (ô UI là "ĐỒNG BỘ GẦN NHẤT", đúng nghĩa cũ). Đặt tên `LastSeenUtc` sẽ dễ nhầm với `first_seen_at`.
2. **Thêm trạng thái nguồn thứ 3.** Plan nêu 2 chuỗi; đã thêm `"Số trên MÁY NÀY (app chạy độc lập, chưa nối Hub)."`
   cho ca hook `QueryOrderStatistics == null` (app Đơn hàng chạy riêng / chế độ không có Hub) — nói "Hub không phản
   hồi" trong ca đó là sai sự thật.
3. **Test "Tải phiếu" không giữ được đúng ý định cũ.** Nhánh *"đang chờ đến lượt"* nằm sau
   `_services.Sessions.Get(id)`, mà `AppServices.Sessions` là chỉ-đọc và tạo factory phiên THẬT trong ctor → không
   bơm được stub nếu không sửa code sản phẩm (plan cấm). Đã viết lại test đi qua đúng API thật
   (`OrderRowViewModel.RedownloadSlipCommand`) và kiểm nhánh **chưa mở phiên**; nhánh Queued chưa có test — ghi chú
   ngay trong file test.
4. **Thêm reference có alias vào project test hub** (`XuLyDonShopee.Core`, `Aliases="ordersCore"`) — cần thiết cho
   test bắt buộc số 4; alias để không đụng các file test hub khác (Shopee.Hub.Web đã LINK sẵn `SyncedOrder`,
   `ShopeeShippingNav`… nên tham chiếu thường sẽ trùng kiểu).
5. **Định dạng mốc so sánh trong SQL:** hub ghi thời gian bằng `Iso(DateTimeOffset)` → hậu tố `+00:00`, còn
   `DateTime(Kind=Utc).ToString("o")` ra `Z`; so chuỗi thì `'+' < 'Z'` nên đơn ở ĐÚNG biên `from` sẽ bị loại oan.
   Đã dựng mốc qua `Iso(new DateTimeOffset(...))` cho cùng định dạng (bản cũ cũng dính, nhưng bị lỗi múi giờ che mất).
6. **Chưa sờ tới** ngày đặt đơn thật của Shopee, `OrdersPush`, version, commit, deploy — đúng phạm vi plan.

### Cần lưu ý khi deploy (PHÁ TƯƠNG THÍCH — deploy Hub và phát hành client CÙNG ĐỢT)

- `GET /api/orders/stats` đổi tham số `from/to` → `fromUtc/toUtc`. **Client CŨ** gọi `?from=&to=` sẽ nhận
  `400 BadRequest` → `GetOrderStatisticsAsync` trả `null` → tab Thống kê bản cũ **tự về số local** (không crash,
  không hộp thoại), nhưng dòng nguồn của bản cũ chưa có nên người dùng bản cũ không biết mình đang xem số máy.
- **Client MỚI + Hub CŨ** (nếu lỡ phát hành client trước): hub cũ đọc `from/to` rỗng → cũng `BadRequest` → client mới
  hiển thị số local kèm chữ "Hub không phản hồi…". An toàn cả hai chiều, nhưng ĐỀ NGHỊ **deploy Hub trước**.
- Migration chạy lúc hub khởi động: `ALTER TABLE orders ADD COLUMN first_seen_at TEXT` + backfill + tạo index. Với
  DB thật trên VM, backfill lấy `synced_at` — với đơn đã đồng bộ nhiều lần đó là lần GẦN NHẤT, nên **số liệu lịch sử
  trước bản vá chỉ là xấp xỉ** (đơn cũ có thể dồn vào ngày đồng bộ gần nhất). Từ bản vá trở đi số liệu chính xác.
  Backfill + tạo index chạy một lần trên bảng `orders` — nếu bảng lớn, lần khởi động đầu sau deploy sẽ lâu hơn bình thường.
- Chưa deploy, chưa commit, chưa bump version — để phiên chính quyết.
