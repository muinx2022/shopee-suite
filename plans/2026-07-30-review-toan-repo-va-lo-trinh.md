# Plan: Review toàn repo 2026-07-30 + lộ trình refactor tiếp theo

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (mỗi đợt có plan con riêng)

## 1. Bối cảnh & mục tiêu

User yêu cầu "review và refactor toàn bộ repo". Đã chạy đợt review đa-agent 30/07 (24 agent: 5 kiểm chứng plan 25/07 + 4 review code mới từ 0a9210d + xác minh đối kháng từng finding). Kết quả thô lưu ở `scratchpad/review-2026-07-30.json` + `scratchpad/findings-full.txt` (không commit).

### Kết luận kiểm chứng plan 25/07 (`2026-07-25-ke-hoach-refactor-toan-app.md`)

- **Đợt 1 (sửa bug hành vi): HOÀN THÀNH** — 7/7 mục 1A, 4/4 mục 1B, 5 mục 1C (1C.4 đổi hướng chủ đích ở 24f7234: giữ debugger attach xuyên suốt), 5/5 mục 1D đều đã fix (các commit 567cdcb, 01a689d, 24f7234…).
- **Đợt 2 (dọn code chết): HOÀN THÀNH ~95%** — 2B/2C/2D gone toàn bộ (commit 1c26dad −809 dòng FleetViewModel…). Sót lại:
  - Extension orders: cụm `withDebugger/keyInfo/dbgType/dbgEnter`, nhánh `hello`, `releaseDbg` (chết mới sau 24f7234) → gộp vào plan con B1.
  - Orders proxy: b2310c5 đã gỡ proxy runtime + màn Proxy nhưng cụm Core mồ côi còn nguyên (ProxyRotator/KiotProxyClient/KiotKeyPool/ProxyParser + ~7 file test, ProxyRepository chỉ nuôi status bar) → B1.
  - Hub 2E: log VM 10→30/07 **im hoàn toàn** với `/accounts/append` + `/accounts/remove` → đủ bằng chứng xoá → B2. Riêng `/api/shops` + `/api/orders` user đã chốt 29/07 **GIỮ** làm API admin — không xoá.
  - `ExtensionRunnerAutomation.cs:119` ngưỡng CDP 120s chết (deadline ngoài 90s luôn thắng) → gộp vào đợt 3 (3C đằng nào cũng sửa file này).
- **Đợt 3 (hợp nhất trùng lặp): CÒN NGUYÊN, đã cập nhật số liệu:**
  - 3A: 3 bản Shopee-login vẫn lệch ngữ nghĩa parse (MB đòi ≥3 phần + prefix SPC_F + join lại cookie; SE ≥3 phần không prefix, không đòi password; CA chấp nhận ≥2 phần, cookie tuỳ chọn). SE/ShopeeLoginService 221 dòng nhưng vẫn là bản đầy đủ độc lập thứ 3 (SearchSession.cs:177 dùng).
  - 3B: 2 bản human-input KHÔNG byte-tương-đương (lệch hằng delay click/gõ + toạ độ khởi tạo; SE có thêm wheel/clearFirst/SelectAllAndDelete) — hợp nhất phải tham số hoá hằng theo bản gọi, không được đồng nhất delay.
  - 3C: chỗ tự parse `/json/list` đã TĂNG 12→20 (ExtensionRunnerAutomation 10; BraveInstanceSession:1440; PageCdpHelper:186,256; SE/BraveManager:93; SE/SearchSession:255; Core CdpClient:17,51,131,165; BigSellerCookieEngine:764).
  - 3D: 4 bản kill-Brave còn nguyên (đã cùng gọi Core BraveProcessReaper nhưng kịch bản bọc ngoài vẫn 4 bản).
  - 3E: JsonAtomicFile chưa có; 13 store vẫn lặp khuôn (danh sách trong review JSON).
  - 3F: 4 cặp trùng orders↔suite còn nguyên; LƯU Ý drift mới: OrdersWebSocketServer đã có SendAsync fail-fast (fix 1B.3) mà bản Search chưa — hợp nhất phải giữ fail-fast.
  - 3G: search manifest đã `"type":"module"`; scrape + orders còn classic worker.
- **Đợt 4 (tách god class) — danh sách cập nhật theo đo 30/07:**
  | File | Dòng |
  |---|---|
  | extensions/shopee-search/background.js | 2471 |
  | orders/…/ShopeeLoginService.cs | 2427 |
  | extensions/shopee-orders/background.js | 2002 |
  | orders/…/AccountsViewModel.cs | **1955 (MỚI)** |
  | suite MB BraveInstanceSession.cs | 1964 |
  | suite MB ExtensionRunnerAutomation.cs | 1910 |
  | orders/…/OrdersBridgeSession.cs | 1471 (0 test) |
  | orders/…/OrdersRepository.cs | 1295 |
  | server Dispatch.razor | **1270 (MỚI, sau khi đã tách tab Đơn hàng)** |
  | suite UP BigSellerProductUpdateRunner.cs | 1267 |
  | orders/…/AccountSession.cs | 1206 |
  | suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs | **1073 (MỚI)** |
  | server Fleet.razor | 925 |
