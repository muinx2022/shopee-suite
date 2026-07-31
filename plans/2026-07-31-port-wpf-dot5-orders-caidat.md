# Plan: Port WPF — Đợt 5: toàn bộ module Đơn hàng + màn Cài đặt gộp (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** hoàn thành (Opus đã thực thi xong — xem "Báo cáo thực thi" cuối file)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` (20 quyết định) + "Báo cáo thực thi" plan đợt
> 1/2/3/4. Bẫy đã biết: TemplateBinding cho attached property trong template; `<Run Text="{Binding…}">` phải
> `Mode=OneWay`; TabItem không đặt ContentAlignment=Center và không đặt Foreground/FontWeight trên TabItem
> (thừa kế cây logic lan xuống thân tab — dùng `TextElement.*` trên phần tử trong template); helper
> `VisualTreeSearch`; công tắc `SHOPEESUITE_BINDING_LOG` + `SHOPEESUITE_SOFTWARE_RENDER` (máy này cần bật
> software render mới chụp được ảnh). Việc chạy trong WORKTREE `d:\Projects\shopee-suite-onlywin` (nhánh
> `only-windows`); TUYỆT ĐỐI không đọc/ghi `d:\Projects\shopee-suite`.

## 1. Bối cảnh & mục tiêu

Suite đã WPF hết trừ 2 chỗ còn placeholder: màn Cài đặt gộp và toàn bộ module Đơn hàng
(`orders/XuLyDonShopee.App` — DLL được shell nạp). Đợt 5 port nốt:

**Nguồn tham chiếu:**
- View/style orders cũ: `git show d6bb696:<path>` (như các đợt trước).
- Màn Cài đặt: KHÔNG dùng bản d6bb696 — dùng bản MỚI đã làm lại (một hệ style, không webhook):
  `git show 3456351:suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.axaml` (commit trên main đã merge
  vào nhánh này; VM tương ứng `UnifiedSettingsViewModel` với `Suite.*`/`Orders.*` giữ nguyên).

| Port sang (WPF) | Nguồn Avalonia | Ghi chú |
|---|---|---|
| `orders/XuLyDonShopee.App/Styles/Colors.xaml` | `Styles/Colors.axaml` (78 dòng) | token màu riêng module (tông cam) — giữ key + giá trị |
| `orders/XuLyDonShopee.App/Styles/Controls.xaml` | `Styles/Controls.axaml` (**530 dòng**, ~80 selector) | file style lớn nhất orders: card/field/underline, `ToggleButton.switch` (track+knob), style NumericUpDown/AutoCompleteBox/DataGrid.proxy — dịch theo NGHĨA sang Style/Trigger/Template WPF |
| `orders/XuLyDonShopee.App/Styles/ModuleResources.xaml` | `Styles/ModuleResources.axaml` (24 dòng) | merge Colors + khai 6 converter; icon giờ ở app-level (suite `Themes/Icons.xaml`) — orders tham chiếu geometry qua StaticResource/DynamicResource cấp Application lúc chạy, chọn cách ổn định và ghi rõ |
| `orders/XuLyDonShopee.App/Views/MainView.xaml` (+.cs) | `Views/MainView.axaml` (42 dòng) | gốc module: ContentControl đổi màn theo `SelectedNavIndex`; khai DataTemplate tường minh cho 3 VM con (Accounts/Orders/Statistics) TẠI ĐÂY (ViewLocator đã xoá) |
| `orders/XuLyDonShopee.App/Views/AccountsView.xaml` (+.cs) | `Views/AccountsView.axaml` (**750 dòng**, code-behind 129 dòng) | màn lớn nhất orders: list thẻ tài khoản, TabControl, DataGrid proxy, panel log đen; code-behind: Tapped→Preview mouse, Clipboard→`System.Windows.Clipboard.SetText`, auto-scroll log |
| `orders/XuLyDonShopee.App/Views/OrdersView.xaml` (+.cs) | `Views/OrdersView.axaml` (228 dòng, code-behind 50) | DataGrid đơn + AutoCompleteBox→`ComboBox IsEditable` (QĐ 15); `CellPointerPressed` double-click → `MouseDoubleClick` |
| `orders/XuLyDonShopee.App/Views/OrderStatisticsView.xaml` (+.cs) | `Views/OrderStatisticsView.axaml` (198 dòng) | `CalendarDatePicker` → `DatePicker` |
| `orders/XuLyDonShopee.App/Views/ConfirmDialog.xaml` (+.cs) | `Views/ConfirmDialog.axaml` (29+31 dòng) | modal xác nhận — `Close(bool)` → `DialogResult` |
| `orders/XuLyDonShopee.App/Views/OrderDetailDialog.xaml` (+.cs) | `Views/OrderDetailDialog.axaml` (75+47 dòng) | modal chi tiết — `Close(string?)` → property `Result` + `DialogResult` |
| `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml` (+.cs) | bản 3456351 (514 dòng) | toàn bộ binding `Suite.*`/`Orders.*` giữ nguyên; `Orders` có thể null → binding classic + gate `HasOrders`; NumericUpDown chu kỳ → TextBox + giữ kẹp [1,1440] của VM |

