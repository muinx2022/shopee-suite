# Plan: Cầu nối lấy "Số tiền cuối cùng" (cột Ước tính) qua mở chi tiết đơn

- **Ngày:** 2026-07-24
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nền:** Nút "▶ Chạy" chạy qua cầu nối extension. Sync chỉ quét LIST đơn ("Tất cả") → có buyer/status/total/carrier/**tracking** (list card có `.tracking-number`), NHƯNG **KHÔNG có "Số tiền cuối cùng"** (cột Ước tính) vì field này CHỈ có ở **trang chi tiết đơn**.

## 1. Bối cảnh & mục tiêu

Luồng Playwright cũ lấy "Số tiền cuối cùng" bằng `FetchFinalAmountsForPageAsync`: với mỗi đơn "Chờ lấy hàng"/"Chuẩn bị hàng" CHƯA có final → mở CHI TIẾT (`/portal/sale/order/{shopeeOrderId}`) → đọc `[type='FinalAmount'] .amount` → parse VND → gán `FinalAmount`/`FinalAmountText`. Cầu nối **chưa port bước này** → cột Ước tính luôn rỗng + GSheet không có.

**Mục tiêu:** port bước "mở chi tiết đọc Số tiền cuối cùng" sang cầu nối. Chỉ mở chi tiết cho đơn ĐANG chuẩn bị (`LaChuanBiHang`) CHƯA có final (bỏ đơn đã có ở DB — tránh mở lại mỗi chu kỳ). Đọc xong MERGE vào DTO TRƯỚC khi callback persist (một lần upsert, không cần update lần 2).

**KHÔNG đụng tracking:** list card ĐÃ có `.tracking-number` (đã xác nhận qua DOM thật) — extension đọc đúng; đơn vừa arrange chưa gán mã thì bắt ở chu kỳ kế. Không sửa gì cho tracking.

## 2. Kiến trúc ghép

- **Selector final (đã có, tái dùng nguyên):** `FinalAmountJs` = ưu tiên `[type='FinalAmount'] .amount`; fallback tìm phần tử text `=== "Số tiền cuối cùng"` rồi lần ≤4 cấp cha tìm `.amount`. (Xem `ShopeeLoginService.cs:5289`.)
- **URL chi tiết:** `https://banhang.shopee.vn/portal/sale/order/{shopeeOrderId}` (shopeeOrderId đã có trong DTO từ scan — href `/portal/sale/order/(\d+)`).
- **Chỉ mở cho đơn cần:** `ShopeeShippingNav.LaChuanBiHang(status)` && `FinalAmount is null` && `!finalDoneSns.Contains(OrderSn)` && `ShopeeOrderId` khác rỗng. (`finalDoneSns` = tập order_sn ĐÃ có final trong DB, App cấp — mirror `ordersWithFinalAmount` của Playwright.)
- **Parse:** `ShopeeShippingNav.ParseVndAmount(finalText)` → `long?`; set `order.FinalAmount` + `order.FinalAmountText = finalText`.

## 3. Các bước

### B1. Extension `background.js`
- Const: `const ORDER_DETAIL_PREFIX = "https://banhang.shopee.vn/portal/sale/order/";`
- page-func `pageReadFinalAmount()` (world MAIN, TỰ CHỨA): port nguyên `FinalAmountJs` sang JS thuần (norm inline). Trả chuỗi text (rỗng nếu chưa thấy).
- Lệnh `syncOrderFinals` (input `cmd.orders` = mảng `{orderSn, shopeeOrderId}`; chốt chặn ~30 đơn/lượt, log nếu cắt):
  - Với mỗi đơn: `const t = await chrome.tabs.create({ url: ORDER_DETAIL_PREFIX + shopeeOrderId, active: false });`
    → `waitTabComplete(t.id, 20000)` → nếu url `/verify` → gom cờ captcha, đóng tab, `send captcha` rồi dừng lượt.
    → POLL `pageReadFinalAmount` (≤15s, 500ms) tới khi khác rỗng → lấy finalText.
    → `chrome.tabs.remove(t.id)` (LUÔN đóng, kể cả lỗi — try/catch).
  - Trả `{ action:"pageData", kind:"finals", data: JSON.stringify([{orderSn, finalText}, ...]) }`.
  - Best-effort per-đơn: 1 đơn lỗi → finalText rỗng, tiếp đơn kế (KHÔNG phá lượt). Giữ `shopTabId` là tab shop (đừng đổi active sang tab chi tiết — tạo active:false).
- Đăng ký lệnh trong dispatch (chỗ `case "syncOrders":` …): thêm `case "syncOrderFinals": await doSyncOrderFinals(cmd.orders || []); return;`.

### B2. Core `OrdersBridgeSession.cs`
- Thêm field + ctor param `Func<IReadOnlySet<string>>? finalDoneSns = null` (SAU `syncCallback`) — tập order_sn đã có final (bỏ qua). Cập nhật `<param>` doc.
- Thêm `_finalsTcs` (TaskCompletionSource<string?>) + xử `pageData kind=finals` trong bộ nhận message (chỗ đang xử `kind=="orders"`/`kind=="shopList"`): `_finalsTcs?.TrySetResult(data)`. Dò `action=="captcha"` như các lệnh khác.
- Trong `RunShopOrdersAsync`, SAU `var orders = ParseOrdersJson(...)` và TRƯỚC `_syncCallback(...)`:
  - Tính `done = finalDoneSns?.Invoke() ?? EmptySet`.
  - `var needFinal = orders.Where(o => ShopeeShippingNav.LaChuanBiHang(o.Status) && o.FinalAmount is null && !string.IsNullOrWhiteSpace(o.ShopeeOrderId) && !done.Contains(o.OrderSn)).ToList();`
  - Nếu `needFinal.Count > 0`:
    - `_finalsTcs = NewTcs<string?>();`
    - `await _ws.SendAsync(new { action="syncOrderFinals", orders = needFinal.Select(o => new { orderSn=o.OrderSn, shopeeOrderId=o.ShopeeOrderId }) });`
    - `var finalsJson = await _finalsTcs.Task.WaitAsync(TimeSpan.FromSeconds(20 + 20*needFinal.Count), ct);` (đủ thời gian mở nhiều tab; hoặc trần cứng 300s). Nếu `_captchaSeen` → log + bỏ bước final (vẫn persist phần đã có).
    - Parse finalsJson (mảng `{orderSn, finalText}`), map orderSn → finalText; với mỗi order khớp: `order.FinalAmount = ShopeeShippingNav.ParseVndAmount(finalText); order.FinalAmountText = string.IsNullOrWhiteSpace(finalText) ? null : finalText;` (chỉ set khi finalText khác rỗng).
    - Log `"Lấy Số tiền cuối cùng: {got}/{needFinal.Count} đơn."`
  - RỒI mới `await _syncCallback(shopId, shopLogin, orders, ct)` (persist DTO đã có final).
- Best-effort: lỗi bước final (timeout/parse) → log + tiếp tục persist (KHÔNG ném, KHÔNG phá vòng).

### B3. App `AccountSession.cs`
- Trong `RunBridgeContinuousAsync`, chỗ `new OrdersBridgeSession(userDataDir, browserChoice, log, invoiceDir, province, syncCallback)` → THÊM đối số cuối:
  `finalDoneSns: () => _services.Orders.GetOrderSnsWithFinalAmount(_accountId)`.
  (Method này đã có — trả tập order_sn có final_amount khác NULL của account.)

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH; `dotnet test orders/XuLyDonShopee.Tests` giữ **925** xanh (không phá test).
- [ ] `node --check extensions/shopee-orders/background.js` OK.
- [ ] Diff đúng phạm vi 3 file. KHÔNG đụng tracking/scan list, KHÔNG đụng luồng Playwright.
- [ ] (Fable verify thật sau deploy) Sync 1 shop có đơn "Chờ lấy hàng": cột **Ước tính** có số (Số tiền cuối cùng), GSheet có cột tương ứng; KHÔNG captcha khi mở chi tiết; các đơn đã có final không bị mở lại (log got < tổng pending các chu kỳ sau).

## 5. Rủi ro & lưu ý

- **Captcha khi mở chi tiết:** trình duyệt SẠCH (extension, không CDP lái) nên mở `/portal/sale/order/{id}` như người thật — kỳ vọng KHÔNG captcha (Playwright cũ chỉ dính captcha ở CỬA Seller Centre, không ở trang chi tiết). Vẫn dò `/verify` để dừng an toàn. VERIFY thật.
- **Số tab:** mỗi đơn pending mở 1 tab chi tiết rồi đóng (active:false, tuần tự). `finalDoneSns` chặn mở lại đơn đã có final → chỉ đơn pending MỚI mới mở. Chốt chặn 30 đơn/lượt.
- **shopTabId:** tạo tab chi tiết `active:false` + luôn `chrome.tabs.remove` sau đọc → không lạc tab thao tác. Đừng để `_dbgTab`/debugger dính tab chi tiết (bước này KHÔNG cần trusted click — chỉ navigate + đọc DOM).
- **Đơn "Đã hủy"/khác:** `LaChuanBiHang` loại sẵn → không mở chi tiết đơn đã hủy/đã giao (đúng như Playwright).
- **DTO:** xác minh `SyncedOrder` có `ShopeeOrderId` (từ scan) + `FinalAmount`/`FinalAmountText` set được (Playwright đã gán trực tiếp → OK).

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
