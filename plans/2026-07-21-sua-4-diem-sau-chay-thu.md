# Plan: Sửa 4 điểm người dùng báo sau chạy thử (refresh sau sync shop, checkbox lệch, tên tab, màu cam)

- **Ngày:** 2026-07-21
- **Trạng thái:** hoàn thành (2026-07-21 — Fable nghiệm thu: build 0 lỗi + 769/769 test + kiểm thị giác; đồng ý giữ xanh cho UsageBrushes.Accent vì là màu ngữ nghĩa trạng thái, đổi cam sẽ lẫn đỏ Captcha)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** cây làm việc CHÍNH `D:\Projects\shopee-suite`, nhánh `feature/gop-don-hang`. KHÔNG đụng `main`, `server/`, `shared/`.

## 1. Bốn điểm người dùng báo (2026-07-21, sau khi vọc app)

1. **BUG — Sync shop từ BigSeller báo thành công nhưng sang tab Đơn hàng KHÔNG thấy shop.**
   Nguyên nhân đã biết: màn Tài khoản (orders) chỉ `Reload()` khi `SelectedNavIndex` THAY ĐỔI
   (`MainViewModel.OnSelectedNavIndexChanged`); index mặc định 0 = Tài khoản nên vào lại tab
   Đơn hàng không đổi index → không reload → danh sách hiển thị bản nạp lúc mở app.
2. **UI — checkbox "Xóa profile và tạo lại" trên ribbon bị lệch** ô vuông so với text.
3. **Text — đổi tên tab "Đơn hàng" thành "Shopee"** (tab strip trên cùng).
4. **Màu không đồng nhất — phần suite (Workspace...) đang accent XANH `#0078D7`, phần Đơn hàng
   cam Shopee `#EE4D2D` → người dùng chốt: đổi accent của SUITE sang CAM Shopee** cho đồng nhất
   toàn app.

## 2. Các bước

### A. Refresh danh sách tài khoản sau sync shop (sửa tận gốc bằng event)

1. `orders/XuLyDonShopee.App/Services/AppServices.cs`: thêm event
   `public event Action? AccountsChanged;` + `public void RaiseAccountsChanged()` — mẫu y hệt
   cặp `OrdersChanged`/`RaiseOrdersChanged()` sẵn có trong file.
2. `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: trong ctor subscribe
   `services.AccountsChanged` → handler nạp lại danh sách: nếu
   `Dispatcher.UIThread.CheckAccess()` thì `Reload()` thẳng, không thì
   `Dispatcher.UIThread.Post(Reload)` (mẫu marshal như MainViewModel xử lý OrdersChanged).
   LƯU Ý: `Reload()` đã tồn tại và đã được gọi khi vào tab — ngữ nghĩa không đổi.
3. `suite/Shopee.Suite/Modules/BigSeller/BigSellerViewModel.cs` — trong `SyncShopsToOrders`:
   sau vòng lặp, nếu `added > 0` → `svc.RaiseAccountsChanged();` (đặt trước dòng set `Status`).
4. Test (`orders/XuLyDonShopee.Tests/`): thêm test AccountsViewModel — insert account qua repo
   SAU khi VM đã dựng → `RaiseAccountsChanged()` → danh sách VM có dòng mới. Nếu
   `Dispatcher.UIThread` gây khó trong môi trường test thì test nhánh `CheckAccess()==true`
   (gọi từ thread test luôn được coi là có access hay không — kiểm thực tế; bất khả thi thì
   tách handler ra method internal `OnAccountsChangedForTest` và test method đó + kiểm wiring
   bằng cách raise event; ghi rõ cách chọn trong báo cáo).

### B. Checkbox ribbon lệch

5. `suite/Shopee.Suite/Themes/Theme.axaml` — style `CheckBox.ribbonToggle`: căn lại ô vuông và
   nhãn thẳng hàng (VerticalAlignment/VerticalContentAlignment Center, khoảng cách hộp-chữ hợp
   lý, chiều cao khớp các nút ribbon bên cạnh). Đối chiếu trực quan bằng screenshot khi chạy app.

### C. Tên tab

6. `suite/Shopee.Suite/ViewModels/ShellViewModel.cs`: RibbonTab tiêu đề `"Đơn hàng"` →
   `"Shopee"` (đổi cả navTitle/label hiển thị trên tab strip nếu tách riêng). Các nhãn nhóm
   trong ribbon ("Màn hình"/"Hành động"/"Tùy chọn") GIỮ NGUYÊN. Nếu logic "nhớ màn theo tab"
   key theo Title thì đổi đồng bộ để không mất state.

### D. Accent suite → cam Shopee

7. `suite/Shopee.Suite/Themes/Theme.axaml`: đổi token accent XANH sang CAM Shopee — ít nhất
   `AccentBrush #0078D7 → #EE4D2D`; RÀ TOÀN BỘ file tìm các token/giá trị dẫn xuất của accent
   (hover/pressed/selected/pill topnav, border focus...) đang hardcode tông xanh (`#0078D7`,
   `#106EBE`, `#005A9E`, các biến thể...) → đổi sang bộ cam tương ứng (cam đậm hơn cho
   hover/pressed, vd `#D8431F`/`#C23A1A` — chọn tông hài hòa với `#EE4D2D`, ghi rõ mapping
   trong báo cáo). KHÔNG đổi Success/Danger/Warning. KHÔNG đụng Colors.axaml của orders
   (đã cam sẵn).
