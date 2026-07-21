# Plan: UI tab Shopee — double-click đơn (info + đổi trạng thái) & ribbon "Hành động" theo màn

- **Ngày:** 2026-07-22
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Hai chỉnh sửa UI trên **tab "Shopee"** của app Shopee Suite (module đơn hàng nằm ở `orders/`, ribbon ở suite shell). Gộp chung 1 plan vì cùng vùng UI.

### Việc A — Double-click lưới Đơn hàng → hộp thoại info + đổi trạng thái
- **Hiện tại:** double-click 1 ô trong lưới Đơn hàng → **copy text ô** đó vào clipboard + hiện toast "Đã copy…". Xử lý ở code-behind `OnCellPointerPressed`.
- **Mong muốn:** double-click 1 dòng → mở **hộp thoại thông tin cơ bản** của đơn + cho **đổi trạng thái** (chọn 1 trong **các trạng thái đã sync về**).
- **Quyết định đã chốt:**
  - Trạng thái là **chuỗi tự do** đọc từ Shopee (không enum). Nguồn cho ComboBox = **danh sách trạng thái distinct trong DB** (`OrdersRepository.AllStatuses()`).
  - Đổi trạng thái là **SỬA TẠM (local-only)**: cập nhật ngay bản ghi trong DB, **KHÔNG cần giữ vững qua sync** — chấp nhận lần sync sau ghi đè lại bằng trạng thái thật từ Shopee. → KHÔNG thêm cờ override, KHÔNG sửa logic sync/gsheet/hub.
  - Thông tin cơ bản hiển thị: **mã đơn (OrderSn), người mua, sản phẩm (ItemSummary), tổng tiền, thanh toán, trạng thái hiện tại, ĐVVC (Carrier), mã vận đơn (TrackingNumber), ngày sync (SyncedAt)**.

### Việc B — Ribbon "Hành động" + checkbox "Xóa profile và tạo lại" chỉ bật ở màn Tài khoản
- **Hiện tại:** nhóm ribbon "Hành động" (Chọn tất cả / Sync đã chọn / Dừng đã chọn / Dừng tất cả) và checkbox "Xóa profile và tạo lại" (nhóm "Tùy chọn") **luôn bật** ở mọi màn của tab Shopee (Tài khoản / Đơn hàng / Chạy tự động / Proxy).
- **Mong muốn:** các nút/checkbox này **chỉ enable khi đang ở màn "Tài khoản"**; các màn khác → **làm mờ (disable), KHÔNG ẩn** (bố cục ribbon giữ nguyên kiểu Office).
- **Quyết định đã chốt:** cả **4 nút** "Hành động" + checkbox đều theo màn (kể cả "Dừng tất cả" — không giữ ngoại lệ). Dùng **IsEnabled** (disable), không dùng IsVisible.
- Nguồn sự thật "đang ở màn Tài khoản" = **`OrdersMainViewModel.SelectedNavIndex == 0`**.

## 2. Phạm vi

- **Làm:** Việc A + Việc B như trên.
- **Không làm:**
  - Không đụng logic **sync đơn** / đẩy **gsheet** / đẩy **hub** (đổi trạng thái chỉ local, tạm).
  - Không thêm enum/danh sách trạng thái cố định (dùng danh sách động đã có).
  - Không đổi các nút riêng của màn Đơn hàng (Làm mới / In nhiều đơn / Xuất CSV).
  - Không đụng module Scrape (đang có agent khác sửa captcha ở đó) — chỉ chạm `orders/` + `suite/Shopee.Suite/ViewModels/ShellViewModel.cs`, `.../RibbonModels.cs`, `.../MainWindow.axaml`, `orders/.../MainViewModel.cs`.

## 3. Các bước thực hiện

> Đường dẫn tương đối từ gốc repo. Số dòng lấy từ khảo sát, kiểm lại vì có thể lệch.

### Việc A

