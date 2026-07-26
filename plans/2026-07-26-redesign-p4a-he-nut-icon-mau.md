# Plan: Redesign GĐ4a — Nền: đồng nhất hệ NÚT (icon + màu) toàn app

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh & mục tiêu

Người dùng: *"rà soát lại toàn bộ button ở tất cả form của app, mỗi nơi dùng icon mỗi kiểu, màu sắc mỗi kiểu,
không đồng nhất"*. Đã audit toàn bộ (~95 nút, 28 file `.axaml`) — kết quả:

**Ba cơ chế icon chạy song song:**
| Kiểu | Số lượng | Ví dụ |
|---|---|---|
| Emoji màu | ~34 nút (36%) | 🗑 💾 🔐 📂 📊 📦 🤖 🔍 ➕ 🆕 🔄 |
| Ký tự đơn sắc | ~34 nút (36%) | ▶ ■ ✕ ✓ ✔ ✖ ↻ ⟳ ⟲ ↺ ⬆ ⬇ ↗ ↶ ⏯ |
| Chữ thuần | ~24 nút (25%) | "Làm mới", "Xuất CSV", "Đóng" |
| **PathIcon vector** | ~13 (3%) | CHỈ ở nút điều hướng ribbon |

**Cùng một hành động, icon khác nhau khắp nơi:** "Tải lại" có tới **4 glyph + chữ** (`↻` `⟳` `⟲` `🔄` "Làm mới");
"Xóa" có `🗑` `−` `✖` `↺` "Xóa"; "Dừng" có `■` và `✕`; "Lưu" có `💾` `✔` "Lưu"; "Mở thư mục" có `📂` `📁` `↗` `…`.

