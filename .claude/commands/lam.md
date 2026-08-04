---
description: Phiên chính nhận việc + viết plan, giao opus-dev (Opus high) thực thi, rồi nghiem-thu phản biện
argument-hint: <mô tả việc cần làm>
---

Việc cần làm: $ARGUMENTS

Lệnh này là **lối giao việc có chủ đích** cho Opus. Mặc định của dự án (xem `CLAUDE.md`) là phiên chính tự
thực thi; user gõ `/lam` nghĩa là lần này họ MUỐN giao. Làm đủ 4 bước, không bỏ bước nào.

## 1. Nhận việc + viết plan (TỰ LÀM, không giao)

- Khảo sát code liên quan (Glob/Grep/Read) đến khi hiểu rõ hiện trạng. Không đoán.
- Yêu cầu mơ hồ ở điểm quyết định → hỏi user bằng AskUserQuestion **trước khi** viết plan.
- Viết plan tại `plans/YYYY-MM-DD-<ten-viec>.md` theo mẫu `plans/TEMPLATE.md`: đường dẫn tương đối từ gốc
  repo, hành vi mong muốn, **tiêu chí nghiệm thu đo được** (lệnh cần chạy → kết quả mong đợi).
- Commit riêng file plan trước khi giao việc — plan là căn cứ để người phản biện chấm.

## 2. Giao `opus-dev` thực thi

- Agent tool, `subagent_type: "opus-dev"` (model Opus, effort high — đã đặt trong định nghĩa agent).
- Prompt phải **tự đủ**: subagent KHÔNG thấy hội thoại này. Ghi rõ đường dẫn file plan, bối cảnh, file cần
  sửa, thay đổi cụ thể, tiêu chí nghiệm thu, lệnh build/test.
- Hạng mục độc lập (không đụng chung file) → giao song song trong cùng một message.
  Hạng mục đụng chung file → giao tuần tự, kẻo ghi đè nhau.
- Nhắc subagent giữ quy ước dự án: tên tiếng Việt không dấu cho luật nghiệp vụ, comment tiếng Việt có dấu,
  **0 warning** là mốc nghiệm thu chứ không phải mong muốn.

## 3. Nghiệm thu + phản biện đối kháng

- **Tự đối chiếu trước:** đọc `git diff` thật, so với plan. KHÔNG tin báo cáo suông của subagent — nó có thể
  báo xong trong khi thiếu việc.
- **Tự chạy kiểm chứng:** `dotnet build ShopeeSuite.sln` (0 warning) + `dotnet test` các project liên quan.
  Lưu ý `ShopeeSuite.sln` KHÔNG chứa `Shopee.Hub.Web` — sửa Hub thì build/test riêng project của nó.
- **Rồi mới gọi `nghiem-thu`** (Agent tool, `subagent_type: "nghiem-thu"`): đưa đường dẫn plan, tóm tắt việc
  đã làm, phạm vi diff, lệnh kiểm chứng. Bảo nó tự chạy lại, đừng tin số mình đưa.
- Đọc báo cáo rồi **tự đối chiếu lại code** — người phản biện cũng sai được, đừng nhận bừa. Điểm nào đúng thì
  sửa; sửa đáng kể thì SendMessage nhờ chính agent đó soi lại. Tối đa 2 lượt.
- Test xanh không thay được bước này: đợt 2026-08-04 có hai bản vá build xanh + test xanh nhưng vẫn hỏng
  logic, chỉ lượt phản biện mới chặn được.

## 4. Báo cáo cuối

- Tiếng Việt: đã làm gì, **kết quả kiểm chứng thật** (số test, warning), người phản biện nói gì, còn gì chưa xong.
- Cập nhật mục `Trạng thái` đầu file plan (`đang làm` → `hoàn thành` / `dừng`). Plan sai giữa chừng thì ghi rõ
  đã đổi hướng ra sao và vì sao, đừng sửa plan cho khớp kết quả như chưa có gì xảy ra.
- Commit thay đổi của việc đó (stage chọn lọc đúng file thuộc việc, không `git add -A`). Không push trừ khi
  user yêu cầu.
