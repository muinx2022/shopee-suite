# Plan: `extensions/shared/` — khử trùng lặp 3 extension (3G)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (chờ phiên chính nghiệm thu — xem "Báo cáo thực thi" cuối file)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

3 extension (`shopee-search` 2471 dòng, `shopee-scrape` 984, `shopee-orders` ~2000 sau B1) chép tay lẫn nhau: ws-bridge (reconnect), tab-wait, verify/network-error detect (2 danh sách marker bổ khuyết nhau), dbg-input (trustedClick/dbgClick), sleep/rand. Hiện trạng manifest (30/07): search đã `"type":"module"`; scrape + orders còn classic service worker; orders có content_scripts (content.js 15 dòng gửi 'wake').

Ràng buộc vận hành: extension nạp THẲNG từ thư mục repo qua `--load-extension` (cả dev lẫn máy client sau release) → import tương đối ra ngoài thư mục extension KHÔNG hoạt động ⇒ mô hình: **nguồn chuẩn ở `extensions/shared/`, mỗi extension giữ bản copy checked-in trong thư mục nó, script sync tự chép + kiểm drift**.

## 2. Phạm vi

- **Làm:** như dưới. Khu: `extensions/**` + `release-suite.cmd`/`release-suite.sh` (thêm bước sync-check) + (nếu cần) script `tools/`.
- **Không làm:** KHÔNG hợp nhất 3 extension làm một (khác footprint quyền — chủ đích); KHÔNG đổi hành vi/delay các thao tác (anti-bot); KHÔNG đụng C# ngoài việc không có gì phải đổi (đường load giữ nguyên thư mục từng extension).

## 3. Các bước thực hiện

1. Chuyển `shopee-scrape` + `shopee-orders` manifest sang `"type":"module"` (MV3 service worker module; content_scripts của orders không ảnh hưởng). Kiểm tra không dùng `importScripts` sẵn có.
2. Tạo `extensions/shared/` với các module ES, nguồn lấy từ bản TỐT NHẤT hiện có (đối chiếu cả 3 trước khi viết):
   - `ws-bridge.js` — mẫu reconnect có guard socket-sống + huỷ timer của orders (`shopee-orders/background.js` sau fix 1C.1/1C.2 các bản đã fix nằm ở search — lấy bản ĐÃ FIX làm chuẩn), tham số hoá port + handler.
   - `tab-wait.js` — bản scrape (chuẩn nhất theo review 25/07).
   - `net-detect.js` — verify/network-error markers: tập HỢP của 2 danh sách hiện có (ghi bảng nguồn từng marker).
   - `dbg-input.js` — trustedClick/dbgClick/… (bản orders đang sống; search/scrape dùng phần nào lấy phần đó).
   - `util.js` — sleep/rand.
3. Mỗi extension: import từ bản copy nội bộ (vd `shopee-search/shared/ws-bridge.js`); XOÁ code trùng tại chỗ. Hành vi từng extension phải giữ nguyên — chỗ nào 2 bản lệch tham số (delay, số retry) thì tham số hoá theo bản của extension đó.
4. Script sync: `extensions/sync-shared.cmd` + `.sh` (copy `extensions/shared/*.js` → `shopee-*/shared/`); gắn vào `release-suite.cmd`/`.sh` TRƯỚC bước đóng gói; thêm mode `--check` (so hash, exit≠0 nếu drift) và gọi mode check trong release script.
5. `node --check` cả 3 background.js + các module.

## 4. Tiêu chí nghiệm thu

- [ ] `node --check` sạch toàn bộ file JS đổi.
- [ ] 3 manifest đều `"type":"module"`; extension load được (không kiểm tra tay được thì tối thiểu: cấu trúc import đúng, không tham chiếu importScripts).
- [ ] Grep các hàm trùng cũ (reconnect chép tay, sleep/rand bản cục bộ) = 0 ngoài shared/ + bản copy.
- [ ] `sync-shared --check` pass; release-suite.cmd/.sh có bước sync + check.
- [ ] Báo cáo: bảng "module nào lấy từ bản nào, extension nào lệch gì đã tham số hoá".

## 5. Rủi ro & lưu ý

- Chạy SAU B1 (B1 đang sửa `shopee-orders/background.js`).
- Đổi manifest sang module là thay đổi runtime thật — soi kỹ scrape/orders không dùng API chỉ có ở classic worker; nếu extension nào chuyển module gặp rào thật sự → giữ classic + dùng `importScripts` bản copy, ghi rõ.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Trạng thái: xong.** Nền: `deac3f4` (worktree bị cắt từ `0d7918c` → đã `git merge --ff-only main` theo bước 0).

### Module shared lấy từ bản nào

