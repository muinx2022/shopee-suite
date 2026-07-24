# Plan GĐ3: Port đọc + xử đơn 1 shop sang extension (trên nền cầu nối GĐ1+GĐ2 đã đạt)

- **Ngày:** 2026-07-24
- **Trạng thái:** hoàn thành — **VERIFIED THẬT 2026-07-24 (1 shop end-to-end, KHÔNG captcha):** đọc đơn → đặt địa chỉ (3 checkbox) → Chuẩn bị hàng → in phiếu → **lưu PDF OK** → hết đơn → set địa chỉ về địa chỉ khác. Bẫy đã vượt: helper `_na` world:MAIN (inject qua execInTab), tiêu đề modal `.eds-modal__title`, lưu phiếu = extension fetch blob trong tab awbprint (cookie) → base64, giữ debugger attach xuyên suốt, nút In phiếu scope trong modal (né link order list), sticky-shop (SSO qua /account). GĐ4 kế tiếp: vòng lặp mọi shop + sync + thay nút Chạy cũ.
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nền:** GĐ1+GĐ2 ĐẠT (login Playwright → clean+extension đọc shop + Chi tiết KHÔNG captcha). Xem roadmap.

## 1. Bối cảnh & mục tiêu

Cầu nối đã chứng minh: trình duyệt sạch + extension vào được Seller Centre của shop, đọc "Chờ Lấy Hàng", KHÔNG captcha. GĐ3 port các thao tác XỬ ĐƠN 1 shop sang extension (executeScript đọc DOM + `chrome.debugger` trusted click/type + bắt tab phiếu). Tái dùng **hàm thuần** sẵn có (ShopeeShippingNav, ScanOrdersJs, ParseOrdersJson) — chỉ viết lại phần "chạm trang".

**Làm theo 2 lát cắt (Opus làm lần lượt trong cùng plan):**
- **Phần A — ĐỌC đơn (read-only, test được NGAY):** port `SyncAllOrdersAsync` → lệnh extension `syncOrders`: vào "Tất cả" → quét bảng đơn (`ScanOrdersJs`) → phân trang → trả danh sách đơn. Không đụng "Chuẩn bị hàng". Test được kể cả shop 0 đơn chờ (đọc lịch sử đơn).
- **Phần B — XỬ đơn (cần shop có đơn chờ để test):** lệnh `setPickupAddress{province}` + `prepareNextOrder{invoiceDir}` (Chuẩn bị hàng → chọn Bưu cục → xác nhận → In phiếu giao → bắt tab phiếu → lưu PDF). C# lặp `prepareNextOrder` tới hết đơn.

C# side lưu DB/GSheet/hub GIỮ NGUYÊN (đã tách khỏi Playwright, nhận DTO) — GĐ4 mới ghép; GĐ3 chỉ trả DTO ra log để nghiệm thu.

## 2. Phạm vi

- **Làm:**
  - Extension: lệnh `syncOrders`, `setPickupAddress`, `prepareNextOrder` + page-funcs port từ C# (ScanOrdersJs, tìm nút/tab/modal theo ShopeeShippingNav). Trusted click/type + bắt tab phiếu (awbprint) qua `chrome.tabs.onCreated`.
  - C#: `OrdersBridgeSession` thêm `RunShopOrdersAsync` (chạy trên tab shop đang mở SAU openShopDetail): syncOrders → (nếu ToShip>0) setPickupAddress + loop prepareNextOrder → trả DTO (danh sách đơn + số phiếu lưu). Parse qua `LoginSession.ParseOrdersJson` (thêm forwarder nếu cần) + `ShopeeShippingNav`.
  - Trigger test: nút "🧪 Chạy thử" nay chạy tới đọc đơn (Phần A) — log số đơn đọc được; Phần B thêm sau.
- **KHÔNG làm (GĐ4):** ghép vào vòng lặp shop production, sync DB/GSheet/hub thật, thay nút "▶ Chạy" cũ. KHÔNG đụng `OpenAsync`/`TryLoginSubaccountAsync`/luồng Playwright seller-centre cũ.

## 3. Các bước

### Phần A — Extension `syncOrders` + C# đọc (test được ngay)

**A1. `extensions/shopee-orders/background.js`** — thêm:
- page-func `pageScanOrders()`: port `ScanOrdersJs` (`ShopeeLoginService.cs:5080-5176`) — đọc `a[data-testid='order-item']`, `.order-sn`, `.buyer-username`, `.item*`, `.total-price`, `.status*`, `.tracking-number`, `.eds-popover__content` → JSON mảng đơn. Tự chứa (world MAIN).
- page-func `pageLocateOrderTab(reSrc)`: tìm tab "Tất cả"/"Chờ lấy hàng" (`IsAllTabText`/`IsToShipTabText`) → toạ độ (để trusted click).
- page-func `pageFindNextPage()`: tìm nút "trang sau" còn bật (`FindNextPageButtonAsync` logic) → toạ độ hoặc null.
- lệnh `syncOrders`: trên tab shop (`shopTabId`), điều hướng tới `/portal/sale/order` (chrome.tabs.update) → chờ load → click tab "Tất cả" (trusted) → vòng: `pageScanOrders` → gom, dò trang sau → trusted click "trang sau" → chờ danh sách đổi → lặp (chốt chặn ~10 trang). Trả `{action:"pageData", kind:"orders", data:<json mảng gộp>}`. Dò `/verify` → captcha.

