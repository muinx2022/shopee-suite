# Plan: Port WPF — Đợt 3: WorkspaceView (màn nặng nhất) + ScrapeStatsWindow (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` (20 quyết định kỹ thuật) + mục "Báo cáo thực
> thi" của plan đợt 1 và đợt 2 (quy ước đã hình thành + các bẫy đã gặp). Việc chạy trong WORKTREE
> `d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`); TUYỆT ĐỐI không đọc/ghi
> `d:\Projects\shopee-suite`.

## 1. Bối cảnh & mục tiêu

Đợt 1–2 xong: shell + Accounts/Data/BigSeller đã là WPF thật, build 0 warning, binding log 0. Đợt 3 port màn
lớn nhất của app:

| Port sang | Nguồn Avalonia (`git show d6bb696:<path>`) | Ghi chú |
|---|---|---|
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml` (+.cs) | `.../Workspace/WorkspaceView.axaml` (**924 dòng**, code-behind 27 dòng) | TabControl nhiều tab con, ≥2 DataGrid, ô log, 16 chỗ pseudo-class, `$parent`, code-behind dùng `FindAncestorOfType<DataGridRow>` + Tapped |
| `suite/Shopee.Suite/Modules/Scrape/ScrapeStatsWindow.xaml` (+.cs) | `.../Scrape/ScrapeStatsWindow.axaml` (54 dòng, code-behind 21 dòng) | Cửa sổ thống kê scrape — thay placeholder C# `Modules/Scrape/ScrapeStatsWindow.cs` hiện tại (GIỮ nguyên tên class/chữ ký nơi gọi) |

Các VM liên quan (WorkspaceViewModel/WorkspaceShopViewModel/WorkspaceStatsViewModel/Scrape*) đã port từ đợt 1
(brush qua `AppBrushes`) — đợt này CHỈ làm view.

## 2. Phạm vi

- **Làm:** 2 view trên; thay DataTemplate placeholder của WorkspaceViewModel trong `App.xaml` bằng view thật;
  xoá placeholder `ScrapeStatsWindow.cs`; port nốt các style Theme.xaml đang đánh dấu `<!-- đợt N -->` mà
  WorkspaceView cần (đối chiếu selector Workspace trong Theme.axaml cũ qua `git show d6bb696:`).
- **Không làm:** Search/CheckAccount/Fleet/Settings/orders (đợt 4–5); không sửa ViewModel trừ lỗi compile
  thật sự do port (ghi rõ báo cáo); không commit.

## 3. Các bước thực hiện

1. Đọc nguồn `WorkspaceView.axaml` cũ TRỌN VẸN trước khi viết (924 dòng — chia theo tab con mà port, đừng
   dịch trộn); đọc `Theme.xaml` hiện tại (bảng quy ước đầu file) + báo cáo đợt 2 (bẫy watermark: đã fix bằng
   `TemplateBinding`, KHÔNG quay lại kiểu `{Binding (b:…)}`).
2. Port `WorkspaceView.xaml`: đúng cấu trúc tab con + lưới + panel như bản cũ; `Classes.xxx` động →
   DataTrigger; `$parent[...]` → RelativeSource FindAncestor; code-behind Tapped → `PreviewMouseLeftButtonUp`
   + helper leo `VisualTreeHelper` (đợt 2 đã có mẫu trong `DataView.xaml.cs` — tái dùng cùng idiom, nếu trùng
   logic thì cân nhắc đưa helper chung vào `Infrastructure/` thay vì copy).
3. Port `ScrapeStatsWindow.xaml` + code-behind; giữ đúng cách `WindowHost`/VM đang mở nó.
4. Build + test: `dotnet build ShopeeSuite.sln` 0 error 0 warning; 2 project test xanh.
5. Chạy thử cách ly (quy ước như đợt 2: `data-dir.txt` cạnh exe trỏ thư mục tạm — XOÁ sau; `--mode
   workspace`; KHÔNG bấm nút chạy scrape/import/update thật, không phóng Brave; chỉ đóng đúng PID mình mở).
   Màn Workspace là màn mặc định của tab Workspace. Dùng lại rig UIAutomation ở
   `C:\Users\NGXUAN~1\AppData\Local\Temp\claude\d--Projects-shopee-suite\86f7fb17-b280-49ad-87e5-94d7a1e7b273\scratchpad\`
   (`verify-dot2.ps1`, `verify-modals2.ps1` — bản modals2 đã sửa 3 lỗi rig: chọn tab trước, dò cửa sổ
   ShowDialog bằng EnumWindows + FromHandle, gate nút theo hub-client.json/hub-server.json giả). Chụp từng
   tab con của Workspace + ScrapeStatsWindow (nếu mở được từ UI với dữ liệu rỗng; bị gate thì ghi rõ),
   `SHOPEESUITE_BINDING_LOG` = 0 dòng.
6. Điền "Báo cáo thực thi" plan này (bản trong worktree).

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 error 0 warning; test 1459 + 61 xanh.
- [ ] Màn Workspace hiện view thật, đủ các tab con/lưới/log theo bản cũ; ScrapeStatsWindow mở-đóng đúng.
- [ ] Binding log 0 dòng khi duyệt hết các tab con Workspace.
- [ ] Không sót `data-dir.txt`/file tạm/process con.

## 5. Rủi ro & lưu ý

- Đây là view NẶNG nhất — nếu quá nửa thời gian mà chưa xong phần port XAML, ưu tiên HOÀN CHỈNH từng tab con
  (build được, chạy được) thay vì dịch dở cả file; tab nào chưa xong để placeholder cục bộ + ghi rõ báo cáo.
- DataGrid trong Workspace có style/row template riêng ở Theme cũ — dịch theo nghĩa sang WPF (Trigger/
  RowStyle), đừng bịa part Avalonia (`BackgroundRectangle` không tồn tại ở WPF).
- Brush theo dòng từ VM đã Freeze sẵn — bind thẳng, không tạo brush trong converter mới.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày:** 2026-07-31 · **Người thực thi:** Opus (`opus-executor`) · **Nơi làm:** worktree
`d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`) · **CHƯA commit** (theo plan).

### 1. Kết quả kiểm chứng (số liệu thật)

| Hạng mục | Lệnh | Kết quả |
|---|---|---|
| Build | `dotnet build ShopeeSuite.sln --no-incremental` | **Build succeeded — 0 Warning(s), 0 Error(s)** |
| Test core | `dotnet test suite/Shopee.Core.Tests` | **Passed 61 / Failed 0 / Skipped 0** |
| Test orders | `dotnet test orders/XuLyDonShopee.Tests` | **Passed 1459 / Failed 0 / Skipped 0** |
| Lỗi binding runtime | `SHOPEESUITE_BINDING_LOG` + rig 9 bước (mở app → chọn dòng shop → context menu → ScrapeStatsWindow → 4 tab con → đổi tài khoản) | **0 dòng** sau TỪNG bước (đo lại sau mỗi thao tác, không chỉ cuối) |
| Đóng app | gửi WM_CLOSE cho cửa sổ chính | **ExitCode 0**, không sót process con |
| Dọn dẹp | `data-dir.txt`, thư mục data tạm, process | đã xoá hết; chỉ còn ShopeeSuite **production** (PID 33732) + 8 Brave của nó — KHÔNG bị đụng |

### 2. File đã tạo / sửa / xoá

| File | Việc |
|---|---|
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml` | **TẠO** — port 924 dòng .axaml (5 tab con, 3 DataGrid, 2 ô log, 13 style cục bộ) |
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml.cs` | **TẠO** — `ShopGrid_SelectionChanged` + `OnInstanceClick` (thay `PointerReleased`) |
| `suite/Shopee.Suite/Modules/Scrape/ScrapeStatsWindow.xaml` (+`.xaml.cs`) | **TẠO** — cửa sổ thật, giữ nguyên 2 hàm dựng + `FitOnOpen()` như bản cũ |
| `suite/Shopee.Suite/Modules/Scrape/ScrapeStatsWindow.cs` | **XOÁ** — placeholder `PortingWindow` của đợt 1 |
| `suite/Shopee.Suite/Infrastructure/VisualTreeSearch.cs` | **TẠO** — `GetParent` + `FindAncestor<T>` (thay `FindAncestorOfType<T>` của Avalonia), dùng chung |
| `suite/Shopee.Suite/Modules/Data/DataView.xaml.cs` | **SỬA 1 dòng** — vòng leo cây gọi `VisualTreeSearch.GetParent` (đúng gợi ý bước 2 của plan: đưa helper chung vào `Infrastructure/` thay vì chép) |
| `suite/Shopee.Suite/App.xaml` | **SỬA** — `WorkspaceViewModel` → `ws:WorkspaceView` (bỏ placeholder ComingSoon) |

`Themes/Theme.xaml` **KHÔNG phải sửa**: mọi selector Workspace ở bản Avalonia đều nằm trong `UserControl.Styles`
(cục bộ), đã đối chiếu `git show d6bb696:suite/Shopee.Suite/Themes/Theme.axaml` — các style dùng chung mà view
này cần (`card`, `pill*`, `mono`, `log`, `acctList`, `primary/danger/success`, `btnLabel`, DataGrid nền tảng)
đã có đủ từ đợt 1–2; không còn marker `<!-- đợt N -->` nào chờ.

### 3. Ba lỗi RUNTIME phải sửa trong lúc port (build KHÔNG bắt được)

1. **`Run.Text` bind HAI CHIỀU mặc định → crash chết app.** Lượt chạy đầu: app không hiện cửa sổ, `stderr` báo
   *Stack overflow*. Truy `%TEMP%\shopeesuite-crash.log`: gốc là
   `XamlParseException: A TwoWay or OneWayToSource binding cannot work on the read-only property 'DisplayName'`.
   `Run.TextProperty` khai `BindsTwoWayByDefault` → bind vào property chỉ-đọc (`DisplayName`, `Accounts.Count`,
   các property của record `ResumePendingRow`) là **ném lúc dựng cây**, không phải chỉ ghi log. Lỗi này còn bị
   khuếch đại thành stack overflow vì `App.HandleUiCallbackException` mở `MessageDialog`, dialog lại ném tiếp →
   đệ quy vô hạn. **Đã sửa:** ghi rõ `Mode=OneWay` cho cả 7 `<Run>` bind trong file + để lại comment cảnh báo.
   *(Lưu ý cho đợt sau: `DataView.xaml` đợt 2 cũng có 5 `<Run Text="{Binding SelectedCount}"/>` KHÔNG OneWay —
   hiện không lỗi vì `SelectedCount` là `[ObservableProperty]` có setter, nhưng là bẫy chờ nổ. Tôi KHÔNG sửa vì
   ngoài phạm vi đợt 3; đề nghị Fable cho dọn ở đợt 6.)*
2. **Nội dung tab con bị co lại + nằm giữa.** `TabControl.UpdateSelectedContent()` của WPF **chép**
   `Horizontal/VerticalContentAlignment` của TabItem đang chọn sang `PART_SelectedContentHost`. Style tab tôi đặt
   `VerticalContentAlignment=Center` (để canh chữ trên ô tab) → lưới process + ô log + `DataView` nhúng co bằng
   chiều cao mong muốn rồi trôi ra giữa màn. **Đã sửa:** TabItem để `Stretch` cả hai, chữ trên ô tab canh giữa
   bằng `ContentPresenter` trong template (đặt Alignment tường minh). Kèm ghi chú trong XAML.
3. **`MC3088` lúc biên dịch XAML:** `<Style.Resources>` không được nằm chen giữa các `<Setter>` (Setters là
   content của Style). Đã dời `Style.Resources` của `wsAction` lên đầu style.

### 4. Quy ước dịch đã dùng (ngoài bảng chung ở đầu Theme.xaml)

| Avalonia | WPF (bản port này) |
|---|---|
| `Style Selector="TabControl.subtabs TabItem …"` (selector con) | style riêng `subtabItem` gắn qua `ItemContainerStyle` của `subtabs` |
| `DataGrid.shopGrid DataGridColumnHeader / DataGridCell` | `shopGridHeader` / `shopGridCell` gắn qua `ColumnHeaderStyle` / `CellStyle` |
| `Classes.running="{Binding XxxRunning}"` (4 cột, 4 property khác nhau) | **1 style** `wsAction`; chỗ dùng đẩy cờ vào `Tag`, style đọc ngược `{Binding Tag, RelativeSource=Self}` |
| `TabItem:selected Border.tabBadge` | DataTrigger leo tổ tiên `{Binding IsSelected, RelativeSource=AncestorType TabItem}` |
| `ListBoxItem:selected Ellipse.acctDot` / `Border.wsAcct` | DataTrigger leo `ListBoxItem` / gộp thẻ vào template `wsAcctItem` |
| `Classes="pill" + Classes.success/.warning` | `pillCookie` = `pill` + 2 DataTrigger theo `HasCookie` |
| `$parent[UserControl].DataContext.XxxCommand` | `{Binding DataContext.XxxCommand, RelativeSource={RelativeSource AncestorType={x:Type UserControl}}}` |
| `#Root.DataContext.…` | `{Binding DataContext.…, ElementName=Root}` |
| `PointerReleased` + `FindAncestorOfType<DataGridRow>` | `AddHandler(PreviewMouseLeftButtonUpEvent, …, handledEventsToo: true)` + `VisualTreeSearch.FindAncestor` |
| `ZIndex` / `Spacing` / `ToolTip.Tip` / `LetterSpacing` | `Panel.ZIndex` / `Margin` / `ToolTip` / bỏ |

