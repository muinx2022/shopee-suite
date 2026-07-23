# Plan: Tải lại file phiếu bị thiếu (tự động khi sync + nút bấm tay)

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

File phiếu PDF chỉ được lưu MỘT lần trong bước Xử lý đơn (bấm "In phiếu giao" → bắt tab awbprint → `SaveSlipAsync` lưu `<mã đơn>.pdf` vào thư mục phiếu). Lưu lỗi (mạng/tab không mở kịp) → đơn vẫn arranged nhưng file mất, không có đường tải lại. Người dùng muốn tải lại được từ chi tiết đơn trong tab "Tất cả".

**Người dùng chốt (AskUserQuestion 2026-07-23): làm CẢ HAI:**
1. **Tự động khi sync:** mỗi lượt sync, đơn nào **thiếu phiếu** (định nghĩa dưới) được tự mở chi tiết trên danh sách đơn → bấm "In phiếu giao" → lưu lại file.
2. **Nút bấm tay:** màn Đơn hàng có nút "Tải phiếu" trên dòng đơn thiếu file — bấm là tải lại ngay đơn đó (phiên trình duyệt của tài khoản phải đang chạy).

**Định nghĩa "thiếu phiếu":** đơn đang theo dõi có trạng thái **Chuẩn bị hàng** (`ShopeeShippingNav.LaChuanBiHang`) + **ĐÃ có mã vận đơn** (`tracking_number` khác rỗng — tức arrange đã xong, phiếu đáng lẽ phải có) + file `<invoiceDir>/<SanitizeFileName(order_sn)>.pdf` **không tồn tại hoặc không phải PDF thật** (magic `%PDF-` — tái dùng logic kiểm như `TryReadSlipBase64`). Đơn chưa có vận đơn KHÔNG tính (phiếu sẽ được tạo ở bước Xử lý đơn).

## 2. Phạm vi

- **Làm:** luồng Core tải-lại-phiếu theo mã đơn + gọi tự động cuối lượt sync + nút "Tải phiếu" ở màn Đơn hàng.
- **Không làm:**
  - KHÔNG đụng bước arrange/đặt địa chỉ của Xử lý đơn (đơn đã arranged rồi — chỉ IN LẠI).
  - KHÔNG đổi logic GSheet (file có lại thì lượt đẩy sau tự đính kèm nhờ điều kiện `coFileBoSung` sẵn có — không phải sửa gì).
  - KHÔNG thêm selector DOM mới nếu selector tương đương đã có trong luồng Xử lý đơn — TÁI DÙNG tối đa.

## 3. Các bước thực hiện

> Trước khi code, KHẢO SÁT KỸ `ShopeeLoginService.cs` luồng `ProcessFirstOrderAsync`/arrange: cách mở modal "Thông Tin Chi Tiết" của một card, nút "In phiếu giao" (data-testid + fallback text, `PrintButtonWaitSeconds`), bắt tab awbprint (`SafeUrlHasAwbprint`), `SaveSlipAsync`, `CloseDetailModalAsync`; và cách "định vị card theo mã" trong `FetchFinalAmountsForPageAsync`. Tái dùng các mảnh này — KHÔNG viết lại selector.

### Core — luồng tải lại phiếu theo mã đơn

1. `ShopeeLoginService.cs`: thêm method interface `ILoginSession`:
   `Task<int> RedownloadSlipsAsync(IReadOnlyList<string> orderSns, string downloadDir, Action<string>? log = null, CancellationToken ct = default)` — trả SỐ phiếu lưu lại thành công. Luồng (best-effort, kiểu người như các luồng khác):
   - Về trang danh sách đơn (tái dùng bước 1 của `SyncAllOrdersAsync`), chuyển tab **"Tất cả"** (`EnsureOrderListTabAsync` với `l1-tab-all` — đơn Chuẩn bị hàng chắc chắn nằm trong Tất cả; người dùng cũng yêu cầu "từ tab tất cả").
   - Duyệt trang (tái dùng vòng quét + nút trang sau + `WaitOrderListReadyAsync`, chốt chặn `MaxSyncPages`): trên mỗi trang, với từng mã trong `orderSns` còn thiếu — định vị card theo mã (tái dùng cách của `FetchFinalAmountsForPageAsync`), mở **modal chi tiết** của card đó (đúng cách Xử lý đơn mở), bấm **"In phiếu giao"** (selector sẵn có), chờ tab awbprint, `SaveSlipAsync(newPage, downloadDir, orderSn, ...)`, đóng tab phiếu + đóng modal (`CloseDetailModalAsync`).
   - Per-đơn best-effort: 1 đơn lỗi (không thấy card / nút in không bấm được / lưu fail) → log rõ + sang đơn khác, KHÔNG ném; OCE ném xuyên. Tìm đủ mọi mã hoặc hết trang → dừng.
   - Sau khi lưu, KIỂM lại file (tồn tại + magic `%PDF-`) rồi mới đếm thành công.
2. Nếu luồng Xử lý đơn mở modal/bấm in qua các hàm private dùng được lại → tách/gọi chung; nếu buộc phải chỉnh chữ ký hàm private thì giữ nguyên hành vi luồng Xử lý đơn (tiêu chí: diff luồng cũ = 0 về hành vi).

### App — helper "đơn thiếu phiếu"

