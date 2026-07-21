# Plan: Gộp phase 1b — module hóa app đơn hàng vào shell Shopee.Suite

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nhánh:** `feature/gop-don-hang` (đã checkout sẵn trên cây chính — KHÔNG đụng `main`)

## 1. Bối cảnh & mục tiêu

Tiếp theo phase 1a (đã xong, commit `690a287`): 3 project `orders/XuLyDonShopee.{Core,App,Tests}`
đã nằm trong `ShopeeSuite.sln`, build sạch, 720 test xanh, nhưng App vẫn là WinExe độc lập.

Phase 1b: biến `XuLyDonShopee.App` thành **thư viện module** và cắm vào shell `Shopee.Suite`
thành module sidebar "Xử lý đơn Shopee" (giữ nguyên nav nội bộ 5 màn: Tài khoản / Đơn hàng /
Chạy tự động / Proxy / Cài đặt). Sau phase này solution chỉ còn 1 exe (`Shopee.Suite`).
Dữ liệu vẫn đọc/ghi `%APPDATA%\XuLyDonShopee` như cũ (zero migration).

Kiến trúc 2 bên (đã khảo sát, số dòng tính tại commit `690a287`):
- **Suite**: entry `suite/Shopee.Suite/Program.cs` (VelopackApp trước Avalonia); `App.axaml`
  map VM→View bằng DataTemplate TƯỜNG MINH (dòng 28-42), style global ở `Themes/Theme.axaml`
  (nạp tại `App.axaml:25`); shell = `ViewModels/ShellViewModel.cs` (danh sách `Modules` dòng
  72-88, VM tạo 1 lần sống suốt đời) + `MainWindow.axaml` (top bar + `ContentControl Current`);
  dừng-êm trước update: hook static `UpdateService.PrepareShutdownAsync` (gán ở
  `ShellViewModel.cs:57-63`, trần 45s ở `UpdateService.cs:99-108`); shutdown thường:
  `App.axaml.cs:65-70` (`desktop.ShutdownRequested`).
- **Orders (app đơn hàng)**: entry cũ `Program.cs` + `App.axaml(.cs)` — tạo `AppServices`
  (`Services/AppServices.cs:50-72`, mở SQLite + migration ngay trong ctor), tạo
  `MainWindow{DataContext=new MainViewModel(services)}`, gán `DialogService.MainWindow`,
  hook `ShutdownRequested` = `AutoRun.StopAsync()` rồi `Sessions.StopAllAsync()` (block, đúng
  thứ tự — kill hết Brave tránh mồ côi). `MainViewModel : ViewModelBase`, ctor CHỈ cần
  `AppServices`, không phụ thuộc Window. Map VM→View bằng `ViewLocator.cs` (reflection,
  `Match` CHỈ nhận `XuLyDonShopee.App.ViewModels.ViewModelBase` → thêm vào suite an toàn,
  không hijack VM suite). `DialogService` (static) cần `Window` owner thật cho
  ShowDialog/StoragePicker. 2 dialog (`ConfirmDialog`, `ImportProxyDialog`) là Window thuần,
  ĐÃ KIỂM: không dùng StaticResource/Classes nào của app → không cần tiêm style vào dialog.

Xung đột version phải đồng bộ trong phase này:

| Package | Suite | Orders | → Chốt |
|---|---|---|---|
| Avalonia (+Desktop/Fluent/DataGrid/Inter) | 11.3.0 | 11.2.8 | **11.3.0** (nâng orders) |
| CommunityToolkit.Mvvm | 8.3.2 | 8.4.2 | **8.4.2** (nâng suite) |
| Microsoft.Playwright | 1.60.0 (Shopee.Core) | 1.49.0 (XuLyDonShopee.Core) | **1.60.0** (nâng orders) |

Binding mode: orders bật `AvaloniaUseCompiledBindingsByDefault=true` + `x:DataType` trong các
view — GIỮ NGUYÊN trong csproj của orders (thuộc tính theo project, không ảnh hưởng suite).

## 2. Phạm vi

- **Làm:** module hóa App orders + cắm vào shell + đồng bộ version + wire vòng đời + cô lập style.
- **Không làm:**
  - KHÔNG gộp domain tài khoản/proxy/browser với suite (phase 2–3).
  - KHÔNG di trú dữ liệu (`%APPDATA%\XuLyDonShopee` giữ nguyên).
  - KHÔNG đổi namespace/tên project orders; KHÔNG viết test mới (baseline 720 giữ nguyên).
  - KHÔNG cấu hình publish/Velopack/release; KHÔNG đụng `server/`.
  - KHÔNG commit (Fable nghiệm thu rồi commit).