- **Đợt 5 (nhất quán): còn nguyên**, cập nhật: bản UpdateUrl/Restore chép tay giờ là **3** (Fleet, AllData, Dispatch:1209); 3 trang chưa URL-state: /orders, /logs-view, /config/accounts; lệch tên param p/ps vs page/size.

### Bug MỚI trong code viết 25→30/07 (14 confirmed qua xác minh đối kháng + 1 refuted)

Chi tiết từng bug nằm trong 2 plan con:
- `2026-07-30-sua-bug-moi-orders-extension.md` (B1): mốc trả hàng bị đầu độc khi tab không chọn được (nặng nhất), tabTraHang không verify, retry click Chuẩn bị hàng bấm mù, notify đơn-trả bỏ sót đơn đã dọn, mốc cảnh báo địa chỉ không nhả, cơ chế đẩy-lại-mã-đổi chết với Apps Script, + dọn chết sót (proxy, extension).
- `2026-07-30-sua-bug-moi-hub-thong-ke.md` (B2): race MarkHubSynced COALESCE nuốt cờ reset (cùng lớp bug "cờ đã đẩy" v1.6.3), shop nhân đôi case-sensitive → đếm trùng, map client bản-sau-thắng → mất số, first_seen_at sai ngày, DateTime.Now trên VM UTC, 3 hàm stats còn giữ lock toàn cục, + dọn chết hub.
- Refuted (không sửa): Logs.razor poll chết khi exception — xác minh cho thấy không tái dựng được; ghi nhận là hardening tuỳ chọn đợt 5.

## 2. Phạm vi

- **Làm (theo thứ tự):** Đợt B (2 plan con bug mới, chạy song song) → Đợt 3 (plan con theo 3A-3G) → Đợt 4 (mỗi god class 1 plan con) → Đợt 5. User đã chốt 30/07: làm **cả 5 đợt liên tục**, không chờ production giữa các đợt.
- **Không làm:** viết lại kiến trúc; hợp nhất 3 extension làm một; đổi hành vi nghiệp vụ ngoài các bug nêu; xoá `/api/shops` + `/api/orders` (user chốt giữ 29/07); deploy hub/release client (làm sau khi nghiệm thu tổng, cần user).

## 3. Các bước thực hiện

1. Đợt B: giao B1 (cây chính) + B2 (worktree) song song → nghiệm thu → commit từng plan → merge.
2. Đợt 3: plan con `3A+3B` (login + human-input, nhạy anti-bot — LÀM CẨN THẬN NHẤT), `3C+3D` (CDP + kill Brave + fix ngưỡng 120s), `3E` (JsonAtomicFile), `3F` (orders dùng shared/), `3G` (extensions shared). Trình tự: 3E ∥ 3F ∥ 3G song song được; 3A+3B rồi 3C+3D tuần tự (chung file module).
3. Đợt 4: tách god class theo bảng trên (ưu tiên: OrdersBridgeSession + test; Dispatch.razor; ShopeeLoginService orders; AccountsViewModel orders; BraveInstanceSession; ExtensionRunnerAutomation; background.js search).
4. Đợt 5: hằng AssignmentOps/Status, URL-state + helper UrlState chung, magic number, DateTime.UtcNow, quy ước.
5. Sau mỗi đợt: build 2 solution + test toàn bộ; so với baseline 30/07 (build 0 lỗi 0 warning; test orders 1449, hub 30).

## 4. Tiêu chí nghiệm thu

- [ ] Mỗi plan con: build sạch, test xanh (không tụt so baseline), diff được Fable đọc đối chiếu plan.
- [ ] Đợt B xong: 14 bug confirmed đều có fix hoặc lý do bỏ qua ghi rõ.
- [ ] Đợt 3 xong: login/human-input/ListTargets/kill-Brave mỗi thứ 1 bản ở Core; 13 store dùng JsonAtomicFile; orders dùng shared/ cho 4 hạ tầng.
- [ ] Đợt 4 xong: các file trong bảng ≤ ~800 dòng trừ ngoại lệ khai báo; OrdersBridgeSession có test.
- [ ] Đợt 5 xong: grep magic string assignment-op phía client+hub = 0 (ngoài file hằng).

## 5. Rủi ro & lưu ý

- Code automation anti-bot: mọi hợp nhất 3A/3B giữ nguyên tham số delay/easing/thứ tự thao tác TỪNG BẢN (tham số hoá, không đồng nhất). Diff logic trước/sau, không "tiện tay cải thiện".
- 3 bản parse tài khoản lệch: khi hợp nhất chọn ngữ nghĩa MB (chặt nhất) làm chuẩn nhưng phải kiểm tra file tài khoản thật đang chạy không bị loại — nếu nghi ngờ, hỏi user.
- Hợp đồng client↔hub đổi (OrderPushItem.CreatedAt, app-alert don_tra): deploy hub TRƯỚC, release client SAU.
- Apps Script (Code.gs) user phải redeploy TAY trên script.google.com TRƯỚC khi release client (B1 đổi hợp đồng payload chỉ-mã-trả).

---

## Báo cáo thực thi (Opus điền sau khi xong)

(plan tổng — theo dõi qua các plan con)
