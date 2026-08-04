# Plan: Đóng banner lỗi địa chỉ → bền local + Hub + client khác

- **Ngày:** 2026-08-04
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Auto

## 1. Bối cảnh & mục tiêu

**Hành vi mong muốn (user chốt):** trên tab Kết quả, bấm **X** đóng banner "Cảnh báo: Lỗi địa chỉ. Shop …" nghĩa là địa chỉ đã được fix. Phải:

1. Dismiss **local** (SQLite `pickup_address_alerts.dismissed_at`).
2. Dismiss **Hub** (`orders_pickup_alerts`).
3. **Lan sang client khác** cùng subaccount → họ cũng bỏ banner / dấu X đỏ.
4. Mở lại app / chọn lại tài khoản → **không** hiện lại banner chỉ vì đã từng lỗi (trừ khi vòng chạy sau lại fail pickup thật → upsert mới là đúng).

**Hiện trạng đã kiểm chứng trên máy dev (2026-08-04):**

| Nơi | Thực tế |
|---|---|
| Local `app.db` | `alina99.store` **đã có** `dismissed_at` — dismiss local ghi được |
| Hub `hub.db` `orders_pickup_alerts` | **0 dòng** cho shop thật (trước khi probe) — upsert banner thật chưa lên Hub |
| Hub API (localhost sau deploy) | upsert / dismiss / GET hoạt động (probe `probe.store` OK) |

**Gốc lỗi (đã đọc code):**

1. **Hub `DismissPickupAlert` chỉ `UPDATE`** khi đã có dòng và `dismissed_at IS NULL`. Nếu upsert Hub trước đó fail (404 Hub cũ / offline / fire-and-forget nuốt lỗi) → dismiss **không tạo tombstone** → GET Hub trả `[]` → máy khác giữ banner local active mãi.
2. **Merge kéo Hub** (`AccountsViewModel.KetQua.SyncAddressAlertsFromHubAsync`): Hub `dismissed=false` → luôn `PickupAlerts.Upsert` local (xóa dismiss). Race điển hình: upsert Hub chậm tới **sau** user bấm X → Hub active stale → lần sync sau **dựng lại** banner đã đóng.
3. **Dismiss Hub fire-and-forget + `catch {}` rỗng** — fail im lặng; user tưởng đã đồng bộ.
4. **Chỉ sync khi chọn tài khoản** (`SelectedRow`) — máy khác mở sẵn tab Kết quả không tự kéo dismiss (có thể chấp nhận nếu có nút/đồng bộ định kỳ nhẹ; tối thiểu: mỗi lần vào tab / interval ngắn khi đang xem Kết quả).

**Không nhầm với:** vòng chạy mới vẫn `pickupOk=false` → `GhiBannerLoiDiaChi` upsert lại (hiện lại banner) là **đúng** — địa chỉ chưa fix thật.

## 2. Phạm vi

**Làm:**

- Hub: dismiss = **UPSERT tombstone** (có dòng → set `dismissed_at`; chưa có → INSERT với `dismissed_at` now).
- Hub: upsert active chỉ “thắng” dismiss khi là sự kiện mới (dùng mốc thời gian — xem bước dưới).
- Client merge Hub: **local dismiss mới hơn Hub active** thì không dựng lại; thay vào đó re-push dismiss lên Hub.
- Client dismiss: đảm bảo gọi Hub dismiss tạo tombstone; log khi fail (không làm fail UI).
- Giảm race upsert-sau-dismiss trên client (thứ tự / chốt theo shop).
- Unit test cover dismiss tombstone + merge không resurrect.
- Deploy Hub DLL sau khi sửa (API + DB logic).

**Không làm:**

- Không đổi luật bỏ qua shop khi `pickupOk=false`.
- Không đổi nội dung Slack / `orders/app-alert`.
- Không backfill lịch sử banner cũ trên Hub (chỉ sửa hành vi từ bản này).
- Không làm SignalR push realtime (kéo khi chọn acc / khi đang xem Kết quả là đủ cho vòng này).

## 3. Các bước thực hiện

### Bước 1 — Hub: dismiss tạo tombstone

File: `server/Shopee.Hub.Web/Data/HubDatabase.PickupAlerts.cs`

Đổi `DismissPickupAlert` thành INSERT…ON CONFLICT DO UPDATE:

- Set `dismissed_at = now`, `updated_by_machine = mid`.
- Giữ `province` / `created_at` nếu đã có; nếu INSERT mới: `province=''`, `created_at=now`, `dismissed_at=now`.

### Bước 2 — Hub: upsert không đè dismiss mới hơn (chống race)

Cùng file. `UpsertPickupAlert` hiện luôn `dismissed_at=NULL`.

Sửa logic ON CONFLICT:

- Luôn cập nhật `province`, `updated_by_machine`.
- Chỉ clear `dismissed_at` + refresh `created_at` khi **chưa dismiss** HOẶC khi client gửi mốc sự kiện mới hơn `dismissed_at`.

Cách làm gọn (khuyến nghị):

- Thêm field optional `OccurredAt` (ISO) vào `OrdersPickupAlertRequest` (`suite/Shopee.Core/Coordination/OrderDtos.cs`).
- Client upsert/dismiss gửi `OccurredAt = UtcNow` lúc sự kiện.
- Hub: nếu `OccurredAt` parse được và `< dismissed_at` hiện có → **không** clear dismiss (bỏ qua upsert cũ); ngược lại mới active lại.
- Client đời cũ không gửi `OccurredAt` → Hub dùng `UtcNow` lúc nhận (hành vi gần như hiện tại; vẫn cần bước 3 phía client).

