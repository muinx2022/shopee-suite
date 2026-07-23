# Roadmap: Chuyển tự-động-hoá Đơn hàng (Seller Centre) từ Playwright → Extension để né anti-bot

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm (GĐ0 — kiểm chứng)
- **Người lập:** Fable
- **Nhánh:** sẽ dùng nhánh riêng cho từng giai đoạn

## 1. Vấn đề & quyết định

Chạy thật: đăng nhập subaccount → list shop OK, nhưng **bấm "Chi tiết" vào Seller Centre bị Shopee đá sang
`shopee.vn/verify/captcha` và captcha KHÔNG tải được** ("Lỗi tải") — Shopee chặn hẳn. Đổi Chrome/Edge, đổi
profile đều vô ích ⇒ **nguyên nhân: Playwright lái trang qua CDP bị anti-bot Shopee soi** ở cửa Seller Centre
(gắt nhất). Chrome tay không bị.

**Quyết định user (2026-07-23):** đi hướng **Extension** — làm giống module Search (chạy Shopee ổn định).

## 2. Cơ chế Search (khuôn mẫu — đã khảo sát)

- **Extension MV3** (`extensions/shopee-search`): content script + background service worker, match
  `https://shopee.vn/*`. Background nối **WebSocket `ws://localhost:<wsPort>`** tới app C#.
- **Đọc/logic DOM** làm bằng `chrome.scripting.executeScript({world:'MAIN', func})` — chạy TRONG trang như
  một extension người dùng bình thường ⇒ **KHÔNG bị soi** như Playwright attach page (Runtime.enable...).
- **Thao tác chuột/phím "thật"**: extension đọc toạ độ (getBoundingClientRect) rồi gửi `{kind:'cdpInput',
  op:'click'/'wheel'/'moveTo'/'type', x, y}` về C#; **C# bắn TRUSTED input qua CDP `Input.dispatchMouseEvent`
  /`Input.dispatchKeyEvent`** (kênh `cdpGesture`), ack lại. CDP CHỈ dùng cho input + vòng đời trình duyệt,
  KHÔNG lái trang.
- **Điều hướng**: `chrome.tabs.update({url})`. **Phát hiện verify/captcha** qua URL `/verify/` → báo C#.
- **Phía C#** (`Shopee.Module.Search/Engine`): `WebSocketServer` (server), `CdpInputController` (bắn input
  trusted), `BraveManager` (launch `--load-extension` + `--remote-debugging-port` + truyền wsPort),
  `SearchOrchestrator`/`SearchSession` (gửi lệnh `start/stop/pause/resume`, nhận `progress/captcha/done/...`).

## 3. Ràng buộc lớn

- Module **Đơn hàng (XuLyDonShopee) KHÔNG ref suite** (chỉ `XuLyDonShopee.Core → Shopee.Proxy.Kiot`). Nên
  `WebSocketServer`/`CdpInputController`/`BraveManager` của Search **không dùng lại trực tiếp được** — phải
  CHÉP/PORT sang orders (hoặc dựng project shared, nhưng chép gọn hơn cho việc này).
- Toàn bộ tự-động-hoá Seller Centre hiện nằm trong `ShopeeLoginService.cs` (~5000 dòng Playwright): đọc
  "Chờ Lấy Hàng", vào shop, đọc danh sách đơn, "Chuẩn bị hàng"/đặt địa chỉ lấy hàng, sync "Tất cả", tải/in
  phiếu, đọc chi tiết đơn... **Mỗi thao tác này phải viết lại bằng extension JS** (executeScript + cdpGesture).
  Đây là phần TO — làm dần theo giai đoạn.
- Extension mới phải match **`banhang.shopee.vn/*`** (Search match `shopee.vn/*`) + có thể cả
  `subaccount.shopee.com/*`, `accounts.shopee.vn/*`.

## 4. Giai đoạn

### GĐ0 — KIỂM CHỨNG (làm TRƯỚC, gate cả dự án) ⭐

**Mục tiêu:** chứng minh cơ chế extension **thật sự né được captcha ở bước "Chi tiết"** — TRƯỚC khi bỏ công
port toàn bộ. Nếu extension mở shop vẫn dính captcha → DỪNG, tính hướng khác (đỡ phí hàng tuần).

- Chép tối thiểu sang orders: `WebSocketServer` + `CdpInputController` + launcher `--load-extension` (mẫu
  `BraveManager`), hoặc dựng một POC riêng.
- Extension POC match `banhang.shopee.vn/*`: nối WS, nhận lệnh "mở shop <shopId>", làm bằng
  `executeScript` (tìm nút "Chi tiết" của dòng `tr[data-row-key]`, đọc toạ độ) + `cdpGesture` click trusted;
  hoặc `chrome.tabs.update` sang URL shop.
- **Chạy thật:** đăng nhập sẵn (tay cũng được) tới `/portal/shop`, cho POC mở một shop.
  - **Không captcha → cơ chế OK → sang GĐ1.**
  - **Vẫn captcha → báo user, DỪNG dự án extension, bàn hướng khác** (vd API seller).
- Không cần đẹp/đầy đủ — chỉ đủ để trả lời "có né được không".

### GĐ1 — Hạ tầng cầu nối trong orders (nếu GĐ0 đạt)

- Cổng hoá `WebSocketServer` + `CdpInputController` + `OrdersBraveManager` (launch Brave/Edge/Chrome +
  `--load-extension` + CDP input) vào `XuLyDonShopee.Core`/`.App`. Vòng đời phiên: launch → extension `ready`
  → C# gửi lệnh. Giữ proxy/profile như cũ.
- Extension `extensions/shopee-orders` khung: nối WS, keep-alive, phát hiện verify/captcha/lỗi mạng, kênh
  `cdpInput`, protocol lệnh↔kết quả (JSON).

### GĐ2 — Port luồng đăng nhập + vào shop

- subaccount login (điền form, mở hộp thư — phần mở hộp thư có thể GIỮ Playwright vì hotmail không bị
  Shopee soi; chỉ phần trên trang Shopee mới cần extension) → "Tài khoản của tôi"/"Kênh Người bán" →
  `/portal/shop` → đọc danh sách shop → mở từng shop (bước GĐ0 đã chứng).

### GĐ3 — Port xử lý đơn 1 shop

- Đọc "Chờ Lấy Hàng"; vào "Tất cả"; "Chuẩn bị hàng" + đặt địa chỉ lấy hàng; sync đơn (đọc bảng, phân
  trang); tải/in phiếu. Mỗi cái = một lệnh extension + parser kết quả về C#. Giữ nguyên tầng lưu DB/GSheet/hub.

### GĐ4 — Vòng lặp shop + thay thế đường Playwright cũ

- `AccountSession.RunAsync` gọi orchestrator extension thay cho `ShopeeLoginService` (Playwright). Gỡ dần
  code Playwright seller-centre (giữ lại tới khi extension thay đủ). Test 774+ vẫn xanh (điều chỉnh).

## 5. Nguyên tắc

- **Không xoá code Playwright cho tới khi extension thay được từng phần** (giữ đường lui mỗi giai đoạn).
- Mỗi giai đoạn: nhánh riêng → plan chi tiết → opus thực thi → nghiệm thu → merge → (nếu cần) release test thật.
- GĐ0 là CỔNG: chưa qua GĐ0 thì KHÔNG làm GĐ1+.
- Phần KHÔNG trên trang Shopee (hotmail lấy mã, DB, GSheet, hub) giữ nguyên — chỉ đổi tầng chạm trang Shopee.

---

## Nhật ký giai đoạn

- GĐ0: (chưa bắt đầu)
