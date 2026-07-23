# Plan: Modal đơn hàng copy được + sync tab "Tất cả" với vòng đời đơn (app chỉ giữ đơn Chuẩn bị hàng)

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Người dùng đổi hướng so với plan `2026-07-23-tab-mac-dinh-shopee-sync-cho-lay-hang.md` (đã commit `1df7509`): sync quay lại quét tab **"Tất cả"**, nhưng app đổi sang mô hình **vòng đời đơn**:

- App **chỉ lưu giữ** đơn ở trạng thái **Chuẩn bị hàng** (= "Chuẩn bị hàng"/"Chờ lấy hàng"/"Chuẩn bị giao hàng"… — khớp `chuẩn bị` hoặc `chờ lấy hàng`).
- Sync vào tab "Tất cả" để **dò theo mã đơn** xem các đơn đang theo dõi đã đi đến đâu.
- Đơn chuyển sang **Đã hủy** hoặc **Đã giao** → **lưu GSheet xong thì XÓA khỏi app** (bảng `orders` — "form Đơn hàng" hết hiển thị).
- **Người dùng đã chốt (AskUserQuestion 2026-07-23):**
  1. Đơn rời Chuẩn bị hàng nhưng CHƯA kết thúc (Đang giao/Đang vận chuyển…) → **GIỮ** trong app, theo dõi tiếp đến khi Đã giao/Đã hủy (để còn tô đỏ đơn hoàn/hủy muộn + đếm "Đã bán").
  2. Đơn lần đầu xuất hiện mà KHÔNG ở Chuẩn bị hàng (Chờ xác nhận, Đang giao, Đã giao…) → **BỎ QUA, không lưu** (đơn Chờ xác nhận sẽ tự vào ở lượt sau khi đã thành Chuẩn bị hàng).

Kèm 1 việc UI: modal "Thông tin đơn hàng" (double-click dòng đơn) hiện **không copy được chữ** — cho phép bôi đen + copy.

## 2. Phạm vi

- **Làm:** Việc A (modal copy) + Việc B (sync Tất cả + vòng đời đơn) dưới đây.
- **Không làm:**
  - KHÔNG đụng luồng Xử lý đơn / Kiểm tra / AutoRun / login / verify email.
  - KHÔNG đổi hợp đồng JSON với Apps Script (`GoogleSheetSyncService` giữ nguyên).
  - KHÔNG đụng tab mặc định Shopee (Việc A của plan trước — giữ nguyên).
  - KHÔNG đổi `MaxSyncPages` (10), `ScanOrdersJs`.

## 3. Các bước thực hiện

> Đường dẫn tương đối từ gốc repo. Tham chiếu commit `1df7509` khi revert — chỉ revert các hunk THUỘC SYNC, KHÔNG revert `ShellViewModel.cs` (tab mặc định Shopee giữ nguyên).

### Việc A — Modal Thông tin đơn hàng copy được

1. `orders/XuLyDonShopee.App/Views/OrderDetailDialog.axaml`: đổi **9 TextBlock giá trị** (các phần tử có `x:Name`: `OrderSnText`, `BuyerText`, `ProductText`, `TotalText`, `PaymentText`, `StatusText`, `CarrierText`, `TrackingText`, `SyncedAtText`) từ `TextBlock` → **`SelectableTextBlock`** (Avalonia 11 có sẵn; giữ nguyên `x:Name`, `TextWrapping`, `FontWeight`). Cột nhãn trái (`Classes="lbl"`) GIỮ `TextBlock`. Code-behind gán `.Text` không cần đổi (SelectableTextBlock có property `Text`).

### Việc B — Sync tab "Tất cả" + vòng đời đơn

2. **Revert phần sync-tab của commit `1df7509`** — `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`:
   - Call site trong `SyncAllOrdersAsync` quay lại `EnsureOrderListTabAsync(page, "l1-tab-all", ShopeeShippingNav.IsAllTabText, "Tất cả", mx, my, rng, L, ct)`.
   - Log về `"Về danh sách đơn (Tất cả) để sync..."`; comment "1b)" về "Tất cả"; comment vùng `MaxSyncPages` + doc comment interface `SyncAllOrdersAsync` bỏ các ghi chú "chỉ quét Chờ lấy hàng / bất hoạt", thay bằng mô tả hành vi MỚI: quét tab Tất cả; đơn mới chỉ được LƯU khi Chuẩn bị hàng; đơn đã theo dõi cập nhật đến trạng thái cuối rồi bị dọn (xem tầng App).
   - `orders/XuLyDonShopee.App/Services/AccountSession.cs`: StatusText về `"Đang sync đơn hàng (tab Tất cả)..."`; doc comment `SyncOrdersAsync` cập nhật; XÓA comment "2026-07-23 … BẤT HOẠT" cạnh `DetectNewlyDelivered` (tính năng hoạt động trở lại).

