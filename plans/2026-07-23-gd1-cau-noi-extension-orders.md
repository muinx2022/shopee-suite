# Plan GĐ1: Cầu nối extension↔C# cho Đơn hàng + lát cắt kiểm chứng (chưa port business logic)

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Thuộc:** roadmap `plans/2026-07-23-orders-extension-migration-roadmap.md` (GĐ0 ĐẠT). Nối tiếp POC `2026-07-23-poc-mo-sach-*.md`.

## 1. Bối cảnh & mục tiêu

Sau GĐ0 (nút "Mở sạch" né captcha), user chốt: biến thành **nút Chạy acc thật**, làm đủ dây chuyền (đăng nhập → list shop → Chi tiết → đọc đơn → chuẩn bị hàng → sync) nhưng qua extension để không captcha. Đây là port GĐ1-4.

**Kiến trúc đã chốt (hybrid, khác Search):** extension điều khiển trang + **tự bắn trusted input qua `chrome.debugger`** (GĐ0 đã chứng né captcha); C#↔extension trao đổi lệnh/dữ liệu qua **WebSocket** (chép khuôn `WebSocketServer` của Search). **KHÔNG mở `--remote-debugging-port`** (Search mở cổng này để C# bắn input — ta KHÔNG theo, vì cổng CDP mở trên Seller Centre chưa kiểm chứng, rủi ro captcha; GĐ0 an toàn chính nhờ không có cổng này). Tức C# KHÔNG cần `CdpInputController`/`CdpSession` — nhẹ hơn Search.

**Mục tiêu GĐ1 (CHỈ hạ tầng + 1 lát cắt kiểm chứng — KHÔNG port login/verify/process/sync):** dựng cầu nối và **chứng minh chạy thật, captcha-free, một lát cắt dọc**: người dùng (đã đăng nhập tay tới `/portal/shop`) bấm chạy thử → extension đọc danh sách shop → C# nhận → extension bấm "Chi tiết" shop đầu bằng trusted click (KHÔNG captcha) → đọc số "Chờ Lấy Hàng" → C# nhận + log. Nếu lát cắt này chạy sạch → kiến trúc chuẩn, GĐ2+ port từng bước business lên nền này. Tái dùng **hàm parse thuần** sẵn có (không viết lại logic).

**Phân tầng giữ nguyên (KHÔNG chạm ở GĐ1, và hầu hết cả dự án):** DB (`OrdersRepository`), GSheet, Hub hook, Notify, proxy/quản lý phiên, đọc/ghi file phiếu, và **luồng mở/đăng nhập Hotmail-Outlook** (domain Microsoft, không phải Shopee — có thể giữ Playwright riêng ở GĐ2). Tầng App (`AccountSession`) đã tách sạch khỏi Playwright: nhận DTO → xử lý. GĐ1 chưa đụng tầng này.

## 2. Phạm vi

- **Làm (GĐ1):**
  1. C# bridge trong `orders/XuLyDonShopee.Core`: `OrdersWebSocketServer` (chép khuôn Search) + mở rộng launcher truyền `wsPort` qua URL hash + `OrdersBridgeSession` (vòng đời: cấp cổng → start WS → launch sạch → chờ `ready` → gửi lệnh → nhận kết quả).
  2. Extension `extensions/shopee-orders` (tiến hoá từ `shopee-orders-test`): nối WS, xử lý 3 lệnh `readShopList` / `openShopDetail{shopId}` / `readToShip`, dùng `chrome.scripting.executeScript({world:'MAIN'})` (port `ScanShopListJs` + đọc to-do box) + `chrome.debugger` trusted click cho "Chi tiết"; báo `ready`/`pageData`/`progress`/`captcha`/`error`.
  3. Trigger tạm để test: đổi nút "🧪 Mở sạch" (hoặc thêm nút "🧪 Chạy thử bridge") gọi lát cắt: open → readShopList → openShopDetail(shop đầu) → readToShip, đổ kết quả vào panel log của acc.
- **KHÔNG làm (để GĐ2-4):**
  - KHÔNG port đăng nhập subaccount/verify-email (GĐ1 giả định user đã đăng nhập tay, hồ sơ acc có phiên).
  - KHÔNG port "Chuẩn bị hàng"/đặt địa chỉ/in phiếu/sync đơn.
  - KHÔNG đụng `OpenAsync`/luồng Playwright production, KHÔNG đụng nút "▶ Chạy" cũ (đường lui).
  - KHÔNG mở `--remote-debugging-port`, KHÔNG chép `CdpInputController`/`CdpSession`.
  - KHÔNG bỏ code Playwright cũ.

