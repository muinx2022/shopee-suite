# Plan: đồng bộ trạng thái kết thúc (Đã hủy / Đã giao) lên Hub và GSheet

- **Ngày:** 2026-07-26
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Người dùng báo hai triệu chứng, cùng một gốc:

1. **GSheet không tô đỏ đơn đã hủy.**
2. **Hub không cập nhật trạng thái hủy** — ảnh chụp `api.schedra.net/orders` cho thấy đơn `260726PKY6C28D`
   (thực tế đã hủy) vẫn hiện "Chờ lấy hàng".

Khảo sát đã xác định (bằng đọc code + soi CSDL hub thật):

### Lỗi A — Hub không bao giờ nhận trạng thái mới (đã xác nhận)

`OrdersRepository.UpsertMany` (nhánh UPDATE, quanh dòng 154–167) chỉ mở lại cờ đẩy hub khi **vận đơn** hoặc
**số tiền cuối cùng** vừa xuất hiện:

```sql
hub_synced_at = CASE WHEN (tracking_number IS NULL AND $tracking IS NOT NULL)
                       OR (final_amount IS NULL AND $finalAmount IS NOT NULL) THEN NULL ELSE hub_synced_at END
```

`GetForHubPush` chỉ lấy đơn `hub_synced_at IS NULL`. Đổi **trạng thái** không nằm trong danh sách ⇒ đơn đã đẩy
một lần thì mọi thay đổi trạng thái về sau (Đã hủy, Đã giao…) **không bao giờ** lên hub. Sau đó client dọn đơn
kết thúc khỏi máy (`NenXoaDonKetThuc`) ⇒ hub sai vĩnh viễn, không có đường sửa.

Bằng chứng CSDL hub (`/var/lib/shopee-hub/hub.db`): **không có đơn nào ở trạng thái kết thúc**; đơn từ 24/07 vẫn
"Chờ lấy hàng".

### Lỗi B — Đơn hủy mất vận đơn ⇒ GSheet bỏ qua, không tô đỏ

Cũng trong nhánh UPDATE nói trên: `tracking_number = $tracking` **ghi đè thẳng**, không `COALESCE` như
`final_amount`. Khi Shopee hủy đơn và danh sách "Tất cả" không còn hiện mã vận đơn, cột này bị xóa về NULL.

Khi đó ở `HubOutbox.cs` (khoảng dòng 316–327):

```csharp
if (daHuy && !coVanDon) { settled.Add(p.OrderSn); continue; }   // coi như xong, KHÔNG gửi gì
```

⇒ đơn hủy bị đánh dấu "xong" mà không gửi lên sheet, rồi bị dọn xóa. Dòng cũ trên sheet nằm nguyên màu trắng.

Nhánh này vốn dành cho đơn hủy **chưa từng vào sổ** (hủy trước khi vào pipeline giao — không muốn spam dòng đỏ),
nhưng nó không phân biệt "chưa từng ghi sheet" với "đã có dòng trên sheet rồi". Đơn chưa có vận đơn **vẫn được
ghi dòng trắng** (theo đúng comment hiện có), nên tình huống "đã có dòng, sau đó hủy" là có thật.

### Lỗi C — Hub sẽ bị xóa dữ liệu khi ta bắt đầu đẩy lại (phát sinh do fix A)

`HubDatabase.Orders.cs` (`ON CONFLICT(shop_id,order_sn) DO UPDATE`) ghi đè thẳng `final_amount`,
`final_amount_text`, `tracking_number`. Sau khi fix A, đơn hủy sẽ được đẩy lại — nếu lúc đó local không còn vận
đơn thì hub đang có vận đơn sẽ **bị xóa**. Phải chống mất dữ liệu ở hub trước khi bật đẩy lại.

**Mục tiêu:** trạng thái kết thúc của đơn phải lên tới cả hub lẫn GSheet, và không bên nào bị mất dữ liệu đã có.

## 2. Phạm vi

- **Làm:**
  - Client: mở lại cờ đẩy hub khi **trạng thái** đơn đổi.
  - Client: giữ vận đơn đã có (không để lượt sync sau xóa mất).
  - Client: nhánh "bỏ qua đơn hủy chưa có vận đơn" chỉ áp dụng khi đơn **chưa từng ghi sheet**.
  - Hub: upsert đơn không ghi đè NULL lên `final_amount` / `final_amount_text` / `tracking_number` đã có.
  - Test cho cả 4 điểm trên.
