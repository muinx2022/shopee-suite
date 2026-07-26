# Plan: Vòng chờ đẩy (outbox) — luồng đẩy độc lập cho Hub · GSheet · Đã bán

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh & mục tiêu

**Yêu cầu người dùng:** "tạo 1 vòng chờ — Hub không kết nối được thì client chờ; khi client chạy, còn vòng chờ
thì đẩy lên Hub". Đã chốt: **luồng đẩy ĐỘC LẬP**, phủ **cả 3 loại**: (a) đơn + phiếu lên Hub, (b) Google Sheet,
(c) đếm "Đã bán" theo SKU.

**Hiện trạng:** hàng đợi đã có một phần (cờ `hub_synced_at` / `hub_slip_synced_at` / `gsheet_synced_at` NULL =
chờ đẩy) nhưng **chỉ được đẩy KÉ sau khi một shop sync xong**, và **gắn token của PHIÊN**. Hệ quả đã quan sát
được trong log thật:
- Hub sống lại trong lúc client nghỉ 30′ giữa 2 vòng → hàng tồn nằm im tới shop kế tiếp.
- Dừng phiên trong ~10s sau khi shop xong → lượt đẩy bị **hủy giữa chừng** (token phiên), phải đợi vòng sau.

**LỖI PHÁT HIỆN THÊM (quan trọng — sửa luôn trong plan này):** đếm "Đã bán" **KHÔNG có đường thử lại**.
`DetectNewlyDelivered(accountId, scanned)` (`OrdersRepository.cs:576`) phát hiện đơn CHUYỂN sang đã-giao bằng
cách so trạng thái quét ↔ trạng thái đã lưu. Nếu hub lỗi lúc `IncrementSoldBySkuAsync`, cờ `sold_counted_at`
vẫn NULL **nhưng DB đã lưu trạng thái đã-giao** → lượt sync sau KHÔNG còn thấy "chuyển trạng thái" → **mất đếm
vĩnh viễn**. Cần hàng đợi theo DB thay vì chỉ dựa vào transition.

## 2. Hiện trạng code (đã khảo sát — bám theo)

`orders/XuLyDonShopee.App/Services/AccountSession.cs`:
- 4 luồng đẩy, mỗi luồng 1 cờ chống chồng **theo instance phiên**: `_gsheetPushing` :401 / `_hubPushing` :436 /
  `_soldCounting` :466 / `_hubSlipPushing` :646; các `Start*InBackground` :411/:445/:476/:655 đều fire-and-forget
  với `ct` = **token phiên**.
- Thân xử lý: `PushOrdersToHubAsync` :544, `PushSlipsToHubAsync` :679, `PushOrdersToGsheetAsync` :812,
  `IncrementSoldBySkuAsync` :498.
- **`PushPendingToHubAsync` :600 đã là `public static`** (nhận accountId + pending + hook + markFn) → tái dùng được ngay.

`orders/XuLyDonShopee.Core/Data/OrdersRepository.cs` — hàng đợi sẵn có:
`GetForHubPush(accountId)` :383 · `GetForHubSlipPush(accountId)` :505 · `GetForGsheetPush(accountId, shopId?)` :297.
**Chưa có** hàng đợi cho đếm "Đã bán".

Hook hub rót từ suite (mặc định null = app chạy độc lập): `AppServices.PushOrdersToHub` / `IncrementSoldBySku` /
`PushOrderSlipsToHub`. GSheet KHÔNG cần hub (đi thẳng Apps Script).

## 3. Phạm vi

- **Làm:** tách logic đẩy thành phần dùng lại được → thêm **guard toàn tiến trình** → **worker định kỳ** đẩy tồn
  → **hàng đợi cho đếm Đã bán** → **hiển thị số tồn**.
- **KHÔNG làm:** không đổi cách TÍNH dữ liệu (đơn, phiếu, số ước tính, điều kiện đẩy GSheet); không đụng redesign
  giao diện (`Themes/Theme.axaml`, `Modules/Workspace/*`); không đụng phần đồng bộ cấu hình GSheet vừa xong.
- **Thay thế việc "chờ êm khi dừng phiên"** đã bàn trước đó — worker nhặt lại hàng tồn nên không cần grace period.

## 4. BẤT BIẾN (làm sai là hỏng dữ liệu)