## 3. Các bước thực hiện

### Hạng mục A — C# bridge (Core)

**A1. `orders/XuLyDonShopee.Core/Services/OrdersWebSocketServer.cs` (MỚI)** — chép khuôn `suite/Shopee.Module.Search/Engine/WebSocketServer.cs`, đổi namespace `XuLyDonShopee.Core.Services`:
- `HttpListener` prefix `http://localhost:{port}/` → `AcceptWebSocketAsync` → giữ 1 socket mới nhất.
- Gom frame tới `EndOfMessage`, `JsonDocument.Parse`, raise `MessageReceived(JsonDocument)`; events `Connected`/`Disconnected`; prop `IsConnected`.
- `SendAsync(object)` camelCase, `DefaultIgnoreCondition=WhenWritingNull`, có `_sendLock`.
- Cấp cổng: có `PortAllocator` chưa? Kiểm tra `orders/`; KHÔNG có thì dùng cách đơn giản — chọn cổng trống bằng `TcpListener(IPAddress.Loopback,0)` lấy port rồi nhả. (GĐ1 một phiên/lần test là đủ; đa-lane để sau.)

**A2. `orders/XuLyDonShopee.Core/Services/BraveLaunchArgs.cs`** — cho `BuildCleanPocArgs` nhận thêm khả năng nhúng `wsPort` vào startUrl. Cách gọn: tầng gọi tự dựng `startUrl = "https://banhang.shopee.vn/portal/shop#_od_ws=<wsPort>"` rồi truyền vào `BuildCleanPocArgs` như cũ (KHÔNG cần sửa hàm — chỉ đổi startUrl ở nơi gọi). Giữ nguyên: không remote-debugging-port, không proxy, có load-extension. (Xác nhận: hash `#...` KHÔNG làm Chromium coi là arg lạ — an toàn.)

**A3. `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs` (MỚI)** — vòng đời một phiên bridge kiểm chứng:
- ctor nhận `userDataDir`, `browserChoice`.
- `RunSliceAsync(ct)`: cấp `wsPort` → `new OrdersWebSocketServer(wsPort)` + `.Start()` → đăng ký `MessageReceived` → launch trình duyệt sạch qua `PocCleanLauncher` (đổi để nhận `startUrl` có hash `#_od_ws={wsPort}`; giữ tính chất sạch) → chờ message `ready` (timeout ~30s) → gửi tuần tự: `{action:"readShopList"}` → nhận `pageData` shop list (parse qua `LoginSession.ParseShopListJson`) → `{action:"openShopDetail", shopId:<đầu>}` → nhận `progress`/`captcha` → `{action:"readToShip"}` → nhận `pageData` ToShip (parse `ShopeeDashboard.ParseToShipCount`) → trả `OrdersBridgeSliceResult{Shops, FirstShopId, ToShipCount, Captcha, Error}`.
- Expose callback/log để App đổ ra panel log; giữ `Process` để kill.
- Message-in xử lý ĐỒNG BỘ trước khi JsonDocument bị dispose (bài học từ Search `OnMessage`).

**Ghi chú tái dùng:** `LoginSession.ParseShopListJson` (public wrapper `ShopeeLoginService.ParseShopListJson`, `ShopeeLoginService.cs:361`), `ShopeeDashboard.ParseToShipCount` (`ShopeeDashboard.cs:15`) — dùng lại NGUYÊN, không viết lại.

### Hạng mục B — Extension `extensions/shopee-orders` (MỚI, tiến hoá từ shopee-orders-test)

Tạo thư mục `extensions/shopee-orders/` (KHÔNG sửa `shopee-orders-test` — giữ làm POC tham chiếu). Các file:

**B1. `manifest.json`** — MV3: `permissions:["debugger","tabs","scripting"]`, `host_permissions:["https://banhang.shopee.vn/*"]`, `background.service_worker="background.js"`, `content_scripts` match `banhang.shopee.vn/*` (content.js có thể no-op như Search, logic ở background). name "Shopee Orders Bridge".

