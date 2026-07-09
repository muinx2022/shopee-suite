---
description: Fable hoạch định plan rồi giao subagent Opus triển khai + test
argument-hint: <mô tả việc cần làm>
---

Việc cần làm: $ARGUMENTS

Thực hiện đúng quy trình sau, không bỏ bước:

## 1. Hoạch định (tự làm, không giao)
- Khảo sát code liên quan (đọc file, grep) đến khi hiểu rõ hiện trạng.
- Viết plan chi tiết: mục tiêu, chia thành từng hạng mục; mỗi hạng mục ghi rõ file cần sửa, thay đổi cụ thể, tiêu chí nghiệm thu, cách test.
- In plan ra cho user xem rồi mới giao việc.

## 2. Giao việc cho Opus
- Giao từng hạng mục qua Agent tool cho subagent `opus-dev` (nếu agent chưa được nạp trong phiên thì dùng `general-purpose` với model "opus").
- Prompt giao việc phải tự đủ: bối cảnh, file, thay đổi cụ thể, tiêu chí nghiệm thu, lệnh build/test — subagent không thấy hội thoại này.
- Hạng mục độc lập (không đụng chung file) → giao song song trong cùng 1 message. Hạng mục đụng chung file → giao tuần tự.

## 3. Review + nghiệm thu (tự làm, không giao)
- Đọc diff thực tế (`git diff`), đối chiếu với plan — không tin báo cáo suông của subagent.
- Chạy `dotnet build` tổng; verify hành vi thật nếu chạy được.
- Hạng mục nào sai/thiếu: giao lại kèm mô tả lỗi cụ thể (dùng SendMessage tới agent cũ để giữ ngữ cảnh).

## 4. Báo cáo cuối
- Tóm tắt: plan, việc đã giao, kết quả build/test, việc còn dang dở (nếu có). Không commit trừ khi user yêu cầu.