| Module | Nguồn chuẩn | Ai dùng | Điểm lệch đã tham số hoá |
|---|---|---|---|
| `util.js` | search (rand/randInt/clamp), orders+scrape (sleep) | cả 3 | search cần sleep NHÂN `sessionPace` → thêm `pacedSleep(paceGetter)`; scrape/orders dùng `sleep` thường |
| `ws-bridge.js` | search (bản ĐÃ FIX: huỷ timer treo, guard socket-sống cùng cổng, gỡ handler khi thay socket, `ws !== sock` ở mọi handler) + `try/catch` quanh `new WebSocket` của orders | search, orders | `reconnectDelayMs` (search 3000, orders 1200); `onOpen`/`onMessage`/`onClose`/`onPortChange` là callback (search reject `_gestPending` + lưu `_wsPort` vào storage.local; orders chỉ gửi `ready`, tự lưu `storage.session`) |
| `tab-wait.js` | scrape (`waitForTabComplete` nghe sự kiện) + search (`waitForUrl`/`waitForUrlChange`) | cả 3 | `timeoutMs`/`pollMs`/`sleep` truyền vào (search truyền sleep có nhịp để giữ đúng hành vi cũ) |
| `net-detect.js` | GỘP danh sách marker của search + scrape; `isVerifyUrl` lấy bản anchored-pathname của scrape | search, scrape | `world` (search MAIN, scrape ISOLATED) + `onInjectError` (search false, scrape true) |
| `dbg-input.js` | orders (bản đang chạy) | orders | `moveDelayMs`/`pressDelayMs` (mặc định 70/50 = số cũ) |

Mô hình copy: nguồn chuẩn `extensions/shared/`, mỗi extension có bản copy checked-in `shopee-*/shared/`; `extensions/sync-shared.cmd|.sh` chép + `--check` so `fc /b` / `cmp` (exit 1 nếu lệch). `release-suite.cmd/.sh` gọi `--check` ở bước 0.

### Delta hành vi (cố ý, cần biết khi soi lỗi hiện trường)

1. **Marker lỗi mạng GỘP** (đúng yêu cầu mục 2 của plan): scrape nay bắt thêm `err_timed_out` / `took too long to respond` / `site can` / `checking the proxy` → những trang này giờ trả `proxyError` thay vì chạy tiếp rồi fail "không tìm thấy nút scrape". Search bắt thêm `err_tunnel_connection_failed` / `err_socks_connection_failed`.
2. **`isVerifyUrl` anchored**: search đổi từ `/\/verify\//` (cả URL) sang pathname `^/verify(/|$)` → bắt được `/verify` không đuôi, KHÔNG còn dính "verify" trong query. scrape gộp 2 phép thử verify (`detectCaptcha` trước đây dùng bản lỏng) về cùng một hàm.
3. **orders đổi cổng WS thì THAY socket** (trước: có socket sống là bỏ qua, giữ socket cổng cũ). Đây chính là phần "bản đã fix" của search.
4. **`waitForTabComplete` thay `waitTabComplete` (orders)**: nghe sự kiện thay vì poll 400ms → trả sớm khi tab bị đóng. Mọi call site đều bỏ giá trị trả về nên không đổi luồng.
5. **`waitForTabLoad` (search)**: nay trả về ngay khi tab không tồn tại / bị đóng thay vì chờ hết 15s. Call site cũng bỏ giá trị trả về.
6. 3 manifest đều `"type": "module"`; KHÔNG dùng `importScripts` (không extension nào vướng rào phải quay lại classic worker).

### Nghiệm thu

- `node --check` (chép sang `.mjs` vì node coi `.js` là CJS): **23/23 file OK** — 5 module nguồn, 11 bản copy, 3 background.js, content/overlay/popup.
- Nạp thật 3 `background.js` như ES module với `chrome.*` giả lập: **3/3 OK** (bắt được lỗi sai đường dẫn import / sai tên export mà `--check` không thấy).
- Rig hành vi cho module shared (WebSocket + chrome.tabs giả lập): **37/37 OK** — gồm "connect lại cùng cổng không mở socket thứ hai", "đổi cổng gỡ handler cũ rồi mới đóng", "rớt socket → onClose + nối lại đúng cổng sau nhịp nghỉ", các ca `isVerifyUrl`, `waitForTabComplete` gỡ listener ở mọi lối ra.
- `sync-shared` cả `.cmd` lẫn `.sh`: chép OK, `--check` sạch = exit 0, cố tình làm lệch/xoá 1 file = exit 1 + in tên file.
- `dotnet build suite/Shopee.Suite` (csproj bundle `extensions/**`): **0 warning, 0 error**; kiểm output `bin/Debug/net8.0/extensions/` — `shared/` đã được chép đủ cho cả 3 extension. `dotnet test suite/Shopee.Core.Tests`: **16/16 passed**.

### Điểm cần phiên chính soi

- **Chưa chạy thử trên Brave thật.** Đổi `"type":"module"` là thay đổi runtime; mọi listener vẫn đăng ký ở top-level module nên đúng luật MV3, nhưng nên mở 1 lượt scrape + 1 lượt Đơn hàng để chắc.
- `extensions/sync-shared.cmd` phải giữ **CRLF** (LF làm cmd.exe parse sai) và không có ký tự `>`/`(`/`)` trong dòng `REM`. `.gitattributes` chỉ ép `*.sh eol=lf`, không đụng `.cmd`.
- `orders/background.js` vẫn còn ~12 chỗ dò verify bằng `/\/verify/i.test(url)` viết thẳng — CỐ Ý giữ: nó là phép thử lỏng trên toàn URL của host banhang/subaccount, đổi sang `isVerifyUrl` anchored là đổi ngữ nghĩa, nằm ngoài phạm vi plan.
- `shopee-scrape/content.js` vẫn có `sleep` riêng: đây là content script inject vào TRANG (không phải module), không import được.
- `dbg-input.js` hiện chỉ orders dùng — chưa khử trùng lặp gì, chỉ là dọn chỗ theo plan.
