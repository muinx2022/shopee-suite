# Plan: Tab Đơn hàng ở /dispatch — chọn máy → list tài khoản → bấm action

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Tab **BigSeller** của `/dispatch` đã đổi sang trục máy-first (commit `a179738`): hàng thẻ máy → chọn máy → lưới
shop → bấm action thẳng trên dòng. Người dùng muốn tab **Đơn hàng** làm y hệt: *list máy → bấm máy nào thì list ra
các tài khoản của máy đó + action tương ứng; mỗi acc có nhiều shop; **1 acc chỉ chạy bởi 1 client tại 1 thời
điểm**, còn **1 client chạy được nhiều acc**.*

**Hai ràng buộc đó code client ĐÃ có sẵn — không phải xây mới:**
- `orders/XuLyDonShopee.App/Services/AccountSessionManager.cs:25-39`: giữ NHIỀU phiên nhưng chỉ **một phiên chiếm
  slot cầu nối** một lúc (cổng WS 47821 cố định + `KillBrowsersOnProfile` giết chéo), acc còn lại vào **hàng đợi
  FIFO** trạng thái `Queued`. Đúng "1 client nhiều acc".
- Account-lease `orders:<login>` (qua `ReserveAccounts`) đã khoá xuyên máy → "1 acc 1 client".

**Chỗ vướng đã khảo sát:** hub **không có danh bạ tài khoản Đơn hàng**. Tài khoản (`Email/Password/Cookie/ProxyKey/
PickupAddress/VerifyEmail…`) chỉ nằm trong DB cục bộ từng máy (`orders/XuLyDonShopee.Core/Data/AccountRepository.cs`),
KHÔNG có đường đẩy lên hub. Trang hub `/config/accounts` là acc Shopee cho **Scrape/Search**, khác tập này.

**Người dùng đã chốt: hub làm GƯƠNG (mirror), KHÔNG sở hữu tài khoản.** Mỗi máy tự đẩy danh bạ acc của nó lên hub
(**không đẩy mật khẩu/cookie**); hub hiển thị acc theo máy đang chọn. Đánh đổi đã chấp nhận: không giao được acc
sang máy chưa có acc đó. Lý do chọn: đăng nhập Shopee từ máy lạ dính verify/captcha nên acc vốn nên dính máy.

**Đã có sẵn để dùng lại (đừng xây lại):**
- Client lưu sẵn **shop theo acc**: bảng `account_shops(account_id, shop_login, shop_name, sort_order)` trong
  `orders/XuLyDonShopee.Core/Data/ResultsRepository.cs` — ghi mỗi lượt đọc `/portal/shop`.
- `AccountSessionManager.Start(long id)` / `Stop(long id)` / `Get(long id)` + event `Changed`.
- Suất đơn hàng đã heartbeat 12s: `suite/Shopee.Core/Coordination/OrdersSlotHeartbeat.cs` (commit `fb637f0`),
  id suất = `<id-máy>:orders`, và đã có tiền lệ **lệnh đi trong phản hồi heartbeat + ack** (lệnh update app).

**Nút cần có (người dùng chọn):** `▶ Chạy` / `✖ Dừng`, `↻ Đồng bộ đơn một lượt`, `🔑 Đăng nhập lại / kiểm tra tk`.
KHÔNG làm nút "chạy cả máy".

## 2. Phạm vi

**Làm:**
- Client đẩy **gương danh bạ** acc + shop con + trạng thái phiên lên hub (không mật khẩu, không cookie).
- Hub: 2 bảng lưu gương + endpoint nhận.
- Kênh **lệnh hub → suất đơn hàng** đi trong phản hồi heartbeat + ack (đúng khuôn lệnh update app đang chạy).
- Client thực thi lệnh `run` / `stop` (chắc chắn có đường sạch: `AccountSessionManager.Start/Stop`).
- Tab Đơn hàng ở `/dispatch`: thẻ máy (chỉ suất đơn hàng) → lưới acc của máy đó → nút action, luật khoá 1-acc-1-máy.

**Làm NẾU tìm được đường sạch, KHÔNG được bịa:**
- Lệnh `sync-once` (đồng bộ đơn một lượt) và `relogin` (đăng nhập lại / kiểm tra tk). Phải soi
  `AccountsViewModel.Run/ChayThuBridge` + `AccountSession` xem có điểm vào cấp service không. **Không có đường sạch
  thì BỎ hai lệnh đó khỏi đợt này**, nút hiện disable kèm title "sẽ làm ở đợt sau", và **báo rõ trong báo cáo** —
  tuyệt đối không chép/nhái logic phiên.

