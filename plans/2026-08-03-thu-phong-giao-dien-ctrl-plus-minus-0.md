# Plan: Thu phóng giao diện toàn app bằng Ctrl + / Ctrl - / Ctrl 0

- **Ngày:** 2026-08-03
- **Trạng thái:** hoàn thành
- **Người lập & thực thi:** phiên chat chính (theo CLAUDE.md dự án — phiên chính tự thực thi)

## 1. Bối cảnh & mục tiêu

Người dùng muốn app desktop (WPF) hỗ trợ phóng to / thu nhỏ **cả form lẫn chữ** giống trình duyệt:

- `Ctrl` + `+` (hoặc `=`, `NumPad +`) → phóng to một nấc
- `Ctrl` + `-` (hoặc `NumPad -`) → thu nhỏ một nấc
- `Ctrl` + `0` (hoặc `NumPad 0`) → về mặc định 100%

Hiện trạng khảo sát:

- App là **một cửa sổ chính** `suite/Shopee.Suite/MainWindow.xaml` (shell ribbon + ContentControl nội dung),
  cộng 7 cửa sổ phụ: `MessageDialog`, `ImportAccountsWindow`, `CheckAccountWindow`, `RowEditWindow`,
  `ScrapeStatsWindow`, và 2 cửa sổ của module đơn hàng (`ConfirmDialog`, `OrderDetailDialog`).
  Module `XuLyDonShopee.App` KHÔNG có cửa sổ riêng — view của nó nằm trong shell nên tự thu phóng theo.
- `MainWindow.xaml` đã có `Window.InputBindings` (Ctrl+1…4 chuyển tab) — thêm phím tắt kiểu đó chỉ áp cho
  cửa sổ chính; muốn áp cho MỌI cửa sổ thì phải bắt ở tầng `Application`.
- Có sẵn khuôn kho cấu hình JSON: `Shopee.Core/Infrastructure/AppModeStore.cs` (`SuitePaths.RootFile` +
  `JsonAtomicFile`) → chép khuôn cho mức thu phóng, lưu ngoài thư mục cài Velopack (cập nhật không xoá).
- `WindowFit.FitOnOpen()` (chạy ở `SourceInitialized`) kẹp cửa sổ vào vùng làm việc màn hình — phải phối hợp
  để cửa sổ phụ phóng to không tràn ra ngoài màn.

Cách làm: dùng **một `ScaleTransform` dùng chung** gán vào `LayoutTransform` của phần tử gốc từng cửa sổ.
`LayoutTransform` phóng ở tầng **layout** nên chữ, khoảng cách, nút, lưới… đều to lên đúng tỉ lệ (không phải
`RenderTransform` kiểu phóng ảnh mờ). Đổi `ScaleX/ScaleY` của transform dùng chung ⇒ mọi cửa sổ đang mở cập
nhật tức thì, không cần khởi động lại.

## 2. Phạm vi

- **Làm:**
  - Kho lưu mức thu phóng `%AppData%\ShopeeSuite\ui-zoom.json` (nhớ giữa các lần chạy app).
  - Dịch vụ `UiZoom` cài ở `App.OnStartup`: bắt phím tắt toàn app + gắn transform cho mọi cửa sổ (kể cả cửa
    sổ mở sau, kể cả hộp thoại của module đơn hàng).
  - Nấc thu phóng rời rạc kiểu trình duyệt, có chặn trên/dưới.
  - Cửa sổ phụ: nhân kích thước khai báo (`Width/Height/Min*/Max*`) theo mức phóng rồi kẹp lại vào vùng làm
    việc màn hình để không tràn.
  - Thanh trạng thái hiện đoạn "Thu phóng: N%" khi mức ≠ 100% (kèm tooltip nhắc phím tắt) để người dùng biết
    mình đang ở mức nào và cách quay về.
  - Ô chỉnh thu phóng trong màn Cài đặt (ComboBox chọn mức) — để người không nhớ phím tắt vẫn đổi được.
- **Không làm:**
  - Không đổi kích thước **cửa sổ chính** theo mức phóng (người dùng thường maximize; tự đổi kích thước cửa
    sổ chính dễ nhảy lung tung). Chỉ nội dung bên trong phóng/thu.
  - Không làm `Ctrl + lăn chuột` (dễ đụng cuộn danh sách/DataGrid trong app).
  - Không đụng hub web (`server/`) — chỗ đó trình duyệt đã có sẵn zoom.
  - Không đổi giá trị `FontSize` trong `Theme.xaml`/style (thu phóng là tầng transform, không sửa design token).

## 3. Các bước thực hiện

### Bước 1 — Kho cấu hình `UiZoomStore`

Tạo `suite/Shopee.Core/Infrastructure/UiZoomStore.cs`, khuôn theo `AppModeStore`:

- File `SuitePaths.RootFile("ui-zoom.json")`, DTO `{ "zoom": 1.15 }`.
- `Shared` singleton (`Lazy`), `Current` (double, mặc định `1.0`), `Save(double)` ghi nguyên tử.
- Kẹp giá trị đọc/ghi vào `[MinZoom..MaxZoom]`; giá trị hỏng/NaN → `1.0`.
- Hằng số nấc thu phóng đặt ở đây (`Steps`) + hàm thuần `Next(current, +1/-1)` để test được không cần WPF.

### Bước 2 — Dịch vụ `UiZoom` (WPF)

Tạo `suite/Shopee.Suite/Services/UiZoom.cs`:

- `static ScaleTransform _scale` dùng chung cho mọi cửa sổ.
- `Install()` gọi trong `App.OnStartup` (trước khi tạo `MainWindow`):
  - nạp `UiZoomStore.Shared.Current` vào `_scale`;
  - `EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, …)` → gắn transform +
    scale kích thước cho MỌI cửa sổ WPF của app (bao gồm cửa sổ của `XuLyDonShopee.App`);
  - `EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, …)` → bắt phím tắt ở
    tầng cửa sổ (tunnel: cửa sổ nhận trước TextBox/DataGrid nên gõ trong ô nhập vẫn thu phóng được).
- Phím: `OemPlus`/`Add` → tăng; `OemMinus`/`Subtract` → giảm; `D0`/`NumPad0` → 1.0. Điều kiện: đang giữ
  `Ctrl` và KHÔNG giữ `Alt`/`Win` (cho phép `Ctrl+Shift+=` vì đó là cách gõ dấu `+`). Xử lý xong đặt
  `e.Handled = true`.
- `Apply(double zoom)`: kẹp nấc → đổi `_scale.ScaleX/Y` → lưu kho → cập nhật kích thước các cửa sổ phụ đang
  mở → bắn event `Changed` (thanh trạng thái + màn Cài đặt nghe).
- Kích thước cửa sổ phụ: lưu kích thước gốc (`Width/Height/MinWidth/MinHeight/MaxWidth/MaxHeight`) lần đầu
  gắn vào một `ConditionalWeakTable<Window, …>` rồi mỗi lần đổi mức thì gán `gốc × zoom`; bỏ qua giá trị
  `NaN` (cửa sổ `SizeToContent`) và `Infinity` (Max* mặc định). Sau khi gán, gọi lại
  `WindowFit.FitToWorkingArea()` để cửa sổ không tràn màn.
- Cửa sổ chính (`Application.Current.MainWindow`) chỉ gắn transform, KHÔNG đụng kích thước.
- Khi zoom ≠ 1 đặt `TextOptions.SetTextFormattingMode(window, Ideal)` (mức 1.0 trả về `Display`): chế độ
  `Display` khớp pixel nguyên nên chữ bị lệch nét khi nhân tỉ lệ lẻ.

### Bước 3 — Cắm vào `App.OnStartup`

`suite/Shopee.Suite/App.xaml.cs`: gọi `Services.UiZoom.Install()` trong `OnStartup` (bọc try/catch như các
hook khác — lỗi thu phóng không được chặn app khởi động).

### Bước 4 — Chỉ báo trên thanh trạng thái

- `suite/Shopee.Suite/ViewModels/ShellViewModel.cs`: thêm `ZoomText` (`"Thu phóng: 125%"`) + `ShowZoom`
  (true khi ≠ 100%), đăng ký `UiZoom.Changed` để `OnPropertyChanged`.
- `suite/Shopee.Suite/MainWindow.xaml`: thêm một `statusSeg` bên phải (cạnh đoạn phiên bản), ẩn khi 100%,
  tooltip "Ctrl + / Ctrl - để thu phóng · Ctrl 0 về 100%".

### Bước 5 — Ô chỉnh trong Cài đặt