- **Không làm:**
  - KHÔNG đổi giao thức `/api/orders/push` (hub đã `DO UPDATE` đủ cột trạng thái — không cần thêm gì).
  - KHÔNG viết migration/backfill sửa các đơn đã kẹt sẵn trên hub: đơn kết thúc đã bị xóa khỏi client nên không
    còn nguồn để sửa. Đơn nào còn trong client sẽ tự đúng ở lượt sync kế.
  - KHÔNG đụng UI, không đổi Apps Script.
  - KHÔNG thêm xử lý dọn/hoà giải dữ liệu local ngoài các điểm nêu trên.

## 3. Các bước thực hiện

### Bước 1 — `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`: mở lại cờ hub khi trạng thái đổi

Trong nhánh UPDATE của `UpsertMany`, bổ sung điều kiện vào biểu thức `hub_synced_at`:

```sql
hub_synced_at = CASE WHEN (tracking_number IS NULL AND $tracking IS NOT NULL)
                       OR (final_amount IS NULL AND $finalAmount IS NOT NULL)
                       OR (COALESCE(status, '') <> COALESCE($status, ''))
                       OR (COALESCE(cancel_reason, '') <> COALESCE($cancelReason, ''))
                     THEN NULL ELSE hub_synced_at END,
```

Lưu ý bắt buộc:
- Trong `UPDATE` của SQLite, cột ở vế phải `SET` là **giá trị CŨ** → so cũ-với-tham-số-mới là đúng.
- **CHỈ so `status` và `cancel_reason`.** KHÔNG so `status_description` — mô tả này hay dao động
  (đếm ngược, nhắc nhở…) nên so nó sẽ làm đơn bị đẩy lại hub mỗi lượt sync, gây tải vô ích.
- Cập nhật khối comment ngay trên câu lệnh cho khớp hành vi mới (giải thích vì sao trạng thái đổi phải đẩy lại,
  và vì sao loại `status_description`).

### Bước 2 — cùng file: giữ vận đơn đã có

Đổi `tracking_number = $tracking` thành:

```sql
tracking_number = COALESCE($tracking, tracking_number),
```

Ghi comment: lượt sync này không đọc được vận đơn (đơn đã hủy nên danh sách không hiện, hoặc lỗi đọc) thì GIỮ mã
đã có, KHÔNG xóa — mất vận đơn kéo theo đơn hủy rơi vào nhánh bỏ-qua của GSheet và hub mất dữ liệu.

### Bước 3 — `orders/XuLyDonShopee.App/Services/HubOutbox.cs`: đơn đã có dòng trên sheet thì phải gửi để tô đỏ

Sửa điều kiện bỏ qua (khoảng dòng 322):

```csharp
if (daHuy && !coVanDon && !p.DaGhiSheet)
```

Nghĩa là: đơn hủy chưa có vận đơn **và chưa từng ghi sheet** → vẫn bỏ qua như cũ (không spam dòng đỏ vô nghĩa).
Đơn hủy **đã có dòng trên sheet** → đi tiếp xuống phần quyết định gửi, ở đó `huyDoi` sẽ bật và đơn được gửi lại
kèm `DaHuy: true` để Apps Script tô đỏ.

Cập nhật comment khối ngay trên đó cho khớp. Kiểm tra `GsheetPendingOrder` đã có thuộc tính `DaGhiSheet`
(suy từ `gsheet_synced_at`) — nếu tên khác thì dùng đúng tên đang có, không thêm cột mới.

### Bước 4 — `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs`: không ghi đè NULL lên dữ liệu đã có

Trong mệnh đề `ON CONFLICT(shop_id,order_sn) DO UPDATE SET`, đổi 3 cột:

```sql
final_amount = COALESCE($fa, final_amount),
final_amount_text = COALESCE($fat, final_amount_text),
tracking_number = COALESCE($tn, tracking_number),
```

Các cột còn lại giữ nguyên (trạng thái PHẢI ghi đè để đơn hủy cập nhật được). Ghi comment giải thích: đơn hủy
đẩy lại có thể không kèm vận đơn / số tiền cuối cùng — giữ giá trị hub đang có thay vì xóa.

### Bước 5 — Test

Thêm test (theo phong cách sẵn có trong `orders/XuLyDonShopee.Tests/`, tên hàm tiếng Việt không dấu):

1. `UpsertMany`: đơn đã có `hub_synced_at`, sync lại với **status đổi** (Chờ lấy hàng → Đã hủy) ⇒ `hub_synced_at`
   về NULL và `GetForHubPush` lấy được đơn đó.
2. `UpsertMany`: sync lại với status **y hệt** (chỉ đổi `status_description`) ⇒ `hub_synced_at` GIỮ nguyên
   (không đẩy lại vô ích).
