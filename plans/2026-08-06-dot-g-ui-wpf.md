# Plan: Đợt G — Cải thiện UI 2 app WPF (suite + Đơn hàng)

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành (code + nghiệm thu tĩnh-động; còn 6 điểm duyệt thị giác — xem cuối file)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Các điểm UI WPF từ đợt rà soát 05/08, bám định hướng UI phẳng Win11 đã chốt (bo 4/6, không gradient/bóng, header tối đặc, active kiểu Win11 nhẹ; 2 bộ token riêng suite vs Đơn hàng). Toàn bộ là XAML + ít code-behind/VM; không đổi nghiệp vụ.

## 2. Phạm vi

- **Làm:** 9 mục phần 3 (suite: G1–G5, orders app: G6–G9).
- **Không làm:** không đổi hành vi nghiệp vụ; không đụng server; không thêm màn mới (đợt H); không đổi 2 bộ token màu.

## 3. Các bước thực hiện

### Suite (`suite/Shopee.Suite`)

**G1. Style chip trạng thái header dùng chung.** 4 màn (SearchView ~19–29, BigSellerView ~15–25, CheckAccountView ~17–27, AccountsView ~13–32) copy khối header chip CHỒNG LỚP (Grid 1 ô, chip đè cùng ô tiêu đề → status dài/cửa sổ hẹp là chồng chữ). Màn Cài đặt đã sửa đúng mẫu (UnifiedSettingsView ~37–55: 2 cột + StringToVis + MaxWidth 420). Rút mẫu đó thành style/ControlTemplate dùng chung trong Theme.xaml (vd key `headerStatusChip` + layout 2 cột chuẩn) rồi áp cho cả 4 màn (AccountsView có thêm nút Check Acc trong header — giữ nút, vẫn theo layout 2 cột). Workspace header hiển thị Status chữ trần (~:257) — đổi sang cùng chip cho nhất quán nếu không phá layout, không ép được thì ghi lại.

**G2. Chuẩn hóa bo góc về 4/6.** Hiện rải 3–8: nút 5 (Theme.xaml ~:186), popup ComboBox 8 (~:265), khung subtabs 7 (~:932), khung lưới shop 7 (WorkspaceView ~:504), pill op 3 (WorkspaceView ~:328), chip 4, thẻ 6. Quy ước: control nhỏ (nút, input, chip, pill) = 4; panel/khung/popup/thẻ = 6. Sửa toàn bộ CornerRadius trong Theme.xaml + các view suite về 2 nấc này (grep `CornerRadius` toàn suite/Shopee.Suite).

**G3. Nút "Dừng việc shop này" thôi overlay.** WorkspaceView (~:70–72): nút đang đè góc trên-phải ngoài TabControl, khay tab chừa lề phải cứng 210px. Đưa nút vào cùng hàng với khay subtab (DockPanel/Grid cột Auto bên phải), bỏ margin cứng — tự co giãn.

**G4. Tham số hóa 4 cột op lưới shop.** WorkspaceView ~:527/551/588/624 — 4 khối DataGridTemplateColumn ~121 dòng gần copy nguyên xi (Grid > Button wsAction + doneBadge + PathIcon). Gộp về 1 DataTemplate dùng chung + attached property/Tag chứa (op, icon, tooltip, command); GIỮ khác biệt có chủ đích: 2 cột IMPORT/UPDATE có ContextMenu xóa tiến độ resume mà SCRAPE/TÊN SP không có.

**G5. Chữ gợi ý phím tắt theo số tab thật.** MainWindow (~:82) ghi cứng "Ctrl + 1…4" trong khi chế độ Shopee chỉ 2 tab, Workspace 3. Bind theo `Tabs.Count` (vd "Ctrl + 1…{n}") hoặc đổi chữ chung chung.

### App Đơn hàng (`orders/XuLyDonShopee.App`)