1. **Đổi handler double-click** — `orders/XuLyDonShopee.App/Views/OrdersView.axaml.cs`, hàm `OnCellPointerPressed` (~`:34-56`).
   - Giữ điều kiện double-click chuột trái (`ClickCount == 2`, left button).
   - Thay vì copy: lấy `OrderRowViewModel` của dòng qua `e.Row.DataContext` (event `DataGridCellPointerPressedEventArgs` có `Row`). Nếu null → bỏ qua.
   - Gọi service mở dialog (bước 3), nhận trạng thái mới; nếu có thay đổi → cập nhật DB (bước 5) + refresh lưới (bước 6).
   - Bỏ đường copy clipboard + toast cho double-click (Popup toast + `CellTextExtractor` nếu không còn dùng chỗ nào khác thì gỡ; nếu còn dùng thì để nguyên, chỉ bỏ nhánh gọi).
2. **Tạo dialog `OrderDetailDialog`** — thêm `orders/XuLyDonShopee.App/Views/OrderDetailDialog.axaml` (+ `.axaml.cs`) theo khuôn `ConfirmDialog.axaml`/`ImportProxyDialog.axaml` (`Window`, `WindowStartupLocation=CenterOwner`, `SizeToContent=Height`).
   - Hiển thị **thông tin cơ bản** (danh sách field ở mục 1) dạng nhãn:giá-trị, chỉ đọc.
   - **ComboBox chọn trạng thái**: `ItemsSource` = danh sách trạng thái đã sync (bước 4), `SelectedItem` = trạng thái hiện tại của đơn.
   - Nút **Lưu** → `Close(<trạng thái đã chọn>)`; **Hủy** → `Close(null)`. (Theo mẫu code-behind thuần của `ImportProxyDialog`, `Close(value)`.)
3. **Thêm service mở dialog** — `orders/XuLyDonShopee.App/Services/DialogService.cs`: thêm `Task<string?> EditOrderAsync(OrderRow row, IReadOnlyList<string> statuses)` (hoặc nhận `OrderRowViewModel`) → mở `OrderDetailDialog` bằng `ShowDialog<string?>(MainWindow)`, trả trạng thái mới (null nếu Hủy hoặc không đổi).
4. **Nguồn danh sách trạng thái** — dùng `OrdersRepository.AllStatuses(...)` (`orders/XuLyDonShopee.Core/Data/OrdersRepository.cs:437-462`). Ưu tiên tái dùng danh sách đã nạp sẵn trong `OrdersViewModel` (`ReloadStatuses`, `OrdersViewModel.cs:163-174`) nếu tiện; nếu không thì gọi lại repo.
5. **Thêm hàm cập nhật trạng thái 1 đơn** — `OrdersRepository`: thêm `void UpdateStatus(long accountId, string orderSn, string status)` = `UPDATE orders SET status=$status WHERE account_id=$accountId AND order_sn=$orderSn`. (Chỉ cột `status`; KHÔNG đụng cột khác.) Expose `AccountId` + `OrderSn` từ `OrderRowViewModel`/`OrderRow` ra ngoài để gọi được (hiện `OrderRowViewModel` chưa lộ `Id`/`AccountId` — bổ sung property đọc, hoặc truyền thẳng `OrderRow`).
6. **Refresh lưới sau khi đổi** — sau `UpdateStatus`, phát `OrdersChanged` hoặc gọi `OrdersViewModel.Reload()` (`OrdersViewModel.cs:129`) để dòng hiện trạng thái mới.

### Việc B

7. **Thêm cờ enable cho item ribbon** — `suite/Shopee.Suite/ViewModels/RibbonModels.cs`: thêm property observable `bool IsEnabled` (mặc định `true`) cho `RibbonActionItem` (~`:90-108`) và `RibbonToggleItem` (~`:114-146`). (Hoặc — sạch hơn — thêm cờ `IsEnabled` ở mức `RibbonGroup` (`:33-45`) và bind IsEnabled của container nhóm; xem Rủi ro để chọn cách tránh xung đột với CanExecute.)
8. **Bind vào template** — `suite/Shopee.Suite/MainWindow.axaml`:
   - Nút "Hành động": `Button Classes="ribbon"` (`:90`) thêm `IsEnabled="{Binding IsEnabled}"`.
   - Checkbox "Tùy chọn": `CheckBox Classes="ribbonToggle"` (`:105`) thêm `IsEnabled="{Binding IsEnabled}"`.
   - (Nếu chọn cách group-level: bind `IsEnabled` ở container nhóm quanh `:66/:68`.)