Nút op "đang chạy" (viền cam + icon cam) **đã kiểm chứng bằng thí nghiệm**: tạm bind `Tag` sang một cờ luôn
`true` (`ScrapeToggleEnabled`), chạy app → nút SCRAPE đổi viền + icon sang cam đúng như spec, 3 nút còn lại giữ
xám (ảnh `crop-ws-1-tab-shop-probe.png`); sau đó **đã hoàn nguyên** về `ScrapeRunning`. Cần thí nghiệm này vì
`DataTrigger Value="True"` so với giá trị `bool` qua property `Tag` (kiểu `object`) là điểm dễ hỏng ngầm, mà
trạng thái "đang chạy" thật thì không dựng được nếu không có Hub + job sống.

### 5. Nghiệm thu bằng mắt (rig UIAutomation)

Script: `…\86f7fb17-…\scratchpad\verify-dot3.ps1` (viết mới, kế thừa `verify-modals2.ps1` của đợt 2). Chạy bản
dev với `data-dir.txt` trỏ thư mục tạm, `--mode workspace`, hub trỏ cổng chết `127.0.0.1:59999`; **seed dữ liệu
giả** vào data-dir tạm (2 tk BigSeller — 1 có cookie / 1 chưa, 4 shop, 1 tiến độ scrape `stopped` + 1
`completed`) để lộ được đủ trạng thái UI. KHÔNG bấm nút chạy scrape/import/update, KHÔNG phóng Brave.

