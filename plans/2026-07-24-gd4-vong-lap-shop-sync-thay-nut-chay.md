# Plan GĐ4: Vòng lặp MỌI shop + sync DB/GSheet/hub + thay nút "▶ Chạy" bằng cầu nối (liên tục)

- **Ngày:** 2026-07-24
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nền:** GĐ1-3 ĐẠT (cầu nối chạy trọn flow 1 shop: login → shop → đọc đơn → arrange → in phiếu → lưu PDF → revert địa chỉ, KHÔNG captcha).

## 1. Bối cảnh & mục tiêu

Cầu nối `OrdersBridgeSession` hiện CHỈ xử **shop ĐẦU** một lần rồi dừng (one-shot), và **chỉ log số đơn** — chưa lưu DB/GSheet/hub. GĐ4 ghép nốt để thành **nút Chạy thật, liên tục**, thay luồng Playwright seller-centre cũ:
- **4a. Lặp MỌI shop:** đọc danh sách shop → với TỪNG shop: openShopDetail → readToShip → syncOrders (đọc đơn) → xử đơn (arrange/in phiếu) → revert địa chỉ → đóng tab shop → về `/portal/shop` → shop kế.
- **4b. Sync dữ liệu:** đọc đơn xong mỗi shop → **lưu DB + GSheet + hub + notify** (tái dùng logic `AccountSession.SyncOrdersAsync` bước SAU-đọc).
- **4c. Liên tục + thay nút:** nghỉ 3-5' giữa shop; lặp lại cả chu kỳ theo `GetOrderIntervalMinutes()`; nút **"▶ Chạy"** production dùng cầu nối thay Playwright.

## 2. Kiến trúc ghép (CHỐT)

- `LoginSession.ParseOrdersJson` (dùng cho cả bridge lẫn Playwright) trả **cùng DTO** `SyncOrdersResult.Orders` (List đơn). ⇒ bridge đọc đơn (extension syncOrders) parse ra CÙNG kiểu DTO mà `AccountSession.SyncOrdersAsync` đang xử.
- **`OrdersBridgeSession` (Core) KHÔNG ref App** → không tự lưu DB được. Dùng **CALLBACK do App rót**: `Func<long shopId, IReadOnlyList<SyncedOrder>, CancellationToken, Task>` gọi sau khi đọc xong đơn mỗi shop; App implement callback = phần lưu DB/GSheet/hub.
- **Tách phần lưu** khỏi `AccountSession.SyncOrdersAsync`: hiện bước 2-8 (lọc `LaChuanBiHang`/existing → DetectNewlyDelivered → UpsertMany → MarkSoldCounted → RaiseOrdersChanged → RedownloadSlips → GSheet/hub/notify background) thao tác trên `result.Orders` (DTO). Trích thành method `PersistSyncedOrdersAsync(long shopId, IReadOnlyList<SyncedOrder> orders, CancellationToken ct)` — dùng lại cho CẢ đường Playwright cũ LẪN callback bridge. (RedownloadSlips dùng Playwright → với bridge BỎ bước này ở GĐ4 hoặc để bridge tự lo phiếu; xem rủi ro.)

## 3. Các bước (lát cắt)

### 4a — Bridge lặp MỌI shop
- `OrdersBridgeSession`: viết lại đuôi `RunLoginThenSliceAsync` (hoặc thêm `RunAllShopsAsync`): sau `atSellerCentre` + readShopList → **vòng qua từng shop** trong `shops`: openShopDetail(shopId) → readToShip → syncOrders → (callback sync) → nếu ToShip>0: setPickupAddress + loop prepareNextOrder + setPickupAddressToOther → **đóng tab shop** (thêm lệnh `closeShopTab` cho extension: `chrome.tabs.remove(shopTabId)` + về listTabId) → chờ nghỉ (tham số) → shop kế.
- Extension: thêm lệnh `closeShopTab` (đóng shopTabId, reset về tab picker). openShopDetail cần chắc chắn quay lại được picker giữa các shop (picker tab listTabId giữ nguyên; mở shop ở TAB MỚI như hiện tại → đóng tab đó là về picker).
- Kết quả trả: tổng hợp (số shop, tổng đơn, tổng phiếu) + đã gọi callback per-shop.