3. **Helper trạng thái dùng chung** — `orders/XuLyDonShopee.Core/Services/ShopeeShippingNav.cs`:
   - Thêm `public static bool LaChuanBiHang(string? status)`: chuẩn hóa (trim + gộp khoảng trắng + lower) rồi `Contains("chuẩn bị") || Contains("chờ lấy hàng")` — bê đúng logic `IsPrepareToShipStatus` hiện là private trong `ShopeeLoginService.cs` (~`:4203`).
   - `ShopeeLoginService.IsPrepareToShipStatus` sửa thành gọi `ShopeeShippingNav.LaChuanBiHang` (bỏ trùng lặp, hành vi không đổi).

4. **Repo** — `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`:
   - Thêm `public IReadOnlySet<string> GetOrderSns(long accountId)` — tập mã đơn hiện có của tài khoản.
   - Thêm `public int DeleteOrders(long accountId, IReadOnlyCollection<string> orderSns)` — DELETE theo `(account_id, order_sn)` trong 1 transaction, trả số dòng đã xóa. Danh sách rỗng → 0, không mở connection.
   - `GetForGsheetPush`: SELECT thêm `sold_counted_at`, `hub_synced_at`; record `GsheetPendingOrder` thêm 2 field `bool DaDemDaBan` (`sold_counted_at IS NOT NULL`) và `bool DaDayHub` (`hub_synced_at IS NOT NULL`). Cập nhật doc comment.

5. **Lọc INSERT khi sync** — `orders/XuLyDonShopee.App/Services/AccountSession.cs`, trong `SyncOrdersAsync` (trước `DetectNewlyDelivered`/`UpsertMany`):
   - `var existing = _services.Orders.GetOrderSns(_accountId);`
   - `var toUpsert = result.Orders.Where(o => existing.Contains(o.OrderSn) || ShopeeShippingNav.LaChuanBiHang(o.Status)).ToList();`
   - Truyền `toUpsert` (thay `result.Orders`) vào CẢ `DetectNewlyDelivered` LẪN `UpsertMany`. Comment rõ chính sách: đơn ĐÃ theo dõi luôn cập nhật (kể cả sang Đã giao/Đã hủy — cần cho GSheet + "Đã bán"); đơn MỚI chỉ nhận khi Chuẩn bị hàng. LƯU Ý quan trọng ghi vào comment: filter này đồng thời CHẶN việc đơn đã-bị-xóa (bước 6) được insert lại ở lượt quét sau (nó xuất hiện lại trong tab Tất cả với trạng thái kết thúc → không phải Chuẩn bị hàng → bỏ qua) — không có filter này sẽ lặp vô hạn ghi-xóa.
   - Câu tổng kết sync có thể thêm số đơn bỏ qua: `— bỏ qua X đơn ngoài theo dõi` khi X > 0 (X = result.Orders.Count − toUpsert.Count).

