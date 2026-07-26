# Plan: Redesign GĐ4c — Áp hệ nút/icon vào các form MODULE ĐƠN HÀNG

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh

GĐ4a dựng nền (luật nút + token màu), GĐ4b đã áp xong cho **toàn bộ form suite** và gỡ chặn kiến trúc (bộ icon
nay nằm ở `orders/XuLyDonShopee.App/Styles/Icons.axaml`, **cả hai module dùng chung**). GĐ4c là **mảnh cuối**:
các form của module Đơn hàng — hiện vẫn còn nguyên emoji/ký tự và chiều cao nút cũ.

**Người dùng chỉ đích danh:** *"bên shopee, phần tài khoản, thêm tài khoản lại chỉ là dấu +, không phải icon"*.
Đúng: form orders chưa qua đợt nào, nên còn `+`, `🗑`, `💾`, `⬇`, `✓`, `↶`… và nút vẫn **cao 38px** trong khi
chuẩn mới là **30px** → đứng cạnh nút suite là thấy vênh ngay.

**LUẬT NÚT (nhắc lại — người dùng chốt, ghi đè spec handoff):**
- Mọi nút **một dáng duy nhất**: nền trắng, viền `#E2DDD8` **luôn thấy rõ**, bo 5, **cao 30**, chữ `#423C38` cỡ 12.
- **KHÔNG nút nào tô nền.** Màu ngữ nghĩa **CHỈ ở ICON**: chính → cam (`.primary`/`.accent`), xóa/nguy hiểm → đỏ
  (`.destructive`), thành công → xanh (`.success`), trung tính → xám (không class).
- **Icon 14px** trong nút (theme đã đặt mặc định — đừng ghi đè cỡ tại chỗ dùng).

**BẢNG ÁNH XẠ "mỗi hành động ĐÚNG MỘT icon"** nằm ở **đầu file `Icons.axaml`** — TRA Ở ĐÓ, không tự chế icon mới.
Dùng `{DynamicResource IconSave}`, `{DynamicResource IconDelete}`…

## 2. Phạm vi

- **Làm:** áp icon + dáng nút chuẩn cho các view của `orders/XuLyDonShopee.App/Views/`, và dọn chiều cao/cỡ chữ
  nút + ô nhập cho khớp suite.
- **KHÔNG làm:** không đụng `suite/**` (đã xong ở 4b) trừ khi phát hiện lỗi rõ ràng — nếu có thì BÁO, đừng tự sửa.
  Không đổi hành vi/binding. Không đụng `Icons.axaml` (bộ icon đã chốt; thiếu icon thì BÁO).

## 3. Các việc

### Bước 1 — `Views/AccountsView.axaml` (nặng nhất, người dùng chỉ đích danh)
- Nút **"+ Thêm tài khoản"** → `IconAdd` + nhãn "Thêm tài khoản", class `.success` (khớp cặp Thêm/Xóa bên suite).
- Nút xóa (đang `.iconDanger`, hình thùng rác chữ) → `Classes="destructive iconOnly"` + `IconDelete`.
- Các nút trong form chi tiết: Lưu → `.primary` + `IconSave`; Hủy → `IconClose`; mở link/"Vào TK" → `IconOpenExternal`;
  nút con mắt xem mật khẩu (`.ghostIcon`) → giữ không viền (nằm TRONG ô nhập) nhưng đổi glyph sang icon nếu có
  icon phù hợp, không có thì giữ nguyên và ghi rõ trong báo cáo.
- Bộ lọc "chưa xác minh" (`.unverifiedFilter`) → dáng nút chuẩn + `IconFilter`, màu ngữ nghĩa ở icon.
- Nút "Bật lại"/trạng thái → `.success` + icon phù hợp.

### Bước 2 — `Views/OrdersView.axaml`
- "Làm mới" → `IconRefresh` · "In nhiều đơn" → `IconExport` (hoặc icon in nếu bảng có) · "Xuất CSV" → `IconExport`
  (nếu trùng icon với "In nhiều đơn" thì phân biệt bằng nhãn — **đừng bịa icon mới**, BÁO nếu thấy cần bổ sung).