## 2. Phạm vi

- **Làm:** các file trên; khôi phục `orders/XuLyDonShopee.App/Services/DialogService.cs` về dialog thật
  (đợt 1 tạm dùng MessageBox cho Confirm/Info và `EditOrderAsync` trả null — nay dùng ConfirmDialog/
  OrderDetailDialog); `App.xaml` suite: UnifiedSettingsViewModel + orders MainViewModel → view thật (sau đợt
  này KHÔNG còn DataTemplate nào trỏ ComingSoonView); orders cần icon dạng control thì tự tạo bản
  `XuLyDonShopee.App.Controls.PathIcon` tối giản (orders KHÔNG được ref project Shopee.Suite — chiều ref là
  suite→orders; không đảo chiều, không thêm project mới).
- **Không làm:** không sửa VM/logic orders (trừ lỗi compile do port — ghi rõ); không đụng script release,
  csproj (đã WPF từ đợt 1); không commit.

## 3. Các bước thực hiện

1. Đọc trọn nguồn từng file (bắt đầu từ Controls.axaml + AccountsView.axaml vì nặng nhất) + báo cáo 4 đợt
   trước; port theo QĐ 5/6/15/16 và các bẫy đã biết (đầu plan).
2. Thứ tự khuyến nghị: Colors/Controls/ModuleResources → MainView + DataTemplates → OrdersView →
   OrderStatisticsView → 2 dialog + DialogService → AccountsView (750 dòng — chia theo khối) →
   UnifiedSettingsView. Mỗi mốc build một lần cho chắc.
3. Build + test: `dotnet build ShopeeSuite.sln` 0 error 0 warning; test 1459 + 61 xanh.
4. **Chạy thử cách ly — chú ý CAO HƠN các đợt trước** (module Đơn hàng đụng tài nguyên thật của máy):
   - KHÔNG chạy `--mode full` (BraveFleet.StartupSweep sẽ giết Brave của bản production đang chạy).
   - Màn Đơn hàng: chạy `--mode shopee`. Cách ly KÉP: `data-dir.txt` cạnh exe (kho suite) VÀ đổi biến môi
     trường `APPDATA` của TIẾN TRÌNH con sang thư mục tạm trước khi Start-Process (module orders mở
     `%APPDATA%\XuLyDonShopee\app.db` — không đổi APPDATA là đụng DB production). Xác minh sau khi mở app:
     file `app.db` MỚI nằm trong thư mục tạm, DB thật không bị mở (kiểm tra không có handle mới).
   - KHÔNG bấm: đăng nhập, Sync, Xử lý đơn, mở trình duyệt — bất kỳ nút nào phóng browser/extension. Cầu nối
     WS cổng 47821 đang được bản production dùng — nếu app dev tự mở listener lúc khởi động (kiểm bằng
     `netstat`) thì ghi nhận + đóng app sớm, không thử tranh cổng.
   - Chụp: 3 màn orders (Tài khoản / Đơn hàng / Thống kê — seed dữ liệu giả vào app.db TẠM nếu giúp lưới có
     dòng), 2 dialog (mở qua UIAutomation như rig đợt 2–4, nhớ EnumWindows cho ShowDialog), màn Cài đặt ở
     `--mode shopee` (section Chế độ + Phiên bản + Đơn hàng) VÀ `--mode workspace` (section Hiệu năng + Đồng
     bộ nhiều máy; orders null → không lỗi binding). Bật `SHOPEESUITE_SOFTWARE_RENDER=1` khi chụp.
   - `SHOPEESUITE_BINDING_LOG` = 0 dòng sau TỪNG bước ở mọi lượt chạy.
