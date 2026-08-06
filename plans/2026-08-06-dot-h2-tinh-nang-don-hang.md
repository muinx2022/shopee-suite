# Plan: Đợt H2 — Tính năng đơn hàng (digest, lọc, ZIP phiếu, chẩn đoán đơn kẹt)

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (sau H1)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

6 tính năng vòng đơn hàng, đều xây trên hạ tầng sẵn: webhook OrderNotifyService, kho phiếu `/slips/{shopId}/{orderSn}`, `OrdersRepository.Query/Count` đã hỗ trợ lọc ngày, `OutboxPending` 5 loại tồn, cờ `NenXoaDonKetThuc`, `hub_push_gen` chống đua. Đêm vẫn có đơn (user xác nhận 06/08) nên mọi thứ chạy 24/7 — digest là để NGƯỜI nghỉ mà vẫn nắm tình hình, không phải để máy nghỉ.

## 2. Phạm vi

- **Làm:** 6 mục phần 3 (hub: H2.1/H2.3/H2.4; client orders app: H2.2/H2.5/H2.6).
- **Không làm:** không đụng vòng scrape/update; không deploy/release (phiên chính lo).

### 2b. HIỆN TRẠNG CÂY (cập nhật 06/08 sau các đợt A–G + H1 — dò theo symbol, plan viết trước các đợt đó)

- **`OrdersRepository` đã tách 5 partial** (đợt D): `.Sync/.Gsheet/.Hub/.SoldCount/.Query.cs`. Hàm mới phải đặt
  đúng partial theo mảng, KHÔNG dồn về file gốc (gốc chỉ giữ record + ctor + hàm bắc nhiều mảng).
- **`OrderNotifyService` vừa được H1.3 thêm 2 hàm dựng tin webhook** (máy mất nhịp / trở lại) — H2.1 thêm tin
  digest theo ĐÚNG khuôn đó, đừng chế khuôn thứ hai.
- **`/orders` của hub đã có toggle ẩn cột `?hide=` + pattern UrlState** (đợt F5): lọc "có mã trả" của H2.3 phải
  vào URL cùng cách (mặc định = vắng key), và nhớ cột bị ẩn vẫn phải khớp `colspan` khối mobile.
- **`window.confirm` đã bị xoá sạch khỏi hub** (đợt F3): mọi xác nhận mới dùng `Shared/ConfirmDialog.razor`
  (`AskAsync`), nút nguy hiểm `danger: true`.
- **`OrdersView.xaml` (client) vừa đổi ở đợt G6**: `FrozenColumnCount=2` + ContextMenu ẩn/hiện 6 cột phụ (state
  trong `OrdersViewModel.ShowCol*`, lưu key `orders_hidden_columns`). Thanh lọc ngày của H2.2 thêm vào hàng lọc
  hiện có, KHÔNG phá bố cục cột.
- **`OrdersView.xaml.cs` đăng ký `PropertyChanged` ở `Loaded`/gỡ ở `Unloaded`** (vá rò rỉ đợt G) — nếu đụng
  code-behind này thì giữ nguyên vòng đời đó.
- **Màn Thống kê đã có chip preset ngày** (G8, `ApplyDatePresetCommand`, dùng `DateTime.Today` để khớp
  `TryBuildCreatedRange`). H2.2 làm ở màn ĐƠN HÀNG — nếu tái dùng được style `dateChip` thì dùng lại.
- **Cột `machines.outbox_pending` + `MachinePresence.OutboxPending`** đã có từ H1.4 (null = máy không báo).
  H2.6 (tooltip breakdown) là phía CLIENT, đọc `OutboxPending` 5 field của `AppServices` — không đụng hub.
- **Test hiện tại (nền để so):** orders 1506 · Core 83 · hub 80. Số chỉ được TĂNG.

## 3. Các bước thực hiện

### H2.1 (Hub) Tin tổng kết cuối ngày qua webhook
- BackgroundService gửi 1 tin/ngày lúc giờ VN cấu hình (mặc định 21:00; bật/tắt + giờ trong /settings cùng section webhook). Nội dung gộp: tổng đơn "chuẩn bị hàng" phát sinh hôm nay THEO SHOP (top + tổng, mốc `first_seen_at` ngày VN như v1.7.6), số mã trả hàng mới hôm nay, shop còn cảnh báo địa chỉ active, máy đang offline. Format theo khuôn tin OrderNotifyService hiện có (Slack markdown).
- Chống gửi trùng khi hub restart quanh giờ gửi: lưu mốc "đã gửi ngày d" (bảng config/settings của hub), so ngày VN.

### H2.2 (Client) Lọc khoảng ngày ở màn Đơn hàng
- OrdersView: thêm 2 DatePicker Từ/Đến vào thanh lọc (cạnh lọc shop/trạng thái hiện có) → truyền `createdFromUtc/createdBeforeUtc` vào `OrdersRepository.Query/Count` (đã hỗ trợ — màn Thống kê đang dùng, OrderStatisticsViewModel ~:172). Đến = hết-ngày (cộng 1 ngày, so <). Nút ✕ xóa nhanh 2 ô. Đổi filter reset về trang 1 (khớp cơ chế phân trang hiện có).

