# Plan: Client đẩy đơn mới lên hub sau mỗi lần sync (module Đơn hàng)

- **Ngày:** 2026-07-21
- **Trạng thái:** hoàn thành (2026-07-21 — Fable nghiệm thu: tự build 0 lỗi 0 warning + 767/767 test, soi diff wiring OrdersModuleHost đạt; 1 test flaky ProxyRepository do ClearAllPools là nợ hạ tầng test có sẵn, không liên quan)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** cây làm việc CHÍNH `D:\Projects\shopee-suite`, nhánh `feature/gop-don-hang`
  (đã có commit hub `5cc044c`). KHÔNG đụng `main`. Một agent khác đang làm UI ribbon trong
  worktree `../shopee-suite-wt-fresh-profile` — KHÔNG đọc/ghi sang đó, và KHÔNG sửa các file
  UI thuộc việc ribbon (`MainWindow.axaml`, `ShellViewModel.cs`, `MainView.axaml`,
  `MainViewModel.cs`, `AccountsView.axaml`, các View/VM màn hình).

## 1. Bối cảnh & mục tiêu

Hub đã có `POST /api/orders/push` (tự đăng ký shop theo username, idempotent theo
`(shop_id, order_sn)`, notify webhook khi có đơn mới). Phía client (module Đơn hàng trong app
suite) cần đẩy đơn lên hub sau mỗi lần sync — **không chặn flow chính, không mất đơn khi hub
offline** (lượt sync sau tự đẩy bù).

Hạ tầng sẵn có (khảo sát 2026-07-21):
- Điểm ghi đơn DUY NHẤT: `AccountSession.SyncOrdersAsync` —
  `_services.Orders.UpsertMany(...)` (`orders/XuLyDonShopee.App/Services/AccountSession.cs:646`),
  sau đó kích 2 tích hợp nền: `StartGsheetPushInBackground` (`:693`, pattern Interlocked chống
  chồng + `Task.Run` + nuốt lỗi + cờ DB `gsheet_synced_at` làm hàng đợi ngầm) và
  `StartNotifyInBackground` (`:699`).
- Mẫu cờ DB: `OrdersRepository.GetForGsheetPush` (`:162`) + `MarkGsheetSynced` (`:202`,
  `COALESCE` giữ mốc lần đầu); cột thêm bằng `EnsureColumn` (`Database.cs:130-149`).
- RÀNG BUỘC KIẾN TRÚC: module đơn hàng KHÔNG tham chiếu `Shopee.Core` (suite). Chỉ shell
  `Shopee.Suite` thấy cả hai → **hook do shell rót vào** `AppServices`, module mặc định null
  (chạy độc lập vẫn nguyên vẹn). Mẫu shell rót hook: `App.axaml.cs:55` (`DiagLog`),
  `OrdersModuleHost` (`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`).
- Phía suite: `CoordinationRuntime.Active` / `.Client` (`suite/Shopee.Core/Coordination/
  CoordinationRuntime.cs:10-15`), `HubClient.PushOrdersAsync(OrdersPushRequest, ct)` →
  `OrdersPushResult?` (đã có, commit `5cc044c`), DTO `OrderPushItem`/`OrdersPushRequest`
  (`suite/Shopee.Core/Coordination/OrderDtos.cs` — `ShopUsername`, `ShopName?`, `Orders`).

## 2. Phạm vi

