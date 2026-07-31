# Plan: Port WPF — Đợt 1: hạ tầng + shell chạy được (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` — mọi quy ước chuyển đổi (mục 2, đánh số 1–20)
> áp dụng nguyên xi cho đợt này; dưới đây chỉ ghi việc CỤ THỂ của đợt 1, có chỗ dẫn chiếu "QĐ n" = quyết định
> số n của plan tổng. Việc chạy trong WORKTREE nhánh `only-windows` — mọi đường dẫn quy về thư mục làm việc
> của bạn; TUYỆT ĐỐI không đọc/ghi `d:\Projects\shopee-suite` (cây chính đang có việc khác chạy).

## 1. Bối cảnh & mục tiêu

Đợt 1 biến solution từ Avalonia sang WPF ở mức "khung xương chạy được": app build 0 warning, mở lên có shell
(dải tab điều hướng + ribbon + status bar) đúng phong cách phẳng Win11 hiện tại, màn Chào hoạt động, mọi màn
module tạm hiển thị placeholder "đang port"; toàn bộ service/VM đụng Avalonia đã chuyển WPF; project orders
compile được (view của orders port ở đợt 5). Các đợt sau chỉ còn tạo lại từng view.

Nguồn đối chiếu hành vi/giao diện: bản Avalonia hiện tại ngay trong worktree (trước khi xoá — đọc git:
`git show HEAD:<đường-dẫn>.axaml`). Mẫu idiom WPF cũ: `git show 989901c:<đường-dẫn>` (xem plan tổng mục 1).

## 2. Phạm vi

