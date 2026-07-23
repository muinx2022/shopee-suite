# Plan: Màn "Chạy tự động" — sắp lại form để hết thanh cuộn dọc

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`, worktree riêng)

## 1. Bối cảnh & mục tiêu

Màn "Chạy tự động" (`orders/XuLyDonShopee.App/Views/AutoRunView.axaml`) cột trái là ScrollViewer + StackPanel xếp DỌC mọi thứ (2 ô số, 2 toggle, ghi chú đóng khung, nút Lưu, card Điều khiển) → tổng chiều cao ~850px, vượt vùng nhìn ở cửa sổ thực tế → hiện thanh cuộn dọc giữa form và panel nhật ký. Người dùng muốn **sắp xếp lại để không còn phải cuộn** ở kích thước cửa sổ bình thường (~1080p, cửa sổ như ảnh chụp).

## 2. Phạm vi

- **Làm:** chỉ sửa layout `AutoRunView.axaml` (cột trái). GIỮ ScrollViewer làm lưới an toàn khi cửa sổ quá thấp.
- **Không làm:** KHÔNG đổi binding/logic/VM; KHÔNG đụng panel nhật ký (cột phải); KHÔNG đổi file nào khác. KHÔNG đụng nút Bắt đầu/Dừng cao 44 (chủ ý CTA — comment sẵn trong file).

## 3. Các bước thực hiện

Sửa `orders/XuLyDonShopee.App/Views/AutoRunView.axaml`, cột trái (trong ScrollViewer):

1. **2 ô số về 1 hàng:** "Số tài khoản mỗi lô" + "Nghỉ giữa các lô" đặt trong `Grid ColumnDefinitions="*,*" ColumnSpacing="20"` (mỗi ô vẫn label trên + NumericUpDown dưới, Width 180 giữ hoặc bỏ Width cho stretch). Dòng chú "Càng nhiều thì càng mở nhiều Brave..." giữ dưới ô thứ nhất (FontSize 11.5 như cũ) — nằm trong cell trái.
2. **2 toggle về 1 hàng:** "Tự Sync đơn hàng" + "Tự Xử lý đơn" đặt trong `Grid ColumnDefinitions="*,*" ColumnSpacing="20"`; mỗi cell giữ cấu trúc hiện tại (tiêu đề + mô tả bên trái, ToggleButton switch bên phải, mô tả `TextWrapping="Wrap"`). Bỏ `Border` kẻ vạch trên của toggle Sync (không còn cần phân cách dọc).
3. **Ghi chú "Kiểm tra đơn ... LUÔN tự chạy":** bỏ khung Border nền — thành 1 dòng `TextBlock` nhỏ (FontSize 11.5, màu TextMuted, Wrap) ngay dưới hàng toggle.
4. **Nén khoảng cách:** StackPanel gốc `Margin="24,16,16,16"`; subtitle `Margin="0,2,0,10"`; heading section `Margin="0,0,0,8"`; card Cấu hình `Padding="20,16"` `Margin="0,0,0,14"`; StackPanel trong card `Spacing="12"`; card Điều khiển `Padding="20,16"`, StackPanel `Spacing="12"`.
5. **Card Điều khiển:** giữ nút 44px; khối TRẠNG THÁI giữ nguyên nội dung, `Padding="12,10"`.
6. Cập nhật comment bố cục đầu file cho khớp (2 hàng × 2 cột trong card cấu hình).

### Build
7. `dotnet build orders/XuLyDonShopee.App` (hoặc cả solution) 0 lỗi. Không có test UI — nghiệm thu hình thức bằng đọc XAML + build; người dùng xem mắt thường sau.

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 lỗi.
- [ ] Cột trái hết cuộn ở chiều cao vùng nội dung ≥ ~640px (ước tính chiều cao mới ~560–600px: header ~70 + card cấu hình ~300 + section+card điều khiển ~200).
- [ ] Không binding nào bị đổi/mất (so diff: chỉ layout/margin/padding/cấu trúc Grid).
- [ ] ScrollViewer vẫn còn (cửa sổ cực thấp vẫn dùng được).
- [ ] Panel nhật ký (cột phải) không đổi.

## 5. Rủi ro & lưu ý

- Cột trái rộng ~600px (3* của 1084) → mỗi cell toggle ~280px: mô tả sẽ wrap 2 dòng — chấp nhận (Wrap sẵn).
- Đây là việc chạy trong **worktree riêng** (cây chính đang có agent khác): mọi đường dẫn quy về thư mục làm việc của agent, tuyệt đối không đọc/ghi cây chính.

---

## Báo cáo thực thi

**Người thực thi:** Opus (`opus-executor`) — worktree `agent-ae72b53b0bcd57a3f`.

### Ghi chú khởi động
- Worktree ban đầu bị tạo từ commit sai (85895f3, trước khi có module Đơn hàng) nên KHÔNG có `plans/` lẫn `orders/`. Đã `git reset --hard feature/gop-don-hang` (đưa nhánh worktree về commit 0677390 — nơi có plan + file đích) để lấy đúng nền mã. Thao tác này chỉ di chuyển con trỏ nhánh của worktree, không đụng cây chính.

### Đã hoàn thành (chỉ sửa `orders/XuLyDonShopee.App/Views/AutoRunView.axaml`)
1. **2 ô số về 1 hàng:** "Số tài khoản mỗi lô" + "Nghỉ giữa các lô" nay nằm trong `Grid ColumnDefinitions="*,*" ColumnSpacing="20"`. Bỏ `Width="180"`/`HorizontalAlignment="Left"` của 2 NumericUpDown cho stretch theo cột. Dòng chú "Càng nhiều..." vẫn ở cell trái (FontSize 11.5, thêm `TextWrapping="Wrap"`).
2. **2 toggle về 1 hàng:** "Tự Sync đơn hàng" + "Tự Xử lý đơn" nay nằm trong `Grid ColumnDefinitions="*,*" ColumnSpacing="20"`, mỗi cell là `Grid ColumnDefinitions="*,Auto"` (mô tả + switch). Đã BỎ `Border` kẻ vạch trên (`Border05` / `BorderThickness="0,1,0,0"`) của toggle Sync. Thêm `TextWrapping="Wrap"` cho mô tả 2 cell.
3. **Ghi chú "Kiểm tra ... LUÔN tự chạy":** bỏ khung `Border` (InputBg/Border010) → còn 1 `TextBlock` (FontSize 11.5, `TextMuted`, Wrap) ngay dưới hàng toggle.
4. **Nén khoảng cách:** root StackPanel `24,24,16,40` → `24,16,16,16`; subtitle `...,0,16` → `...,0,10`; 2 heading section `0,0,0,12` → `0,0,0,8`; card Cấu hình `Padding 24,22`/`Margin 0,0,0,24` → `20,16`/`0,0,0,14`, StackPanel trong card `Spacing 18` → `12`; card Điều khiển `Padding 24,22` → `20,16`, StackPanel `Spacing 16` → `12`.
5. **Card Điều khiển:** giữ nút 44px, khối TRẠNG THÁI giữ nguyên nội dung, `Padding 14,12` → `12,10`.
6. **Comment bố cục đầu file:** bổ sung mô tả card CẤU HÌNH nay là 2 hàng × 2 cột.

### Kết quả kiểm chứng
- `dotnet build orders/XuLyDonShopee.App -c Debug` → **Build succeeded, 0 Warning, 0 Error**.
- So sánh binding trước/sau (`git show HEAD:... | grep 'Binding ...'` vs bản mới): **15 binding trùng khớp tuyệt đối** — không mất/đổi binding nào (BatchSize, GapMinutes, DoSync, DoProcess, CanEditConfig, SaveConfigCommand, SavedMessage×2, ToggleButtonText, ToggleCommand, StatusText, LogEntries, LogPath, ClearLogCommand, Display).
- `ScrollViewer Grid.Column="0"` **vẫn còn** (grep = 1) → lưới an toàn khi cửa sổ quá thấp được giữ.
- Panel nhật ký cột phải (`AutoRunLogList`, "Nhật ký hoạt động", `logDark`, Border `Grid.Column="1"`) **không xuất hiện trong diff** → không đổi.
- `git diff --stat`: 1 file, 50 insertions / 46 deletions — toàn bộ nằm ở cột trái.

### Vướng mắc/bỏ dở
- Không có. Nghiệm thu hình thức bằng đọc XAML + build (không có test UI); ước tính chiều cao mới ~560–600px như plan — cần user xem mắt thường để xác nhận hết cuộn ở cửa sổ thật.

### Đề xuất
- Nhắc Fable: nhánh worktree này đã được reset về `feature/gop-don-hang` (0677390) để có nền đúng — khi commit trong worktree và merge, đối chiếu lại base cho khớp.