**Không làm:**
- KHÔNG chuyển acc lên hub làm nguồn sự thật (đó là hướng khác, người dùng đã loại).
- KHÔNG đẩy `Password` / `Cookie` / `VerifyEmailPassword` lên hub.
- KHÔNG đụng tab BigSeller, KHÔNG đụng `Fleet.razor`.
- KHÔNG commit, KHÔNG deploy, KHÔNG release client.

## 3. Các bước thực hiện

### Bước 1 — DTO gương (`suite/Shopee.Core/Coordination/HubDtos.cs`)

```csharp
/// <summary>Một tài khoản Đơn hàng trên MỘT máy (gương — hub không sở hữu, không nhận mật khẩu/cookie).</summary>
public sealed record OrdersAccountItem(
    string Login,            // Account.Email — KHOÁ tự nhiên, KHÔNG dùng Id local (Id lệch giữa các máy)
    string SessionState,     // "" | "queued" | "opening" | "running" | "stopping" (map từ SessionState)
    List<string> Shops,      // shop_name của account_shops, đúng thứ tự sort_order
    bool VerifyFailed,       // Account.VerifyFailedAt != null → cần xác minh
    DateTimeOffset? LastSyncAt);

public sealed record OrdersAccountsPushRequest(string MachineId, string Hostname, List<OrdersAccountItem> Accounts);
```

**Khoá là `Login` (email), KHÔNG phải Id local** — đây là bẫy đã dính: Id acc do client tự sinh nên lệch giữa các
máy (xem plan/memory "workbook sync account id divergence"). `MachineId` = **id suất đơn hàng** (`<host>:orders`).

Route mới trong `HubRoutes`: `OrdersAccounts = "/orders/accounts"`.

### Bước 2 — Client đẩy gương (`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`)

Thêm một worker nhẹ (đặt cạnh các `Wire*` sẵn có):
- Nguồn: `services.Accounts.GetAll()` + `ResultsRepository.GetShops(accountId)` + trạng thái phiên từ
  `AccountSessionManager.Get(id)?.State`.
- Nhịp đẩy: **đẩy ngay khi khởi động**, **đẩy khi `AccountSessionManager.Changed` bắn** (gộp nhịp: nhiều thay đổi
  liên tiếp chỉ đẩy 1 lần, tối thiểu 3s giữa 2 lượt), và **đẩy định kỳ 60s** làm nhịp nền. KHÔNG bám nhịp
  heartbeat 12s — danh bạ đổi chậm, đẩy 12s là phí băng thông qua tunnel.
- Lỗi mạng → nuốt + thử lượt sau (như mọi đường đẩy hub khác trong file này).
- Chỉ chạy ở chế độ có module Đơn hàng (`ShowsShopee`), tức nơi `OrdersSlotHeartbeat` đang sống.

### Bước 3 — Hub: lưu gương (`server/Shopee.Hub.Web/Data/HubDatabase.OrdersAccounts.cs` — file partial MỚI)

```sql
CREATE TABLE IF NOT EXISTS orders_accounts(
  machine_id TEXT NOT NULL, login TEXT NOT NULL, session_state TEXT DEFAULT '',
  verify_failed INTEGER DEFAULT 0, last_sync_at TEXT DEFAULT '', updated_at TEXT,
  PRIMARY KEY(machine_id, login));
CREATE TABLE IF NOT EXISTS orders_account_shops(
  machine_id TEXT NOT NULL, login TEXT NOT NULL, shop_name TEXT NOT NULL, sort_order INTEGER DEFAULT 0,
  PRIMARY KEY(machine_id, login, shop_name));
```

`UpsertOrdersAccounts(request)`: **thay TOÀN BỘ danh bạ CỦA MÁY ĐÓ** trong một transaction (xoá dòng của
`machine_id` rồi ghi lại). Đây là hợp đồng của gương — client là nguồn sự thật cho danh sách của chính nó — chứ
KHÔNG phải tự ý dọn dữ liệu; **không đụng** dòng của máy khác. Ghi comment nói rõ điều này.