**Màu loạn:** token trùng tên nhưng khác giá trị giữa 2 module (`SuccessBrush` #00783C vs #2E7D32; `DangerBrush`
#C8463C vs #C62828); **4 sắc đỏ** cho cùng vai trò (thêm #C0392B ở `.iconDanger`); **3 sắc xanh**; và **2 nút
XANH DƯƠNG `#1976D2` lạc lõng** giữa app cam (`ConfirmDialog.axaml:12`, `OrderDetailDialog.axaml:58`).

**Bộ design handoff CHỈ THỊ RÕ:** *"Icons are Unicode glyphs as placeholders. **Replace with the codebase's real
icon set** (Lucide / Fluent UI icons recommended); keep the same meaning and position."* → hướng đi đã chốt:
**một bộ icon vector duy nhất**.

**GĐ4a = NỀN.** Áp vào từng form là GĐ4b (suite) + GĐ4c (Đơn hàng), chạy sau.

## 2. Phạm vi

- **Làm (chỉ 4 file nền):**
  1. `suite/Shopee.Suite/Infrastructure/AppIcons.cs` — mở rộng thành bộ icon hành động đầy đủ + **bảng ánh xạ
     hành động → icon** (tài liệu ngay trong file).
  2. `suite/Shopee.Suite/Themes/Theme.axaml` — thêm class `.destructive`; bổ sung class nút icon-nhỏ dùng chung.
  3. `orders/XuLyDonShopee.App/Styles/Colors.axaml` — kéo palette về ĐÚNG bộ token của handoff (khớp suite).
  4. `orders/XuLyDonShopee.App/Styles/Controls.axaml` — các class nút của orders về cùng ngôn ngữ với suite.
- **KHÔNG làm ở GĐ4a:** KHÔNG sửa nội dung các view (đó là 4b/4c) — trừ 2 chỗ hard-code màu xanh dương phải xoá
  ngay vì nó nằm trong file style-lân cận (xem Bước 5). KHÔNG đụng `Modules/Workspace/*` và `MainWindow.axaml`
  (đã redesign xong, 4b sẽ xử lý phần icon của Workspace).

## 3. Bảng ánh xạ CHUẨN — hành động → icon (áp cho CẢ 2 module)

Mỗi hành động **đúng một** icon. Đây là hợp đồng cho 4b/4c dùng:

| Hành động | Tên icon | Thay cho các glyph đang dùng |
|---|---|---|
| Chạy / Bắt đầu | `Play` | ▶ · "Chạy" |
| Dừng | `Stop` | ■ · ✕ (nghĩa dừng) · "Dừng" |
| Tạm dừng / Tiếp tục | `Pause` / `Resume` | ⏸ ⏯ |
| Tải lại / Làm mới | `Refresh` | ↻ ⟳ ⟲ 🔄 · "Làm mới" |
| Lưu | `Save` | 💾 ✔ ✓ · "Lưu" |
| Xóa | `Delete` | 🗑 − ✖ · "Xóa" |
| Thêm | `Add` | + ➕ |
| Nhập (import) | `Import` | ⬇ · "Import" |
| Xuất (export) | `Export` | ⬆ 📦 · "Xuất CSV" |
| Mở thư mục / duyệt | `Folder` | 📂 📁 … |
| Mở liên kết ngoài | `OpenExternal` | ↗ |
| Lọc | `Filter` | 🔍 ▽ · "Lọc" |
| Sửa | `Edit` | ✎ ✏ |
| Chọn tất cả | `CheckAll` | ✓ ✔ |
| Bỏ chọn | `Uncheck` | ✖ ✕ (nghĩa bỏ chọn) |
| Cập nhật app | `Upgrade` | ⬆ 🔄 |
| Thống kê | `Chart` | 📊 ▤ |
| Đăng nhập / khoá | `Login` | 🔐 |
| Sinh mới / AI | `Sparkle` | 🆕 🤖 |
| Đồng bộ | `Sync` | ⇅ ⟲ |
| Hủy / đóng | `Close` | ✕ ↶ · "Hủy" · "Đóng" |

**Nguồn path data:** Fluent/Material 24×24 đơn sắc (khuôn 10 icon sẵn có trong `AppIcons.cs` — `Dashboard`,
`Database`, `Search`, `People`, `Servers`, `Inventory`, `Receipt`, `PlayCircle`, `SwapHoriz`, `Settings`).
Giữ nguyên 10 icon cũ (ribbon đang dùng), CHỈ THÊM.

## 3b. ⚠️ HỆ NÚT — CHỈ THỊ MỚI CỦA NGƯỜI DÙNG (GHI ĐÈ spec handoff)

Người dùng chốt (2026-07-26, sau khi xem bản redesign): *"không dùng bg cho button, chỉ dùng icon màu, button
dùng default; chỗ thì có border chỗ thì không (hoặc border cùng nền không nhìn thấy) — thống nhất button dùng
mặc định, chỉ cần thêm icon nếu cần thiết"*.

⇒ **BỎ toàn bộ nút tô nền.** Handoff spec ghi "primary = nền cam đặc" — **KHÔNG theo nữa**, ý người dùng thắng.

**Luật nút DUY NHẤT toàn app:**
1. **Một kiểu dáng cho mọi nút:** nền `CardBackgroundBrush` (trắng), **viền `#E2DDD8` LUÔN THẤY RÕ**, radius 5,
   cao 30, chữ `TextBodyBrush` (#423C38). Không có nút nào "viền trùng nền" hay mất viền.
2. **Màu ngữ nghĩa CHỈ nằm ở ICON** (chữ giữ màu thường):
   - Hành động chính → icon **cam** `AccentBrush`
   - Xóa / nguy hiểm → icon **đỏ** `DangerBrush`
   - Thành công / xác nhận → icon **xanh** `SuccessBrush`
   - Trung tính → icon xám `TextSecondaryBrush`
3. **Hover:** viền đậm hơn + nền rất nhẹ `#FAF8F6` (cần phản hồi khi rê chuột — đây KHÔNG phải "nút tô nền").
   **Pressed:** nền `#F2EFEC`. **Disabled:** mờ như hiện tại.
4. **Bỏ các class tô nền:** suite `.primary`/`.danger`/`.success` và orders `.accent` → tất cả về DÁNG MẶC ĐỊNH,
   chỉ khác màu icon. Giữ TÊN class (view đang dùng) nhưng đổi định nghĩa thành "đặt màu icon", để 4b/4c không
   phải sửa hàng loạt ngay.
5. **Ngoại lệ giữ nguyên (KHÔNG áp luật này):**
   - **Nút ribbon** — người dùng đã duyệt ("phần ribbon thì rất ổn rồi"): giữ nền trong suốt + tint cam khi
     hover/đang mở.
   - **Huy hiệu ✓ xanh** góc nút op (chỉ báo trạng thái, không phải nút).
6. **Nút op ở Workspace** (4 nút Scrape/Import/Update/Tên SP) hiện tô NỀN CAM khi đang chạy — theo luật mới đổi
   thành: **icon cam + viền cam**, nền giữ trắng. Vẫn nhận ra ngay "đang chạy" mà không tô nền.
   → Việc sửa nút op thuộc GĐ4b (file `Modules/Workspace/`), KHÔNG làm ở 4a.

## 4. Bảng token màu CHUẨN (một nguồn, 2 module giống hệt)

Kéo `orders/Colors.axaml` về đúng giá trị handoff (suite đã đúng từ GĐ1):

| Token | Giá trị chuẩn | Orders hiện tại → sửa |
|---|---|---|
| Accent | `#EE4D2D` | giống ✔ |
| AccentHover | `#D8401F` | `#E0431F` → sửa |
| AccentText (brand text) | `#C93B1D` | `#C0341C` → sửa |
| Success | `#1F8A4C` | `#2E7D32` → sửa |
| Danger | `#C22B1E` | `#C62828` → sửa |
| Warning | `#B06F06` | (Amber #F5A623) → sửa |
| Info | `#2B5FC4` | `#1565C0` → sửa |
| Nền app | `#F7F5F3` | WindowTop/Bottom → sửa |
| Surface | `#FFFFFF` | giống ✔ |
| Subtle surface | `#FBFAF9` | — thêm |
| Border | `#E2DDD8` | các `Border0xx` alpha đen → đổi sang hex ấm |
| Text primary | `#2C2724` | `#1C1C1C` → sửa |
| Text body | `#423C38` | `#4A4A4A` → sửa |
| Text secondary | `#7D756F` | `#5A5A5A` → sửa |
| Text muted | `#A8A09B` | `#8A8A8A`/`#9A9A9A` → sửa |

**Xóa hẳn:** mọi sắc đỏ lẻ (`#C0392B`, `#C62828`), xanh lẻ (`#2E7D32`, `#1EA055` — trừ `SuccessDotBrush #3FA860`
dùng cho huy hiệu ✓ theo spec), và **2 nút xanh dương `#1976D2`**.

## 5. Các bước thực hiện

1. **`AppIcons.cs`** — thêm các icon ở bảng §3 (đơn sắc, 24×24, đặt tên đúng như bảng). Ghi **bảng ánh xạ §3
   thành comment ngay đầu file** để 4b/4c tra cứu, kèm câu: "mỗi hành động ĐÚNG MỘT icon — thêm icon mới phải
   cập nhật bảng này".
2. **`Theme.axaml`** —
   - Thêm `Button.destructive`: chữ + viền `DangerBrush` (#C22B1E), hover nền `DangerBgBrush` (#FDECEB). Dùng cho
     **xóa thật** (xóa dữ liệu/tài khoản); `.danger` giữ nguyên nghĩa "Dừng tất cả" (cam nhạt) như GĐ1 đã chốt.
   - Thêm `Button.iconOnly` (nút vuông chỉ icon, ~30×30, radius 4) để 4b/4c dùng thay các nút icon tự chế.
   - Thêm style cho `PathIcon` trong nút: cỡ mặc định 16, màu kế thừa `Foreground` của nút (để nút đổi màu thì
     icon đổi theo — KHÔNG hard-code màu icon).
3. **`orders/Colors.axaml`** — đổi giá trị theo bảng §4. **GIỮ NGUYÊN TÊN KEY** (rất nhiều view orders đang bind).
   Key nào không còn dùng thì để lại, đừng xóa ở bước này (4c dọn).
4. **`orders/Controls.axaml`** — đưa các class về cùng ngôn ngữ với suite:
   - `.accent` = nút CHÍNH filled cam (đã đúng, chỉ chỉnh màu hover theo token mới).
   - `.secondary` = nút phụ trắng viền `#E2DDD8` (khớp Button mặc định của suite).
   - `.accentOutline` = viền cam chữ cam.
   - `.iconDanger` → chuyển sang dùng token Danger chuẩn (bỏ `#C0392B`); cân nhắc đổi tên thành `.destructive`
     cho khớp suite (nếu đổi tên phải cập nhật view orders → việc đó thuộc 4c; GĐ4a có thể khai **cả hai** tên
     trỏ cùng style để 4c chuyển dần).
   - Bo góc/chiều cao khớp spec (nút 30, radius 5; nút nhỏ radius 4).
5. **Xóa 2 nút xanh dương lạc lõng** — `orders/XuLyDonShopee.App/Views/ConfirmDialog.axaml:12` và
   `OrderDetailDialog.axaml:58`: bỏ hex `#1976D2`, dùng class `.accent`. (Ngoại lệ duy nhất được đụng view ở 4a
   vì đây là lỗi màu rõ ràng, sửa 1 dòng/file.)

## 6. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution 0 error; `dotnet test XuLyDonShopee.Tests` xanh.
- [ ] Mở app: KHÔNG view nào trắng trơn; các màn hiện có vẫn hiển thị đúng (4a chưa đổi view nên chỉ đổi TÔNG màu
      của module Đơn hàng cho khớp suite).
- [ ] Module Đơn hàng chuyển sang tông ẤM giống suite (nền `#F7F5F3`, chữ `#2C2724`, viền `#E2DDD8`) — không còn
      lệch tông giữa 2 module.
- [ ] KHÔNG còn nút xanh dương `#1976D2` nào.
- [ ] `AppIcons.cs` có đủ icon ở bảng §3 + bảng ánh xạ dạng comment.
- [ ] Không đụng `Modules/Workspace/*`, `MainWindow.axaml`.
- [ ] Báo cáo liệt kê: icon đã thêm, token đã đổi (cũ→mới), class nút sau khi thống nhất.

## 7. Rủi ro & lưu ý

- **Giữ nguyên TÊN key** trong `orders/Colors.axaml` — đổi tên là vỡ hàng loạt view orders.
- Orders nạp `Controls.axaml` ở cấp `UserControl` nên style của nó **thắng** theme suite ở phạm vi orders — đó là
  chủ ý, giữ vậy; chỉ cần GIÁ TRỊ khớp nhau.
- Đổi `.iconDanger` sang tông đỏ chuẩn sẽ hơi khác mắt so với hiện tại — đúng mục tiêu.
- Icon path data phải là đường dẫn hợp lệ; sai cú pháp `Data` → PathIcon không vẽ (không nổ build). Sau khi thêm,
  **phải mở app xem thử ít nhất vài nút**, hoặc render harness như GĐ2 đã làm.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