8. Kiểm ảnh hưởng: grep `#0078D7` (và các mã xanh tìm thấy) toàn `suite/` — chỗ nào ngoài
   Theme.axaml hardcode màu xanh accent (axaml khác, C# `Brush.Parse`...) thì liệt kê; đổi các
   chỗ thuộc vai trò "accent" (vd nút primary), CHỪA lại chỗ mang nghĩa riêng (nếu phân vân,
   ghi vào báo cáo để Fable quyết).

### E. Kiểm chứng

9. `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi 0 warning; test orders → 767 baseline +
   test mới pass.
10. Chạy app + chụp màn hình: (a) tab strip hiện `Workspace / Cấu hình BigSeller / Shopee /
    Cài đặt`, pill tab + nút nav active + nút accent giờ CAM đồng nhất; (b) checkbox ribbon
    thẳng hàng; (c) màn Workspace/Dữ liệu không còn lộ accent xanh. Đóng app sau khi chụp.

## 3. Tiêu chí nghiệm thu

- [ ] Build sạch; test ≥767 + test mới pass.
- [ ] Sync shop từ BigSeller → tab Shopee (Đơn hàng cũ) thấy NGAY dòng mới không cần đổi màn
      (qua event — kiểm bằng test + đọc code wiring).
- [ ] Checkbox ribbon thẳng hàng (screenshot).
- [ ] Tab đổi tên "Shopee"; nhớ-màn-theo-tab vẫn hoạt động.
- [ ] Accent suite cam Shopee đồng nhất (screenshot), Success/Danger/Warning giữ nguyên,
      orders không đổi.

## 4. Rủi ro & lưu ý

- Event `AccountsChanged` bắn từ thread UI (command BigSeller chạy trên UI thread) nhưng viết
  handler chịu được cả thread nền (CheckAccess/Post) — về sau ai bắn từ nền cũng an toàn.
- Đổi màu: theme suite dùng token là chính nhưng có thể có chỗ hardcode — bước 8 là lưới quét,
  đừng bỏ.
- App có thể đang được người dùng mở — trước khi build, kiểm tra `ShopeeSuite` process; nếu
  đang chạy thì báo trong log và dừng nhẹ nhàng bằng CloseMainWindow (KHÔNG kill), chờ thoát
  rồi mới build.

---

## Báo cáo thực thi (Opus — 2026-07-21)

Trước khi build: phát hiện `ShopeeSuite` đang chạy (PID 16636). Đã đóng nhẹ bằng `CloseMainWindow()`
(KHÔNG kill), chờ thoát hẳn (~0s) rồi mới build. Xong việc cũng đã đóng app sau khi chụp.

### A. Refresh danh sách tài khoản sau sync shop (event)

- `orders/XuLyDonShopee.App/Services/AppServices.cs`: thêm `public event Action? AccountsChanged;` +
  `public void RaiseAccountsChanged() => AccountsChanged?.Invoke();` (ngay dưới cặp `OrdersChanged`,
  cùng doc-comment "có thể bắn từ thread nền → người nghe phải marshal").
- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: trong ctor thêm
  `_services.AccountsChanged += OnAccountsChanged;`; handler `private void OnAccountsChanged() => RunOnUi(Reload);`
  (đặt cạnh `OnSessionsChanged`). Dùng lại helper `RunOnUi` SẴN CÓ (CheckAccess→Reload thẳng, không thì Post)
  — đúng mẫu marshal yêu cầu; `Reload()` giữ lựa chọn/form/tick theo `_editingId`.
- `suite/Shopee.Suite/Modules/BigSeller/BigSellerViewModel.cs` — `SyncShopsToOrders`: sau vòng lặp,
  `if (added > 0) svc.RaiseAccountsChanged();` đặt NGAY TRƯỚC dòng gán `Status`. (`svc` = `OrdersModuleHost.Services`,
  CÙNG instance `AppServices` mà `AccountsViewModel` nghe → sự kiện tới đúng màn.)
- Test: `orders/XuLyDonShopee.Tests/AccountsViewModelTests.cs` thêm 2 test:
  `RaiseAccountsChanged_ThemTaiKhoanNgoaiMan_DanhSachVmCoDongMoi` (Insert ngoài màn → Raise → list có dòng mới)
  và `RaiseAccountsChanged_GiuLuaChonVaSuaDo` (giữ sửa dở + lựa chọn). Đã KIỂM THỰC TẾ: trong môi
  trường test này `Dispatcher.UIThread.CheckAccess()==true` trên thread test → `RunOnUi(Reload)` chạy
  ĐỒNG BỘ, nên chọn cách test thẳng end-to-end (raise event → assert list) như plan ưu tiên, KHÔNG phải
  tách `OnAccountsChangedForTest`.

### B. Checkbox ribbon lệch

- `suite/Shopee.Suite/Themes/Theme.axaml` — style `CheckBox.ribbonToggle`: BỎ `MinHeight=58` (thủ phạm:
  template Fluent để ô vuông trong wrapper top-align cao ~32 → ô vuông nằm TRÊN, chữ center trong 58 → lệch).
  Nay để checkbox cao TỰ NHIÊN (~32) + `VerticalAlignment=Center` + `VerticalContentAlignment=Center` →
  wrapper ô vuông lấp đúng chiều cao control nên ô vuông + chữ đều center → THẲNG HÀNG (không phụ thuộc
  tên phần tử template — robust).
- `suite/Shopee.Suite/MainWindow.axaml` — DataTemplate `RibbonToggleItem`: bọc CheckBox trong
  `<Border MinHeight="58" Margin="6,0">` (checkbox `VerticalAlignment=Center` bên trong). Border 58 giữ
  chiều cao hàng nút để NHÃN NHÓM "Tùy chọn" khớp baseline với "Màn hình"/"Hành động"; checkbox căn giữa
  trong Border. (Plan bước 5 chỉ nêu Theme.axaml — có mở rộng tối thiểu sang DataTemplate để vừa thẳng
  hàng ô-vuông/chữ vừa giữ chiều cao khớp nút bên cạnh; xác nhận bằng screenshot phóng to.)

### C. Tên tab

- `suite/Shopee.Suite/ViewModels/ShellViewModel.cs`: `new RibbonTab("Đơn hàng", …)` → `new RibbonTab("Shopee", …)`.
  Cập nhật thêm 2 comment doc/section cho khớp. Các nhãn nhóm ("Màn hình"/"Hành động"/"Tùy chọn") GIỮ NGUYÊN;
  màn con "Đơn hàng" (RibbonScreenItem trong tab) GIỮ NGUYÊN tên. "Nhớ màn theo tab" dùng
  `Dictionary<RibbonTab, object>` key theo THAM CHIẾU RibbonTab (KHÔNG theo Title) → đổi tên không mất state.
  Tab strip bind `RibbonTab.Title` (MainWindow.axaml) → hiển thị "Shopee" ngay.

### D. Accent suite → cam Shopee (mapping màu)

Đổi trong `suite/Shopee.Suite/Themes/Theme.axaml` + 2 shadow accent hardcode ngoài Theme:

| Token / vị trí | Vai trò | Xanh (cũ) | Cam (mới) |
|---|---|---|---|
| `AccentBrush` | accent chính | `#0078D7` | `#EE4D2D` |
| `AccentHoverBrush` | hover | `#106EBE` | `#D8431F` |
| `AccentPressedBrush` | pressed | `#005A9E` | `#C23A1A` |
| `BrandBadgeBrush` / `NavActiveBrush` (gradient, 2 brush) | huy hiệu "S" + pill tab đang chọn | `#2A93F0`→`#0A6BD8` | `#FF6E4E`→`#E0421D` |
| `SystemAccentColor` | Fluent accent | `#0078D7` | `#EE4D2D` |
| `SystemAccentColorLight1` | Fluent accent sáng | `#1C86E0` | `#FF6B4A` |
| `SystemAccentColorDark1` | Fluent accent đậm | `#106EBE` | `#D8431F` |
| `TextControlBorderBrushFocused` | viền TextBox focus | `#0078D7` | `#EE4D2D` |
| `ComboBoxBackgroundBorderBrushFocused` | viền ComboBox focus | `#0078D7` | `#EE4D2D` |
| `DataGridRowSelected*BackgroundBrush` (×4) | nền dòng chọn (opacity thấp) | `#0078D7` | `#EE4D2D` |
| `Button.card:pointerover` Background | nền hover thẻ Welcome | `#F4F9FF` | `#FFF4F1` |
| `WelcomeView.axaml` BoxShadow huy hiệu "S" | bóng accent | `#330A6BD8` | `#33E0421D` |
| `MainWindow.axaml` BoxShadow huy hiệu "S" (app bar) | bóng accent | `#400A6BD8` | `#40E0421D` |

- GIỮ NGUYÊN: `SuccessBrush/DangerBrush/WarningBrush` và biến thể; toàn bộ tông chrome tối trung tính
  (Sidebar*, NavBar*, NavText*, NavDivider — đây là navy/xám, KHÔNG phải accent xanh). Không đụng
  `orders/…/Colors.axaml` (đã cam sẵn). Các chỗ green `#1EA055`/`#EAF8F0` (WorkspaceView — trạng thái
  "xong") và amber `#FFF7E6` (DataView — warning) là màu NGHĨA RIÊNG, giữ nguyên.
- CHỪA + FLAG cho Fable: `suite/Shopee.Suite/Modules/Accounts/AccountItemViewModel.cs:12`
  `UsageBrushes.Accent = "#0078D7"` — màu chữ cột "Tình trạng" cho trạng thái **"Đã dùng"** trong bảng
  Tài khoản & Proxy (bảng màu 4 nghĩa: xanh=Đang dùng, accent=Đã dùng, đỏ=Captcha, xám=Chưa). Nếu đổi
  sang cam `#EE4D2D` sẽ RẤT GẦN đỏ Danger `#C8463C` → khó phân biệt "Đã dùng" với "⚠ Captcha". Đã GIỮ
  xanh + để Fable quyết (đổi sang một tông trung tính khác, hay giữ nguyên).

### Kiểm chứng

- **Build:** `dotnet build ShopeeSuite.sln -c Release` → **Build succeeded, 0 Warning(s), 0 Error(s)**.
- **Test:** `dotnet test orders/XuLyDonShopee.Tests` → **Passed! Failed 0, Passed 769** (767 baseline + 2 test mới).
  Chạy riêng 2 test mới trước: Passed 2/2 (xác nhận CheckAccess==true → Reload đồng bộ).
- **Screenshot** (chạy app thật, UIAutomation chọn tab, PrintWindow chụp — lưu ở scratchpad):
  - `shot-01-default.png` (tab Workspace): tab strip = **Workspace · Cấu hình BigSeller · Shopee · Cài đặt**;
    huy hiệu "S", pill tab đang chọn, nút màn active, nền dòng chọn, gạch chân tab con — TẤT CẢ **CAM**,
    không còn lộ xanh.
  - `shot-02-shopee-tab.png` + `crop-checkbox.png` (tab Shopee): pill "Shopee" cam active; checkbox
    "Xóa profile và tạo lại" — ô vuông + nhãn **THẲNG HÀNG** (crop phóng 3× xác nhận), nhãn nhóm
    "Tùy chọn" khớp baseline "Màn hình"/"Hành động".
  - UIAutomation liệt kê 4 tab đúng tên (gồm "Shopee").
  - App đã đóng nhẹ (`CloseMainWindow`) sau khi chụp — đã xác nhận không còn tiến trình.

### Không làm (đúng phạm vi)

- KHÔNG commit git (theo yêu cầu). KHÔNG đụng `main`/`server/`/`shared/`. KHÔNG đổi
  Success/Danger/Warning hay chrome tối trung tính.

### Đề xuất cho Fable

1. Quyết `UsageBrushes.Accent` (mục D, flag ở trên) — giữ xanh hay đổi tông.
2. B đã phải mở rộng nhẹ sang `MainWindow.axaml` (Border bọc 58) ngoài Theme.axaml như plan nêu — nếu
   muốn gói gọn 1 file thì cần chấp nhận lệch nhãn nhóm hoặc override phần tử template Fluent (kém robust).