**G6. Bảng đơn: ghim cột + ẩn cột phụ.** OrdersView (~:196–253, 15 cột ~1600px): đặt `FrozenColumnCount="2"` (ghim Shop + Mã đơn — kiểm tra thứ tự cột thực tế, ghim 2 cột đầu dùng đối chiếu); thêm ContextMenu trên header cho ẩn/hiện các cột phụ ít dùng (Ước tính, Thanh toán, …) — lưu lựa chọn vào settings local của app (SettingsRepository nếu có khuôn, không thì session-only, ghi rõ chọn gì).

**G7. Nút đóng banner địa chỉ dễ bấm, hết trùng ngôn ngữ.** AccountsView (~:694–700): X đỏ 11px trùng với X đỏ "shop lỗi" ở cột tiến độ ngay dưới (~:722–726). Đổi thành nút chữ "Đóng" (kèm ✕ nhỏ), vùng bấm ≥ 28px, style theo token. GIỮ nguyên hành vi đóng (luật rev/tombstone — CHỈ đổi hình thức nút, không đụng logic).

**G8. Chip preset ngày ở màn Thống kê.** OrderStatisticsView (~:79–94, 2 DatePicker Từ/Đến): thêm hàng chip "Hôm nay · 7 ngày · Tháng này" (kiểu segmented Win11, tái dùng style subtab/tabBadge của theme orders) — bấm chip là set 2 DatePicker tương ứng (theo giờ VN) và refresh.

**G9. Vặt:** (a) AccountsView cột trái Width=360 đo tay từng dính cắt chữ (~:248–251) → MinWidth + Auto cho hàng nút; (b) badge "TK chưa xác nhận" hardcode `#FDECEA/#F5C6C0` (~:361–366) → token họ WarnSoft*/Danger* trong Colors.xaml; (c) hợp nhất style trùng tên lệch định nghĩa phía suite: 2 bản `fieldLabel` (WorkspaceView ~:218–224 vs RowEditWindow ~:18–20) + `sectionLabel` (UnifiedSettingsView ~:22–27) chồng vai `section` (Theme ~:156–160) → 1 bản trong Theme, override margin tại chỗ.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0 error 0 warning (XAML compile qua BAML là lưới bắt lỗi cú pháp chính).
- [ ] 3 bộ test xanh (test orders có test dựng control WPF thật — phải xanh).
- [ ] Grep `CornerRadius` suite: chỉ còn giá trị 4 hoặc 6 (liệt kê ngoại lệ có lý do nếu giữ).
- [ ] G4: 4 cột op còn 1 template; diff chứng minh ContextMenu Import/Update được giữ.
- [ ] App khởi động được: chạy `dotnet run`/exe build của Shopee.Suite ở chế độ hiện tại, cửa sổ mở không crash, mở lần lượt các màn đã sửa (Search/BigSeller/CheckAccount/Accounts/Workspace/Đơn hàng/Thống kê). Nếu môi trường không cho tương tác UI thì tối thiểu app phải khởi động + không có binding error trong Output (bật `PresentationTraceSources`/soi stderr), ghi rõ đã kiểm tới đâu.
- [ ] Báo cáo kèm ảnh chụp màn hình từng màn đã sửa nếu chụp được (memory `verify-wpf-ui-tren-may-nay`: dùng UIA, chú ý DPI); không chụp được thì ghi rõ.

## 5. Rủi ro & lưu ý

- Binding error WPF chết câm — sau khi sửa XAML phải soi trace binding lúc app chạy, đừng chỉ tin build xanh.
- G6 FrozenColumnCount đổi hành vi cuộn ngang — kiểm tra không phá cột template (nút/hyperlink) ở 2 cột bị ghim.
- G1/G9c đụng ResourceDictionary scope: style cùng key ở scope khác nhau resolve khác — sau hợp nhất phải mở TỪNG màn dùng style đó.
- App đang được user dùng làm việc thật — KHÔNG để app tự khởi động chiếm foreground lâu; mở kiểm tra rồi đóng.
- KHÔNG commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày làm:** 2026-08-06 · **Người thực thi:** `opus-executor` · **KHÔNG commit** (theo yêu cầu).