| Ảnh (scratchpad `…86f7fb17-…`) | Nội dung đã soi |
|---|---|
| `ws-1-tab-shop-final.png` | Tab "Shop & cấu hình": dải header + hint, **banner việc dở** (pill op · tk · shop · "đã cào 145/500 dòng" · giờ + 2 nút), list tk trái (chấm, `✓ cookie` xanh / `⚠ chưa đăng nhập` vàng, "1/3 shop đã scrape"), khay segmented + **huy hiệu số shop "3"** + tên tk mép phải, thẻ tk (pill xanh, Workbook, lưới 4 cột op, CẤU HÌNH CHẠY 6 ô + 2 ô đường dẫn) |
| `ws-2-shop-selected-final.png` | Chọn dòng shop qua UIA → `ShopGrid_SelectionChanged` → `PickShopCommand` chạy, không lỗi |
| `ws-9/10-ctxmenu-final.png` | Chuột phải nút IMPORT: ContextMenu "Xoá tiến độ import (chạy lại từ đầu)…" + icon, mở được **menu con** "Xác nhận…" → DataContext kế thừa vào popup OK (0 dòng binding log) |
| `ws-3-scrapestats-final.png` | **ScrapeStatsWindow**: tiêu đề + tóm tắt, 2 thẻ sheet (trạng thái/dòng/khoảng mono/khung tk) + nút đỏ "Xoá tiến độ", chân "Làm mới"/"Đóng"; đóng sạch, còn đúng 1 cửa sổ |
| `ws-4-tab-Thngk-final.png` | Tab "Thống kê": 4 thẻ KPI + card từng shop × 4 ô op (pill trạng thái, số dòng, khoảng, máy) |
| `ws-5-tab-Dliu-final.png` | Tab "Dữ liệu": `DataView` nhúng, **combo tài khoản ẩn đúng** (fixed-acct), lọc/phân trang/status |
| `ws-6/7-…-final.png` | 2 tab log: lưới tiến trình + ô log nền đen **lấp đầy chiều cao** (sau khi sửa lỗi 2), 2 nút "Log acc này"/"Log gộp" |
| `ws-8-acc2-final.png` | Đổi sang tk chưa đăng nhập: pill chuyển **vàng** "⚠ Chưa đăng nhập BigSeller", badge số shop → 1, workbook + tên tk mép khay tab đổi theo |

