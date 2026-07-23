# Plan: Màn Cài đặt hiển thị theo chế độ + tách phần Update dùng chung

- **Ngày:** 2026-07-23
- **Trạng thái:** hoàn thành (build suite 0/0; 911 test)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nhánh:** `feature/che-do-settings` (worktree `d:\Projects\shopee-suite-wt-chedo2`)

## 1. Bối cảnh & mục tiêu

Tính năng "Chế độ ứng dụng" (Full/Workspace/Shopee) đã có: gate tab ở ribbon theo chế độ. Nhưng **màn Cài
đặt vẫn hiện đủ mọi section** ở mọi chế độ. Người dùng muốn màn Cài đặt cũng theo chế độ:
- **Shopee** → chỉ hiện cài đặt Shopee (Đơn hàng).
- **Workspace** → chỉ hiện cài đặt Workspace (Hiệu năng · Đồng bộ Hub).
- **Full** → hiện đủ.
- **Phần UPDATE (Phiên bản & cập nhật) DÙNG CHUNG — luôn hiện ở CẢ 3 chế độ** (vì cả 3 đều cập nhật).
- Mục chọn chế độ (selector) vẫn luôn hiện.

**Hiện trạng (đã khảo sát):**
- `UnifiedSettingsView.axaml`: ScrollViewer gồm: (1) section "Chế độ ứng dụng" (luôn hiện, có selector +
  "Lưu & khởi động lại"); (2) section "SHOPEE SUITE" + `ContentControl {Binding Suite}`; (3) section "ĐƠN
  HÀNG" + `ContentControl {Binding Orders}` (ẩn khi `HasOrders` false).
- `UnifiedSettingsViewModel`: `Suite` (SettingsViewModel), `Orders` (nullable), `HasOrders`, selector chế độ.
- **Card "Phiên bản & cập nhật" đang NẰM LỒNG trong `SettingsView.axaml`** (tab "Hiệu năng", cột phải, các
  dòng ~68–92) — cùng chỗ với Hiệu năng (trần Brave) + "Máy của bạn". Nên KHÔNG thể chỉ ẩn nguyên khối
  `Suite` ở Shopee mode (sẽ ẩn luôn Update).
- Card Update bind: `AppVersionText`, `UpdateStatus`, `UpdateSupported`, `CheckUpdateCommand`, `UpdateReady`,
  `ApplyUpdateCommand`, `UpdateNotSupported`. Converter `StringToBool` khai báo cấp APP (`App.axaml` dòng 20)
  ⇒ dùng được ở `UnifiedSettingsView`. `MonoFont` là DynamicResource (theme) ⇒ có sẵn.
- Helper có sẵn: `AppModeStore.ShowsWorkspace(mode)` (Full|Workspace), `ShowsShopee` (Full|Shopee).

**Mục tiêu:** tách card Update thành section RIÊNG luôn hiện ở `UnifiedSettingsView`; gate section Suite
(Hiệu năng · Hub) theo chế độ có Workspace; section Đơn hàng giữ gate theo `HasOrders`.

## 2. Phạm vi

- **Làm:** thêm cờ hiển thị theo chế độ ở `UnifiedSettingsViewModel`; tách card Update sang
  `UnifiedSettingsView` (luôn hiện); gate section Suite; bỏ card Update khỏi `SettingsView.axaml` (tránh trùng).
- **Không làm:**
  - KHÔNG đổi logic Update/cập nhật (chỉ DI CHUYỂN card + bind qua `Suite.*`).
  - KHÔNG đụng nội dung 2 tab "Hiệu năng"/"Đồng bộ nhiều máy" (ngoài việc bỏ card Update khỏi tab Hiệu năng).
  - KHÔNG đụng module Đơn hàng, ShellViewModel, App.axaml, luồng đăng nhập/nhánh khác.
  - KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — `UnifiedSettingsViewModel.cs`

- Thêm `public bool ShowsWorkspaceSettings { get; }` = `AppModeStore.ShowsWorkspace(AppModeStore.Shared.Current)`
  (đọc trong ctor, gán readonly — chế độ không đổi giữa vòng đời vì đổi = restart).
- Giữ `Suite`, `Orders`, `HasOrders`, selector như cũ.

### Bước 2 — `UnifiedSettingsView.axaml`

