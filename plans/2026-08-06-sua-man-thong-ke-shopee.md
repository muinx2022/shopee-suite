# Plan: sửa màn "Shopee → Thống kê" (module Đơn hàng)

- **Ngày:** 2026-08-06
- **Trạng thái:** đang làm
- **Người lập:** phiên chính · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Người dùng yêu cầu rà soát màn **Shopee → Thống kê** ([ShellViewModel.cs:228](../suite/Shopee.Suite/ViewModels/ShellViewModel.cs) →
`OrderStatisticsViewModel` của module Đơn hàng). Đợt rà soát 06/08/2026 (đọc toàn tuyến + render màn thật bằng
harness WPF + bắn sự kiện chuột thật + 5 góc soi đối kháng) tìm ra **14 vấn đề**. Người dùng chốt:
*"bạn cứ làm lần lượt đi, sao cho không còn lỗi và dựng lại đúng yêu cầu là ổn"* → sửa HẾT, làm tuần tự 2 đợt.

**Cơ chế hiện tại của màn** (giữ nguyên, không đập đi):
số LOCAL vẽ ngay (đồng bộ) → hỏi Hub ở nền → có số chung thì thay vào; `_statsRequestId` chống lượt cũ đè lượt
mới; `_dangHienSoHub`/`_shopSoHub`/`_rangeSoHub` chống "số nhảy" mỗi lượt sync. Phần này ĐÚNG, đừng phá.

**Quyết định đã chốt cho vấn đề #1** (số local hụt): **KHÔNG** dựng bảng tổng hợp (ledger) ở client.
Lý do: (a) Hub đã là kho lịch sử đầy đủ — lọc `first_seen_at`, không bao giờ dọn đơn; (b) dự án đã chốt
"không xử lý thêm dữ liệu ở local"; (c) số local chỉ là đường DỰ PHÒNG trong lúc Hub chưa trả lời.
→ Cách sửa: **nói đúng sự thật trên UI** (xem bước 1).

## 2. Phạm vi

- **Làm:** 14 mục liệt kê ở mục 3, chia 2 đợt. Sửa cả VM, View, style của module, hook ở suite, và test.
- **Không làm:**
  - KHÔNG dựng bảng tổng hợp/ledger thống kê ở client (đã chốt ở mục 1).
  - KHÔNG đổi luật dọn đơn kết thúc (`NenXoaDonKetThuc`, `HubOutbox`) — vòng đời đơn giữ nguyên.
  - KHÔNG đổi API/hợp đồng `GET /api/orders/stats` và DTO `SharedOrderStatistics` (client cũ đang chạy).
  - KHÔNG đụng các màn khác của module (Tài khoản, Đơn hàng) trừ 1 dòng gate ở `MainViewModel` (bước 10).
  - KHÔNG đổi `Themes/Theme.xaml` của suite.

---

## 3. Các bước thực hiện

### ĐỢT 1 — số đúng & dùng được (bước 1-6)

**Bước 1 — Nói rõ số LOCAL bị hụt (vấn đề #1, nặng nhất)**

Sự thật: kho `orders` trên máy KHÔNG phải kho lịch sử — [HubOutbox.cs:540](../orders/XuLyDonShopee.App/Services/HubOutbox.cs)
xoá hẳn đơn *Đã giao/Đã hủy* khi xong nghĩa vụ, và [OrderPersistPipeline.cs:92](../orders/XuLyDonShopee.App/Services/OrderPersistPipeline.cs)
chỉ INSERT đơn mới khi còn *Chuẩn bị hàng*. Nên khi đang xem "số máy này", 3 thẻ **ĐÃ GIAO / ĐÃ HỦY /
DOANH THU ƯỚC TÍNH** luôn thiếu.

Sửa trong `orders/XuLyDonShopee.App/ViewModels/OrderStatisticsViewModel.cs`:
- Thêm `[ObservableProperty] private bool _dangXemSoMay;` — true khi lưới đang hiện số LOCAL (đặt `true` cuối
  `ApplyLocal`, `false` trong `ApplyShared`). Đây là property CÔNG KHAI cho XAML, khác `_dangHienSoHub` (field
  nội bộ điều khiển việc vẽ đè) — **không gộp hai thứ này**.
- Bổ sung vế cảnh báo vào 3 hằng dòng nguồn local (`SourceDangHoiText`, `SourceLocalText`,
  `SourceStandaloneText`): thêm câu *"Kho máy chỉ giữ đơn CHƯA kết thúc (đơn Đã giao/Đã hủy đã dọn sau khi đẩy
  Hub & Google Sheet) — ĐÃ GIAO / ĐÃ HỦY / DOANH THU bên dưới bị HỤT."*

