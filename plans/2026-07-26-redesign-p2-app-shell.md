# Plan: Redesign GĐ2 — App shell (tab strip · ribbon · status bar) theo handoff

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh & mục tiêu

Tiếp GĐ1 (token ấm đã xong, commit `4a1e...`). GĐ2 dựng lại **khung app (shell)** theo mockup
**"Ribbon Window"** của bộ handoff: dải tab trên + ribbon + thanh trạng thái đáy.

**NGUỒN CHUẨN (đọc trước khi sửa):**
- `C:\Users\Ng Xuan Mui\Downloads\Windows form Shopee Manager\design_handoff_shopee_suite\README.md`
  → mục **"Screen 1 — App shell"** + **"Status bar"** + Design Tokens.
- `...\Ribbon Window.dc.html` (đọc CSS inline lấy số chính xác). `support.js` = BỎ QUA.

**Hiện trạng:** `suite/Shopee.Suite/MainWindow.axaml` — Grid 4 hàng: Row0 dải nav TỐI (brand + ListBox.topnav
chip), Row1 ribbon (ItemsControl `SelectedTab.Groups`, mỗi nhóm = hàng nút + nhãn nhóm ở đáy), Row2 nội dung,
Row3 footer counter. Style ở `Themes/Theme.axaml` (`ListBox.topnav`, `Button.ribbon`, `Button.brand`).
ViewModel: `ViewModels/ShellViewModel.cs` + `RibbonModels.cs` (RibbonTab/RibbonGroup/RibbonScreenItem/
RibbonActionItem/RibbonToggleItem — `RibbonScreenItem` có `PathIcon Data`, `RibbonActionItem` có `Glyph` chuỗi).

## 2. Phạm vi

- **Làm:** restyle + dựng lại Row0 (tab strip), Row1 (ribbon), Row3 (status bar) theo spec.
  Chỉ sửa: `suite/Shopee.Suite/MainWindow.axaml`, `suite/Shopee.Suite/Themes/Theme.axaml`, và
  `ViewModels/ShellViewModel.cs` + `ViewModels/RibbonModels.cs` NẾU cần thêm property hiển thị.
- **KHÔNG làm:**
  - KHÔNG đụng `Modules/Workspace/*` (GĐ3 đang chạy song song trong worktree — CHẠM LÀ CONFLICT).
  - KHÔNG đụng `orders/` (GĐ4).
  - KHÔNG làm breadcrumb strip + ô tìm kiếm toàn cục của mockup (đó là nội dung mẫu của screen 1; app ta
    dùng page-header riêng từng màn — xem mockup 2).
  - KHÔNG đổi logic điều hướng / command hiện có.
- **QUYẾT ĐỊNH ĐÃ CHỐT — giữ khung cửa sổ HỆ ĐIỀU HÀNH:** mockup vẽ title bar riêng có nút ─ ▢ ✕. Ta GIỮ
  chrome native của Windows (không `ExtendClientArea`) để không phá kéo-thả/snap/maximize. Thay vào đó **gộp
  app-mark + tên app vào ĐẦU DẢI TAB** (trái) — giảm tầng chrome, vẫn giữ nhận diện. Ghi rõ trong báo cáo.

## 3. Các bước thực hiện

### Bước 1 — Dải TAB (Row 0) — spec §"Tab strip"

- Nền **TRẮNG** (`CardBackgroundBrush`), bỏ hẳn nền tối (`NavBarBrush` cũ). Viền đáy `#f0edea`. Padding ngang 12.
- Bên trái: **app mark** = ô vuông cam bo 5px (kích thước ~20–22px cho cân với tab) + chữ "Shopee Suite"
  12.5px/600 màu `TextPrimaryBrush`; giữ `Button.brand` (bấm về Workspace) nhưng nền trong suốt, hover `#f2efec`.
- Tab: padding `16,9,16,11`, **13px/600**; chữ thường `#7d756f`, hover nền `#faf8f6` + chữ `#2c2724`;
  **ACTIVE = chữ `#2c2724` + THANH CHỈ BÁO 3px màu `#ee4d2d` ở ĐÁY tab, thụt vào 12px mỗi bên** (KHÔNG tô nền
  chip như hiện tại). Tab canh ĐÁY dải.
  → Sửa style `ListBox.topnav` trong Theme.axaml: bỏ CornerRadius/nền chip, dựng indicator bằng Border 3px
  (đặt trong ItemTemplate hoặc qua template ListBoxItem) — chọn cách nào chắc chắn không vỡ template.
- Bên phải dải: chữ gợi ý mờ 11.5px `#a8a09b`: "Ctrl + 1…4 để chuyển tab".
  **Kèm phím tắt thật**: Ctrl+1..4 chuyển tab (KeyBinding trên Window → command trong ShellViewModel chọn
  `Tabs[i]` nếu tồn tại). Nếu làm phím tắt phát sinh rủi ro, vẫn phải làm cho khớp chữ gợi ý — đừng ghi chữ suông.

### Bước 2 — RIBBON (Row 1) — spec §"Ribbon"

