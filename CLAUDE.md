# Quy trình làm việc

- Việc triển khai KHÔNG tầm thường (tính năng mới, sửa nhiều file, refactor): phiên chính đóng vai kiến trúc sư — tự khảo sát code và viết plan chi tiết, KHÔNG tự code. Sau đó giao từng hạng mục cho subagent `opus-dev` (model Opus) triển khai + build + test, rồi phiên chính review diff thật và nghiệm thu. Quy trình đầy đủ: xem `.claude/commands/lam.md` (lệnh `/lam`).
- Việc vặt (sửa vài dòng, đổi chuỗi, trả lời câu hỏi, đọc code): làm trực tiếp, không giao subagent.

# Build

- .NET/C#: app desktop WPF trong `suite/`, hub web Blazor trong `server/Shopee.Hub.Web/`.
- Build: `dotnet build` project bị ảnh hưởng; sửa nhiều project thì build cả solution.
