# Plan: Đợt 1+2 — Extensions shopee-search + shopee-scrape: sửa bug + dọn code chết

- **Ngày:** 2026-07-25
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Plan cha:** `plans/2026-07-25-ke-hoach-refactor-toan-app.md` (mục 1C + 2F)

## 1. Bối cảnh & mục tiêu

3 extension trong `extensions/` làm cầu nối automation né anti-bot. Plan này CHỈ đụng `extensions/shopee-search/` và `extensions/shopee-scrape/` — extension `shopee-orders/` và `shopee-orders-test/` do plan khu orders xử lý (tránh conflict). User đã chốt: XOÁ máy pause/resume của search (C# không bao giờ gửi lệnh này).

Bối cảnh giao thức: search nhận lệnh từ C# qua WS cổng 9111 (`suite/Shopee.Module.Search/Engine/SearchOrchestrator.cs` chỉ gửi `action: "start"` và `"stop"`); scrape nhận lệnh qua CDP gọi global `__launcher*` (`suite/Shopee.Module.MultiBrave/Engine/ExtensionRunnerAutomation.cs`), kênh message nội bộ là fallback. KHÔNG đổi phía C# trong plan này.

## 2. Phạm vi

- **Làm:** các hạng mục dưới trong `extensions/shopee-search/` và `extensions/shopee-scrape/`.
- **Không làm:** KHÔNG đụng `shopee-orders/`, `shopee-orders-test/`, mọi file C#; không tách module background.js (đợt 4); không làm `extensions/shared/` (đợt 3).

## 3. Các bước thực hiện

### Bước 1 — Fix race reconnect của shopee-search (bug: run tự khởi động lại)

`extensions/shopee-search/background.js:17-36` (`connectWs`): (a) `onclose` lên lịch reconnect 3s nhưng không lưu/huỷ timer; (b) `connectWs` không guard socket đang OPEN/CONNECTING. Kịch bản lỗi: tab reload → `onUpdated` (dòng ~2444) gọi `connectWs` tạo socket mới, timer cũ nổ → thay luôn socket đang sống → `onopen` gửi `ready` lần nữa → C# (`SendPendingSearchOnReady`) gửi lại `start` → run đang chạy bị stop + restart. Sửa theo mẫu guarded của shopee-orders: biến `reconnectTimer` (huỷ trước khi đặt mới), đầu `connectWs` có `if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;`.

### Bước 2 — Reject `_gestPending` khi WS đóng

`shopee-search/background.js:57-80`: gesture `cdpInput` đang bay khi socket rớt sẽ treo đủ 30s timeout mỗi cú. Sửa: trong `onclose`, duyệt `_gestPending` reject/resolve-fail toàn bộ entry (để flow rơi ngay xuống fallback synthetic), rồi clear map.

### Bước 3 — Token hoá waiter kết quả của shopee-scrape (bug: báo ok/fail nhầm dòng)

`extensions/shopee-scrape/background.js:964-973`: `SCRAPE_RESULT` shift waiter đầu hàng FIFO, trong khi `__launcherExecuteScrapeStep` tạo waiter mới trước mỗi lần re-inject (`:680, 729, 763, 769`) → kết quả cũ đến muộn resolve nhầm waiter mới. Sửa: gắn token (vd `rowNumber + '-' + nonce tăng dần`) truyền vào content script khi inject (qua `args` của `executeScript`), content script gửi kèm token trong `SCRAPE_RESULT`; background chỉ resolve waiter có token khớp, kết quả token lạ thì bỏ + log. Nhớ cập nhật cả `extensions/shopee-scrape/content.js` phía gửi kết quả.

### Bước 4 — Selector text-match trước, index sau (shopee-search)

`shopee-search/background.js:1696` và `:1861` (`sortButtons[2]` = nút "Bán chạy"), `:792` (`opts[2]`): hiện index đứng trước, Shopee đổi thứ tự nút là click nhầm âm thầm. Sửa: đảo ưu tiên — match theo text (đã có sẵn logic text ở gần đó) trước, index chỉ làm fallback cuối + log khi phải dùng fallback. GIỮ NGUYÊN cách click/delay (anti-detect) — chỉ đổi cách CHỌN element.

### Bước 5 — Dọn code chết shopee-search

Đã grep C# xác nhận không ai gửi các lệnh này:
- Máy pause/resume: handler `pause`/`resume` (`:100-101`) + `waitWhilePaused` (`:107-116`) + toàn bộ ~10 điểm gọi `await waitWhilePaused()` trong 3 flow (xoá lời gọi, giữ nguyên logic xung quanh).
- `send({action:'shopInfo', name})` (`:355`) — C# không có consumer.
- `DELAY_MS` (`:3`), `getPageHtml()` (`:935-951`), `rawSleep` (`:2380`) — 0 caller.
- Lưu `state.filters` (các dòng `:121,131,298,391`) — không hàm nào đọc (lọc làm ở C#); bỏ việc lưu, KHÔNG đổi payload phía C#.
- `content.js` là no-op có chủ đích — xoá file + gỡ khỏi `manifest.json`.

### Bước 6 — Dọn code chết shopee-scrape

- `CAPTCHA_WAIT_TIMEOUT_MS` (`:15`), `CAPTCHA_CHECK_INTERVAL_MS` (`:16`), `isInjectableUrl` (`:97-106`) — 0 caller (captcha dùng `CAPTCHA_MANUAL_WAIT_MS`/`CAPTCHA_POLL_MS`).

### Bước 7 — Kiểm tra

- `node --check` từng file .js đã sửa (nếu máy không có node: soát tay + nhờ Fable chạy). Manifest hợp lệ (JSON parse).
- Grep trong extension: không còn tham chiếu tới symbol đã xoá.

## 4. Tiêu chí nghiệm thu

- [ ] `connectWs` có guard OPEN/CONNECTING + reconnectTimer được huỷ trước khi đặt mới; không còn đường nào tạo socket thứ 2 khi socket sống.
- [ ] WS đóng → mọi entry `_gestPending` bị reject ngay (đọc code xác nhận).
- [ ] `SCRAPE_RESULT` chỉ resolve waiter có token khớp; content.js gửi kèm token.
- [ ] Nút sort chọn theo text trước, index là fallback có log.
- [ ] Grep `waitWhilePaused|shopInfo|getPageHtml|rawSleep|DELAY_MS` trong `shopee-search/` = 0 hit; 3 const thừa của scrape đã xoá; search manifest không còn content script.
- [ ] `node --check` pass mọi file sửa.

## 5. Rủi ro & lưu ý

- Đây là code anti-detect: KHÔNG đổi delay/easing/thứ tự thao tác chuột-phím; bước 4 chỉ đổi cách chọn element.
- Xoá `waitWhilePaused` phải xoá ĐÚNG các dòng `await waitWhilePaused();` — không kéo theo dòng lân cận.
- Token bước 3: content script của scrape được inject nhiều lần — bảo đảm listener message trong content không đăng ký chồng (giữ nguyên guard `__shopee27052026ScrapeClickerInjected` hiện có).
- KHÔNG đụng `shopee-orders/` (plan orders đang sửa file đó song song).

---

## Báo cáo thực thi (Opus điền sau khi xong)
