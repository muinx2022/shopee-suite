# Plan: Port WPF — Đợt 4: SearchView + CheckAccountView/Window + FleetView (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` (20 quyết định) + mục "Báo cáo thực thi" của
> plan đợt 1/2/3 (quy ước + bẫy: TemplateBinding cho attached property trong template; `<Run Text="{Binding…}">`
> PHẢI `Mode=OneWay` khi property chỉ-đọc — bẫy làm chết app ở đợt 3; TabItem giữ ContentAlignment=Stretch;
> helper `Infrastructure/VisualTreeSearch.cs` dùng chung). Việc chạy trong WORKTREE
> `d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`); TUYỆT ĐỐI không đọc/ghi `d:\Projects\shopee-suite`.

## 1. Bối cảnh & mục tiêu

Đợt 1–3 xong (shell + Accounts/Data/BigSeller + Workspace/ScrapeStats). Đợt 4 port 3 màn còn lại của suite:

| Port sang | Nguồn Avalonia (`git show d6bb696:<path>`) | Ghi chú |
|---|---|---|
| `suite/Shopee.Suite/Modules/Search/SearchView.xaml` (+.cs) | `.../Search/SearchView.axaml` (363 dòng, code-behind 8 dòng) | TabControl 2 tab con + DataGrid + log |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountView.xaml` (+.cs) | `.../CheckAccount/CheckAccountView.axaml` (149 dòng, code-behind 21 dòng) | TabControl + DataGrid "TK OK"; code-behind lọc SelectionChanged bong bóng |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountWindow.xaml` (+.cs) | `.../CheckAccount/CheckAccountWindow.axaml` (10 dòng, code-behind 14 dòng) | Vỏ Window bọc CheckAccountView — thay placeholder C# `Modules/CheckAccount/CheckAccountWindow.cs` (GIỮ tên class/chữ ký nơi gọi) |
| `suite/Shopee.Suite/Modules/Fleet/FleetView.xaml` (+.cs) | `.../Fleet/FleetView.axaml` (198 dòng, code-behind 8 dòng) | TabControl + DataGrid đa máy + tab Log |

VM liên quan đã port từ đợt 1 (FleetViewModel có brush qua `AppBrushes`). Đợt này CHỈ view.

## 2. Phạm vi

- **Làm:** 4 file trên + code-behind; App.xaml: SearchViewModel/FleetViewModel (và CheckAccount VM nếu có
  DataTemplate) → view thật; xoá placeholder `CheckAccountWindow.cs`; bổ sung style Theme còn thiếu mà 3 màn
  này cần (đối chiếu Theme.axaml cũ, phần đánh dấu `<!-- đợt N -->`).
- **Không làm:** Settings + toàn bộ orders (đợt 5); không sửa VM trừ lỗi compile do port (ghi rõ); không commit.

## 3. Các bước thực hiện

1. Đọc trọn nguồn 4 file qua `git show d6bb696:`; đọc Theme.xaml hiện tại + báo cáo 3 đợt trước.
2. Port từng view theo QĐ 5/6/15/16; áp ngay các bài học: `<Run>` bind → `Mode=OneWay`; TabItem không đặt
   VerticalContentAlignment=Center; leo cây dùng `VisualTreeSearch`; watermark qua attached property có sẵn.
3. Build + test: 0 error 0 warning; 1459 + 61 xanh.
4. Chạy thử cách ly đúng quy ước các đợt trước (data-dir tạm — xoá sau; `--mode workspace`; KHÔNG bấm nút
   chạy check/search thật, không phóng Brave; đóng đúng PID). Search + Trạng thái (Fleet) nằm trong ribbon
   tab Workspace; CheckAccount mở từ màn "Tài khoản & Proxy" (nút "Check Acc" mở CheckAccountWindow — mở thử,
   chụp, đóng; lệnh bị gate thì ghi rõ). Seed dữ liệu tạm nếu giúp lưới có dòng (đợt 3 đã làm mẫu). Chụp từng
   màn/tab; `SHOPEESUITE_BINDING_LOG` = 0 dòng sau từng bước.
5. Điền "Báo cáo thực thi" plan này (bản trong worktree).

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 error 0 warning; test 1459 + 61 xanh.
- [ ] 3 màn + CheckAccountWindow hiện view thật đúng bố cục bản cũ; sau đợt này suite KHÔNG còn màn
      placeholder nào ngoài Cài đặt (UnifiedSettings) và màn thuộc orders.
