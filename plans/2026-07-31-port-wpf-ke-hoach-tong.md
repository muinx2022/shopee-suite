# Plan TỔNG: Port toàn bộ UI Avalonia → WPF (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** hoàn thành (đã merge về main; còn smoke-test Full + phát hành 1.7.0)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`) — theo TỪNG plan đợt, không thực thi trực tiếp plan này

## 1. Bối cảnh & mục tiêu

App desktop hiện là **Avalonia 11.3** (suite/Shopee.Suite là shell WinExe; orders/XuLyDonShopee.App là DLL
module Avalonia được shell nạp). Người dùng quyết định:

- Tạo nhánh **`only-windows`**: port TOÀN BỘ UI sang **WPF** (`net8.0-windows`, `UseWPF`), giữ nguyên toàn bộ
  ViewModel/logic — chỉ thay tầng view + service đụng API framework.
- Nhánh `only-windows` sau khi xong sẽ **merge về `main`** → main là bản thuần Windows.
- Ngay TRƯỚC khi merge, tách nhánh **`avalonia`** từ đỉnh `main` để giữ bản đa nền tảng (cài client Ubuntu
  khi cần). Channel Velopack: `win` tiếp tục dùng cho bản WPF; channel `linux` chỉ phát hành từ nhánh
  `avalonia`.

Khối lượng (khảo sát 2026-07-31): ~30 file .axaml (~6.450 dòng) + ~750 dòng code-behind; 2 file style lớn
(`suite/Shopee.Suite/Themes/Theme.axaml` 694 dòng ~90 selector, `orders/XuLyDonShopee.App/Styles/Controls.axaml`
530 dòng ~80 selector); ~25 file C# ngoài code-behind có `using Avalonia`.

**Tham khảo quý:** commit `989901c` là bản WPF cuối trước migrate (2026-07-05). Lấy file cũ bằng
`git show 989901c:<đường-dẫn>` — đáng giá nhất: `suite/Shopee.Suite/Themes/Theme.xaml`,
`Services/WpfDialogService.cs`, `Services/WpfFilePickerService.cs`, `Behaviors/PasswordBoxAssist.cs`,
`MainWindow.xaml`. LƯU Ý: bản cũ lạc hậu ~1 tháng (chưa có module Data, UnifiedSettings, orders…) — chỉ dùng
làm mẫu idiom WPF, KHÔNG revert.

## 2. Các quyết định kỹ thuật đã chốt (áp dụng cho MỌI đợt)

1. **Không thêm thư viện UI ngoài** (không ModernWpf/MaterialDesign/Extended.Wpf.Toolkit). Theme tự viết,
   phẳng kiểu Windows 11 (bo 4–6, không gradient/bóng, header tối đặc, active = nền accent nhạt + gạch chân).
   Ngoại lệ duy nhất: test project được thêm `Xunit.StaFact` nếu cần chạy test tạo control WPF (STA).
2. **Font:** bỏ Inter (`Avalonia.Fonts.Inter`), dùng `Segoe UI Variable` (fallback `Segoe UI`); mono giữ
   `Cascadia Mono` fallback `Consolas`. Token font đặt trong Theme.xaml.
3. **`PathIcon`:** tự dựng custom control WPF `PathIcon` trong suite (DP `Data : Geometry`; template =
   `<Path Data={TemplateBinding Data} Fill={TemplateBinding Foreground} Stretch="Uniform"/>`, mặc định 16×16)
   để giữ nguyên cú pháp view + cơ chế icon ăn theo `Foreground` của Button mà theme đang dựa vào.
4. **Icons:** chuyển kho Geometry dùng chung (hiện ở `orders/.../Styles/Icons.axaml`) về
   `suite/Shopee.Suite/Themes/Icons.xaml`, merge ở App.xaml — orders là DLL chạy trong app suite nên tra
   resource cấp Application được.
5. **`IsVisible` → `Visibility`:** dùng `BooleanToVisibilityConverter` có sẵn + tự viết `InverseBoolConverter`,
   `InverseBoolToVisibilityConverter`, và đổi 2 converter ở `suite/.../Infrastructure/Converters.cs`
   (`CountToBool`, `StringToBool`) sang trả `Visibility`. Khai báo 1 lần ở App.xaml, key thống nhất:
   `BoolToVis`, `InvBool`, `InvBoolToVis`, `CountToVis`, `StringToVis`.
6. **`Classes="x"` → `Style="{StaticResource x}"`:** giữ NGUYÊN TÊN class cũ làm x:Key (card, h1, h2, caption,
   primary, success, danger, section…) để diff view dễ đối chiếu. `Classes.active="{Binding…}"` →
   `DataTrigger` trong style.
7. **ViewLocator bỏ hẳn:** suite lẫn orders dùng `DataTemplate DataType="{x:Type vm:X}"` tường minh trong
   App.xaml (xoá `orders/XuLyDonShopee.App/ViewLocator.cs`).