- **Làm:** csproj 2 project UI; xoá toàn bộ .axaml/.axaml.cs; dựng lại App/Program/MainWindow/MessageDialog/
  Welcome/ComingSoon + Theme lõi + Icons + PathIcon; port toàn bộ file C# ngoài code-behind có `using
  Avalonia` (danh sách đủ ở bước 5–6); placeholder cho mọi VM màn hình; sửa 2 file test Avalonia.
- **Không làm:** KHÔNG port các view module (Accounts/Data/BigSeller/Workspace/Search/CheckAccount/Fleet/
  Settings/toàn bộ view orders) — đợt 2–5. KHÔNG đụng script release/manifest ngoài mục app.manifest.
  KHÔNG commit (điều phối commit sau nghiệm thu). KHÔNG sửa gì trong `server/`, `shared/`, `suite/Shopee.Core`,
  `orders/XuLyDonShopee.Core` (các project này không có Avalonia).

## 3. Các bước thực hiện

1. **csproj** —
   - `suite/Shopee.Suite/Shopee.Suite.csproj`: `TargetFramework=net8.0-windows`, thêm `<UseWPF>true</UseWPF>`
     + `<UseWindowsForms>true</UseWindowsForms>` (QĐ 11), bỏ 6 package `Avalonia*`, bỏ
     `AvaloniaUseCompiledBindingsByDefault`, thêm `<ApplicationManifest>app.manifest</ApplicationManifest>`
     (QĐ 18) + `<ApplicationIcon>` trỏ icon sẵn có của repo (`assets/app-icon.ico` — đường dẫn tương đối từ
     csproj), thêm `<StartupObject>Shopee.Suite.Program</StartupObject>` (QĐ 17). GIỮ: Velopack,
     CommunityToolkit.Mvvm, cách đọc `version.txt`, các target Bundle extensions.
   - `orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj`: `net8.0-windows` + `UseWPF`, bỏ 5 package
     `Avalonia*`, bỏ compiled-bindings prop, `<AvaloniaResource Include="Assets\**"/>` →
     `<Resource Include="Assets\**"/>`. GIỮ InternalsVisibleTo.
   - `orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj`: `net8.0-windows` + `UseWPF` (test tạo control
     WPF), thêm `Xunit.StaFact` NẾU cần STA (QĐ 1).
2. **Xoá toàn bộ view Avalonia** — `git rm` mọi `*.axaml` + `*.axaml.cs` trong `suite/Shopee.Suite/` và
   `orders/XuLyDonShopee.App/`, xoá `orders/XuLyDonShopee.App/ViewLocator.cs` (QĐ 7). (Nội dung vẫn đọc lại
   được qua `git show HEAD:<path>` trong suốt đợt.)
3. **Theme lõi** — tạo `suite/Shopee.Suite/Themes/Theme.xaml` (ResourceDictionary WPF) port từ
   `git show HEAD:suite/Shopee.Suite/Themes/Theme.axaml` (694 dòng):
   - Toàn bộ token màu/brush/số đo giữ NGUYÊN key + giá trị (SubtleBrush, BorderBrush, TextSecondaryBrush,
     AccentBrush, MonoFont…); font theo QĐ 2.
   - Style Button mặc định (thay `ControlTheme x:Type Button` cũ): template phẳng Border `PART_bd` +
     ContentPresenter, trigger IsMouseOver/IsPressed/IsEnabled; các style con `primary`, `success`,
     `danger` (nếu class tồn tại trong theme cũ) làm `Style x:Key` BasedOn nút mặc định (QĐ 6).
   - Style `card` (Border), `h1`/`h2`/`caption`/`section` (TextBlock), TextBox (kèm WatermarkAssist — QĐ 15),
     CheckBox, ComboBox, ListBox/ListBoxItem cho `topnav`, TabControl/TabItem (active kiểu Win11: nền accent
     nhạt + gạch chân + chữ cam — đối chiếu style `:selected` cũ), ScrollBar để mặc định, DataGrid style CƠ SỞ
     (header, row hover/selected — viết theo template WPF thật, QĐ ở plan tổng mục 5).
   - Chỗ nào theme cũ chỉ phục vụ view chưa port (selector cho DataGrid con của Workspace, v.v.) thì để lại
     comment `<!-- đợt N -->`, KHÔNG dịch mù.
4. **Icons + PathIcon** — `suite/Shopee.Suite/Themes/Icons.xaml` port nguyên khối Geometry từ
   `git show HEAD:orders/XuLyDonShopee.App/Styles/Icons.axaml` (133 dòng, giữ nguyên key); custom control
   `suite/Shopee.Suite/Controls/PathIcon.cs` theo QĐ 3 (style mặc định trong Theme.xaml, ăn Foreground).
5. **Bootstrap + shell (suite)** —
   - `suite/Shopee.Suite/Program.cs`: giữ `[STAThread]` + `VelopackApp.Build().Run()` dòng đầu, sau đó
     `var app = new App(); app.InitializeComponent(); app.Run();` (QĐ 17).
   - `suite/Shopee.Suite/App.xaml` + `App.xaml.cs`: merge Theme.xaml + Icons.xaml; khai converter dùng chung
     (QĐ 5) + `DataTemplate DataType` tường minh cho TỪNG VM màn hình hiện có (danh sách 10 template cũ xem
     `git show HEAD:suite/Shopee.Suite/App.axaml`): Welcome → WelcomeView; TẤT CẢ VM còn lại (kể cả
     UnifiedSettingsViewModel và MainViewModel orders) → `ComingSoonView` tạm. `OnStartup` port đủ logic
     `OnFrameworkInitializationCompleted` cũ (init engine theo AppMode, UpdateService.CheckAsync, gán
     `DialogService.MainWindow`, lưới đỡ lỗi + hook shutdown theo QĐ 17).
   - `suite/Shopee.Suite/MainWindow.xaml(+.cs)` port từ bản axaml 273 dòng: ListBox `topnav`, ribbon
     (ItemsControl + 3 DataTemplate item, icon qua PathIcon), ContentControl nội dung, status bar 32px;
     `Window.KeyBindings` Ctrl+1..4 → `Window.InputBindings`; ContainerQuery → QĐ 12;
     `Classes.active` → DataTrigger.
   - `suite/Shopee.Suite/MessageDialog.xaml(+.cs)`: port cả phần dựng nút bằng code (TryFindResource WPF
     không có out-param; `ShowDialog<bool>` → `ShowDialog()` + property kết quả — QĐ 13).
   - `suite/Shopee.Suite/Views/WelcomeView.xaml`, `Views/ComingSoonView.xaml` (+ .cs) — port thẳng;
     ComingSoonView thêm dòng phụ "màn này đang được port sang bản Windows" khi làm placeholder.
6. **Port file C# đụng Avalonia (suite)** — theo bảng khảo sát, tất cả nằm trong worktree:
   `Services/UiThread.cs` (QĐ 9; `InvokeAsync(...).GetTask()` → `.Task`),
   `Services/AvaloniaFilePickerService.cs` → đổi tên `WpfFilePickerService.cs` (QĐ 10, tham khảo 989901c),
   `Services/AvaloniaDialogService.cs` → `WpfDialogService.cs` (QĐ 13),
   `Services/WindowHost.cs` (owner qua `window.Owner`), `Services/AppRestart.cs`
   (`Application.Current.Shutdown()`), `Infrastructure/WindowFit.cs` (QĐ 11),
   `Behaviors/LogText.cs` (DependencyProperty.RegisterAttached; dùng `AppendText`/`ScrollToEnd`),
   `Infrastructure/Converters.cs` (QĐ 5), `ViewModels/RibbonModels.cs` + `ViewModels/ModuleItem.cs`
   (`System.Windows.Media.Geometry.Parse`), và 8 file VM brush qua `Infrastructure/AppBrushes.cs` mới (QĐ 8):
   `Modules/Accounts/AccountItemViewModel.cs`, `Modules/Data/DataRowItem.cs`, `Modules/Fleet/FleetViewModel.cs`,
   `Modules/Scrape/ScrapeInstanceViewModel.cs`, `Modules/Scrape/ScrapeTargetViewModel.cs`,
   `Modules/Scrape/ScrapeViewModel.cs`, `Modules/Workspace/WorkspaceShopViewModel.cs`,
   `Modules/Workspace/WorkspaceStatsViewModel.cs` (+ DispatcherTimer → `System.Windows.Threading`).
   Nếu tên file service đổi thì sửa mọi chỗ `new`/đăng ký tương ứng.
7. **Port file C# đụng Avalonia (orders, để compile — KHÔNG port view)** —
   `Services/DialogService.cs` (QĐ 10/13 — property `MainWindow` giữ nguyên tên vì suite gán vào),
   `Views/CellTextExtractor.cs` (VisualTreeHelper + LogicalTreeHelper, giữ nguyên hành vi vì có unit test),
   `ViewModels/MainViewModel.cs` + `ViewModels/OrderStatisticsViewModel.cs` (Dispatcher — QĐ 9),
   6 converter trong `Converters/` (IValueConverter WPF; converter màu trả Brush ĐÃ Freeze).
   2 file test: `orders/XuLyDonShopee.Tests/CellTextExtractorTests.cs`,
   `OrderStatusPillConverterTests.cs` — port sang control/brush WPF (STA nếu cần). Nếu kẹt STA quá 2 lần
   thử: đánh dấu Skip kèm ghi chú "port đợt 5" + BÁO RÕ trong báo cáo, không xoá test.
8. **Build + test + chạy** — `dotnet build ShopeeSuite.sln` (0 error/0 warning — sửa đến khi đạt);
   `dotnet test orders/XuLyDonShopee.Tests` + `suite/Shopee.Core.Tests`; chạy app
   (`dotnet run --project suite/Shopee.Suite`) ở chế độ mặc định: shell mở, chuyển tab các module thấy
   placeholder, màn Chào bấm được, đóng app không treo process. Soát Output/console: không có
   `System.Windows.Data Error` lặp ồ ạt từ shell.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln`: 0 error, 0 warning.