Endpoint: `api.MapPost(HubRoutes.OrdersAccounts, …)` trong `Api/ClientApiEndpoints.cs`.

### Bước 4 — Kênh lệnh hub → suất đơn hàng

Bảng hub:
```sql
CREATE TABLE IF NOT EXISTS orders_commands(
  id TEXT PRIMARY KEY, machine_id TEXT NOT NULL, login TEXT NOT NULL, action TEXT NOT NULL,
  status TEXT NOT NULL, created_at TEXT, ack_at TEXT, error TEXT DEFAULT '');
```
`action` ∈ `run` | `stop` | `sync-once` | `relogin`. `status` ∈ `pending` | `sent` | `done` | `failed`.

- `MachineHeartbeatResponse` thêm `List<OrdersCommandDto> OrdersCommands` (client cũ bỏ qua field lạ — đúng khuôn
  đã ghi trong chú thích của DTO đó). Hub trả các lệnh `pending` của đúng `machine_id` rồi đánh `sent`.
- Ack: `POST /orders/commands/ack` với `(Id, Status, Error)` → hub cập nhật `done`/`failed`.
- **Chống lệnh mồ côi:** lệnh ở `sent` quá 5 phút chưa ack → đưa về `failed` kèm lý do "client không phản hồi"
  (quét cùng chỗ với các sweep sẵn có, đừng dựng timer mới).
- Client: `OrdersSlotHeartbeat` đọc `OrdersCommands` từ phản hồi, bắn qua một hook (giống hook `UpdateRequested`
  đang có) để `OrdersModuleHost` thực thi rồi ack. **Dedup theo `Id`** — chống chạy lại lệnh cũ khi mạng lặp.

### Bước 5 — Client thực thi lệnh (`OrdersModuleHost`)

Map `login` → `accountId` bằng `services.Accounts.GetAll()` (so sánh `Email`, ordinal-ignore-case). Không tìm thấy
→ ack `failed` kèm lý do rõ ("máy này không có tài khoản <login>").

| action | Thực thi | Ghi chú |
|---|---|---|
| `run` | `AccountSessionManager.Start(id)` | Đang chạy rồi → ack `done` kèm "đã chạy sẵn" (idempotent) |
| `stop` | `AccountSessionManager.Stop(id)` | Không chạy → ack `done` |
| `sync-once` | soi tìm đường sạch; không có → **bỏ**, ack `failed` "chưa hỗ trợ" | xem mục Phạm vi |
| `relogin` | soi tìm đường sạch; không có → **bỏ**, ack `failed` "chưa hỗ trợ" | xem mục Phạm vi |

Sau khi thực thi, **đẩy gương ngay** (Bước 2) để hub thấy trạng thái mới không phải chờ 60s.

### Bước 6 — UI tab Đơn hàng (`server/Shopee.Hub.Web/Components/Pages/Dispatch.razor`)

Thay khối read-only hiện tại. Bố cục **y hệt tab BigSeller** (dùng lại `.mcards`/`.mcard`/`.opbtn`, đừng đẻ class mới
nếu tái dùng được):

1. Hàng thẻ máy — **chỉ suất đơn hàng** (`Kind == MachineSlots.Orders`), máy offline `disabled` thật.
   Chưa máy nào → dòng nhắc "Chưa máy client nào chạy chế độ Shopee/Full kết nối hub."
2. Chưa chọn máy → dòng nhắc + mọi nút disable (giống tab BigSeller).
3. Chọn máy → lưới acc của máy đó, mỗi dòng:
   `Tài khoản (login) | Shop (số shop, bung danh sách khi bấm) | Trạng thái | Đơn chờ | Sync cuối | Hành động`
   - Trạng thái: pill theo `session_state` (`▶ Đang chạy` / `⏱ Chờ đến lượt` / `— Dừng`) + `⚠ Cần xác minh` khi
     `verify_failed`.
   - Đơn chờ: dùng lại `ShopOrderSummaries` sẵn có, cộng theo các shop của acc (khớp theo tên shop).
   - Hành động: `▶ Chạy` ⇄ `✖ Dừng` (đổi vai theo trạng thái), `↻ Đồng bộ 1 lượt`, `🔑 Đăng nhập lại`.