- [ ] Binding log 0 dòng khi duyệt hết các tab con của 3 màn.
- [ ] Không sót `data-dir.txt`/file tạm/process con.

## 5. Rủi ro & lưu ý

- CheckAccountView code-behind cũ lọc `SelectionChanged` bong bóng từ DataGrid con — WPF sự kiện cũng bubble
  qua `RoutedEventArgs.OriginalSource`: giữ đúng hành vi lọc, đừng bỏ.
- FleetView bind brush trạng thái máy từ VM (đã Freeze) — bind thẳng.
- SearchView có 2 tab con: soát kỹ phần kết quả theo máy (lưới) — đối chiếu memory luồng Search nếu cần
  nhưng KHÔNG đổi nghiệp vụ.

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
| Lỗi binding runtime | `SHOPEESUITE_BINDING_LOG` + rig 11 bước (Search 3 tab → Fleet 4 tab → Tài khoản & Proxy → CheckAccountWindow 2 tab) | **0 dòng** sau TỪNG bước, ở cả 4 lượt chạy (`dot4b`, `probe`, `tall`, `fix`, `final`) |
| Regression đợt 3 | chạy lại `verify-dot3.ps1` (Workspace + ScrapeStatsWindow) trên build đợt 4 | **0 dòng** binding log, 9/9 bước OK, ExitCode 0 |
| Đóng app | WM_CLOSE cho cửa sổ chính | **ExitCode 0**, cửa sổ con đóng sạch (còn đúng 1) |
| Dọn dẹp | `data-dir.txt`, 7 thư mục data tạm, process | đã xoá hết. Chỉ còn ShopeeSuite **production** (PID 33732) — không bị đụng. Brave (8) + msedge (15) đang chạy đều có `CreationDate` TRƯỚC phiên này (01:08 và 30/07) → rig KHÔNG phóng trình duyệt nào |

### 2. File đã tạo / sửa / xoá

| File | Việc |
|---|---|
| `suite/Shopee.Suite/Modules/Search/SearchView.xaml` (+`.xaml.cs`) | **TẠO** — port 363 dòng .axaml (3 tab con, TabControl link-tab có template riêng, 4 DataGrid, ô log) |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountView.xaml` (+`.xaml.cs`) | **TẠO** — 2 tab con + lọc `SelectionChanged` bong bóng qua `e.OriginalSource` |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountWindow.xaml` (+`.xaml.cs`) | **TẠO** — vỏ Window bọc view, giữ `FitOnOpen()` |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountWindow.cs` | **XOÁ** — placeholder `PortingWindow` của đợt 1 |
| `suite/Shopee.Suite/Modules/Fleet/FleetView.xaml` (+`.xaml.cs`) | **TẠO** — port 198 dòng .axaml (4 tab con, 3 DataGrid, ItemsControl "việc gián đoạn") |
| `suite/Shopee.Suite/App.xaml` | **SỬA** — 3 DataTemplate Search/CheckAccount/Fleet → view thật |
| `suite/Shopee.Suite/Themes/Theme.xaml` | **SỬA** — style `TabItem`: (a) thêm `Horizontal/VerticalContentAlignment=Stretch`; (b) dời `Foreground`/`FontWeight`/`FontSize`/`FontFamily` từ TabItem vào `TextElement.*` trên Border trong template (xem lỗi 2) |
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml` | **SỬA** — style `subtabItem` dính CÙNG lỗi 2 (file của đợt 3, sửa lây — xem mục 6) |
| `suite/Shopee.Suite/App.xaml.cs` | **SỬA (+7 dòng)** — công tắc chẩn đoán `SHOPEESUITE_SOFTWARE_RENDER` (xem mục 6, điểm 1) |

`Themes/Icons.xaml` KHÔNG phải sửa: 13 icon 3 màn này cần (Folder/Delete/Refresh/Play/Resume/Stop/CheckAll/
Uncheck/Close/Export/Sparkle/OpenExternal/Save) đã có đủ từ đợt 1.

### 3. HAI lỗi runtime phải sửa trong lúc port (build KHÔNG bắt được)