- Nút xóa bộ lọc `✕` → `IconClose` (`iconOnly`).
- 4 nút phân trang `« ‹ › »`: **GIỮ ký tự** (bảng ánh xạ không có mục phân trang) — ghi rõ trong báo cáo.
- Bỏ `Foreground="#2E7D32"` hard-code (dòng ~121) → token `SuccessBrush`.

### Bước 3 — `Views/SettingsView.axaml` (orders)
- 4 nút "Lưu" → `.primary` + `IconSave` (đây chính là chỗ người dùng so sánh: "nút Lưu bên BigSeller một kiểu,
  bên Shopee kiểu khác" — sau bước này phải GIỐNG HỆT nút Lưu bên `BigSellerView`).
- Nút chọn thư mục → `IconFolder`.

### Bước 4 — `Views/ConfirmDialog.axaml` + `Views/OrderDetailDialog.axaml`
- ConfirmDialog: "Đồng ý" → `.primary` + `IconCheck`; "Hủy" → `IconClose`.
- OrderDetailDialog: "Lưu" → `.primary` + `IconSave`; nút đóng → `IconClose`.
- (4a đã bỏ màu xanh dương `#1976D2` ở 2 file này — giờ chỉ thêm icon.)

### Bước 5 — Dọn kích thước cho khớp suite
- `Styles/Controls.axaml`: các class còn `Height="38"` → **30**; `FontSize` 13/13.5 → **12**; ô nhập
  (`Border.field`, `ComboBox.field`, `AutoCompleteBox.field`, `NumericUpDown.field`) `MinHeight` 38 → **30**,
  cỡ chữ 13.5 → **12.5** (khớp `ControlContentThemeFontSize` của suite).
- Quét các view orders còn `Height="38"` inline → bỏ (để theme lo) hoặc đổi 30.
- Ngoại lệ giữ nguyên: `.logClear` (nút trên panel log ĐEN), `.ghostIcon` (trong ô nhập).

### Bước 6 — Rà soát cuối
- Grep toàn `orders/XuLyDonShopee.App/Views/`: **không còn emoji/ký tự icon trong `Content` của `<Button>`**
  (🗑 💾 📂 📁 ➕ ⬇ ⬆ ↗ ↶ ✔ ✓ ✖ ✕ ■ ▶ ↻ ⟳ + −). Ngoại lệ được phép: 4 nút phân trang, `PasswordChar`.
- Grep hex màu hard-code còn sót trong view orders → token (trừ các trường hợp đã ghi lý do ở 4a).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` 0 error; `dotnet test XuLyDonShopee.Tests` xanh.
- [ ] **Nút "Lưu" ở tab Shopee và ở Cấu hình BigSeller GIỐNG HỆT nhau** (cùng icon, cùng dáng, cùng cỡ) — đây là
      ca người dùng nêu đích danh, phải tự kiểm bằng cách so 2 chỗ.
- [ ] Nút "+ Thêm tài khoản" có ICON (không còn dấu `+` chữ).
- [ ] Không nút nào ở orders còn cao 38 (trừ ngoại lệ đã nêu); đứng cạnh nút suite không vênh.
- [ ] Không còn emoji trong `Content` của Button ở `orders/**/Views/` (trừ ngoại lệ đã ghi).
- [ ] Kiểm chứng bằng render harness (đã có sẵn ở scratchpad từ 4b): dựng THẬT các view orders, duyệt cây,
      **0 PathIcon trống**.
- [ ] KHÔNG đụng `suite/**` và `Icons.axaml`.

## 5. Rủi ro & lưu ý

- **Đổi chiều cao ô nhập 38 → 30 là thay đổi bố cục thật** — nhiều form orders canh theo chiều cao cũ. Sau khi
  đổi phải dựng view bằng harness kiểm không vỡ layout (chồng chữ, cắt chữ).
- `.iconDanger` đang được dùng làm nút vuông 38×38 cạnh nút "Thêm tài khoản" — đổi sang `iconOnly` (30×30) sẽ
  đổi bố cục hàng đó; kiểm lại bằng mắt/harness.
- Nút dựng ở code-behind (nếu có trong orders) — đừng bỏ sót.
- Giữ nguyên MỌI `Command`/`ToolTip.Tip`/`IsVisible`/`IsEnabled`/`x:Name` — đây là việc TRÌNH BÀY.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