### 4b — Sync DB/GSheet/hub
- App: trích `PersistSyncedOrdersAsync` từ `AccountSession.SyncOrdersAsync` (giữ đường cũ gọi nó). Chữ ký nhận `shopId` + `orders` DTO + ct.
- Callback bridge (rót từ App layer nơi tạo `OrdersBridgeSession`): gọi `PersistSyncedOrdersAsync(shopId, orders, ct)`.
- `OrdersBridgeSession` ctor/RunAllShops nhận `Func<...>` callback; gọi sau syncOrders mỗi shop.
- Lưu ý: `_currentShopId` trong persist = shopId đang xử (bridge truyền vào), KHÔNG đọc field AccountSession.

### 4c — Liên tục + thay nút "▶ Chạy"
- Wire nút **"▶ Chạy"** (`Run` command / `AccountSessionManager.Start` → `AccountSession.RunAsync`) dùng **đường cầu nối**: login Playwright → clean+extension → RunAllShops (4a+4b) → nghỉ `GetOrderIntervalMinutes()` → lặp lại chu kỳ. GIỮ nút "🧪 Chạy thử" (one-shot) để test, hoặc gộp.
- Vòng đời: bridge session sống suốt (không đóng sau 1 shop); Dừng = TryKillPoc + cancel (đã có). Theo dõi "Chờ Lấy Hàng" mỗi chu kỳ.
- GỠ DẦN Playwright seller-centre: chưa xóa `SyncAllOrdersAsync`/`ProcessFirstOrderAsync` Playwright (giữ đường lui) nhưng nút Chạy KHÔNG còn gọi chúng.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH; `dotnet test` 916 giữ.
- [ ] node --check background.js.
- [ ] (Verify THẬT) Bấm "▶ Chạy" (hoặc Chạy thử) trên acc nhiều shop có đơn: chạy **qua TẤT CẢ shop**, mỗi shop đọc đơn + **lưu vào DB** (kiểm màn Đơn hàng có đơn) + arrange/in phiếu (shop có đơn chờ) + revert địa chỉ; nghỉ giữa shop; **lặp lại chu kỳ**; KHÔNG captcha. Dừng đóng được cả 2 trình duyệt.
- [ ] Đơn lưu DB đúng (lọc Chuẩn bị hàng), GSheet/hub push chạy (nếu cấu hình).

## 5. Rủi ro & lưu ý

- **RedownloadSlips (tải lại phiếu thiếu) dùng Playwright** trong SyncOrdersAsync → với bridge KHÔNG có LoginSession. GĐ4 tạm BỎ redownload ở đường bridge (phiếu đã lưu ngay lúc prepareNextOrder); hoặc port sang extension sau. Persist chỉ làm DB/GSheet/hub/notify.
- **DTO type khớp:** xác minh `ParseOrdersJson` trả đúng kiểu `SyncAllOrdersAsync` trả (SyncOrdersResult.Orders). Nếu bridge chỉ có List đơn (không có Pages/ReachedPageCap) thì bọc lại cho persist.
- **shopId nguồn:** bridge biết shopId (từ readShopList). Truyền vào persist làm `_currentShopId` để UpsertMany gắn đúng shop.
- **Đóng tab shop giữa các shop:** phải chắc về được picker (listTabId vẫn /portal/shop). Nếu picker cũng bị sticky-redirect giữa chừng → cần re-SSO (dùng lại gotoSellerCentre). Cân nhắc.
- **Vòng lặp liên tục:** chốt chặn thời gian/hard-cap như AccountSession cũ; Dừng phải cắt sạch.
- Mỗi lát cắt (4a → 4b → 4c) build + test; 4c test thật trên acc nhiều shop.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