1. **Nội dung tab con co lại + dạt góc trên-trái.** Giống bẫy đợt 3 nhưng ở style TabItem CHUNG của theme:
   `TabControl.UpdateSelectedContent()` chép `Horizontal/VerticalContentAlignment` của TabItem đang chọn sang
   `PART_SelectedContentHost`; theme mặc định WPF cho TabItem = Center (thừa kế từ TabControl) → lưới/ô log co
   bằng kích thước mong muốn. Đợt 1–3 chưa lộ vì chưa có view nào dùng TabItem của theme (Workspace có style
   `subtabItem` riêng). **Đã sửa:** thêm 2 setter `Stretch` vào style TabItem của theme.
2. **Chữ cam + IN ĐẬM lan ra TOÀN BỘ nội dung tab.** Theme (và `subtabItem` của đợt 3) đặt
   `Foreground`/`FontWeight` TRÊN CHÍNH `TabItem` để tô nhãn ô tab. Ở WPF thuộc tính thừa kế chảy theo cây
   **LOGIC**, mà `TabItem.Content` có logical parent chính là TabItem → mọi `TextBlock`/`TextBox` trong thân tab
   không tự khai màu/độ đậm đều bị tô **cam + Bold**. Bản Avalonia KHÔNG dính vì thừa kế của nó đi theo cây
   **TRỰC QUAN**, mà thân tab nằm trong ContentPresenter của TabControl chứ không nằm trong TabItem.
   Đo thật: lưới link Search, giá trị ô nhập ("0"), dòng `Machines` của Fleet đều bị đậm/cam (ảnh
   `d4-*-tall.png`). **Đã sửa:** bỏ 4 setter chữ khỏi TabItem, đặt `TextElement.FontFamily/FontSize/FontWeight/
   Foreground` trên Border trong template (chỉ ảnh hưởng header). Sửa CẢ `subtabItem` của WorkspaceView vì
   cùng gốc lỗi (ảnh `ws-8-acc2-d4reg.png`: thân tab Workspace giờ nhẹ chữ đúng như bản Avalonia).

### 4. Quy ước dịch đã dùng (bổ sung bảng chung)

| Avalonia | WPF (bản port này) |
|---|---|
| `TabControl.Template` với `ItemsPresenter` + `ContentPresenter Content={TemplateBinding SelectedContent}` | `ControlTemplate` + `ItemsPresenter` + `<ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent"/>` (tên PART bắt buộc; ContentSource tự nối cả ContentTemplate) |
| `$parent[UserControl].DataContext.CloseLinkTabCommand` trong `ItemTemplate` | `{Binding DataContext.CloseLinkTabCommand, RelativeSource={RelativeSource AncestorType={x:Type UserControl}}}` — KHÔNG dùng `ElementName=Root` (khác namescope trong DataTemplate) |
| `$parent[ItemsControl].DataContext.ResumeJobCommand` | `RelativeSource AncestorType={x:Type ItemsControl}` |
| `IsVisible="{Binding ErroredAccounts.Count, Converter=CountToBool}"` | `Visibility="{Binding …, Converter={StaticResource CountToVis}}"` |
| `IsVisible="{Binding InterruptedStatus, Converter=StringToBool}"` | `Converter={StaticResource StringToVis}` |
| `IsVisible="{Binding IsClientPanel}"` | `Converter={StaticResource BoolToVis}` |
| `<sys:Int32>1</sys:Int32>` trong ComboBox | giữ nguyên, đổi xmlns sang `clr-namespace:System;assembly=mscorlib` |
| `DataGridTextColumn FontFamily="{DynamicResource MonoFont}"` | `{StaticResource MonoFont}` (cột không nằm trong cây nhưng StaticResource phân giải lúc nạp XAML) |
| `SelectionChanged` + `e.Source` lọc bong bóng | `e.OriginalSource` (bản WPF cũ `989901c` cũng dùng đúng cách này) |

### 5. Nghiệm thu bằng mắt (rig UIAutomation)

Script `…\86f7fb17-…\scratchpad\verify-dot4.ps1` (viết mới, kế thừa `verify-dot3.ps1`). Bản dev, `data-dir.txt`
trỏ thư mục tạm, `--mode workspace`, hub trỏ cổng chết `127.0.0.1:59999`; seed file link `.txt` 4 dòng + sidecar
trạng thái + `search-ui.json` + `check-account/tk-ok.txt` + `check-settings.json`. KHÔNG bấm nút chạy
check/search, KHÔNG phóng Brave.