8. **Brush trong ViewModel:** WPF `SolidColorBrush` tạo ngoài UI thread PHẢI `.Freeze()`. Dựng helper tĩnh
   `suite/Shopee.Suite/Infrastructure/AppBrushes.cs`: `AppBrushes.From("#RRGGBB")` (parse + Freeze + cache)
   và các brush ngữ nghĩa thay `Brushes.Accent/Success/Danger/Muted` của Avalonia. 8 file VM đang tạo brush
   (Scrape*, Fleet, Workspace*, AccountItem, DataRowItem) đều đi qua helper này.
9. **Dispatcher:** `Dispatcher.UIThread` → `System.Windows.Application.Current.Dispatcher` (bọc trong
   `suite/.../Services/UiThread.cs` như cũ; orders dùng trực tiếp Application.Current.Dispatcher).
10. **File/folder picker:** `Microsoft.Win32.OpenFileDialog/SaveFileDialog/OpenFolderDialog` (.NET 8 có
    OpenFolderDialog). Chuỗi filter kiểu WPF đã dùng sẵn trong code gọi → truyền thẳng, xoá parser.
11. **`WindowFit`:** bật thêm `UseWindowsForms=true` ở csproj suite, dùng `System.Windows.Forms.Screen`
    (WorkingArea, DPI qua `VisualTreeHelper.GetDpi`). `Position` → `Left/Top` (đơn vị DIP — chia scaling).
12. **ContainerQuery** (status bar MainWindow tự ẩn đoạn phụ khi hẹp) → WPF không có: thay bằng
    `DataTrigger` trên `ActualWidth` của status bar qua converter `LessThanConverter` (ngưỡng 1240) hoặc
    handler `SizeChanged` — chọn cách gọn, hành vi giữ nguyên.
13. **Dialog:** WPF `ShowDialog()` blocking trả `bool?` — service bọc lại giữ chữ ký async hiện có
    (`Task<bool>`/`Task<T?>`); kết quả tuỳ biến (`Close(string?)` của orders) → property `Result` trên Window.
14. **Compiled binding:** xoá `x:DataType`/`AvaloniaUseCompiledBindingsByDefault`; WPF binding reflection.
15. **Control thiếu ở WPF:** `NumericUpDown` → TextBox + kẹp giá trị trong VM/code-behind (chỉ 1 chỗ dùng);
    `AutoCompleteBox` → `ComboBox IsEditable="True"`; `Watermark` → attached property `WatermarkAssist` tự
    viết (VisualBrush/label mờ); `CalendarDatePicker` → `DatePicker`.
16. **Cú pháp cơ học:** `RowDefinitions="…"` inline → khối `<Grid.RowDefinitions>`; `Spacing` → Margin;
    `ToolTip.Tip` → `ToolTip`; `$parent[X]` → `RelativeSource FindAncestor`; `{Binding !X}` → converter;
    `xmlns` avaloniaui → schema WPF; `using:` → `clr-namespace:`; `avares://` → `pack://application:,,,/…`;
    `ZIndex` → `Panel.ZIndex`; `RenderOptions.TextRenderingMode` → `TextOptions`; `LetterSpacing` → bỏ
    (WPF không có — chấp nhận).
17. **App lifecycle:** `Program.Main` giữ `VelopackApp.Build().Run()` ĐẦU TIÊN rồi mới dựng
    `App` WPF (tự viết Main, tắt entry point sinh tự động bằng `StartupObject`). Lưới đỡ lỗi dùng
    `DispatcherUnhandledException` + `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`.
    Hook tắt app (lưu BigSellerStore, `OrdersModuleHost.StopAsync`, `MultiBraveRuntime.Cleanup`) chuyển sang
    `MainWindow.Closing`/`Application.Exit`/`SessionEnding`.
18. **`app.manifest`** (đang mồ côi trên đĩa, có PerMonitorV2): tham chiếu lại trong csproj suite.
19. **Nghiệm thu mỗi đợt:** `dotnet build ShopeeSuite.sln` 0 error 0 warning + toàn bộ test xanh + app chạy
    được bằng mắt ở phạm vi đợt đó. KHÔNG yêu cầu pixel-perfect từng đợt; tinh chỉnh dồn về đợt cuối.
20. **Trên nhánh `only-windows` xoá:** `release-suite.sh`, `publish-suite.sh`, `install-linux.sh` (đợt cuối) —
    bản Linux sống ở nhánh `avalonia`. `release-suite.cmd` giữ nguyên channel `win`; vụ
    `PublishReadyToRun` + WDAC (ghi chú trong csproj orders) kiểm lại ở đợt cuối — ràng buộc đó là của DLL
    Avalonia, nhiều khả năng hết áp dụng với runtime WPF chính chủ Microsoft.