- **Làm (orders — module tự đủ, không biết gì về hub):**
  - `orders/XuLyDonShopee.Core/Data/Database.cs`: `EnsureColumn` thêm `hub_synced_at TEXT`
    vào bảng `orders` (theo mẫu các cột gsheet).
  - `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`:
    - `GetForHubPush(long accountId)` → `IReadOnlyList<SyncedOrder>` các đơn
      `hub_synced_at IS NULL` (dựng lại SyncedOrder từ cột bảng — mẫu GetForGsheetPush);
    - `MarkHubSynced(long accountId, IEnumerable<string> orderSns, DateTime atUtc)` —
      `COALESCE(hub_synced_at, $at)` giữ mốc lần đầu (mẫu MarkGsheetSynced).
  - `orders/XuLyDonShopee.App/Services/AppServices.cs`: hook
    `public Func<long, IReadOnlyList<SyncedOrder>, CancellationToken, Task<bool>>? PushOrdersToHub { get; set; }`
    (tham số: accountId, lô đơn, ct; trả true = hub nhận OK). Mặc định null = tắt.
  - `orders/XuLyDonShopee.App/Services/AccountSession.cs`: thêm
    `StartHubPushInBackground(log, tok)` — gọi NGAY CẠNH `StartGsheetPushInBackground` (`:693`),
    KHÔNG điều kiện insertedOrders (để đẩy bù backlog khi hub sống lại). Pattern y GSheet:
    cờ `_hubPushing` Interlocked chống chồng; `Task.Run(CancellationToken.None)`, thân dùng
    token phiên; hook null → return im lặng; `GetForHubPush` rỗng → return; chia LÔ ≤200 đơn,
    tuần tự — mỗi lô hook trả true → `MarkHubSynced` cho đúng các SN của lô, trả false/ném →
    log + DỪNG các lô sau (lượt sync sau đẩy lại); nuốt mọi lỗi trừ OperationCanceledException.
- **Làm (suite shell — rót hook):**
  - `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`: sau khi `TryCreate()` thành công,
    gán `Services.PushOrdersToHub = async (accountId, orders, ct) => {...}`:
    - `CoordinationRuntime.Active == false` hoặc `Client == null` → return false (đơn giữ
      nguyên chưa đánh dấu — thử lại lượt sau);
    - đọc account từ `Services.Accounts` theo accountId → `ShopUsername` = Email (trim,
      fallback Phone, fallback `"account-{id}"`); `ShopName` = cùng giá trị;
    - map `SyncedOrder` → `OrderPushItem` (mirror field-by-field, helper private static trong
      OrdersModuleHost);
    - `await CoordinationRuntime.Client.PushOrdersAsync(req, ct)` → non-null = true, null/ném
      → false (nuốt exception trong lambda, log qua `Trace.WriteLine`).
- **Làm (tests, `orders/XuLyDonShopee.Tests/`):**
  - `OrdersRepository`: đơn mới sau UpsertMany có mặt trong `GetForHubPush`; sau `MarkHubSynced`
    biến mất; Mark 2 lần giữ mốc đầu; đơn account khác không lẫn.
  - `AccountSession` (mức khả thi với stub sẵn có của test suite): hook được gọi với đúng lô
    pending; hook trả false → KHÔNG mark; hook null → không nổ. Nếu test qua AccountSession khó
    (phụ thuộc browser) thì tách logic lô-và-mark ra method/helper test được — ghi rõ cách làm
    trong báo cáo.
- **Không làm:**
  - KHÔNG sửa các file thuộc việc ribbon (danh sách ở đầu plan); KHÔNG đụng `server/`,
    `shared/`, `suite/Shopee.Core/` (mọi thứ cần đã có sẵn).
  - KHÔNG thêm UI cấu hình mới (dùng cấu hình hub-client sẵn có của suite ở tab
    Trạng thái/Cài đặt).
  - KHÔNG commit.

## 3. Kiểm chứng