1. **KHÔNG được đẩy ĐÔI.** Worker và phiên có thể chạy cùng lúc trên cùng tài khoản. Nguy hiểm nhất là **đếm
   "Đã bán"**: 2 luồng cùng chạy = **+2 cho 1 đơn**. Bắt buộc có guard **toàn tiến trình** theo
   `(accountId, loại)` — cờ instance hiện tại KHÔNG đủ.
2. **Chỉ đánh cờ SAU khi đích nhận OK** (giữ nguyên nguyên tắc hiện có): hub OK → `MarkHubSynced`; sheet OK →
   `MarkGsheetSynced`; hub +1 OK → `MarkSoldCounted`. Thà đẩy lại thừa còn hơn mất.
3. **Token của worker KHÔNG phải token phiên** — đó là toàn bộ lý do tồn tại của việc này. Dùng token vòng đời app.
4. **Bước tách code (Bước 1) KHÔNG được đổi hành vi** — thuần cơ học, test cũ phải xanh y nguyên.
5. Hub chưa cấu hình / app chạy độc lập (hook null) → worker im lặng bỏ qua phần hub, **vẫn chạy phần GSheet**.

## 5. Các bước thực hiện

### Bước 1 — Tách logic đẩy ra chỗ dùng chung (cơ học, không đổi hành vi)
Tạo `orders/XuLyDonShopee.App/Services/HubOutbox.cs`: chuyển 4 thân xử lý thành **static** nhận tham số tường
minh `(long accountId, AppServices services, Action<string> log, CancellationToken ct)`:
`PushOrdersToHubAsync`, `PushSlipsToHubAsync`, `PushOrdersToGsheetAsync(shopId, shopLogin, …)`,
`IncrementSoldBySkuAsync`. `AccountSession` giữ nguyên các `Start*InBackground` nhưng **gọi vào `HubOutbox`**.
Giữ nguyên mọi log, thứ tự (phiếu đẩy SAU đơn), và cách nuốt lỗi.

### Bước 2 — Guard toàn tiến trình
Tạo `orders/XuLyDonShopee.App/Services/PushGate.cs`: static, `TryEnter(long accountId, PushKind kind)` +
`Exit(...)` (dùng `ConcurrentDictionary` + `Interlocked`, hoặc `SemaphoreSlim(1,1)` per key). `PushKind` =
`Hub | HubSlip | Gsheet | SoldCount`.
- `AccountSession.Start*InBackground` **thay** cờ instance bằng gate này (bỏ `_gsheetPushing`/`_hubPushing`/
  `_soldCounting`/`_hubSlipPushing`) → phiên và worker không thể chạy chồng.
- Không vào được gate → log 1 dòng như hiện tại ("lượt đẩy trước còn đang chạy — bỏ qua") rồi thôi.

### Bước 3 — Hàng đợi cho đếm "Đã bán" (sửa lỗi mất đếm)
- `OrdersRepository`: thêm `GetForSoldCountRetry(long accountId)` → các đơn `sold_counted_at IS NULL`
  **và** `sku` khác rỗng; trả `(OrderSn, Sku, Status, StatusDescription, CancelReason)` để caller lọc
  "đã giao" bằng `ShopeeShippingNav.LaDaGiaoDaBan(...)` trong C# (SQL không biết luật này).
- `HubOutbox.IncrementSoldBySkuAsync` bổ sung nhánh dùng nguồn này khi worker gọi (phiên vẫn truyền danh sách
  transition như cũ). **Không đếm đơn hủy.**

### Bước 4 — Worker định kỳ
Tạo `orders/XuLyDonShopee.App/Services/HubOutboxWorker.cs`:
- Khởi động cùng app (không theo phiên) — chỗ dựng: cạnh nơi khởi tạo `AppServices`/`MainViewModel`; `Dispose`
  khi thoát. **Chạy 1 lượt NGAY khi mở app** (bắt đúng ý "khi client chạy, còn vòng chờ thì đẩy").
- Chu kỳ **2 phút**; mỗi lượt duyệt **mọi tài khoản** (`services.Accounts.GetAll()`), với mỗi tài khoản:
  đếm tồn 4 loại → có tồn thì gọi `HubOutbox.*` qua `PushGate`.
