# Plan: Màn Cài đặt chia TAB (mỗi phần 1 tab) + gỡ sạch chữ webhook/Slack

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Màn Cài đặt hiện tại (`suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml` — WPF, bản v1.7.0)
là MỘT cột dọc cuộn qua 5 section. Người dùng xem xong nói "không thấy gì thay đổi" so với bản cũ (phần đầu
màn vốn giống) và chốt yêu cầu mới:

1. **Mỗi phần đặt vào 1 tab** — TabControl thay cho cột dọc. User chốt thêm (tin nhắn bổ sung): KHÔNG tách
   riêng 2 tab kiểu cũ "Hiệu năng" / "Đồng bộ nhiều máy" — GỘP chung thành MỘT tab. Kết quả **4 tab**:
   *Chế độ ứng dụng · Phiên bản & cập nhật · Hiệu năng & Đồng bộ · Đơn hàng*.
2. **Loại bỏ phần webhook tới Slack** — card webhook đã bỏ từ trước, nhưng CHỮ nhắc webhook vẫn còn (ít
   nhất: dòng caption dưới tiêu đề "…Cấu hình AI, prompt và webhook thông báo đặt trên Hub"). Gỡ sạch mọi
   chữ "webhook"/"Slack" khỏi màn Cài đặt.

Máy đang chạy bản production v1.7.0 (WPF) — app này build từ `main` hiện tại, làm trực tiếp trên cây chính.

## 2. Phạm vi

- **Làm:** viết lại bố cục `UnifiedSettingsView.xaml` (+ `.xaml.cs` nếu cần) thành TabControl; gỡ chữ
  webhook/Slack trong màn Cài đặt; CHANGELOG mục mới (không bump version — điều phối bump khi phát hành).
- **Không làm:** không đổi `UnifiedSettingsViewModel`/`SettingsViewModel` (2 bên) — mọi binding giữ nguyên;
  không đụng Theme.xaml trừ khi style TabItem hiện có thiếu gì đó cho màn này; không đụng nhánh `avalonia`.

## 3. Các bước thực hiện

1. Đọc `UnifiedSettingsView.xaml` hiện tại + style TabControl/TabItem trong `Themes/Theme.xaml` (đã có từ
   đợt port, active = gạch chân cam — LƯU Ý 2 bẫy đã ghi trong báo cáo plan đợt 4: TabItem giữ
   ContentAlignment=Stretch, KHÔNG đặt Foreground/FontWeight trên TabItem).
2. Bố cục mới:
   - Hàng đầu: TextBlock "Cài đặt" `h1` + chip trạng thái `Suite.Status` (giữ như cũ); caption dưới tiêu đề
     VIẾT LẠI ngắn gọn, KHÔNG nhắc webhook (vd: "Chế độ ứng dụng · cập nhật · hiệu năng · đồng bộ nhiều máy
     · Đơn hàng").
   - Dưới: `TabControl` chiếm phần còn lại, 4 TabItem theo thứ tự: **Chế độ ứng dụng** / **Phiên bản & cập
     nhật** / **Hiệu năng & Đồng bộ** / **Đơn hàng**. Nội dung mỗi tab = đúng các card của section tương
     ứng hiện tại, chuyển NGUYÊN KHỐI (binding giữ nguyên từng chữ). Tab "Hiệu năng & Đồng bộ" chứa đủ 4
     card (Tài nguyên → trần Brave · Máy của bạn · Máy này · Kết nối Hub) — xếp lưới 2 cột cho cân, bọc
     ScrollViewer dọc; tab "Đơn hàng" cũng bọc ScrollViewer.
   - Ẩn tab theo chế độ: "Hiệu năng & Đồng bộ" `Visibility` theo `ShowsWorkspaceSettings`; "Đơn hàng" theo
     `HasOrders` (converter BoolToVis có sẵn). Tab mặc định chọn = "Chế độ ứng dụng" (luôn hiện ở mọi chế
     độ — an toàn khi các tab khác Collapsed).
3. Gỡ chữ webhook/Slack: grep `webhook`/`slack` (không phân biệt hoa thường) trong
   `suite/Shopee.Suite/Modules/Settings/` + phần chữ tĩnh các card Đơn hàng trong màn này → xoá/viết lại
   câu cho tự nhiên. KHÔNG đụng code backend (OrderNotifyService…) — chỉ chữ trên màn Cài đặt.
4. Build `dotnet build ShopeeSuite.sln` 0 error 0 warning; `dotnet test` 2 project xanh.
5. Chạy thử CÁCH LY (bản production v1.7.0 ĐANG CHẠY — tuyệt đối không đụng):
   - `data-dir.txt` cạnh exe build ra + redirect USERPROFILE/APPDATA/LOCALAPPDATA/TEMP sang hồ sơ giả có
     thật (quy trình chuẩn trong báo cáo plan đợt 5 — `plans/2026-07-31-port-wpf-dot5-orders-caidat.md`);
     KHÔNG `--mode full`; không bấm nút chạy job; chỉ đóng đúng PID mình mở; `SHOPEESUITE_SOFTWARE_RENDER=1`
     khi chụp.
   - Chạy `--mode shopee`: thấy 3 tab (Chế độ / Phiên bản / Đơn hàng); `--mode workspace`: thấy 3 tab
     (Chế độ / Phiên bản / Hiệu năng & Đồng bộ). Chụp TỪNG tab ở cả 2 chế độ; `SHOPEESUITE_BINDING_LOG`
     = 0 dòng.
   - Grep ảnh/cây UIA: không còn chữ "webhook" trong màn.
6. Điền "Báo cáo thực thi" + cập nhật CHANGELOG (mục "Chưa phát hành": màn Cài đặt chia tab, mỗi phần một
   tab; gỡ chữ webhook còn sót).

## 4. Tiêu chí nghiệm thu

- [ ] Build 0/0; test 1459 + 61 xanh.
- [ ] Màn Cài đặt là TabControl 4 tab (Hiệu năng + Đồng bộ nhiều máy GỘP 1 tab), ẩn/hiện đúng theo chế độ;
      mọi nút/ô nhập cũ đủ nguyên vẹn trong tab tương ứng (đối chiếu binding từng section với bản trước).
- [ ] Grep `webhook|slack` trong `Modules/Settings/*.xaml` = 0 kết quả.
- [ ] Binding log 0 dòng ở cả 2 chế độ chạy thử; không sót file tạm/process; production không bị đụng.

## 5. Rủi ro & lưu ý

- Tab "Đơn hàng" chứa binding `Orders.*` với `Orders` có thể null (workspace mode) — tab Collapsed nhưng
  binding classic vẫn phải im lặng (0 lỗi log) như bản hiện tại.
- TabControl là control ĐÃ có style chuẩn trong theme — đừng chế style mới; nếu tab quá dài do 5 nhãn thì
  giữ nhãn ngắn gọn như liệt kê ở bước 2.
- KHÔNG commit — điều phối commit sau nghiệm thu.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