5. Dọn sạch (data-dir.txt, APPDATA tạm, process), điền "Báo cáo thực thi" plan này trong worktree.

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 error 0 warning; test 1459 + 61 xanh.
- [ ] Không còn `ComingSoonView` nào được trỏ tới trong App.xaml; mọi màn hai module là view thật.
- [ ] 3 màn orders + 2 dialog + màn Cài đặt (cả 2 chế độ) dựng đúng bố cục nguồn; DialogService hết đường
      MessageBox tạm.
- [ ] Binding log 0 dòng ở mọi lượt; app thoát ExitCode 0.
- [ ] DB/АppData production không bị đụng (nêu bằng chứng trong báo cáo); không sót file tạm/process.

## 5. Rủi ro & lưu ý

- Đây là đợt RỦI RO NHẤT về an toàn dữ liệu (DB orders thật + cổng 47821 + Brave production) — mọi lượt chạy
  phải qua cách ly kép như bước 4; nghi ngờ gì thì DỪNG chạy thử và ghi nhận, đừng liều.
- `Controls.axaml` cũ có `ToggleButton.switch` template (track + knob trượt) — WPF viết lại bằng
  ControlTemplate + Trigger IsChecked (dịch chuyển knob bằng HorizontalAlignment/Margin, không cần animation
  nếu bản cũ không có).
- AccountsView code-behind cũ dùng `TopLevel.GetTopLevel(this).Clipboard.SetTextAsync` →
  `System.Windows.Clipboard.SetText` (đồng bộ); auto-scroll log tái dùng `Behaviors/LogText` của suite? KHÔNG
  — orders không ref suite: nếu cần thì nhân bản attached property tối giản trong orders (ghi rõ).
- Màn Cài đặt: `Orders` null ở workspace mode — mọi binding `Orders.*` nằm dưới `Visibility` gate `HasOrders`,
  binding classic, không được đổ lỗi binding (tiêu chí 0 dòng áp cả lượt workspace).

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày:** 2026-07-31 · **Người thực thi:** Opus (`opus-executor`) · **Nơi làm:** worktree
`d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`) · **CHƯA commit** (theo plan).

### 1. Kết quả kiểm chứng (số liệu thật)

| Hạng mục | Lệnh | Kết quả |
|---|---|---|
| Build | `dotnet build ShopeeSuite.sln --no-incremental` | **Build succeeded — 0 Warning(s), 0 Error(s)** |
| Test orders | `dotnet test orders/XuLyDonShopee.Tests --no-build` | **Passed 1459 / Failed 0 / Skipped 0** |
| Test core | `dotnet test suite/Shopee.Core.Tests --no-build` | **Passed 61 / Failed 0 / Skipped 0** |
| Lỗi binding runtime | `SHOPEESUITE_BINDING_LOG` + rig 12 bước × 2 chế độ | **0 dòng** sau TỪNG bước, ở **cả 8 lượt chạy** (`d5a`…`d5h`, `d5w`) |
| Đóng app | WM_CLOSE cho cửa sổ chính | **ExitCode 0** ở mọi lượt (trừ lượt `d5a` crash — xem mục 4) |
| `ComingSoonView` còn ai trỏ tới? | grep `App.xaml` | Chỉ còn `ComingSoonViewModel` (màn "sắp có" thật của shell) — **0 màn module** |

### 2. AN TOÀN DỮ LIỆU — cách ly KÉP đã đo, không phải "tin là được"

Máy đang chạy bản production (`ShopeeSuite.exe` PID 33732 + 8 Brave). **Trước khi chạy, tôi phát hiện bước 4
của plan có một chỗ KHÔNG chạy được như mô tả** và phải đổi cách (chi tiết mục 5, điểm 1):

