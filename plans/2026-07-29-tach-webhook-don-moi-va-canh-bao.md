# Plan: Notify do Hub quyết (client chỉ đẩy sự kiện/dữ liệu)

- **Ngày:** 2026-07-29
- **Trạng thái:** hoàn thành
- **Người lập:** Auto · **Người thực thi:** Auto

## Đã làm

- Hub Settings: **3 ô** webhook (đơn mới / lỗi app / đơn trả).
- Push đơn: insert → notify đơn mới; mã trả mới/đổi → notify đơn trả.
- `POST /api/orders/app-alert` — client báo lỗi địa chỉ → Hub gửi webhook lỗi app.
- Client nối Hub: không tự Slack đơn mới/đơn trả; lỗi ưu tiên Hub, fallback local.
- Client độc lập: vẫn dùng 3 ô webhook local.
- Deploy Hub + update local xong.

## Rủi ro đã xử lý

- Trùng tin: Hub gửi, client không gửi khi đã nối Hub.
- Hub cũ chưa có route alert: client fallback local.
- Legacy nhiều URL đơn mới: vẫn gửi đủ đến khi Lưu.
