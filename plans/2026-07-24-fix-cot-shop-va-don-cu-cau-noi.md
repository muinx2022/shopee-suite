# Plan: Fix cột Shop (GSheet) + đơn cũ tích tụ ở đường cầu nối (rót shop-context)

- **Ngày:** 2026-07-24
- **Trạng thái:** hoàn thành (code + build + 916 test xanh + deploy + purge 906→0; chờ user verify thật)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nền:** GĐ4 đã deploy — nút "▶ Chạy" chạy qua cầu nối (`OrdersBridgeSession.RunAllShopsAsync` → callback `PersistSyncedOrdersAsync`).

## 1. Bối cảnh & chẩn đoán (ĐÃ soi code + DB thật)

Sau khi chuyển nút Chạy sang cầu nối, xuất hiện **2 lỗi CÙNG một gốc**:

1. **Đơn cũ tích tụ:** DB `app.db` có **906 đơn** (chỉ 13 "Chờ lấy hàng"; 893 đã kết thúc: 496 Đã hủy, 361 Đã giao, 27 Đã giao ĐVVC, 9 Đã nhận). Tất cả tạo trong 2 ngày (23–24/07) → chính đường cầu nối tích tụ. **901 đơn có `shop_id=NULL`** (chỉ 5 đơn mới nhất có shop_id thật). 543 đơn kết thúc **chưa từng gsheet-sync** → không "settled" → cleanup không xóa → tích tụ, tự nuôi qua filter `existing`.
2. **Cột "Tài khoản" (GSheet) sai:** ghi email account thay vì tên shop (do chuyển sang subaccount, 1 subaccount → nhiều shop).

**Gốc chung:** đường cầu nối **KHÔNG set 2 field** `_currentShopId` / `_currentShopLogin` của `AccountSession`. Cụ thể:
- Callback hiện tại: `(shopId, orders, c) => PersistSyncedOrdersAsync(shopId, orders, log, c)` — chỉ truyền `shopId` qua **tham số**.
- Nhưng `PersistSyncedOrdersAsync` gọi `StartGsheetPushInBackground(log, tok)` / `StartHubPushInBackground(...)` — các hàm này đọc **FIELD** `_currentShopId` (line ~1007) + `_currentShopLogin` (line ~1008), **KHÔNG** đọc tham số.
- Field `_currentShopId`/`_currentShopLogin` chỉ được set trong vòng lặp **Playwright cũ** (`RunAsync`, line ~2198–2199), KHÔNG set ở đường cầu nối.
- Hệ quả: `PushOrdersToGsheetAsync` chạy với `shopId=null` → `GetForGsheetPush(account, null)` (scope toàn account) + `tenShop = account.Email` (fallback). Đơn kết thúc không được đẩy/settle đúng shop → không xóa → tích tụ.

**Giá trị đúng cho cột Shop:** `ShopListItem.LoginName` (vd "alina99.store") — bridge ĐÃ có sẵn (`shop.LoginName` trong `RunAllShopsAsync`), chỉ chưa rót vào `_currentShopLogin`. (`ShopListItem.cs`: "LoginName … dùng làm cột Tên Shop trên Google Sheet".)

**Ghi chú cột GSheet:** header "Tài khoản → Shop" nằm ở **Google Apps Script / Sheet của user** (app chỉ gửi field JSON `tenShop`); phần code chỉ sửa **giá trị** `tenShop`. Không đụng hợp đồng JSON (giữ tên field `tenShop`).

**Quyết định dữ liệu của user:** *Xóa sạch, sync lại* — purge TOÀN BỘ bảng `orders` (kể cả Chờ lấy hàng) để chu kỳ sau bridge quét lại sạch. (Purge do Fable làm lúc deploy khi app đã tắt — KHÔNG thuộc phạm vi opus.)

## 2. Phạm vi

- **LÀM (code):** luồn `shopLogin` (tên shop) qua chuỗi cầu nối `RunAllShopsAsync/RunSliceCoreAsync → RunShopOrdersAsync → _syncCallback`; callback ở App set `_currentShopId` + `_currentShopLogin` TRƯỚC khi gọi `PersistSyncedOrdersAsync`. Nhờ đó GSheet lấy đúng tên shop + cleanup/GSheet scope đúng shop.
- **KHÔNG làm:** đổi hợp đồng JSON GSheet, đổi header cột (việc của user), đụng luồng Playwright `RunAsync` cũ, đổi chiến lược quét "Tất cả", đổi `MAX_ORDER_PAGES`, đụng extension `background.js` (không cần).

## 3. Các bước (2 file)

### B1. `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`

- **Đổi kiểu callback** thêm tham số `shopLogin`:
  - Field `_syncCallback`: `Func<string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>?`
    → `Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>?` (thêm `string shopLogin` làm THAM SỐ THỨ 2, sau `shopId`).
  - Tham số ctor `syncCallback` cùng kiểu mới; cập nhật `<param>` doc ("gọi SAU khi đọc xong đơn mỗi shop — App lưu DB/GSheet/hub, kèm tên shop").
- **`RunShopOrdersAsync`** đổi chữ ký: `RunShopOrdersAsync(string shopId, int toShip, CancellationToken ct)`
  → `RunShopOrdersAsync(string shopId, string shopLogin, int toShip, CancellationToken ct)`.
  - Chỗ gọi callback (line ~553): `_syncCallback(shopId, shopLogin, orders, ct)`.