3. `UpsertMany`: đơn đang có `tracking_number`, sync lại với tracking NULL ⇒ vẫn giữ mã cũ.
4. `UpsertMany`: `cancel_reason` từ NULL → có giá trị ⇒ `hub_synced_at` về NULL.
5. Hàm quyết định gửi GSheet: đơn hủy, không vận đơn, **đã** ghi sheet ⇒ được gửi kèm `DaHuy = true`;
   đơn hủy, không vận đơn, **chưa** ghi sheet ⇒ vẫn bỏ qua. Nếu logic này nằm trong thân `HubOutbox` khó test
   trực tiếp thì test qua đường đang có sẵn của repo (đừng refactor lớn chỉ để test — nếu không test được thì
   ghi rõ trong báo cáo, đừng tự ý tách kiến trúc).

### Bước 6 — Build & chạy test

- `dotnet build` solution → 0 lỗi (cảnh báo hiện có giữ nguyên, không phát sinh mới).
- `dotnet test` → toàn bộ xanh (mốc hiện tại **1008 test**, sau bước 5 sẽ tăng).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution: 0 error.
- [ ] `dotnet test`: 100% xanh, số test > 1008, có đủ các ca ở Bước 5.
- [ ] Đọc lại `UpsertMany`: `hub_synced_at` reset khi `status` HOẶC `cancel_reason` đổi; KHÔNG reset khi chỉ
      `status_description` đổi; `tracking_number` dùng `COALESCE`.
- [ ] Đọc lại `HubOutbox`: nhánh bỏ qua có thêm `&& !p.DaGhiSheet`; đơn hủy đã ghi sheet đi tới nhánh gửi.
- [ ] Đọc lại `HubDatabase.Orders.cs`: 3 cột `final_amount` / `final_amount_text` / `tracking_number` dùng
      `COALESCE`; `status` / `status_description` / `cancel_reason` vẫn ghi đè thẳng.
- [ ] Comment ở cả 3 file đã cập nhật khớp hành vi mới (không để lại comment mô tả hành vi cũ).

## 5. Rủi ro & lưu ý

- **Đừng so `status_description`** khi quyết định đẩy lại hub — đây là bẫy chính, sẽ gây đẩy lại liên tục.
- Fix Bước 1 làm số "việc dở" (đếm qua `CountForHubPush`) tăng lên ở lượt sync đầu sau khi cập nhật — đúng ý đồ,
  không phải lỗi; không cần chặn.
- `NenXoaDonKetThuc` giữ nguyên: nó đã yêu cầu `p.DaDayHub` trước khi xóa, nên sau fix, đơn hủy sẽ được giữ lại
  đến khi đẩy hub xong rồi mới dọn — đúng thứ tự mong muốn.
- Đơn kết thúc đã bị xóa khỏi client từ trước KHÔNG sửa được trên hub — chấp nhận, đã ghi ở phần Không làm.
- Sau khi merge cần **deploy hub** (Bước 4 chạm `server/`) rồi mới release client — nhưng việc deploy/release do
  Fable làm, agent KHÔNG tự deploy, KHÔNG tự commit, KHÔNG bump version.

---

## Báo cáo thực thi

Đã làm đúng 6 bước. File sửa:

- `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs` — `UpsertMany`: thêm `status` + `cancel_reason` vào điều
  kiện reset `hub_synced_at` (KHÔNG có `status_description`); `tracking_number = COALESCE($tracking, tracking_number)`.
- `orders/XuLyDonShopee.App/Services/HubOutbox.cs` — nhánh bỏ qua thành `daHuy && !coVanDon && !p.DaGhiSheet`.
- `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs` — `DO UPDATE` dùng `COALESCE` cho `final_amount`,
  `final_amount_text`, `tracking_number`; các cột trạng thái vẫn ghi đè thẳng.
- Test: 4 ca mới ở `OrdersRepositoryTests.cs` + file mới `HubOutboxGsheetHuyTests.cs` (2 ca, dựng Web App giả
  trên loopback với cổng 0 nên không đụng cổng cố định).

Nghiệm thu (Fable tự chạy): `dotnet build` cả `ShopeeSuite.sln` và `server/ShopeeHub.sln` → 0 error, 0 warning;
`dotnet test` → **1014/1014 xanh** (trước: 1008). Diff khớp plan từng dòng.

Hạn chế đã biết: đơn kết thúc bị dọn khỏi client từ trước không sửa được trên hub — không còn nguồn để đẩy lại.