- `Environment.GetFolderPath(ApplicationData)` của .NET 8 trên Windows đi qua `SHGetFolderPath` → **đổi biến
  `APPDATA` KHÔNG redirect được** (đo bằng chương trình dò: đổi `APPDATA`+`USERPROFILE` sang đường dẫn KHÔNG
  tồn tại → hàm trả về **chuỗi rỗng**, tức `app.db` sẽ rơi vào thư mục làm việc, không phải nơi mình muốn).
- Cách chạy được: đổi `USERPROFILE` (kèm `APPDATA`/`LOCALAPPDATA`/`TEMP`) sang một **hồ sơ giả CÓ THẬT**
  (`…\scratchpad\d5-home-<tag>\AppData\Roaming`). Đo lại: `GetFolderPath` trả về đúng hồ sơ giả.
- Vì sao bắt buộc: `OrdersModuleHost.TryCreate()` gọi `new AppServices()` (không tham số) → `app.db` mặc định,
  **và** `WireBrowserLifetime` đăng ký `BraveFleet.AddManagedRoot({thư mục app.db}\profiles)` rồi chạy
  `StartupSweep()` — nghĩa là chạy `--mode shopee` mà không cách ly thì vừa mở DB thật vừa có thể **giết Brave
  của bản production**. (Plan chỉ cảnh báo StartupSweep ở `--mode full`; thực tế nó chạy cả ở `--mode shopee`.)

Kết quả đo trước/sau **mọi lượt chạy** (rig tự in):

| Kiểm chứng | Trước | Sau |
|---|---|---|
| `%APPDATA%\XuLyDonShopee\app.db` (production) | 1.339.392 bytes · ghi lúc 2026-07-31 01:23:12 · sha256 `4BDF62F4…` | **Y HỆT** (cả 9 lượt) |
| Số tiến trình Brave | 8 | **8** |
| `app.db` trong hồ sơ giả | — | **có** (được tạo + seed) |
| Cổng cầu nối 47821 do PID dev mở | — | **0 dòng netstat** (không tranh cổng với production) |
| Tiến trình sót | — | chỉ còn ShopeeSuite **production** (PID 33732) |

Ngoài ra: kho suite (`hub-client.json`, `persistent-data`…) đi qua `data-dir.txt` cạnh exe → thư mục tạm; hub
KHÔNG cấu hình nên `CoordinationRuntime` NoOp — nhật ký trong app cho thấy đúng điều đó: *"Hub: đẩy 0/24 đơn —
hub không phản hồi, sẽ thử lại lượt sau"*. Không bấm nút đăng nhập/Sync/Xử lý đơn/mở trình duyệt nào.
Đã dọn sạch: 9 thư mục `d5-data-*`, 9 thư mục `d5-home-*`, `data-dir.txt`.

### 3. File đã tạo / sửa

