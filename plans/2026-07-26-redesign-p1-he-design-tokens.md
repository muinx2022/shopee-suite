# Plan: Redesign GĐ1 — Hệ design (tokens + component core) theo handoff

- **Ngày:** 2026-07-26
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Người dùng cung cấp **bộ design handoff hi-fi** và yêu cầu thiết kế lại TOÀN BỘ giao diện theo đó. Đây là
GIAI ĐOẠN 1 (nền): đổi **hệ design token + style component lõi** trong theme suite để reskin cả app. Shell
(MainWindow) và từng màn làm ở GĐ sau.

**NGUỒN CHUẨN (đọc kỹ, bám sát — fidelity CAO):**
- `C:\Users\Ng Xuan Mui\Downloads\Windows form Shopee Manager\design_handoff_shopee_suite\README.md`
  (mục **Design Tokens**: Colors, Typography, Spacing/radii — bảng hex + size đầy đủ).
- 2 file mockup cùng thư mục: `Ribbon Window.dc.html`, `BigSeller Workspace.dc.html` (mở đọc CSS inline để
  lấy giá trị chính xác khi README chưa đủ). `support.js` = runtime prototype, **BỎ QUA**.

**Đặc trưng palette mới (ẤM-trung tính, khác hẳn theme lạnh hiện tại):** nền app `#f7f5f3`, surface trắng,
subtle `#fbfaf9`, viền ấm `#e2ddd8`/`#e5e1dd`/`#ebe7e3`, chữ ấm `#2c2724`/`#7d756f`/`#a8a09b`, accent cam
`#ee4d2d` (hover `#d8401f`, brand-text `#c93b1d`, tint `#fff3ef`/`#ffe8e1`, tint-border `#fbd6ca`/`#f6c6bb`).
Status màu: Success fg `#1f8a4c`/bg `#eafaf0`/dot `#3fa860`; Warning fg `#b06f06`/bg `#fff6e6`; Danger fg
`#c22b1e`/bg `#fdeceb`; Info fg `#2b5fc4`/bg `#eef4ff`; neutral badge fg `#6a625d`/bg `#f2efec`.

**Hiện trạng:** `suite/Shopee.Suite/Themes/Theme.axaml` giữ toàn bộ token + ControlTheme Button + style
TextBox/DataGrid/Card/tab. Module Đơn hàng có bộ riêng `orders/XuLyDonShopee.App/Styles/Colors.axaml` +
`Controls.axaml` (GĐ4 mới đồng bộ — GĐ1 KHÔNG đụng orders).

## 2. Phạm vi

- **Làm (CHỈ `suite/Shopee.Suite/Themes/Theme.axaml`):**
  1. Đổi toàn bộ **color/brush token** sang palette ấm của spec.
  2. Đổi **bo góc**: ControlCornerRadius 4→5; Border.card 6→8; thêm ý niệm pill (radius 20) cho badge.
  3. Đổi **type**: cỡ chữ h1/h2/body/caption + control font-size theo bảng Typography spec.
  4. **Style component lõi** khớp spec: Button mặc định (nút phụ trắng viền `#e2ddd8`), `.primary`
     (nút CHÍNH = NỀN CAM đặc `#ee4d2d` + chữ trắng, hover `#d8401f` — ĐỔI từ outline sang filled),
     `.danger`/"Dừng tất cả" (bg `#fff3ef` viền `#f6c6bb` chữ `#c93b1d`), TextBox/ComboBox (cao ~30, viền
     `#e2ddd8`, radius 5, focus viền cam), Border.card (radius 8, viền `#ebe7e3`), DataGrid (header nền
     `#fbfaf9` chữ HOA 10.5px/700 muted letter-spacing 0.4; row hover `#fffaf8`; kẻ dòng `#f4f1ee`), pill
     trạng thái (radius 20, theo bảng màu status).
- **KHÔNG làm ở GĐ1:** không đụng MainWindow (shell = GĐ2), không đụng các view module (GĐ3-4), KHÔNG đụng
  orders (GĐ4), KHÔNG đổi font (giữ Inter nhúng — spec ghi Segoe nhưng composite Segoe từng VỠ trên máy user,
  xem memory; Inter render ổn, look tương đương). Giữ NavBar token tạm (shell GĐ2 sẽ xử) — chỉ đổi giá trị cho
  bớt chỏi nếu tiện, KHÔNG đổi cấu trúc.
- **BẤT BIẾN:** chỉ đổi GIÁ TRỊ + template Button/style; KHÔNG thêm/đổi TÊN key override FluentTheme lạ (vỡ
  template → view trắng). Sau sửa phải mở app kiểm tra TabControl + DataGrid không trắng.

## 3. Các bước thực hiện

1. Mở `README.md` handoff, đọc kỹ mục **Design Tokens** (Colors + Typography + Spacing/radii). Đối chiếu 2
   mockup HTML khi cần giá trị chính xác (vd hex viền, padding nút).