| Ảnh (hậu tố `-final` = build sạch, `-fix` = có seed giả) | Nội dung đã soi |
|---|---|
| `d4-1-search-tim-kiem-*.png` | Tab "Tìm kiếm": pill trạng thái "Đã nạp 4 link (3 đang chọn)", thẻ chọn file + nút xoá đỏ, ô Khu vực + Lane + 4 nút (Tìm kiếm xanh · Tiếp tục · Dừng đỏ), lưới link 5 cột (checkbox tick đúng: dòng "Processed" BỎ tick), ô log đen |
| `d4-1-search-tim-kiem-tall.png` (seed) | **Dải tab theo link**: 2 tab "Thời Trang Nam · 12 SP ✕" / "Điện Thoại & Phụ Kiện · 0 SP ✕", dòng "Tài khoản: … · … · link", lưới sản phẩm 7 cột 12 dòng; **khối "Tài khoản lỗi (2)"** hiện đúng qua CountToVis |
| `d4-2-search-xuat-excel-*.png` | Tab "Xuất Excel": 3 thẻ (bộ lọc 4 ô, thư mục mono + 2 nút, 2 nút xuất/xoá + ghi chú) |
| `d4-3-search-danh-muc-ai-*.png` | Tab "Danh mục (AI)": caption kèm provider "OpenAI", ô path mono, 4 nút, 2 lưới cạnh nhau (1.1*/14/1.6*) |
| `d4-4-fleet-theo-doi-*.png` | Fleet tab "Theo dõi": dải máy + trạng thái + checkbox "Chạy đè", lưới 6 cột (bản seed có 4 dòng) |
| `d4-5-fleet-giao-viec-fix.png` | Tab "Giao việc": panel client + nút "Tạm dừng nhận việc", lưới việc với **cột Trạng thái tô theo `StateBrush`** (xanh "đang chạy" / xanh dương "chờ tới lượt"), khối **"Việc gián đoạn (2)"** + dòng `InterruptedStatus` + 2 thẻ có nút "Tiếp tục" |
| `d4-6-fleet-search-da-may-*.png` | Tab "Search (đa máy)": đúng 1 thông báo phía client (2 khối `IsClientPanel`/`IsCoordOff` loại trừ nhau) |
| `d4-7-fleet-log-fix.png` | Tab "Log": nút "Xoá log", lưới 3 cột, **nội dung tô theo `Brush` từng dòng** (xanh/cam/đỏ/xám) |
| `d4-8-checkacc-check-fix.png` | **CheckAccountWindow** tab 1: 2 ô nhập mono nhiều dòng (có dữ liệu seed), 3 nút (Dừng mờ vì `IsRunning=false`), combo "Số luồng"=3 đọc từ settings, ô log đen |
| `d4-9-checkacc-tk-ok-fix.png` | Tab "TK OK": chuyển tab kích hoạt `OnTabChanged` → `LoadOkGrid()` nạp 3 dòng từ `tk-ok.txt`; cột tài khoản mono + cột tick |

### 6. Điểm trệch plan / còn lại

1. **Máy dev không "present" được cửa sổ WPF mới — phải thêm công tắc `SHOPEESUITE_SOFTWARE_RENDER`.**
   Lượt chụp đầu ra ảnh **trắng trơn** (cả `CopyFromScreen` lẫn `PrintWindow(PW_RENDERFULLCONTENT)`), trong khi
   UIAutomation vẫn đọc đủ 71 phần tử với BoundingRectangle đúng → layout chạy, chỉ khâu vẽ ra màn hình hỏng.
   **Đã loại trừ nguyên nhân do đợt 4**: `git stash` toàn bộ thay đổi → build HEAD (đợt 3) → vẫn TRẮNG; trong khi
   ảnh chụp toàn desktop cùng lúc cho thấy các app khác vẽ bình thường, và ảnh đợt 3 chụp lúc 04:35 thì đầy đủ.
   Minimize/restore chỉ đổi trắng → đen. Đặt `RenderOptions.ProcessRenderMode = SoftwareOnly` là khôi phục ngay.
   Tôi để lại **7 dòng** trong `App.xaml.cs`, **mặc định TẮT**, cùng họ với `SHOPEESUITE_BINDING_LOG` của đợt 1.
   → **Fable quyết:** giữ (đợt 5 port orders sẽ cần lại) hay bỏ (xoá 7 dòng là xong). Bản phát hành KHÔNG đổi
   hành vi vì biến môi trường không được đặt.
2. **Sửa `WorkspaceView.xaml` (file của đợt 3).** Style `subtabItem` dính đúng lỗi 2; để nguyên thì toàn bộ thân
   màn Workspace vẫn in đậm sai. Đã chạy lại rig đợt 3 để nghiệm thu (0 lỗi binding, 9/9 bước).