3. `AccountSession.cs`: thêm hàm PURE `internal static bool ThieuPhieu(string? status, string? trackingNumber, string pdfPath)` (Chuẩn bị hàng + có vận đơn + file không tồn tại/không phải PDF — dùng chung cho auto + hiển thị nút). Đọc file qua helper kiểm magic tách được (refactor nhẹ `TryReadSlipBase64` nếu tiện — GIỮ hành vi).
4. `OrdersRepository.cs`: thêm query gọn `IReadOnlyList<(string OrderSn, string? Status, string? TrackingNumber)> GetOrdersForSlipCheck(long accountId)` (hoặc tái dùng dữ liệu sẵn có nếu đã đủ — khảo sát trước, tránh thêm query trùng).

### App — tự động khi sync

5. `AccountSession.SyncOrdersAsync`: SAU vòng upsert + trước bước về-trang-chủ-đọc-số (vẫn trong `_navigating`):
   - Tính danh sách thiếu phiếu từ DB (bước 3+4), chốt chặn **tối đa 5 đơn/lượt** (tránh kéo dài sync; còn thiếu thì lượt sau làm tiếp — log rõ "còn X đơn chờ lượt sau").
   - Có đơn thiếu → `StatusText = "Đang tải lại N phiếu thiếu..."` + gọi `RedownloadSlipsAsync` + log kết quả ("Tải lại phiếu: xong x/y."). Lỗi/0 thành công KHÔNG phá kết quả sync.
6. Sau khi tải lại thành công ≥1 phiếu → `_services.RaiseOrdersChanged()` (cột Phiếu ở màn Đơn hàng cập nhật).

### App — nút bấm tay ở màn Đơn hàng

7. `OrderRowViewModel.cs`: thêm property `HasSlipFile` (tính lúc nạp dòng: file tồn tại + magic OK — dùng helper bước 3) và command `RedownloadSlipCommand`.
8. `OrdersView.axaml`: cột Phiếu — cạnh link "In phiếu" thêm nút/link nhỏ **"Tải phiếu"** chỉ hiện khi `!HasSlipFile` và đơn có mã vận đơn (đơn chưa xử lý không hiện). Style theo link sẵn có.
9. Wiring: `OrdersViewModel` (nơi dựng OrderRowViewModel) nối command tới phiên: tìm phiên của `AccountId` qua manager phiên hiện có (khảo sát `AccountSessionManager`/`AppServices` — đúng cách màn Tài khoản đang lấy phiên). 
   - Phiên KHÔNG chạy (null/chưa Running) → toast/notify sẵn có: "Mở phiên tài khoản này trước (màn Tài khoản) rồi bấm Tải phiếu."
   - Phiên đang bận (`_navigating`) → "Tài khoản đang bận thao tác khác — thử lại sau."
   - Chạy được → gọi method mới trên `AccountSession`: `Task<bool> RedownloadSlipAsync(string orderSn)` (bọc `_navigating` y hệt các lượt điều hướng khác, gọi `RedownloadSlipsAsync` 1 phần tử, cập nhật StatusText + log, xong `RaiseOrdersChanged`). Chạy nền, không block UI.

### Test + build

10. Test: `ThieuPhieu` (ma trận: đúng trạng thái/vận đơn/file thiếu → true; không vận đơn → false; file PDF hợp lệ → false; file rác không magic → true; trạng thái khác → false — dùng file tạm). Repo query mới (nếu thêm). KHÔNG test UI/luồng browser.
11. `dotnet build ShopeeSuite.sln` 0 lỗi; `dotnet test orders/XuLyDonShopee.Tests` toàn bộ xanh (873 hiện có + mới).

## 4. Tiêu chí nghiệm thu

- [ ] Build + toàn bộ test xanh.
- [ ] (Đối chiếu code) `RedownloadSlipsAsync` TÁI DÙNG selector/bước sẵn có (mở modal, nút In phiếu giao, awbprint, SaveSlipAsync, đóng modal) — không selector mới tự chế; luồng Xử lý đơn không đổi hành vi.
- [ ] Sync: có đơn thiếu phiếu → tự tải lại (≤5 đơn/lượt), log "Tải lại phiếu: xong x/y"; không đơn thiếu → không thêm bước, sync như cũ.
- [ ] Màn Đơn hàng: dòng thiếu phiếu (có vận đơn) hiện nút "Tải phiếu"; bấm khi phiên chạy → tải + cột Phiếu cập nhật; phiên không chạy → thông báo hướng dẫn, không crash.
- [ ] File lưu đúng `<invoiceDir>/<mã đơn>.pdf`, kiểm magic `%PDF-` trước khi tính thành công; GSheet lượt đẩy sau tự đính kèm phiếu (không sửa gì thêm ở GSheet).
- [ ] Mọi nhánh lỗi best-effort: log rõ, không ném, không phá sync/phiên.

## 5. Rủi ro & lưu ý

- **DOM chưa soi live:** nút "In phiếu giao" trong modal với đơn ĐÃ arranged nhiều khả năng vẫn hiện (Shopee cho in lại), nhưng chưa kiểm thật — mọi bước phải best-effort + log chẩn đoán (tái dùng log chẩn đoán nút In sẵn có) để soi khi chạy thật.
- **Không tìm thấy card trong `MaxSyncPages` trang** (đơn quá cũ) → log + bỏ qua, không lặp vô hạn.
- **Loại trừ thao tác chuột:** mọi lượt đều nằm trong cửa sổ `_navigating` (auto: đang trong sync; tay: `RedownloadSlipAsync` tự bật) — không hai luồng chuột cùng trang.
- Nút bấm tay chạy NỀN (Task.Run/async command) — không treo UI thread.

---

## Báo cáo thực thi

(Opus điền sau khi xong.)