### 6. Điểm trệch plan / chưa kiểm được

1. **Sửa 1 dòng ở `DataView.xaml.cs` (file của đợt 2)** — theo đúng gợi ý bước 2 của plan (đưa helper leo cây
   vào `Infrastructure/` thay vì copy). Hành vi giữ nguyên; đã chạy lại tab Dữ liệu, không lỗi.
2. **Thêm `CanUserSortColumns="False"`** cho 3 DataGrid (bản cũ không ghi). Cột template của Avalonia vốn không
   sort được nếu thiếu `SortMemberPath`; ghi rõ để bấm header ở WPF không đổi thứ tự dòng ngoài ý muốn.
3. **`instanceRow` thêm `TargetNullValue`** = nền thẻ trắng khi `AccountBrush` null (dòng manual) — Avalonia để
   null là trong suốt; ở WPF `Background=null` cũng trong suốt nhưng khai rõ cho khỏi phụ thuộc mặc định.
4. **Chưa quan sát được bằng mắt**: badge ✓ "đã xong" (`ScrapeDone`…) và nút "Dừng việc shop này"
   (`SelectedAccount.HasRunningWork`) — cả hai chỉ bật khi có Hub sống / job thật; binding của chúng đã chạy
   (0 lỗi log) và cơ chế DataTrigger `Tag` đã kiểm bằng thí nghiệm ở mục 4. Đề nghị soi lại ở đợt 6 khi chạy
   thật có Hub.
5. **Lưới shop hơi thấp** ở cửa sổ 1500×940 (cụm "CẤU HÌNH CHẠY" chiếm phần `Auto` bên dưới) — đây là đúng cấu
   trúc `RowDefinitions="*,Auto"` của bản Avalonia, không phải lỗi port; cửa sổ cao hơn thì lưới giãn ra.
