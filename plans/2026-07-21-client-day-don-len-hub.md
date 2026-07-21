# Plan: Client đẩy đơn mới lên hub sau mỗi lần sync (module Đơn hàng)

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
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

<chưa có>