Sửa `orders/XuLyDonShopee.App/Views/OrderStatisticsView.xaml`:
- Dưới 3 thẻ ĐÃ GIAO (dòng ~206), ĐÃ HỦY (~213), DOANH THU ƯỚC TÍNH (~220): thêm một `TextBlock` cỡ 10.5
  chữ `TextMuted` nội dung *"chỉ đơn CÒN trên máy"*, `Visibility` bind `DangXemSoMay` qua `BoolToVis`.
  Thẻ DOANH THU đã có dòng "Không tính đơn hủy" — ghép thành một dòng động, đừng chồng 2 dòng.
- Sửa đoạn ghi chú "Cách tính" (dòng ~329): thêm câu nói thẳng việc đơn kết thúc bị dọn khỏi máy và
  chỉ số CHUNG (Hub) mới đủ lịch sử.

**Bước 2 — Lăn chuột trên lưới phải cuộn được TRANG (vấn đề #2)**

Đã đo: con trỏ trên `DataGrid` → offset trang 0→0, `handled=True`; trên thẻ số → 0→48. 4 lưới nuốt sạch
bánh xe chuột, mà chúng chiếm gần hết thân màn.

Sửa `orders/XuLyDonShopee.App/Views/OrderStatisticsView.xaml.cs` (hiện 9 dòng) + XAML:
- Thêm handler `LuoiXemSo_PreviewMouseWheel`: nếu lưới **không còn cuộn được theo chiều lăn** (hoặc không có
  gì để cuộn) → `e.Handled = true` rồi bắn lại `MouseWheelEventArgs` lên phần tử cha để `ScrollViewer` ngoài
  nhận. Còn cuộn được thì để nguyên (giữ khả năng cuộn trong lưới dài).
- Tìm `ScrollViewer` nội bộ của `DataGrid` qua `VisualTreeHelper`; so `VerticalOffset` với `0` và
  `ScrollableHeight`. `ScrollableHeight <= 0` → chuyển tiếp ngay.
- Gắn `PreviewMouseWheel="LuoiXemSo_PreviewMouseWheel"` cho CẢ 4 `DataGrid`.

**Bước 3 — Nhịp sang ngày mới (vấn đề #3)**

`DateTime.Today` chỉ đọc 1 lần (ctor + `ApplyDatePreset`), app chạy 24/7 → qua nửa đêm chip "Hôm nay" vẫn
sáng mà dữ liệu là của hôm qua.

Chép ĐÚNG mẫu đã có ở [AccountsViewModel.KetQua.cs](../orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.KetQua.cs)
(`NhipDoSangNgay = 60s`, `System.Threading.Timer`, callback marshal về UI, nuốt lỗi để ngoại lệ không giết
tiến trình):
- Thêm `_timerSangNgay` dựng ở ctor; `NhipSangNgay()` → `UiDispatch.Run(() => KiemTraSangNgay(DateTime.Today))`.
- `KiemTraSangNgay(DateTime homNay)`: chỉ làm gì khi `_ngayCoiLaHomNay != homNay`. Cập nhật `_ngayCoiLaHomNay`,
  rồi **nếu `DatePreset` khác rỗng** → gọi lại `ApplyDatePreset(DatePreset)` để khoảng ngày trượt theo ngày mới.
  `DatePreset` rỗng (người dùng tự chọn ngày trên lịch) → **TUYỆT ĐỐI không giật ngày khỏi tay họ**.
- `OrderStatisticsViewModel` implement `IDisposable`, `Dispose()` dọn timer + gỡ `_services.OrdersChanged`.
- Gọi dọn khi thoát app: thêm `_mainVm?.StatisticsVm.Dispose()` cạnh dòng
  `_mainVm?.AccountsVm.Dispose()` trong [OrdersModuleHost.cs:116](../suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs).

**Bước 4 — Gỡ bom hẹn giờ trong test (vấn đề #4)**

`TuChonNgayTrenLich_NhaHetChip` ([OrderStatisticsDatePresetTests.cs:78](../orders/XuLyDonShopee.Tests/OrderStatisticsDatePresetTests.cs))
gán `DateTime.Today.AddDays(-3)`; **ngày mùng 4** giá trị đó trùng đúng `FromDate` mặc định (mùng 1) → setter
không bắn → chip không nhả → test ĐỎ. (Đã đo: gán trùng giá trị ⇒ `DatePreset` vẫn `'thang-nay'`.)
- Sửa thành `vm.FromDate = vm.FromDate!.Value.AddDays(-3);` (luôn khác giá trị cũ, không phụ thuộc ngày chạy).
- Thêm test mới `GanLaiDungGiaTriCu_KhongCoiLaNguoiDungDoiNgay` chốt hành vi này để không tái phát.

**Bước 5 — Nút "Làm mới" phải đọc lại kho đơn (vấn đề #5)**

Hiện `giuSoHub` bỏ qua `ApplyLocal` cho MỌI đường vào, kể cả bấm nút → tooltip "Đọc lại số liệu từ kho đơn"
nói dối, và Hub báo 0 đơn thì màn kẹt rỗng dù máy có đơn.
- Tách hai đường: `Reload()` (nút bấm) → ép vẽ lại local; đường `OnOrdersChanged` → giữ số Hub như hiện nay.
  Cách làm: thêm tham số `bool epVeLocal = false` cho `Reload`/`ApplyStatistics`, `RelayCommand` truyền `true`.
- **Sửa test hiện có** `DangHienSoChung_VeLaiCungKhoangNgay_KhongVeDeSoLocal`: nó đang gọi `vm.Reload()` để
  mô phỏng "OrdersChanged bắn sau lượt sync" — đổi sang `services.RaiseOrdersChanged()` cho đúng đường thật.
  Tương tự với `DangHienSoChung_LuotHoiMoiThatBai_NoiRoLaSoCu`.
- Thêm test `BamLamMoi_VeLaiSoLocalNgay_DuDangHienSoHub`.

**Bước 6 — Chưa cấu hình Hub thì đừng tố "Hub không phản hồi" (vấn đề #6)**

Hook `QueryOrderStatistics` được rót VÔ ĐIỀU KIỆN ([OrdersModuleHost.cs:53](../suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs))
nên `SourceStandaloneText` là code chết; máy chưa cấu hình Hub vẫn bị báo "Hub không phản hồi".
- Thêm vào `AppServices`: `public Func<bool>? HubDaCauHinh { get; set; }` (xmldoc: null = không biết → coi như
  CÓ hub, giữ hành vi cũ cho test).
- Suite rót: `services.HubDaCauHinh = () => CoordinationRuntime.Client is not null;` (đặt cạnh
  `WireOrderStatisticsRead`). Đọc TƯƠI mỗi lần gọi — người dùng có thể cấu hình Hub rồi `Reconnect()` giữa chừng.
- VM: chọn `SourceStandaloneText` khi `HubDaCauHinh?.Invoke() == false`, kể cả ở nhánh lượt hỏi trả `null`.
- Test: `ChuaCauHinhHub_NoiLaChayDocLap_KhongToHubChet`.

---

### ĐỢT 2 — nhất quán & trình bày (bước 7-14)

**Bước 7 — Danh sách shop phải gộp shop từ Hub (vấn đề #7)**

`AllShopLogins()` chỉ trả shop CÒN đơn trên máy; đơn kết thúc bị dọn → shop biến mất khỏi ComboBox và bộ lọc
âm thầm tụt về "Tất cả shop", trong khi số đang xem là số CHUNG toàn hệ thống.
- Trong `ApplyShared`: bổ sung mọi `x.Shop` của `shared.ShopRows` chưa có vào `ShopOptions` (đặt cờ
  `_reloadingOptions` quanh thao tác để không kích `ApplyStatistics`; giữ nguyên `SelectedShop`).
- Trong `Reload`: nếu `previous` không còn trong danh sách local **nhưng đang có số Hub cho shop đó** thì vẫn
  thêm lại và giữ chọn, thay vì tụt về "Tất cả shop".
- Test: `ShopChiConTrenHub_VanNamTrongDanhSachVaGiuChon`.

**Bước 8 — Một định dạng ngày duy nhất trên màn (vấn đề #8)**

`DatePicker` theo locale máy (máy dev hiện `8/1/2026`) trong khi chữ mô tả ép `vi-VN` (`01/08/2026`) — cùng
một màn hai định dạng, dễ đọc nhầm ngày↔tháng.
- Đặt `Language="vi-VN"` cho CẢ HAI `DatePicker` (WPF lấy culture của `DatePicker` từ `FrameworkElement.Language`).
- Kiểm chứng bằng harness render (mục 4), KHÔNG chỉ đọc code.

**Bước 9 — Local và Hub phải cùng luật hiển thị + cùng thứ tự (vấn đề #9)**

- Cột "Ước tính": thống nhất **0 → chuỗi RỖNG** ở cả hai đường (sửa `BuildBreakdown` trả `string.Empty` khi
  tổng = 0; giữ nguyên nhánh hub dòng ~385).
- Tie-break sắp xếp: cả hai đường dùng `StringComparer.CurrentCultureIgnoreCase`. Hub trả theo Ordinal nên
  **client sắp lại** sau khi nhận (đừng đổi hub — client cũ đang chạy).
- Test: `LuoiTrangThai_LocalVaHub_CungDinhDangVaCungThuTu`.

**Bước 10 — Màn ẩn thì đừng quét kho đơn + bắn HTTP (vấn đề #10)**

VM sống suốt vòng đời app; mỗi `OrdersChanged` (sau MỖI shop của MỖI lượt sync) đều quét kho đơn trên luồng
UI + bắn HTTP lên Hub kể cả khi người dùng đang ở màn khác.
- Thêm `public bool DangHienTrenMan { get; set; }` (mặc định `false`).
- `MainViewModel.OnSelectedNavIndexChanged`: đặt `_statisticsVm.DangHienTrenMan = value == 2;` cho mọi nhánh.
- `OnOrdersChanged`: màn không hiện → chỉ bật cờ `_canVeLai = true`, KHÔNG quét. Khi màn được chọn lại
  (`Reload()` đã được gọi sẵn ở `case 2`) thì vẽ lại và hạ cờ.
- Dispose (bước 3) đã gỡ event — giữ nguyên.
- Test: `ManAn_KhoDonDoi_KhongGoiHub` (đếm số lần hook được gọi).

**Bước 11 — Nhãn "ĐỒNG BỘ GẦN NHẤT" (vấn đề #11)**

Cả hai nguồn đều lấy mốc sync gần nhất **trong khoảng đang lọc** → chọn tháng 6 sẽ tưởng app ngừng sync cả tháng.
- Đổi nhãn thẻ thành `ĐỒNG BỘ GẦN NHẤT (TRONG KHOẢNG)`, thêm `ToolTip` giải thích.

**Bước 12 — Khoảng ngày không hợp lệ thì dòng nguồn phải câm (vấn đề #12)**

Nhánh `!TryBuildCreatedRange` không đụng `SourceText` → header vừa báo "hãy chọn ngày" vừa khẳng định
"Số chung toàn hệ thống (từ Hub)".
- Đặt `SourceText = string.Empty` trong nhánh đó; XAML ẩn dòng rỗng (`StringToVis`, đã có sẵn trong
  `ModuleResources.xaml`).
- Bổ sung assert `SourceText` vào test `ClearingEitherDate_DoesNotThrow_AndShowsValidationMessage`.

**Bước 13 — Module tự đủ tài nguyên (vấn đề #13)**

`statsGrid` là `DataGrid` DUY NHẤT trong module không tự đặt `AutoGenerateColumns="False"` — nó ăn nhờ implicit
style của suite. Đã đo: dựng view ngoài shell thì cả 4 lưới **nhân đôi cột**. Trái lời hứa
"ModuleResources tự đủ tài nguyên" ghi ở đầu `ModuleResources.xaml`.
- Thêm `<Setter Property="AutoGenerateColumns" Value="False" />` vào style `statsGrid`.
- Thêm token `NeutralBadgeBgBrush` vào `orders/XuLyDonShopee.App/Styles/Colors.xaml` (module đang tra nhờ của suite).
- Đổi `#66FFFFFF` (hover chip, dòng ~82) sang token có sẵn của module (`ButtonHoverBg`).
- Bỏ 2 `CanUserResizeColumns="True"` thừa (mặc định WPF đã true, lại chỉ đặt ở 2/4 lưới nên đọc như khác biệt cố ý).

**Bước 14 — Bố cục lưới (vấn đề #14)**

- Khi `SelectedShop` khác "Tất cả shop": ẩn khối "HIỆU QUẢ THEO SHOP" (nó chỉ còn 1 dòng lặp lại y các thẻ số
  phía trên), cho "PHÂN BỔ TRẠNG THÁI" chiếm hết chiều ngang. Thêm property `HienLuoiShop` (= đang xem tất cả shop).
- Bỏ chiều cao ghim cứng `250`/`220`: đổi `RowDefinition Height="Auto"` + `MaxHeight` trên `DataGrid`
  (`320` cho hàng trên, `260` cho hàng dưới) để màn cao thì không thừa chỗ trống, mà nhiều dòng vẫn cuộn được
  trong lưới. **Lưu ý:** đang nằm trong `ScrollViewer` nên TUYỆT ĐỐI không dùng `Height="*"`.

---

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — xanh toàn bộ, **không giảm số test**.
- [ ] `dotnet test server/Shopee.Hub.Web.Tests/Shopee.Hub.Web.Tests.csproj` — xanh (bước 9 chỉ đụng client, phải không gãy hub).
- [ ] Test mới có đủ, mỗi cái canh đúng một luật của plan:
      `GanLaiDungGiaTriCu_...`, `BamLamMoi_VeLaiSoLocalNgay_...`, `ChuaCauHinhHub_...`,
      `ShopChiConTrenHub_...`, `LuoiTrangThai_LocalVaHub_CungDinhDangVaCungThuTu`, `ManAn_KhoDonDoi_KhongGoiHub`,
      và một test cho nhịp sang ngày mới (gọi thẳng hàm `KiemTraSangNgay` — **không** ngồi chờ 60s).
- [ ] **Thử phá từng test mới**: sửa ngược lại đúng cái luật nó canh → test phải ĐỎ. Không đỏ = test rỗng.
- [ ] Harness render (`scratchpad/StatsShot`, đã dựng sẵn — phiên chính chạy) cho thấy:
      2 ô ngày hiện `dd/MM/yyyy`; lăn chuột trên lưới → offset trang ĐỔI; lọc 1 shop → khối shop biến mất;
      xem số local → 3 thẻ có dòng "chỉ đơn CÒN trên máy".
- [ ] `git diff` không chạm file ngoài phạm vi mục 2.

## 5. Rủi ro & lưu ý

- **Đừng gộp `DangXemSoMay` với `_dangHienSoHub`.** Cái sau điều khiển việc có vẽ đè số local hay không
  (chống "số nhảy"); cái trước chỉ để XAML biết đang hiện nguồn nào. Gộp là làm hỏng chống-số-nhảy.
- **Bước 5 sẽ làm ĐỎ 2 test đang xanh** (`DangHienSoChung_VeLaiCungKhoangNgay_KhongVeDeSoLocal`,
  `DangHienSoChung_LuotHoiMoiThatBai_NoiRoLaSoCu`) vì chúng gọi `vm.Reload()` để mô phỏng OrdersChanged.
  Phải sửa test sang `services.RaiseOrdersChanged()` — **KHÔNG** được nới lỏng assert cho test qua.
- **Bước 2**: chỉ chuyển tiếp bánh xe khi lưới hết chỗ cuộn. Chuyển tiếp vô điều kiện sẽ làm không cuộn được
  bên trong lưới trạng thái dài.
- **Bước 3**: callback `System.Threading.Timer` mà ném ra ngoài là GIẾT tiến trình — bắt buộc `try/catch` nuốt.
- **Bước 7**: thêm mục vào `ShopOptions` mà quên cờ `_reloadingOptions` sẽ kích `OnSelectedShopChanged` →
  vòng vẽ lại vô tận.
- **Bước 10**: gate "màn ẩn" không được làm màn mở lên hiện số cũ mốc meo — `case 2` của
  `OnSelectedNavIndexChanged` đã gọi `Reload()`, kiểm lại đường đó còn chạy.
- Mọi chuỗi hiện ra UI viết **tiếng Việt có dấu**; tên hàm/hằng mang luật nghiệp vụ đặt **tiếng Việt không dấu**
  theo quy ước `orders/CLAUDE.md`.

---

## Bổ sung sau nghiệm thu đợt 1 (dồn sang ĐỢT 2)

**Bước 15 — dọn phần chữ + bố cục mà đợt 1 làm phát sinh:**
- `SourceText` nay dài, tràn 2 dòng và chạy sát ô "Từ ngày" ở 1366px → rút gọn phần luôn hiện, đẩy phần giải
  thích dài vào `ToolTip`.
- Câu cảnh báo "…ĐÃ GIAO / ĐÃ HỦY / DOANH THU **bên dưới** bị HỤT" vẫn hiện khi `HasData == false` (kho rỗng) —
  lúc đó bên dưới không có thẻ nào. Phải câm ở cả nhánh kho rỗng, không chỉ nhánh khoảng-ngày-không-hợp-lệ.
- `BoolToVis` = Visible/**Collapsed** ⇒ 2 dòng ghi chú xuất hiện rồi biến mất làm cả hàng thẻ nhảy ~14px mỗi lượt
  Hub trả lời → dùng `Hidden` hoặc chừa `MinHeight` giữ chỗ.
- Dòng ghép của thẻ DOANH THU ("Không tính đơn hủy · chỉ đơn CÒN trên máy") chưa có `TextTrimming`/`TextWrapping`,
  cần đo lại bề rộng ở 1366px.

## Báo cáo thực thi

### Đợt 1 (bước 1-6) — HOÀN THÀNH

Người thực thi: `opus-executor`. Phản biện: `nghiem-thu` → kết luận "đạt có điều kiện", chỉ ra **2 lỗi thật**;
phiên chính tự kiểm chứng lại, xác nhận cả hai đúng và **tự sửa**:

1. **Test nhịp sang ngày là test RỖNG.** `ApplyDatePreset` tự đọc `DateTime.Today` nên tham số `homNay` của
   `KiemTraSangNgay` không đi tới đâu; test chỉ assert "đã vẽ lại". Đã tách lõi
   `ApDungChipNgay(preset, homNay)` để nhịp truyền ngày mới xuống, và siết test assert `FromDate`/`ToDate`
   THẬT SỰ trượt (+ thêm ca chip "Hôm nay").
2. **Ca "Hub báo 0 đơn" vẫn kẹt màn rỗng.** Lượt `epVeLocal` vẫn bắn hỏi Hub, Hub trả 0 → `ApplyShared` hạ
   `HasData` → số local chỉ nháy một vòng HTTP. Đã thêm chốt trong `ApplyShared`: Hub báo 0 đơn mà kho máy vừa
   đọc ra đơn cho ĐÚNG (shop, khoảng) đó → GIỮ lưới số máy, đổi dòng nguồn nói thẳng lý do.

**Kiểm chứng cuối (phiên chính tự chạy):** `dotnet build ShopeeSuite.sln` = 0 warning/0 error;
`dotnet test orders` = **1595 xanh** (trước đợt: 1578). Thử phá 3 luật mới → **cả 3 mutant đều bị test giết**:
nhịp sang ngày tự đọc đồng hồ ⇒ 2 test đỏ; bỏ chốt Hub-0-đơn ⇒ 1 test đỏ; nới chốt thành `>= 0` ⇒ 1 test đỏ.
Đo bằng harness WPF render offscreen: lăn chuột trên lưới `0→0` (trước) → `0→48` (sau), lăn ngược ở đỉnh cũng nhả.

Ba mục còn lại của báo cáo nghiệm thu (dòng cảnh báo trỏ vào chỗ trống, nhảy bố cục do `Collapsed`, dòng thẻ
DOANH THU có nguy cơ tràn) đã dồn vào **bước 15** ở trên.

### Đợt 2 (bước 7-15) — HOÀN THÀNH

Người thực thi: `opus-executor`. Đã làm đủ 9 bước, chạm đúng 5 file, **không đụng `server/Shopee.Hub.Web`**.

**Lệch plan có chủ ý (phiên chính đồng ý):** bước 7 gộp **toàn bộ** shop Hub vào `ShopOptions` thay vì chỉ giữ
lại shop đang chọn — bản literal của plan chỉ cứu được shop đang chọn, các shop Hub khác vẫn biến mất ngay lượt
`Reload` kế tiếp và không bao giờ quay lại (khi đã lọc 1 shop thì Hub chỉ trả `ShopRows` của shop đó).

**Kiểm chứng (phiên chính tự chạy):** build `ShopeeSuite.sln` = 0 warning/0 error; `dotnet test orders` =
**1604 xanh** (đợt 1: 1595, +9 ca). Người thực thi thử 13 mutant, 12 chết ngay, 1 sống (test bước 7 dựng sẵn shop
từ ctor nên đoạn chèn không bao giờ chạy) → đã viết lại test cho tới khi mutant chết.

**Soi bằng mắt (harness render, việc mà subagent không làm được):**
- 2 ô ngày hiện `01/08/2026` / `06/08/2026` — hết cảnh một màn hai định dạng ngày.
- Dòng nguồn rút còn 1 dòng, phần dài đã vào ToolTip; header hết chật.
- Nhãn `ĐỒNG BỘ GẦN NHẤT (TRONG KHOẢNG)`.
- Cột "Ước tính" của dòng "Đã hủy" nay TRỐNG (trước `₫0`) — khớp luật của số Hub.
- Lưới cao theo nội dung: ở 1366×768 nhìn được thêm hẳn một khối so với trước.
- Lọc 1 shop → khối "HIỆU QUẢ THEO SHOP" biến mất và lưới trạng thái nở ra 2 cột (`Grid.ColumnSpan` qua
  `DataTrigger` chạy thật — đây là chỗ người thực thi tự đánh dấu "dễ câm nhất").
- Dòng ghép thẻ DOANH THU hiện đủ, không bị cắt `…` ở 1366px.
- Lăn chuột trên lưới: trang vẫn cuộn được (`0→48`) — đợt 2 không làm hỏng bản vá của đợt 1.