- [ ] Grep `Avalonia` trong `suite/**/*.cs;*.csproj` + `orders/**/*.cs;*.csproj` (loại bin/obj) = 0 kết quả.
- [ ] Không còn file `.axaml` nào trong repo.
- [ ] `dotnet test` 2 project test: xanh (hoặc đúng 2 test Skip có ghi chú như bước 7).
- [ ] App chạy: shell + topnav + ribbon + status bar hiện đúng, Ctrl+1..4 chuyển tab, màn Chào thật,
      các màn khác placeholder, đóng app sạch (hook shutdown chạy — thấy log StopAsync/Cleanup).
- [ ] Điền mục "Báo cáo thực thi" của plan này (trong worktree).

## 5. Rủi ro & lưu ý

- `App.xaml.cs` cũ init nhiều engine theo AppMode — port NGUYÊN THỨ TỰ, đừng bỏ bước (StartupJanitor,
  CoordinationRuntime…); sai thứ tự là hỏng runtime khó lần.
- `StartupObject` + App.xaml: WPF sinh `Main` riêng nếu App.xaml có `Build Action=ApplicationDefinition` —
  giữ ApplicationDefinition nhưng entry point thật là `Program.Main` (StartupObject quyết định); KHÔNG đặt
  `StartupUri` (MainWindow dựng tay trong OnStartup để gán DataContext ShellViewModel như cũ).
- Ribbon/topnav là mặt tiền — dành công tinh chỉnh cho GIỐNG bản Avalonia hiện tại (so bằng mắt, chụp màn
  hình đính kèm báo cáo nếu chạy được).