- `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi 0 warning mới.
- `dotnet test orders/XuLyDonShopee.Tests/... -c Release` → 754 baseline + test mới pass.
- Kiểm nối dây tĩnh: grep xác nhận `PushOrdersToHub` được gán trong OrdersModuleHost và
  được gọi trong AccountSession (đường StartHubPushInBackground).

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch; toàn bộ test pass (≥754 + mới).
- [ ] Hook null (app đơn hàng chạy độc lập/hub tắt) → hành vi cũ y nguyên, không lỗi.
- [ ] Hub offline (hook trả false) → đơn KHÔNG bị đánh dấu, lượt sau đẩy lại; flow sync chính
      không chậm/không lỗi.
- [ ] Hub nhận OK → `hub_synced_at` được ghi, lượt sau không đẩy lại (idempotent 2 tầng:
      cờ client + UNIQUE hub).
- [ ] Không sửa file nào thuộc việc ribbon.

## 5. Rủi ro & lưu ý

- `SyncedOrder` không có accountId — `GetForHubPush` nhận accountId tham số, mark theo
  `(account_id, order_sn)`.
- Đừng mark khi hook ném/false — thà đẩy lặp (hub idempotent) còn hơn mất đơn.
- Notify trên hub chỉ bắn khi `Added>0` — đẩy bù backlog lần đầu có thể tạo 1 tin nhiều đơn
  (chấp nhận, đúng nghĩa "đơn mới về hub").
- Lô 200 để không nghẽn tunnel; `_bulkHttp` timeout 5 phút đã đủ rộng.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Kết quả:** Hoàn thành toàn bộ phạm vi plan. Build `ShopeeSuite.sln -c Release` = 0 warning / 0 error;
test module orders = 767/767 pass (754 baseline + 13 test mới). KHÔNG commit (theo yêu cầu).

### Đã làm — theo từng file

- `orders/XuLyDonShopee.Core/Data/Database.cs`:
  - Thêm cột `hub_synced_at TEXT` vào `CREATE TABLE orders` (cho DB mới).
  - Thêm `EnsureColumn(conn, "orders", "hub_synced_at", "TEXT")` trong `Initialize()` (migration cho DB CŨ).
- `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs` (thêm 2 method, đặt trước `CountByAccount`):
  - `GetForHubPush(long accountId) → IReadOnlyList<SyncedOrder>`: SELECT các đơn `hub_synced_at IS NULL`,
    `ORDER BY id`, dựng lại `SyncedOrder` đầy đủ 18 cột (mẫu `GetForGsheetPush`; `items_json` NULL → `"[]"`).
  - `MarkHubSynced(long accountId, IEnumerable<string> orderSns, DateTime atUtc)`: cập nhật trong 1 transaction,
    `hub_synced_at = COALESCE(hub_synced_at, $at)` (giữ mốc lần đầu), khóa `(account_id, order_sn)`, bỏ SN rỗng.
- `orders/XuLyDonShopee.App/Services/AppServices.cs`:
  - Thêm hook `public Func<long, IReadOnlyList<SyncedOrder>, CancellationToken, Task<bool>>? PushOrdersToHub`
    (mặc định null = tắt). Thêm using `System.Collections.Generic`, `System.Threading`,
    `System.Threading.Tasks`, `XuLyDonShopee.Core.Models`.
- `orders/XuLyDonShopee.App/Services/AccountSession.cs`:
  - Gọi `StartHubPushInBackground(log, tok)` NGAY CẠNH `StartGsheetPushInBackground` (không điều kiện
    `insertedOrders`) — dòng ~699.
  - `const int HubPushBatchSize = 200`; field `_hubPushing`; `StartHubPushInBackground` (Interlocked chống
    chồng + `Task.Run(CancellationToken.None)` + thân dùng token phiên); `PushOrdersToHubAsync` (hook null →
    return, không đụng DB; `GetForHubPush` rỗng → return; nuốt OCE + mọi lỗi, log "Hub: lỗi — ...").
  - Tách lõi chia-lô-và-đánh-dấu thành hàm THUẦN `public static Task<int> PushPendingToHubAsync(accountId,
    pending, push, markSynced, batchSize, ct)` — chia lô ≤200 tuần tự, lô true → markSynced đúng SN, lô false
    → break (dừng lô sau), `push` null / pending rỗng → trả 0, `ct` hủy → ThrowIfCancellationRequested cho xuyên.
- `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`:
  - Sau `Services = new AppServices()` gọi `WireHubPush(Services)` gán `Services.PushOrdersToHub`.
  - Lambda: `CoordinationRuntime.Active==false || Client==null` → false; đọc account theo id →
    `ShopUsername = Email(trim) ?? Phone(trim) ?? "account-{id}"`, `ShopName = ShopUsername`; map
    `SyncedOrder → OrderPushItem` (helper `ToPushItem`, mirror 18 field); `await Client.PushOrdersAsync` →
    non-null = true; `catch (OperationCanceledException) when (ct.IsCancellationRequested)` → rethrow, lỗi khác
    (gồm timeout tunnel) → `Trace.WriteLine` + false. Thêm using `System.Collections.Generic`, `System.Linq`,
    `System.Threading`, `Shopee.Core.Coordination`, `XuLyDonShopee.Core.Models`.
- Tests (`orders/XuLyDonShopee.Tests/`):
  - `OrdersRepositoryTests.cs` (+5): đơn mới có mặt trong `GetForHubPush` + map đủ field; `MarkHubSynced` →
    biến mất khỏi pending; mark tất cả → rỗng; mark 2 lần giữ mốc đầu; không lẫn tài khoản khác.
  - `AccountSessionHubPushTests.cs` (mới, +7): chia lô 250→[200,50] + đánh dấu đủ + đúng thứ tự + accountId
    xuyên xuống hook; lô nhỏ hơn batch → 1 lô; hook false → không đánh dấu; hook false ở lô 2 → giữ lô 1 đã
    đánh dấu, dừng lô 3; hook null → không nổ; pending rỗng → hook không gọi; token đã hủy → ném OCE.
  - `DatabaseMigrationTests.cs` (+1): DB CŨ thiếu `hub_synced_at` → migration thêm cột, đơn cũ còn nguyên +
    vào hàng đợi `GetForHubPush`, `MarkHubSynced` → rỗng.

### Kiểm chứng (lệnh + kết quả thật)

- `dotnet build ShopeeSuite.sln -c Release` → **Build succeeded. 0 Warning(s), 0 Error(s)**.
- `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj -c Release --no-build`:
  - Lần 1: 766 pass / 1 fail — `ProxyRepositoryTests.Update_SuaMoiTruong_VaKhongAnhHuongDongKhac` ném
    `ObjectDisposedException: 'SQLitePCL.sqlite3'` tại `ProxyRepository.Insert`. Đây là **race flaky sẵn có**:
    `TempDatabase.Dispose` gọi `SqliteConnection.ClearAllPools()` (process-global) từ một test class chạy SONG
    SONG, dispose kết nối mà ProxyRepositoryTests đang dùng. KHÔNG liên quan thay đổi của việc này
    (ProxyRepository không bị đụng). Càng nhiều test dùng TempDatabase thì xác suất chạm race càng cao.
  - Lần 2 (chạy lại): **767 pass / 0 fail** (Total 767). Xác nhận flaky, không phải lỗi code.
- Nối dây tĩnh (grep): `services.PushOrdersToHub = ...` trong OrdersModuleHost; `StartHubPushInBackground` được
  gọi trong SyncOrdersAsync; đường `PushOrdersToHubAsync → GetForHubPush + PushPendingToHubAsync + MarkHubSynced`.

### Cách xử lý phần test AccountSession

Chọn **tách helper thuần** (không test trực tiếp qua `AccountSession`). Lý do: ctor `AccountSession` cần
`ShopeeLoginService` + `IProxyHealthChecker` + 3 delegate (phụ thuộc browser), khó dựng trong unit test — đây
cũng là mẫu sẵn có của repo (`NextLoopDecision`/`ShouldSkipProcessing` là `public static`, test trực tiếp).
Đã rút lõi chia-lô-và-đánh-dấu ra `public static PushPendingToHubAsync(...)` với tham số `push` ĐÚNG chữ ký hook
(`Func<long, IReadOnlyList<SyncedOrder>, CancellationToken, Task<bool>>?`) → test phủ đủ 3 kịch bản plan yêu cầu
(hook nhận đúng lô pending; hook false → không mark; hook null → không nổ) cộng thêm partial-stop + hủy token.
`PushOrdersToHubAsync` (private, phần đụng DB + hook + log) chỉ còn là lớp vỏ mỏng đọc pending/ghi mark quanh helper.

### Đề xuất (không bắt buộc)

- Race flaky của `ClearAllPools()` là vấn đề hạ tầng test chung (không thuộc plan này): có thể tắt song song
  giữa các test class dùng SQLite bằng `[Collection]` chung, hoặc bỏ `ClearAllPools()` global trong
  `TempDatabase.Dispose`. Nên xử lý ở một plan riêng vì đụng nhiều test class ngoài phạm vi việc này.
- Điểm làm rõ so với plan (mục 2, nhánh suite): plan ghi "null/ném → false (nuốt exception trong lambda)". Tôi
  cho **OperationCanceledException khi `ct.IsCancellationRequested`** CHO XUYÊN (rethrow) — để khớp mệnh đề
  AccountSession "nuốt mọi lỗi trừ OperationCanceledException" và đồng nhất với các task nền khác trong file
  (SyncOrdersAsync/PushOrdersToGsheetAsync/Notify đều rethrow OCE). Timeout tunnel (TaskCanceledException khi ct
  CHƯA hủy) vẫn coi là lỗi hub → false + log, không bị nuốt nhầm thành "hủy".
