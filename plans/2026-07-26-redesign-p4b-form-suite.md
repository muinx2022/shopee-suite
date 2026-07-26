# Plan: Redesign GĐ4b — Áp hệ nút/icon vào các form SUITE

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh

GĐ4a đã dựng xong NỀN (commit `98e97b7`): bộ icon vector, token màu 2 module về một mối, và **luật nút mới của
người dùng**. GĐ4b = áp vào các form của **suite**. GĐ4c (form module Đơn hàng) chạy SAU vì phụ thuộc Bước 0.

**LUẬT NÚT (người dùng chốt, ghi đè spec handoff) — nhắc lại vì đây là thứ phải áp:**
- **Một dáng duy nhất cho mọi nút**: nền trắng, viền `#E2DDD8` LUÔN THẤY RÕ, bo 5, cao 30, chữ `#423C38`.
- **KHÔNG nút nào tô nền.** Màu ngữ nghĩa **CHỈ nằm ở ICON**: chính → cam, xóa/nguy hiểm → đỏ, thành công →
  xanh, trung tính → xám.
- Ngoại lệ đã duyệt: nút **ribbon** (nền trong suốt, tint cam khi hover/active; icon đen → cam khi active).

**Người dùng vừa nêu (2026-07-26):** *"mấy icon cho đồng bộ hết — bên cấu hình BigSeller nút Lưu một kiểu, bên
Shopee nút Lưu kiểu khác"*. Đây chính là việc của 4b/4c.

## 2. BƯỚC 0 — Gỡ chặn kiến trúc (làm TRƯỚC, 4c phụ thuộc)

**Vấn đề:** `AppIcons.cs` nằm ở `suite/Shopee.Suite/Infrastructure/`, mà chiều tham chiếu là
`Shopee.Suite → XuLyDonShopee.App` (KHÔNG có chiều ngược) ⇒ các form module Đơn hàng **không với tới được**
bộ icon ⇒ GĐ4c bế tắc.

**Cách giải:** đưa các icon HÀNH ĐỘNG xuống project TẦNG DƯỚI dạng ResourceDictionary XAML để **cả hai module
dùng chung một nguồn**:
1. Tạo `orders/XuLyDonShopee.App/Styles/Icons.axaml` — `ResourceDictionary` chứa **toàn bộ icon hành động** dạng
   `<StreamGeometry x:Key="IconPlay">M…</StreamGeometry>` (key đặt `Icon` + tên trong bảng §3 của plan 4a:
   `IconPlay`, `IconStop`, `IconRefresh`, `IconSave`, `IconDelete`, `IconAdd`, `IconImport`, `IconExport`,
   `IconFolder`, `IconOpenExternal`, `IconFilter`, `IconEdit`, `IconCheck`, `IconCheckAll`, `IconUncheck`,
   `IconUpgrade`, `IconChart`, `IconLogin`, `IconSparkle`, `IconSync`, `IconClose`, `IconPause`).
   → Dùng `StreamGeometry` (không phải string) để `Data="{StaticResource IconSave}"` không phụ thuộc chuyển kiểu.
2. Merge vào **cả hai** nơi:
   - orders: `Styles/ModuleResources.axaml` (mỗi view orders đã merge file này).
   - suite: `Themes/Theme.axaml` (hoặc `App.axaml`) qua
     `avares://XuLyDonShopee.App/Styles/Icons.axaml` — suite tham chiếu orders nên URI này hợp lệ.