- Đừng "tiện tay" refactor logic VM/service không liên quan framework — diff phải thuần port.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày:** 2026-07-31 · **Người thực thi:** Opus (`opus-executor`) · **Nơi làm:** worktree `d:\Projects\shopee-suite-onlywin`
(nhánh `only-windows`) · **CHƯA commit** (theo plan).

### 1. Kết quả kiểm chứng (số liệu thật)

| Hạng mục | Lệnh | Kết quả |
|---|---|---|
| Build | `dotnet build ShopeeSuite.sln --no-incremental` | **Build succeeded — 0 Warning(s), 0 Error(s)** |
| Test orders | `dotnet test orders/XuLyDonShopee.Tests` | **Passed 1459 / Failed 0 / Skipped 0** |
| Test core | `dotnet test suite/Shopee.Core.Tests` | **Passed 61 / Failed 0 / Skipped 0** |
| Còn file `.axaml`? | `find . -name "*.axaml"` | **0 file** |
| Còn `Avalonia` trong `.cs`/`.csproj` của 3 project UI? | grep (loại bin/obj) | **0 kết quả** (xem mục 5 về 2 file `Shopee.Core` ngoài phạm vi) |
| Lỗi binding runtime | biến môi trường `SHOPEESUITE_BINDING_LOG` + chạy app | **0 dòng** `System.Windows.Data Error` (trước khi sửa: 4 dòng, đã xử lý — mục 3) |
| Chạy app | mở → Ctrl+2 → Ctrl+3 → bấm X | Shell hiện đúng, chuyển tab được, **đóng sạch, exit code 0** (hook shutdown chạy hết, không sót process) |

### 2. Đã làm (theo từng bước của plan)

1. **csproj** — `Shopee.Suite`: `net8.0-windows` + `UseWPF` + `UseWindowsForms`, bỏ 6 gói Avalonia,
   `StartupObject=Shopee.Suite.Program`, `ApplicationManifest=app.manifest` (file mồ côi cũ), `ApplicationIcon`
   → `assets/app-icon.ico`; giữ Velopack/Toolkit/`version.txt`/2 target Bundle extension.
   `XuLyDonShopee.App`: `net8.0-windows` + `UseWPF`, bỏ 5 gói Avalonia, `AvaloniaResource` → `Resource`.
   `XuLyDonShopee.Tests`: `net8.0-windows` + `UseWPF` + gói `Xunit.StaFact` 1.1.11.
2. **Xoá view Avalonia** — 30 `.axaml` + 20 `.axaml.cs` + `ViewLocator.cs` (`git rm`, đang ở trạng thái staged-delete).
3. **Theme + icon + control** — mới: `Themes/Theme.xaml` (≈690 dòng, giữ NGUYÊN key + giá trị token, kèm bảng quy
   ước dịch `Classes` → `x:Key` cho các đợt sau), `Themes/Icons.xaml` (dời từ orders sang suite, giữ nguyên key +
   bảng ánh xạ hành-động→icon), `Controls/PathIcon.cs`, `Behaviors/WatermarkAssist.cs`.
4. **Bootstrap + shell** — `Program.cs` (Velopack chạy trước, rồi `new App()`), `App.xaml`/`App.xaml.cs`
   (merge theme+icon, 6 converter, 11 `DataTemplate` tường minh, port nguyên thứ tự init engine, hook shutdown
   3 đường), `MainWindow.xaml(+.cs)`, `MessageDialog.xaml(+.cs)`, `Views/WelcomeView.*`, `Views/ComingSoonView.*`.
5. **C# suite đụng Avalonia** — `Services/UiThread.cs`, `AvaloniaDialogService.cs`→`WpfDialogService.cs`,
   `AvaloniaFilePickerService.cs`→`WpfFilePickerService.cs`, `WindowHost.cs`, `AppRestart.cs`,
   `Infrastructure/WindowFit.cs`, `Behaviors/LogText.cs`, `Infrastructure/Converters.cs` (đổi sang trả
   `Visibility` + thêm `InvBool`/`InvBoolToVis`/`WiderThan`), `ViewModels/RibbonModels.cs`, `ViewModels/ModuleItem.cs`,
   8 VM brush đi qua `Infrastructure/AppBrushes.cs` (mới, parse + **Freeze** + cache), `Dialogs.cs`/`FilePicker.cs`
   trỏ impl mới.
