# Plan: Port WPF — Đợt 1: hạ tầng + shell chạy được (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
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

<để trống>