9. **Nối cờ với màn đang chọn** — `suite/Shopee.Suite/ViewModels/ShellViewModel.cs` chỗ dựng nhóm "Hành động" + "Tùy chọn" (`:152-168`):
   - Đặt `IsEnabled` ban đầu = `ordersVm.SelectedNavIndex == 0` cho 4 action item + toggle item.
   - Đăng ký nghe `ordersVm.PropertyChanged`: khi `SelectedNavIndex` đổi → cập nhật `IsEnabled = (SelectedNavIndex == 0)` cho các item đó. (`OrdersMainViewModel.SelectedNavIndex` là `[ObservableProperty]` — `orders/XuLyDonShopee.App/ViewModels/MainViewModel.cs:62-63,91`; mẫu nghe source có ở `RibbonToggleItem`, `RibbonModels.cs:126-129`.)
10. **Disable, KHÔNG ẩn** — dùng `IsEnabled` (làm mờ). Không dùng `IsVisible`.

### Build + test
11. Build: `dotnet build ShopeeSuite.sln` phải thành công. Nếu có test liên quan orders thì chạy.

## 4. Tiêu chí nghiệm thu

- [ ] Build `dotnet build ShopeeSuite.sln` thành công.
- [ ] Double-click 1 dòng đơn → mở hộp thoại hiện đúng **thông tin cơ bản** (mã đơn, người mua, sản phẩm, tổng tiền, thanh toán, trạng thái, ĐVVC, mã vận đơn, ngày sync). KHÔNG còn copy text ô + toast khi double-click.
- [ ] Hộp thoại có ComboBox liệt kê **các trạng thái đã sync về** (từ DB), chọn được, mặc định = trạng thái hiện tại.
- [ ] Bấm Lưu → trạng thái đơn trong lưới đổi ngay theo lựa chọn (ghi DB cột `status`). Bấm Hủy → không đổi gì.
- [ ] (Đối chiếu code) `UpdateStatus` chỉ đụng cột `status` theo `(account_id, order_sn)`; KHÔNG đụng sync/gsheet/hub.
- [ ] (Ribbon) Ở màn **Tài khoản**: nhóm "Hành động" (4 nút) + checkbox "Xóa profile và tạo lại" **bật bình thường, bấm được**.
- [ ] (Ribbon) Chuyển sang **Đơn hàng / Chạy tự động / Proxy**: cả 4 nút "Hành động" + checkbox **làm mờ (disabled), không bấm được**, và **không bị ẩn** (vẫn thấy). Quay lại Tài khoản → bật lại.
- [ ] Nhóm "Màn hình" (Tài khoản/Đơn hàng/Chạy tự động/Proxy) vẫn bật ở mọi màn.

## 5. Rủi ro & lưu ý

- **Xung đột `IsEnabled` vs `CanExecute` (ribbon):** nút "Hành động" đang tự disable theo `CanExecute` của command. Nếu set thẳng `IsEnabled="{Binding IsEnabled}"` trên `Button` command-bound có thể chỏi với trạng thái do CanExecute. **Ưu tiên gate ở mức container nhóm** (đặt `IsEnabled` cho cả nhóm "Hành động"/"Tùy chọn" — con tự kế thừa disable trong Avalonia), để ở màn Tài khoản (`IsEnabled=true`) các nút vẫn theo CanExecute riêng, còn màn khác (`IsEnabled=false`) khóa cả cụm. Nếu gate per-item, phải kiểm chứng lúc chạy rằng ở màn Tài khoản nút vẫn hoạt động đúng.
- **Refresh lưới:** dùng đúng cơ chế sẵn có (`OrdersChanged`/`Reload`) để tránh double-load hay mất selection.
- **Sửa trạng thái là tạm:** ghi rõ (comment code) rằng `UpdateStatus` là local-only, lần sync sau sẽ ghi đè — để người sau không tưởng là bền. KHÔNG tự ý thêm cơ chế giữ-vững.
- **Không đụng vùng Scrape** (agent khác đang sửa captcha trên cây chính). Việc này chạy trong **worktree riêng**; mọi đường dẫn quy về thư mục làm việc của agent, tuyệt đối không đọc/ghi cây chính.
- Lấy `OrderRow` từ dòng: chú ý `OrderRowViewModel` hiện không lộ `AccountId`/`OrderSn` ra ngoài — bổ sung property đọc gọn, đừng phá binding cột hiện có.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chờ thực thi>
