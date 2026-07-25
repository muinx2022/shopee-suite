# Plan: Làm lại giao diện app desktop theo kiểu phẳng Windows 11

- **Ngày:** 2026-07-25
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Người dùng thấy giao diện app desktop (Avalonia, thư mục `suite/` + module đơn hàng `orders/`)
**cũ, bo tròn nhiều, chữ mờ**. Yêu cầu: **thiết kế lại theo kiểu phẳng của Windows 11** — phẳng
(bỏ gradient/đổ bóng), bo góc ít hơn, chữ sắc nét hơn. Đã chốt với người dùng qua hỏi-đáp:

- **Header (dải chứa nút module):** đi hướng **"tối phẳng, hiện đại"** — 1 màu tối đặc, bỏ gradient,
  bỏ đổ bóng, tab đang chọn = nền cam Shopee ĐẶC (không gradient).
- **Bo góc:** người dùng chọn "gần vuông 2px" rồi bổ sung "kiểu phẳng Win 11". Dung hòa: theo **chuẩn
  Win 11** = **4px cho control / 6px cho thẻ** (Win 11 gốc là 4/8; ta siết thẻ về 6 để gần "vuông" hơn
  theo ý người dùng). Đây chỉ là vài token → sau đổi lại 2px hay 8px đều dễ.

**Hiện trạng đã khảo sát (2 bộ design TÁCH BIỆT, phải sửa cả hai):**

1. **Suite** — token tập trung ở `suite/Shopee.Suite/Themes/Theme.axaml`; header dựng ở
   `suite/Shopee.Suite/MainWindow.axaml`; bo góc/đổ bóng inline rải ở 9 view module + WelcomeView.
   Font UI = Inter nhúng (`avares://Avalonia.Fonts.Inter/Assets#Inter`). Bo góc hiện: 6/8/9/10/12/14/15/16.
2. **Đơn hàng** — palette ở `orders/XuLyDonShopee.App/Styles/Colors.axaml`, control ở
   `orders/.../Styles/Controls.axaml` (bo 5/6/8/10 + nhiều BoxShadow). Font đã là
   `Segoe UI Variable Text, Segoe UI, Inter`. Mỗi view merge `ModuleResources.axaml` (kéo theo Colors.axaml)
   vào Resources riêng — các key (CardBg, Border014, TextSecondary…) là RIÊNG của orders, không phải của suite.

**Nguyên nhân "chữ mờ":** (a) chữ phụ tương phản thấp (suite `TextSecondaryBrush #6E727A`; orders
`TextMuted #8A8A8A`, `Text9A #9A9A9A`); (b) suite dùng Inter grayscale-AA còn orders dùng Segoe (Win11
native, sắc hơn trên Windows) → hai bộ chữ lệch độ nét.

## 2. Phạm vi

- **Làm:**
  1. Chuẩn hóa **bo góc** về thang 4px (control) / 6px (thẻ) trên CẢ hai bộ, gồm literal inline trong view.
  2. **Phẳng hóa:** đổi mọi gradient (header, chip tab, huy hiệu "S", nền cửa sổ orders) → màu ĐẶC; bỏ các
     BoxShadow trang trí.
  3. **Header tối phẳng:** nền tối đặc `#1B1F2A`, bỏ bóng, tab chọn nền cam đặc `#EE4D2D` bo 4.
  4. **Chữ sắc nét hơn:** tăng tương phản chữ phụ/mờ; bật `UseLayoutRounding` + `TextRenderingMode=Antialias`
     ở root; (tăng cường, có kiểm chứng) đổi font suite sang ưu tiên Segoe UI Variable, fallback Inter.
  5. **Palette nền/viền** theo tông trung tính Win 11.
- **KHÔNG làm:**
  - KHÔNG đổi bố cục/luồng chức năng, KHÔNG đổi logic ViewModel.
  - KHÔNG thêm/đổi TÊN các resource-key override FluentTheme không tồn tại (comment trong Theme.axaml đã cảnh
    báo: sai key → vỡ template TabControl/DataGrid → view trống). CHỈ đổi GIÁ TRỊ của key/literal đang có.
  - KHÔNG đụng hub web (`server/`).
  - KHÔNG đổi hình dạng pill/tròn có chủ đích: toggle switch (track 12 / knob 10), `nav-pill` (2), avatar tròn,
    chấm trạng thái.

## 3. Các bước thực hiện

> Thực thi tuần tự trong 1 lượt (design coherence). Build 1 lần cuối. Bảng giá trị là BẮT BUỘC theo đúng số.

### Bước 1 — Suite: `suite/Shopee.Suite/Themes/Theme.axaml`

Đổi GIÁ TRỊ (giữ nguyên tên key & cấu trúc):

