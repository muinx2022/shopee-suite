# Plan: Vá 5 lỗ hổng đồng bộ banner lỗi địa chỉ (hậu review v1.7.16)

- **Ngày:** 2026-08-04
- **Trạng thái:** hoàn thành (Bước 1 và Bước 5 ĐÃ ĐỔI HƯỚNG sau phản biện — xem Báo cáo thực thi)
- **Người lập:** phiên chính · **Người thực thi:** phiên chính

## 1. Bối cảnh & mục tiêu

Bản **v1.7.16** (commit `ca3f553`) đã làm dismiss banner lỗi địa chỉ đồng bộ đa máy qua Hub (tombstone +
merge theo mốc thời gian). Hub đã deploy (SQL trong `/opt/shopee-hub/Shopee.Hub.Web.dll` khớp nguyên văn
HEAD), client đã release v1.7.16. Build 0 warning, test 1479 + 49 xanh.

Review lại phát hiện **5 lỗi**, trong đó 1 lỗi vô hiệu hoá chính mục đích của banner:

**Lỗi 1 (nặng) — tombstone Hub cũ chôn banner mới ở local.**
`PickupAlertMerge.QuyetDinh` trả `LocalDismiss` **vô điều kiện** khi `hubDismissed=true`, không so mốc.
Bằng chứng: `PickupAlertHubDong.DismissedAt` được khai báo + rót dữ liệu ở `OrdersModuleHost.HubPush.cs`
nhưng **không chỗ nào đọc** — đúng field cần để bịt lỗ.

Kịch bản hỏng: shop lỗi địa chỉ lúc 10:00 khi Hub chết → local ghi banner active, đẩy Hub **thất bại**
(chỉ log). Hub vẫn giữ tombstone `dismissed_at=09:00` từ lần bấm X trước. 10:01 timer 60s GET thành công
→ `Dismissed=true` → `LocalDismiss` → **banner + dấu X đỏ biến mất dù địa chỉ vẫn đang lỗi**.

Nhánh ngược (local dismiss vs hub active) ĐÃ chặn kỹ bằng `hubCreatedAt`; chỉ nhánh này bị bỏ sót.

**Lỗi 2 — `LoadAddressAlertsFromLocal()` chạy ngoài UI thread.** Nhánh thoát sớm của
`SyncAddressAlertsFromHubAsync` (`fetch is null` = chế độ Shopee không có Hub, hoặc account rỗng email)
gọi thẳng, không bọc `RunOnUi`. Trước v1.7.16 hàm chỉ gọi từ UI thread nên vô hại; timer 60s mới gọi qua
`Task.Run` → `AddressAlertRows.Clear()` chạm ObservableCollection đang bind từ thread nền → WPF ném
exception, bị `catch` của timer nuốt (chỉ còn dòng Trace), lặp mỗi 60s.

**Lỗi 3 — `ToDictionary(..., OrdinalIgnoreCase)` có thể ném.** Khoá bảng local là
`PRIMARY KEY(account_id, shop_login)` collation BINARY → hai dòng khác hoa/thường tồn tại song song được
→ `ArgumentException` trong `RunOnUi` → `DispatcherUnhandledException` → popup "Lỗi" lặp 60s
(`e.Handled=true` nên không sập app).

**Lỗi 4 — sync đúp.** `OnSelectedRowChanged` đặt `DetailTabIndex = 1` (kích `OnDetailTabIndexChanged` →
sync lần 1) rồi tự gọi sync lần 2. Cả hai đều không qua cờ `_syncPickupAlertsBusy` (cờ chỉ áp cho timer).

**Lỗi 5 — Hub dismiss không so `created_at`.** Upsert được canh bằng `OccurredAt` nhưng dismiss thì
`dismissed_at=$d` vô điều kiện → bất đối xứng; một POST dismiss đang bay mà upsert mới hơn kịp đáp trước
sẽ bị chôn.

**Rác:** bảng `orders_pickup_alerts` trên Hub production còn dòng probe smoke test
`('hoangdh200392:muinx', 'probe.store', 'Test', …)` — shop giả dưới account login thật.

## 2. Phạm vi

**Làm:**

- Lỗi 1: `QuyetDinh` nhận thêm `localCreatedAt` + `hubDismissedAt`; thêm action `KeepLocalActiveRepushHub`;
  call site đẩy lại **upsert** lên Hub thay vì dismiss local.
