# Plan: Port WPF — Đợt 5: toàn bộ module Đơn hàng + màn Cài đặt gộp (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
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

<để trống>
