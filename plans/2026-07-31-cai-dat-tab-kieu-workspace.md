# Plan: Màn Cài đặt — tab đổi sang kiểu tab của Workspace + sắp lại thứ tự

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

v1.7.1 vừa phát hành: màn Cài đặt (`suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml`) là
TabControl 4 tab nhưng dùng style TabItem MẶC ĐỊNH của theme (chữ + gạch chân cam). User xem xong chốt thêm:

1. **Tab phải giống dải tab con của màn Workspace** (`Modules/Workspace/WorkspaceView.xaml` — dải
   "Shop & cấu hình · Thống kê · Dữ liệu · Theo dõi Scrape · Theo dõi Update", style cục bộ `subtabItem`:
   tab active là "viên" trắng nổi bo tròn trong dải nền xám, không phải gạch chân).
2. **Thứ tự tab mới:** *Chế độ ứng dụng → Đơn hàng → Hiệu năng & Đồng bộ → Phiên bản & cập nhật*.

## 2. Phạm vi

- **Làm:**
  - Đưa style tab con của Workspace (`subtabItem` + phần trang trí dải tab nếu có) từ resource cục bộ của
    `WorkspaceView.xaml` lên `Themes/Theme.xaml` thành style DÙNG CHUNG có x:Key (giữ nguyên diện mạo);
    WorkspaceView đổi sang tham chiếu bản chung — **không đổi pixel nào** ở Workspace.
  - `UnifiedSettingsView.xaml`: TabControl áp style chung đó; đổi thứ tự 4 TabItem theo mục tiêu; gate
    Visibility giữ nguyên (Đơn hàng theo `HasOrders`, Hiệu năng & Đồng bộ theo `ShowsWorkspaceSettings`);
    tab mặc định vẫn "Chế độ ứng dụng" (index 0, luôn hiện).
  - CHANGELOG mục "Chưa phát hành" (KHÔNG bump version — điều phối bump khi phát hành).
- **Không làm:** không đổi nội dung/binding bên trong các tab; không đổi ViewModel; không đụng các màn khác
  ngoài 2 file view + Theme.xaml.

## 3. Các bước thực hiện

1. Đọc `WorkspaceView.xaml` phần style tab con (`subtabItem` — báo cáo đợt 3/4 có ghi 2 bẫy TabItem:
   ContentAlignment=Stretch, chữ tô qua `TextElement.*` trong template — style này ĐÃ xử đúng, chuyển
   nguyên trạng) + `UnifiedSettingsView.xaml` hiện tại.
2. Chuyển style lên Theme.xaml (x:Key gợi ý: `subtab`/`subtabItem` — đặt tên nhất quán bảng quy ước đầu
   file); WorkspaceView + UnifiedSettingsView cùng tham chiếu. Nếu dải tab Workspace có Border/nền bọc
   ngoài TabPanel thì tái hiện y hệt ở màn Cài đặt.
3. Đổi thứ tự TabItem trong `UnifiedSettingsView.xaml`: Chế độ ứng dụng / Đơn hàng / Hiệu năng & Đồng bộ /
   Phiên bản & cập nhật.
4. Build 0 error 0 warning; test 2 project xanh.
5. Chạy thử CÁCH LY chuẩn (bản production v1.7.1 ĐANG CHẠY): data-dir.txt + hồ sơ giả USERPROFILE, cấm
   `--mode full`, không bấm nút chạy job, `SHOPEESUITE_SOFTWARE_RENDER=1`, đóng đúng PID. Rig sẵn:
   `scratchpad\verify-caidat-tab.ps1` (phiên 86f7fb17). Chụp: màn Cài đặt cả 2 chế độ (shopee: Chế độ /
   Đơn hàng / Phiên bản; workspace: Chế độ / Hiệu năng & Đồng bộ / Phiên bản) + MỘT ảnh màn Workspace để
   chứng minh dải tab ở đó không đổi. `SHOPEESUITE_BINDING_LOG` = 0 dòng.
6. Điền "Báo cáo thực thi" + CHANGELOG.

## 4. Tiêu chí nghiệm thu

- [ ] Build 0/0; test 1459 + 61 xanh.
- [ ] Tab màn Cài đặt nhìn Y HỆT dải tab con Workspace (đối chiếu ảnh cạnh nhau); thứ tự đúng
      Chế độ → Đơn hàng → Hiệu năng & Đồng bộ → Phiên bản.
- [ ] Màn Workspace không đổi pixel nào (ảnh trước/sau).
- [ ] Binding log 0 dòng cả 2 chế độ; production không bị đụng; không sót file tạm.

## 5. Rủi ro & lưu ý

- Style đang là RESOURCE CỤC BỘ của WorkspaceView — chuyển lên theme phải mang theo mọi StaticResource nó
  tham chiếu (brush/số đo cục bộ nếu có), kẻo XamlParseException lúc chạy (build không bắt được).
- KHÔNG commit — điều phối commit sau nghiệm thu.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
