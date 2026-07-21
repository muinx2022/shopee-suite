# Plan: Sửa 4 điểm người dùng báo sau chạy thử (refresh sau sync shop, checkbox lệch, tên tab, màu cam)

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** cây làm việc CHÍNH `D:\Projects\shopee-suite`, nhánh `feature/gop-don-hang`. KHÔNG đụng `main`, `server/`, `shared/`.

## 1. Bốn điểm người dùng báo (2026-07-21, sau khi vọc app)

1. **BUG — Sync shop từ BigSeller báo thành công nhưng sang tab Đơn hàng KHÔNG thấy shop.**
   Nguyên nhân đã biết: màn Tài khoản (orders) chỉ `Reload()` khi `SelectedNavIndex` THAY ĐỔI
   (`MainViewModel.OnSelectedNavIndexChanged`); index mặc định 0 = Tài khoản nên vào lại tab
   Đơn hàng không đổi index → không reload → danh sách hiển thị bản nạp lúc mở app.
2. **UI — checkbox "Xóa profile và tạo lại" trên ribbon bị lệch** ô vuông so với text.
3. **Text — đổi tên tab "Đơn hàng" thành "Shopee"** (tab strip trên cùng).
4. **Màu không đồng nhất — phần suite (Workspace...) đang accent XANH `#0078D7`, phần Đơn hàng
   cam Shopee `#EE4D2D` → người dùng chốt: đổi accent của SUITE sang CAM Shopee** cho đồng nhất
   toàn app.

## 2. Các bước

### A. Refresh danh sách tài khoản sau sync shop (sửa tận gốc bằng event)

1. `orders/XuLyDonShopee.App/Services/AppServices.cs`: thêm event
   `public event Action? AccountsChanged;` + `public void RaiseAccountsChanged()` — mẫu y hệt
   cặp `OrdersChanged`/`RaiseOrdersChanged()` sẵn có trong file.
2. `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: trong ctor subscribe
   `services.AccountsChanged` → handler nạp lại danh sách: nếu
   `Dispatcher.UIThread.CheckAccess()` thì `Reload()` thẳng, không thì
   `Dispatcher.UIThread.Post(Reload)` (mẫu marshal như MainViewModel xử lý OrdersChanged).
   LƯU Ý: `Reload()` đã tồn tại và đã được gọi khi vào tab — ngữ nghĩa không đổi.
3. `suite/Shopee.Suite/Modules/BigSeller/BigSellerViewModel.cs` — trong `SyncShopsToOrders`:
   sau vòng lặp, nếu `added > 0` → `svc.RaiseAccountsChanged();` (đặt trước dòng set `Status`).
4. Test (`orders/XuLyDonShopee.Tests/`): thêm test AccountsViewModel — insert account qua repo
   SAU khi VM đã dựng → `RaiseAccountsChanged()` → danh sách VM có dòng mới. Nếu
   `Dispatcher.UIThread` gây khó trong môi trường test thì test nhánh `CheckAccess()==true`
   (gọi từ thread test luôn được coi là có access hay không — kiểm thực tế; bất khả thi thì
   tách handler ra method internal `OnAccountsChangedForTest` và test method đó + kiểm wiring
   bằng cách raise event; ghi rõ cách chọn trong báo cáo).

### B. Checkbox ribbon lệch

5. `suite/Shopee.Suite/Themes/Theme.axaml` — style `CheckBox.ribbonToggle`: căn lại ô vuông và
   nhãn thẳng hàng (VerticalAlignment/VerticalContentAlignment Center, khoảng cách hộp-chữ hợp
   lý, chiều cao khớp các nút ribbon bên cạnh). Đối chiếu trực quan bằng screenshot khi chạy app.

### C. Tên tab

6. `suite/Shopee.Suite/ViewModels/ShellViewModel.cs`: RibbonTab tiêu đề `"Đơn hàng"` →
   `"Shopee"` (đổi cả navTitle/label hiển thị trên tab strip nếu tách riêng). Các nhãn nhóm
   trong ribbon ("Màn hình"/"Hành động"/"Tùy chọn") GIỮ NGUYÊN. Nếu logic "nhớ màn theo tab"
   key theo Title thì đổi đồng bộ để không mất state.

### D. Accent suite → cam Shopee

7. `suite/Shopee.Suite/Themes/Theme.axaml`: đổi token accent XANH sang CAM Shopee — ít nhất
   `AccentBrush #0078D7 → #EE4D2D`; RÀ TOÀN BỘ file tìm các token/giá trị dẫn xuất của accent
   (hover/pressed/selected/pill topnav, border focus...) đang hardcode tông xanh (`#0078D7`,
   `#106EBE`, `#005A9E`, các biến thể...) → đổi sang bộ cam tương ứng (cam đậm hơn cho
   hover/pressed, vd `#D8431F`/`#C23A1A` — chọn tông hài hòa với `#EE4D2D`, ghi rõ mapping
   trong báo cáo). KHÔNG đổi Success/Danger/Warning. KHÔNG đụng Colors.axaml của orders
   (đã cam sẵn).
8. Kiểm ảnh hưởng: grep `#0078D7` (và các mã xanh tìm thấy) toàn `suite/` — chỗ nào ngoài
   Theme.axaml hardcode màu xanh accent (axaml khác, C# `Brush.Parse`...) thì liệt kê; đổi các
   chỗ thuộc vai trò "accent" (vd nút primary), CHỪA lại chỗ mang nghĩa riêng (nếu phân vân,
   ghi vào báo cáo để Fable quyết).

### E. Kiểm chứng

9. `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi 0 warning; test orders → 767 baseline +
   test mới pass.
10. Chạy app + chụp màn hình: (a) tab strip hiện `Workspace / Cấu hình BigSeller / Shopee /
    Cài đặt`, pill tab + nút nav active + nút accent giờ CAM đồng nhất; (b) checkbox ribbon
    thẳng hàng; (c) màn Workspace/Dữ liệu không còn lộ accent xanh. Đóng app sau khi chụp.

## 3. Tiêu chí nghiệm thu

- [ ] Build sạch; test ≥767 + test mới pass.
- [ ] Sync shop từ BigSeller → tab Shopee (Đơn hàng cũ) thấy NGAY dòng mới không cần đổi màn
      (qua event — kiểm bằng test + đọc code wiring).
- [ ] Checkbox ribbon thẳng hàng (screenshot).
- [ ] Tab đổi tên "Shopee"; nhớ-màn-theo-tab vẫn hoạt động.
- [ ] Accent suite cam Shopee đồng nhất (screenshot), Success/Danger/Warning giữ nguyên,
      orders không đổi.

## 4. Rủi ro & lưu ý

- Event `AccountsChanged` bắn từ thread UI (command BigSeller chạy trên UI thread) nhưng viết
  handler chịu được cả thread nền (CheckAccess/Post) — về sau ai bắn từ nền cũng an toàn.
- Đổi màu: theme suite dùng token là chính nhưng có thể có chỗ hardcode — bước 8 là lưới quét,
  đừng bỏ.
- App có thể đang được người dùng mở — trước khi build, kiểm tra `ShopeeSuite` process; nếu
  đang chạy thì báo trong log và dừng nhẹ nhàng bằng CloseMainWindow (KHÔNG kill), chờ thoát
  rồi mới build.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
