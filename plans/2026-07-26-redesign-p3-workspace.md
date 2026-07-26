# Plan: Redesign GĐ3 — Màn BigSeller Workspace theo handoff

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — **WORKTREE**

## 1. Bối cảnh & mục tiêu

Tiếp GĐ1 (token ấm đã xong). GĐ3 dựng lại **màn BigSeller Workspace** theo mockup **"BigSeller Workspace"**
của bộ handoff. (GĐ2 dựng shell — chạy SONG SONG ở cây chính, KHÔNG được đụng file của nhau.)

**NGUỒN CHUẨN (đọc trước khi sửa):**
- `C:\Users\Ng Xuan Mui\Downloads\Windows form Shopee Manager\design_handoff_shopee_suite\README.md`
  → mục **"Screen 2 — BigSeller Workspace"** (page header · hint strip · account list · sub-tabs · card ·
  shop table · run config) + Design Tokens.
- `...\BigSeller Workspace.dc.html` — đọc CSS inline lấy số chính xác. `support.js` = BỎ QUA.

**Hiện trạng** `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.axaml`:
Grid 3 hàng — Row0 header (title + description + chip Status bên phải), Row1 banner "việc dở" + thanh
[↻ Tải lại] + hướng dẫn + [■ Dừng tất cả], Row2 = Grid 2 cột (300px list acc | TabControl 5 tab:
"Shop & cấu hình" · Thống kê · Dữ liệu · Theo dõi Scrape · Theo dõi Update). Trong tab 1: card thông tin acc
(email + chip cookie + nút Thống kê / Đăng nhập-cấu hình) + `DataGrid x:Name="ShopGrid"` (cột Shop + 4 cột op)
+ khối "Cấu hình CHẠY". VM: `WorkspaceViewModel.cs`, `WorkspaceAccountViewModel.cs`, `WorkspaceShopViewModel.cs`.

## 2. Phạm vi

- **Làm:** dựng lại giao diện màn Workspace theo spec. CHỈ sửa file trong
  `suite/Shopee.Suite/Modules/Workspace/` (View + VM của module này).
- **KHÔNG làm:**
  - **KHÔNG sửa `suite/Shopee.Suite/Themes/Theme.axaml`** (GĐ2 đang sửa file đó ở cây chính → conflict).
    Style mới đặt **CỤC BỘ** trong `<UserControl.Styles>` của WorkspaceView.
  - KHÔNG đụng `MainWindow.axaml` / `ViewModels/` cấp shell (GĐ2).
  - KHÔNG đụng `orders/`.
  - KHÔNG đổi logic chạy việc / command / binding nghiệp vụ — đây là việc TRÌNH BÀY.
  - Các tab "Thống kê / Dữ liệu / Theo dõi Scrape / Theo dõi Update": GIỮ NGUYÊN nội dung, chỉ đổi style
    thanh sub-tab cho khớp spec (spec ghi rõ chỉ thiết kế tab "Shop & cấu hình").

## 3. Các bước thực hiện

### Bước 1 — Page header (spec §"Page header")

- Nền `#fbfaf9`, viền đáy `#eae6e2`, padding `18,14,18,12` (trái/trên/phải/dưới).
- Trái: tiêu đề **"BigSeller Workspace" 17px/700, letter-spacing −0.2**; mô tả 12px `#7d756f` (giữ nguyên chữ
  hiện có, cho xuống dòng đẹp).
- Phải: nút phụ **"⟳ Tải lại"** (cao 30, nền trắng, viền `#e2ddd8`) + nút **"■ Dừng tất cả"**
  (nền `#fff3ef`, viền `#f6c6bb`, chữ `#c93b1d`/600, hover nền `#ffe8e1`).
  → Chuyển 2 nút này TỪ thanh Row1 hiện tại LÊN header; giữ nguyên command đang bind.
- Chip "N tài khoản BigSeller" hiện ở góc phải header: giữ, đặt cho cân (hoặc chuyển thành chữ muted).

### Bước 2 — Hint strip (spec §"Hint strip")