Map API: `ClientApiEndpoints` truyền `OccurredAt` vào DB methods.

### Bước 3 — Client: merge Hub không resurrect dismiss mới hơn

File: `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.KetQua.cs` — `SyncAddressAlertsFromHubAsync`.

Với mỗi item Hub:

- `dismissed == true` → `PickupAlerts.Dismiss` (như cũ).
- `dismissed == false`:
  - Đọc local `ListAll`; nếu cùng shop đã có `DismissedAt` và (không có cách so `Hub.CreatedAt` từ API — xem dưới) thì:
    - **Không** `Upsert`.
    - Fire-and-forget `DismissPickupAlertToHub` để ghi tombstone / sửa Hub stale.
  - Ngược lại → `Upsert` như cũ.

Để so được thời gian Hub active vs local dismiss: mở rộng `OrdersPickupAlertItem` thêm `CreatedAt` / `DismissedAt` (ISO, optional). GET Hub đã có đủ cột — map thêm trong `ClientApiEndpoints`. `FetchPickupAlertsFromHub` và tuple merge cập nhật tương ứng.

Merge rule đo được:

- Hub dismissed → local dismiss.
- Hub active và (`local không dismiss` HOẶC `hub.CreatedAt > local.DismissedAt`) → local upsert.
- Hub active và `local.DismissedAt >= hub.CreatedAt` → giữ local dismiss + re-push dismiss Hub.

### Bước 4 — Client: dismiss / upsert Hub chắc chắn hơn

Files:

- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.KetQua.cs` — `DismissAddressAlert`
- `orders/XuLyDonShopee.App/Services/OrderPersistPipeline.cs` — `GhiBannerLoiDiaChi`
- `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.HubPush.cs` (nếu cần gửi `OccurredAt`)

Chi tiết:

1. Gửi `OccurredAt` trên upsert + dismiss.
2. Dismiss: sau dismiss local, vẫn fire-and-forget Hub nhưng **log** khi `false`/exception (`Trace` hoặc `log` nếu có) — không nuốt rỗng hoàn toàn; UI vẫn biến mất ngay.
3. Chống race trên **cùng process**: với mỗi `(accountLogin, shopLogin)`, dùng `SemaphoreSlim(1,1)` hoặc hàng đợi đơn giản trong helper nhỏ (có thể đặt `orders/.../Services/PickupAlertHubSync.cs` hoặc static gate trong `OrderPersistPipeline`) để upsert Hub và dismiss Hub **không chồng chéo** — dismiss chờ upsert đang chạy xong rồi mới dismiss (hoặc ngược lại theo thứ tự gọi).

### Bước 5 — Kéo Hub khi đang xem Kết quả (lan sang máy khác sớm hơn)

File: `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.KetQua.cs` / `AccountsViewModel.cs`.

Khi `DetailTabIndex` = tab Kết quả và có `SelectedRow`: mỗi ~60s gọi `SyncAddressAlertsFromHubAsync` (best-effort, bỏ qua nếu đang sync). Hủy timer khi đổi tab / dispose VM. Không spam khi không mở tab.

### Bước 6 — Test

File: `orders/XuLyDonShopee.Tests/PickupAddressAlertsTests.cs` (+ test Hub DB nếu có project test Hub; không thì test pure merge helper).

Tách hàm thuần merge nếu cần (vd. `QuyetDinhMergePickupAlert(localDismissedAt, hubDismissed, hubCreatedAt)`) để test không cần UI:

- Local dismiss mới hơn Hub active → KeepLocalDismiss (repush).
- Hub dismissed → LocalDismiss.
- Hub active mới hơn local dismiss → LocalUpsert.
- Hub DismissPickupAlert khi chưa có dòng → list ra `Dismissed=true`.

Chạy: `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj --filter PickupAddressAlerts`

### Bước 7 — Build + deploy Hub

- `dotnet build` orders + suite/hub bị ảnh hưởng; 0 warning.
- Publish + deploy `Shopee.Hub.Web.dll` lên `vps-muinx` (như quy trình CLAUDE.md).
- Client: bump version + CHANGELOG khi release (không bắt buộc trong bước plan này nếu user chưa bảo release — nhưng sửa client cần bản app mới mới có merge/timer).

## 4. Tiêu chí nghiệm thu

- [ ] Bấm X → local `dismissed_at` có; GET Hub cùng `accountLogin`+`shopLogin` có dòng `dismissed=true` (kể cả trước đó Hub chưa từng có dòng).
- [ ] Máy/client khác (hoặc xóa local alert rồi sync): sau sync, banner biến mất; không bị Hub active stale dựng lại.
- [ ] Giả lập race: upsert Hub “cũ” (`OccurredAt` trước `dismissed_at`) không clear dismiss trên Hub.
- [ ] Vòng chạy mới `pickupOk=false` thật → banner hiện lại (upsert mới thắng dismiss cũ).
- [ ] `dotnet test … --filter PickupAddressAlerts` xanh; build 0 warning.
- [ ] Hub production health OK sau deploy.

## 5. Rủi ro & lưu ý

- Đổi hợp đồng DTO thêm field optional — client cũ bỏ qua field mới; Hub cũ không hiểu `OccurredAt` thì chỉ lợi từ tombstone dismiss + merge client mới.
- Timer 60s chỉ khi mở tab Kết quả — tránh tải Hub khi idle.
- Không xóa dòng alert (giữ lịch sử); chỉ dismiss.
- Dữ liệu probe test `probe.store` trên Hub có thể để hoặc xóa tay — không ảnh hưởng production shops.

---

## Báo cáo thực thi (điền sau khi xong)

_(chưa thực thi)_