6. **C# orders** — `Services/DialogService.cs`, `Views/CellTextExtractor.cs`, 6 converter + `Converters/BrushPalette.cs`
   (mới, Freeze), 6 chỗ dùng Dispatcher gom về `Services/UiDispatch.cs` (mới); 2 file test port sang control WPF.

### 3. Điểm phải trệch plan (và lý do)

1. **4 cửa sổ phụ phải có bản placeholder** — `ImportAccountsWindow`, `CheckAccountWindow`, `RowEditWindow`,
   `ScrapeStatsWindow` bị ViewModel gọi trực tiếp (`AccountsViewModel`, `DataViewModel`, `ScrapeViewModel`), xoá
   `.axaml` là 4 VM không compile. Đã thêm `Views/PortingWindow.cs` + 4 lớp con thuần C# **giữ nguyên tên + chữ ký
   hàm dựng + property** (`Logins`/`ProxyKeys`) → VM không phải sửa một dòng nào. Cửa sổ chỉ hiện "đang port +
   nút Đóng", đóng trả `DialogResult=false` (VM coi như người dùng bấm Hủy). Đợt 2/3/4 xoá file `.cs` này, thêm
   cặp `.xaml/.xaml.cs` cùng tên.
2. **`ComingSoonView` điền chữ bằng code-behind thay vì binding** — plan nói dùng `ComingSoonView` làm placeholder
   cho MỌI VM module; nhưng bind `Title`/`Description` vào VM module (không có 2 property đó) sẽ đổ hàng loạt
   `System.Windows.Data Error` — đúng thứ tiêu chí nghiệm thu cấm. Nay view tự nhận: `ComingSoonViewModel` → lấy
   Title/Description như cũ; VM khác → tiêu đề suy từ tên kiểu (`WorkspaceViewModel` → "Workspace") + badge
   "Đang port" + câu giải thích.
3. **`ribbon` tách làm 2 style** — `ribbon` (nút hành động) và `ribbonNav` (nút điều hướng, có `DataTrigger IsActive`).
   Để chung một style thì mỗi `RibbonActionItem` đẻ 1 dòng `System.Windows.Data Error: 40` lúc mở app (đã đo: 4 dòng);
   tách xong log còn **0 dòng**.
4. **`x:Key` `card` bị va tên** — Avalonia có cả `Button.card` (thẻ bấm ở màn Chào) lẫn `Border.card`. WPF bắt key duy
   nhất → `card` = Border, `cardButton` = Button. (Ghi trong đầu file Theme.xaml.)
5. **csproj phải chỉnh implicit usings** — SDK desktop (`net8.0-windows`) tự thêm `using System.Windows.Forms` +
   `System.Drawing` (do `UseWindowsForms`) → va tên `Control/TextBox/Brush/Timer/Application` khắp nơi, và **bỏ**
   `System.IO` + `System.Net.Http` so với SDK thường → vỡ loạt file cũ. Đã `<Using Remove>` 2 cái đầu và
   `<Using Include>` 2 cái sau (suite + test project).
6. **`NoWarn WFAC010`** — analyzer WinForms đòi bỏ `dpiAware` khỏi `app.manifest`, nhưng WPF BẮT BUỘC khai
   PerMonitorV2 ở manifest và app này không chạy WinForms (chỉ mượn lớp `Screen`). Tắt đúng một mã cảnh báo,
   có ghi lý do trong csproj — nếu không thì không đạt "0 warning".
7. **`DialogService` của orders tạm dùng `MessageBox`** — 2 hộp thoại riêng của module (`ConfirmDialog`,
   `OrderDetailDialog`) thuộc đợt 5. `ConfirmAsync`/`InfoAsync` tạm dùng MessageBox của Windows;
   **`EditOrderAsync` trả `null`** (như bấm Hủy) + ghi Trace. Chọn thư mục / lưu CSV đã là bản WPF thật.
8. **`Xunit.StaFact` là bắt buộc, không phải tuỳ chọn** — control WPF chỉ dựng được trên luồng STA, xunit chạy MTA
   (8 test `CellTextExtractorTests` fail đúng lỗi này trước khi thêm). Sau khi đổi sang `[StaFact]/[StaTheory]`:
   xanh hết, **không có test nào bị Skip**.