- **Cập nhật 2 chỗ gọi `RunShopOrdersAsync`:**
  - Trong `RunAllShopsAsync` (line ~451): tính nhãn shop giống Playwright cũ và truyền vào:
    `var shopLogin = string.IsNullOrWhiteSpace(shop.LoginName) ? shop.ShopName : shop.LoginName;`
    → `await RunShopOrdersAsync(shop.ShopId, shopLogin, toShip ?? 0, ct)`.
    (Có thể tái dùng biến `shopName` sẵn có ở line ~431, NHƯNG `shopName` fallback về `ShopId`; ở đây fallback nên về `ShopName` cho đúng cột Shop. Dùng biến MỚI `shopLogin` như trên; giữ `shopName` cho log.)
  - Trong `RunSliceCoreAsync` (line ~525, đường "Chạy thử" — `_syncCallback` null nên giá trị không dùng nhưng chữ ký bắt buộc): truyền nhãn shop đầu:
    `var firstShopLogin = string.IsNullOrWhiteSpace(shops[0].LoginName) ? shops[0].ShopName : shops[0].LoginName;`
    → `await RunShopOrdersAsync(firstShopId, firstShopLogin, toShip ?? 0, ct)`. (Đảm bảo `shops` không rỗng ở điểm này — nếu code hiện có guard rỗng thì giữ; nếu chưa chắc, dùng `firstShopId` làm fallback: `shops.Count > 0 ? (... shops[0] ...) : firstShopId`.)

### B2. `orders/XuLyDonShopee.App/Services/AccountSession.cs`

- Trong `RunBridgeContinuousAsync` (line ~1797), đổi lambda `syncCallback` sang kiểu mới + **set 2 field trước khi persist**:
  ```csharp
  Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task> syncCallback =
      (shopId, shopLogin, orders, c) =>
      {
          // Rót shop-context để GSheet lấy đúng Tên Shop + cleanup/GSheet scope theo shop (giống vòng Playwright).
          _currentShopId = shopId;
          _currentShopLogin = string.IsNullOrWhiteSpace(shopLogin) ? null : shopLogin;
          return PersistSyncedOrdersAsync(shopId, orders, log, c);
      };
  ```
  - Lưu ý: `StartGsheetPushInBackground` chụp field ĐỒNG BỘ ngay khi được gọi (bên trong `PersistSyncedOrdersAsync`, trước await đầu) → set field ngay trước lời gọi là an toàn, không đua (mỗi shop chụp đúng giá trị của mình; các shop cách nhau 3–5' nghỉ).
  - KHÔNG đổi thân `PersistSyncedOrdersAsync` (vẫn nhận `shopId` tham số cho `UpsertMany`; giờ trùng khớp với field vừa set).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` giữ **916** test xanh (không sửa test nào; nếu có test chạm chữ ký callback thì báo lại — KHÔNG tự nới).
- [ ] Diff CHỈ gồm 2 file trên. Không đụng `background.js`, không đổi hợp đồng JSON GSheet.
- [ ] (Fable verify sau khi deploy + purge) Bấm "▶ Chạy": DB chỉ tích "Chờ lấy hàng" + theo dõi tới kết thúc rồi xóa; **không phình lại đơn cũ**; GSheet cột Shop = tên shop (vd "alina99.store"), KHÔNG phải email account.

## 5. Rủi ro & lưu ý

- **Chụp field lúc spawn:** cơ chế `StartGsheetPushInBackground` chụp `_currentShopId/_currentShopLogin` ngay tại thời điểm gọi (đồng bộ) — set-rồi-gọi là đúng thứ tự. Đừng chuyển việc set field vào task nền.
- **Đường "Chạy thử" (RunSliceCoreAsync):** callback null nên `shopLogin` không dùng, nhưng vẫn phải truyền để khớp chữ ký — đừng để null-ref khi `shops` rỗng.
- **Purge dữ liệu (Fable làm, ngoài phạm vi opus):** `DELETE FROM orders` khi app đã tắt (trong bước deploy). Opus KHÔNG đụng DB.
- **Không đổi** `MAX_ORDER_PAGES`/chiến lược quét — filter `existing || LaChuanBiHang` đã đúng; sau purge + fix scope, hệ tự hội tụ về chỉ giữ Chờ lấy hàng.

---

## Báo cáo thực thi

- **Code (opus-dev):** sửa đúng 2 file theo plan — `OrdersBridgeSession.cs` (kiểu callback + ctor + `RunShopOrdersAsync` thêm tham số `shopLogin`; `RunAllShopsAsync`/`RunSliceCoreAsync` truyền nhãn `LoginName` fallback `ShopName`) + `AccountSession.cs` (lambda `syncCallback` set `_currentShopId`/`_currentShopLogin` trước `PersistSyncedOrdersAsync`). Diff đúng phạm vi, không đụng file cấm.
- **Nghiệm thu Fable:** review diff khớp plan; guard `shops.Count==0` chặn trước `shops[0]` (an toàn); build Release publish OK; `dotnet test` **916 pass / 0 fail** (chạy độc lập).
- **Purge dữ liệu:** `DELETE FROM orders` khi app đã tắt → 906 → 0 đơn.
- **Deploy:** publish self-contained R2R → robocopy vào `…\Programs\ShopeeSuite` → mở lại app (2026-07-24).
- **Còn lại:** user bấm "▶ Chạy" verify: DB chỉ giữ Chờ lấy hàng + theo dõi tới kết thúc rồi xóa (không phình đơn cũ); cột Shop GSheet = tên shop (header "Tài khoản→Shop" user tự đổi bên Apps Script).