### Đã làm — từng mục

**G1. Chip trạng thái header dùng chung.** Thêm style `headerStatusChip` (ContentControl + ControlTemplate) vào
`suite/Shopee.Suite/Themes/Theme.xaml`; chip TỰ ẨN khi Status rỗng/null (2 Trigger trên `Content`) nên chỗ dùng
không cần converter. Áp cho **6 màn**: SearchView · BigSellerView · CheckAccountView · AccountsView (giữ nút
"Check Acc", nút + chip cùng cột `Auto`) · UnifiedSettingsView (nguồn của mẫu, nay dùng chung style) ·
WorkspaceView (trước là **chữ trần** ở header — đã đổi sang chip, layout không phá). 4 màn cũ đổi từ header
CHỒNG LỚP sang Grid 2 cột (`*` + `Auto`, khối tiêu đề chừa lề phải 16).

**G2. Bo góc về 4/6.** Quy ước ghi ngay trong Theme.xaml. Đã sửa: nút (5→4), nút brand/ribbon (5→4), TextBox
(5→4), PasswordBox (5→4), khung ComboBox (5→4), `card` (8→6), `pill` (20→4), ô log (5→**6**, là mảng lớn =
panel), tab topnav (5,5,0,0→4,4,0,0), `subtabItem` (7→4), `subtabTray` (10→6), app-mark MainWindow (5→4),
`tabBadge` (9→4), nút `wsAction` (5→4), pill op trong banner resume (3→4), khung lưới shop (7→6), 2 hộp
thông tin nền `SubtleBrush` ở Fleet/Cài đặt (4→6, là panel).

**G3. Nút "Dừng việc shop này" thôi overlay.** Đưa vào cột thứ 3 (`Auto`) của hàng khay sub-tab trong template
`subtabsWorkspace`; bỏ lề phải cứng `210` (`Margin="18,12,210,10"` → `18,12,18,10`). Nút cũ (lớp đè
`HorizontalAlignment=Right/VerticalAlignment=Top`) đã xoá. Binding + tooltip + `FallbackValue=Collapsed` giữ
nguyên (DataContext của TabControl = WorkspaceViewModel).

**G4. Tham số hoá 4 cột op.** Thân ô (khung nút + icon + huy hiệu ✓) gộp vào **một** ControlTemplate trong style
`wsAction`; thêm attached property `Shopee.Suite.Behaviors.OpBadgeAssist` (`Done`, `Tip`) để truyền phần riêng.
Style `doneBadge` đã xoá (0 nơi dùng). **ContextMenu "xoá tiến độ" của IMPORT/UPDATE giữ nguyên** — xem mục
kiểm chứng.

**G5. Chữ gợi ý phím tắt.** `ShellViewModel.TabShortcutHint` (`Ctrl + 1…{Tabs.Count}`; 1 tab → "Ctrl + 1");
MainWindow bind thay chuỗi cứng "Ctrl + 1…4".

**G6. Bảng đơn: ghim cột + ẩn cột phụ.** `FrozenColumnCount="2"` (Shop + Mã đơn — đã đối chiếu thứ tự cột thực
tế, cả hai là `DataGridTextColumn`). ContextMenu ẩn/hiện 6 cột phụ (Phân loại · Đơn trả hàng · Ước tính · Thanh
toán · ĐVVC · Sync lúc). Lưu **SettingsRepository** key mới `orders_hidden_columns`
(`GetHiddenOrderColumns`/`SetHiddenOrderColumns`), VM `OrdersViewModel` có 6 property `ShowCol*` ghi ngay khi
tick; code-behind `OrdersView` đồng bộ `DataGridColumn.Visibility` (cột không nằm trong cây trực quan nên không
bind thẳng được).

**G7. Nút đóng banner địa chỉ.** `ghostIcon` ✕ đỏ 11px → nút chữ **"Đóng"** kèm ✕ 9px **xám** (style mới
`alertClose`, cao 28). Command + CommandParameter + luật rev/tombstone KHÔNG đụng tới.

