# Plan: Tab mặc định = Shopee + Sync đơn CHỈ tab "Chờ lấy hàng"

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Hai chỉnh sửa theo yêu cầu người dùng (nhánh `feature/gop-don-hang`):

### Việc A — Vào app mặc định mở tab "Shopee" (thay vì "Workspace")
- **Hiện tại:** `ShellViewModel` dựng 4 tab ribbon (Workspace · Cấu hình BigSeller · Shopee · Cài đặt) và kết thúc constructor bằng `SelectedTab = workspaceTab;` (`suite/Shopee.Suite/ViewModels/ShellViewModel.cs:233-234`).
- **Mong muốn:** mở app → tab **Shopee** (module đơn hàng) hiển thị sẵn.
- **Lưu ý:** `ordersTab` là biến nullable — CHỈ được dựng khi module đơn hàng khởi tạo được (`:139-172`). Phải giữ fallback: module null → vẫn mở Workspace như cũ. Nút logo/brand (`GoHome`) VẪN về Workspace — không đổi.

### Việc B — Sync đơn hàng CHỈ quét tab "Chờ lấy hàng" (bỏ tab "Tất cả")
- **Hiện tại:** `SyncAllOrdersAsync` (`orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs:3775`) về trang danh sách đơn rồi chuyển sang tab "Tất cả" (`EnsureOrderListTabAsync(page, "l1-tab-all", ShopeeShippingNav.IsAllTabText, "Tất cả", ...)` tại `:3838-3839`), quét tối đa 10 trang mọi trạng thái.
- **Mong muốn (người dùng chốt 2026-07-23):** chỉ sync trạng thái **"Chờ lấy hàng"**, KHÔNG sync trạng thái nào khác nữa.
- **Hạ tầng có sẵn:** helper `EnsureToShipTabAsync` (`:5094-5097`, testid `l1-tab-toship`, text match `ShopeeShippingNav.IsToShipTabText`, nhãn log "Chờ lấy hàng") — đang dùng cho luồng Xử lý đơn. Tái dùng nguyên xi, KHÔNG viết selector mới.

### Hệ quả đã CHẤP NHẬN (ghi nhận, KHÔNG sửa thêm trong plan này)
- **"Đã bán" theo SKU** (`DetectNewlyDelivered`): dựa vào việc sync NHÌN THẤY đơn chuyển sang "đã giao". Chỉ quét tab Chờ lấy hàng → không còn thấy đơn đã giao → tính năng +1 "Đã bán" thành **bất hoạt** (im lặng, không lỗi). GIỮ NGUYÊN code (gọi vẫn chạy, trả rỗng) — không gỡ.
- **GSheet tô đỏ đơn hủy** (`LaDonHuy`/`huyDoi`): đơn hủy rời tab Chờ lấy hàng → status trong DB không được cập nhật nữa → cờ hủy không lật → hết tô đỏ tự động. GIỮ NGUYÊN code.
- Cả hai hệ quả trên phải được nêu trong báo cáo cuối cho người dùng.

## 2. Phạm vi

- **Làm:** Việc A + Việc B đúng như trên (đổi tab đích + nhãn log/StatusText/doc comment liên quan).
- **Không làm:**
  - KHÔNG đụng logic đẩy GSheet / hub / notify / sold-count (giữ nguyên — Fable đã review riêng phần GSheet).
  - KHÔNG đổi `MaxSyncPages` (10), `ScanOrdersJs`, logic quét trang, needFinal, upsert DB.
  - KHÔNG gỡ helper `IsAllTabText` hay test của nó (helper dùng chung, test còn giá trị).
  - KHÔNG đụng luồng Xử lý đơn / Kiểm tra / AutoRun.

## 3. Các bước thực hiện

> Đường dẫn tương đối từ gốc repo. Số dòng từ khảo sát 2026-07-23, kiểm lại khi sửa.

### Việc A
1. `suite/Shopee.Suite/ViewModels/ShellViewModel.cs` (~`:233-234`): đổi
   `SelectedTab = workspaceTab;` → `SelectedTab = ordersTab ?? workspaceTab;`
   và sửa comment thành: mặc định mở tab Shopee (đơn hàng); module đơn hàng không dựng được → về Workspace.
   (`ordersTab` khai báo là `RibbonTab? ordersTab = null` phía trên — kiểm đúng tên biến.)