| File | Việc |
|---|---|
| `orders/XuLyDonShopee.App/Styles/Colors.xaml` | **TẠO** — port 78 dòng, giữ nguyên key + giá trị (bỏ Inter/JetBrains Mono khỏi FontFamily) |
| `orders/XuLyDonShopee.App/Styles/Controls.xaml` | **TẠO** — port ~80 selector của `Controls.axaml` sang Style/Trigger/Template WPF |
| `orders/XuLyDonShopee.App/Styles/ModuleResources.xaml` | **TẠO** — merge Controls (Controls tự merge Colors) + 13 converter |
| `orders/XuLyDonShopee.App/Controls/PathIcon.cs` | **TẠO** — bản PathIcon RIÊNG của module (không đảo chiều ref) |
| `orders/XuLyDonShopee.App/Behaviors/WatermarkAssist.cs` | **TẠO** — gợi ý mờ cho `TextBox` (bản riêng, ~50 dòng) |
| `orders/XuLyDonShopee.App/Behaviors/PasswordBoxAssist.cs` | **TẠO** — bind được `PasswordBox.Password` (bản riêng) |
| `orders/XuLyDonShopee.App/Converters/VisibilityConverters.cs` | **TẠO** — 6 converter: `InvBoolToVis`/`StringToVis`/`EmptyToVis`/`NullToVis`/`NotNullToVis`/`DateOffset` |
| `orders/XuLyDonShopee.App/Views/MainView.xaml` (+`.cs`) | **TẠO** — vỏ module + 3 DataTemplate màn con |
| `orders/XuLyDonShopee.App/Views/AccountsView.xaml` (+`.cs`) | **TẠO** — port 750 dòng + 129 dòng code-behind (9 style cục bộ, segmented TabControl, lưới kết quả) |
| `orders/XuLyDonShopee.App/Views/OrdersView.xaml` (+`.cs`) | **TẠO** — port 228 dòng; `CellPointerPressed` → `MouseDoubleClick` + leo cây tìm `DataGridRow` |
| `orders/XuLyDonShopee.App/Views/OrderStatisticsView.xaml` (+`.cs`) | **TẠO** — port 198 dòng; `CalendarDatePicker` → `DatePicker` |
| `orders/XuLyDonShopee.App/Views/ConfirmDialog.xaml` (+`.cs`) | **TẠO** — `Close(bool)` → `DialogResult` |
| `orders/XuLyDonShopee.App/Views/OrderDetailDialog.xaml` (+`.cs`) | **TẠO** — `Close(string?)` → property `Result` + `DialogResult` |
| `orders/XuLyDonShopee.App/Services/DialogService.cs` | **SỬA** — bỏ hết đường MessageBox tạm; `ConfirmAsync`/`InfoAsync` → ConfirmDialog, `EditOrderAsync` → OrderDetailDialog |
| `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml` (+`.cs`) | **TẠO** — port bản mới 408 dòng của `3456351` (plan ghi 514 — xem mục 5 điểm 6) |
| `suite/Shopee.Suite/App.xaml` | **SỬA** — 3 DataTemplate ComingSoon → view thật (UnifiedSettings + orders MainView); bỏ dòng trỏ `SettingsViewModel` (xem mục 5 điểm 5) |

Không sửa dòng nào trong ViewModel/logic của orders lẫn suite. `Themes/Theme.xaml`, `Themes/Icons.xaml`,
`csproj`, script release: **không đụng**.

### 4. Lỗi RUNTIME phải sửa trong lúc port (build KHÔNG bắt được)

**`StaticResource` trong dictionary được merge KHÔNG thấy dictionary "anh em"** — lượt chạy đầu (`d5a`) app
**chết ngay khi mở** (stack overflow do `HandleUiCallbackException` mở MessageDialog rồi lại ném — cùng kiểu
khuếch đại đã gặp ở đợt 3). Gốc trong `%TEMP%\shopeesuite-crash.log`:

```
XamlParseException: Initialization of 'XuLyDonShopee.App.Controls.PathIcon' threw an exception.
 ---> InvalidOperationException: '{DependencyProperty.UnsetValue}' is not a valid value for property 'Foreground'
```

`Controls.xaml` đặt `Foreground="{StaticResource TextMuted}"`, mà `Colors.xaml` khi đó là **anh em** của nó
trong `ModuleResources.xaml` chứ không nằm trong phạm vi của chính nó. Phạm vi phân giải `StaticResource` của
một ResourceDictionary = entry của nó + dictionary NÓ merge + dự phòng `Application.Resources`. Nên các key
TRÙNG TÊN với suite (`AccentBrush`/`SuccessBrush`/`DangerBrush`) vẫn "chạy" nhờ rơi vào dự phòng Application
(che lỗi!), còn key riêng của module (`TextMuted`/`CardBg`/`Border010`…) ra `UnsetValue` → gán vào `Foreground`
là ném lúc dựng cây. **Đã sửa:** `Controls.xaml` tự merge `Colors.xaml`; `ModuleResources.xaml` chỉ merge
`Controls.xaml` (tra tài nguyên đi đệ quy nên view vẫn thấy đủ token, và không tạo bản sao brush thứ hai).
Kèm comment cảnh báo trong cả 2 file.

### 5. Điểm trệch plan / quyết định phải tự lấy