### H2.3 (Hub) /orders lọc "đơn có mã trả hàng"
- Thêm checkbox/toggle "Có mã trả" vào thanh lọc Orders.razor (WHERE `return_request_code IS NOT NULL` — thêm tham số vào `Db.QueryOrdersPage`/Count). Trạng thái vào URL theo pattern UrlState. Cột "Đơn trả hàng" đã có sẵn để đối chiếu.

### H2.4 (Hub) Tải ZIP phiếu theo bộ lọc hiện tại
- Nút "⬇ ZIP phiếu" trên /orders: tải mọi phiếu PDF của các đơn khớp BỘ LỌC hiện tại có phiếu trên hub. Endpoint mới (admin-auth như trang) stream `ZipArchive` (entry = `{shop}/{orderSn}.pdf`, dùng kho `/slips/...` hiện có — đọc cách endpoint slip hiện phục vụ file để dùng đúng đường dẫn vật lý). Trần 500 phiếu/lượt — quá trần trả 400 kèm thông báo thu hẹp bộ lọc. Stream trực tiếp (không dựng file tạm to trong RAM/disk; ZipArchive trên response stream, CompressionLevel.NoCompression vì PDF nén sẵn).

### H2.5 (Client) Màn chẩn đoán "đơn kết thúc chưa dọn được" + nút đẩy lại
- Hiện chỉ có log đếm tổng (HubOutbox ~:512 "N đơn kết thúc chờ lượt sau"). Làm cửa sổ/panel mở từ badge ⏳ (hoặc menu): liệt kê từng đơn terminal chưa xóa được, mỗi đơn kèm nghĩa vụ còn thiếu suy từ ĐÚNG các điều kiện trong `NenXoaDonKetThuc` (chưa ghi sheet / chưa lên hub / phiếu chưa đẩy / chưa đếm Đã bán / mã trả chưa đẩy) — viết hàm thuần `ChanDoanDonKetThuc(order) -> danh sách nghĩa vụ thiếu` trong Core CẠNH `NenXoaDonKetThuc` để 2 luật không trôi lệch nhau (tốt nhất: NenXoaDonKetThuc gọi lại hàm chẩn đoán hoặc cùng nguồn điều kiện), + test ma trận ca.
- Nút "Đẩy lại" per-đơn: reset các cờ đã-đẩy của đơn đó (hub_synced_at + cờ gsheet_da_co_* — soi chính xác bộ cờ theo `UpsertMany` reset-conditions hiện có; `hub_push_gen` đã chống đua) để lượt outbox sau đẩy lại. Confirm trước khi reset.

### H2.6 (Client) Tooltip breakdown badge ⏳ Chờ đẩy
- Badge hiện chỉ số tổng; `OutboxPending` đã tách 5 field (AppServices ~:21–31). Tooltip liệt kê 5 dòng: đơn hub / phiếu / dòng sheet / lượt đếm Đã bán / mã trả hàng (ẩn dòng = 0). Click badge mở màn H2.5.

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 warning; 3 bộ test xanh.
- [ ] Test mới: (a) hàm chẩn đoán H2.5 ma trận ca khớp `NenXoaDonKetThuc` (đơn mà NenXoa=false ⇔ chẩn đoán ra ≥1 nghĩa vụ thiếu — property-test đơn giản trên các tổ hợp cờ); (b) digest H2.1: gửi đúng 1 lần/ngày kể cả restart (test logic mốc-đã-gửi); (c) H2.3 query lọc mã trả. Test thử phá rồi khôi phục, ghi vào báo cáo.
- [ ] H2.4: chạy hub local, tạo dữ liệu phiếu giả → tải ZIP đúng nội dung; quá trần trả 400.
- [ ] H2.2: màn Đơn hàng lọc ngày đúng (kiểm bằng DB dev có đơn nhiều ngày), reset trang 1.
- [ ] H2.5: nút Đẩy lại làm outbox lượt sau đẩy lại đơn (kiểm bằng test repository: reset cờ → GetForGsheetPush/GetForHubPush lại trả đơn đó).

## 5. Rủi ro & lưu ý

- **H2.5 nút Đẩy lại là thao tác ghi DB theo lệnh user** — reset đúng BỘ CỜ tối thiểu, đừng reset `gsheet_tab` (chống nhân đôi dòng khi sang tháng — memory `gsheet-tab-theo-thang`), đừng đụng `first_seen_at`.
- Luật "cờ đã-đẩy kẹt trạng thái cũ" từng dính (memory `push-once-flag-stale-state`) — đọc kỹ reset-conditions trong UpsertMany trước khi viết reset tay.
- H2.1 giờ gửi theo Asia/Ho_Chi_Minh — dùng đúng helper giờ VN hiện có trong repo (đã có cho first_seen_at), không tự quy đổi.
- KHÔNG commit/deploy/release.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