`Modules/Settings/UnifiedSettingsView.xaml` + `UnifiedSettingsViewModel.cs`: trong thẻ "Chế độ ứng dụng"
thêm một dòng "Cỡ giao diện" = ComboBox các nấc (`75% … 200%`), chọn là áp ngay (không cần khởi động lại),
kèm câu mô tả nhắc phím tắt.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln -c Debug` — 0 error (cảnh báo không tăng so với trước).
- [ ] `dotnet test suite/Shopee.Core.Tests` — xanh; có test mới cho `UiZoomStore.Next`/kẹp biên
      (tăng từ nấc cao nhất giữ nguyên, giảm từ nấc thấp nhất giữ nguyên, giá trị lạ → 1.0).
- [ ] Chạy app: `Ctrl` `+` → toàn bộ chữ + nút + lưới to lên; `Ctrl` `-` → nhỏ lại; `Ctrl` `0` → về 100%.
- [ ] Đóng app rồi mở lại → giữ đúng mức thu phóng lần trước (`%AppData%\ShopeeSuite\ui-zoom.json`).
- [ ] Mở một hộp thoại (vd Import tài khoản / hộp thoại xác nhận của Đơn hàng) khi đang ở 125% → nội dung
      hộp thoại cũng to theo và không bị cắt chữ.
- [ ] Thanh trạng thái hiện "Thu phóng: N%" khi ≠ 100%, ẩn khi 100%.
- [ ] Ctrl+1…4 chuyển tab vẫn chạy như cũ (không bị handler mới nuốt).

## 5. Rủi ro & lưu ý

- **Nuốt phím oan:** class handler `PreviewKeyDown` chạy cho mọi cửa sổ — phải lọc đúng tổ hợp và chỉ
  `Handled = true` khi thật sự xử lý, kẻo hỏng phím trong ô nhập (vd `Ctrl+0` không có ý nghĩa khác, nhưng
  `Ctrl+-` cũng không → an toàn; tuyệt đối không bắt khi có `Alt`).
- **`Loaded` là routed event Direct** — class handler chỉ chạy cho chính cửa sổ, không phải mọi con.
- **Phóng to trên màn nhỏ:** cửa sổ chính không đổi kích thước nên ở mức cao nội dung có thể bị chật; ribbon
  đã có `ScrollViewer` ngang, các màn khác đa số có scroll riêng. Chấp nhận — người dùng tự chọn mức.
- **Chia sẻ `ScaleTransform`:** `Transform` là `Freezable`, gắn cho nhiều phần tử được miễn KHÔNG `Freeze()`.
- **`WindowFit.FitToWorkingArea`** bỏ qua cửa sổ có `Width/Height` = NaN (SizeToContent) — đúng ý, không cần
  chỉnh; chỉ cần gọi lại sau khi đổi kích thước.

---

## Báo cáo thực thi (2026-08-03)

**Đã làm đúng 5 bước của plan.** File mới: `suite/Shopee.Core/Infrastructure/UiZoomStore.cs`,
`suite/Shopee.Suite/Services/UiZoom.cs`, `suite/Shopee.Core.Tests/UiZoomStoreTests.cs`. File sửa:
`App.xaml.cs`, `MainWindow.xaml`, `ViewModels/ShellViewModel.cs`, `Modules/Settings/UnifiedSettingsView.xaml`,
`Modules/Settings/UnifiedSettingsViewModel.cs`, `CHANGELOG.md`, `version.txt` (1.7.7 → 1.7.8).

### Kiểm chứng

- `dotnet build ShopeeSuite.sln -c Debug` → 0 lỗi, 0 cảnh báo.
- `dotnet test ShopeeSuite.sln` → **1532 test xanh** (71 Core + 1461 Đơn hàng), gồm 7 test mới cho `UiZoomStore`.
- **Harness WPF thật** (scratchpad, `Compile Include` THẲNG `UiZoom.cs` + `WindowFit.cs` của app, kho dữ liệu
  cách ly bằng `data-dir.txt` nên không đụng `%AppData%\ShopeeSuite`): mở cửa sổ thật, gõ phím thật bằng
  `SendKeys` → **21/21 PASS**, kèm ảnh chụp 100% / 130% / 85% đối chiếu mắt thường.
  Không boot app thật vì bản cài đang chạy trên máy (né `BraveFleet.StartupSweep` + `HubOutboxWorker` đúp).

### 2 lỗi harness bắt được và đã sửa (nếu không chạy thật thì không thấy)

1. **Hộp thoại mở khi đang phóng bị kẹt ở đúng `MinWidth` mới** (440 ở mức 130% ra 468 thay vì 572): lúc
   `Loaded`, cửa sổ mới mở còn trong lượt tự-đo `SizeToContent` và GHI ĐÈ `Width/Height` bằng kích thước HWND
   cũ, rồi bị `MinWidth` mới kẹp lên. → Hoãn `ApplySize` một nhịp `DispatcherPriority.Background`.
2. **Thu nhỏ lại không được** khi hộp thoại đang mở (về 100% mà `Width` vẫn 468): gán `Width` TRƯỚC `MinWidth`
   thì `Width` nhỏ mới bị `MinWidth` cũ (lớn) kẹp lên. → Bắt buộc thứ tự **Min/Max trước, Width/Height sau**.

### Khác plan

- Không có. Riêng phần "ô chỉnh trong Cài đặt" đặt ở tab **Chế độ ứng dụng** (tab duy nhất LUÔN hiện ở mọi chế
  độ app) để chế độ `Shopee`/`Workspace` đều dùng được.

### Chưa làm / còn lại

- Chưa chạy thử trên **app thật** (bản cài của người dùng đang mở). Cần người dùng bấm thử sau khi phát hành,
  đặc biệt: mức phóng cao trên màn nhỏ ở các màn lưới dày (Dữ liệu, Đơn hàng).
- Chưa phát hành (`release-suite.cmd`) — chờ người dùng yêu cầu.