**G8. Chip preset ngày màn Thống kê.** Hàng chip segmented "Hôm nay · 7 ngày · Tháng này" (style `dateChip` +
`dateChipTray`) trên 2 ô ngày. VM thêm `ApplyDatePresetCommand` + `DatePreset` + 3 cờ `IsPreset*`; đặt cả 2 mốc
rồi **vẽ lại đúng một lượt** (cờ `_dangDatPreset`), tự chọn ngày trên lịch thì nhả hết chip.

**G9.** (a) Cột trái AccountsView `Width="360"` → `Width="Auto" MinWidth="300"`, hàng nút đổi sang 4 cột
`Auto/Auto/*/Auto` (nhãn không bao giờ bị cắt), kẹp `MaxWidth="320"` cho ô tìm kiếm + danh sách (2 thành phần
bề rộng vô hạn) để cột không phình. (b) `#FDECEA/#F5C6C0` → token mới `DangerSoftBg`/`DangerSoftBorder` trong
`Styles/Colors.xaml` (GIỮ NGUYÊN giá trị màu). (c) Hợp nhất style trùng tên: `fieldLabel` (2 bản: WorkspaceView
+ RowEditWindow) → 1 bản trong Theme.xaml; `sectionLabel` (UnifiedSettingsView) xoá, 2 chỗ dùng chuyển sang
`section` của Theme + override Margin tại chỗ.

### File đã tạo/sửa

Tạo: `suite/Shopee.Suite/Behaviors/OpBadgeAssist.cs` · `orders/XuLyDonShopee.Tests/OrdersColumnVisibilityTests.cs`
· `orders/XuLyDonShopee.Tests/OrderStatisticsDatePresetTests.cs`.

Sửa (suite): `Themes/Theme.xaml` · `MainWindow.xaml` · `ViewModels/ShellViewModel.cs` ·
`Modules/{Search/SearchView, BigSeller/BigSellerView, CheckAccount/CheckAccountView, Accounts/AccountsView,
Settings/UnifiedSettingsView, Workspace/WorkspaceView, Fleet/FleetView, Data/RowEditWindow}.xaml`.

Sửa (orders): `XuLyDonShopee.Core/Data/SettingsRepository.cs` · `App/Styles/Colors.xaml` ·
`App/ViewModels/{OrdersViewModel, OrderStatisticsViewModel}.cs` ·
`App/Views/{OrdersView.xaml, OrdersView.xaml.cs, AccountsView.xaml, OrderStatisticsView.xaml}`.

KHÔNG đụng `server/` (đợt F đang chạy song song), không `git add/rm/restore`, không commit.

### Kết quả kiểm chứng (lệnh thật + kết quả thật)

1. `dotnet build ShopeeSuite.sln --no-incremental` → **Build succeeded. 0 Warning(s), 0 Error(s)** (13,55s).
   XAML của cả 2 app compile qua BAML nên mọi key StaticResource/attached property sai đều đã lộ ở đây.
2. `dotnet test orders/XuLyDonShopee.Tests` → **Passed! Failed: 0, Passed: 1506** (lượt chạy đầu có **1 flake**
   `NotifyDonTraKhoMaTests.BadgeChoDay_DemCaMaTraHangConTon` — test gọi HTTP tới `127.0.0.1:9`; chạy lại riêng
   lớp đó **10/10 xanh**, chạy lại full suite **1506/1506 xanh**. Không liên quan diff đợt G: không đụng
   outbox/return-codes/gsheet).
   `dotnet test suite/Shopee.Core.Tests` → **Passed! Failed: 0, Passed: 76**.