- Dải 1 dòng dưới header: nền `#fffdfc`, viền đáy `#f0ece8`, chữ 11.5px `#7d756f`, **1 dòng, tràn thì …**
  (`TextTrimming=CharacterEllipsis`, KHÔNG wrap).
- Nội dung: giữ câu hướng dẫn hiện có (đang nằm ở thanh Row1), diễn đạt theo spec:
  "ℹ Bấm 1 tài khoản → chạy theo từng shop (▶ scrape · ⇧ import · ✎ update · ⬤ tên SP). Nút nền cam = đang
  chạy, bấm lại để dừng · dấu ✓ góc nút = đã xong."
- **Banner "việc dở"** (tính năng đã có: liệt kê + Tiếp tục/Hủy) → GIỮ NGUYÊN chức năng, đặt ngay dưới hint
  strip, style lại cho hợp tông ấm (nền tint cam `#fff3ef`, viền `#f6c6bb`).

### Bước 3 — Cột trái: list tài khoản (spec §"Left column")

- Rộng **292**, nền `#fbfaf9`, viền phải `#eae6e2`.
- Header: **"TÀI KHOẢN BIGSELLER"** (10.5px/700, letter-spacing 0.7, muted) + số lượng ("6 tài khoản").
- Thẻ acc: padding `11,9`, radius 6, viền `#efebe7`, nền trắng; **hover viền `#e2ddd8`**;
  **CHỌN = nền `#fff3ef` + viền `#f6c6bb`**.
- Nội dung thẻ: **chấm tròn 6px** (chọn = `#ee4d2d`, thường = `#cfc8c2`) + email 12.5px/600 (ellipsis);
  dòng 2 thụt 14px, 11px: "✓ cookie" màu xanh `#1f8a4c` · "0/3 shop đã scrape" muted.
  → Map vào dữ liệu hiện có (`DisplayName`, `CookieStatus`, `ScrapeSummary`).

### Bước 4 — Sub-tabs (spec §"Sub-tabs")

- Dải nền `#f7f5f3`, viền đáy `#eae6e2`, padding `18,10,18,0`, cuộn ngang khi hẹp.
- Tab **đang chọn**: chữ `#ee4d2d` + **gạch chân 2px cam thụt 10px**; tab thường `#7d756f`, hover `#2c2724`.
- Áp cho TabControl hiện có (style cục bộ trong View, KHÔNG sửa Theme.axaml).

### Bước 5 — Card + BẢNG SHOP (spec §"Card" — phần quan trọng nhất)

- Card: nền trắng, viền `#ebe7e3`, radius 8, padding `16,14,16,16`.
- Header card (cho phép xuống dòng): email 14.5px/700 · **pill xanh "✓ Đã có cookie BigSeller"**
  (nền `#eafaf0`, chữ `#1f8a4c`, radius 20, 11px/600) · giãn · nút phụ "▤ Thống kê" · nút **primary cam**
  "Đăng nhập / cấu hình ↗".
- Caption 11.5px muted: "Mỗi shop = 1 sheet — bấm nút trên dòng shop để chạy đúng shop đó."
- **Bảng shop:** viền `#eeeae6`, radius 7, cuộn ngang cả khối khi hẹp.
  - Cột: **SHOP · SCRAPE · IMPORT · UPDATE · TÊN SP** — 4 cột op rộng 58, canh giữa; **KHÔNG có cột Tiến độ**
    (đúng hiện trạng sau khi user yêu cầu bỏ).
  - Header: nền `#fbfaf9`, **CHỮ IN HOA** 10.5px/700 muted, letter-spacing 0.4 (viết hoa thẳng trong header text).
  - Dòng: tên shop 12.5px/600, ellipsis khi tràn.
  - **NÚT OP (điểm cốt lõi):** **40×30**, radius 5.
    - *Chờ (idle)*: nền trắng, viền `#e2ddd8`, glyph `#55504c`.
    - *Đang chạy*: **nền + viền `#ee4d2d`, glyph TRẮNG**.
    - *Xong*: kiểu idle + **huy hiệu tròn ✓ 14px màu `#3fa860`** ở góc trên-phải (lệch −5/−5), dấu ✓ trắng 9px.
    - Hover: viền `#f6c6bb`.
    - Glyph: ▶ scrape · ⇧ import · ✎ update · ⬤ tên SP.
    → VM `WorkspaceShopViewModel` đã có `*Running`, `*Done`, `*ToggleContent`, `*ToggleEnabled` — TÁI DÙNG,
      chỉ đổi cách hiển thị. Lưu ý `*ToggleContent` hiện đổi glyph sang "■" khi chạy; theo spec **nền cam đã
      thể hiện "đang chạy"**, nên có thể giữ glyph gốc và bỏ đổi sang ■ (tooltip vẫn nói "bấm để dừng") —
      chọn phương án nào thì GHI RÕ trong báo cáo.