- `WindowBackgroundBrush` `#F5F7FA` → `#F3F3F3`
- `BorderBrush` `#D7D9DE` → `#E1E2E5`
- `SubtleBrush` `#EEF1F5` → `#F2F3F5`
- `TextSecondaryBrush` `#6E727A` → `#565B66`   ← fix chính "chữ mờ"
- **Header/nav phẳng:**
  - `NavBarBrush`: đang là `LinearGradientBrush` (#252B3B→#191D28) → đổi thành `SolidColorBrush` màu `#1B1F2A`.
  - `NavActiveBrush`: đang `LinearGradientBrush` (#FF6E4E→#E0421D) → `SolidColorBrush` `#EE4D2D`.
  - `BrandBadgeBrush`: đang `LinearGradientBrush` (#FF6E4E→#E0421D) → `SolidColorBrush` `#EE4D2D`.
  - `NavTextBrush` `#9BA3B5` → `#A7AFC0` (chữ tab nghỉ rõ hơn trên nền tối).
- **Bo góc (đổi Value):**
  - `ControlCornerRadius` `6` → `4`
  - `ControlTheme Button` CornerRadius `6` → `4` (dòng ~109)
  - `Button.card` CornerRadius `12` → `6` (dòng ~187)
  - `Border.card` CornerRadius `8` → `6` (dòng ~254)
  - `ListBox.topnav ListBoxItem` + `:pointerover` + `:selected` CornerRadius `9` → `4` (3 chỗ, dòng ~313/321/326)
  - `Button.ribbon` CornerRadius `6` → `4` (dòng ~376)
  - `Button.brand` CornerRadius `0` → giữ `0`.

### Bước 2 — Suite header: `suite/Shopee.Suite/MainWindow.axaml`

- Dòng ~15: BỎ thuộc tính `BoxShadow="0 2 12 0 #33000000"` trên Border header (giữ `BorderThickness` đáy 1px).
- Dòng ~21: huy hiệu "S" `CornerRadius="10"` → `6`; dòng ~22: BỎ `BoxShadow="0 2 6 0 #40E0421D"`.

### Bước 3 — Suite các view: literal bo góc + đổ bóng

Quy tắc chuẩn hóa: **chip/badge/pill nhỏ, icon-box, ô nhập → 4; thẻ/panel/khối log → 6.** Cụ thể:

- `Views/WelcomeView.axaml`: badge "S" `CornerRadius="15"` → `6`, BỎ `BoxShadow` dòng ~9; icon-box tile `11` → `6`.
- `Views/ComingSoonView.axaml`: icon-box `16` → `6`; chip `12` → `4`.
- `Modules/BigSeller/BigSellerView.axaml`: chip `14` → `4`; khối log `6` → `6` (giữ).
- `Modules/Accounts/AccountsView.axaml`: chip `14` → `4`.
- `Modules/Workspace/WorkspaceView.axaml`: `6.5` → `4`; chip `14` → `4`; badge `10` (dòng ~131) → `4`; badge `10`
  (dòng ~169) → `4`; thẻ `8` (dòng ~376) → `6`; badge trạng thái `10` (dòng ~382) → `4`; log `8` (dòng ~469/493) → `6`.
- `Modules/Settings/SettingsView.axaml`: chip `14` → `4`; khối `6` (dòng ~48) → giữ `6` hoặc `4` (thống nhất 4).
- `Modules/Fleet/FleetView.axaml`: khối `8` (dòng ~23/53) → `6`; khối `6` (dòng ~103) → `4`.
- `Modules/Search/SearchView.axaml`: chip `14` → `4`; log `8` → `6`.
- `Modules/CheckAccount/CheckAccountView.axaml`: chip `14` → `4`; log `8` → `6`.

### Bước 4 — Đơn hàng: `orders/XuLyDonShopee.App/Styles/Colors.axaml`

- `TextMuted` `#8A8A8A` → `#6E6E6E`
- `Text9A` `#9A9A9A` → `#7A7A7A`
- (`TextSecondary #4A4A4A` giữ nguyên — đã đủ đậm.)
- Nền cửa sổ `WindowTop #F7F8FA` / `WindowBottom #F0F2F5`: đặt CẢ HAI = `#F3F3F3` (khử gradient nền — xem bước 6).

### Bước 5 — Đơn hàng: `orders/XuLyDonShopee.App/Styles/Controls.axaml`

- Bo góc → **4**: `.accent` (dòng ~20), `.secondary` (~54), `.accentOutline` (~86), `.iconDanger` (~114),
  `.ghostIcon` (5→4, ~137), `Border.field` (~181), `ComboBox.field` (~230), `AutoCompleteBox.field` (~246),
  `NumericUpDown.field` (~263), `ListBox.nav ListBoxItem` + presenter (~283/290), `ListBox.navTop ListBoxItem`
  + presenter (8→4, ~357/364), `Button.logClear` (5→4, ~543).
- Bo góc → **6**: `Border.card` (10→6, ~150), `DataGrid.proxy` (8→6, ~508), `Border.acct-card` (8→6, ~461).
- BỎ đổ bóng: `.accent` `BoxShadow` (~21), `Border.card` `BoxShadow` (~151), `Border.acct-card:selected`
  `BoxShadow` (~472). GIỮ `BoxShadow` của knob toggle (~489).
- GIỮ toggle track/knob CornerRadius (12/10) và `nav-pill` (2).

### Bước 6 — Đơn hàng các view: gradient nền + literal bo góc

- `Views/MainView.axaml`: có `LinearGradientBrush` nền cửa sổ (dùng WindowTop/WindowBottom) → sau bước 4 hai
  stop đã bằng nhau nên phẳng; nếu còn gradient literal khác thì đặt về đặc `#F3F3F3`.
- Quét nhanh `orders/.../Views/*.axaml` (OrdersView, AccountsView, SettingsView, MainView, OrderDetailDialog,
  ConfirmDialog): literal `CornerRadius` cho chip/badge → `4`, cho thẻ/dialog → `6`. GIỮ avatar tròn, chấm
  trạng thái, pill toggle.

### Bước 7 — Chữ sắc nét (root render + font)

- `suite/Shopee.Suite/MainWindow.axaml` (thẻ `<Window>`): thêm `UseLayoutRounding="True"` và
  `RenderOptions.TextRenderingMode="Antialias"`.
- `orders/.../Views/MainView.axaml` (root UserControl): thêm `RenderOptions.TextRenderingMode="Antialias"`
  (UseLayoutRounding mặc định True).
- **(Tăng cường, CÓ kiểm chứng)** Trong `Theme.axaml`, đổi `UiFont` từ Inter-nhúng sang ưu tiên Segoe UI
  Variable, fallback Inter nhúng:
  `Segoe UI Variable Text, Segoe UI, avares://Avalonia.Fonts.Inter/Assets#Inter`
  → Windows dùng Segoe (sắc, Win11-native, KHỚP module đơn hàng); Linux fallback Inter nhúng.
  **BẮT BUỘC kiểm chứng:** build + chạy thử, xác nhận (1) chữ hiện đúng tiếng Việt có dấu, (2) không lỗi
  load font. **Nếu cú pháp composite URI không load được** (chữ biến mất/ô vuông) → GIỮ NGUYÊN
  `avares://Avalonia.Fonts.Inter/Assets#Inter` và ghi rõ trong báo cáo để Fable quyết.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` toàn solution THÀNH CÔNG, 0 error (chạy ở gốc repo).
- [ ] App khởi động được, KHÔNG view nào trắng trơn (đặc biệt TabControl/DataGrid — dấu hiệu vỡ template do
      sai key). Mở lần lượt các tab module + màn Welcome + màn Đơn hàng.
- [ ] Header: nền tối ĐẶC một màu, KHÔNG gradient, KHÔNG bóng đổ; tab đang chọn nền cam đặc bo góc nhỏ (~4px).
- [ ] Không còn bo góc ≥8 ở control/chip; thẻ bo ~6; nút/ô nhập bo ~4 (soi bằng mắt vài màn tiêu biểu).
- [ ] Chữ phụ đậm/rõ hơn trước (so nền); không còn cảm giác "mờ".
- [ ] `git diff` chỉ động vào file trong phạm vi (Theme/MainWindow/9 view suite + Colors/Controls/view orders).
      KHÔNG đổi code C#/logic.
- [ ] Báo cáo ghi rõ: font cuối cùng dùng gì (Segoe hay giữ Inter) và vì sao.

## 5. Rủi ro & lưu ý

- **Vỡ template FluentTheme:** tuyệt đối không đổi TÊN/không thêm key override lạ; chỉ đổi VALUE. Sau khi sửa
  phải mở app kiểm tra TabControl (tab module) + DataGrid (Workspace/Proxy) không trống.
- **Font composite (bước 7):** đây là điểm rủi ro nhất. Có fallback an toàn (giữ Inter) + phải kiểm chứng
  runtime, không được để chữ biến mất. Nếu nghi ngờ, ưu tiên GIỮ Inter và báo lại.
- **Hai palette riêng:** key trùng tên (AccentBrush, CardBg, TextSecondary…) nhưng nằm ở 2 file khác nhau
  (suite Theme.axaml vs orders Colors.axaml) — sửa ĐÚNG file theo bước, đừng nhầm.
- **Đường dẫn tương đối tính từ gốc repo** `d:\Projects\shopee-suite`. Nếu chạy trong worktree thì quy về thư
  mục làm việc của agent, không đọc/ghi cây chính.
- Số dòng (~) chỉ để định vị nhanh; bám theo TÊN key/giá trị literal thực tế khi sửa.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Hoàn thành 16 file (11 suite + 5 orders). Build 0 error, 899 test xanh, font Segoe verified.
Fable chỉnh thêm khi nghiệm thu: ribbon-active đổi sang kiểu Win 11 nhẹ (nền cam nhạt + gạch chân
accent + chữ/icon cam, thay khối cam kín); huy hiệu ✓ doneBadge trả về hình tròn (6.5). Đã commit 4eea103.