6. **Dọn đơn kết thúc sau khi lưu GSheet** — `AccountSession.PushOrdersToGsheetAsync`:
   - Restructure: đọc `pending = GetForGsheetPush(...)` TRƯỚC nhánh check URL (URL trống giờ KHÔNG return sớm nữa — vẫn phải dọn; khi URL trống coi như mọi đơn "đã settled GSheet" vì người dùng không dùng tính năng sheet).
   - Đơn KẾT THÚC: `terminal = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason) || ShopeeShippingNav.LaDaGiaoDaBan(p.Status)`.
   - Theo dõi per-đơn cờ `gsheetSettled`:
     - URL trống → mọi đơn settled;
     - skip vì "hủy mà chưa từng có vận đơn" → settled (by design không ghi sheet);
     - skip vì điều-kiện-không-cần-gửi (dòng `if (!(!p.DaGhiSheet || coFileBoSung || huyDoi || vanDonMoi))`) → settled;
     - được GỬI → settled CHỈ khi kết quả `Ok` từ script;
     - `PushAsync` ném (lỗi mạng/lô) → mọi đơn định-gửi coi CHƯA settled (giữ lại, lượt sau tự đẩy lại — hành vi retry sẵn có).
   - Sau khi xử lý kết quả (vòng `MarkGsheetSynced` hiện có — GIỮ nguyên), tính danh sách xóa. Tách hàm PURE để test được, ví dụ trong `AccountSession`:
     `internal static bool NenXoaDonKetThuc(GsheetPendingOrder p, bool gsheetSettled, bool hubHookActive)` =
     `terminal(p) && gsheetSettled && (!LaDaGiaoDaBan(p.Status) || string.IsNullOrWhiteSpace(p.Sku) || p.DaDemDaBan) && (!hubHookActive || p.DaDayHub)`.
     Giải thích 2 điều kiện giữ-lại: (a) đơn ĐÃ GIAO có SKU nhưng `sold_counted_at` còn NULL → +1 "Đã bán" lên hub chưa xong → giữ để lượt sau retry (xóa sớm là mất đếm); (b) hub đơn hàng đang bật (`hubHookActive` — dùng ĐÚNG điều kiện "hook đã rót" mà `StartHubPushInBackground` đang dùng, đọc code chỗ đó) mà đơn chưa `hub_synced_at` → giữ, kẻo hub mất đơn.
   - `deletable.Count > 0` → `_services.Orders.DeleteOrders(_accountId, deletable)` + `_services.RaiseOrdersChanged()` (lưới Đơn hàng tự vẽ lại) + log 1 dòng: `"Dọn: đã lưu sheet & xóa {n} đơn kết thúc (Đã giao/Đã hủy) khỏi app."`. Đơn terminal chưa settled → log đếm ngắn (`"Dọn: {m} đơn kết thúc chờ lượt sau (GSheet/hub/đếm chưa xong)."`) khi m > 0.
   - Doc comment method cập nhật theo hành vi mới.

7. **Test** — `orders/XuLyDonShopee.Tests`:
   - `ShopeeShippingNavTests`: `LaChuanBiHang` (— "Chuẩn bị hàng", "Chờ lấy hàng", "chuẩn  bị  giao  hàng" nhiều khoảng trắng → true; "Đã giao", "Đang giao", "Chờ xác nhận", null/rỗng → false).
   - `OrdersRepositoryTests`: `GetOrderSns` (đúng account); `DeleteOrders` (xóa đúng đơn/đúng account, trả số dòng, danh sách rỗng → 0); `GetForGsheetPush` map 2 cờ mới `DaDemDaBan`/`DaDayHub` đúng theo cột NULL/không-NULL.
   - Test mới cho `NenXoaDonKetThuc`: ma trận — terminal+settled+không-SKU → xóa; Đã giao+SKU+chưa đếm → giữ; Đã giao+SKU+đã đếm → xóa; Đã hủy+settled → xóa; chưa settled → giữ; hub active + chưa đẩy hub → giữ; trạng thái trung gian ("Đang giao") → không xóa.
   - Test filter insert (nếu viết được thuần DB): upsert đơn mới "Đang giao" bị lọc… (logic nằm ở App/LINQ — tối thiểu test `LaChuanBiHang` + repo là đủ; KHÔNG dựng test UI).
   - Nếu có test cũ assert chuỗi "(tab Chờ lấy hàng)" hoặc hành vi to-ship → cập nhật về hành vi mới.

### Build + test
8. `dotnet build ShopeeSuite.sln` 0 lỗi; `dotnet test orders/XuLyDonShopee.Tests` toàn bộ xanh (843 test hiện có + test mới).

## 4. Tiêu chí nghiệm thu