3. Grep `CornerRadius` toàn `suite/Shopee.Suite` (36 chỗ): chỉ còn **4** hoặc **6**, trừ **3 ngoại lệ có lý do
   đã ghi comment tại chỗ**:
   - `Themes/Theme.xaml:896` `2,2,0,0` — gạch chỉ báo tab cao **2px** (bo 4 > nửa chiều cao → méo);
   - `MainWindow.xaml:72` `2,2,0,0` — gạch chỉ báo tab cao **3px** (cùng lý do);
   - `WorkspaceView.xaml:202` `7` — huy hiệu ✓ **14×14, bán kính = nửa cạnh = hình TRÒN**.
4. Grep key style:
   - `headerStatusChip`: 1 định nghĩa (Theme) ↔ 6 nơi dùng. `fieldLabel`: 1 định nghĩa (Theme) ↔ 27 nơi dùng
     (RowEditWindow + WorkspaceView). `section`: 1 định nghĩa ↔ 2 nơi dùng. `wsAction`: 1 ↔ 4. `subtabTray`: 1 ↔ 2.
   - Key đã xoá: `doneBadge` → **0 nơi dùng**; `sectionLabel` → **0 nơi dùng**.
   - Orders: `alertClose` 1↔1, `dateChip` 1↔3, `dateChipTray` 1↔1, `DangerSoftBg`/`DangerSoftBorder` 1↔1;
     `ghostIcon` vẫn còn 3 nơi dùng khác (không bị mồ côi). Hex `#FDECEA/#F5C6C0` chỉ còn trong Colors.xaml.
5. **G4 — bằng chứng ContextMenu được giữ**: sau khi sửa, `WorkspaceView.xaml` vẫn có **2** khối `<ContextMenu>`
   và 2 dòng `Command="{Binding ResetImportProgressCommand}"` (dòng 609) / `ResetUpdateProgressCommand`
   (dòng 641) — nằm trong `<Button.ContextMenu>` của đúng cột IMPORT và UPDATE; `git diff` cho thấy 2 khối này
   chỉ bị **thụt lề lại** (xoá 4 mức indent), nội dung không đổi. Mức gộp: markup huy hiệu ✓ từ **4 bản chép**
   còn **1** (`grep -c 'Text="✓"'`: 4 → 1), 4 `<Grid HorizontalAlignment="Center">` bọc ngoài biến mất, khối
   4 cột op 120 → 104 dòng (phần còn lại chủ yếu là 2 ContextMenu buộc phải giữ + comment mới).
6. Test mới (11 ca, đều xanh) và **đã thử phá để chắc nó canh thật** — 3 đột biến, cả 3 đều bị bắt:
   - `AddDays(-6)` → `AddDays(-7)` ⇒ `Chip7Ngay_TinhCaHomNay_Nen_Lui6Ngay` FAIL;
   - bỏ `SetHiddenOrderColumns` ⇒ `TatCot_LuuVaoSettings_VaBanVmMoiVanNho` FAIL;
   - bỏ guard `if (_dangDatPreset) return;` ⇒ `DoiChip_KhongLotQuaKhoangNgayKhongHopLe` FAIL.
   (Lượt đầu ca thứ 3 KHÔNG bắt được vì kịch bản test chọn sai — đã sửa test cho đúng ca vỡ thật: người dùng
   kéo "Đến ngày" về quá khứ rồi bấm chip "Hôm nay". Mã nguồn đã khôi phục nguyên vẹn sau khi thử.)

### Vướng mắc / khác plan (cần kiến trúc sư duyệt)

1. **KHÔNG chạy app thật** — theo chỉ thị cập nhật khi giao việc (app sẽ heartbeat vào Hub production và có thể
   giành lease tài khoản thật). Vì vậy **chưa có ảnh chụp màn hình** và **chưa soi binding error lúc chạy**;
   phần duyệt thị giác để phiên chính thu xếp với user. Lưới bắt lỗi thay thế: build BAML 0 warning + grep đối
   chiếu từng key ở mục 4.