3. **`AppIcons.cs` GIỮ LẠI 10 icon ĐIỀU HƯỚNG** (`Dashboard`, `Database`, `Search`, `People`, `Servers`,
   `Inventory`, `Receipt`, `PlayCircle`, `SwapHoriz`, `Settings`) vì ribbon bind qua ViewModel (cần `string` ở C#).
   **XÓA 23 icon hành động khỏi AppIcons.cs** sau khi đã chuyển sang XAML — **một nguồn duy nhất**, không nhân bản.
   Giữ bảng ánh xạ (comment) ở AppIcons.cs, ghi rõ icon hành động nay nằm ở `Icons.axaml`.
4. **Kiểm chứng:** cả 2 module resolve được key (build + render harness như 4a đã làm).

## 3. Các form SUITE cần áp (Bước 1…)

Mỗi nút: **bỏ emoji/ký tự trong `Content`**, thay bằng `PathIcon` + nhãn chữ; gán class theo vai trò
(`primary`/`destructive`/`success`/không class); nút chỉ-icon dùng `Classes="iconOnly"`.
**Tra bảng §3 của plan 4a — mỗi hành động ĐÚNG MỘT icon.** (Ví dụ: mọi nút "Tải lại/Làm mới" ở MỌI màn đều dùng
`IconRefresh`, không nơi nào còn `↻`/`⟳`/`⟲`/`🔄`.)

| File | Việc chính |
|---|---|
| `Modules/BigSeller/BigSellerView.axaml` | 💾 Lưu → `IconSave`; 🔐 Đăng nhập → `IconLogin`; 🗑 Xóa Medias → `IconDelete`; ↻ → `IconRefresh`; `…` chọn file → `IconFolder` |
| `Modules/Data/DataView.axaml` | 🔍 Lọc → `IconFilter`; ➕ → `IconAdd`; 🆕 Sinh SKU → `IconSparkle`; 🗑 → `IconDelete`; ⟳ → `IconRefresh`; ↺ → `IconRefresh` |
| `Modules/Search/SearchView.axaml` | ↻ · 📦 Xuất → `IconExport`; 🤖 → `IconSparkle`; 🗑 → `IconDelete`; ✔/✖ chọn → `IconCheckAll`/`IconUncheck`; 📂 → `IconFolder`; ↗ → `IconOpenExternal`; ⏯ → `IconResume` |
| `Modules/Accounts/AccountsView.axaml` | ⬇ Import → `IconImport`; 💾 → `IconSave`; ⟲ Đồng bộ → `IconSync`; − Xóa → `IconDelete`; + → `IconAdd` |
| `Modules/CheckAccount/CheckAccountView.axaml` | ▶ Chạy → `IconPlay`; ■ Dừng → `IconStop`; 📂/📁 → `IconFolder`; ↻ → `IconRefresh` |
| `Modules/Fleet/FleetView.axaml` | ⟳ → `IconRefresh`; 🗑 → `IconDelete`; ▶/⏸ → `IconPlay`/`IconPause` |
| `Modules/Settings/SettingsView.axaml` + `UnifiedSettingsView.axaml` | 💾 → `IconSave`; ⬆/🔄 Cập nhật → `IconUpgrade`; 🔌/■ Kết nối → `IconSync`/`IconStop` |
| `Views/WelcomeView.axaml`, `Views/ComingSoonView.axaml` | rà lại nút/thẻ cho khớp dáng chung |
| `MessageDialog.axaml(.cs)`, `Modules/Data/RowEditWindow.axaml`, `ImportAccountsWindow.axaml`, `ScrapeStatsWindow.axaml` | ✔ Lưu → `IconSave`; Đồng ý → `IconCheck`; Hủy/Đóng → `IconClose`; nút dựng ở code-behind (`MessageDialog`) cũng phải theo dáng chung |
| `Modules/Workspace/WorkspaceView.axaml` | **4 nút op**: bỏ tô NỀN CAM khi chạy → **icon cam + viền cam**, nền giữ trắng (theo luật nút mới). Glyph `▶ ⇧ ✎ ●` → `IconPlay`/`IconImport`/`IconEdit`/`IconSparkle`. Giữ huy hiệu ✓ xanh. Các nút khác trong màn: theo bảng. |

**Dọn kèm:** xóa màu hard-code còn sót trong view suite (vd `Button.wsAction` `#4B5563`/`#1EA055`/`#EAF8F0` ở
WorkspaceView) → dùng token.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` 0 error; `dotnet test XuLyDonShopee.Tests` xanh.
- [ ] **KHÔNG còn emoji nào trong `Content` của `<Button>` ở thư mục `suite/`** (grep kiểm: 🗑 💾 🔐 📂 📁 📊 📦 🤖 🔍 ➕ 🆕 🔄 ⏯ ↻ ⟳ ⟲ ↺ ⬆ ⬇ ↗ ↶ ✔ ✖ ■ ▶ ✎ ●).
- [ ] Cùng một hành động → cùng một icon ở MỌI màn (đặc biệt: "Lưu" và "Tải lại" — 2 chỗ người dùng chỉ đích danh).
- [ ] Mọi nút cùng dáng, viền thấy rõ; màu chỉ ở icon; không nút nào tô nền (trừ ribbon).
- [ ] Nút op Workspace: chờ = icon xám, đang chạy = **icon + viền cam** (không tô nền), xong = huy hiệu ✓ xanh.
- [ ] `AppIcons.cs` chỉ còn 10 icon điều hướng; icon hành động chỉ tồn tại ở `Icons.axaml` (không nhân bản).
- [ ] Kiểm chứng bằng render harness: không icon nào trống ở CẢ hai module.
- [ ] KHÔNG đụng `orders/**/Views/*` (để 4c) trừ `Styles/Icons.axaml` + `ModuleResources.axaml` của Bước 0.

## 5. Rủi ro & lưu ý

- **Bước 0 là chặn của 4c** — làm cẩn thận và kiểm chứng resolve được ở CẢ hai module trước khi qua Bước 1.
- Nhiều nút hiện đặt `Content="🗑 Xóa"` — khi tách icon phải dùng `<StackPanel Orientation="Horizontal">` +
  `PathIcon` + `TextBlock`, giữ nguyên `Command`/`ToolTip.Tip`/`IsVisible`/`IsEnabled`.
- `MessageDialog` dựng nút bằng **code-behind** → phải sửa ở `.cs`, đừng bỏ sót.
- Giữ nguyên MỌI hành vi/binding — đây là việc TRÌNH BÀY.
- Không đụng `MainWindow.axaml` phần bố cục shell (đã duyệt); chỉ được sửa nếu có nút emoji cần thay icon.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
