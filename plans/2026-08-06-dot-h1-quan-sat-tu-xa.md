# Plan: Đợt H1 — Tính năng quan sát từ xa qua Hub

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (sau F)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Mục tiêu gốc của hệ Hub là vận hành đa máy không phải ngồi cạnh máy. Hiện muốn biết "hôm nay ổn không" phải mở lần lượt /orders, /machines, /config/errored, /logs-view; máy chết im lặng không ai báo; dữ liệu banner địa chỉ nằm trên hub nhưng không xem được từ web; backlog "chờ đẩy" của từng client hub không thấy. 4 tính năng đợt này lấp đúng các lỗ đó, tận dụng hạ tầng sẵn: FleetStateService.IsOnline, OrderNotifyService (webhook 3 sự kiện, Settings.razor ~:30–53), bảng `pickup_alerts` (HubDatabase.PickupAlerts + API client sẵn), heartbeat đã mang MaxBrave/AppVersion.

**Trình tự triển khai bắt buộc: hub deploy TRƯỚC, client release SAU** (H1.4 cần cả hai phía; hub phải chịu được client cũ chưa gửi field mới).

## 2. Phạm vi

- **Làm:** 4 mục phần 3.
- **Không làm:** digest cuối ngày + các tính năng đơn hàng (đợt H2); tự cứu acc/OTP (phiên riêng có user); KHÔNG deploy/release trong đợt (phiên chính lo).

## 3. Các bước thực hiện

### H1.1 Dashboard đầu trang chủ
Trang chủ `/` (Fleet.razor, hiện 4 KPI việc BigSeller ~:16–33): thêm **hàng thẻ tổng quan** phía trên, mỗi thẻ là số + link tới trang chi tiết:
- Đơn "chuẩn bị hàng" hôm nay (bảng `orders`, mốc `first_seen_at` theo ngày VN — logic "Đơn chờ hôm nay" đã có từ v1.7.6, tái dùng đúng query đó) → link /orders.
- Máy offline / tổng máy (FleetStateService) → /machines. Chỉ tô đỏ khi có máy offline MÀ đang giữ việc.
- Acc lỗi (bảng errored) → /config/errored.
- Việc gián đoạn/Interrupted (bảng assignments) → /dispatch.
- Cảnh báo địa chỉ đang active (H1.2) → cuộn xuống section.
Thẻ nào = 0 thì hiển thị mờ (không màu cảnh báo). Cấu trúc css theo token hiện có; mobile xuống 2 cột (khuôn m-* sẵn).

### H1.2 Banner lỗi địa chỉ trên hub + đóng từ xa
Section (trong Fleet.razor hoặc trang con — chọn theo bố cục, ưu tiên section ngay dưới hàng KPI) liệt kê `pickup_alerts` đang active: shop, nội dung, tuổi, máy phát hiện. Nút "Đã xử lý" per-dòng → đi ĐÚNG đường dismiss mà client X dùng (rev/tombstone — bấm X luôn thắng, xem memory `pickup-alert-sync-no-clock-compare`; đọc `HubDatabase.PickupAlerts.cs` + endpoint client hiện có, dùng lại logic — TUYỆT ĐỐI không tự chế so-mốc-giờ). Dismiss từ hub phải lan xuống client như dismiss từ client (qua chính hợp đồng rev hiện có).

### H1.3 Webhook "máy rơi offline khi đang giữ việc"
- Nguồn trạng thái: FleetStateService (đã có IsOnline + badge x/y máy). Thêm kiểm tra định kỳ (timer trong service sẵn có hoặc BackgroundService mới): máy có assignment đang chạy/lease sống mà mất nhịp > N phút (N cấu hình, mặc định 10) → gửi webhook qua `OrderNotifyService` (thêm loại sự kiện mới, cùng khuôn 3 sự kiện hiện có).
- Chống spam: mỗi episode offline báo đúng 1 lần; khi máy online lại thì gửi tin "đã trở lại" (1 lần); trạng thái episode giữ trong bộ nhớ service (mất khi restart hub cũng chấp nhận được — ghi rõ).
- Bật/tắt + ngưỡng N trong /settings, cùng section webhook hiện có, lưu cùng chỗ config webhook.

### H1.4 Backlog "chờ đẩy" của client lên heartbeat, hiện ở /machines
- **Client (orders module trong suite):** `OutboxPending` đã tách 5 loại tồn (AppServices ~:21–31). Đưa TỔNG số tồn (1 số int) vào heartbeat: khảo sát đường heartbeat hiện tại (HubClient/HttpCoordinationHub phía suite Core — heartbeat đã mang MaxBrave/AppVersion qua trường mở rộng) + cách orders module cung cấp số cho shell (OrdersModuleHost đã là cầu giữa shell và module — thêm provider/callback theo khuôn MaxBrave). Client không có module orders (chế độ khác) thì không gửi field → hub hiển thị "—".
- **Hub:** nhận field mới (client cũ không gửi → null, không lỗi), lưu vào bảng machines (cột mới, migration theo khuôn cột store_* đã có), hiển thị cột "⏳ Tồn" ở /machines; > 0 tô vàng, > 50 tô đỏ (ngưỡng hằng số, ghi chú).
- Hub phải deploy TRƯỚC client — code hub chấp nhận thiếu field; code client gửi thêm field không phá hub cũ (nhưng thứ tự deploy vẫn như trên cho sạch).

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 error 0 warning; 3 bộ test xanh.
- [ ] Test hub mới: (a) query "đơn chờ hôm nay" của dashboard = đúng số theo mốc ngày VN (test theo khuôn test first_seen_at sẵn có); (b) webhook offline: máy giữ việc + mất nhịp giả lập → đúng 1 tin, online lại → 1 tin "trở lại", không lặp; (c) heartbeat mang/thiếu field backlog đều xử lý đúng. Test viết xong PHẢI thử phá (đổi luật → đỏ) rồi khôi phục — ghi vào báo cáo.
- [ ] H1.2: dismiss từ hub đi cùng code-path với dismiss từ client (chỉ ra hàm chung trong báo cáo); KHÔNG có phép so thời gian chéo máy nào mới.
- [ ] Chạy hub local, mở `/`: hàng KPI render, số khớp dữ liệu DB dev; section địa chỉ hiện khi có alert giả.
- [ ] Client build xanh; chế độ không có orders module không gửi field (kiểm bằng test hoặc log).

## 5. Rủi ro & lưu ý

- **Banner địa chỉ là khu từng nhiều sẹo** (memory: cấm so mốc giờ chéo máy, tombstone tự lành, RunOnUi=BeginInvoke làm repush chết câm) — đọc memory + code hiện có TRƯỚC khi viết dòng nào; sai luật rev ở đây là bug lan cả fleet.
- Heartbeat là hợp đồng client↔hub — thêm field phải backward-compatible cả 2 chiều (client cũ→hub mới, client mới→hub cũ).
- Webhook: đừng gửi trong lock của FleetStateService; fire-and-forget có log lỗi.
- KHÔNG commit/deploy/release — phiên chính lo, theo trình tự hub trước client sau.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