- Lỗi 2: bọc `RunOnUi` cho nhánh thoát sớm.
- Lỗi 3: thay `ToDictionary` bằng `Dictionary` + `TryAdd` (giữ dòng mới nhất, không ném).
- Lỗi 4: dời cờ chống chồng vào chính `SyncAddressAlertsFromHubAsync` + coalesce (có lượt mới lúc đang chạy
  → chạy lại đúng 1 lần, không mất lượt khi đổi tài khoản giữa chừng).
- Lỗi 5: `DismissPickupAlert` bỏ qua khi `$d < created_at`.
- Test cho cả 5 điểm.
- Xoá dòng probe `probe.store` khỏi Hub production (backup `hub.db` trước).
- Deploy Hub + release client v1.7.17.

**Không làm:**

- KHÔNG đụng dòng `alina99.store` trên Hub (phản ánh lần bấm X thật của user).
- KHÔNG đổi luật bỏ qua shop khi `pickupOk=false`, không đổi Slack/`orders/app-alert`.
- KHÔNG làm SignalR/realtime; giữ nhịp kéo 60s.
- KHÔNG backfill lịch sử banner cũ.

## 3. Các bước thực hiện

### Bước 1 — Lỗi 1: merge tôn trọng banner local mới hơn tombstone

File: `orders/XuLyDonShopee.Core/Services/PickupAlertMerge.cs`

- Thêm enum `MergePickupAlertAction.KeepLocalActiveRepushHub` — giữ banner local đang hiện, đẩy lại
  **upsert** lên Hub để sửa tombstone cũ.
- Đổi chữ ký:
  ```csharp
  QuyetDinh(DateTime? localCreatedAt, DateTime? localDismissedAt,
            bool hubDismissed, DateTimeOffset? hubCreatedAt, DateTimeOffset? hubDismissedAt)
  ```
- Luật mới ở nhánh `hubDismissed`:
  - local đang **active** (`localDismissedAt is null` && `localCreatedAt is not null`)
    && `hubDismissedAt is not null` && `localCreatedAt > hubDismissedAt` → `KeepLocalActiveRepushHub`.
  - còn lại → `LocalDismiss` (giữ nguyên luồng lan dismiss đa máy).
- Nhánh `hubDismissed=false` giữ nguyên logic cũ.
- Gom chuẩn hoá `DateTime?` → UTC vào một helper dùng chung (tránh lặp `Kind switch`).

**Kiểm chứng lan dismiss KHÔNG vỡ:** hai máy cùng banner tạo 10:00, máy A bấm X 10:30 → Hub tombstone
10:30; máy B: `localCreatedAt=10:00 > 10:30` sai → `LocalDismiss`. Đúng như cũ.

File: `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.KetQua.cs`

- Truyền `local?.CreatedAt`, `dong.DismissedAt` vào `QuyetDinh`.
- Case `KeepLocalActiveRepushHub`: gom vào danh sách `repushUpsert` `(shop, province, occurredAt=local.CreatedAt)`,
  sau `RunOnUi` fire-and-forget `_services.UpsertPickupAlertToHub` qua `PickupAlertHubGate`, log khi fail
  (cùng kiểu với repush dismiss sẵn có).

### Bước 2 — Lỗi 2: nhánh thoát sớm về UI thread

Cùng file, trong `SyncAddressAlertsFromHubAsync`: bọc `RunOnUi(LoadAddressAlertsFromLocal)` ở nhánh
`fetch is null || accountLogin.Length == 0`.

### Bước 3 — Lỗi 3: dictionary không ném

Cùng file: thay `ListAll(...).ToDictionary(a => a.ShopLogin, a => a, StringComparer.OrdinalIgnoreCase)`
bằng vòng lặp + `TryAdd` (`ListAll` sắp `created_at DESC` → `TryAdd` giữ dòng **mới nhất**).

### Bước 4 — Lỗi 4: một cờ chống chồng cho mọi lối gọi

Cùng file:

- Tách thân hiện tại thành `private async Task SyncAddressAlertsOnceAsync()`.
- `SyncAddressAlertsFromHubAsync` thành lớp bọc: `Interlocked.CompareExchange` trên `_syncPickupAlertsBusy`;
  đang bận → đặt `_syncPickupAlertsPending = 1` rồi thoát; đang rảnh → vòng `do { pending=0; await Once(); }
  while (pending == 1)`, `finally` hạ cờ bận.
- `NhipSyncPickupAlerts` bỏ cờ riêng (dùng cờ chung), giữ kiểm tra tab + `SelectedRow`.