- **Backoff khi hub lỗi:** thất bại liên tiếp → giãn 2′ → 5′ → 10′ (trần 10′), thành công → về 2′. Không spam log:
  chỉ log khi **bắt đầu đẩy tồn** và khi **kết quả đổi** (từ lỗi → OK hoặc ngược lại).
- Toàn bộ nuốt lỗi, không bao giờ ném ra ngoài.

### Bước 5 — Số tồn hiển thị
- `AppServices` (hoặc `MainViewModel` module Đơn hàng): property tổng tồn (đơn + phiếu + sheet + đếm) và event
  đổi. Worker cập nhật sau mỗi lượt.
- Hiện ở thanh trạng thái: thêm **1 đoạn CHỈ hiện khi tồn > 0**, vd `⏳ Chờ đẩy: N`, tooltip tách theo loại.
  (Thanh trạng thái ở `suite/Shopee.Suite/MainWindow.axaml` — chỉ THÊM 1 đoạn, không đổi bố cục đã redesign.)
- Log khi worker đẩy bù: `"Vòng chờ: đẩy N đơn / M phiếu / K dòng sheet còn tồn."`

### Bước 6 — Test
- `PushGate`: 2 luồng cùng `TryEnter` cùng (acc, kind) → chỉ 1 vào được; `Exit` xong luồng sau vào được.
- `GetForSoldCountRetry`: trả đúng đơn `sold_counted_at` NULL + có SKU; bỏ đơn đã đếm; bỏ đơn không SKU.
- Kịch bản mất đếm (hồi quy lỗi vừa phát hiện): đơn chuyển sang đã-giao → hub lỗi (không mark) → lượt sau
  `DetectNewlyDelivered` KHÔNG thấy transition **nhưng** `GetForSoldCountRetry` VẪN trả đơn đó.
- Backoff: chuỗi lỗi → chu kỳ giãn đúng bậc; thành công → về 2′ (tách hàm thuần tính chu kỳ để test được).

## 6. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution 0 error; `dotnet test XuLyDonShopee.Tests` xanh (test cũ **không sửa hành vi**, chỉ
      thêm mới — nếu buộc phải sửa test cũ thì giải trình rõ trong báo cáo).
- [ ] Hub tắt → chạy sync → đơn/phiếu tồn lại; **bật hub trong lúc client đang NGHỈ giữa 2 vòng** → trong ≤2 phút
      worker tự đẩy hết mà KHÔNG cần shop nào sync.
- [ ] Dừng phiên ngay sau khi shop xong (kịch bản đã gây mất lượt đẩy) → worker nhặt lại trong ≤2 phút.
- [ ] Không đẩy đôi: phiên đang đẩy thì worker bỏ qua (và ngược lại) — đặc biệt đếm "Đã bán" không bao giờ +2.
- [ ] Đơn chuyển đã-giao mà hub lỗi → lượt sau vẫn được đếm bù (không mất đếm).
- [ ] Thanh trạng thái hiện `⏳ Chờ đẩy: N` khi có tồn, biến mất khi hết.
- [ ] App Đơn hàng chạy độc lập (không hub): worker vẫn đẩy GSheet, không lỗi.
- [ ] Không đụng file redesign (`Themes/Theme.axaml`, `Modules/Workspace/*`).

## 7. Rủi ro & lưu ý

- **Bước 1 là refactor trên đường dữ liệu quan trọng nhất** — làm cơ học, đọc kỹ, đừng "tiện tay" tối ưu.
- **Đẩy đôi đếm "Đã bán"** là rủi ro nghiêm trọng nhất (sai số liệu kho, không tự phát hiện) → gate phải đúng.
- Worker duyệt mọi tài khoản mỗi 2′: các truy vấn đếm tồn phải NHẸ (đếm bằng SQL `COUNT`, đừng nạp cả list rồi
  `.Count` nếu tránh được).
- Hub-mode: `PushOrdersToGsheetAsync` hiện nhận `shopId/shopLogin` để lọc theo shop; worker KHÔNG biết shop nào
  → gọi với `shopId = null` (hành vi cũ = mọi đơn của tài khoản) — kiểm lại rằng nhánh null vẫn đúng.
- Đừng để worker chạy lúc app vừa mở mà DB chưa sẵn sàng → chạy sau khi `AppServices` khởi tạo xong.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