2. Trong `Theme.axaml`, ánh xạ & đổi GIÁ TRỊ token (giữ nguyên KEY để mọi view/binding hiện có vẫn chạy):
   - `WindowBackgroundBrush` → `#f7f5f3` · `CardBackgroundBrush` → `#ffffff` · `SubtleBrush` → `#fbfaf9` ·
     `BorderBrush` → `#e2ddd8`.
   - `TextPrimaryBrush` → `#2c2724` · `TextSecondaryBrush` → `#7d756f`.
   - `AccentBrush` `#ee4d2d` (giữ) · `AccentHoverBrush` → `#d8401f` · `AccentPressedBrush` → `#c93b1d`.
   - `SuccessBrush` → `#1f8a4c` · `WarningBrush` → `#b06f06` · `DangerBrush` → `#c22b1e` · `DangerHoverBrush`
     → hợp tông (vd `#a82217`).
   - `LogBackgroundBrush`/`LogForegroundBrush`: giữ (panel log đen) trừ khi spec khác.
   - `TabUnselectedBrush` → `#7d756f` (chữ tab/sub-tab chưa chọn).
   - Thêm token phụ nếu cần cho component: brand tint (`#fff3ef`,`#ffe8e1`), tint border (`#fbd6ca`,`#f6c6bb`),
     border-strong `#e5e1dd`, border-light `#ebe7e3`, border-lightest `#f4f1ee`, row-hover `#fffaf8`,
     status bg (success `#eafaf0`, warn `#fff6e6`, danger `#fdeceb`, info `#eef4ff`), text-muted `#a8a09b`.
   - `ControlCornerRadius` 4→**5**.
3. **Component lõi** (đổi/viết style trong Theme.axaml theo spec):
   - **Button mặc định** (nút phụ): nền trắng, viền `#e2ddd8`, radius 5, cao ~30, chữ `#423c38`, hover nền
     `#faf8f6`. **`.primary` = NÚT CHÍNH filled**: nền `AccentBrush`, chữ trắng, viền trong suốt, hover
     `#d8401f` (ĐỔI khỏi look outline cũ — spec: primary là nền cam đặc). `.danger` giữ ý "Dừng tất cả":
     nền `#fff3ef`, viền `#f6c6bb`, chữ `#c93b1d`, hover nền `#ffe8e1`. `.success` cho hợp tông (outline
     xanh `#1f8a4c` hoặc theo spec). Nút nhỏ (icon) radius 4.
   - **TextBox/ComboBox**: cao ~30, viền `#e2ddd8`, radius 5, focus viền cam (giữ token
     TextControlBorderBrushFocused = accent), chữ 12.5.
   - **Border.card**: radius **8**, viền `#ebe7e3`, nền trắng.
   - **DataGrid**: header nền `#fbfaf9`, chữ HOA 10.5–11px/700 letter-spacing 0.4 màu muted; row hover
     `#fffaf8`; kẻ ngang `#f4f1ee`; dòng chọn nền tint cam nhạt (giữ cơ chế opacity hiện có nhưng tông ấm).
   - **Typography class** (h1/h2/body/caption): map cỡ theo spec (page title 17/700 ls −0.2; card heading
     14.5/700; body 12.5/400; secondary 12/400; caption/muted 11–11.5). Giữ tên class hiện có.
   - **Pill/badge**: thêm style pill (radius 20, padding 3,9, 11px/600) + biến thể màu status (success/warn/
     danger/info/neutral) để GĐ3-4 dùng.
4. Build + mở app kiểm tra không vỡ template.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` toàn solution 0 error; `dotnet test` XuLyDonShopee.Tests xanh.
- [ ] Mở app: toàn bộ màn suite chuyển tông ẤM (nền `#f7f5f3`, chữ `#2c2724`, viền ấm); KHÔNG view nào trắng
      trơn (TabControl tab module + DataGrid Workspace/Proxy còn vẽ).
- [ ] Nút `.primary` giờ là NỀN CAM đặc chữ trắng; ô nhập viền ấm focus cam; thẻ bo 8; header bảng chữ HOA muted.
- [ ] Chỉ đụng `Theme.axaml`. (orders + MainWindow + view module để GĐ sau.)
- [ ] Báo cáo: liệt kê token đã đổi (bảng cũ→mới), component đã sửa, chỗ nào lệch spec & lý do.

## 5. Rủi ro & lưu ý

- **Đổi `.primary` từ outline→filled** ảnh hưởng MỌI nút `.primary` toàn app — đúng ý spec (nút chính nền cam),
  nhưng review kỹ chỗ nhiều nút primary cạnh nhau.
- Font: GIỮ Inter (không thử Segoe composite — đã vỡ, xem memory [[ui-flat-win11-direction]]).
- Palette ấm sẽ khiến các màu hard-code inline ở view (đỏ/xanh lệ thuộc) hơi lệch tông — chấp nhận ở GĐ1,
  GĐ4 dọn.
- Đường dẫn handoff có dấu cách — trích dẫn nguyên văn khi mở.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Hoàn thành, chỉ sửa `Theme.axaml`. Build 0 error, 910 test xanh. Đổi ~18 token sang palette ấm + thêm 22 token
phụ (brand tint, status bg, border light/lightest, row hover, text body/muted). `.primary` outline → FILLED
nền cam; `.danger` kiểu "Dừng tất cả"; input cao 30 ép template-part (Fluent không đọc token của ta);
card radius 8; DataGrid header muted + row hover; thêm `Border.pill` + 5 biến thể.
Lệch spec: header bảng chữ HOA phải viết hoa ở view (Avalonia không có TextTransform) — GĐ3 làm; giữ font Inter.
Tồn: `.danger` đang dùng chung cho cả nút xoá thật → GĐ4 tách class `.destructive` (đỏ #C22B1E).