1. **Cách ly kép làm KHÁC bước 4 của plan** (bắt buộc, đã đo): đổi `APPDATA` không đủ — phải đổi `USERPROFILE`
   sang hồ sơ giả CÓ THẬT (mục 2). Kèm phát hiện: `StartupSweep` chạy cả ở `--mode shopee` chứ không chỉ
   `--mode full` như plan ghi.
2. **KHÔNG port 9 nhóm selector 0-caller của `Controls.axaml`** (đã ghi rõ ngay đầu file mới): `ListBox.nav*`,
   `ListBox.navTop*`, `Border.topbar`/`statusBar` + 2 style chữ của chúng, `ListBox.acct` + `Border.acct-card`,
   `ListBox.logDark`, `Button.accent/.accentOutline/.iconDanger`, `Button.iconAction(.play)` (style cục bộ chết
   trong `AccountsView.axaml`), `NumericUpDown.field`. Lý do: nav dọc/ngang + thanh trạng thái của module đã
   dời lên shell, danh sách log đã đổi sang TextBox, và WPF không có NumericUpDown. `ToggleButton.switch` thì
   **có port** (0 caller) vì plan nêu đích danh ở mục 5.
3. **2 hộp thoại merge `ModuleResources`** (bản Avalonia không merge). Ở WPF, style `primary` của suite chỉ tô
   được `PathIcon` KIỂU SUITE; icon trong 2 dialog là `PathIcon` kiểu module nên không merge là icon ra xám.
   Một dòng merge, giao diện đúng như bản cũ.
4. **`AutoCompleteBox` → `ComboBox IsEditable`** (QĐ 15) có một khác biệt hành vi đã biết: dropdown gợi ý
   **không tự lọc** theo chuỗi đang gõ (WPF không có `FilterMode`), nó vẫn liệt kê đủ shop. Việc lọc THẬT nằm
   ở bảng bên dưới (VM lọc theo `AccountFilterText`) nên thao tác người dùng không đổi. Gợi ý mờ của ô này làm
   bằng `TextBlock` phủ (template mặc định của ComboBox không chèn được `PART_Watermark`).
5. **Xoá DataTemplate cho `settings:SettingsViewModel`** trong `App.xaml`: VM này chỉ sống lồng trong
   `UnifiedSettingsViewModel` (`ShellViewModel` không bao giờ đặt nó làm nội dung màn), giữ lại thì lại là một
   dòng trỏ `ComingSoonView` — trái tiêu chí nghiệm thu.
6. **Nguồn màn Cài đặt là 408 dòng, không phải 514** như plan ghi (`git show 3456351:…UnifiedSettingsView.axaml`
   = 408). Đã port trọn vẹn bản 408 dòng đó.
7. **`{StaticResource Border}` ở `AccountsView.axaml` dòng 635 là key KHÔNG TỒN TẠI** (Colors.axaml chỉ có
   `BorderSoft`/`Border05`/`Border06`/`Border010`…; Theme.axaml của suite cũng không có key `Border`). Thẻ đó
   vốn là "card" (nền CardBg + viền + bo 6) nên tôi dùng thẳng `Style="{StaticResource card}" Padding="12,7"`.
8. **`ResultDate` là `DateTimeOffset` không-null** còn `DatePicker.SelectedDate` của WPF là `DateTime?` → phải
   có converter (`DateOffset`). `ConvertBack` trả `Binding.DoNothing` khi người dùng xoá trắng lịch → giữ đúng
   ghi chú "xoá trắng KHÔNG ghi đè" của bản Avalonia.
9. **Đã ĐO chuyện `Orders` null**: giữ đúng plan (binding classic `Orders.*` + gate `Visibility` theo
   `HasOrders`), chạy `--mode workspace` → **0 dòng** lỗi binding. Tức WPF coi mắt xích null giữa PropertyPath
   là "giá trị chưa có", không phải lỗi thiếu property. (Tôi đã thử viết bản `ContentControl + ContentTemplate`
   để né hẳn, nhưng bỏ vì plan yêu cầu giữ nguyên đường bind và số đo cho thấy không cần.)