## 3. Các bước thực hiện

### A. `orders/XuLyDonShopee.App` → thư viện module

1. `XuLyDonShopee.App.csproj`: bỏ `OutputType=WinExe` (thành Library), bỏ
   `ApplicationManifest` + xóa `app.manifest`, bỏ `BuiltInComInteropSupport` (đã grep: không
   có COM interop). Nâng toàn bộ package Avalonia 11.2.8 → **11.3.0**. Giữ
   `AvaloniaUseCompiledBindingsByDefault=true`.
2. Xóa `Program.cs`, `App.axaml`, `App.axaml.cs` (logic khởi tạo/shutdown chuyển sang suite —
   bước C). GIỮ `ViewLocator.cs`.
3. `Views/MainWindow.axaml(.cs)` → **`Views/MainView.axaml(.cs)`** (đổi tên file + class,
   `Window` → `UserControl`; tên `MainView` BẮT BUỘC để ViewLocator resolve từ
   `MainViewModel`). Giữ nguyên `x:DataType="vm:MainViewModel"`, DockPanel topbar + statusBar
   + `ContentControl CurrentViewModel`. Bỏ các thuộc tính chỉ có ở Window (`Title`, `Icon`,
   `Width/Height`, `WindowStartupLocation`, `Background` gradient cấp window — nếu muốn giữ
   nền gradient thì chuyển thành Background của Grid/DockPanel gốc trong UserControl).
4. **Cô lập style/resource vào subtree module** (tránh đụng `Theme.axaml` suite — trùng key
   `AccentBrush/SuccessBrush/DangerBrush/WarningBrush/UiFont/MonoFont`, trùng class
   `Border.card`): trong `MainView.axaml` khai báo
   - `UserControl.Resources`: `ResourceInclude Styles/Colors.axaml` + đăng ký lại 6 converter
     đang nằm ở `App.axaml` cũ (`VnEnum`, `StatusColor`, `DateDisplay`, `Initial`,
     `StatusPill`, `OrderStatusPill` — đúng key cũ).
   - `UserControl.Styles`: `StyleInclude Styles/Controls.axaml`.
   - `FontFamily="{StaticResource UiFont}"` trên UserControl gốc.
   TUYỆT ĐỐI không thêm Colors/Controls của orders vào `Application.Resources/Styles` của suite.

### B. Đồng bộ version còn lại

5. `orders/XuLyDonShopee.Core/XuLyDonShopee.Core.csproj`: Playwright 1.49.0 → **1.60.0**.
6. `suite/Shopee.Suite/Shopee.Suite.csproj`: CommunityToolkit.Mvvm 8.3.2 → **8.4.2**; thêm
   `ProjectReference` → `orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj`.

### C. Cắm vào shell suite

7. Tạo `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` — glue tĩnh, nhỏ:
   - `static AppServices? Services` (private set);
   - `static MainViewModel? TryCreate()`: `try { Services = new AppServices(); return new
     MainViewModel(Services); } catch (Exception ex) { Trace/Debug ghi lỗi; return null; }`
     (AppServices ctor mở SQLite + migration — nếu hỏng thì suite vẫn chạy, chỉ thiếu module);
   - `static Task StopAsync()`: nếu `Services != null` → `await AutoRun.StopAsync()` RỒI
     `await Sessions.StopAllAsync()` (đúng thứ tự); guard flag chống gọi đúp (kiểm tra
     `AutoRunService.StopAsync`/`StopAllAsync` có idempotent không — nếu có thì khỏi guard).
8. `suite/Shopee.Suite/Infrastructure/AppIcons.cs`: thêm hằng icon mới cho đơn hàng (Material
   filled 24×24, dạng receipt/hóa đơn — vd `receipt_long`), đặt tên `Receipt`, format giống
   các hằng sẵn có.
9. `suite/Shopee.Suite/ViewModels/ShellViewModel.cs`: trong ctor gọi
   `OrdersModuleHost.TryCreate()`; nếu non-null → chèn
   `new ModuleItem("Xử lý đơn Shopee", AppIcons.Receipt, "Tài khoản shop · theo dõi & xử lý đơn · in phiếu", ordersVm, "Đơn hàng")`
   vào danh sách `Modules` NGAY TRƯỚC mục "Cài đặt". Nối thêm vào cuối hook
   `UpdateService.PrepareShutdownAsync` (dòng 57-63): `await OrdersModuleHost.StopAsync()`
   (sau các bước dừng của suite, vẫn trong trần 45s).