**A2. `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`**:
- Thêm `RunShopOrdersAsync(ct)` (gọi SAU khi đã openShopDetail thành công trong slice, HOẶC nối vào RunSliceCoreAsync): gửi `syncOrders` → nhận `pageData kind=orders` → parse `ShopeeLoginService.ParseOrdersJson` (thêm forwarder `internal static` như ParseShopListJson nếu chưa có) → trả số đơn + list.
- Slice hiện tại (RunSliceCoreAsync) sau readToShip → gọi thêm bước đọc đơn, log "Đọc được N đơn (Tất cả)". (Giữ Phần B tách, thêm sau.)

**A3. Test Phần A:** bấm "🧪 Chạy thử" → log: 3 shop → Chi tiết → Chờ Lấy Hàng → **Đọc được N đơn** — KHÔNG captcha.

### Phần B — Xử đơn (làm sau khi A xanh; cần shop có đơn chờ)

**B1. Extension** — thêm:
- `setPickupAddress{province}`: mở "Cài Đặt Vận Chuyển" → tab "Địa Chỉ" (port `OpenShippingAddressSettingsAsync`/`SetPickupAddressAsync`, dùng `ShopeeShippingNav.IsShippingSettingText/IsAddressTabText/AddressDetailMatchesProvince/IsPickupTagText/IsEditButtonText/IsSetPickupCheckboxText/IsSaveButtonText/IsConfirmButtonText`). Trusted click qua các bước modal.
- `prepareNextOrder{invoiceDir}`: về `/portal/sale/order` tab "Chờ lấy hàng" → tìm đơn đầu có nút "Chuẩn bị hàng" (`IsPrepareOrderButtonText`) → trusted click → modal "Giao Đơn Hàng" (`IsShipOrderModalTitle`) → chọn "tự mang tới Bưu cục" (`IsDropoffTitleText`) → "Xác nhận" (`IsConfirmArrangeButtonText`) → modal "Thông Tin Chi Tiết" (`IsDetailModalTitle`) → "In phiếu giao" (`IsPrintSlipButtonText`) trusted click → **bắt tab phiếu (window.open→awbprint) qua `chrome.tabs.onCreated`** → lấy URL PDF → gửi URL về C# để C# tải+lưu (C# `SaveSlipAsync` tái dùng: fetch PDF, kiểm magic `%PDF-`, tên = mã đơn). Trả `{action:"orderPrepared", orderCode, slipTabUrl}` hoặc `{action:"noOrder"}`.

**B2. C#** — `RunShopOrdersAsync` mở rộng: nếu ToShip>0 → `setPickupAddress(province)` → loop `prepareNextOrder(invoiceDir)` tới `noOrder`/chốt chặn; mỗi phiếu C# tải PDF từ `slipTabUrl` (tái dùng logic SaveSlipAsync — có thể chép sang) lưu vào invoiceDir. Log số đơn xử + số phiếu lưu.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH; `dotnet test` 916 giữ.
- [ ] node --check background.js.
- [ ] Diff đúng phạm vi: extension + OrdersBridgeSession + (nếu cần) forwarder ParseOrdersJson trong ShopeeLoginService. KHÔNG đụng OpenAsync/nút Chạy cũ.
- [ ] (Verify THẬT — Phần A) Chạy thử → log đọc được N đơn từ "Tất cả", KHÔNG captcha.
- [ ] (Verify THẬT — Phần B, khi có shop đơn chờ) prepareNextOrder chạy trọn 1 đơn: Chuẩn bị hàng → chọn Bưu cục → in phiếu → lưu PDF hợp lệ, KHÔNG captcha.

## 5. Rủi ro & lưu ý

- **Bắt tab phiếu (awbprint):** "In phiếu giao" mở tab mới bằng `window.open` → extension nghe `chrome.tabs.onCreated` bắt tab awbprint, lấy URL, C# tải. `--disable-popup-blocking` đã có trong BuildCleanPocArgs? Kiểm — nếu chưa, THÊM để tab phiếu mở được.
- **SW cache / cổng cố định / kill-by-profile / poll bảng:** đã xử ở GĐ1+GĐ2 (PrepareFreshExtensionCopy + KillBrowsersOnProfile + poll). Các page-func mới cũng nên poll chờ phần tử render.
- **Trusted type địa chỉ:** setPickupAddress có thể cần gõ/tick — dùng `chrome.debugger` dispatchKeyEvent (đã có dbg helper ở GĐ2? — nếu đã gỡ khi pivot thì THÊM lại `withDebugger`/`dbgType` cho phần này).
- **Test Phần B cần shop có đơn "Chờ Lấy Hàng">0** — account test hiện 0 đơn; Fable/user chuẩn bị shop có đơn khi test B.
- Mỗi thao tác 1 lệnh + parser; giữ best-effort per-đơn (1 đơn lỗi không phá cả lượt) như code cũ.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