## 3. Lộ trình các đợt (mỗi đợt một plan riêng, giao lần lượt)

| Đợt | Nội dung | Plan |
|---|---|---|
| 1 | Hạ tầng + shell chạy được: csproj (cả suite lẫn orders), xoá toàn bộ .axaml (tạo lại dần), Theme.xaml lõi, Icons, PathIcon, App/Program/MainWindow/MessageDialog/Welcome/ComingSoon, toàn bộ service + VM đụng Avalonia, orders compile được (converter/DialogService/CellTextExtractor/2 file test), mọi màn module = placeholder | `plans/2026-07-31-port-wpf-dot1-ha-tang-shell.md` |
| 2 | Module suite nhóm A: AccountsView (+ImportAccountsWindow), DataView (+RowEditWindow), BigSellerView | (viết sau khi đợt 1 nghiệm thu) |
| 3 | WorkspaceView (924 dòng — nặng nhất) + ScrapeStatsWindow | (viết sau) |
| 4 | SearchView, CheckAccountView (+CheckAccountWindow), FleetView | (viết sau) |
| 5 | Toàn bộ orders: Colors/Controls/ModuleResources, MainView, OrdersView, AccountsView (750 dòng), OrderStatisticsView, 2 dialog, màn Cài đặt gộp (bản MỚI vừa làm lại ở main — merge main vào only-windows trước đợt này) | (viết sau) |
| 6 | Tổng dọn + phát hành: rà binding error toàn app, tinh chỉnh theme, app.manifest/R2R/script release, xoá script Linux, CHANGELOG, bump version 1.7.0; tách nhánh `avalonia` từ main rồi merge `only-windows` → main | (viết sau) |

Điều phối (Fable làm, không giao agent): tạo nhánh + worktree, merge main → only-windows giữa các đợt khi
cần (nhất là trước đợt 5 để lấy màn Cài đặt mới + bỏ webhook), commit sau mỗi đợt nghiệm thu, và toàn bộ
bước nhánh/merge ở đợt 6.

## 4. Tiêu chí nghiệm thu toàn cục (khi merge về main)

- [ ] `dotnet build ShopeeSuite.sln` 0 error 0 warning; `dotnet test` (Shopee.Core.Tests +
      XuLyDonShopee.Tests) xanh toàn bộ.
- [ ] Không còn package/`using` Avalonia nào trong solution (grep `Avalonia` = 0 ở csproj + .cs sống).
- [ ] Chạy thật trên Windows: đủ các màn theo 3 chế độ Full/Workspace/Shopee, mở/đóng các Window con,
      scrape + orders flow cơ bản hoạt động (mức smoke).
- [ ] Log runtime không có System.Windows.Data Error nghiêm trọng lặp lại.
- [ ] `vpk pack` đóng gói được bản win và client cũ (channel win) update lên bình thường.
- [ ] Nhánh `avalonia` tồn tại, trỏ đúng đỉnh main trước merge.

## 5. Rủi ro & lưu ý

- **Brush không Freeze** → crash cross-thread lúc runtime, build không bắt được: mọi brush VM phải qua
  `AppBrushes`. Đợt nào port VM có brush thì phải soát.
- **DataGrid WPF khác Avalonia**: không có `DataGridCellPointerPressedEventArgs` (dùng `MouseDoubleClick`),
  template part khác (`BackgroundRectangle` không tồn tại), `SelectionMode` giá trị khác — các style DataGrid
  trong Theme phải viết theo template WPF thật, đừng dịch selector 1:1.
- **Theme là khối lượng lớn nhất** (~170 selector): dịch theo NGHĨA (trigger/template WPF) chứ không theo chữ.
- Bản WPF cũ `989901c` chỉ để tham khảo idiom; mọi hành vi lấy theo code Avalonia HIỆN TẠI.
- Merge main → only-windows sẽ conflict kiểu "sửa ở main / đã xoá ở only-windows" với file .axaml: giải quyết
  = giữ xoá, nội dung mới port tay ở đợt tương ứng (Fable xử lý lúc merge).

---

## Nhật ký điều phối (Fable cập nhật)

- 2026-07-31: chốt WPF với người dùng; khảo sát xong; viết plan tổng + plan đợt 1.
- 2026-07-31: đợt 1–6 hoàn thành, nghiệm thu đạt (build 0 warning, test 1459+61, binding log 0). Nhánh `avalonia` repoint về 5432766 (bản Avalonia cuối, có màn Cài đặt mới); merge only-windows → main = b7766cb. Còn lại trước 1.7.0: smoke-test mode Full trên máy thật, đo R2R/WDAC, thử vpk pack; 2 món nhỏ ghi nợ: cột ĐVVC lưới Đơn hàng cắt chữ, màn Search cửa sổ <860px cần ScrollViewer (plan riêng nếu cần).