### Bước 5 — Lỗi 5: Hub dismiss không chôn banner mới hơn

File: `server/Shopee.Hub.Web/Data/HubDatabase.PickupAlerts.cs` — `DismissPickupAlert`, nhánh ON CONFLICT:

```sql
dismissed_at=CASE
  WHEN $d < orders_pickup_alerts.created_at THEN orders_pickup_alerts.dismissed_at
  ELSE $d
END,
updated_by_machine=$m;
```

Nhánh INSERT (chưa có dòng → tombstone) giữ nguyên.

### Bước 6 — Test

`orders/XuLyDonShopee.Tests/PickupAddressAlertsTests.cs`:

- Hub dismissed + local active **mới hơn** tombstone → `KeepLocalActiveRepushHub`.
- Hub dismissed + local active **cũ hơn** tombstone → `LocalDismiss` (lan dismiss đa máy còn nguyên).
- Hub dismissed + local đã dismiss → `LocalDismiss`.
- Hub dismissed + Hub thiếu `DismissedAt` → `LocalDismiss` (không giữ bừa).
- Cập nhật 4 test cũ theo chữ ký mới.

`server/Shopee.Hub.Web.Tests/PickupAlertsHubTests.cs`:

- Dismiss có `OccurredAt` **cũ hơn** `created_at` → banner vẫn active.
- Dismiss `OccurredAt` mới hơn `created_at` → dismiss ăn (chống hồi quy).

### Bước 7 — Build + test + deploy + release

- `dotnet build ShopeeSuite.sln` — 0 warning.
- `dotnet test` orders + hub — xanh.
- Backup `hub.db` trên VM rồi `DELETE FROM orders_pickup_alerts WHERE shop_login='probe.store'`.
- Publish + deploy `Shopee.Hub.Web.dll` lên `vps-muinx`, restart, health 200.
- Bump `version.txt` → **1.7.17** + CHANGELOG, chạy `release-suite.cmd`, cập nhật bản cài local.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` → 0 warning, 0 error.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` + `server/Shopee.Hub.Web.Tests` → xanh, không giảm số test cũ.
- [ ] Test mới chứng minh: Hub tombstone cũ + local active mới hơn → **giữ banner** (không còn `LocalDismiss`).
- [ ] Test mới chứng minh: Hub dismiss cũ hơn `created_at` → **không** xoá banner đang active trên Hub.
- [ ] `grep` xác nhận không còn `ToDictionary` ở đường merge; nhánh thoát sớm nằm trong `RunOnUi`.
- [ ] Hub production: `probe.store` biến mất, `alina99.store` còn nguyên; `curl https://api.schedra.net/health` → 200.
- [ ] `strings -el` trên DLL VM chứa `WHEN $d < orders_pickup_alerts.created_at`.
- [ ] GitHub Releases có v1.7.17; bản cài local lên 1.7.17.

## 5. Rủi ro & lưu ý

- **Đừng phá luồng lan dismiss đa máy** — đây là chức năng chính của v1.7.16. Mọi thay đổi nhánh
  `hubDismissed` phải giữ `LocalDismiss` khi tombstone Hub mới hơn banner local.
- **Lệch đồng hồ giữa các máy** đẩy quyết định về phía "hiện banner" (an toàn hơn giấu). Có thể ping-pong
  một lượt rồi hội tụ về trạng thái hiện banner — chấp nhận.
- So sánh ISO trên SQL là **lexicographic**: mọi mốc phải qua `ChuanHoaIso` (đã có) để cùng dạng UTC `"o"`.
- Xoá dữ liệu production: **backup `hub.db` trước**, `DELETE` đúng một `shop_login`, đọc lại bảng sau khi xoá.
- Deploy Hub **trước**, release client sau (client mới gửi thêm field; Hub cũ vẫn nhận được nhưng không canh).

---

## Báo cáo thực thi

### Đổi hướng ở Bước 1 và Bước 5 — plan ban đầu SAI

Bản vá đầu (commit `561797a`) làm đúng plan, build 0 warning, test 1485/51 xanh. Nhưng phản biện độc lập
chỉ ra hai lỗi nặng mà tôi đã **tự kiểm chứng lại trên code và xác nhận là đúng**:

1. **`localCreatedAt` không phải "mốc lỗi"** — `PickupAddressAlertsRepository.Upsert` ghi
   `created_at = UtcNow` mỗi lần, mà đường merge mirror-upsert lại chạy **mỗi 60s** khi Hub còn active. Mốc
   local do đó trôi theo giờ hiện tại. Luật mới đem mốc trôi đó so `hubDismissedAt` → máy nào vừa mirror xong
   sau lúc máy khác bấm X sẽ giữ banner **vĩnh viễn** (mỗi tick quyết định y hệt).
2. **So mốc chéo máy trong SQL dismiss** (`$d < created_at`) — hai mốc đến từ hai đồng hồ độc lập. Máy phát
   hiện lỗi chạy nhanh giờ ⇒ Hub **từ chối vĩnh viễn** lần bấm X của máy khác, banner không bao giờ gỡ được.
   Trước bản vá, dismiss vô điều kiện nên vẫn hội tụ.

Đánh giá lại mức nghiêm trọng của lỗi gốc (Lỗi 1 trong §1): nếu địa chỉ **vẫn lỗi thật**, vòng shop kế
(3–5 phút) phát hiện lại và upsert với mốc mới hơn `dismissed_at` ⇒ Hub bỏ tombstone ⇒ banner hiện lại ở mọi
máy. Tức Lỗi 1 chỉ làm **mất banner tạm một vòng và TỰ LÀNH**, trong khi bản vá đánh đổi bằng nguy cơ banner
**kẹt vĩnh viễn** — đắt hơn nhiều. Quyết định: **gỡ cả hai luật so mốc**, giữ nguyên tắc "bấm X LUÔN thắng".

### Lỗi thứ 6 phát hiện thêm (nặng, có từ v1.7.16 đang phát hành)

`RunOnUi` là `Dispatcher.BeginInvoke` (bất đồng bộ). Code gom `repushDismiss` **bên trong** callback rồi đọc
**ngay sau** khối đó — mà sau `await fetch(...).ConfigureAwait(false)` luồng luôn là threadpool ⇒ callback mới
chỉ được xếp hàng ⇒ list luôn rỗng. **Nhánh repush chưa từng chạy lần nào**, kể cả ở v1.7.16. Đã sửa: bắn
`DayLaiHub` ngay trong callback UI.

### Đã giao

| Hạng mục | Trạng thái |
|---|---|
| Lỗi 1 (tombstone cũ xoá banner mới) | **KHÔNG sửa** — chấp nhận, tự lành sau 1 vòng shop; lý do ghi trong xmldoc `PickupAlertMerge.QuyetDinh` |
| Lỗi 2 (`LoadAddressAlertsFromLocal` ngoài UI thread) | xong — bọc `RunOnUi` |
| Lỗi 3 (`ToDictionary` ném) | xong — `Dictionary` + `TryAdd` |
| Lỗi 4 (sync chồng) | xong — cờ busy + pending coalesce dùng chung mọi lối gọi |
| Lỗi 5 (Hub dismiss vô điều kiện) | **KHÔNG sửa** — cố ý, ghi rõ lý do trong xmldoc `DismissPickupAlert` |
| Lỗi 6 (repush chết) | xong — bắn trong callback UI |
| Ghi local trượt dòng khi shop lệch hoa/thường | xong — dùng `local?.ShopLogin ?? dong.ShopLogin` |
| Rác `probe.store` trên Hub production | xong — backup `hub.db.bak-20260804` rồi xoá đúng 1 dòng |

### Kiểm chứng

- `dotnet build ShopeeSuite.sln` → 0 warning, 0 error.
- `dotnet test orders` → 1482 passed (mốc trước 1479, +3 test merge).
- `dotnet test hub` → 51 passed (mốc trước 49, +2 test dismiss).
- Hub deploy lại + restart, `systemctl is-active` = active, health 200; `strings -el` xác nhận SQL dismiss đã
  về dạng vô điều kiện (0 hit cho luật `$d < created_at`).
- `orders_pickup_alerts` production còn đúng 1 dòng `alina99.store` (nguyên vẹn).

### Còn tồn

Chưa vá được **gốc** của Lỗi 1 bằng cách không đụng đồng hồ. Hướng đúng khi cần: bỏ so mốc, thay bằng
số hiệu bản ghi tăng dần (`rev`) do Hub cấp + cờ "chưa đẩy được lên Hub" ở local (outbox) — client chỉ nghe
Hub khi `rev` Hub mới hơn `rev` đã thấy, và không để tombstone Hub đè dòng local còn đang chờ đẩy. Việc này
cần thêm cột hai phía + migration, nên tách plan riêng.