10. **Bổ sung nhỏ so với nguồn:** `CanUserSortColumns="False"` cho 6 DataGrid (giữ thói quen đợt 3–4);
    `UpdateSourceTrigger=PropertyChanged` cho các ô lọc/tìm kiếm (Avalonia mặc định cập nhật theo từng ký tự,
    WPF mặc định `LostFocus` → không ghi rõ là ô tìm kiếm "không phản hồi"); `IsDefault`/`IsCancel` cho 2 cặp
    nút của dialog (Enter/Esc).

### 6. Nghiệm thu bằng mắt (rig UIAutomation)

Script `…\86f7fb17-…\scratchpad\verify-dot5.ps1` (viết mới, kế thừa `verify-dot4.ps1`) + chương trình seed
`…\scratchpad\seed-orders\` (console .NET 8 ngoài repo, tham chiếu `XuLyDonShopee.Core`, dùng ĐÚNG repository
thật để bơm 3 tài khoản + 4 shop + 24 đơn đủ nhóm trạng thái vào `app.db` TẠM).

| Ảnh (hậu tố `-d5h` = lượt shopee đầy đủ, `-d5w` = lượt workspace) | Nội dung đã soi |
|---|---|
| `d5-1-orders-tai-khoan-*.png` | Màn **Tài khoản**: cột trái (ô tìm + gợi ý mờ, nút lọc "Những TK chưa xác nhận (1)" icon đỏ, 3 dòng tk, nhãn đỏ "TK chưa xác nhận" + nút vuông "Truy cập TK", hàng nút Thêm/Kéo TK/Xoá), placeholder giữa, panel nhật ký đen bên phải + đường dẫn file log |
| `d5-2-orders-tk-ket-qua-*.png` | Chọn tk → VM tự mở tab **Kết quả** (đúng thiết kế `DetailTabIndex=1`): khay segmented + huy hiệu "4", tên tk mép phải, DatePicker, dòng "Chưa gộp được từ Hub", ô TỔNG "22 đơn", lưới 3 cột (Mexico 7 · Saigon 3 · Hanoi 12 · Da Nang 0) |
| `d5-3-orders-tk-chi-tiet-*.png` | Tab **Thông tin tài khoản**: 4 card (ĐĂNG NHẬP 2 cột · COOKIE "✓ Đã có" · EMAIL XÁC MINH · ĐỊA CHỈ LẤY HÀNG), nhãn section cam, ô nhập kiểu underline, nút Lưu (icon cam) / Hủy |
| `d5-3b-orders-tk-hien-mk-*.png` | Bấm con mắt → ô mật khẩu đổi từ PasswordBox (●●●) sang TextBox hiện chữ `matkhau-demo-01`, gạch dưới thành CAM khi focus; ô "Mật khẩu email" vẫn che → cặp PasswordBox/TextBox hoạt động đúng. Panel log đã có dòng thật + tự cuộn |
| `d5-4-orders-tk-chua-xac-nhan-*.png` | Chọn tk thứ 3: huy hiệu tab đổi về "0", tiêu đề log đổi theo tk, lưới kết quả rỗng |
| `d5-5-orders-don-hang-*.png` | Màn **Đơn hàng**: thanh lọc (ô gõ-để-lọc-shop + gợi ý mờ, combo trạng thái, ô tìm, 3 nút), dòng "Đang hiển thị: 24/24 đơn" + pager + "100 /trang", lưới 16 cột, **pill trạng thái đúng màu theo từ khoá** (cam chờ · xanh dương đang giao · xanh lá hoàn thành/đã giao · đỏ hủy/trả hàng), cột Phiếu 2 nút link cam |
| `d5-6-orders-thong-ke-*.png` | Màn **Thống kê**: 2 hàng × 5 thẻ số (Tổng 24 · Cần xử lý 4 cam · Đã giao 6 xanh · Đã hủy 3 đỏ · Doanh thu ₫11.160.000), 2 DatePicker + combo shop + nút Làm mới, 4 lưới (trạng thái/shop/ĐVVC/thanh toán), ghi chú cách tính |
| `d5-8-dialog-xac-nhan-*.png` | **ConfirmDialog**: tiêu đề "Xóa tài khoản", câu hỏi, nút "Đồng ý" (icon check cam) + "Hủy" |
| `d5-9-dialog-chi-tiet-don-*.png` | **OrderDetailDialog** (double-click dòng lưới): 9 dòng nhãn:giá trị (chọn/copy được), combo "Đổi trạng thái" chọn sẵn đúng trạng thái đơn, nút Lưu/Hủy |
| `d5-7-caidat-shopee-*.png` + `d5-7b-…-cuoi-*.png` | **Cài đặt · chế độ Shopee**: CHẾ ĐỘ ỨNG DỤNG (combo + chú thích "đang khoá bởi shortcut" + nút tạo shortcut) · PHIÊN BẢN & CẬP NHẬT · ĐƠN HÀNG (Tự động hoá: thư mục + ô "Chu kỳ theo dõi đơn (phút)"=30 · Trình duyệt: combo + "Đang dùng: Chrome (…)" · Đồng bộ Google Sheet: 3 ô có gợi ý mờ). **Hiệu năng + Đồng bộ nhiều máy ẩn đúng** |
| `d5-7-caidat-workspace-*.png` + `d5-7b-…-cuoi-*.png` | **Cài đặt · chế độ Workspace**: hiện HIỆU NĂNG (2 ô số + dòng "→ Tối đa 10 cửa sổ Brave") và ĐỒNG BỘ NHIỀU MÁY (tên máy + kết nối Hub + 4 nút). **Section ĐƠN HÀNG ẩn đúng** (Orders null) và **0 dòng lỗi binding** |

### 7. Còn lại / đề nghị cho đợt 6

1. **Nhãn nút "Thêm tài khoản" bị cắt thành "Thêm tài khoả"** ở cột trái rộng 340px (3 nút chia nhau chỗ). Đây
   là đúng cấu trúc `ColumnDefinitions="*,Auto,Auto"` của bản Avalonia chứ không phải lỗi port, nhưng nhìn hỏng
   — đợt 6 nên cho nút "Kéo TK từ Hub" thu gọn hoặc đưa xuống dòng.
2. **Pill "Trả hàng/Hoàn tiền" bị cắt** trong cột Trạng thái rộng 140 (giống bản cũ). Nới cột hoặc
   `TextTrimming` ở đợt 6.
3. **ComboBox vẫn dùng chrome mặc định của WPF** — đúng như đợt 4 đã ghi (dựng template phẳng dồn về đợt 6 để
   2 module đổi cùng lúc). Ở module Đơn hàng tôi cũng chỉ set cỡ/viền/nền, KHÔNG set `CornerRadius` (ComboBox
   WPF không có thuộc tính đó).
4. **`LetterSpacing` bỏ** (WPF không có) ở `TextBlock.section` và các tiêu đề 19px — như các đợt trước.
5. **Chưa quan sát được bằng mắt** (cần phiên trình duyệt THẬT nên cố ý không thử): vòng quay cam của cột tiến
   độ tab Kết quả (`IsChecking`), chấm xanh + "Chờ lấy: N" trên dòng tk đang chạy, nút "Tải phiếu" ở trạng thái
   thiếu file. Binding của chúng đã chạy (0 lỗi log); riêng vòng quay dùng `DataTrigger` + `EnterActions` với
   `RotateTransform` đặt TẠI CHỖ DÙNG (đặt trong Setter là mọi dòng chung một transform → animate sai).
6. **`Views/PortingWindow.cs` của đợt 1 vẫn 0 lớp con** — đợt 5 không cần placeholder nào, nên xoá được ở đợt 6.
7. **Cách chạy lại rig:**
   ```powershell
   & '<scratchpad>\verify-dot5.ps1' -Tag <hậu-tố> -Mode shopee      # 3 màn orders + 2 dialog + Cài đặt
   & '<scratchpad>\verify-dot5.ps1' -Tag <hậu-tố> -Mode workspace -NoSeed
   ```
   Script tự: chụp vân tay `app.db` production trước/sau, tạo/xoá `data-dir.txt`, dựng hồ sơ người dùng giả,
   seed dữ liệu, in số dòng binding log sau TỪNG bước, kiểm netstat 47821 + số tiến trình Brave, và đóng đúng
   PID nó mở.