- **Cấu hình chạy:** tiêu đề "CẤU HÌNH CHẠY · tài khoản này · máy này" (11px/700, ls 0.5, muted; phần đuôi
  nhạt hơn/400). Ô số dàn lưới tự co (mỗi ô tối thiểu ~112px), nhãn 11.5px `#7d756f` NẰM TRÊN ô cao 30
  (viền `#e2ddd8`, radius 5, 12.5px; focus viền cam). Ô đường dẫn: lưới tối thiểu ~280px, ô **mono 12px** +
  nút "…" 34×30. Ghi chú chân 11px muted (giữ chữ hiện có).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution 0 error (trong worktree); `dotnet test XuLyDonShopee.Tests` xanh.
- [ ] Màn Workspace: header `#fbfaf9` + hint strip 1 dòng; list acc 292px thẻ có chấm tròn, chọn = nền cam nhạt
      + viền cam; sub-tab gạch chân cam; card bo 8 với pill cookie xanh + nút primary cam.
- [ ] Bảng shop: header CHỮ HOA muted; **nút op 40×30** thể hiện đủ 3 trạng thái (trắng / **nền cam khi chạy** /
      **✓ xanh góc khi xong**); không có cột Tiến độ.
- [ ] Mọi command cũ vẫn chạy (Tải lại, Dừng tất cả, 4 nút op, ContextMenu xoá tiến độ, banner việc dở
      Tiếp tục/Hủy, Đăng nhập/cấu hình, Thống kê).
- [ ] **KHÔNG sửa** `Themes/Theme.axaml`, `MainWindow.axaml`, `ViewModels/` cấp shell, `orders/`.
- [ ] Không tab nào trắng trơn (DataGrid + 5 sub-tab còn vẽ).

## 5. Rủi ro & lưu ý

- **Worktree:** mọi đường dẫn quy về thư mục làm việc của agent; TUYỆT ĐỐI không đọc/ghi cây chính.
- **Không đụng Theme.axaml** — style mới để trong `<UserControl.Styles>` của WorkspaceView. Được DÙNG token
  của Theme (DynamicResource) vì GĐ1 đã thêm: `BrandTintBrush` #FFF3EF, `BrandTintStrongBrush` #FFE8E1,
  `BrandTintBorderBrush` #FBD6CA, `BrandTintBorderStrongBrush` #F6C6BB, `SuccessBgBrush` #EAFAF0,
  `SuccessDotBrush` #3FA860, `IdleDotBrush` #D3CCC7, `BorderLightBrush` #EBE7E3, `RowHoverBrush` #FFFAF8,
  `TextBodyBrush` #423C38, `TextMutedBrush` #A8A09B, `BrandTextBrush` #C93B1D, `Border.pill` + biến thể.
- Huy hiệu ✓ đè góc nút: đã có `Border.doneBadge` (13px, tròn) trong View — chỉnh theo spec (14px, `#3fa860`).
- Nút op đổi sang "nền cam khi chạy": hiện dùng `Button.wsAction.running` (xanh `#1EA055`) → đổi sang cam theo
  spec.
- Giữ `x:Name="ShopGrid"` + `SelectionChanged="ShopGrid_SelectionChanged"` (code-behind đang dùng).

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
