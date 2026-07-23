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

(Opus điền sau khi xong.)
