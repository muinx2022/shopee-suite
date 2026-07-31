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

### A. Việc theo plan (tab Cài đặt kiểu Workspace)

**File sửa**

| File | Thay đổi |
|---|---|
| `suite/Shopee.Suite/Themes/Theme.xaml` | **THÊM** mục "TAB CON (sub-tab)" gồm 3 style dùng chung: `subtabItem` (ô tab, chuyển NGUYÊN TRẠNG từ WorkspaceView — giữ cả 2 bẫy: `HorizontalContentAlignment/VerticalContentAlignment=Stretch` và tô chữ bằng `TextElement.*` trên Border trong template), `subtabTray` (khay xám #F2EFEC · bo 10 · đệm 4), `subtabs` (TabControl ráp sẵn: khay sát trái ở trên + `ContentPresenter` Stretch, `ItemsPanel` = StackPanel ngang). Đặt ngay sau style TabControl mặc định để `BasedOn={StaticResource {x:Type TabControl}}` và các StaticResource tra được. |
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml` | **XOÁ** style cục bộ `subtabItem` (43 dòng) và các setter trùng của `subtabs`; style TabControl cục bộ đổi tên `subtabs` → **`subtabsWorkspace`** (`BasedOn={StaticResource subtabs}` của theme, chỉ còn ghi đè `Template` vì Workspace có phần RIÊNG: lề `18,12,210,10`, tên tk ở mép phải qua `Tag`, `PART_SelectedContentHost` lề `18,4,18,18`). Khay trong template dùng `Style="{StaticResource subtabTray}"` thay 3 thuộc tính inline. Chỗ dùng (dòng 526 cũ) đổi sang `subtabsWorkspace`. |
| `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml` | `TabControl` bỏ `Background/BorderThickness` inline → `Style="{StaticResource subtabs}"`. **Đổi thứ tự 4 TabItem** thành Chế độ ứng dụng → Đơn hàng → Hiệu năng & Đồng bộ → Phiên bản & cập nhật (đổi CHỖ ĐỨNG nguyên khối, nội dung/binding/gate Visibility giữ nguyên byte); đánh số lại comment TAB 1..4 + sửa comment đầu file. |
| `CHANGELOG.md` | Thêm mục **Chưa phát hành** (2 gạch đầu dòng: dải tab Cài đặt + đổi font). KHÔNG bump `version.txt`. |

Đổi tên `subtabs` → `subtabsWorkspace` là BẮT BUỘC: nếu giữ tên cũ thì `BasedOn="{StaticResource subtabs}"` khai trong chính `UserControl.Resources` sẽ tự tham chiếu chính nó (StaticResource quét từ điển hiện tại trước) → lỗi lúc dựng cây.

### B. Việc bổ sung do điều phối giao giữa chừng (font)

| File | Thay đổi |
|---|---|
| `suite/Shopee.Suite/Themes/Theme.xaml` | `UiFont`: `Segoe UI Variable Text, Segoe UI` → **`Segoe UI`** (+ ghi lý do vào comment). Mono/Emoji giữ nguyên. |
| `orders/XuLyDonShopee.App/Styles/Colors.xaml` | `UiFont` đổi y hệt (+ comment nhắc phải giữ giống suite). |
| `suite/.../MessageDialog.xaml`, `Modules/Accounts/ImportAccountsWindow.xaml`, `Modules/CheckAccount/CheckAccountWindow.xaml`, `Modules/Data/RowEditWindow.xaml`, `Modules/Scrape/ScrapeStatsWindow.xaml`, `orders/.../Views/ConfirmDialog.xaml`, `orders/.../Views/OrderDetailDialog.xaml` | Thêm `TextOptions.TextRenderingMode="ClearType"` ở root cho khớp MainWindow. |

Soát toàn bộ `suite/` + `orders/` (bỏ bin/obj): chỉ 2 chỗ khai `Segoe UI Variable` (2 file `UiFont` trên) — **không có chỗ nào hardcode trong view/csproj**. Cả 8 Window root ĐÃ có sẵn `TextOptions.TextFormattingMode="Display"` từ trước (không thiếu chỗ nào); chỉ `TextRenderingMode` là trước đây riêng MainWindow có, nay đồng bộ cả 8.

### C. Kiểm chứng (số THẬT)

- `dotnet build ShopeeSuite.sln -c Debug` → **Build succeeded, 0 Warning(s), 0 Error(s)**.
- `dotnet test ShopeeSuite.sln -c Debug --no-build` → **XuLyDonShopee.Tests 1459/1459 passed**, **Shopee.Core.Tests 61/61 passed**, 0 failed.
- Chạy thử CÁCH LY (rig `scratchpad\verify-caidat-tab.ps1` + `verify-ws-subtab.ps1` + `verify-orders-font.ps1`: `data-dir.txt` trỏ kho tạm, USERPROFILE/APPDATA/LOCALAPPDATA/TEMP → hồ sơ giả, `--mode shopee|workspace` (KHÔNG bao giờ `--mode full`), `SHOPEESUITE_SOFTWARE_RENDER=1`, chỉ đổi tab/nav rồi chụp, đóng bằng WM_CLOSE đúng PID mình mở):
  - **Cài đặt · chế độ Shopee** → TabItem đọc được: `Chế độ ứng dụng | Đơn hàng | Phiên bản & cập nhật` (tab Hiệu năng ẩn đúng). Ảnh `tab-shopee-{1,2,3}-*-t7shopee.png`. **Binding log 0 dòng**.
  - **Cài đặt · chế độ Workspace** → `Chế độ ứng dụng | Hiệu năng & Đồng bộ | Phiên bản & cập nhật` (tab Đơn hàng ẩn đúng). Ảnh `tab-workspace-{1,2,3}-*-t7ws.png`. **Binding log 0 dòng**.
  - **Workspace TRƯỚC/SAU** (`ws-wsbefore.png` = HEAD sạch, `ws-wsmid.png` = sau khi refactor tab, CÙNG font cũ để cô lập biến): vùng dải tab con `X380..1100 Y370..425` khác **0 / 40 376 pixel**; toàn vùng nội dung app `X12..1888 Y40..1150` khác **55 / 2 085 347 pixel** — 55 pixel này là chấm xanh trên thanh trạng thái, lệch đúng **±1 ở kênh Blue** (`#F2EFEB` vs `#F2EFEC`, hình dạng/vị trí y hệt) = nhiễu chụp màn hình, không phải đổi layout. Phần còn lại lệch trong `ws-diff-before-mid.png` nằm hết ở thanh tiêu đề + 7px bóng đổ 2 mép (cửa sổ nền phía sau khác nhau giữa 2 lượt).
  - **Đo hình học dải tab** (ảnh cuối, font mới): khay xám cao **48px** ở CẢ Workspace lẫn Cài đặt; viên trắng đang chọn cao **36px**, thụt vào **6px** so với mép khay ở CẢ hai màn ⇒ hai dải tab giống nhau từng pixel về kích thước, chỉ khác bề ngang do nhãn khác nhau. Ảnh cắt cạnh nhau: `crop-ws-strip.png` vs `crop-caidat-strip.png`.
  - **Font**: `font-cu-variable.png` (Segoe UI Variable) vs `font-moi-segoeui.png` (Segoe UI) — cùng một câu, phóng 2×, thấy rõ đổi bộ chữ; màn Đơn hàng + Tài khoản của module orders vẽ bình thường (`orders-*-ordfont.png`), **binding log 0 dòng**.
- **An toàn production**: `app.db` production `1339392 bytes | 2026-07-31 01:23:12 | sha256 4BDF62F4…` KHÔNG đổi qua cả 6 lượt chạy; tiến trình ShopeeSuite production (PID 19948) còn nguyên; số cửa sổ Brave trước/sau = 8/8; cổng 47821 do PID dev mở = 0; `data-dir.txt` đã tự xoá sau mỗi lượt (`git status` chỉ có 12 file sửa thuộc việc này, không rác).

### D. Lưu ý cho người nghiệm thu

- KHÔNG commit (theo yêu cầu). `version.txt` giữ 1.7.1.
- Sửa diện mạo tab con từ nay chỉ sửa `subtabItem`/`subtabTray` trong Theme.xaml — Workspace và Cài đặt cùng ăn theo.
- 3 hộp thoại `MessageDialog` / `ConfirmDialog` / `OrderDetailDialog` **chưa có** `UseLayoutRounding="True"` (5 cửa sổ còn lại có). Không tự thêm vì ngoài phạm vi được giao; nếu muốn chữ/viền các hộp thoại này khớp hẳn thì thêm sau.