9. **Thêm hook log lỗi binding (opt-in)** — WPF chỉ đẩy `System.Windows.Data Error` vào Output của debugger, chạy
   thường không thấy gì → không kiểm được tiêu chí nghiệm thu. `App.HookBindingTrace()` bật khi có biến môi trường
   `SHOPEESUITE_BINDING_LOG=<đường-dẫn>`; mặc định tắt. Dùng lại được cho các đợt sau.
10. **`CellTextExtractor.ExtractCellText` đổi kiểu tham số** `Control?` → `DependencyObject?` (WPF duyệt cây bằng
    `VisualTreeHelper`/`LogicalTreeHelper` trên `DependencyObject`). Hành vi giữ nguyên, test giữ nguyên ca kiểm.

### 4. Khác biệt nhìn thấy so với bản Avalonia (đã chụp màn hình khi chạy)

Giống về bố cục: dải tab trắng + app-mark cam + gạch cam 3px dưới tab đang chọn + gợi ý "Ctrl + 1…4"; ribbon nút lớn
(icon trên / nhãn dưới, nhóm + caption đáy, vạch dọc ngăn nhóm, nút active tint cam + chữ/icon cam); thanh trạng thái
32px với chấm xanh nhấp nháy, các cụm đếm, vạch ngăn 1×16, phiên bản `v1.6.17` ở góc phải. Co hẹp cửa sổ (~1100 DIP)
thì các đoạn phụ (acc Shopee · proxy, Trình duyệt, máy online) tự ẩn — đúng như `ContainerQuery` cũ.

Khác biệt cố ý / chấp nhận ở đợt này:
- **Font**: Segoe UI Variable Text thay Inter → chữ mảnh và "Windows" hơn một chút.
- **LetterSpacing** (h1, header DataGrid) bỏ vì WPF không có.
- Icon nút ribbon nay thừa kế `Foreground` của nút (thường `#2C2724`, active → cam) thay vì 2 màu tách rời.
- Nội dung mọi màn module là thẻ "Đang port" (đúng phạm vi đợt 1).

### 5. Vướng mắc / còn lại

1. **Grep `Avalonia` = 0 chỉ đúng với 3 project UI.** Toàn repo còn 5 file dính chữ Avalonia trong **comment**:
   `suite/Shopee.Core/Infrastructure/AppModeStore.cs`, `suite/Shopee.Core/Products/ProductGridEngine.cs`,
   `suite/Shopee.Core/Shopee.Core.csproj`, `orders/XuLyDonShopee.Core/XuLyDonShopee.Core.csproj`, cộng vài file
   `server/`+`shared/`. Plan mục 2 CẤM sửa các project này nên tôi để nguyên — cần Fable quyết (sửa comment là
   thao tác 1 dòng/ file, không đụng logic).
2. **Chỉ chạy thật được chế độ `--mode workspace`.** Máy này ĐANG chạy bản production (`ShopeeSuite.exe` PID 33732 +
   8 tiến trình Brave). Chạy chế độ Full sẽ (a) `BraveFleet.StartupSweep()` giết Brave "mồ côi" của bản production,
   (b) `OrdersModuleHost` mở chung `%APPDATA%\XuLyDonShopee\app.db` và có thể chạy vòng đẩy dữ liệu ra ngoài.
   Nên tôi chạy bản dev với `data-dir.txt` trỏ vào thư mục tạm (cơ chế có sẵn của `SuitePaths`) + `--mode workspace`
   → kho dữ liệu rỗng, không có cấu hình Hub (coordination NoOp), không đụng gì của production; file `data-dir.txt`
   đã xoá sau khi chạy. **Hệ quả: tab "Shopee" và template `RibbonToggleItem` (checkbox "Xóa profile và tạo lại")
   chưa được nhìn tận mắt** — đề nghị Fable chạy `--mode full` lúc máy rảnh để soát nốt.
3. **Chưa kiểm bằng mắt**: `MessageDialog` (chưa có thao tác nào gọi tới), `WatermarkAssist`, các style TextBox/
   ComboBox/DataGrid/TabItem (chưa có view nào dùng) — sẽ lộ ở đợt 2–5. Style DataGrid mới chỉ là nền tảng
   (header/dòng/ô), cột và template riêng của từng màn dựng ở đợt sau.
4. **`RowEditWindow` placeholder không gán `RowEditViewModel.ConfirmOwner`** — vô hại vì cửa sổ không lưu gì; đợt 2
   dựng cửa sổ thật phải gán lại (bản cũ dùng nó cho hộp "SKU trùng").