4. **Luật khoá 1-acc-1-máy:** acc đang bị **máy khác** giữ (`Snap.AccountLeases` có `AccountId == "orders:" + login`
   với `MachineId` khác máy đang chọn) → nút `▶ Chạy` **disabled** kèm title "acc đang chạy ở máy X". Đây là chỗ
   `Snap.AccountLeases` ĐÚNG việc — khác tab BigSeller (tab đó phải dùng `Snap.Leases`).
5. Bấm nút = tạo `orders_commands` `pending` → hiện `⏳ đã gửi lệnh…` cho tới khi client ack (bám ack, đừng lạc
   quan theo DB), giống cách tab BigSeller bám lease.
6. View-state vào URL: thêm `omach` (máy đang chọn ở tab Đơn hàng), giữ nguyên `tab/f/acct/q/mach` đang có.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới; `dotnet test` xanh.
- [ ] **Tương thích ngược:** hub mới + client CŨ (không đẩy gương, không hiểu `OrdersCommands`) → hub không lỗi,
      tab Đơn hàng hiện "chưa có dữ liệu", heartbeat cũ vẫn 200.
- [ ] DB production (đã có `orders`, `machines` 11 cột) mở bằng hub mới → tự thêm 3 bảng mới, không mất dữ liệu.
- [ ] Đẩy gương: POST `/orders/accounts` với 2 acc (1 acc 3 shop) → hub lưu đúng; POST lại với 1 acc → danh bạ của
      máy đó còn đúng 1 acc (**thay toàn bộ**), danh bạ máy khác **không đổi**.
- [ ] Không có `password` / `cookie` trong payload đẩy lên (kiểm bằng đọc JSON thật gửi đi).
- [ ] Tab Đơn hàng: thẻ máy chỉ hiện suất đơn hàng; máy offline không bấm được; chưa chọn máy thì nút disable.
- [ ] Chọn máy → thấy đúng acc của máy đó kèm số shop; bung ra thấy danh sách shop.
- [ ] Bấm `▶ Chạy` → sinh 1 dòng `orders_commands` `pending` đúng `(machine_id, login, action)`; heartbeat kế tiếp
      của suất đó **nhận được lệnh** và dòng chuyển `sent`; ack → `done`.
- [ ] Gửi cùng một lệnh 2 lần (giả lập mạng lặp) → client chỉ thực thi **một lần** (dedup theo Id).
- [ ] Acc đang bị máy khác giữ (tạo account-lease `orders:<login>` của máy khác) → nút `▶ Chạy` **disabled** kèm lý do.
- [ ] Lệnh `sent` quá 5' không ack → tự chuyển `failed`, không kẹt `sent` vĩnh viễn.
- [ ] 400px không cuộn ngang; nền tối đọc được.
- [ ] Tab BigSeller và `Fleet.razor` **không đổi hành vi** (`git diff` của Fleet.razor rỗng).

## 5. Rủi ro & lưu ý

- **Khoá là `login`, không phải Id local.** Id acc do từng máy tự sinh nên lệch nhau — dùng Id là tái hiện đúng lỗi
  "1 acc không sync trên 1 client" đã từng dính.
- **Đừng đẩy mật khẩu/cookie lên hub.** Người dùng chọn mô hình gương chính vì điều này.
- **`Snap.AccountLeases` dùng ĐÚNG ở tab này** (khoá acc Shopee, khoá `orders:<login>`) nhưng **SAI ở tab BigSeller**
  (tab đó phải dùng `Snap.Leases`). Đừng gộp hai chỗ làm một hàm.
- Hợp đồng gương là **thay toàn bộ danh bạ của MỘT máy**; tuyệt đối không đụng dòng của máy khác, và không tự ý
  "dọn" thêm gì ngoài phạm vi máy đó.
- Lệnh phải **idempotent + dedup theo Id**: heartbeat có thể lặp khi mạng chập chờn; chạy lại `run` là mở lại
  trình duyệt giữa chừng phiên đang chạy — hỏng thật.
- `sync-once` / `relogin`: **thà bỏ còn hơn nhái**. Nếu không có điểm vào cấp service, để nút disable và báo lại.
- Client thay đổi ở đợt này **chỉ có tác dụng sau khi release** — bản release sắp tới sẽ gồm cả việc "suất làm việc"
  (commit `fb637f0`) đang chờ.

---

## Báo cáo thực thi (Opus điền sau khi xong)