3. **Plan ghi "SearchView … TabControl 2 tab con"; thực tế nguồn có 3 tab con** (Tìm kiếm · Xuất Excel ·
   Danh mục (AI)). Đã port cả 3. Mục 5 của plan nói "soát kỹ phần kết quả theo máy (lưới)" — trong `SearchView`
   KHÔNG có lưới kết quả-theo-máy (phần đó nằm ở Hub web); phía client chỉ có tab thông báo trong `FleetView`.
4. **Thêm `CanUserSortColumns="False"`** cho 7 DataGrid mới (bản Avalonia không ghi; giữ đúng thói quen đợt 3 để
   bấm header không đổi thứ tự dòng ngoài ý muốn) và `CellStyle="{StaticResource cellTight}"` cho 2 cột checkbox.
5. **2 ô nhập nhiều dòng của CheckAccount được thêm `Vertical/HorizontalScrollBarVisibility="Auto"`** — template
   TextBox của theme hard-code `Hidden`, dán hàng trăm tài khoản mà không có thanh cuộn thì rất khó dùng. Bản WPF
   cũ `989901c` (`MonoTextBox`) cũng đặt Auto cả hai.
6. **Bố cục tab "Tìm kiếm" ép hàng `*` về 0 khi cửa sổ thấp.** `RowDefinitions="Auto,190,*,Auto,150"` — khi có
   ĐỦ cả dải tab link lẫn khối "Tài khoản lỗi", ở cửa sổ 1175px thì dải tab link không còn chỗ (WPF ép sao `*`
   về 0 trước). Đây là ĐÚNG cấu trúc bản Avalonia (không phải lỗi port); cửa sổ cao hơn là hiện đủ (ảnh
   `-tall`). Nếu người dùng thấy vướng thì nên đổi `190` → `Auto/MaxHeight` ở đợt 6, không nên sửa lén ở đợt này.
7. **Chưa quan sát được bằng mắt:** trạng thái "đang chạy thật" của Search (nút Dừng bật, tab đổi trạng thái
   liên tục) và dữ liệu Fleet từ Hub THẬT — cả hai chỉ dựng được khi có Hub sống + job thật. Đã thay bằng
   **thí nghiệm nhồi dữ liệu giả** (2 `ProbeSeed` tạm trong `SearchViewModel`/`FleetViewModel`), chụp xong
   **ĐÃ HOÀN NGUYÊN** (`git diff` của 2 file VM = rỗng, đã kiểm lại).
8. **Nit UIAutomation:** TabItem của dải tab link có `Name` = tên kiểu (`…SearchFileTab`) vì header là
   DataTemplate (StackPanel) — giống tab đầu của Workspace ở đợt 3. Không ảnh hưởng người dùng; nếu muốn trợ
   năng tốt hơn thì thêm `AutomationProperties.Name="{Binding Header}"` ở đợt 6.
9. **ComboBox vẫn dùng chrome mặc định của WPF** (nền xám gradient nhẹ) — lộ rõ ở ô "Danh mục" tab Xuất Excel và
   ô "Số luồng". Đây là khoảng trống theme từ đợt 1 ("giữ template mặc định"), không thuộc phạm vi đợt 4; đề
   nghị dựng template phẳng ở đợt 6.
10. **`Views/PortingWindow.cs` giờ 0 lớp con** (4 cửa sổ tạm của đợt 1 đã thành view thật hết). Tôi KHÔNG xoá vì
    đợt 5 có thể mượn lại làm placeholder cho 2 dialog của orders (`ConfirmDialog`, `OrderDetailDialog` đang tạm
    dùng MessageBox). Nếu đợt 5 không dùng thì xoá ở đợt 6.

### 7. Cách chạy lại rig nghiệm thu (cho đợt sau)

```powershell
$env:SHOPEESUITE_SOFTWARE_RENDER='1'      # bắt buộc trên máy này, nếu không ảnh chụp TRẮNG TRƠN
& '<scratchpad>\verify-dot4.ps1' -Tag <hậu-tố> [-Tall]
```
`-Tall` kéo cửa sổ cao 2080px (cần để dải tab theo link của Search có chỗ hiện). Script tự tạo/xoá `data-dir.txt`,
tự seed dữ liệu, tự đóng đúng PID nó mở và in số dòng binding log sau TỪNG bước.