10. `suite/Shopee.Suite/App.axaml`: thêm xmlns tới `XuLyDonShopee.App` và
    `<ordersRoot:ViewLocator/>` vào `Application.DataTemplates` (an toàn vì `Match` chỉ nhận
    ViewModelBase của orders; các DataTemplate tường minh của suite giữ nguyên).
11. `suite/Shopee.Suite/App.axaml.cs`: sau khi tạo `desktop.MainWindow`:
    - `XuLyDonShopee.App.Services.DialogService.MainWindow = desktop.MainWindow;` (dialog/
      picker của module owner về cửa sổ shell);
    - trong handler `ShutdownRequested` hiện có (dòng 65-70), thêm (try/catch riêng, đặt
      TRƯỚC phần cleanup MultiBrave): `OrdersModuleHost.StopAsync().GetAwaiter().GetResult();`
      — giữ ngữ nghĩa block như app gốc để Brave không mồ côi khi đóng app.

### D. Kiểm chứng

12. `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi; cảnh giác warning NU1605/NU1608 do
    version mới — phải xử lý sạch (không để downgrade ngầm).
13. `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj -c Release` → 720 pass
    (tests không đụng Program/App.axaml/MainWindow — đã kiểm trước, xóa các file đó không phá test).
14. Chạy thật: `dotnet run --project suite/Shopee.Suite/Shopee.Suite.csproj` → app mở được,
    top bar có tab "Đơn hàng", click vào thấy đủ 5 nav nội bộ + status bar; đảo qua vài module
    suite cũ (Workspace, Dữ liệu, Cài đặt) xác nhận view vẫn render, màu suite không đổi
    (AccentBrush suite vẫn xanh `#0078D7`, module orders vẫn cam `#EE4D2D`). Đóng app sạch.
    Nếu bị WDAC chặn chạy (0x800711C7) → ghi nhận, để người dùng kiểm tay.

## 4. Tiêu chí nghiệm thu

- [ ] Solution chỉ còn 1 WinExe (`Shopee.Suite`); `XuLyDonShopee.App` là Library, không còn
      Program/App.axaml/app.manifest/MainWindow (đã thành MainView UserControl).
- [ ] Build Release 0 lỗi, không warning NuGet version mới; 720/720 test xanh.
- [ ] App chạy: có module "Xử lý đơn Shopee" (nav "Đơn hàng"), 5 màn nội bộ hiển thị; các
      module suite cũ không đổi hành vi/màu sắc.
- [ ] Style hai bên không lẫn: không thêm resource/style orders nào vào Application của suite.
- [ ] Vòng đời wire đủ 2 đường: `ShutdownRequested` và `UpdateService.PrepareShutdownAsync`
      đều dừng AutoRun + Sessions của orders.
- [ ] KHÔNG sửa gì ngoài các file nêu trên (đặc biệt không đụng `server/`, `main`).

## 5. Rủi ro & lưu ý

- **Nút của orders dưới ControlTheme Button global của suite** (`Themes/Theme.axaml:104-139`
  thay hẳn template, dùng `Border#PART_bd`): các class `.accent/.secondary/...` của orders nhắm
  `/template/ ContentPresenter#PART_ContentPresenter` (template Fluent) có thể KHÔNG ăn → nút
  mất màu nhấn. Nếu quan sát thấy vậy: thêm setter cấp control (Background/Foreground trực tiếp
  trên `Button.accent` v.v.) trong `Controls.axaml` làm fallback — chấp nhận hover chưa giống
  100%, KHÔNG sửa Theme.axaml của suite, KHÔNG vendored template Fluent. Ghi lại hiện trạng
  visual vào báo cáo.
- **Playwright 1.49→1.60**: API tương thích nhưng hành vi runtime (CDP/stealth) chưa kiểm —
  nghiệm thu cuối cùng (mở phiên Brave + login + sync đơn thật) do NGƯỜI DÙNG làm sau khi merge
  nhánh; phase này chỉ cần build/test/app-mở-được. Không được "vá stealth" gì thêm.
- **SQLite chung file**: khi chạy suite hợp nhất thì KHÔNG chạy app Xu-ly-don-shopee cũ song
  song (tranh khóa `%APPDATA%\XuLyDonShopee\app.db`). Ghi chú này vào báo cáo cho người dùng.
- Máy có WDAC/ISG: fail đồng loạt `0x800711C7` khi test/chạy = chính sách máy, báo cáo chứ
  đừng sửa code né.
- `OrdersChanged` bắn từ thread nền — `MainViewModel` đã tự marshal về UI thread, không cần sửa.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