- [ ] Build + toàn bộ test xanh.
- [ ] Modal Thông tin đơn hàng: bôi đen + Ctrl+C được mọi giá trị (9 dòng); nhãn trái không cần.
- [ ] Sync quét tab "Tất cả" (log/StatusText như trước commit `1df7509`); tab mặc định Shopee GIỮ nguyên.
- [ ] Đơn mới KHÔNG ở Chuẩn bị hàng → không vào DB; đơn đã theo dõi → luôn cập nhật trạng thái mới.
- [ ] Đơn theo dõi chuyển Đã hủy (từng có vận đơn) → gửi GSheet `daHuy=true` (tô đỏ) rồi XÓA khỏi DB sau khi script trả Ok; Đã hủy chưa từng có vận đơn → xóa thẳng không gửi; Đã giao → sau khi GSheet settled + "Đã bán" đã đếm (nếu có SKU) + hub đã nhận (nếu hub bật) → XÓA.
- [ ] Đơn trung gian (Đang giao/Đang vận chuyển/Chờ xác nhận đã theo dõi) → GIỮ trong app.
- [ ] GSheet lỗi (mạng/script) → đơn kết thúc GIỮ lại, lượt sync sau tự đẩy + dọn tiếp; KHÔNG mất dòng sheet, KHÔNG xóa khi chưa settled.
- [ ] Lưới Đơn hàng tự refresh sau khi dọn (qua `RaiseOrdersChanged`).
- [ ] Đơn đã xóa xuất hiện lại trong scan (vẫn nằm trong 10 trang đầu, trạng thái kết thúc) → KHÔNG insert lại, KHÔNG lặp ghi-xóa.
- [ ] `DetectNewlyDelivered`/"Đã bán" theo SKU hoạt động trở lại (comment "bất hoạt" đã gỡ).

## 5. Rủi ro & lưu ý

- **Thứ tự an toàn:** XÓA chỉ diễn ra SAU khi mọi nghĩa vụ của đơn hoàn tất (GSheet settled + sold-count + hub). Nghi ngờ thì GIỮ — đơn thừa vô hại, đơn mất là mất dữ liệu.
- **Race nền:** gsheet-push, hub-push, sold-count chạy nền song song sau sync. Cờ đọc từ snapshot `pending` — nếu hub/sold vừa xong sau snapshot thì đơn được giữ thêm 1 lượt, xóa lượt sau (chấp nhận, không sao).
- **MaxSyncPages=10:** dọn dựa trên TRẠNG THÁI TRONG DB (không cần đơn xuất hiện lại trong scan) nên đơn kết thúc cũ nằm ngoài 10 trang vẫn được dọn dần; riêng đơn TRUNG GIAN cũ ngoài 10 trang sẽ nằm lại đến khi được quét thấy — DB co dần theo thời gian nên hiếm gặp.
- **Notify đơn mới:** giờ chỉ báo khi đơn VÀO Chuẩn bị hàng (trước có thể báo từ Chờ xác nhận) — hệ quả đã hiểu, không sửa thêm.
- **Legacy:** DB đang có ~295 đơn đủ loại trạng thái; lượt sync đầu sau bản này sẽ dọn hàng loạt đơn Đã giao/Đã hủy cũ (đơn đã có cờ gsheet → skip-gửi → settled → xóa; đơn hủy không vận đơn → xóa thẳng). Đây là chủ đích ("app chỉ giữ đơn Chuẩn bị hàng").
- `SelectableTextBlock` yêu cầu Avalonia 11 — dự án đang ở 11.x; nếu build lỗi vì thiếu type thì báo lại, KHÔNG tự chế control.

---

## Báo cáo thực thi

**Trạng thái:** hoàn thành. Build `ShopeeSuite.sln` 0 lỗi / 0 cảnh báo; `dotnet test XuLyDonShopee.Tests` **873/873 xanh** (843 cũ + 30 mới).

### Việc A — Modal copy được
- `orders/XuLyDonShopee.App/Views/OrderDetailDialog.axaml`: đổi 9 phần tử giá trị (`OrderSnText`, `BuyerText`, `ProductText`, `TotalText`, `PaymentText`, `StatusText`, `CarrierText`, `TrackingText`, `SyncedAtText`) từ `TextBlock` → `SelectableTextBlock`; giữ nguyên `x:Name`/`TextWrapping`/`FontWeight`. Cột nhãn (`Classes="lbl"`) vẫn là `TextBlock`. Code-behind gán `.Text` không đổi (SelectableTextBlock kế thừa property `Text`).