Thứ tự section trong StackPanel:
1. **CHẾ ĐỘ ỨNG DỤNG** (luôn hiện) — giữ nguyên.
2. **PHIÊN BẢN & CẬP NHẬT** (MỚI, luôn hiện): thêm header Border (mẫu như các header khác, text "PHIÊN BẢN
   & CẬP NHẬT", caption "Cập nhật dùng chung cho mọi chế độ.") + chép NGUYÊN card Update từ
   `SettingsView.axaml` (dòng ~68–92), đổi mọi `{Binding X}` → `{Binding Suite.X}`:
   `Suite.AppVersionText`, `Suite.UpdateStatus`, `Suite.UpdateSupported`, `Suite.CheckUpdateCommand`,
   `Suite.UpdateReady`, `Suite.ApplyUpdateCommand`, `Suite.UpdateNotSupported`. Giữ converter
   `{StaticResource StringToBool}` (cấp app) + `{DynamicResource MonoFont}`.
3. **WORKSPACE** (đổi tên header "SHOPEE SUITE" → "WORKSPACE" cho khớp mô hình; caption "Hiệu năng · đồng bộ
   Hub"): header Border + `ContentControl {Binding Suite}` — **cả hai** bọc `IsVisible="{Binding
   ShowsWorkspaceSettings}"`.
4. **ĐƠN HÀNG** (giữ nguyên gate `IsVisible="{Binding HasOrders}"` cho cả header + ContentControl).

Kết quả: Full → mọi section; Workspace → Chế độ + Update + Workspace (không Đơn hàng); Shopee → Chế độ +
Update + Đơn hàng (không Workspace).

### Bước 3 — `SettingsView.axaml`

- Bỏ card "Phiên bản & cập nhật" (Border Classes="card", dòng ~68–92) khỏi cột phải tab "Hiệu năng" (giờ hiện
  ở UnifiedSettings). Cột phải còn card "Máy của bạn". Giữ nguyên phần còn lại + tab "Đồng bộ nhiều máy".
- KHÔNG xóa property/command Update trong `SettingsViewModel` (vẫn bind từ UnifiedSettings qua `Suite.*`).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build suite/Shopee.Suite` — 0 error, 0 warning mới. (`dotnet test orders/XuLyDonShopee.Tests`
      không đụng nhưng chạy cho chắc — xanh.)
- [ ] Rà XAML: mọi bind trong card Update di chuyển đã đổi sang `Suite.*`; không còn card Update trong
      `SettingsView.axaml`; section Workspace bọc `ShowsWorkspaceSettings`, Đơn hàng bọc `HasOrders`.
- [ ] Logic 3 chế độ đúng: Full đủ; Workspace (Update+Workspace, không Đơn hàng); Shopee (Update+Đơn hàng,
      không Workspace); selector + Update luôn hiện.

## 5. Rủi ro & lưu ý

- Card Update dùng `{Binding UpdateReady}` v.v. — sau khi di chuyển vào UnifiedSettingsView (DataContext =
  UnifiedSettingsViewModel), PHẢI prefix `Suite.` cho MỌI bind của card (kể cả trong `IsVisible`
  converter). Sót một bind (vd `ApplyUpdateCommand` không prefix) → nút chết.
- `SettingsView` DataContext là `SettingsViewModel` (qua ContentControl {Binding Suite}) → card Update NẾU
  còn trong SettingsView vẫn bind trực tiếp; ta BỎ nó ở SettingsView nên không xung đột.
- Nhánh này off `feature/gop-don-hang`; cùng lúc có việc SSO đang sửa `ShopeeLoginService.cs` trên cây
  chính (khác file hẳn — không đụng nhau). Fable merge lần lượt.
- Đổi tên header "SHOPEE SUITE"→"WORKSPACE" chỉ là nhãn hiển thị; không đổi `x:Class`/binding.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày thực thi:** 2026-07-23 · **Người thực thi:** Opus (`opus-dev`)

### File đã sửa (3)

1. `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsViewModel.cs`
   - Thêm `public bool ShowsWorkspaceSettings { get; }` (read-only auto-property, kèm XML doc).
   - Gán trong ctor: `ShowsWorkspaceSettings = AppModeStore.ShowsWorkspace(current);` (tái dùng biến `current`
     đã đọc từ `AppModeStore.Shared.Current`). Không đổi gì khác.

2. `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.axaml`
   - Thêm section MỚI "PHIÊN BẢN & CẬP NHẬT" (header Border + card Update) giữa section "Chế độ ứng dụng" và
     section Workspace — LUÔN hiện (không gate). Card Update chép từ `SettingsView.axaml`, MỌI bind đổi sang
     `Suite.*` (8 bind: `Suite.AppVersionText`, `Suite.UpdateStatus` ×2 gồm cả trong `IsVisible` converter,
     `Suite.UpdateSupported`, `Suite.CheckUpdateCommand`, `Suite.UpdateReady`, `Suite.ApplyUpdateCommand`,
     `Suite.UpdateNotSupported`). Card bọc `Margin="28,14" HorizontalAlignment="Left" MaxWidth="520"` để căn lề
     đồng nhất với section selector + để text `TextWrapping="Wrap"` xuống dòng đẹp (xem "Điểm lệch").
   - Đổi header cũ "SHOPEE SUITE" → "WORKSPACE", caption "Hiệu năng · đồng bộ Hub · phiên bản & cập nhật" →
     "Hiệu năng · đồng bộ Hub". Bọc CẢ header Border LẪN `ContentControl {Binding Suite}` bằng
     `IsVisible="{Binding ShowsWorkspaceSettings}"`.
   - Section "Đơn hàng" giữ nguyên gate `HasOrders`.

3. `suite/Shopee.Suite/Modules/Settings/SettingsView.axaml`
   - Bỏ nguyên card "Phiên bản & cập nhật" (Border Classes="card" + comment "── Phiên bản + tự cập nhật
     (Velopack) ──") khỏi cột phải tab "Hiệu năng". Cột phải còn card "Máy của bạn" (giữ nguyên, kể cả
     `Margin="0,0,0,14"`).
   - Cập nhật comment mô tả cột phải cho khớp (bỏ "+ phiên bản/cập nhật", ghi chú card đã tách sang
     UnifiedSettingsView).
   - KHÔNG đụng `SettingsViewModel.cs` — mọi property/command Update giữ nguyên (đã verify còn public: bind
     `Suite.*` resolve được).

### Kết quả build/test

- `dotnet build suite/Shopee.Suite/Shopee.Suite.csproj`: **Build succeeded — 0 Warning, 0 Error.**
- `dotnet test orders/XuLyDonShopee.Tests`: **Passed! — Failed: 0, Passed: 911, Skipped: 0, Total: 911.**
- Rà XAML (grep): 8/8 bind card Update trong UnifiedSettingsView đều prefix `Suite.`; SettingsView không còn
  bind Update nào; converter `StringToBool` xác nhận cấp app (`App.axaml` trong `<Application.Resources>`),
  `MonoFont` ở `Themes/Theme.axaml` (include app-level) → cả hai resolve ở UnifiedSettingsView.

### Điểm lệch so với spec / cần phiên chính soi

- **Card Update thêm `Margin="28,14" HorizontalAlignment="Left" MaxWidth="520"`** (plan chỉ nói "chép NGUYÊN
  card"). Lý do: card gốc nằm trong Grid cột phải của SettingsView (tự có lề + bề rộng cột); đặt trần vào
  StackPanel của ScrollViewer sẽ dính mép trái x=0 và stretch full-width, lệch hẳn mọi section khác (đều
  padding 28) và làm dòng `TextWrapping="Wrap"` không có mốc để wrap. Margin/Align/MaxWidth chọn để đồng nhất
  với section "Chế độ ứng dụng" (`Margin="28,14" HorizontalAlignment="Left"`). KHÔNG đổi binding/logic.
- **Sửa 1 comment trong SettingsView** (mô tả cột phải) cho khớp việc vừa bỏ card — nằm ngay tại điểm sửa,
  không phải "chỗ khác".
- `Margin="0,0,0,14"` của card "Máy của bạn" giữ nguyên theo plan ("giữ nguyên phần còn lại") → còn 14px
  khoảng trắng thừa dưới đáy cột (vô hại). Nếu muốn sạch tuyệt đối có thể bỏ, nhưng plan dặn không đụng.
- Chưa chạy app thật để mắt thấy 3 chế độ (worktree, không release) — logic hiển thị đã verify qua đọc XAML +
  cờ `ShowsWorkspaceSettings`; đề nghị phiên chính xác nhận trực quan khi nghiệm thu nếu cần.