**B2. `background.js`** — service worker:
- Đọc `wsPort` từ URL hash `#_od_ws=` của tab active (query tabs, đọc `tab.url` hash) hoặc lưu `chrome.storage`; nối `ws://localhost:<wsPort>`; onopen → `send({action:"ready"})`.
- `onmessage` → switch `action`:
  - `readShopList`: `chrome.scripting.executeScript({target:{tabId}, world:'MAIN', func: scanShopList})` — hàm `scanShopList` port từ `ScanShopListJs` (`ShopeeLoginService.cs:2587`) trả JSON `[{rowKey,name,login}]` → `send({action:"pageData", kind:"shopList", data})`.
  - `openShopDetail{shopId}`: executeScript đọc `getBoundingClientRect` của `tr[data-row-key='<id>'] button` khớp text "chi tiet"/"detail" (logic từ `OpenShopDetailAsync`) → `chrome.debugger` bắn trusted click tại toạ độ (dùng lại cơ chế `trustedClick` của shopee-orders-test background.js) → theo dõi tab mới (`chrome.tabs.onCreated`/query) là Seller Centre shop → `send({action:"progress", message:"opened shop"})`; nếu URL rơi vào `/verify/` → `send({action:"captcha"})`.
  - `readToShip`: trên tab shop, executeScript đọc to-do box `.to-do-box-item`/`.item-desc`=="Chờ Lấy Hàng"/`.item-title` (logic từ `ReadToShipCountAsync`/`FindToShipTitleAsync`) → `send({action:"pageData", kind:"toShip", data:{raw}})`.
- Giữ hàm `trustedClick` qua `chrome.debugger` (attach→Input.dispatchMouseEvent→detach) từ shopee-orders-test.
- Dò `/verify/` sau mỗi điều hướng → `captcha`.

**B3. `content.js`** — no-op (khai báo đủ manifest), theo khuôn Search.

### Hạng mục C — Trigger test (App)

Sửa `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: command `MoSachPoc` (hoặc thêm `ChayThuBridge`) thay vì chỉ mở trình duyệt thì gọi `OrdersBridgeSession.RunSliceAsync` với hồ sơ acc đang chọn (công thức profile-dir như hiện tại), đổ kết quả (shop list count, first shopId, ToShip, captcha?) vào `_services.Log.Append(email, ...)`. Đổi nhãn nút ở `AccountsView.axaml` thành "🧪 Chạy thử (bridge)". Giữ `TryKillPoc`/khoá hồ sơ như cũ. `ResolveOrdersExtension` cần trỏ được `extensions/shopee-orders` (thêm/đổi để tìm thư mục mới; giữ tương thích test cũ hoặc thêm hàm `ResolveOrdersBridgeExtension`).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` XANH (916 cũ giữ nguyên) + test thuần cho phần mới nếu có (vd parse message).
- [ ] Diff đúng phạm vi: Core thêm `OrdersWebSocketServer.cs`, `OrdersBridgeSession.cs`, sửa nhẹ `BraveLaunchArgs`/`PocCleanLauncher` (nhận startUrl có hash); App sửa `AccountsViewModel.cs`+`AccountsView.axaml`; extension mới `extensions/shopee-orders/*`. KHÔNG đụng `OpenAsync`/nút "▶ Chạy".
- [ ] (Verify THẬT do user, sau khi deploy) Đăng nhập tay tới `/portal/shop` → bấm "🧪 Chạy thử (bridge)": panel log hiện (a) đọc được N shop, (b) mở "Chi tiết" shop đầu **KHÔNG captcha**, (c) đọc được số "Chờ Lấy Hàng". Đây là cổng GĐ1 → mở khoá GĐ2.

## 5. Rủi ro & lưu ý

- **`chrome.debugger` giữ lâu:** GĐ0 chỉ chứng cú click ngắn. GĐ1 nhiều thao tác → cần xác nhận attach/detach lặp hoặc giữ attach KHÔNG gây captcha. Nếu banner "đang gỡ lỗi" phiền nhưng không captcha thì chấp nhận. Đây chính là điều lát cắt GĐ1 kiểm chứng.
- **Tab mới khi "Chi tiết":** Seller Centre shop mở tab mới (như `OpenShopDetailAsync` bắt tab mới). Extension phải theo tab mới (onCreated/query theo URL `banhang.shopee.vn` + shopId) và các lệnh sau (`readToShip`) chạy trên tab đó. Cần truyền/nhớ `tabId` shop.
- **wsPort qua hash:** Chromium giữ hash khi load; extension đọc `location.hash`. Nếu Shopee điều hướng làm rụng hash → cần fallback (lưu `chrome.storage` lúc first-load, như Search `ReinjectWsPortAsync`). GĐ1 làm bản đơn giản trước, ghi nhận nếu rụng.
- **Không đa-lane ở GĐ1:** một phiên/lần test. `PortAllocator` đa-lane để GĐ sau nếu cần chạy nhiều acc song song.
- **ResolveExtension:** đảm bảo trỏ đúng `extensions/shopee-orders` (bản mới) cạnh exe khi deploy — nhắc bước publish kèm thư mục extension mới.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