### Việc B — Sync tab "Tất cả" + vòng đời đơn
- `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`: revert 4 chỗ sync-tab của `1df7509` — doc comment interface `SyncAllOrdersAsync` (mô tả tab "Tất cả" + mô hình vòng đời đơn mới), comment `MaxSyncPages`, log `"Về danh sách đơn (Tất cả) để sync..."`, và call site 1b) đổi lại `EnsureOrderListTabAsync(page, "l1-tab-all", ShopeeShippingNav.IsAllTabText, "Tất cả", …)`. **KHÔNG** đụng call site tab "Chờ lấy hàng" trong `ProcessFirstOrderAsync` (dòng ~3570, luồng Xử lý đơn — ngoài phạm vi). `IsPrepareToShipStatus` giờ ủy quyền cho `ShopeeShippingNav.LaChuanBiHang` (bỏ trùng, hành vi giữ nguyên).
- `orders/XuLyDonShopee.Core/Services/ShopeeShippingNav.cs`: thêm `public static bool LaChuanBiHang(string?)` (chuẩn hóa qua `NormalizeUiText` rồi `Contains("chuẩn bị") || Contains("chờ lấy hàng")`).
- `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`: thêm `GetOrderSns(long)` và `DeleteOrders(long, IReadOnlyCollection<string>)` (1 transaction, trả số dòng, rỗng→0 không mở connection); `GetForGsheetPush` SELECT thêm `sold_counted_at`, `hub_synced_at`; record `GsheetPendingOrder` thêm `bool DaDemDaBan`, `bool DaDayHub`.
- `orders/XuLyDonShopee.App/Services/AccountSession.cs`:
  - `SyncOrdersAsync`: StatusText về `"Đang sync đơn hàng (tab Tất cả)..."`; doc comment cập nhật; bỏ comment "BẤT HOẠT"; thêm bộ lọc INSERT `toUpsert = result.Orders.Where(o => existing.Contains(o.OrderSn) || LaChuanBiHang(o.Status))` (dùng `GetOrderSns`) truyền vào cả `DetectNewlyDelivered` lẫn `UpsertMany`; summary thêm `— bỏ qua X đơn ngoài theo dõi` khi X>0.
  - `PushOrdersToGsheetAsync`: restructure — đọc `pending` TRƯỚC check URL; URL trống → coi mọi đơn settled + KHÔNG return, vẫn dọn; theo dõi cờ per-đơn `settled` (skip-by-design → settled; gửi Ok → settled; `PushAsync` ném → đơn định-gửi giữ chưa settled nhưng vẫn dọn đơn settled-by-design); sau đó tính `deletable` qua `NenXoaDonKetThuc`, gọi `DeleteOrders` + `RaiseOrdersChanged()` + log dọn.
  - Thêm hàm PURE `internal static bool NenXoaDonKetThuc(GsheetPendingOrder, bool gsheetSettled, bool hubHookActive)` đúng công thức plan.
  - Thêm `using XuLyDonShopee.Core.Data;`.
- `orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj`: thêm `<InternalsVisibleTo Include="XuLyDonShopee.Tests" />` để test được hàm `internal NenXoaDonKetThuc` (mẫu như Core; cần vì plan chỉ định `internal static`).

### Test mới (30 case)
- `ShopeeShippingNavTests`: `LaChuanBiHang_ChuaChuanBiHoacChoLayHang` (12 case).
- `OrdersRepositoryTests`: `GetForGsheetPush_MapCoDaDemDaBan_VaDaDayHub_TheoCotNull`, `GetOrderSns_TraDungMaDon_DungTaiKhoan`, `DeleteOrders_XoaDungDon_DungTaiKhoan_TraSoDong`, `DeleteOrders_DanhSachRong_Tra0`, `DeleteOrders_MaKhongTonTai_Tra0_KhongDungDonKhac`.
- `AccountSessionCleanupTests` (mới): ma trận `NenXoaDonKetThuc` — terminal+settled+không-SKU→xóa, Đã giao+SKU+chưa/đã đếm→giữ/xóa, Đã giao không-SKU→xóa, Đã hủy+settled→xóa, chưa settled→giữ, hub bật chưa/đã đẩy→giữ/xóa, trung gian (5 trạng thái)→không xóa. Tổng 13 case.

### Kiểm chứng
- `dotnet build ShopeeSuite.sln`: **Build succeeded, 0 Warning, 0 Error**.
- `dotnet test orders/XuLyDonShopee.Tests`: **Passed! Failed: 0, Passed: 873**.

### Quyết định tự chọn (plan chưa nói rõ)
- `hubHookActive` = `_services.PushOrdersToHub is not null` (đúng điều kiện `PushOrdersToHubAsync` dùng để return-sớm khi hook chưa rót).
- Thêm `InternalsVisibleTo` cho project App (plan chỉ định `NenXoaDonKetThuc` là `internal`; App chưa có sẵn cấu hình này như Core).
- Log dọn dùng đúng câu plan gợi ý; số đơn xóa lấy từ giá trị trả về thực của `DeleteOrders`.

### Không có vướng mắc / bỏ dở.