### Việc B
2. `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`:
   - `:3838-3839`: thay lời gọi `EnsureOrderListTabAsync(page, "l1-tab-all", ShopeeShippingNav.IsAllTabText, "Tất cả", ...)` bằng `EnsureToShipTabAsync(page, mx, my, rng, L, ct)` (giữ nhận `(mx, my)`).
   - `:3813`: log "Về danh sách đơn (Tất cả) để sync..." → "Về danh sách đơn (Chờ lấy hàng) để sync...".
   - Cập nhật comment mục `:3767` ("duyệt tab \"Tất cả\" mọi trang") và comment `:3771` ("vẫn đọc tab Tất cả") cho khớp hành vi mới.
   - Doc comment của `SyncAllOrdersAsync` trong interface (`:170-220` vùng `ILoginSession`) — chỗ nào nói quét tab "Tất cả" thì sửa thành "Chờ lấy hàng", và ghi chú: đơn ngoài tab này (đã giao/đã hủy/đang giao...) KHÔNG còn được cập nhật trạng thái mỗi lượt sync (dữ liệu cũ vẫn nằm trong DB).
3. `orders/XuLyDonShopee.App/Services/AccountSession.cs`:
   - `:634`: StatusText "Đang sync đơn hàng (tab Tất cả)..." → "Đang sync đơn hàng (tab Chờ lấy hàng)...".
   - Doc comment của `SyncOrdersAsync` (`:585-609`) nếu nhắc tab "Tất cả" thì sửa theo.
   - Thêm 1 câu comment ngắn cạnh `DetectNewlyDelivered` (`:645-649`) ghi rõ: từ 2026-07-23 sync chỉ quét tab Chờ lấy hàng nên nhánh phát-hiện-đã-giao thực tế bất hoạt; GIỮ code để dễ bật lại.
4. Rà chuỗi còn sót: grep `"tab Tất cả"` / `"(Tất cả)"` trong `orders/` — chỉ sửa các chỗ thuộc luồng SYNC; các chỗ thuộc màn "Đơn hàng" UI (filter "Tất cả" trong OrdersView...) GIỮ NGUYÊN.

### Build + test
5. `dotnet build ShopeeSuite.sln` thành công; `dotnet test orders/XuLyDonShopee.Tests` toàn bộ xanh (774 test hiện có). Nếu có test assert chuỗi StatusText/tab cũ → cập nhật test theo hành vi mới (chỉ đổi chuỗi, không đổi cấu trúc test).

## 4. Tiêu chí nghiệm thu

- [ ] Build solution + toàn bộ test xanh.
- [ ] Mở app: tab ribbon đang chọn là **Shopee**, màn con đầu tiên (Tài khoản) hiển thị. Khi module đơn hàng không khởi tạo được → vẫn mở Workspace (đọc code xác nhận nhánh fallback).
- [ ] (Đối chiếu code) `SyncAllOrdersAsync` gọi `EnsureToShipTabAsync` (testid `l1-tab-toship`), KHÔNG còn tham chiếu `l1-tab-all`/`IsAllTabText` trong luồng sync (helper + test của `IsAllTabText` vẫn còn nguyên).
- [ ] Log/StatusText nói "Chờ lấy hàng" thay vì "Tất cả" trong luồng sync.
- [ ] KHÔNG có thay đổi nào ở: GSheet push, hub push, notify, sold-count, Xử lý đơn, AutoRun, MaxSyncPages, ScanOrdersJs.

## 5. Rủi ro & lưu ý

- **Sub-tab của "Chờ lấy hàng":** trang Shopee có thể có tab con (Chưa xử lý / Đã xử lý) bên trong tab Chờ lấy hàng; ta KHÔNG click tab con — quét danh sách mặc định sau khi vào l1-tab. Chẩn đoán trang 1 sẵn có (log "Trạng thái đọc được (trang 1)") sẽ cho biết thực tế quét được gì — người dùng soi log khi chạy thật. KHÔNG thêm selector mới khi chưa có DOM thật.
- Selector trang Shopee chưa được soi live trên phiên thật (như ghi chú memory hiện có) — mọi nhánh tab đều best-effort, fail chỉ log rồi quét tab hiện tại, không ném.
- `EnsureToShipTabAsync` là private static trong cùng class phiên — gọi thẳng được, không đổi chữ ký.

---

## Báo cáo thực thi

(Opus điền sau khi xong.)