2. **G4 không phải "1 DataTemplate"** mà là **1 ControlTemplate** (trong style `wsAction`) + attached property.
   Lý do: một DataTemplate dùng chung cho cả 4 cột đòi mỗi cột trỏ vào một *đối tượng op* riêng trên
   `WorkspaceShopViewModel` (XAML không có binding path động), tức phải tách 4 nhóm property
   `ScrapeRunning/ToggleTip/ToggleEnabled/Done`… thành 4 VM con và làm lại chuỗi thông báo trong
   `RefreshFleet()` — rủi ro làm chết cập nhật trạng thái live (nút không đổi cam khi đang chạy), vượt phạm vi
   "không đổi nghiệp vụ". Kết quả vẫn đạt tinh thần plan: một template duy nhất, tham số qua Tag/attached property.
3. **G4 — một khác biệt hành vi nhỏ, cố ý**: khi nút bị disable, trước đây mờ CẢ nút (huy hiệu ✓ nằm ngoài nút
   nên không mờ); nay template chỉ mờ **khung nút**, huy hiệu ✓ giữ nguyên độ đậm. Giữ đúng ý nghĩa cũ ("đã
   xong" không phụ thuộc máy này có bấm được hay không).
4. **G6 — ContextMenu gắn ở LƯỚI, không gắn riêng đầu cột.** Bấm chuột phải lên đầu cột vẫn ra đúng menu (WPF
   leo tổ tiên tìm ContextMenu gần nhất), nhưng bấm chuột phải lên **một dòng đơn cũng ra menu này** (trước đó
   chuột phải không làm gì). Lý do: `DataGridColumnHeader` do ItemContainerGenerator sinh ra nên DataContext của
   nó **có thể là chính `DataGridColumn`** chứ không phải VM màn → bind qua `PlacementTarget` của lưới là đường
   duy nhất chắc chắn đúng. Nếu kiến trúc sư muốn menu CHỈ ở đầu cột thì phải xác minh DataContext của header
   trên app thật trước.
5. **G8 — "theo giờ VN"**: chip dùng `DateTime.Today` (đồng hồ MÁY) chứ không ép múi giờ VN, vì
   `TryBuildCreatedRange` quy đổi mốc ngày sang UTC bằng `TimeZoneInfo.Local`; ép múi giờ riêng cho chip sẽ
   lệch 1 ngày so với chính bộ lọc nó vừa đặt. Các máy chạy app đều để giờ VN.
6. **G9(a)** vẫn còn 2 con số đo tay (`MaxWidth=320` cho ô tìm kiếm + danh sách). Không bỏ hẳn được: cột `Auto`
   đo con với bề ngang vô hạn nên hai thành phần này sẽ đòi bề rộng của chuỗi dài nhất. Cái ĐÃ hết là ràng buộc
   nguy hiểm "bề rộng cột phải đủ cho 3 nút" — nhãn nút giờ không bao giờ bị cắt, và cột tự nới nếu nhãn dài ra.
   Hệ quả: cột trái ≈ 360–364px thay vì đúng 360.
7. **Test mới là phần mở rộng ngoài plan** (plan không yêu cầu viết test). Thêm 11 ca vì tiêu chí "app khởi
   động được" đã bị gỡ — cần thứ khác kiểm chứng logic VM mới của G6/G8.

### Đề xuất

- Khi phiên chính mở app duyệt thị giác, ưu tiên soi 4 chỗ rủi ro nhất: (a) huy hiệu ✓ trên 4 cột op còn hiện
  đúng và không bị cắt; (b) nút "Dừng việc shop này" nằm thẳng hàng khay sub-tab, không đè tên tài khoản;
  (c) menu chuột phải ở màn Đơn hàng tick/bỏ tick đúng cột và nhớ sau khi mở lại app; (d) `pill` bo 4 (trước là
  viên thuốc 20) — đây là thay đổi diện mạo rõ nhất của G2, nếu user không thích thì đảo lại chỉ 1 dòng.
- `pill` bo 20 → 4 là đúng chữ trong plan ("chip, pill = 4") nhưng khác hẳn về cảm giác thị giác; nên hỏi user
  một câu trước khi phát hành.

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **ĐẠT CÓ ĐIỀU KIỆN**. Nó không chấp nhận "build BAML là đủ" mà tự dựng HARNESS kiểm
tĩnh-động trong scratchpad (`xamlprobe/`, ProjectReference tới Shopee.Suite): nạp App.xaml (Theme/Icons) rồi
DỰNG + Measure/Arrange 12 view thật, không mở cửa sổ, không dựng ShellViewModel ⇒ không heartbeat Hub, không
giành lease. Nhờ đó verify được thứ grep không thấy: LoadContent 4 CellTemplate (4/4 wsAction, đúng 2
ContextMenu), chip tự ẩn theo Content, hàng khay tab 3 cột đã hết lề cứng 210, menu ẩn cột đọc/ghi settings
thật, chip ngày đảo Tag đúng.

**ĐÍNH CHÍNH quan trọng cho các đợt sau**: câu "XAML compile qua BAML nên key StaticResource sai đã lộ" trong
báo cáo thực thi là **SAI** — StaticResource phân giải LÚC CHẠY, BAML không kiểm key. Đừng dựa vào lý lẽ đó.

Lỗi đã sửa ngay (phiên chính):
1. **RÒ RỈ VIEW (thật, mức trung bình)**: `OrdersView` chỉ gỡ `vm.PropertyChanged` trong DataContextChanged,
   mà VM sống suốt đời app còn view bị DataTemplate dựng MỚI mỗi lượt vào màn (DataContext không bao giờ đổi)
   ⇒ mỗi lượt điều hướng rò 1 view + DataGrid 15 cột. Sửa: đăng ký ở `Loaded`, gỡ ở `Unloaded`,
   DataContextChanged chỉ đăng ký khi `IsLoaded`. Harness (đã sửa để mô phỏng đúng vòng đời) xác nhận: view cũ
   KHÔNG còn phản ứng, view mới đúng.
2. **Tooltip huy hiệu ✓ mất khi nút disable** (hồi quy do G4 đưa huy hiệu vào trong Button): thêm
   `ToolTipService.ShowOnDisabled="True"` — đúng ca "op đã xong nhưng máy này không được bấm".
3. Nit: comment "5 màn" → 6 màn; bỏ dòng trống thừa OrdersView.xaml.

Chấp nhận không sửa (ghi lại): chip không ẩn khi Status TOÀN KHOẢNG TRẮNG (mẫu gốc dùng IsNullOrWhiteSpace —
thực tế Status không bao giờ là chuỗi trắng); RowEditWindow nhãn 12→11.5px NoWrap và 2 nhãn Cài đặt 12→11px
nhạt hơn (đúng chữ plan, là đổi thị giác — vào checklist duyệt); `dateChip` còn hex `#66FFFFFF` và
`dateChipTray` bo 6 lệch khay 10 của app orders (G2 chỉ ràng buộc suite).

**`pill` 20→4**: nghiệm thu grep ra chỉ ĐÚNG 1 phần tử được vẽ (`pillCookie` ở WorkspaceView) — các pill khác
định nghĩa mà không view nào dùng. Giữ trong đợt này; không ưng thì đảo 1 dòng Theme.xaml.

### 6 điểm cần user duyệt bằng mắt (khi mở app thật)
1. Huy hiệu ✓ trên 4 cột op — hiện đúng, không bị cắt mép ô.
2. Nút "Dừng việc shop này" thẳng hàng khay sub-tab, không đè tên tài khoản.
3. Menu chuột phải màn Đơn hàng — kể cả khi bấm phải lên MỘT DÒNG (hành vi mới, chủ đích).
4. Huy hiệu `pillCookie` bo 4 thay vì viên thuốc.
5. Nhãn ô RowEditWindow (11.5px) + 2 nhãn "HIỆU NĂNG"/"ĐỒNG BỘ NHIỀU MÁY" (11px, nhạt hơn).
6. Cột trái màn Tài khoản (orders) nay tự co — kiểm khi có tài khoản tên dài.