- Nền trắng, **min-height 112**, viền trên `#f2efec`, viền dưới `#e5e1dd`, cuộn ngang khi hẹp
  (`ScrollViewer.HorizontalScrollBarVisibility=Auto`).
- Mỗi **nhóm**: hàng nút (trên) + **caption 10.5px muted canh giữa** (dưới) — giữ cấu trúc hiện có.
  Giữa các nhóm: **vạch dọc 1px `#efecea`**, thụt 2px trên / 6px dưới.
- **Nút LỚN** (RibbonScreenItem + RibbonActionItem): rộng **76–78**, dọc, **icon/glyph 20–21px màu CAM**
  (`AccentBrush`) + nhãn 11.5px canh giữa (cho phép 2 dòng), radius 5, viền trong suốt;
  **hover: nền `#fff3ef` + viền `#fbd6ca`**; **ACTIVE (màn đang mở): giữ NGUYÊN tint đó thường trực**
  (nền `#fff3ef` + viền `#fbd6ca`, chữ/icon cam) — thay kiểu gạch-chân hiện tại.
- Giữ `RibbonToggleItem` (checkbox) như hiện tại, chỉnh cho cân hàng.

### Bước 3 — STATUS BAR (Row 3) — spec §"Status bar"

- Cao **32**, nền `#f2efec`, viền trên `#e5e1dd`, chữ 11.5px `#55504c`.
- Các **đoạn (segment)**: cao hết thanh, padding ngang 12, `nowrap`, **hover `#e8e3df`**; ngăn nhau bằng
  **vạch dọc 1×16px `#dcd6d1`**.
- Trái → phải:
  1. **Chấm xanh 7px NHẤP NHÁY** (opacity 1 → 0.3 → 1, chu kỳ 2.4s) + trạng thái job: "Đang chạy · N job" /
     "Rảnh · không có job". Lấy N từ dữ liệu sẵn có (ShellViewModel/WorkspaceStatus — tự khảo sát; nếu chưa có
     đếm job thì dùng nguồn gần nhất đang có, KHÔNG bịa số).
  2. Các counter hiện có (giữ nguyên nội dung `ShopeeStatusVm.Status*Text` + `WorkspaceStatus.*`), gom thành
     đoạn theo cụm thay vì chuỗi dấu `·` như hiện tại.
  3. Đẩy sang phải: cụm "Trình duyệt: …", "N máy online", và **phiên bản app** (đọc từ nguồn version sẵn có —
     `version.txt`/Assembly; tự khảo sát, KHÔNG hard-code).
- **Quy tắc co (BẮT BUỘC):** thanh KHÔNG được xuống dòng / cắt giữa chữ. Cửa sổ hẹp (< ~1240px) → **ẩn bớt
  đoạn ưu tiên thấp** (counter phụ) và rút gọn nhãn; version LUÔN hiện. Dùng binding theo Bounds.Width của
  Window hoặc converter — miễn không vỡ layout.

### Bước 4 — Dọn token nav cũ

Sau khi shell sáng: `NavBarBrush/NavBarBorderBrush/NavDividerBrush/NavItemHoverBrush/NavTextBrush/
NavTextHoverBrush/NavActiveBrush/BrandBadgeBrush` cập nhật theo giá trị mới (hoặc bỏ nếu hết chỗ dùng — kiểm
grep trước khi xoá; các key `Sidebar*` có thể còn ai dùng, KHÔNG xoá bừa).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution 0 error; `dotnet test XuLyDonShopee.Tests` xanh.
- [ ] Mở app: dải tab TRẮNG, tab đang chọn có **gạch cam 3px ở đáy** (không còn chip cam); ribbon nền trắng
      cao ~112 với icon CAM + caption nhóm + vạch ngăn dọc; nút ribbon hover/active ra tint cam nhạt.
- [ ] Status bar cao 32 nền `#f2efec` với chấm xanh nhấp nháy + các đoạn có vạch ngăn + hover; thu hẹp cửa sổ
      xuống ~1100px KHÔNG vỡ/không xuống dòng; version luôn thấy.
- [ ] Ctrl+1..4 chuyển tab đúng.
- [ ] KHÔNG file nào trong `Modules/Workspace/` bị sửa (GĐ3 song song); không đụng `orders/`.
- [ ] Không view nào trắng trơn (TabControl + DataGrid còn vẽ).

## 5. Rủi ro & lưu ý

- **Template ListBox/ListBoxItem:** dựng gạch-chân active dễ vỡ template. Nếu `/template/` khó bám, chuyển
  `ListBox.topnav` sang `ItemsControl` + `ToggleButton`/`RadioButton` cũng được — miễn giữ binding
  `Tabs`/`SelectedTab` và hành vi chọn tab.
- **KHÔNG đụng thư mục Modules/Workspace** — agent GĐ3 đang sửa song song trong worktree.
- Chấm nhấp nháy: dùng Animation của Avalonia (`Style.Animations` + `KeyFrames` opacity, `IterationCount=Infinite`).
- Giữ mọi command/binding hiện có; đây là việc TRÌNH BÀY, không đổi hành vi.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
